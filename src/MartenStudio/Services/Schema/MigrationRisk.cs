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
/// </remarks>
internal static class MigrationRisk
{
    /// <summary>What the red block says above the list.</summary>
    internal const string Warning =
        "This migration removes or rewrites objects. AutoCreate.CreateOrUpdate is not additive: it drops " +
        "indexes and columns the configuration does not declare, alters changed column types, and drops " +
        "and recreates a changed primary key.";

    /// <summary>Every destructive statement in <paramref name="sql" />, in the order they would run.</summary>
    /// <param name="sql">The migration script, exactly as it would be executed.</param>
    public static IReadOnlyList<DestructiveStatement> Find(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        List<DestructiveStatement> found = [];

        foreach (ReadOnlySpan<char> rawLine in sql.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty || line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string? kind = KindOf(line);
            if (kind is null)
            {
                continue;
            }

            found.Add(new DestructiveStatement(kind, Clamp(line.ToString())));
        }

        return found;
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
        if (StartsWithWords(line, "drop index"))
        {
            return "drops an index";
        }

        if (StartsWithWords(line, "drop table"))
        {
            return "drops a table";
        }

        if (StartsWithWords(line, "drop function") || StartsWithWords(line, "drop sequence"))
        {
            return "drops a function or sequence";
        }

        if (StartsWithWords(line, "alter table") && ContainsWords(line, "drop column"))
        {
            return "drops a column";
        }

        if (StartsWithWords(line, "alter table") && ContainsWords(line, "drop constraint"))
        {
            return "drops a constraint";
        }

        // "alter table x alter column y type z" - Weasel's AlterColumnTypeSql. A type change rewrites
        // every row in the table and can fail, or round, on data that does not fit.
        if (StartsWithWords(line, "alter table") && ContainsWords(line, "alter column") && ContainsWords(line, " type "))
        {
            return "changes a column type";
        }

        if (StartsWithWords(line, "create table") && ContainsWords(line, " as select"))
        {
            return "copies a table out and back (partition rebuild)";
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
