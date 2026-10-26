using System.Text.RegularExpressions;

using MartenStudio.Services.Health.Checks;

using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's statement groups (W5-Q1, issue #8 G6): by Marten operation and document type - <c>load/query · Order</c>,
/// <c>upsert · Order</c>, <c>event append</c>, <c>projection progress</c>, <c>daemon</c> - read off the SQL Marten itself
/// writes, as <c>pg_stat_statements</c> records it (its constants replaced by <c>$n</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shapes, from Marten's sources at <c>V9.39.0</c> and <c>v9.51.0</c></b> (each at the same line at both tags but
/// where two lines are given; recorded live at 9.39.0 and 9.54.1, identically):
/// </para>
/// <list type="bullet">
/// <item><description><b>load/query.</b> <c>select {fields} from {table} as d</c>, <c>… where id = $1</c> (a load) and
/// <c>… where id = ANY($1)</c> (a load of many): <c>src/Marten/Internal/Storage/DocumentStorage.cs</c> lines 89, 92 and 95; a
/// LINQ query's <c>select … from {table} as d</c> (<c>src/Marten/Linq/SqlGeneration/</c>).</description></item>
/// <item><description><b>upsert.</b> <c>insert into {table} (…) values (…) on conflict (…) do update set … [where
/// {table}.mt_version = $n] returning …</c> - an upsert and an overwrite, guarded or not:
/// <c>src/Marten/Internal/ClosedShape/DocumentStorageDescriptorBuilder.cs</c> lines 491-494. T22's guarded shape
/// (<see cref="ConcurrencyConflictCheck.Classify" />) is the guarded one of these.</description></item>
/// <item><description><b>insert.</b> <c>insert into {table} (…) values (…) on conflict (…) do nothing returning …</c>: the
/// same file, lines 511-513.</description></item>
/// <item><description><b>update.</b> <c>update {table} set data = $1, … where id = $n … returning …</c>: the same file, line
/// 611.</description></item>
/// <item><description><b>delete.</b> <c>delete from {table} as d</c> (<c>src/Marten/Linq/SqlGeneration/HardDelete.cs</c> line
/// 14), and a soft delete's <c>update {table} as d set mt_deleted = $1, mt_deleted_at = now()</c>
/// (<c>SoftDelete.cs</c> line 16), told by its <c>mt_deleted_at = now()</c>.</description></item>
/// <item><description><b>undelete.</b> <c>update {table} as d set mt_deleted = $1, mt_deleted_at = NULL</c>
/// (<c>src/Marten/Linq/SqlGeneration/UnSoftDelete.cs</c> line 16), which sets the same column and is no delete; its
/// <c>NULL</c> may be recorded as a constant, <c>$2</c>.</description></item>
/// <item><description><b>event append.</b> <c>insert into {schema}.mt_streams (…) values (</c>, its strict-identity
/// <c>with new_stream as (insert into {schema}.mt_streams …) insert into {schema}.mt_streams_identity …</c>, a rich append's
/// <c>update {schema}.mt_streams set version = … returning version</c>, <c>insert into {schema}.mt_events (</c> and a quick
/// append's <c>select {schema}.mt_quick_append_events(</c>: <c>src/Marten/EventStorage/Dialects/PostgresEventStoreDialect.cs</c>
/// lines 293, 325, 347, 612 and 743; and a tag table's <c>insert into</c> (<c>src/Marten/Events/Operations/InsertEventTagOperation.cs</c>
/// line 38).</description></item>
/// <item><description><b>event read.</b> <c>select version from {schema}.mt_streams where id = $1</c> (the same dialect, line
/// 771), and every other read of the event store's tables that is not the daemon's.</description></item>
/// <item><description><b>projection progress.</b> <c>insert into {schema}.mt_event_progression (name, last_seq_id) values ($1,
/// $2)</c> (<c>src/Marten/Events/Daemon/Progress/InsertProjectionProgress.cs</c> line 38), <c>update … mt_event_progression set
/// last_seq_id = $1, last_updated = transaction_timestamp() where name = $2 and last_seq_id = $3</c>
/// (<c>UpdateProjectionProgress.cs</c>, line 43 at V9.39.0 and 45 at v9.51.0), <c>delete from … where name = $1</c>
/// (<c>DeleteProjectionProgress.cs</c> line 34), and <c>select name, last_seq_id … from {schema}.mt_event_progression</c>
/// (<c>ProjectionProgressStatement.cs</c> lines 48-60 at V9.39.0, 55-67 at v9.51.0, which reads <c>last_updated</c> too) -
/// and the high-water mark's own progression rows (<c>src/Marten/Events/Daemon/HighWater/HighWaterDetector.cs</c>), which are
/// progression rows too.</description></item>
/// <item><description><b>daemon.</b> The daemon's reads of the event sequence: the high-water mark's
/// <c>select coalesce(max(seq_id), 0) from {schema}.mt_events</c> (<c>HighWaterDetector.cs</c> line 192) and its gap
/// detection (<c>GapDetector.cs</c> lines 45-52), the event loader's <c>… where d.seq_id &gt; $1 and d.seq_id &lt;= $2</c>
/// (<c>src/Marten/Events/Daemon/Internals/EventLoader.cs</c> line 146), and any read of <c>mt_events_sequence</c> or of the
/// progression rows against the events.</description></item>
/// </list>
/// <para>
/// <b>A hidden type is never named.</b> A statement that names an <c>mt_doc_</c> table the judged set does not hold - a type
/// <c>IsDocumentTypeVisible</c> hides, another registered store's, or one Marten has not learned - is grouped as
/// <see cref="Hidden" /> whatever else it names, and its text is withheld by the page as it is everywhere
/// (<c>SessionQueryText</c>). Everything that is none of Marten's shapes is grouped as Postgres Studio groups it, by its verb
/// and its first table of the set (<see cref="PostgresStatementClassifier" />): the host's own relational tables beside the
/// store (issue #8 G1).
/// </para>
/// </remarks>
internal sealed partial class MartenStatementClassifier : IStatementClassifier
{
    /// <summary>The group of every statement that names a document table the studio does not show.</summary>
    public static readonly StatementGroup Hidden = new("hidden", "a hidden type");

    /// <summary>An event append, however Marten appends.</summary>
    public static readonly StatementGroup EventAppend = new("events|append", "event append");

    /// <summary>A read of the event store that is not the daemon's.</summary>
    public static readonly StatementGroup EventRead = new("events|read", "event read");

    /// <summary>The projections' progression rows.</summary>
    public static readonly StatementGroup ProjectionProgress = new("events|progress", "projection progress");

    /// <summary>The daemon's reads of the event sequence.</summary>
    public static readonly StatementGroup Daemon = new("events|daemon", "daemon");

    /// <summary>The one classifier: it holds nothing.</summary>
    public static MartenStatementClassifier Instance { get; } = new();

    /// <summary>The verbs of a read - and of a call, which Marten's quick append and the daemon's functions are.</summary>
    private static readonly HashSet<string> ReadVerbs = new(StringComparer.OrdinalIgnoreCase) { "select", "with", "values", "table" };

    /// <inheritdoc />
    public StatementGroup? Classify(string text, IStudioTableSet tables)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tables);

        if (PostgresStatementClassifier.Verb(text) is not { } verb)
        {
            return null;
        }

        // A comment is free text, never a table the statement names: a sqlcommenter tag cannot make a statement a hidden type's.
        text = StatementReads.MaskComments(text);

        if (NamesUnlistedDocumentTable(text, tables))
        {
            return Hidden;
        }

        IReadOnlyList<StudioTable> named = StatementReads.Named(text, tables);
        StudioTable? target = named.Count > 0 ? named[0] : null;

        bool Names(Func<StudioTable, bool> which) => named.Any(which);
        bool namesEvents = Names(static x => IsEvent(x, "mt_events"));
        bool namesProgression = Names(static x => IsEvent(x, "mt_event_progression"));
        bool namesSequence = EventSequence().IsMatch(text);

        if (QuickAppend().IsMatch(text))
        {
            return EventAppend;
        }

        // T22's guarded writes first: the shapes it counts conflicts on, named the same way here.
        if (ConcurrencyConflictCheck.Classify(text) is { } guarded)
        {
            switch (guarded.Kind)
            {
                case GuardedWriteKind.StreamVersion when target is { Kind: StudioTableKind.EventTable }:
                    return EventAppend;
                case GuardedWriteKind.Upsert when target is { Kind: StudioTableKind.DocumentTable } document:
                    return Document("upsert", document);
                case GuardedWriteKind.Update when target is { Kind: StudioTableKind.DocumentTable } document:
                    return Document("update", document);
            }
        }

        switch (verb)
        {
            case "insert" or "copy" when target is { Kind: StudioTableKind.EventTable } events:
                return IsEvent(events, "mt_event_progression") ? ProjectionProgress
                    : IsAppendTarget(events) ? EventAppend
                    : PostgresStatementClassifier.Of(verb, events);

            case "insert" or "copy" when target is { Kind: StudioTableKind.DocumentTable } document:
                return Document(UpsertSet().IsMatch(text) ? "upsert" : "insert", document);

            case "update" when target is { Kind: StudioTableKind.EventTable } events:
                return IsEvent(events, "mt_event_progression") ? ProjectionProgress
                    : IsEvent(events, "mt_streams") && StreamVersion().IsMatch(text) ? EventAppend
                    : PostgresStatementClassifier.Of(verb, events);

            case "update" when target is { Kind: StudioTableKind.DocumentTable } document:
                // A soft delete and an undelete both set mt_deleted; only the delete stamps mt_deleted_at with now().
                return Document(
                    !SoftDeleteFlag().IsMatch(text) ? "update" : DeletedNow().IsMatch(text) ? "delete" : "undelete",
                    document);

            case "delete" when target is { Kind: StudioTableKind.EventTable } events:
                return IsEvent(events, "mt_event_progression") ? ProjectionProgress : PostgresStatementClassifier.Of(verb, events);

            case "delete" when target is { Kind: StudioTableKind.DocumentTable } document:
                return Document("delete", document);

            case "insert" or "update" or "delete" or "copy" or "merge":
                return PostgresStatementClassifier.Instance.Classify(text, tables);
        }

        // Marten's own migrations (create, alter, do), a transaction's control and every other utility statement: by their
        // verb and their table, never as the reads they name tables like.
        if (!ReadVerbs.Contains(verb))
        {
            return PostgresStatementClassifier.Instance.Classify(text, tables);
        }

        // A read, or a call.
        if (namesProgression)
        {
            return namesEvents || namesSequence ? Daemon : ProjectionProgress;
        }

        if (namesSequence || (namesEvents && DaemonRead().IsMatch(text)))
        {
            return Daemon;
        }

        if (namesEvents || Names(static x => x.Kind == StudioTableKind.EventTable))
        {
            return EventRead;
        }

        if (named.FirstOrDefault(static x => x.Kind == StudioTableKind.DocumentTable) is { } read)
        {
            return Document("load/query", read);
        }

        return PostgresStatementClassifier.Instance.Classify(text, tables);
    }

    /// <summary>A document type's group: the operation and the type, as the configuration names it.</summary>
    internal static StatementGroup Document(string operation, StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new StatementGroup("doc|" + operation + "|" + table.Key, operation + " · " + (table.TypeName ?? table.Alias ?? table.Table))
        {
            Table = table,
            Verb = operation,
        };
    }

    /// <summary>
    /// Whether <paramref name="text" /> names an <c>mt_doc_</c> table the set does not hold - qualified, in that schema;
    /// bare, in any schema of the set.
    /// </summary>
    internal static bool NamesUnlistedDocumentTable(string text, IStudioTableSet tables)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tables);

        foreach (Match match in DocumentTableName().Matches(text))
        {
            string name = match.Groups["name"].Value;
            bool listed = match.Groups["schema"].Success
                ? tables.Find(match.Groups["schema"].Value, name) is not null
                : tables.Tables.Any(x => string.Equals(x.Table, name, StringComparison.OrdinalIgnoreCase));

            if (!listed)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEvent(StudioTable table, string name) =>
        table.Kind == StudioTableKind.EventTable && string.Equals(table.Table, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The event store's tables an append writes: the events, the streams, strict identity's and the DCB tags'.</summary>
    private static bool IsAppendTarget(StudioTable table) =>
        IsEvent(table, "mt_events") || IsEvent(table, "mt_streams") || IsEvent(table, "mt_streams_identity")
        || (table.Kind == StudioTableKind.EventTable && table.Table.StartsWith("mt_event_tag_", StringComparison.OrdinalIgnoreCase));

    /// <summary>A document table's name, optionally qualified, either part optionally quoted.</summary>
    [GeneratedRegex(
        "(?<![A-Za-z0-9_$\"])(?:\"?(?<schema>[A-Za-z_][A-Za-z0-9_$]*)\"?\\s*\\.\\s*)?\"?(?<name>mt_doc_[A-Za-z0-9_$]+)\"?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentTableName();

    /// <summary>A quick append: Marten's own function, called.</summary>
    [GeneratedRegex(@"\bmt_quick_append_events\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuickAppend();

    /// <summary>The event store's sequence.</summary>
    [GeneratedRegex(@"\bmt_events_sequence\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EventSequence();

    /// <summary>An upsert's <c>on conflict (…) do update set</c>.</summary>
    [GeneratedRegex(@"\bon\s+conflict\b[^;]*?\bdo\s+update\s+set\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UpsertSet();

    /// <summary>A soft delete's or an undelete's <c>set mt_deleted =</c>.</summary>
    [GeneratedRegex(@"\bset\s+""?mt_deleted""?\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SoftDeleteFlag();

    /// <summary>
    /// A soft delete's <c>set mt_deleted = …, mt_deleted_at = now()</c>, which an undelete's <c>mt_deleted_at = NULL</c> is not -
    /// in the set list, so a predicate on the column elsewhere does not count.
    /// </summary>
    [GeneratedRegex(
        @"\bset\s+""?mt_deleted""?\s*=\s*[^,;]*,\s*""?mt_deleted_at""?\s*=\s*(?:""?pg_catalog""?\s*\.\s*)?""?now""?\s*\(\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeletedNow();

    /// <summary>A rich append's <c>set version =</c>.</summary>
    [GeneratedRegex(@"\bset\s+""?version""?\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StreamVersion();

    /// <summary>The daemon's reads of the events by their sequence: a range, a high-water mark, a gap.</summary>
    [GeneratedRegex(@"\b(?:max|min)\s*\(\s*""?seq_id""?\s*\)|\bseq_id""?\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DaemonRead();
}
