using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

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

    /// <summary>
    /// One on a table a Marten store manages that this studio has no configuration of - an <c>mt_</c> table seen by a
    /// studio over plain data sources. That store's next apply decides it, so it is changed in that store's
    /// <c>StoreOptions</c>, never by a migration here.
    /// </summary>
    StoreManaged,
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

/// <summary>The sweep's index read: the indexes it read, and the tables locked for a schema change it left out.</summary>
/// <param name="Indexes">Every index on the judged tables but the locked ones.</param>
/// <param name="LockedTables">
/// The keys (<see cref="StudioTable.Key" />, compared ordinally, as Postgres compares names: <c>"Orders"</c> and
/// <c>orders</c> are two tables) of the judged tables somebody held or awaited <c>ACCESS EXCLUSIVE</c> on when the read
/// ran: their indexes were not deparsed, so a check that judges a table by its indexes skips them this sweep and says how
/// many (<see cref="HealthTables.LockedNote" />).
/// </param>
internal sealed record HealthIndexRead(IReadOnlyList<IndexFacts> Indexes, IReadOnlySet<string> LockedTables)
{
    /// <summary>No table to read about.</summary>
    public static HealthIndexRead Empty { get; } = new([], new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// The indexes of the locked tables, read but not deparsed: <see cref="HealthIndexRow.Definition" /> and
    /// <see cref="HealthIndexRow.Columns" /> are empty, and everything the catalog holds without opening the table - validity,
    /// scans, size, origin - is there. Never judged; a check whose history must not skip a sweep (T05's "unscanned since")
    /// samples them.
    /// </summary>
    public IReadOnlyList<IndexFacts> LockedIndexes { get; init; } = [];

    /// <summary>Whether the table of <paramref name="tableKey" /> was locked and its indexes left out.</summary>
    public bool IsLocked(string tableKey) => LockedTables.Contains(tableKey);
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

    /// <summary>
    /// <paramref name="verdict" /> with what the judged set left out said in it - every finding's caveat and the status
    /// note end with "Judged: the 2,000 largest of 3,412 tables by estimated size …" - so that a capped set's quiet is
    /// never read as a clean bill for the tables it did not judge (design R5). The sweep applies it to every check of
    /// the Tables area; with no <paramref name="coverage" />, the set is every table, and nothing is added.
    /// </summary>
    /// <param name="verdict">A Tables-area check's verdict.</param>
    /// <param name="coverage">The set's <see cref="IStudioTableSet.Coverage" />.</param>
    public static HealthVerdict WithCoverage(HealthVerdict verdict, string? coverage)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        if (string.IsNullOrWhiteSpace(coverage))
        {
            return verdict;
        }

        string sentence = "Judged: " + coverage + ".";

        return new HealthVerdict(
            [.. verdict.Findings.Select(finding => finding with { Caveat = finding.Caveat is { Length: > 0 } caveat ? caveat + " " + sentence : sentence })],
            verdict.Note is { Length: > 0 } note ? note + " " + sentence : sentence);
    }

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

    /// <summary>
    /// The sweep's index read: every index on the judged tables, and the tables locked for a schema change whose indexes it
    /// left out (<see cref="HealthIndexRead.LockedTables" />), read once per sweep.
    /// </summary>
    /// <remarks>
    /// The only way to the indexes, on purpose: a check that judges a table by its indexes must know which tables it could
    /// not read, or a locked table reads as one with no covering index, no blocking index, nothing redundant.
    /// </remarks>
    public static async Task<HealthIndexRead> IndexReadAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await SetAsync(context).ConfigureAwait(false);
        return await IndexReadAsync(context, set).ConfigureAwait(false);
    }

    /// <summary>The index read of the tables of <paramref name="set" />, as <see cref="IndexReadAsync(HealthCheckContext)" />.</summary>
    internal static Task<HealthIndexRead> IndexReadAsync(HealthCheckContext context, IStudioTableSet set)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(set);

        return context.Shared(IndexesKey, token => ReadIndexesAsync(context, set, token));
    }

    /// <summary>
    /// What a check that judges tables by their indexes adds to its status row while some were locked: "1 table was locked
    /// for a schema change and not judged this sweep." - a count, never a name, because the row is shown at the check's
    /// gate. <see langword="null" /> when none was.
    /// </summary>
    public static string? LockedNote(IReadOnlySet<string> lockedTables)
    {
        ArgumentNullException.ThrowIfNull(lockedTables);

        return lockedTables.Count switch
        {
            0 => null,
            1 => "1 table was locked for a schema change and not judged this sweep.",
            int count => SessionText.Number(count) + " tables were locked for a schema change and not judged this sweep.",
        };
    }

    /// <summary><paramref name="note" /> followed by <see cref="LockedNote" />, or whichever of them there is.</summary>
    public static string? WithLockedNote(string? note, IReadOnlySet<string> lockedTables) =>
        (note, LockedNote(lockedTables)) switch
        {
            ({ Length: > 0 } first, { } locked) => first + " " + locked,
            (_, { } locked) => locked,
            _ => note,
        };

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

    /// <summary>
    /// Every index on the judged tables, with where each comes from - and none of a table locked for a schema change, whose
    /// key goes in <see cref="HealthIndexRead.LockedTables" /> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table somebody holds or awaits <c>ACCESS EXCLUSIVE</c> on - a migration - comes back undeparsed rather than queued
    /// behind (<see cref="HealthTableQueries.IndexesSql" />). Its indexes cannot be judged, nor told from a declared one by
    /// their definition, so they are left out and the table is counted: every other table is still judged, and each check
    /// that judges by indexes says how many were not (<see cref="LockedNote" />).
    /// </para>
    /// <para>
    /// <b>The residual race, once.</b> A lock taken after the statement's <c>pg_locks</c> snapshot makes a deparse wait out
    /// <c>lock_timeout</c> and the statement fail with <c>55P03</c>. The read is then asked once more, when the snapshot sees
    /// the lock and leaves that table out (<see cref="RetryOnceBehindALockAsync{T}" />). A second <c>55P03</c> is not
    /// guessed at - it may be a lock the snapshot cannot see, or one taken again in that instant - so it reaches the sweep,
    /// which says the checks waited too long for a lock (Unavailable, TimedOut), as it would of any read.
    /// </para>
    /// </remarks>
    private static async Task<HealthIndexRead> ReadIndexesAsync(
        HealthCheckContext context,
        IStudioTableSet set,
        CancellationToken cancellationToken)
    {
        if (set.Tables.Count == 0)
        {
            return HealthIndexRead.Empty;
        }

        IReadOnlyList<HealthIndexRow> rows = await RetryOnceBehindALockAsync(
                token => context.Sql.QueryAsync(HealthTableQueries.Indexes(Targets(set)), HealthTableQueries.ReadIndex, token),
                cancellationToken)
            .ConfigureAwait(false);

        // Keyed as the table set keys its tables - ordinally - so a lock on "Orders" skips that table and never orders.
        HashSet<string> locked = new(StringComparer.Ordinal);
        List<IndexFacts> facts = new(rows.Count);
        List<IndexFacts> undeparsed = [];

        foreach (HealthIndexRow row in rows)
        {
            if (set.Find(row.Schema, row.Table) is not { } table)
            {
                continue;
            }

            // An origin is decided by the index's key, never its text, so a locked table's index is told apart as well.
            IndexFacts index = new(table, row, set.OriginOf(row, table));

            if (row.TableLocked)
            {
                locked.Add(table.Key);
                undeparsed.Add(index);
                continue;
            }

            facts.Add(index);
        }

        return new HealthIndexRead(facts, locked) { LockedIndexes = undeparsed };
    }

    /// <summary>
    /// Runs <paramref name="read" />, and once more if the first attempt waited out <c>lock_timeout</c> (<c>55P03</c>); a
    /// second <c>55P03</c>, and anything else, is the caller's.
    /// </summary>
    /// <param name="read">The read, given the cancellation token.</param>
    /// <param name="cancellationToken">Cancels both attempts.</param>
    internal static async Task<T> RetryOnceBehindALockAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        try
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when ((exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState
                                           == PostgresErrorCodes.LockNotAvailable)
        {
            // Taken between the snapshot and a deparse: the second attempt's snapshot sees it, and leaves that table out.
        }

        return await read(cancellationToken).ConfigureAwait(false);
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
/// What autovacuum's three triggers are computed from, for one table: its <c>pg_class</c> size facts, and each threshold
/// and scale factor in force - the table's own storage parameter where it has one, the server's setting where it does not.
/// </summary>
/// <remarks>Init properties with PostgreSQL's defaults, so a unit test states only what its case is about.</remarks>
internal sealed record AutovacuumInputs
{
    /// <summary><c>reltuples</c>: <c>-1</c> for a table never vacuumed or analysed, which counts as 0.</summary>
    public double Reltuples { get; init; }

    /// <summary><c>relpages</c>.</summary>
    public long RelPages { get; init; }

    /// <summary><c>relallfrozen</c> (PostgreSQL 18), or <see langword="null" /> before 18.</summary>
    public long? RelAllFrozen { get; init; }

    /// <summary><c>autovacuum_vacuum_threshold</c>.</summary>
    public double VacuumThreshold { get; init; } = 50;

    /// <summary><c>autovacuum_vacuum_scale_factor</c>.</summary>
    public double VacuumScaleFactor { get; init; } = 0.2;

    /// <summary>
    /// <c>autovacuum_vacuum_max_threshold</c> (PostgreSQL 18; 100,000,000 by default), or <see langword="null" /> before 18 -
    /// a negative value is no cap, as Postgres reads <c>-1</c>.
    /// </summary>
    public double? VacuumMaxThreshold { get; init; }

    /// <summary><c>autovacuum_vacuum_insert_threshold</c>; <c>-1</c> switches the insert trigger off.</summary>
    public double InsertThreshold { get; init; } = 1000;

    /// <summary><c>autovacuum_vacuum_insert_scale_factor</c>.</summary>
    public double InsertScaleFactor { get; init; } = 0.2;

    /// <summary><c>autovacuum_analyze_threshold</c>.</summary>
    public double AnalyzeThreshold { get; init; } = 50;

    /// <summary><c>autovacuum_analyze_scale_factor</c>.</summary>
    public double AnalyzeScaleFactor { get; init; } = 0.1;
}

/// <summary>Autovacuum's three triggers for one table: what each counter must pass, strictly, for it to act.</summary>
/// <param name="Vacuum">Against <c>n_dead_tup</c>.</param>
/// <param name="Insert">Against <c>n_ins_since_vacuum</c>, or <see langword="null" /> when the insert trigger is off.</param>
/// <param name="Analyze">Against <c>n_mod_since_analyze</c>.</param>
/// <param name="UnfrozenShare">The share of pages not all-frozen that scaled the insert trigger (1 before 18).</param>
internal sealed record AutovacuumTriggers(double Vacuum, double? Insert, double Analyze, double UnfrozenShare);

/// <summary>
/// What T07 and T08 say about autovacuum for a table, and the remedy that fits its state: the server's
/// switch, the table's own switch, or a scale factor.
/// </summary>
internal static class Autovacuum
{
    /// <summary>
    /// The scale factors a remedy suggests, largest first: the next one below the scale factor in force is what it offers.
    /// </summary>
    internal static readonly IReadOnlyList<double> ScaleFactorLadder = [0.2, 0.1, 0.05, 0.02, 0.01, 0.005, 0.002, 0.001];

    /// <summary>
    /// Autovacuum's three triggers as <c>relation_needs_vacanalyze</c> computes them (B0 §7a, proved live on 17 and 18 in
    /// §7b) - the same arithmetic <c>HealthTableQueries.TablesSql</c> does in SQL, here so that every threshold can be
    /// unit-tested against stated numbers.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Dead rows: <c>threshold + scale_factor × reltuples</c>, capped on PostgreSQL 18 by
    /// <c>autovacuum_vacuum_max_threshold</c> unless it is negative.</item>
    /// <item>Inserts (13+): <c>threshold + scale_factor × reltuples</c>, and on 18 also times the share of pages that are
    /// not all-frozen - <c>1 - relallfrozen / relpages</c>, used only when both are above zero and with
    /// <c>relallfrozen</c> clamped to <c>relpages</c>. A negative threshold switches it off.</item>
    /// <item>Analyze: <c>threshold + scale_factor × reltuples</c>.</item>
    /// </list>
    /// A <c>reltuples</c> below zero - never vacuumed or analysed, on 14 and later - counts as 0. Autovacuum acts when a
    /// counter is strictly greater than its trigger (<see cref="AutovacuumUnit.Due" />).
    /// </remarks>
    public static AutovacuumTriggers Triggers(AutovacuumInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        double tuples = Math.Max(inputs.Reltuples, 0);

        double vacuum = inputs.VacuumThreshold + (inputs.VacuumScaleFactor * tuples);
        if (inputs.VacuumMaxThreshold is { } cap && cap >= 0 && vacuum > cap)
        {
            vacuum = cap;
        }

        double unfrozen = inputs.RelPages > 0 && inputs.RelAllFrozen is > 0 and { } frozen
            ? 1 - ((double) Math.Min(frozen, inputs.RelPages) / inputs.RelPages)
            : 1;

        double? insert = inputs.InsertThreshold >= 0
            ? inputs.InsertThreshold + (inputs.InsertScaleFactor * tuples * unfrozen)
            : null;

        return new AutovacuumTriggers(vacuum, insert, inputs.AnalyzeThreshold + (inputs.AnalyzeScaleFactor * tuples), unfrozen);
    }

    /// <summary>
    /// A trigger as a person reads it: whole rows, rounded half to even as Postgres' <c>round(float8)</c> rounds them.
    /// </summary>
    public static long Rounded(double trigger) => (long) Math.Round(trigger, MidpointRounding.ToEven);

    /// <summary>
    /// The scale factor a remedy suggests in place of <paramref name="current" />: the next rung of
    /// <see cref="ScaleFactorLadder" /> below it, or <see langword="null" /> when it is already at or below the lowest - then
    /// the scale factor is not what keeps autovacuum from the table.
    /// </summary>
    public static double? LowerScaleFactor(double current)
    {
        foreach (double rung in ScaleFactorLadder)
        {
            if (rung < current - 1e-9)
            {
                return rung;
            }
        }

        return null;
    }

    /// <summary>
    /// A trigger in words, with where each part comes from: "1,050 (autovacuum_vacuum_threshold 50 + autovacuum_vacuum_scale_factor
    /// 0.01 × ~100,000 rows; the scale factor is the table's own storage parameter)".
    /// </summary>
    /// <param name="unit">The decision.</param>
    /// <param name="prefix">The parameters' prefix: <c>autovacuum_vacuum</c>, <c>autovacuum_vacuum_insert</c> or <c>autovacuum_analyze</c>.</param>
    /// <param name="storageOptions">The table's own storage parameters, as <c>name=value</c>.</param>
    /// <param name="partitioned">Whether the decision is one partition's, whose storage parameters are its own.</param>
    /// <param name="cap">The dead-row trigger's cap, when it is the one in force.</param>
    /// <param name="unfrozen">The insert trigger's unfrozen share, when it is below 1.</param>
    public static string Explain(AutovacuumUnit unit, string prefix, IReadOnlyList<string> storageOptions, bool partitioned, double? cap = null, double? unfrozen = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(storageOptions);

        IReadOnlyDictionary<string, string> own = StorageParameters.Parse(storageOptions);

        string Source(string parameter) => partitioned
            ? "the partition's"
            : own.ContainsKey(parameter) ? "the table's own storage parameter" : "the server's setting";

        string scaled = TableText.Count(Rounded(unit.Trigger))
            + " (" + prefix + "_threshold " + Number(unit.Threshold) + ", " + Source(prefix + "_threshold")
            + ", + " + prefix + "_scale_factor " + Number(unit.ScaleFactor) + ", " + Source(prefix + "_scale_factor")
            + ", × " + TableText.Rows((long) unit.Tuples) + " rows (reltuples)"
            + (unfrozen is { } share && share < 1 ? " × " + TableText.Percent(share) + " of its pages not all-frozen (PostgreSQL 18)" : string.Empty);

        return cap is { } capped && unit.Trigger >= capped - 1e-9
            ? scaled + ", capped at autovacuum_vacuum_max_threshold " + TableText.Count(capped) + ")"
            : scaled + ")";
    }

    /// <summary>A setting's value as Postgres prints it: <c>0.2</c>, <c>50</c>.</summary>
    internal static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

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
    /// <param name="table">The table.</param>
    /// <param name="parameter">The scale factor's storage parameter.</param>
    /// <param name="value">Its suggested value.</param>
    /// <param name="text">The studio's words (<see cref="HealthEvaluation.Text" />): where a Marten table's partitions are compared.</param>
    public static string State(TableFacts table, string parameter, string value, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(text);

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

        // Where a person sees which parameters differ: the catalog for a relational table, which no Schema screen describes,
        // and for a Marten table the studio's own words - MartenStudio's Schema screen, or, in a studio over plain data
        // sources, Marten Studio's over the store whose StoreOptions declare the table.
        string partly = table.Row.DifferingStorageOptions.Contains("autovacuum_enabled", StringComparer.OrdinalIgnoreCase)
            ? " Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off for some "
              + "of them ("
              + (table.Table.Kind == StudioTableKind.Relational
                  ? "each partition's pg_class.reloptions says which"
                  : text.MartenTablePartitionParameters)
              + ")."
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

/// <summary>What keeps autovacuum from a table already past its trigger, as far as the sweep can tell (W5-V1 review).</summary>
internal enum AutovacuumHindrance
{
    /// <summary>Nothing the sweep can see.</summary>
    None,

    /// <summary>
    /// A session or a prepared transaction holds or waits for a lock autovacuum's <c>SHARE UPDATE EXCLUSIVE</c> gives way to:
    /// autovacuum skips a table it cannot lock at once.
    /// </summary>
    Locked,

    /// <summary>Every autovacuum worker on the server is busy (T16) - or, for a role that cannot tell them apart, as many maintenance processes.</summary>
    WorkersBusy,

    /// <summary>Cost-based throttling - the table's own or the server's - makes each worker sleep more than it works.</summary>
    Throttled,
}

/// <summary>
/// Why a table past twice its own trigger is still not vacuumed or analysed, and the remedy that answers it (W5-V1 review):
/// once a table is behind, a lower scale factor changes nothing - it is already past its trigger - so the remedy is for
/// what keeps autovacuum away, and a scale factor is offered only where it would at least halve the trigger, so that the
/// next pass starts sooner.
/// </summary>
internal static class AutovacuumHindrances
{
    /// <summary>The cost delay, in milliseconds, at and above which autovacuum counts as throttled (PostgreSQL 12's default is 2).</summary>
    public const double ThrottledDelayMilliseconds = 10;

    /// <summary>The cost limit at and below which autovacuum counts as throttled (the default is 200).</summary>
    public const int ThrottledCostLimit = 100;

    /// <summary>The evidence line of a lock autovacuum gives way to.</summary>
    internal const string LockedLabel = "a lock on the table, held or waited for, that autovacuum gives way to";

    /// <summary>
    /// What keeps autovacuum from <paramref name="unit" />, in the order the evidence is surest. Never a held horizon: under
    /// one autovacuum keeps running and dating its <c>last_autovacuum</c>, so a table is not behind for it - T14 says it
    /// (W5-V1 review).
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="unit">The decision it is behind on.</param>
    /// <param name="workers">The sweep's workers read, or <see langword="null" /> where the check did not read it.</param>
    /// <param name="history">The database's samples: T16's saturated run.</param>
    public static AutovacuumHindrance Of(TableFacts table, AutovacuumUnit unit, MaintenanceWorkersRow? workers, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(history);

        if (unit.Locked)
        {
            return AutovacuumHindrance.Locked;
        }

        if (WorkersBusy(workers, history))
        {
            return AutovacuumHindrance.WorkersBusy;
        }

        return Throttle(table, workers) is not null ? AutovacuumHindrance.Throttled : AutovacuumHindrance.None;
    }

    /// <summary>Whether every worker is busy now, or was in T16's latest sample.</summary>
    public static bool WorkersBusy(MaintenanceWorkersRow? workers, HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return (workers is { Autovacuum: true } read && new AutovacuumWorkersFacts(DateTimeOffset.MinValue, read, [], PostgresPlatform.SelfHosted).Saturated)
            || AutovacuumWorkersCheck.SaturatedRun(history) > 0;
    }

    /// <summary>
    /// The cost setting that throttles the table's workers - its own storage parameter over the server's setting - with its
    /// value and Postgres' default, or <see langword="null" /> when neither is past the line.
    /// </summary>
    public static (string Parameter, string Current, string Suggested, bool TableOwn)? Throttle(TableFacts table, MaintenanceWorkersRow? workers)
    {
        ArgumentNullException.ThrowIfNull(table);

        IReadOnlyDictionary<string, string> own = StorageParameters.Parse(table.Row.StorageOptions);
        double? ownDelay = own.TryGetValue("autovacuum_vacuum_cost_delay", out string? delayText)
            && double.TryParse(delayText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDelay) && parsedDelay >= 0 ? parsedDelay : null;
        int? ownLimit = own.TryGetValue("autovacuum_vacuum_cost_limit", out string? limitText)
            && int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLimit) && parsedLimit > 0 ? parsedLimit : null;

        double delay = ownDelay ?? workers?.CostDelayMilliseconds ?? 2;
        int limit = ownLimit ?? workers?.CostLimit ?? 200;

        if (delay >= ThrottledDelayMilliseconds)
        {
            return ("autovacuum_vacuum_cost_delay", Autovacuum.Number(delay), "2", ownDelay is not null);
        }

        return limit <= ThrottledCostLimit
            ? ("autovacuum_vacuum_cost_limit", limit.ToString(CultureInfo.InvariantCulture), "200", ownLimit is not null)
            : null;
    }

    /// <summary>The evidence line: what keeps autovacuum away.</summary>
    public static string Label(AutovacuumHindrance hindrance, TableFacts table, MaintenanceWorkersRow? workers) => hindrance switch
    {
        AutovacuumHindrance.Locked => LockedLabel,
        AutovacuumHindrance.WorkersBusy when workers is { CanReadAllStats: true } => "every autovacuum worker is busy (T16)",
        AutovacuumHindrance.WorkersBusy => "at least as many maintenance processes as workers, manual runs included (T16)",
        AutovacuumHindrance.Throttled when Throttle(table, workers) is { } throttle =>
            "cost-based throttling: " + throttle.Parameter + " = " + throttle.Current + (throttle.TableOwn ? ", the table's own storage parameter" : ", the server's setting"),
        _ => "nothing the sweep can see",
    };

    /// <summary>The sentence the finding's "why" says about it.</summary>
    public static string Sentence(AutovacuumHindrance hindrance, TableFacts table, MaintenanceWorkersRow? workers) => hindrance switch
    {
        AutovacuumHindrance.Locked =>
            " A session - or a prepared transaction - holds or waits for a lock on it that autovacuum gives way to: autovacuum takes its "
            + "SHARE UPDATE EXCLUSIVE lock only if it can have it at once, so it passes the table by while a conflicting lock is held, "
            + "and while a stronger request waits in the queue - a long migration, a LOCK TABLE, a CREATE INDEX without CONCURRENTLY, an "
            + "orphaned prepared transaction. Database › Sessions shows the sessions, and C07 a prepared transaction.",
        AutovacuumHindrance.WorkersBusy when workers is { CanReadAllStats: true } =>
            " Every autovacuum worker on the server is busy (T16), so a table past its trigger waits for one to come free, however far "
            + "past it it goes.",
        AutovacuumHindrance.WorkersBusy =>
            " At least as many maintenance processes as autovacuum has workers are running on the server, manual runs included (T16) - "
            + "this role cannot tell autovacuum's workers from a manual VACUUM - so a table past its trigger may be waiting for a "
            + "worker to come free.",
        AutovacuumHindrance.Throttled when Throttle(table, workers) is { } throttle =>
            " Autovacuum's cost-based throttling holds it back: " + throttle.Parameter + " = " + throttle.Current
            + (throttle.TableOwn ? ", the table's own storage parameter," : ", the server's setting,")
            + " makes each worker sleep more than it works on a table this size.",
        _ =>
            " Nothing the sweep can see explains it - no lock on the table, no shortage of workers (T16) and no throttling. "
            + "Autovacuum's own log (log_autovacuum_min_duration) says what it did with the table, and when.",
    };

    /// <summary>
    /// The remedy for <paramref name="hindrance" />: more workers - only where the count is of autovacuum's workers, which needs
    /// <c>pg_read_all_stats</c> - or the cost setting back to Postgres' default; none for a lock, whose holder is somebody to
    /// talk to rather than a setting to change.
    /// </summary>
    public static HealthRemedy? Remedy(
        AutovacuumHindrance hindrance,
        TableFacts table,
        MaintenanceWorkersRow? workers,
        PostgresPlatform platform,
        HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(evaluation);

        switch (hindrance)
        {
            case AutovacuumHindrance.WorkersBusy when workers is { CanReadAllStats: true } read:
                string more = (read.MaxWorkers * 2).ToString(CultureInfo.InvariantCulture);
                return ServerSettingRemedy.Build(
                    platform,
                    "autovacuum_max_workers",
                    more,
                    evaluation.Text,
                    read.MaxWorkersContext,
                    ":",
                    where => "Twice the workers, so a table past its trigger is not left waiting for one; raise autovacuum_vacuum_cost_limit with "
                        + "it, which the workers share. Set it in " + where + ".");

            case AutovacuumHindrance.Throttled when Throttle(table, workers) is { } throttle:
                return throttle.TableOwn
                    ? Autovacuum.Remedy(evaluation.TableRemedies, table, throttle.Parameter, throttle.Suggested,
                        "Postgres' own default, so the table's workers are not throttled harder than every other table's.")
                    : ServerSettingRemedy.Build(
                        platform,
                        throttle.Parameter,
                        throttle.Suggested,
                        evaluation.Text,
                        "sighup",
                        ":",
                        where => "Postgres' own default, which a reload applies. Set it in " + where + ".");

            default:
                return null;
        }
    }

    /// <summary>
    /// The next scale factor down, offered only when the trigger it gives is at most half the one in force - the only thing a
    /// lower scale factor can do for a table already past its trigger is make the next pass start sooner - or
    /// <see langword="null" />.
    /// </summary>
    /// <param name="unit">The decision.</param>
    /// <param name="share">The share of the table the scale part counts: 1, or 18's unfrozen share for the insert trigger.</param>
    /// <param name="cap">PostgreSQL 18's cap on the dead-row trigger, or <see langword="null" />.</param>
    public static (double ScaleFactor, double Trigger)? HalvingScaleFactor(AutovacuumUnit unit, double share, double? cap)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (Autovacuum.LowerScaleFactor(unit.ScaleFactor) is not { } lower)
        {
            return null;
        }

        double trigger = unit.Threshold + (lower * unit.Tuples * share);

        if (cap is { } limit and >= 0)
        {
            trigger = Math.Min(trigger, limit);
        }

        return trigger <= unit.Trigger / 2 ? (lower, trigger) : null;
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
