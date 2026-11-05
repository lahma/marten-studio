namespace MartenStudio.Services.Schema;

/// <summary>One kind of statement in a migration that removes or rewrites something.</summary>
/// <param name="Kind">What it does, in the words the dialog uses.</param>
/// <param name="Statement">The line from the script, trimmed, so a person can see which object it is about.</param>
internal sealed record DestructiveStatement(string Kind, string Statement);

/// <summary>
/// Finds the statements in a migration script that remove or rewrite something.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because <c>AutoCreate.CreateOrUpdate</c> is not additive.</b> Weasel's
/// <c>TableDelta.WriteUpdate</c> - which is what an <c>Update</c> delta writes, and <c>Update</c> is
/// allowed under <c>CreateOrUpdate</c> - emits <c>drop index</c> for every physical index the
/// configuration does not declare, <c>drop column</c> for every extra column, <c>alter column … type</c>
/// for a changed one, <c>alter table … drop constraint … CASCADE</c> for a changed primary key, and for a
/// changed partition scheme a <c>create table … as select</c> / <c>drop table … cascade</c> pair that
/// copies the whole table out and back. Only <c>Invalid</c> is refused, and <c>Invalid</c> is a much
/// narrower thing than "this will lose something".
/// </para>
/// <para>
/// So the preview is read for those statement shapes and the apply dialog puts them in front of the
/// person before they type the database's name. This is a <em>text</em> scan of SQL that Weasel wrote and
/// that the same screen is showing: it is an alarm, not a parser, and nothing is authorized or refused by
/// what it finds. The refusal is still the capability, the write policy and the typed confirmation.
/// </para>
/// <para>
/// <b>One <c>drop index</c> is not a drop.</b> From Weasel 9.46 (Marten 9.51, weasel#718) a concurrent index on a
/// partitioned table is built per partition as <c>DROP INDEX CONCURRENTLY IF EXISTS s.child;</c>, then
/// <c>CREATE INDEX CONCURRENTLY child ON s.partition ...</c>, then <c>ALTER INDEX s.parent ATTACH PARTITION s.child;</c>
/// - the drop removes only what an earlier, stopped build of the same index left unattached, and is a no-op otherwise.
/// That exact triple, with one child name throughout, is a note (<see cref="Notes" />) rather than "drops an index";
/// any other <c>drop index</c>, including a lone <c>DROP INDEX CONCURRENTLY IF EXISTS</c>, is still a drop.
/// </para>
/// </remarks>
internal static partial class MigrationRisk
{
    /// <summary>What the red block says above the list.</summary>
    internal const string Warning = WarningHeadline + " " + WarningDetail;

    /// <summary>The block's headline, which the page sets in bold. It is said once: <see cref="WarningDetail" /> is the rest.</summary>
    internal const string WarningHeadline = "This migration removes or rewrites objects.";

    /// <summary>What follows the headline.</summary>
    internal const string WarningDetail =
        "AutoCreate.CreateOrUpdate is not additive: it drops " +
        "indexes and columns the configuration does not declare, alters changed column types, and drops " +
        "and recreates a changed primary key.";

    /// <summary>The kind of a <c>drop index</c>.</summary>
    internal const string DropsIndexKind = "drops an index";

    /// <summary>The kind of a <c>drop table</c>.</summary>
    internal const string DropsTableKind = "drops a table";

    /// <summary>The kind of an <c>alter table … drop column</c>.</summary>
    internal const string DropsColumnKind = "drops a column";

    /// <summary>The kind of an <c>alter table … drop constraint</c>.</summary>
    internal const string DropsConstraintKind = "drops a constraint";

    /// <summary>The kind of an <c>alter table … alter column … type</c>.</summary>
    internal const string ChangesColumnTypeKind = "changes a column type";

    /// <summary>The kind of the partition rebuild's <c>create table … as select</c>.</summary>
    internal const string PartitionRebuildKind = "copies a table out and back (partition rebuild)";

    /// <summary>Every destructive statement in <paramref name="sql" />, in the order they would run.</summary>
    /// <param name="sql">The migration script, exactly as it would be executed.</param>
    public static IReadOnlyList<DestructiveStatement> Find(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        List<DestructiveStatement> found = [];
        List<string> lines = StatementLines(sql);

        for (int i = 0; i < lines.Count; i++)
        {
            ReadOnlySpan<char> line = lines[i];

            if (IsPartitionIndexResume(lines, i))
            {
                continue;
            }

            string? kind = KindOf(line);
            if (kind is null)
            {
                continue;
            }

            found.Add(new DestructiveStatement(kind, Clamp(lines[i])));
        }

        return found;
    }

    /// <summary>
    /// The script's lines, trimmed, without blank lines and <c>--</c> comments (Weasel's markers among them), and without the lines
    /// strictly inside a dollar-quoted body (<c>$function$ … $function$</c>): a host's function in <c>ExtendedSchemaObjects</c> whose
    /// body has <c>ALTER TABLE</c> or <c>CREATE TABLE</c> lines is not a statement of the migration. The line that opens a body and the
    /// line that closes it are kept; <c>$tran$</c>, Weasel's transaction wrapper, is not a body.
    /// </summary>
    private static List<string> StatementLines(string sql)
    {
        List<string> lines = [];
        string? body = null;

        foreach (ReadOnlySpan<char> rawLine in sql.AsSpan().EnumerateLines())
        {
            string text = rawLine.Trim().ToString();
            bool insideAtStart = body is not null;

            if (text.Length == 0 || (!insideAtStart && text.StartsWith("--", StringComparison.Ordinal)))
            {
                continue;
            }

            AdvanceDollarQuotes(text, ref body);

            if (insideAtStart && body is not null)
            {
                continue;
            }

            lines.Add(text);
        }

        return lines;
    }

    /// <summary>Moves <paramref name="body" /> (the open dollar-quote tag, or <see langword="null" />) across one line.</summary>
    private static void AdvanceDollarQuotes(string text, ref string? body)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (body is not null)
            {
                int close = text.IndexOf(body, i, StringComparison.Ordinal);
                if (close < 0)
                {
                    return;
                }

                i = close + body.Length - 1;
                body = null;
                continue;
            }

            char c = text[i];

            if (c == '\'')
            {
                // A string literal on this line: '' is an escaped quote, and an unterminated one runs to the end of the line.
                int end = i + 1;
                while (end < text.Length)
                {
                    if (text[end] == '\'')
                    {
                        if (end + 1 < text.Length && text[end + 1] == '\'')
                        {
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    end++;
                }

                if (end >= text.Length)
                {
                    return;
                }

                i = end;
            }
            else if (c == '$')
            {
                int j = i + 1;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                {
                    j++;
                }

                if (j < text.Length && text[j] == '$' && (j == i + 1 || !char.IsDigit(text[i + 1])))
                {
                    string tag = text[i..(j + 1)];
                    i = j;

                    if (!string.Equals(tag, "$tran$", StringComparison.Ordinal))
                    {
                        body = tag;
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="sql" /> contains anything that removes or rewrites an object.</summary>
    public static bool IsDestructive(string? sql) => Find(sql).Count > 0;

    /// <summary>The kinds present, deduplicated, for a one-line summary.</summary>
    public static IReadOnlyList<string> KindsIn(string? sql)
    {
        List<string> kinds = [];

        foreach (DestructiveStatement statement in Find(sql))
        {
            if (!kinds.Contains(statement.Kind, StringComparer.Ordinal))
            {
                kinds.Add(statement.Kind);
            }
        }

        return kinds;
    }

    private static string? KindOf(ReadOnlySpan<char> line)
    {
        // "drop index" covers "DROP INDEX CONCURRENTLY IF EXISTS" too: only the exact Weasel triple is told apart, by
        // IsPartitionIndexResume, before this is asked.
        if (StartsWithWords(line, "drop index"))
        {
            return DropsIndexKind;
        }

        if (StartsWithWords(line, "drop table"))
        {
            return DropsTableKind;
        }

        if (StartsWithWords(line, "drop function") || StartsWithWords(line, "drop sequence"))
        {
            return "drops a function or sequence";
        }

        if (StartsWithWords(line, "alter table") && ContainsWords(line, "drop column"))
        {
            return DropsColumnKind;
        }

        if (StartsWithWords(line, "alter table") && ContainsWords(line, "drop constraint"))
        {
            return DropsConstraintKind;
        }

        // "alter table x alter column y type z" - Weasel's AlterColumnTypeSql. A type change rewrites
        // every row in the table and can fail, or round, on data that does not fit.
        if (StartsWithWords(line, "alter table") && ContainsWords(line, "alter column") && ContainsWords(line, " type "))
        {
            return ChangesColumnTypeKind;
        }

        if (StartsWithWords(line, "create table") && ContainsWords(line, " as select"))
        {
            return PartitionRebuildKind;
        }

        return null;
    }

    private static bool StartsWithWords(ReadOnlySpan<char> line, string words) =>
        line.StartsWith(words, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWords(ReadOnlySpan<char> line, string words) =>
        line.Contains(words, StringComparison.OrdinalIgnoreCase);

    private static string Clamp(string statement) =>
        statement.Length <= 200 ? statement : statement[..200] + " ...";
}
