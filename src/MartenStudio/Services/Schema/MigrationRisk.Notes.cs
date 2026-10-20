using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Schema;

/// <summary>One statement in a migration that removes nothing but is worth reading before it runs.</summary>
/// <param name="Kind">What it does, in the words the apply section uses.</param>
/// <param name="Statement">The line from the script, trimmed.</param>
internal sealed record MigrationNote(string Kind, string Statement);

/// <summary>
/// The statements of a migration that change nothing a person made but that they should know about before applying it,
/// and what the Schema screen's buttons may create on the way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Notes, not alarms.</b> <see cref="Find" /> lists what removes or rewrites something, and the apply waits for a tick
/// against it. These are listed beside it and ask for nothing: an index build that makes writers wait, and the
/// partition-index rebuild that looks like a drop and is not. Like <see cref="Find" />, a text scan of SQL Weasel wrote,
/// not a parser - nothing is authorized or refused by what it finds.
/// </para>
/// <para>
/// <b>A plain <c>CREATE INDEX</c> on a table that already exists</b> holds a SHARE lock on it for as long as the build
/// takes: reads go on, every insert, update and delete waits. Weasel writes it that way for a missing index on an existing
/// table unless the migrator has <c>BuildIndexesConcurrentlyOnAlter</c>. One case every store with DCB tag tables meets on
/// the way to Marten 9.51: #5694 declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c> on each tag table, so a store whose
/// tag tables were built below 9.51 shows one such build per tag table, and every append of a tagged event waits for it.
/// An index on a table the same script creates is not a note - there is nothing on it to wait.
/// </para>
/// </remarks>
internal static partial class MigrationRisk
{
    /// <summary>What a partition-index rebuild (weasel#718) is called in the notes.</summary>
    internal const string PartitionIndexResumeKind =
        "rebuilds a partition's index that a stopped concurrent build left unattached (a no-op when there is none)";

    /// <summary>What a plain index build on an existing table is called.</summary>
    internal const string BlockingIndexBuildKind =
        "builds an index without CONCURRENTLY: inserts, updates and deletes on the table wait until it finishes";

    /// <summary>What a plain index build on a DCB tag table is called - every tagged append waits for it.</summary>
    internal const string BlockingTagIndexBuildKind =
        "builds an index on a DCB tag table without CONCURRENTLY: every append of an event carrying that tag waits until it finishes";

    /// <summary>The prefix of Marten's DCB tag tables (<c>mt_event_tag_&lt;suffix&gt;</c>).</summary>
    private const string TagTablePrefix = "mt_event_tag_";

    /// <summary>Every note in <paramref name="sql" />, in the order the statements would run.</summary>
    /// <param name="sql">The migration script, exactly as it would be executed.</param>
    public static IReadOnlyList<MigrationNote> Notes(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        List<string> lines = StatementLines(sql);
        HashSet<string> created = CreatedTables(lines);
        List<MigrationNote> notes = [];

        for (int i = 0; i < lines.Count; i++)
        {
            if (IsPartitionIndexResume(lines, i))
            {
                notes.Add(new MigrationNote(PartitionIndexResumeKind, Clamp(lines[i])));
                continue;
            }

            if (BlockingIndexTable(lines[i]) is { } table && !created.Contains(Normalize(table)))
            {
                notes.Add(new MigrationNote(
                    Normalize(table).Contains(TagTablePrefix, StringComparison.Ordinal) ? BlockingTagIndexBuildKind : BlockingIndexBuildKind,
                    Clamp(lines[i])));
            }
        }

        return notes;
    }

    /// <summary>
    /// Whether building Marten's feature schemas - what Check schema, Preview migration and Generate script all ask for -
    /// can create Marten's HiLo objects (<c>mt_hilo</c>, <c>mt_get_next_hi</c>) on <paramref name="runtime" />'s Marten:
    /// below 9.50.3, where the enumeration reaches the HiLo feature's applying lazy (hard rule 14).
    /// </summary>
    public static bool FeatureSchemasMayCreateHilo(MartenRuntime runtime) =>
        !RuntimeFeature.OfflineSchemaEnumeration.IsAvailable(runtime);

    /// <summary>
    /// Whether line <paramref name="index" /> is the drop of Weasel 9.46's partition-index resume: <c>DROP INDEX
    /// CONCURRENTLY IF EXISTS s.child;</c>, then <c>CREATE [UNIQUE] INDEX CONCURRENTLY child ON ...</c>, then
    /// <c>ALTER INDEX s.parent ATTACH PARTITION s.child;</c> - one child name throughout. Weasel's marker comments sit
    /// between them and are not statement lines.
    /// </summary>
    private static bool IsPartitionIndexResume(List<string> lines, int index)
    {
        if (index + 2 >= lines.Count)
        {
            return false;
        }

        if (After(lines[index], "drop index concurrently if exists ") is not { } dropped
            || IndexBuilt(lines[index + 1], concurrently: true) is not { } built
            || !StartsWithWords(lines[index + 2], "alter index ")
            || After(lines[index + 2], " attach partition ", anywhere: true) is not { } attached)
        {
            return false;
        }

        string target = Statement(dropped);

        return string.Equals(target, Statement(attached), StringComparison.Ordinal)
            && (string.Equals(target, built, StringComparison.Ordinal) || target.EndsWith("." + built, StringComparison.Ordinal));
    }

    /// <summary>
    /// The index name a <c>CREATE [UNIQUE] INDEX</c> line builds, when it is concurrent exactly as
    /// <paramref name="concurrently" /> says; otherwise <see langword="null" />.
    /// </summary>
    private static string? IndexBuilt(string line, bool concurrently)
    {
        string? rest = After(line, "create index ") ?? After(line, "create unique index ");
        if (rest is null)
        {
            return null;
        }

        bool isConcurrent = StartsWithWords(rest, "concurrently ");
        if (isConcurrent != concurrently)
        {
            return null;
        }

        if (isConcurrent)
        {
            rest = rest["concurrently ".Length..].TrimStart();
        }

        if (StartsWithWords(rest, "if not exists "))
        {
            rest = rest["if not exists ".Length..].TrimStart();
        }

        return FirstToken(rest);
    }

    /// <summary>
    /// The table a plain (not concurrent) <c>CREATE [UNIQUE] INDEX</c> line builds on, or <see langword="null" /> -
    /// including for <c>ON ONLY</c>, which creates a partitioned parent's index without building anything.
    /// </summary>
    private static string? BlockingIndexTable(string line)
    {
        if (IndexBuilt(line, concurrently: false) is null || After(line, " on ", anywhere: true) is not { } target)
        {
            return null;
        }

        return StartsWithWords(target, "only ") ? null : FirstToken(target);
    }

    /// <summary>The tables a script creates, normalized - an index on one of them blocks nobody.</summary>
    private static HashSet<string> CreatedTables(List<string> lines)
    {
        HashSet<string> created = new(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            if (After(line, "create table ") is not { } rest)
            {
                continue;
            }

            if (StartsWithWords(rest, "if not exists "))
            {
                rest = rest["if not exists ".Length..].TrimStart();
            }

            if (FirstToken(rest) is { } table)
            {
                created.Add(Normalize(table));
            }
        }

        return created;
    }

    /// <summary>The text after <paramref name="words" />, trimmed, when the line starts with them (or contains them).</summary>
    private static string? After(string line, string words, bool anywhere = false)
    {
        int at = anywhere
            ? line.IndexOf(words, StringComparison.OrdinalIgnoreCase)
            : (line.StartsWith(words, StringComparison.OrdinalIgnoreCase) ? 0 : -1);

        return at < 0 ? null : line[(at + words.Length)..].Trim();
    }

    /// <summary>The first token: up to whitespace or an opening parenthesis outside double quotes.</summary>
    private static string? FirstToken(string text)
    {
        bool quoted = false;
        int end = text.Length;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (c is ' ' or '\t' or '('))
            {
                end = i;
                break;
            }
        }

        string token = text[..end].TrimEnd(';');

        return token.Length == 0 ? null : token;
    }

    /// <summary>A statement's operand without its terminating semicolon.</summary>
    private static string Statement(string text) => text.TrimEnd().TrimEnd(';').TrimEnd();

    /// <summary>
    /// A possibly schema-qualified identifier as Postgres resolves it: each unquoted part folded to lower case, each
    /// quoted part kept exactly (its quotes removed and a doubled quote read as one), and the parts joined by a separator
    /// no identifier spells - so <c>studio."Order"</c> and <c>studio.order</c> are two tables, as they are to Postgres,
    /// and <c>"a.b"</c> is never <c>a.b</c>.
    /// </summary>
    internal static string Normalize(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);

        System.Text.StringBuilder result = new(identifier.Length);

        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];

            if (c == '"')
            {
                for (i++; i < identifier.Length; i++)
                {
                    if (identifier[i] != '"')
                    {
                        result.Append(identifier[i]);
                    }
                    else if (i + 1 < identifier.Length && identifier[i + 1] == '"')
                    {
                        result.Append('"');
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else if (c == '.')
            {
                result.Append(PartSeparator);
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }

        return result.ToString();
    }

    /// <summary>What joins a qualified name's parts once normalized: a control character no identifier Weasel writes holds.</summary>
    private const char PartSeparator = '\u001F';
}
