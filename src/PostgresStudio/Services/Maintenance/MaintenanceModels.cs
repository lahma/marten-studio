using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Maintenance;

/// <summary>Why a visitor is shown no Maintenance page.</summary>
internal enum MaintenanceRefusal
{
    /// <summary>Shown.</summary>
    None,

    /// <summary>The store policy refused the database as a whole (<c>TenantId = null</c>): maintenance belongs to no tenant.</summary>
    StorePolicy,

    /// <summary>The source, the database or its sweep could not be read.</summary>
    Unavailable,
}

/// <summary>One judged table as the Maintenance page draws it: its three decisions against their triggers, and its badges.</summary>
/// <remarks>Init properties, so a component test states only what its case is about.</remarks>
internal sealed record MaintenanceTableView
{
    /// <summary>The table's key, <c>schema.table</c>.</summary>
    public required string Key { get; init; }

    /// <summary>The table's schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table's name.</summary>
    public required string Table { get; init; }

    /// <summary>What it is, in the studio's words: "the Order collection's table".</summary>
    public required string Description { get; init; }

    /// <summary>How many leaf partitions it has; zero for an ordinary table.</summary>
    public int Partitions { get; init; }

    /// <summary>The dead-row decision: <c>n_dead_tup</c> against its trigger - for a partitioned table, the partition furthest behind.</summary>
    public AutovacuumUnit? Vacuum { get; init; }

    /// <summary>The insert decision, or <see langword="null" /> when the insert trigger is off.</summary>
    public AutovacuumUnit? Insert { get; init; }

    /// <summary>The analyze decision.</summary>
    public AutovacuumUnit? Analyze { get; init; }

    /// <summary>The dead-row trigger's cap on PostgreSQL 18, or <see langword="null" />.</summary>
    public double? VacuumMaxThreshold { get; init; }

    /// <summary>The share of pages not all-frozen that scales the insert trigger on PostgreSQL 18; 1 before.</summary>
    public double UnfrozenShare { get; init; } = 1;

    /// <summary><c>last_vacuum</c>.</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary><c>last_autovacuum</c>.</summary>
    public DateTimeOffset? LastAutovacuum { get; init; }

    /// <summary><c>last_analyze</c>.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary><c>last_autoanalyze</c>.</summary>
    public DateTimeOffset? LastAutoanalyze { get; init; }

    /// <summary>The table's own <c>autovacuum_*</c> and <c>toast.*</c> storage parameters: what overrides the server's settings.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>Whether a counter is past its trigger: autovacuum would act at its next look.</summary>
    public bool Due { get; init; }

    /// <summary>Whether T07 or T08 would call it behind (before persistence): twice past a trigger by ten thousand rows, not in an hour.</summary>
    public bool Behind { get; init; }

    /// <summary>Why autovacuum leaves it alone, in words, or <see langword="null" /> when it does not.</summary>
    public string? Off { get; init; }

    /// <summary><c>total_vacuum_time</c> plus <c>total_autovacuum_time</c> in milliseconds (PostgreSQL 18), or <see langword="null" />.</summary>
    public double? TotalVacuumMilliseconds { get; init; }

    /// <summary><c>total_analyze_time</c> plus <c>total_autoanalyze_time</c> (PostgreSQL 18), or <see langword="null" />.</summary>
    public double? TotalAnalyzeMilliseconds { get; init; }

    /// <summary><c>vacuum_count</c> plus <c>autovacuum_count</c>, or <see langword="null" />.</summary>
    public long? Vacuums { get; init; }

    /// <summary><c>analyze_count</c> plus <c>autoanalyze_count</c>, or <see langword="null" />.</summary>
    public long? Analyzes { get; init; }

    /// <summary>Whether its bloat can be measured: an ordinary table (a partitioned one is measured partition by partition).</summary>
    public bool Measurable { get; init; }

    /// <summary>How far past its triggers it is, for the order: the largest counter-to-trigger ratio of its decisions.</summary>
    public double Pressure { get; init; }
}

/// <summary>One maintenance command running now, as the page draws it.</summary>
/// <param name="Kind"><c>vacuum</c>, <c>analyze</c>, <c>create index</c> or <c>cluster</c>.</param>
/// <param name="Table">What it works on, in the page's words - never a name the visitor may not see.</param>
/// <param name="Phase">Its phase.</param>
/// <param name="BlocksDone">The blocks its phase has got through.</param>
/// <param name="BlocksTotal">The blocks its phase has to get through.</param>
/// <param name="IndexesProcessed">Indexes vacuumed (PostgreSQL 17+).</param>
/// <param name="IndexesTotal">Indexes to vacuum (PostgreSQL 17+).</param>
/// <param name="Who">"autovacuum", "autovacuum, to prevent wraparound", "manual", or <see langword="null" /> when not known.</param>
/// <param name="Running">How long its statement has run.</param>
/// <param name="Eta">How long until its phase is through, at the rate between the last two samples - or <see langword="null" />.</param>
internal sealed record MaintenanceRunningRow(
    string Kind,
    string Table,
    string? Phase,
    long? BlocksDone,
    long? BlocksTotal,
    long? IndexesProcessed,
    long? IndexesTotal,
    string? Who,
    TimeSpan? Running,
    TimeSpan? Eta);

/// <summary>What is running now.</summary>
/// <param name="Read">Whether the progress views were read this sweep.</param>
/// <param name="Rows">Every command the role may see the table of.</param>
/// <param name="Unseen">How many more run in this database that the role cannot see the table of.</param>
/// <param name="NotChecked">The "not checked" sentence for them, naming the grant, or <see langword="null" />.</param>
internal sealed record MaintenanceRunningView(bool Read, IReadOnlyList<MaintenanceRunningRow> Rows, int Unseen, string? NotChecked)
{
    /// <summary>Nothing read.</summary>
    public static MaintenanceRunningView NotRead { get; } = new(false, [], 0, null);

    /// <summary>
    /// How many run on a table the role can see but this page does not name to this visitor - withheld, hidden, or not one
    /// it judges: counted, never listed, so no block count or ETA tells how big the table is.
    /// </summary>
    public int Unnamed { get; init; }
}

/// <summary>The server's autovacuum workers.</summary>
/// <param name="MaxWorkers"><c>autovacuum_max_workers</c>.</param>
/// <param name="WorkerSlots"><c>autovacuum_worker_slots</c> (PostgreSQL 18), or <see langword="null" />.</param>
/// <param name="Busy">How many are busy, in words: "2 of 3 autovacuum workers are busy", "at least 1 maintenance process running...".</param>
/// <param name="NaptimeSeconds"><c>autovacuum_naptime</c>.</param>
/// <param name="Autovacuum">The server's <c>autovacuum</c>.</param>
/// <param name="Saturated">Whether every worker is busy in this read.</param>
internal sealed record MaintenanceWorkersView(int MaxWorkers, int? WorkerSlots, string Busy, int NaptimeSeconds, bool Autovacuum, bool Saturated);

/// <summary>One counter's way to wraparound: the age, the two lines, and the time to each at the history's rate.</summary>
/// <param name="Noun">"transaction ids" or "multixact ids".</param>
/// <param name="Age">The oldest age: the database's, the server's or a table's.</param>
/// <param name="ForcedLine">Where autovacuum forces a freeze.</param>
/// <param name="RefusalLine">Where Postgres stops handing out ids.</param>
/// <param name="ToForced">How long to the forced line, or <see langword="null" /> when no rate supports a forecast.</param>
/// <param name="ToRefusal">How long to the refusal line, likewise.</param>
/// <param name="Rate">The rate, in words, or <see langword="null" />.</param>
internal sealed record MaintenanceForecast(string Noun, long Age, long ForcedLine, long RefusalLine, TimeSpan? ToForced, TimeSpan? ToRefusal, string? Rate);

/// <summary>One table's freeze age, for the forecast's list.</summary>
/// <param name="Table">The table, in the page's words.</param>
/// <param name="XidAge">Its oldest <c>age(relfrozenxid)</c>, its TOAST table's and partitions' included.</param>
internal sealed record MaintenanceFreezeTable(string Table, long XidAge);

/// <summary>The freeze forecast: T12's and T13's facts, with no read of its own.</summary>
/// <param name="TransactionIds">Transaction ids.</param>
/// <param name="Multixacts">Multixact ids.</param>
/// <param name="OldestTables">The judged tables furthest into the range, oldest first.</param>
internal sealed record MaintenanceFreezeView(MaintenanceForecast TransactionIds, MaintenanceForecast Multixacts, IReadOnlyList<MaintenanceFreezeTable> OldestTables);

/// <summary>Who holds the horizon: T14's finding as this visitor may see it, or T14's note.</summary>
/// <param name="Finding">T14's finding, its evidence gated as the Health page gates it - or <see langword="null" />.</param>
/// <param name="Note">What T14's row says when it found nothing, or why the holder is not shown.</param>
internal sealed record MaintenanceHorizonView(HealthReportFinding? Finding, string? Note);

/// <summary>What one visitor is shown of a database's maintenance.</summary>
internal sealed record MaintenanceView
{
    /// <summary>Why nothing is shown, if so.</summary>
    public MaintenanceRefusal Refusal { get; init; }

    /// <summary>The refusal in words.</summary>
    public string? Reason { get; init; }

    /// <summary>When the sweep read the facts.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>Whether the table read is in the sweep.</summary>
    public bool TablesRead { get; init; }

    /// <summary>The judged tables, worst first, at most <c>MaintenanceService.MaxTables</c>.</summary>
    public IReadOnlyList<MaintenanceTableView> Tables { get; init; } = [];

    /// <summary>How many tables the list leaves out past its cap - all of them within their triggers or less behind.</summary>
    public int TablesCut { get; init; }

    /// <summary>How many tables are not shown to this visitor: in a schema the database browser withholds (D27).</summary>
    public int TablesWithheld { get; init; }

    /// <summary>What a capped table set left out, in words.</summary>
    public string? Coverage { get; init; }

    /// <summary>What is running now.</summary>
    public MaintenanceRunningView Running { get; init; } = MaintenanceRunningView.NotRead;

    /// <summary>The workers, or <see langword="null" /> when not read.</summary>
    public MaintenanceWorkersView? Workers { get; init; }

    /// <summary>The freeze forecast, or <see langword="null" /> when not read.</summary>
    public MaintenanceFreezeView? Freeze { get; init; }

    /// <summary>The horizon.</summary>
    public MaintenanceHorizonView Horizon { get; init; } = new(null, null);

    /// <summary>The server's version, for the version-gated columns (<c>PostgresFeatures</c>).</summary>
    public int ServerVersion { get; init; }

    /// <summary>A refused view.</summary>
    public static MaintenanceView Refused(MaintenanceRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>What one press of "Measure bloat" came to.</summary>
/// <param name="TableKey">The table, <c>schema.table</c>.</param>
/// <param name="Measure">What it measured, or <see langword="null" /> when it did not.</param>
/// <param name="Refusal">Why it did not, in words, or <see langword="null" />.</param>
/// <param name="At">When.</param>
internal sealed record BloatMeasureResult(string TableKey, BloatMeasureRow? Measure, string? Refusal, DateTimeOffset At)
{
    /// <summary>Whether it measured.</summary>
    public bool Succeeded => Measure is not null;
}
