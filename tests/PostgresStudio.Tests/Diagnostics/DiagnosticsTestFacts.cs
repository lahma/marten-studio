using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>Hand-built facts for the diagnostics API's pure tests: no database, no container.</summary>
internal static class DiagnosticsTestFacts
{
    public static readonly DateTimeOffset At = new(2026, 11, 2, 6, 0, 1, TimeSpan.Zero);

    /// <summary>A check's metadata, as the registry would describe it.</summary>
    public static HealthCheckInfo Info(
        string code,
        HealthArea area,
        HealthGate gate = HealthGate.Database,
        HealthCostTier tier = HealthCostTier.Catalog,
        string title = "A check",
        params string[] variants) =>
        new(code, area, tier, gate, title, null, typeof(DiagnosticsTestFacts)) { Variants = variants };

    /// <summary>A finding, stamped as the sweep stamps it.</summary>
    public static HealthFinding Finding(HealthCheckInfo info, string title, string? subject = null, string? variant = null) => new()
    {
        Code = info.Code,
        Severity = HealthSeverity.Warning,
        Title = title,
        WhyItMatters = "Why " + title,
        Subject = subject,
        Variant = variant,
        Area = info.Area,
        Gate = info.Gate,
        CheckTitle = info.Title,
        MeasuredAt = At,
    };

    /// <summary>One check's result.</summary>
    public static HealthCheckResult Result(
        HealthCheckInfo info,
        HealthCheckOutcome outcome = HealthCheckOutcome.Checked,
        HealthCheckProblem? problem = null,
        string? note = null,
        params HealthFinding[] findings) =>
        new(info, outcome, findings, note, problem, [new HealthStatement("select 1", [])], TimeSpan.FromMilliseconds(3));

    /// <summary>A regular sweep of these results.</summary>
    public static HealthSweep Sweep(params HealthCheckResult[] results) =>
        new("tenants", "acme", HealthSweepKind.Regular, At, TimeSpan.FromMilliseconds(40), results);

    /// <summary>A gate over the host's screen with no classifier and no document-type filter.</summary>
    public static HostGate Gate(bool includeActivity) => new(includeActivity, HostGate.ScreenFor(null, hidesDocumentTypes: false));

    /// <summary>A statement fact with window counts.</summary>
    public static StatementFact Statement(long queryId, string? text, string? withheld = null, double execMs = 120, double share = 0.6) => new()
    {
        QueryId = queryId,
        Text = withheld is null ? text : null,
        TextWithheld = withheld,
        TextRead = text is not null,
        SinceBegan = new StatementCounts(10, execMs * 2, 40, 900, 30, 5, 0, null),
        Window = new StatementCounts(5, execMs, 20, 450, 15, 2, 0, null),
        Share = share,
    };

    /// <summary>The statements facts of one database.</summary>
    public static StatementFacts StatementsOf(params StatementFact[] statements) => new()
    {
        StoreKey = "tenants",
        DatabaseId = "acme",
        MeasuredAt = At,
        State = StatementsState.Read,
        Watched = true,
        From = At.AddMinutes(-10),
        To = At,
        StatsReset = At.AddDays(-3),
        TotalExecMs = 200,
        Statements = statements,
    };

    /// <summary>The sessions snapshot of one database, with its connections by application.</summary>
    public static SessionSnapshot SessionsOf(params SessionApplicationCounts[] applications) =>
        new(At, SessionVisibility.Everything, 3, [], 0, [], 0, [], [])
        {
            Connections = new SessionConnections(
                true,
                100,
                3,
                12,
                new SessionApplicationCounts(null, applications.Sum(static x => x.Total), 1, 1, 1, 1, 0, TimeSpan.FromMinutes(2)),
                applications,
                applications.Length),
        };

    /// <summary>The storage facts of one database: one owned table and one in a gated schema.</summary>
    public static StorageFacts StorageOf() => new()
    {
        StoreKey = "tenants",
        DatabaseId = "acme",
        MeasuredAt = At,
        DatabaseBytes = 51_234_567_890,
        EstimatedDatabaseBytes = 50_000_000_000,
        Window = new StatisticsWindow { MeasuredAt = At, Since = At.AddDays(-30), Basis = StatisticsBasis.DatabaseReset },
        GrowthBytesPerDay = 812_345_678.4,
        TableCount = 2,
        LargestTables =
        [
            new StorageTableFacts(
                new StudioTable("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned),
                new StorageTableRow
                {
                    Schema = "public",
                    Table = "orders",
                    HeapBytes = 20_000_000_000,
                    IndexBytes = 5_000_000_000,
                    ToastBytes = 1_000_000_000,
                    EstimatedRows = 300_000_000,
                    Inserts = 1_000_000,
                    Deletes = 0,
                    Updates = 400,
                    HotUpdates = 100,
                }),
            new StorageTableFacts(
                new StudioTable("quartz", "qrtz_triggers", StudioTableKind.Relational, StudioTableOwnership.Library)
                {
                    Library = "Quartz.NET",
                    GatedSchemas = ["quartz"],
                },
                new StorageTableRow { Schema = "quartz", Table = "qrtz_triggers", HeapBytes = 8192, EstimatedRows = 3 }),
        ],
    };
}
