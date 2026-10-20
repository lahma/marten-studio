using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>Where an index on a judged table comes from - which decides what a remedy can say about it.</summary>
internal enum IndexOrigin
{
    /// <summary>The primary key: never redundant, never unused, never a remedy's business.</summary>
    PrimaryKey,

    /// <summary>
    /// One the source's library adds to a table by itself - Marten's <c>mt_doc_type</c> for a hierarchy, <c>mt_deleted</c>
    /// for soft deletes, <c>tenant_id</c> for one tenancy ordering. No line removes it.
    /// </summary>
    Structural,

    /// <summary>One a document type declares: <c>Index</c>, <c>Duplicate</c>, <c>UniqueIndex</c>, a GIN index...</summary>
    Declared,

    /// <summary>One the event store's configuration declares.</summary>
    EventStore,

    /// <summary>One a projection's or an extended table's Weasel definition declares.</summary>
    Managed,

    /// <summary>One the configuration tells the source's migration to leave alone (Marten's <c>IgnoreIndex</c>).</summary>
    Ignored,

    /// <summary>One nobody declares: the source's next apply drops it (Marten's <c>CreateOrUpdate</c> is not additive).</summary>
    Undeclared,

    /// <summary>
    /// One on a table the host's own migrations own (<see cref="StudioTableKind.Relational" />): nothing reconciles it, so no
    /// apply drops it, and only whoever owns the schema changes it.
    /// </summary>
    Host,
}

/// <summary>One judged table and what the catalog read about it.</summary>
/// <param name="Table">The table, as its source describes it.</param>
/// <param name="Row">What <see cref="HealthTableQueries.Tables" /> read.</param>
internal sealed record TableFacts(StudioTable Table, HealthTableRow Row)
{
    /// <summary>The table's key.</summary>
    public string Key => Table.Key;

    /// <summary>The rows Postgres knows of: the collector's live count, else the planner's estimate.</summary>
    public long Rows => Row.LiveRows ?? Math.Max(Row.EstimatedRows, 0);

    /// <summary>The share of dead rows among live and dead, or <see langword="null" /> when the collector has nothing.</summary>
    public double? DeadShare => Row is { LiveRows: { } live, DeadRows: { } dead } && live + dead > 0
        ? (double) dead / (live + dead)
        : null;

    /// <summary>The share of updates that were HOT, since the counters began, or <see langword="null" /> before the first update.</summary>
    public double? HotShare => Row is { Updates: { } updates, HotUpdates: { } hot } && updates > 0 ? (double) hot / updates : null;

    /// <summary>The latest vacuum of either kind.</summary>
    public DateTimeOffset? LastVacuumOfAnyKind => Latest(Row.LastVacuum, Row.LastAutovacuum);

    /// <summary>The latest analyze of either kind.</summary>
    public DateTimeOffset? LastAnalyzeOfAnyKind => Latest(Row.LastAnalyze, Row.LastAutoanalyze);

    /// <summary>Whether the table's own <c>autovacuum_enabled</c> storage parameter is off.</summary>
    public bool AutovacuumOffForTable =>
        StorageParameters.Parse(Row.StorageOptions).TryGetValue("autovacuum_enabled", out string? value)
        && StorageParameters.IsFalse(value);

    /// <summary>
    /// Whether autovacuum leaves this table alone: the server's <c>autovacuum</c> is off, or the table's own
    /// <c>autovacuum_enabled</c> storage parameter is.
    /// </summary>
    public bool AutovacuumOff => !Row.AutovacuumEnabled || AutovacuumOffForTable;

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

/// <summary>One index on a judged table, with where it comes from.</summary>
/// <param name="Table">The table it is on.</param>
/// <param name="Row">What <see cref="HealthTableQueries.Indexes" /> read.</param>
/// <param name="Origin">Where it comes from.</param>
internal sealed record IndexFacts(StudioTable Table, HealthIndexRow Row, IndexOrigin Origin)
{
    /// <summary>The index's key: schema, table, index - the <c>Subject</c> of a finding about it, and what the Schema tab marks by.</summary>
    public string Key => Row.Key;

    /// <summary>Whether anything in the configuration, or the host's own schema, can be changed to remove it.</summary>
    public bool Removable => Origin is IndexOrigin.Declared or IndexOrigin.Managed or IndexOrigin.Undeclared or IndexOrigin.Ignored or IndexOrigin.Host;
}

/// <summary>The database-wide facts every row of the table read carries.</summary>
/// <param name="StatsReset">When the statistics were last reset, or <see langword="null" /> for never.</param>
/// <param name="AutovacuumEnabled">The server's <c>autovacuum</c>.</param>
/// <param name="MaxParallelWorkersPerGather">How many workers one scan may add.</param>
internal sealed record DatabaseStatistics(DateTimeOffset? StatsReset, bool AutovacuumEnabled, int MaxParallelWorkersPerGather)
{
    /// <summary>Nothing read: no table to read about.</summary>
    public static DatabaseStatistics Unknown { get; } = new(null, true, 2);

    /// <summary>The facts the first row carries - every row carries the same ones.</summary>
    public static DatabaseStatistics Of(IReadOnlyList<TableFacts> tables) =>
        tables.Count == 0
            ? Unknown
            : new DatabaseStatistics(tables[0].Row.StatsReset, tables[0].Row.AutovacuumEnabled, tables[0].Row.MaxParallelWorkersPerGather);
}

/// <summary>
/// Reads the tables the sweep judges - from the studio's <see cref="IHealthTableSource" /> - and the catalog's facts about
/// them, once per sweep, for the tables-and-indexes checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set is the source's</b>, built without a statement where it can be (a store's configuration) and never by a
/// call that migrates (AGENTS.md hard rule 14). A hidden document type is not in it, so its name is not even a parameter
/// of a statement here, and a finding names the document type it is about besides, so the sweep would drop it anyway.
/// </para>
/// <para>
/// <b>Read once per sweep, whichever checks ask</b> (<see cref="HealthCheckContext.Shared{T}" />), and shown under every
/// asking check's "Show SQL". A set with no table opens no connection at all.
/// </para>
/// </remarks>
internal static class HealthTables
{
    /// <summary>The memo key of the judged-table set.</summary>
    internal const string SetKey = "tables.set";

    /// <summary>The memo key of the table read.</summary>
    internal const string TablesKey = "tables.stats";

    /// <summary>The memo key of the index read.</summary>
    internal const string IndexesKey = "tables.indexes";

    /// <summary>What a status row says when the studio registered no table source.</summary>
    internal const string NoSource = "This studio registers no table source, so no table is judged.";

    /// <summary>The tables the sweep judges, built once per sweep by the studio's source.</summary>
    /// <exception cref="HealthCheckUnavailableException">The studio registered no <see cref="IHealthTableSource" />.</exception>
    public static Task<IStudioTableSet> SetAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Shared(SetKey, _ =>
            context.Services.GetService<IHealthTableSource>() is { } source
                ? source.TablesAsync(context)
                : throw new HealthCheckUnavailableException(NoSource));
    }

    /// <summary>The judged tables and the catalog's row for each, read once per sweep.</summary>
    public static async Task<IReadOnlyList<TableFacts>> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await SetAsync(context).ConfigureAwait(false);
        return await TablesAsync(context, set).ConfigureAwait(false);
    }

    /// <summary>
    /// The tables of <paramref name="set" /> and the catalog's row for each, read once per sweep - for a caller that holds
    /// the set already. Every caller of one sweep must hand the same set: the first one's is the one read.
    /// </summary>
    internal static Task<IReadOnlyList<TableFacts>> TablesAsync(HealthCheckContext context, IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(set);

        return context.Shared(TablesKey, token => ReadTablesAsync(context, set, token));
    }

    /// <summary>Every index on the judged tables, read once per sweep like <see cref="TablesAsync(HealthCheckContext)" />.</summary>
    public static async Task<IReadOnlyList<IndexFacts>> IndexesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await SetAsync(context).ConfigureAwait(false);
        return await IndexesAsync(context, set).ConfigureAwait(false);
    }

    /// <summary>Every index on the tables of <paramref name="set" />, as <see cref="TablesAsync(HealthCheckContext, IStudioTableSet)" />.</summary>
    internal static Task<IReadOnlyList<IndexFacts>> IndexesAsync(HealthCheckContext context, IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(set);

        return context.Shared(IndexesKey, token => ReadIndexesAsync(context, set, token));
    }

    /// <summary>What the statements bind, for every table of <paramref name="set" />.</summary>
    public static IReadOnlyList<HealthTableTarget> Targets(IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return [.. set.Tables.Select(static x => x.Target)];
    }

    /// <summary>The judged tables and the catalog's row for each of them; a table not created yet has none.</summary>
    private static async Task<IReadOnlyList<TableFacts>> ReadTablesAsync(
        HealthCheckContext context,
        IStudioTableSet set,
        CancellationToken cancellationToken)
    {
        if (set.Tables.Count == 0)
        {
            // Nothing to ask about, so no connection is opened at all.
            return [];
        }

        IReadOnlyList<HealthTableRow> rows = await context.Sql
            .QueryAsync(HealthTableQueries.Tables(Targets(set)), HealthTableQueries.ReadTable, cancellationToken)
            .ConfigureAwait(false);

        List<TableFacts> facts = new(rows.Count);
        foreach (HealthTableRow row in rows)
        {
            if (set.Find(row.Schema, row.Table) is { } table)
            {
                facts.Add(new TableFacts(table, row));
            }
        }

        return facts;
    }

    /// <summary>Every index on the judged tables, with where each comes from.</summary>
    private static async Task<IReadOnlyList<IndexFacts>> ReadIndexesAsync(
        HealthCheckContext context,
        IStudioTableSet set,
        CancellationToken cancellationToken)
    {
        if (set.Tables.Count == 0)
        {
            return [];
        }

        IReadOnlyList<HealthIndexRow> rows = await context.Sql
            .QueryAsync(HealthTableQueries.Indexes(Targets(set)), HealthTableQueries.ReadIndex, cancellationToken)
            .ConfigureAwait(false);

        List<IndexFacts> facts = new(rows.Count);
        foreach (HealthIndexRow row in rows)
        {
            if (set.Find(row.Schema, row.Table) is { } table)
            {
                facts.Add(new IndexFacts(table, row, set.OriginOf(row, table)));
            }
        }

        return facts;
    }
}

/// <summary>How a judged table is named in a finding.</summary>
internal static class StudioTableText
{
    extension(StudioTable table)
    {
        /// <summary>
        /// Whether the table sits outside its source's own schemas - a schema the database browser withholds from a visitor
        /// short of its gate (D27), which a finding naming the table is gated on (<see cref="HealthFinding.GatedSchemas" />).
        /// </summary>
        public bool OutsideOwnSchemas => table.GatedSchemas.Count > 0;

        /// <summary>
        /// The table's key for text shown at the check's gate with no per-visitor schema check - a status row's note, or a
        /// database-wide finding such as T12's that must not vanish because one table in it is gated: the key itself, or -
        /// for a table outside its source's own schemas - words that name neither it nor its schema.
        /// </summary>
        public string NoteName => !table.OutsideOwnSchemas
            ? table.Key
            : table.Kind == StudioTableKind.Projection
                ? "a Marten-managed table outside the store's own schemas"
                : "a table outside the studio's own schemas";

        /// <summary>What the table is, for a sentence: "the Order collection's table", "the event store's table", "the table".</summary>
        public string Describe() => table.Kind switch
        {
            StudioTableKind.DocumentTable when table.TypeName is { } type => "the " + type + " collection's table " + table.Key,
            StudioTableKind.DocumentTable => "the document table " + table.Key,
            StudioTableKind.EventTable => "the event store's table " + table.Key,
            StudioTableKind.Projection => "the Marten-managed table " + table.Key,
            StudioTableKind.Relational when table.Library is { } library => "the " + library + " table " + table.Key,
            StudioTableKind.Relational => "the table " + table.Key,
            _ => (table.Library ?? "Marten") + "'s bookkeeping table " + table.Key,
        };

        /// <summary><c>Describe()</c>, or <c>NoteName</c> for a table outside its source's own schemas.</summary>
        public string DescribeUngated() => table.OutsideOwnSchemas ? table.NoteName : table.Describe();
    }
}

/// <summary>How a cumulative counter changed over the history: from the last reset, to now.</summary>
/// <param name="Change">The change.</param>
/// <param name="From">The first sample of the run.</param>
/// <param name="To">The latest sample.</param>
internal sealed record CounterDelta(double Change, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>How long the change was measured over.</summary>
    public TimeSpan Span => To - From;
}

/// <summary>Rates and persistence over <see cref="HealthHistory" />, for the evaluators. Pure.</summary>
internal static class TableHistory
{
    /// <summary>
    /// The change of a cumulative counter over its latest unbroken run, or <see langword="null" /> before two
    /// samples of it exist. A value lower than the one before it is a reset - <c>pg_stat_reset()</c>, a crash,
    /// a table recreated - and the run starts again there rather than reporting a negative rate.
    /// </summary>
    public static CounterDelta? Delta(HealthTrend trend)
    {
        ArgumentNullException.ThrowIfNull(trend);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = trend.Points;
        if (points.Count < 2)
        {
            return null;
        }

        int start = points.Count - 1;
        while (start > 0 && points[start - 1].Value <= points[start].Value)
        {
            start--;
        }

        (DateTimeOffset At, double Value) first = points[start];
        (DateTimeOffset At, double Value) last = points[^1];

        return start == points.Count - 1 || last.At <= first.At
            ? null
            : new CounterDelta(last.Value - first.Value, first.At, last.At);
    }

    /// <summary>
    /// Since when a condition has held without a break, over the samples the check ran in - or
    /// <see langword="null" /> when it does not hold in the latest one.
    /// </summary>
    /// <param name="history">The database's samples, this sweep's included.</param>
    /// <param name="sweptSeries">The series the check writes in every sweep it runs in.</param>
    /// <param name="conditionSeries">The series it writes, as 1, while the condition holds.</param>
    public static DateTimeOffset? HeldSince(HealthHistoryView history, string sweptSeries, string conditionSeries)
    {
        ArgumentNullException.ThrowIfNull(history);

        DateTimeOffset? since = null;

        foreach (HealthSample sample in history.Samples)
        {
            if (!sample.Values.ContainsKey(sweptSeries))
            {
                // A sweep that did not run the check says nothing either way.
                continue;
            }

            since = sample.Values.TryGetValue(conditionSeries, out double value) && value > 0
                ? since ?? sample.At
                : null;
        }

        return since;
    }
}

/// <summary>
/// What T07 and T08 say about autovacuum for a table, and the remedy that fits its state: the server's
/// switch, the table's own switch, or a scale factor.
/// </summary>
internal static class Autovacuum
{
    /// <summary>
    /// The remedy for a table autovacuum is not keeping up with: switch it back on for the table, or lower the scale
    /// factor - worded by the studio (<see cref="IHealthTableRemedies" />) - and nothing for a server whose
    /// <c>autovacuum</c> is off, which no table's setting changes (<see cref="State" /> says so in words).
    /// </summary>
    /// <param name="remedies">How the studio words a table remedy, or <see langword="null" /> for none.</param>
    /// <param name="table">The table.</param>
    /// <param name="parameter">The scale factor's storage parameter.</param>
    /// <param name="value">Its suggested value.</param>
    /// <param name="explanation">What the setting changes, in words that hold for any source.</param>
    public static HealthRemedy? Remedy(IHealthTableRemedies? remedies, TableFacts table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (remedies is null || !table.Row.AutovacuumEnabled)
        {
            return null;
        }

        return table.AutovacuumOffForTable
            ? remedies.ReenableAutovacuum(table.Table)
            : remedies.AutovacuumSetting(table.Table, parameter, value, explanation);
    }

    /// <summary>The sentence about the table's autovacuum state that a finding's "why" ends with, or nothing.</summary>
    public static string State(TableFacts table, string parameter, string value)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (!table.Row.AutovacuumEnabled)
        {
            return " Autovacuum is switched off for the whole server (autovacuum = off), so nothing will look after this table "
                + "until a DBA does.";
        }

        if (table.AutovacuumOffForTable)
        {
            string off = " Autovacuum is switched off for this table (autovacuum_enabled = false"
                + (table.Row.IsPartitioned ? " on every partition" : string.Empty) + ").";

            // A document table's remedy is a configuration line, and a relational table's the host's own statement (Remedy).
            // Any other table's storage parameters are not something a line the studio can name sets, so the way back is
            // said in words, never as DDL.
            return table.Table.Kind switch
            {
                StudioTableKind.DocumentTable => off,
                StudioTableKind.Relational => off + " Switching it back on is the host's to do, in its own migrations, where this "
                    + "table's storage parameters are set.",
                _ => off + " Switching it back on is the host's to do: re-enable autovacuum on this table where its storage parameters "
                    + "are set - the host's own DDL, or (Marten 9.47+) the table's declaration where the host hands it to Marten (a "
                    + "projection's table, or StoreOptions.Storage.ExtendedSchemaObjects).",
            };
        }

        string partly = table.Row.DifferingStorageOptions.Contains("autovacuum_enabled", StringComparer.OrdinalIgnoreCase)
            ? " Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off for some "
              + "of them (the Schema screen's Tables tab shows which parameters the partitions differ on)."
            : string.Empty;

        if (table.Table.Kind is StudioTableKind.DocumentTable or StudioTableKind.Relational)
        {
            return partly;
        }

        // From Marten 9.47 a projection's or an ExtendedSchemaObjects table's Weasel declaration can carry storage
        // parameters, and an apply then sets a declared one back; Marten's own event-store and bookkeeping tables still
        // declare none a host can set.
        string apply = table.Table.Kind == StudioTableKind.Projection
            ? "no Marten apply resets it unless the apply rebuilds the table, as a changed partition scheme does - or, from "
              + "Marten 9.47, unless the table's own declaration (a projection's table, or an ExtendedSchemaObjects table) "
              + "declares " + parameter + ", which an apply then sets back to the declared value."
            : "no Marten apply resets it unless the apply rebuilds the table, as a changed partition scheme does.";

        return partly + " No StoreOptions line the studio can suggest sets this table's autovacuum settings; a DBA's ALTER TABLE ... SET ("
            + parameter + " = " + value + ") does, and " + apply;
    }
}

/// <summary>The wording the tables-and-indexes findings share.</summary>
internal static class TableText
{
    /// <summary>A count of rows, prefixed <c>~</c> when it is an estimate (D8).</summary>
    public static string Rows(long rows, bool estimate = true) =>
        (estimate ? "~" : string.Empty) + rows.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A count.</summary>
    public static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A share, as a whole percentage - or one decimal under ten percent.</summary>
    public static string Percent(double share) =>
        (share * 100).ToString(share < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A size.</summary>
    public static string Bytes(long bytes) => SchemaFormat.Bytes(bytes);

    /// <summary>A moment, in UTC: evidence is formatted once, for every visitor of the sweep.</summary>
    public static string When(DateTimeOffset? at) =>
        at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";

    /// <summary>A span, as a person says it.</summary>
    public static string Span(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => "under a minute",
        _ when span < TimeSpan.FromHours(2) => ((int) span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min",
        _ when span < TimeSpan.FromDays(2) => ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h",
        _ => ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days",
    };

    /// <summary>How long ago, or "never".</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? Span(now - value) + " ago (" + When(value) + ")" : "never, as far as the statistics collector knows";

    /// <summary>
    /// The caveat every counter-based finding carries: how old the counters are, and what zeroes them.
    /// </summary>
    public static string CountersSince(DateTimeOffset? statsReset, DateTimeOffset now) =>
        statsReset is { } reset
            ? "Counters since the statistics were last reset, " + Ago(reset, now) + ". pg_stat_reset() and a crash zero them."
            : "Counters since the statistics were last reset - never, as pg_stat_database reports it, so they may span the "
              + "whole life of the database; a crash zeroes them without saying so.";
}
