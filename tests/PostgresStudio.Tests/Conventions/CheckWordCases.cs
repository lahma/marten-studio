using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Replication;

using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Conventions;

/// <summary>One evaluation of one shared health check, in Postgres Studio's words, over facts a plain data source can have.</summary>
/// <param name="Check">The check class.</param>
/// <param name="Name">What the facts are, for the theory's display name.</param>
/// <param name="Facts">The facts its evaluator judges.</param>
/// <param name="Evaluation">The evaluation, always in Postgres Studio's words.</param>
internal sealed record CheckWordCase(Type Check, string Name, object Facts, HealthEvaluation Evaluation)
{
    /// <summary>
    /// Whether the facts are about a table named like Marten's (<c>mt_</c>) - one Postgres Studio does classify, as
    /// <see cref="PostgresHealthTableSource.MartenKindOf" /> does, and whose findings may say so (the theory's allow-list).
    /// </summary>
    public bool AboutMartensTables { get; init; }

    /// <summary>The check's code, read off its attribute.</summary>
    public string Code => HealthCheckRegistry.Describe(Check).Code;

    /// <inheritdoc />
    public override string ToString() => Code + " " + Name;
}

/// <summary>
/// Facts that make every shared check's findings, caveats, quiet notes and remedies appear in Postgres Studio's words
/// (wave 6) - stated outright over a plain data source: relational tables, sessions with no daemon lock, statements of the
/// application's own role, a server's settings and slots. Nothing here is Marten's but the cases marked
/// <see cref="CheckWordCase.AboutMartensTables" />, which hold an <c>mt_</c> table as Postgres Studio's table source does.
/// </summary>
internal static class CheckWordCases
{
    /// <summary>When every evaluation here happens - the moment the server and replication facts are read at.</summary>
    public static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private static readonly StudioTable Orders = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
    private static readonly StudioTable Events = new("public", "events", StudioTableKind.Relational, StudioTableOwnership.Owned);
    private static readonly StudioTable Parent = new("public", "customers", StudioTableKind.Relational, StudioTableOwnership.Owned);

    /// <summary>A table named like a Marten document table, as Postgres Studio's table source classifies it.</summary>
    private static readonly StudioTable MartenDocuments =
        new("public", "mt_doc_order", StudioTableKind.DocumentTable, StudioTableOwnership.Library) { Library = "Marten" };

    /// <summary>Every case, for the theory and for its coverage fact.</summary>
    public static IReadOnlyList<CheckWordCase> All { get; } =
    [
        .. Connections(),
        .. Configuration(),
        .. Replication(),
        .. Server(),
        .. Tables(),
        .. Statements(),
    ];

    // ---------------------------------------------------------------------------------------------------------------
    // The evaluation and its builders.

    /// <summary>An evaluation in Postgres Studio's words, with its table remedies, as its sweep hands one over.</summary>
    private static HealthEvaluation Words(HealthHistoryView? history = null, StatisticsWindow? statistics = null) =>
        HealthEvaluation.At(At, history: history, text: PostgresStudioText.Instance) with
        {
            TableRemedies = PostgresHealthTableRemedies.Instance,
            Statistics = statistics,
        };

    /// <summary>A statistics window of <paramref name="length" /> ending now, which every usage guard over seven days passes.</summary>
    private static StatisticsWindow Window(TimeSpan length, bool looksReset = false) => new()
    {
        MeasuredAt = At,
        Since = At - length,
        Basis = StatisticsBasis.ServerReset,
        Reason = looksReset ? StatisticsResetReason.Crash : StatisticsResetReason.None,
        LooksReset = looksReset,
        Signal = looksReset ? "the server restarted without its statistics" : null,
        SharedView = "pg_stat_checkpointer",
    };

    private static StatisticsWindow Month => Window(TimeSpan.FromDays(30));

    /// <summary>A history of samples, each so long before now, oldest first.</summary>
    private static HealthHistoryView History(params (TimeSpan Ago, (string Series, double Value)[] Values)[] samples) =>
        new([
            .. samples
                .OrderByDescending(static x => x.Ago)
                .Select(static x => new HealthSample(At - x.Ago, x.Values.ToDictionary(static v => v.Series, static v => v.Value, StringComparer.Ordinal))),
        ]);

    /// <summary>A history of a check's own samples of each of <paramref name="facts" />, at each moment.</summary>
    private static HealthHistoryView SampledBy(IHealthCheck check, params (DateTimeOffset At, object Facts)[] facts) =>
        new([
            .. facts.Select(x => new HealthSample(
                x.At,
                check.Sample(x.Facts).ToDictionary(static p => p.Series, static p => p.Value, StringComparer.Ordinal))),
        ]);

    private static CheckWordCase Case<TCheck>(string name, object facts, HealthEvaluation? evaluation = null)
        where TCheck : IHealthCheck =>
        new(typeof(TCheck), name, facts, evaluation ?? Words());

    /// <summary>A catalog row for <paramref name="table" />, analysed and quiet unless <paramref name="shape" /> says otherwise.</summary>
    private static TableFacts Table(StudioTable table, Func<HealthTableRow, HealthTableRow>? shape = null)
    {
        HealthTableRow row = new()
        {
            Schema = table.Schema,
            Table = table.Table,
            EstimatedRows = 10_000,
            HeapBytes = 8 * MiB,
            LiveRows = 10_000,
            DeadRows = 0,
            Inserts = 0,
            Updates = 0,
            Deletes = 0,
            HotUpdates = 0,
            SequentialScans = 0,
            SequentialRowsRead = 0,
            IndexScans = 0,
            LastAutovacuum = At.AddHours(-2),
            LastAutoanalyze = At.AddHours(-2),
        };

        return new TableFacts(table, shape is null ? row : shape(row));
    }

    /// <summary>A valid b-tree index on <paramref name="table" /> over <paramref name="columns" />.</summary>
    private static HealthIndexRow IndexRow(StudioTable table, string name, params string[] columns) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        Name = name,
        AccessMethod = "btree",
        Definition = "CREATE INDEX " + name + " ON " + table.Key + " USING btree (" + string.Join(", ", columns) + ")",
        Columns = columns,
        KeyColumnCount = columns.Length,
        OperatorClasses = [.. columns.Select(static _ => 3126u)],
        Collations = [.. columns.Select(static _ => 100u)],
        Options = [.. columns.Select(static _ => (short) 0)],
        KeyAttributes = [.. columns.Select(static (_, i) => (short) (i + 2))],
        KeyWidths = [.. columns.Select(static _ => (short) 4)],
        KeyTypes = [.. columns.Select(static _ => "integer")],
        KeyAverageWidths = [.. columns.Select(static _ => -1)],
        Bytes = 8 * MiB,
        Scans = 10,
        ReferencedColumns = columns,
    };

    /// <summary>The origin Postgres Studio's table source gives an index: a primary key, a Marten table's, or the host's.</summary>
    private static IndexFacts Index(StudioTable table, HealthIndexRow row) =>
        new(table, row, row.IsPrimaryKey ? IndexOrigin.PrimaryKey : table.Kind == StudioTableKind.Relational ? IndexOrigin.Host : IndexOrigin.StoreManaged);

    /// <summary>One of autovacuum's decisions about a table: a counter against a trigger, last acted on <paramref name="last" />.</summary>
    private static AutovacuumUnit Unit(long counter, double trigger, DateTimeOffset? last, double scale = 0.2, double tuples = 10_000) => new()
    {
        Counter = counter,
        Trigger = trigger,
        Last = last,
        Threshold = 50,
        ScaleFactor = scale,
        Tuples = tuples,
    };

    /// <summary>A condition a check samples, held from <paramref name="held" /> ago until now.</summary>
    private static HealthHistoryView Held(string sweptSeries, string conditionSeries, TimeSpan held) =>
        History((held, [(sweptSeries, 1), (conditionSeries, 1)]), (TimeSpan.Zero, [(sweptSeries, 1), (conditionSeries, 1)]));

    // ---------------------------------------------------------------------------------------------------------------
    // Connections: C01, C03-C08, C11-C14.

    private static IEnumerable<CheckWordCase> Connections()
    {
        // C01 - the pool, quiet, nearly full and saturated two checks running.
        yield return Case<PoolSaturationCheck>("room in the pool", new PoolFacts(true, 100, 40, 10, 100, 1, At));
        yield return Case<PoolSaturationCheck>("an unpooled data source", new PoolFacts(false, 100, 0, 0, null, 0, At));
        yield return Case<PoolSaturationCheck>("a pool nearly used up", new PoolFacts(true, 10, 9, 1, 10, 1, At));
        yield return Case<PoolSaturationCheck>(
            "a pool saturated on consecutive checks",
            new PoolFacts(true, 10, 10, 0, 10, 2, At),
            Words(History((TimeSpan.FromMinutes(1), [(PoolSaturationCheck.SaturatedSeries, 1)]))));

        // C03 - the server's, the login role's and the database's connection limits.
        yield return Case<ServerConnectionHeadroomCheck>("room on the server", (ServerConnectionHeadroomFacts) new ServerConnectionLimits(10, 5, 100, 3, 0, 170000));
        yield return Case<ServerConnectionHeadroomCheck>("the server nearly full", (ServerConnectionHeadroomFacts) new ServerConnectionLimits(90, 40, 100, 3, 0, 170000));
        yield return Case<ServerConnectionHeadroomCheck>(
            "the server full, with this process's pool",
            new ServerConnectionHeadroomFacts(new ServerConnectionLimits(96, 40, 100, 3, 0, 170000), PostgresPlatform.SelfHosted, null, 50));
        yield return Case<ServerConnectionHeadroomCheck>(
            "the login role at its connection limit",
            new ServerConnectionHeadroomFacts(
                new ServerConnectionLimits(20, 19, 100, 3, 0, 170000) { LoginRole = "app", RoleLimit = 20, RoleSessions = 19 },
                PostgresPlatform.SelfHosted,
                null,
                10));
        yield return Case<ServerConnectionHeadroomCheck>(
            "the database at its connection limit",
            (ServerConnectionHeadroomFacts) (new ServerConnectionLimits(20, 19, 100, 3, 0, 170000) { DatabaseLimit = 20 }));
        yield return Case<ServerConnectionHeadroomCheck>(
            "Aurora at its ceiling",
            new ServerConnectionHeadroomFacts(new ServerConnectionLimits(4_900, 40, 5_000, 3, 0, 160004), new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker"), null, null));

        // C04 - a session idle inside a transaction, and one whose role hides others' sessions.
        IdleSession Idle(TimeSpan idle, int pid = 4242, bool written = true) =>
            new(pid, "app_user", "billing-api", "10.1.2.3", At.AddHours(-1), "idle in transaction", idle, idle + TimeSpan.FromSeconds(5), written, true, 2, false);

        yield return Case<IdleInTransactionCheck>("nothing idle", new IdleInTransactionFacts(At, SessionVisibility.Everything, 1, [Idle(TimeSpan.FromSeconds(30))]));
        yield return Case<IdleInTransactionCheck>("idle a minute", new IdleInTransactionFacts(At, SessionVisibility.Everything, 1, [Idle(TimeSpan.FromMinutes(1))]));
        yield return Case<IdleInTransactionCheck>(
            "idle ten minutes, other sessions hidden",
            new IdleInTransactionFacts(At, new SessionVisibility(false, 3), 2, [Idle(TimeSpan.FromMinutes(10)), Idle(TimeSpan.FromMinutes(2), 4343, written: false)]));
        yield return Case<IdleInTransactionCheck>(
            "idle holding nothing",
            new IdleInTransactionFacts(At, SessionVisibility.Everything, 1, [new IdleSession(
                4242, "app_user", "billing-api", "10.1.2.3", At.AddHours(-1), "idle in transaction", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), false, false, 0, false)]));

        // C05 - a long statement.
        RunningStatement Running(TimeSpan running, string? wait = null) => new(4242, "app_user", "billing-api", "10.1.2.3", At.AddHours(-1), wait, running);

        yield return Case<LongRunningStatementCheck>("nothing long", new LongRunningFacts(At, SessionVisibility.Everything, 0, []));
        yield return Case<LongRunningStatementCheck>(
            "two statements running long, one waiting",
            new LongRunningFacts(At, SessionVisibility.Everything, 2, [Running(TimeSpan.FromMinutes(20)), Running(TimeSpan.FromMinutes(6), "Lock")]));

        // C06 - a lock wait on a relation, and one behind a VACUUM.
        LockTarget relation = new(LockTargetKind.OtherRelation, PostgresSessionLockLabeler.Wording.OtherRelation, "public", "orders");
        LockWaiter Waiter(TimeSpan waited, bool exact = true) => new(4242, "app_user", "billing-api", "10.1.2.3", "AccessExclusiveLock", relation, exact, waited, [5151]);
        LockBlocker Blocker(bool vacuum = false, bool wraparound = false) =>
            new(5151, true, vacuum ? "active" : "idle in transaction", "app_user", "billing-api", "10.1.2.3", TimeSpan.FromMinutes(4), true, false)
            {
                Vacuum = vacuum,
                Autovacuum = wraparound,
                PreventsWraparound = wraparound,
            };

        yield return Case<LockWaitCheck>("nothing waits", new LockWaitFacts(At, SessionVisibility.Everything, 0, [], []));
        yield return Case<LockWaitCheck>("a wait behind an open transaction", new LockWaitFacts(At, SessionVisibility.Everything, 1, [Waiter(TimeSpan.FromMinutes(2))], [Blocker()]));
        yield return Case<LockWaitCheck>(
            "a migration behind an anti-wraparound vacuum, before PostgreSQL 14",
            new LockWaitFacts(At, new SessionVisibility(false, 2), 1, [Waiter(TimeSpan.FromMinutes(12), exact: false)], [Blocker(vacuum: true, wraparound: true)]));
        yield return Case<LockWaitCheck>("a migration behind a manual VACUUM", new LockWaitFacts(At, SessionVisibility.Everything, 1, [Waiter(TimeSpan.FromMinutes(2))], [Blocker(vacuum: true)]));

        // C12 - a chain under a head idle in its transaction, one under a long statement, one under a VACUUM, and a short one.
        LockedRelation orders = new("public", "orders", "r");
        ChainSession Head(string state, bool vacuum = false) => new(
            new LockGraphNode
            {
                ProcessId = 5151,
                User = "app_user",
                Application = "billing-api",
                ClientAddress = "10.1.2.3",
                BackendStart = At.AddHours(-1),
                State = state,
                StateAge = TimeSpan.FromMinutes(4),
                TransactionAge = TimeSpan.FromMinutes(5),
                HasTransactionId = true,
                Vacuum = vacuum,
                HeldModes = ["AccessExclusiveLock"],
            },
            null);
        ChainSession Behind(int pid, TimeSpan waited) => new(
            new LockGraphNode
            {
                ProcessId = pid,
                User = "app_user",
                Application = "billing-api",
                State = "active",
                WaitEventType = "Lock",
                WaitEvent = "relation",
                Wanted = new LockWanted("relation", "AccessShareLock", orders, null, null, true, waited),
                BlockedBy = [5151],
            },
            relation);
        BlockingChainFacts Chain(ChainSession head, TimeSpan waited, bool hidden = false) =>
            new(At, hidden ? new SessionVisibility(false, 2) : SessionVisibility.Everything, [head, Behind(4242, waited), Behind(4343, TimeSpan.FromSeconds(3))], PostgresPlatform.SelfHosted);

        yield return Case<BlockingChainCheck>("nothing waits", new BlockingChainFacts(At, SessionVisibility.Everything, [], PostgresPlatform.SelfHosted));
        yield return Case<BlockingChainCheck>("a short wait behind a long statement", Chain(Head("active"), TimeSpan.FromSeconds(30)));
        yield return Case<BlockingChainCheck>("a wait behind a head idle in its transaction", Chain(Head("idle in transaction"), TimeSpan.FromSeconds(40)));
        yield return Case<BlockingChainCheck>("a chain six minutes old behind a long statement, others hidden", Chain(Head("active"), TimeSpan.FromMinutes(6), hidden: true));
        yield return Case<BlockingChainCheck>("a chain three minutes old behind a VACUUM", Chain(Head("active", vacuum: true), TimeSpan.FromMinutes(3)));

        // C13 and C14 (issue #20) - who holds the whole server's connections: idle ones hoarded near the limit, and ones that set
        // no application name.
        SessionHolderGroup Holder(string database, string application, long idle, string role = "app_user", string? client = "10.1.2.3", long unknown = 0, bool hidden = false) =>
            new(database, application, role, client, hidden, idle + unknown, 0, idle, 0, unknown, TimeSpan.FromMinutes(3), null);
        ConnectionHolderFacts Holders(long used, bool allVisible, params SessionHolderGroup[] groups)
        {
            ServerConnectionLimits limits = new(used, 0, 103, 3, 0, 170000);
            return new ConnectionHolderFacts(At, limits, new SessionServerHolders(allVisible, 103, 3, 0, 170000, used, groups.Length, groups));
        }

        yield return Case<IdleConnectionsHoardedCheck>("room on the server", Holders(40, true, Holder("app", "billing-api", 30)));
        yield return Case<IdleConnectionsHoardedCheck>("nobody holds a tenth idle", Holders(90, true, Holder("app", "billing-api", 8), Holder("app", "reports", 4)));
        yield return Case<IdleConnectionsHoardedCheck>(
            "one application holds a tenth idle across two databases",
            Holders(90, true, Holder("app", "billing-api", 7), Holder("audit", "billing-api", 5, role: "auditor", client: null), Holder("app", "reports", 2)));
        yield return Case<IdleConnectionsHoardedCheck>(
            "two hoarders, other roles' sessions not readable",
            Holders(96, false, Holder("app", "billing-api", 14), Holder("app", string.Empty, 11, client: "10.1.2.4"), Holder("app", "psql", 0, role: "other", client: null, unknown: 6, hidden: true)));

        yield return Case<UnattributedConnectionsCheck>("every connection is named", Holders(10, true, Holder("app", "billing-api", 6), Holder("app", "reports", 4)));
        yield return Case<UnattributedConnectionsCheck>("one connection with no name", Holders(3, true, Holder("app", string.Empty, 1)));
        yield return Case<UnattributedConnectionsCheck>(
            "unnamed connections in two databases, one role unreadable",
            Holders(60, false, Holder("app", string.Empty, 12), Holder("audit", string.Empty, 9, role: "auditor", client: null), Holder("app", string.Empty, 0, role: "other", client: null, unknown: 3, hidden: true)));

        // C07 - a forgotten prepared transaction.
        yield return Case<PreparedTransactionCheck>("none prepared", new PreparedTransactionFacts(At, 10, []));
        yield return Case<PreparedTransactionCheck>(
            "one prepared a minute ago",
            new PreparedTransactionFacts(At, 10, [new PreparedRow("gid-recent", "tx_owner", At.AddMinutes(-1), TimeSpan.FromMinutes(1), "1234")]));
        yield return Case<PreparedTransactionCheck>(
            "two prepared hours ago",
            new PreparedTransactionFacts(At, 10,
            [
                new PreparedRow("gid-orphan", "tx_owner", At.AddHours(-3), TimeSpan.FromHours(3), "1234"),
                new PreparedRow("gid-other", "tx_owner", At.AddHours(-1), TimeSpan.FromHours(1), "1240"),
            ]));

        // C08 - deadlocks, rollbacks and temporary files between two samples, and a single sample watching.
        DatabaseCounterCheck counters = new();
        DatabaseCounterFacts first = new(At.AddMinutes(-5), 10_000, 1_000, 4, 10, 100 * MiB, At.AddDays(-20), 500);
        DatabaseCounterFacts last = new(At, 12_000, 1_400, 6, 30, 900 * MiB, At.AddDays(-20), 560);

        yield return Case<DatabaseCounterCheck>("one sample", last, Words(SampledBy(counters, (last.ReadAt, last))));
        yield return Case<DatabaseCounterCheck>(
            "deadlocks, rollbacks and temporary files in five minutes",
            last,
            Words(SampledBy(counters, (first.ReadAt, first), (last.ReadAt, last))));
        yield return Case<DatabaseCounterCheck>(
            "a quiet five minutes",
            new DatabaseCounterFacts(At, 10_100, 1_001, 4, 10, 100 * MiB, At.AddDays(-20), 501),
            Words(SampledBy(counters, (first.ReadAt, first), (At, new DatabaseCounterFacts(At, 10_100, 1_001, 4, 10, 100 * MiB, At.AddDays(-20), 501)))));

        // C11 - the cache hit ratio, self-hosted and on Aurora.
        StatisticsReading reading = new() { ReadAt = At, BlocksHit = 37_000_000, BlocksRead = 2_000_000 };
        yield return Case<CacheHitCheck>("a low hit ratio", new CacheHitFacts(reading, PostgresPlatform.SelfHosted), Words(statistics: Window(TimeSpan.FromDays(2))));
        yield return Case<CacheHitCheck>(
            "a low hit ratio on Aurora",
            new CacheHitFacts(reading, new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker")),
            Words(statistics: Window(TimeSpan.FromDays(2))));
        yield return Case<CacheHitCheck>(
            "a good hit ratio",
            new CacheHitFacts(reading with { BlocksHit = 98_000_000 }, PostgresPlatform.SelfHosted),
            Words(statistics: Window(TimeSpan.FromDays(2))));
        yield return Case<CacheHitCheck>("a window under a day", new CacheHitFacts(reading, PostgresPlatform.SelfHosted), Words(statistics: Window(TimeSpan.FromHours(3))));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Configuration: K05-K07, in Postgres Studio's options.

    private static StudioLintFacts Options(bool readOnly = false, StudioCapability[]? on = null, bool consoleRole = false, bool everySchema = false) =>
        new(readOnly, on ?? [], consoleRole, everySchema, PostgresStudioText.OptionsTypeName, "AddPostgresStudio", "PostgresStudioCapabilities");

    private static IEnumerable<CheckWordCase> Configuration()
    {
        yield return Case<ConsoleWithoutRoleCheck>("the console without a role", Options(on: [StudioCapability.RunSql]));
        yield return Case<ConsoleWithoutRoleCheck>("the console off", Options());
        yield return Case<ConsoleWithoutRoleCheck>("the console with a role", Options(on: [StudioCapability.RunSql], consoleRole: true));
        yield return Case<ConsoleWithoutRoleCheck>("read-only", Options(readOnly: true, on: [StudioCapability.RunSql]));

        yield return Case<BrowserWithoutRoleCheck>("every schema without a role", Options(on: [StudioCapability.BrowseDatabase], everySchema: true));
        yield return Case<BrowserWithoutRoleCheck>("exact schemas", Options(on: [StudioCapability.BrowseDatabase]));
        yield return Case<BrowserWithoutRoleCheck>("the browser off", Options());
        yield return Case<BrowserWithoutRoleCheck>("read-only", Options(readOnly: true));
        yield return Case<BrowserWithoutRoleCheck>("every schema with a role", Options(on: [StudioCapability.BrowseDatabase], consoleRole: true, everySchema: true));

        yield return Case<AnonymousStudioCheck>("anonymous with the console on", new AnonymousStudioFacts(Options(on: [StudioCapability.RunSql, StudioCapability.BrowseDatabase]), true, "Production"));
        yield return Case<AnonymousStudioCheck>("anonymous in development", new AnonymousStudioFacts(Options(on: [StudioCapability.RunSql]), true, "Development"));
        yield return Case<AnonymousStudioCheck>("anonymous, nothing on", new AnonymousStudioFacts(Options(readOnly: true), true, "Production"));
        yield return Case<AnonymousStudioCheck>("authorized", new AnonymousStudioFacts(Options(on: [StudioCapability.RunSql]), false, "Production"));
        yield return Case<AnonymousStudioCheck>("not known", new AnonymousStudioFacts(Options(), null, null));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Replication: R01-R04.

    private static IEnumerable<CheckWordCase> Replication()
    {
        // R01 - one slot of each kind, and none.
        ReplicationSlot Slot(string name, long? retained, bool active = false, string? walStatus = "reserved", string type = "physical", TimeSpan? inactive = null, bool thisDatabase = true) =>
            new(name, type, type == "logical" ? "pgoutput" : null, type == "logical" ? (thisDatabase ? "orders" : "billing") : null, false, active,
                retained, walStatus, null, type == "logical" ? null : 1_234, type == "logical" ? 5_678 : null, inactive, null, null, thisDatabase);

        ReplicationSlotFacts Slots(long? cap, params ReplicationSlot[] slots) =>
            new(new ReplicationSlotSnapshot(At, 170011, false, cap, 0, slots.Length, slots), new PostgresServerFacts(At, 170011, "17.11", PostgresPlatform.SelfHosted));

        yield return Case<ReplicationSlotCheck>("no slots", Slots(-1));
        yield return Case<ReplicationSlotCheck>("a slot holding little", Slots(-1, Slot("cdc", 40 * MiB), Slot("standby", 2 * MiB, active: true)));
        yield return Case<ReplicationSlotCheck>(
            "near the cap, inactive and lost",
            Slots(
                200 * GiB,
                Slot("near", 170 * GiB, inactive: TimeSpan.FromHours(1)),
                Slot("unreserved", 210 * GiB, walStatus: "unreserved"),
                Slot("idle_a", 2 * GiB, inactive: TimeSpan.FromHours(1), type: "logical"),
                Slot("idle_b", 3 * GiB, inactive: TimeSpan.FromHours(2), type: "logical", thisDatabase: false),
                Slot("gone", null, walStatus: "lost"),
                Slot("fine", 1 * MiB, active: true)));
        yield return Case<ReplicationSlotCheck>("an inactive slot, uncapped", Slots(-1, Slot("debezium", 3 * GiB, inactive: TimeSpan.FromHours(2), type: "logical")));

        // R02 - archiving failing, behind, and off.
        ArchiverFailingFacts Archive(ArchiverFacts archiver) => new(archiver, null, ReplicationTestFacts.Grants(), ReplicationTestFacts.ServerOf());

        yield return Case<ArchiverFailingCheck>("archive_mode off", Archive(ReplicationTestFacts.Archiver(settings: ReplicationTestFacts.Archiving("off"))));
        yield return Case<ArchiverFailingCheck>("archiving keeps up", Archive(ReplicationTestFacts.Archiver()));
        yield return Case<ArchiverFailingCheck>(
            "archiving failing",
            Archive(ReplicationTestFacts.Archiver(lastArchived: null, failed: 17, lastFailed: ReplicationTestFacts.Wal(1), lastFailedAt: At.AddSeconds(-20))));
        yield return Case<ArchiverFailingCheck>(
            "archiving behind and falling further",
            Archive(ReplicationTestFacts.Archiver(lastArchived: ReplicationTestFacts.Wal(100), current: ReplicationTestFacts.Wal(164), lastArchivedAge: TimeSpan.FromSeconds(30))),
            Words(ReplicationTestFacts.History(
                (At.AddMinutes(-10), [(ReplicationHistory.ArchiverBacklog, 50)]),
                (At, [(ReplicationHistory.ArchiverBacklog, 64)]))));

        // R03 - a primary's view, a standby's, a paused replay, an Aurora reader, and none.
        ReplicationRole primary = new(false, null, null, null, null, null, null, At);
        StandbyRow Row(string name, long? behind, TimeSpan? lag) => new(name, "10.0.0.3", "replicator", "streaming", "async", behind, behind, null, null, lag);
        StandbyLagFacts FromPrimary(params StandbyRow[] rows) =>
            new(ReplicationTestFacts.ServerOf(), primary, new StandbySnapshot(rows.Length, rows, At, 0), ReplicationTestFacts.Grants());
        StandbyLagFacts OnStandby(long? behind, TimeSpan? age, string pause, HealthHistoryView history)
        {
            ReplicationRole role = new(true, "0/5000000", "0/4000000", behind, At - age, age, pause, At);
            return new StandbyLagFacts(ReplicationTestFacts.ServerOf(), role, null, ReplicationTestFacts.Grants(), TimeSpan.Zero, StandbyLagCheck.Carry(history, role));
        }

        HealthHistoryView received = ReplicationTestFacts.History(
            (At.AddMinutes(-10), [(StandbyLagCheck.ReceivedSeries, (double) (0x4000000 - 0x100000))]),
            (At.AddMinutes(-6), [(StandbyLagCheck.ReceivedSeries, (double) (0x4000000 + 0x100000))]),
            (At.AddMinutes(-1), [(StandbyLagCheck.ReceivedSeries, (double) (0x4000000 + 0x100000))]));

        yield return Case<StandbyLagCheck>("no standby", FromPrimary());
        yield return Case<StandbyLagCheck>("a standby an hour behind", FromPrimary(Row("reporting", 13_254_232, TimeSpan.FromMinutes(61)), Row("dr", 0, null)));
        yield return Case<StandbyLagCheck>("this standby six minutes behind", OnStandby(13_254_232, TimeSpan.FromMinutes(1), "not paused", received), Words(received));
        yield return Case<StandbyLagCheck>("replay paused", OnStandby(3 * GiB, TimeSpan.FromHours(2), "paused", HealthHistoryView.Empty));

        StandbyLagFacts aurora = new(
            new PostgresServerFacts(At, 160004, "16.4", new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker")),
            primary,
            null,
            new PostgresGrants("app", false, []))
        {
            Aurora = AuroraReplicaStatus.FromJson(At,
            [
                """{"server_id": "db-instance-1", "session_id": "MASTER_SESSION_ID", "replica_lag_in_msec": null}""",
                """{"server_id": "db-instance-2", "session_id": "u-db-instance-2", "replica_lag_in_msec": 400000, "cur_replay_latency_in_usec": 120}""",
            ]),
        };

        yield return Case<StandbyLagCheck>("an Aurora reader behind", aurora);

        // R04 - requested checkpoints.
        CheckpointStatistics Checkpoints(long timed, long requested) =>
            new("pg_stat_checkpointer", CheckpointCounting.CheckpointerWithSkipped, timed, requested, null, null, null, null, null, null, null, null, null, null, null, At.AddDays(-3));
        RequestedCheckpointsFacts Requested(long timed, long requested) =>
            new(ReplicationTestFacts.ServerOf(), Checkpoints(timed, requested), new WalStatistics(1000, 200, 5e9, 0, null, null, null, null, null), ReplicationTestFacts.Archiving());

        yield return Case<RequestedCheckpointsCheck>("one sample", Requested(100, 50));
        yield return Case<RequestedCheckpointsCheck>(
            "requested checkpoints in twenty minutes",
            Requested(104, 62),
            Words(ReplicationTestFacts.History(
                (At.AddMinutes(-20), [(ReplicationHistory.CheckpointsTimed, 100), (ReplicationHistory.CheckpointsRequested, 50)]),
                (At, [(ReplicationHistory.CheckpointsTimed, 104), (ReplicationHistory.CheckpointsRequested, 62)]))));
        yield return Case<RequestedCheckpointsCheck>(
            "timed checkpoints",
            Requested(140, 51),
            Words(ReplicationTestFacts.History(
                (At.AddMinutes(-60), [(ReplicationHistory.CheckpointsTimed, 100), (ReplicationHistory.CheckpointsRequested, 50)]),
                (At, [(ReplicationHistory.CheckpointsTimed, 140), (ReplicationHistory.CheckpointsRequested, 51)]))));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Server: S01-S10.

    private static IEnumerable<CheckWordCase> Server()
    {
        // S01 - a release past its end of life, near it, and current.
        yield return Case<PostgresEndOfLifeCheck>("PostgreSQL 12", new PostgresServerFacts(At, 120022, "12.22", PostgresPlatform.SelfHosted));
        yield return Case<PostgresEndOfLifeCheck>("PostgreSQL 13", new PostgresServerFacts(At, 130022, "13.22", PostgresPlatform.SelfHosted));
        yield return Case<PostgresEndOfLifeCheck>("PostgreSQL 14 on RDS", new PostgresServerFacts(At, 140019, "14.19", new PostgresPlatform(PostgresPlatformKind.AmazonRds, "marker")));
        yield return Case<PostgresEndOfLifeCheck>("PostgreSQL 17", new PostgresServerFacts(At, 170011, "17.11", PostgresPlatform.SelfHosted));

        // S02 - every setting it judges, at its unsafe value and safe, in Production and not, on three platforms.
        ServerSetting[] safe =
        [
            new("track_counts", "on", "default", false),
            new("autovacuum", "on", "default", false),
            new("track_activities", "on", "default", false),
            new("fsync", "on", "default", false),
            new("full_page_writes", "on", "default", false),
            new("zero_damaged_pages", "off", "default", false),
            new("ignore_checksum_failure", "off", "default", false),
            new("synchronous_commit", "on", "default", false),
        ];

        ServerSetting[] dangerous =
        [
            new("track_counts", "off", "configuration file", false) { Context = "superuser" },
            new("autovacuum", "off", "configuration file", false) { Context = "sighup" },
            new("track_activities", "off", "user", false) { Context = "superuser" },
            new("fsync", "off", "configuration file", false) { Context = "sighup" },
            new("full_page_writes", "off", "configuration file", false) { Context = "sighup" },
            new("zero_damaged_pages", "on", "database", false) { Context = "superuser" },
            new("ignore_checksum_failure", "on", "client", false) { Context = "superuser" },
            new("synchronous_commit", "off", "client", false) { Context = "user" },
            new("shared_buffers", "16384", "configuration file", true) { Context = "postmaster" },
        ];

        DangerousSettingsFacts Dangerous(string? environment, PostgresPlatform platform, params ServerSetting[] settings) =>
            new(At, settings, settings.Length, environment, platform);

        yield return Case<DangerousSettingsCheck>("every setting safe", Dangerous("Production", PostgresPlatform.SelfHosted, safe));
        yield return Case<DangerousSettingsCheck>("every setting dangerous, in Production", Dangerous("Production", PostgresPlatform.SelfHosted, dangerous));
        yield return Case<DangerousSettingsCheck>("every setting dangerous, in Development", Dangerous("Development", PostgresPlatform.SelfHosted, dangerous));
        yield return Case<DangerousSettingsCheck>("every setting dangerous, no environment", Dangerous(null, PostgresPlatform.SelfHosted, dangerous));
        yield return Case<DangerousSettingsCheck>("on Aurora", Dangerous("Production", new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker"), dangerous));
        yield return Case<DangerousSettingsCheck>("on Neon", Dangerous("Production", new PostgresPlatform(PostgresPlatformKind.Neon, "marker"), dangerous));
        yield return Case<DangerousSettingsCheck>("on RDS", Dangerous("Production", new PostgresPlatform(PostgresPlatformKind.AmazonRds, "marker"), dangerous));
        yield return Case<DangerousSettingsCheck>(
            "synchronous_commit local, some not read",
            Dangerous("Production", PostgresPlatform.SelfHosted, [.. safe.Where(static x => x.Name is not ("fsync" or "synchronous_commit")), new("synchronous_commit", "local", "user", false)]));

        // S03 - checksum failures here, elsewhere, and checksums off.
        yield return Case<ChecksumFailuresCheck>("no failures", new ChecksumFacts(At, true, 0, null, 0, 0, null, 0, null, At.AddDays(-20)));
        yield return Case<ChecksumFailuresCheck>("failures here and elsewhere", new ChecksumFacts(At, true, 2, At.AddHours(-3), 4, 2, At.AddHours(-1), 1, At.AddHours(-2), At.AddDays(-20)));
        yield return Case<ChecksumFailuresCheck>("checksums off", new ChecksumFacts(At, false, null, null, null, 0, null, null, null, At.AddDays(-20)));

        // S04 - a superuser, a provider's admin role, a role that reaches the server's files, and an ordinary one.
        yield return Case<SuperuserConnectionCheck>(
            "a superuser in Production",
            new SuperuserConnectionFacts(Posture("postgres", superuser: true, memberOf: ["pg_read_server_files"]), "Production", SelfHosted));
        yield return Case<SuperuserConnectionCheck>(
            "RDS's master user",
            new SuperuserConnectionFacts(Posture("master", memberOf: ["rds_superuser"]), "Production", Platform(PostgresPlatformKind.AmazonRds)));
        yield return Case<SuperuserConnectionCheck>(
            "a role that reaches the server's files",
            new SuperuserConnectionFacts(Posture(memberOf: ["pg_execute_server_program"]), "Development", SelfHosted));
        yield return Case<SuperuserConnectionCheck>("an ordinary role", new SuperuserConnectionFacts(Posture("app"), "Production", SelfHosted));

        // S05 - no TLS from another machine.
        yield return Case<UnencryptedConnectionCheck>("no TLS in Production", new UnencryptedConnectionFacts(Posture(ssl: false, client: "172.17.0.1"), "on", "Production"));
        yield return Case<UnencryptedConnectionCheck>("no TLS on a server without it", new UnencryptedConnectionFacts(Posture(ssl: false), "off", "Development"));
        yield return Case<UnencryptedConnectionCheck>("a local socket", new UnencryptedConnectionFacts(Posture(ssl: false, client: null), "off", "Production"));
        yield return Case<UnencryptedConnectionCheck>("TLS", new UnencryptedConnectionFacts(Posture(ssl: true), "on", "Production"));

        // S06 - PUBLIC may create in an own schema, before and after 15.
        yield return Case<PublicSchemaCreateCheck>("PUBLIC create on 14", new PublicSchemaCreateFacts(["public", "billing", "missing"], [new("public", true), new("billing", false)], 140024));
        yield return Case<PublicSchemaCreateCheck>("PUBLIC create on 17", new PublicSchemaCreateFacts(["public"], [new("public", true)], 170011));
        yield return Case<PublicSchemaCreateCheck>("no grant", new PublicSchemaCreateFacts(["public"], [new("public", false)], 170011));
        yield return Case<PublicSchemaCreateCheck>("no schema of its own", new PublicSchemaCreateFacts([], [], 170011));
        yield return Case<PublicSchemaCreateCheck>("no own schema exists yet", new PublicSchemaCreateFacts(["billing"], [], 170011));

        // S07 - an extension the server can update, one it cannot, and gone files.
        ServerExtensionsRead extensions = new(
        [
            new("pg_stat_statements", true, "1.9", "1.10", false),
            new("timescaledb", true, "2.17.0", "2.16.1", false),
            new("pgstattuple", true, null, "1.5", false),
            new("plpgsql", false, "1.0", "1.0", true),
            new("gone", false, "2.0", null, null),
        ], 5);
        yield return Case<OutdatedExtensionCheck>(
            "one updatable, one not, one gone",
            new OutdatedExtensionFacts(extensions, new Dictionary<string, bool>(StringComparer.Ordinal) { ["pg_stat_statements"] = true, ["timescaledb"] = false }));
        yield return Case<OutdatedExtensionCheck>(
            "every extension current",
            new OutdatedExtensionFacts(new ServerExtensionsRead([new("plpgsql", false, "1.0", "1.0", true)], 1), new Dictionary<string, bool>(StringComparer.Ordinal)));

        // S08 - a password that expires in three days, in twenty, has expired, and never does.
        foreach (int days in new[] { -2, 3, 20, 90 })
        {
            yield return Case<PasswordExpiryCheck>("the password expires in " + days + " days", new PasswordExpiryFacts(At, Posture(validUntil: At.AddDays(days))));
        }

        yield return Case<PasswordExpiryCheck>("no expiry", new PasswordExpiryFacts(At, Posture(validUntil: null)));

        // S09 - a planner switch off for the role, in the file, from the client.
        foreach (string source in new[] { "configuration file", "user", "client", "database" })
        {
            yield return Case<PlannerSwitchesCheck>(
                "enable_seqscan off, set by " + source,
                new PlannerSwitchesFacts(Settings(Setting("enable_seqscan", "off", source: source, boot: "on"), Setting("enable_hashjoin", "on", boot: "on")), SelfHosted));
        }

        yield return Case<PlannerSwitchesCheck>("every switch on", new PlannerSwitchesFacts(Settings(Setting("enable_seqscan", "on", boot: "on")), SelfHosted));

        // S10 - read-only sessions, a standby, both, Azure, and a primary.
        yield return Case<ReadOnlyStoreCheck>("read-only from the client, on a standby", new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "client", boot: "off"), true, SelfHosted));
        yield return Case<ReadOnlyStoreCheck>("read-only for the role", new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "user", boot: "off"), false, SelfHosted));
        yield return Case<ReadOnlyStoreCheck>(
            "read-only in the file",
            new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "configuration file", boot: "off", context: "user"), false, SelfHosted));
        yield return Case<ReadOnlyStoreCheck>(
            "read-only on Azure",
            new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "configuration file", boot: "off"), false, Platform(PostgresPlatformKind.Azure)));
        yield return Case<ReadOnlyStoreCheck>("a primary that writes", new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "off"), false, SelfHosted));
        yield return Case<ReadOnlyStoreCheck>("the setting not shown", new ReadOnlyStoreFacts(null, false, SelfHosted));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Tables: T01, T03-T08, T12-T16, T19, T20, T24-T26.

    private static IEnumerable<CheckWordCase> Tables()
    {
        // T01 - a large table read whole, a young window, a small table, and one sample.
        TableFacts large = Table(Orders, static row => row with { LiveRows = 500_000, EstimatedRows = 500_000, HeapBytes = 64 * MiB });
        HealthHistoryView scans = History(
            (TimeSpan.FromMinutes(20), [(SequentialScansCheck.ScansSeries + Orders.Key, 0), (SequentialScansCheck.RowsReadSeries + Orders.Key, 0)]),
            (TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + Orders.Key, 10), (SequentialScansCheck.RowsReadSeries + Orders.Key, 5_000_000)]));
        TableFacts partitioned = Table(Events, static row => row with { Kind = "p", LiveRows = 2_000_000, EstimatedRows = 2_000_000, PartitionLeaves = 4, LargestLeafRows = 500_000 });
        HealthHistoryView partitionScans = History(
            (TimeSpan.FromMinutes(20), [(SequentialScansCheck.ScansSeries + Events.Key, 0), (SequentialScansCheck.RowsReadSeries + Events.Key, 0)]),
            (TimeSpan.Zero, [(SequentialScansCheck.ScansSeries + Events.Key, 20), (SequentialScansCheck.RowsReadSeries + Events.Key, 8_000_000)]));

        yield return Case<SequentialScansCheck>("a large table read whole", new SequentialScanFacts(At, [large], DatabaseStatistics.Unknown), Words(scans, Month));
        yield return Case<SequentialScansCheck>("a large partitioned table read whole", new SequentialScanFacts(At, [partitioned], DatabaseStatistics.Unknown), Words(partitionScans, Month));
        yield return Case<SequentialScansCheck>("six days of statistics", new SequentialScanFacts(At, [large], DatabaseStatistics.Unknown), Words(scans, Window(TimeSpan.FromDays(6))));
        yield return Case<SequentialScansCheck>("counters that look reset", new SequentialScanFacts(At, [large], DatabaseStatistics.Unknown), Words(scans, Window(TimeSpan.FromDays(30), looksReset: true)));
        yield return Case<SequentialScansCheck>("no large table", new SequentialScanFacts(At, [Table(Orders)], DatabaseStatistics.Unknown), Words(statistics: Month));
        yield return Case<SequentialScansCheck>("one sample", new SequentialScanFacts(At, [large], DatabaseStatistics.Unknown), Words(statistics: Month));

        // T03 - an invalid host index, an invalid primary key, invalid indexes on partitions, a build in progress.
        TableFacts events = Table(Events, static row => row with { Kind = "p", InvalidPartitionIndexes = 2, PartitionLeaves = 4 });
        HealthIndexRow invalid = IndexRow(Orders, "orders_by_status", "status") with { IsValid = false };
        HealthIndexRow primaryKey = IndexRow(Orders, "orders_pkey", "id") with { IsValid = false, IsPrimaryKey = true, IsUnique = true };
        HealthIndexRow onPartitioned = IndexRow(Events, "events_pkey", "id") with { IsValid = false, IsPrimaryKey = true, IsUnique = true };
        HealthIndexRow building = IndexRow(Orders, "orders_by_customer", "customer_id") with { IsValid = false, BuildPhase = "building index: scanning table" };
        HealthIndexRow unseen = IndexRow(Orders, "orders_by_region", "region") with { IsValid = false, UnseenIndexBuilds = 2, TableBuildPhase = "waiting for old snapshots" };

        yield return Case<InvalidIndexCheck>(
            "invalid indexes of every kind",
            new InvalidIndexFacts([Index(Orders, invalid), Index(Orders, primaryKey), Index(Events, onPartitioned), Index(Orders, building), Index(Orders, unseen)], [Table(Orders), events]));
        yield return Case<InvalidIndexCheck>("a build in progress", new InvalidIndexFacts([Index(Orders, building)], [Table(Orders)]));
        yield return Case<InvalidIndexCheck>("no index at all", new InvalidIndexFacts([], []));
        yield return Case<InvalidIndexCheck>(
            "a locked table",
            new InvalidIndexFacts([Index(Orders, invalid)], [Table(Orders)]) { LockedTables = new HashSet<string>([Orders.Key], StringComparer.Ordinal) });

        // T04 - a prefix and an exact duplicate.
        yield return Case<RedundantIndexCheck>(
            "a prefix of another index",
            new RedundantIndexFacts([Index(Orders, IndexRow(Orders, "orders_by_status", "status")), Index(Orders, IndexRow(Orders, "orders_by_status_and_id", "status", "id"))]));
        yield return Case<RedundantIndexCheck>(
            "an exact duplicate",
            new RedundantIndexFacts([Index(Orders, IndexRow(Orders, "a_orders_status", "status")), Index(Orders, IndexRow(Orders, "z_orders_status", "status"))]));
        yield return Case<RedundantIndexCheck>("no redundancy", new RedundantIndexFacts([Index(Orders, IndexRow(Orders, "orders_by_status", "status"))]));

        // T05 - an index never scanned, one not scanned for a month on 16, and a young window.
        TableFacts written = Table(Orders, static row => row with { Inserts = 500_000 });
        UnusedIndexFacts Unused(HealthIndexRow row, bool multiHost = false) => new(At, [Index(Orders, row)], [written], DatabaseStatistics.Unknown, multiHost);

        yield return Case<UnusedIndexCheck>("never scanned", Unused(IndexRow(Orders, "orders_by_status", "status") with { Scans = 0, Bytes = 4 * MiB, ServerVersion = 150_008 }), Words(statistics: Month));
        yield return Case<UnusedIndexCheck>(
            "not scanned for a month, on a multi-host data source",
            Unused(IndexRow(Orders, "orders_by_status", "status") with { Scans = 42, Bytes = 4 * MiB, ServerVersion = 160_004, LastScan = At.AddDays(-31) }, multiHost: true),
            Words(statistics: Month));
        yield return Case<UnusedIndexCheck>("a young window", Unused(IndexRow(Orders, "orders_by_status", "status") with { Scans = 0 }), Words(statistics: Window(TimeSpan.FromDays(3))));
        yield return Case<UnusedIndexCheck>("used", Unused(IndexRow(Orders, "orders_by_status", "status")), Words(statistics: Month));

        // T06 - a plain table is never judged; a table named like a Marten document table is, by Postgres Studio too.
        HealthHistoryView Updates(StudioTable table) => History(
            (TimeSpan.FromMinutes(20), [(HotUpdateCheck.UpdatesSeries + table.Key, 0), (HotUpdateCheck.HotUpdatesSeries + table.Key, 0)]),
            (TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + table.Key, 20_000), (HotUpdateCheck.HotUpdatesSeries + table.Key, 2_000)]));

        yield return Case<HotUpdateCheck>("a relational table", new HotUpdateFacts([Table(Orders)], [], DatabaseStatistics.Unknown), Words(Updates(Orders), Month));
        yield return Case<HotUpdateCheck>("one sample", new HotUpdateFacts([Table(MartenDocuments)], [], DatabaseStatistics.Unknown), Words(statistics: Month)) with { AboutMartensTables = true };
        yield return Case<HotUpdateCheck>(
            "a Marten document table with full pages",
            new HotUpdateFacts([Table(MartenDocuments)], [Index(MartenDocuments, IndexRow(MartenDocuments, "mt_doc_order_pkey", "id") with { IsPrimaryKey = true, IsUnique = true })], DatabaseStatistics.Unknown),
            Words(Updates(MartenDocuments), Month)) with { AboutMartensTables = true };
        yield return Case<HotUpdateCheck>(
            "a Marten document table with an index on data",
            new HotUpdateFacts([Table(MartenDocuments)], [Index(MartenDocuments, IndexRow(MartenDocuments, "mt_doc_order_idx_status", "data"))], DatabaseStatistics.Unknown),
            Words(Updates(MartenDocuments), Month)) with { AboutMartensTables = true };

        // T07 - dead rows and inserts behind their triggers, held, and the table switched off.
        TableFacts Behind(StudioTable table, long dead = 40_000, string[]? options = null, bool serverAutovacuum = true) => Table(table, row => row with
        {
            EstimatedRows = 1_000_000,
            LiveRows = 1_000_000,
            DeadRows = dead,
            LastVacuum = null,
            LastAutovacuum = At.AddDays(-3),
            StorageOptions = options ?? ["autovacuum_vacuum_scale_factor=0.01"],
            AutovacuumEnabled = serverAutovacuum,
            Vacuum = Unit(dead, 10_050, At.AddDays(-3), scale: 0.01, tuples: 1_000_000),
        });

        TableFacts Appended(StudioTable table) => Table(table, row => row with
        {
            EstimatedRows = 1_000_000,
            LiveRows = 1_000_000,
            Inserts = 3_000_000,
            LastAutovacuum = At.AddDays(-3),
            Insert = Unit(500_000, 201_000, At.AddDays(-3), tuples: 1_000_000),
        });

        HealthHistoryView DeadHeld(StudioTable table) => Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.BehindSeries + table.Key, TimeSpan.FromMinutes(12));
        HealthHistoryView InsertsHeld(StudioTable table) => Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.InsertsBehindSeries + table.Key, TimeSpan.FromMinutes(12));

        yield return Case<VacuumBehindCheck>("dead rows behind", new VacuumFacts(At, [Behind(Orders)]), Words(DeadHeld(Orders)));
        yield return Case<VacuumBehindCheck>(
            "dead rows behind a halving scale factor, every worker busy",
            new VacuumFacts(At, [Behind(Orders, 120_000, ["autovacuum_vacuum_scale_factor=0.05"]) is var t ? t with { Row = t.Row with { Vacuum = Unit(120_000, 50_050, At.AddDays(-3), scale: 0.05, tuples: 1_000_000) } } : t])
            {
                Workers = new MaintenanceWorkersRow { MaxWorkers = 3, MaxWorkersContext = "sighup", CanReadAllStats = true, WorkersSeen = 3 },
            },
            Words(DeadHeld(Orders)));
        yield return Case<VacuumBehindCheck>(
            "dead rows behind a throttled table",
            new VacuumFacts(At, [Behind(Orders, options: ["autovacuum_vacuum_scale_factor=0.01", "autovacuum_vacuum_cost_delay=20"])])
            {
                Workers = new MaintenanceWorkersRow { MaxWorkers = 3, CanReadAllStats = false, MaintenanceRunning = 3, CostDelayMilliseconds = 20 },
            },
            Words(DeadHeld(Orders)));
        yield return Case<VacuumBehindCheck>("inserts behind", new VacuumFacts(At, [Appended(Events)]), Words(InsertsHeld(Events)));
        yield return Case<VacuumBehindCheck>("the server's autovacuum off", new VacuumFacts(At, [Behind(Orders, serverAutovacuum: false)]), Words(DeadHeld(Orders)));
        yield return Case<VacuumBehindCheck>("the table's autovacuum off", new VacuumFacts(At, [Behind(Orders, options: ["autovacuum_enabled=false"])]), Words(DeadHeld(Orders)));
        yield return Case<VacuumBehindCheck>("glimpsed", new VacuumFacts(At, [Behind(Orders)]), Words(Held(VacuumBehindCheck.SweptSeries, VacuumBehindCheck.BehindSeries + Orders.Key, TimeSpan.FromMinutes(3))));
        yield return Case<VacuumBehindCheck>("keeping up", new VacuumFacts(At, [Table(Orders)]));
        yield return Case<VacuumBehindCheck>(
            "a Marten event table behind",
            new VacuumFacts(At, [Appended(new StudioTable("public", "mt_events", StudioTableKind.EventTable, StudioTableOwnership.Library) { Library = "Marten" })]),
            Words(InsertsHeld(new StudioTable("public", "mt_events", StudioTableKind.EventTable, StudioTableOwnership.Library)))) with { AboutMartensTables = true };

        // T08 - never analysed, stale, since a restart.
        TableFacts never = Table(Orders, static row => row with { EstimatedRows = -1, LiveRows = 2_000, LastAnalyze = null, LastAutoanalyze = null, StorageOptions = ["autovacuum_enabled=false"] });
        TableFacts read = Table(Events, static row => row with { EstimatedRows = -1, LiveRows = 0, SequentialScans = 1, SequentialRowsRead = 1_000, LastAnalyze = null, LastAutoanalyze = null });
        TableFacts stale = Table(Orders, static row => row with
        {
            EstimatedRows = 1_000_000,
            LiveRows = 1_000_000,
            ModifiedSinceAnalyze = 120_000,
            LastAutoanalyze = At.AddHours(-2),
            Analyze = Unit(120_000, 50_050, At.AddHours(-2), scale: 0.05, tuples: 1_000_000),
        });
        StatisticsReading reset = new() { ReadAt = At, DatabaseReset = At.AddHours(-2), SharedReset = At.AddDays(-60), ArchiverReset = At.AddDays(-60), ServerStartedAt = At.AddDays(-30) };
        TableFacts big = Table(Orders, static row => row with { EstimatedRows = 500_000, LastAutoanalyze = At.AddDays(-2) });

        yield return Case<StaleStatisticsCheck>("never analysed", new StatisticsFacts(At, [never]));
        yield return Case<StaleStatisticsCheck>("read but never analysed", new StatisticsFacts(At, [read]), Words(Held(StaleStatisticsCheck.SweptSeries, StaleStatisticsCheck.WrongSeries + Events.Key, TimeSpan.FromMinutes(11))));
        yield return Case<StaleStatisticsCheck>("stale", new StatisticsFacts(At, [stale]), Words(Held(StaleStatisticsCheck.SweptSeries, StaleStatisticsCheck.WrongSeries + Orders.Key, TimeSpan.FromMinutes(15))));
        yield return Case<StaleStatisticsCheck>("since a statistics reset", new StatisticsFacts(At, [big, Table(Events, static row => row with { EstimatedRows = 5_000 })]) { Statistics = reset });
        yield return Case<StaleStatisticsCheck>("glimpsed", new StatisticsFacts(At, [stale]));
        yield return Case<StaleStatisticsCheck>("fresh", new StatisticsFacts(At, [Table(Orders)]));

        // T12 and T13 - a database far into its ranges, held up by a table that is not judged, and quiet.
        HealthWraparoundDatabase Database(Func<HealthWraparoundDatabase, HealthWraparoundDatabase>? shape = null)
        {
            HealthWraparoundDatabase database = new()
            {
                FreezeMaxAge = 200_000_000,
                MultixactFreezeMaxAge = 400_000_000,
                FailsafeAge = 1_600_000_000,
                MultixactFailsafeAge = 1_600_000_000,
                ServerVersion = 170_000,
                DatabaseXidAge = 5_000,
                DatabaseMultixactAge = 10,
                ServerXidAge = 5_000,
                ServerMultixactAge = 10,
                XidCounter = 1_000_000,
                MultixactCounter = 100,
            };

            return shape is null ? database : shape(database);
        }

        HealthHistoryView Rate(string series, string swept, string forced) => History(
            (TimeSpan.FromMinutes(40), [(series, 1_000_000), (swept, 1), (forced, 1)]),
            (TimeSpan.Zero, [(series, 1_000_000 + 1_000_000 * 40), (swept, 1), (forced, 1)]));

        yield return Case<TransactionIdAgeCheck>(
            "the oldest table far along",
            new TransactionIdAgeFacts([Table(Orders, static row => row with { OldestXidAge = 1_700_000_000 })], new WraparoundFacts(Database(static x => x with { DatabaseXidAge = 1_700_000_000, ServerXidAge = 1_700_000_000 }), [])));
        yield return Case<TransactionIdAgeCheck>(
            "held up by a table that is not judged",
            new TransactionIdAgeFacts(
                [Table(Orders, static row => row with { OldestXidAge = 30_000_000 })],
                new WraparoundFacts(Database(static x => x with
                {
                    DatabaseXidAge = 1_200_000_000,
                    ServerXidAge = 1_200_000_000,
                    OtherOldestXidAge = 1_200_000_000,
                    OtherOldestIsTemporary = true,
                    OtherPastForcedXid = 3,
                    OtherTemporaryPastForcedXid = 1,
                }), [])));
        yield return Case<TransactionIdAgeCheck>(
            "spending transaction ids fast",
            new TransactionIdAgeFacts([Table(Orders, static row => row with { OldestXidAge = 250_000_000 })], new WraparoundFacts(Database(static x => x with { DatabaseXidAge = 250_000_000, ServerXidAge = 250_000_000 }), [])),
            Words(Rate(Wraparound.XidCounterSeries, Wraparound.XidSweptSeries, Wraparound.XidPastForcedSeries)));
        yield return Case<TransactionIdAgeCheck>("young", new TransactionIdAgeFacts([Table(Orders, static row => row with { OldestXidAge = 5_000 })], new WraparoundFacts(Database(), [])));
        yield return Case<TransactionIdAgeCheck>("no table", new TransactionIdAgeFacts([], new WraparoundFacts(Database(), [])));

        yield return Case<MultixactAgeCheck>(
            "the oldest table far along",
            new MultixactAgeFacts(Database(static x => x with { DatabaseMultixactAge = 1_700_000_000, ServerMultixactAge = 1_700_000_000 }), [new TableMultixactAge(Orders, 1_700_000_000)]));
        yield return Case<MultixactAgeCheck>(
            "past its warning line",
            new MultixactAgeFacts(Database(static x => x with { DatabaseMultixactAge = 1_200_000_000, ServerMultixactAge = 1_200_000_000 }), [new TableMultixactAge(Orders, 1_200_000_000)]));
        yield return Case<MultixactAgeCheck>(
            "creating multixacts fast",
            new MultixactAgeFacts(Database(static x => x with { DatabaseMultixactAge = 450_000_000, ServerMultixactAge = 450_000_000 }), [new TableMultixactAge(Orders, 450_000_000)], TimeSpan.FromMinutes(1)),
            Words(Rate(Wraparound.MultixactCounterSeries, Wraparound.MultixactSweptSeries, Wraparound.MultixactPastForcedSeries)));
        yield return Case<MultixactAgeCheck>("young", new MultixactAgeFacts(Database(), [new TableMultixactAge(Orders, 50_000)]));

        // T14 - a session, a prepared transaction, a logical slot, a standby, and nothing.
        HorizonHolder Session(TimeSpan? atLeast, bool wrote = false) => new()
        {
            Kind = HealthHorizonHolderKind.Session,
            XidAge = 50_000,
            AtLeast = atLeast,
            AtMost = atLeast,
            ProcessId = 4242,
            User = "app_user",
            Application = "billing-api",
            Client = "10.1.2.3",
            BackendStart = At.AddDays(-1),
            State = "idle in transaction",
            Bound = "idle",
            HasTransactionId = wrote,
            OwnXidAge = wrote ? 50_000 : null,
        };

        HorizonFacts Horizon(IReadOnlyList<HorizonHolder> holders, SessionVisibility? visibility = null) =>
            new(At, visibility ?? SessionVisibility.Everything, holders, Database(), [Behind(Orders), Table(Events)], false, false);

        ReplicationSlot LogicalSlot(bool thisDatabase, bool lost = false) =>
            new("cdc_orders", "logical", "pgoutput", thisDatabase ? "orders" : "billing", false, false, 4 * GiB, lost ? "lost" : "reserved", null,
                null, 250_000_000, TimeSpan.FromDays(3), null, null, thisDatabase);

        yield return Case<HorizonHeldCheck>("a session for two hours", Horizon([Session(TimeSpan.FromHours(2), wrote: true)]));
        yield return Case<HorizonHeldCheck>("a session for a day, hidden sessions", Horizon([Session(TimeSpan.FromHours(25))], new SessionVisibility(false, 2)));
        yield return Case<HorizonHeldCheck>(
            "a prepared transaction",
            Horizon([new HorizonHolder { Kind = HealthHorizonHolderKind.Prepared, XidAge = 300_000_000, AtLeast = TimeSpan.FromHours(30), Name = "gid-orphan", User = "tx_owner", HasTransactionId = true, OwnXidAge = 300_000_000 }]));
        yield return Case<HorizonHeldCheck>("a logical slot of this database", Horizon([.. HorizonHolder.From(LogicalSlot(true), 0)]));
        yield return Case<HorizonHeldCheck>("a logical slot of another database", Horizon([.. HorizonHolder.From(LogicalSlot(false), 0)]));
        yield return Case<HorizonHeldCheck>("a lost logical slot", Horizon([.. HorizonHolder.From(LogicalSlot(true, lost: true), 0)]));
        yield return Case<HorizonHeldCheck>(
            "a physical slot's standby",
            Horizon([.. HorizonHolder.From(new ReplicationSlot("standby_b", "physical", null, null, false, false, 16 * MiB, "reserved", null, 230_000_000, null, TimeSpan.FromDays(2), null, null, true), 0)]));
        yield return Case<HorizonHeldCheck>(
            "a standby's feedback",
            Horizon([new HorizonHolder { Kind = HealthHorizonHolderKind.Standby, XidAge = 300_000_000, ProcessId = 99, User = "replicator", State = "streaming" }]));
        yield return Case<HorizonHeldCheck>(
            "a session in another database",
            Horizon([new HorizonHolder { Kind = HealthHorizonHolderKind.OtherDatabaseSession, XidAge = 300_000_000, AtLeast = TimeSpan.FromHours(2) }]));
        yield return Case<HorizonHeldCheck>("a session for five minutes", Horizon([Session(TimeSpan.FromMinutes(5))]));
        yield return Case<HorizonHeldCheck>("nothing holds it", Horizon([]));

        // T15 - a key with no index, a key partly covered, and one covered.
        HealthForeignKeyRow Key(string name, short[] attributes, string[] columns) =>
            new(Orders.Schema, Orders.Table, name, attributes, columns, Parent.Schema, Parent.Table, ["id"], [], 1_234, true);
        TableFacts bigOrders = Table(Orders, static row => row with { HeapBytes = 64 * MiB });

        yield return Case<UnindexedForeignKeyCheck>(
            "a key with no index",
            new UnindexedForeignKeyFacts([bigOrders], [Orders, Parent], [Key("orders_customer_id_fkey", [3], ["customer_id"])], []));
        yield return Case<UnindexedForeignKeyCheck>(
            "a key partly covered",
            new UnindexedForeignKeyFacts([bigOrders], [Orders, Parent], [Key("orders_region_customer_fkey", [2, 3], ["region", "customer_id"])], [Index(Orders, IndexRow(Orders, "orders_by_region", "region") with { KeyAttributes = [2] })]));
        yield return Case<UnindexedForeignKeyCheck>(
            "a covered key",
            new UnindexedForeignKeyFacts([bigOrders], [Orders, Parent], [Key("orders_customer_id_fkey", [3], ["customer_id"])], [Index(Orders, IndexRow(Orders, "orders_by_customer", "customer_id") with { KeyAttributes = [3] })]));
        yield return Case<UnindexedForeignKeyCheck>("no key", new UnindexedForeignKeyFacts([bigOrders], [Orders, Parent], [], []));

        // T16 - every worker busy, with and without pg_read_all_stats, and watched.
        AutovacuumWorkersFacts Workers(int busy, bool canReadAllStats) => new(
            At,
            new MaintenanceWorkersRow
            {
                MaxWorkers = 3,
                MaxWorkersContext = "postmaster",
                NaptimeSeconds = 60,
                Autovacuum = true,
                CanReadAllStats = canReadAllStats,
                WorkersSeen = canReadAllStats ? busy : 0,
                MaintenanceRunning = busy,
            },
            [],
            PostgresPlatform.SelfHosted);

        HealthHistoryView Saturated(int samples) => History([
            .. Enumerable.Range(0, samples).Reverse().Select(static i => (TimeSpan.FromMinutes(i), new[] { (AutovacuumWorkersCheck.SweptSeries, 1d), (AutovacuumWorkersCheck.SaturatedSeries, 1d) })),
        ]);

        yield return Case<AutovacuumWorkersCheck>("every worker busy", Workers(3, true), Words(Saturated(3)));
        yield return Case<AutovacuumWorkersCheck>("as many maintenance processes as workers", Workers(3, false), Words(Saturated(4)));
        yield return Case<AutovacuumWorkersCheck>("two samples", Workers(3, true), Words(Saturated(2)));
        yield return Case<AutovacuumWorkersCheck>("room", Workers(1, true));

        // T19 and T20 - a partitioned table against the lock table, and range partitions running out.
        HealthPartitionedTableRow TwoHundred(int maxConnections = 100) => new()
        {
            Schema = Events.Schema,
            Table = Events.Table,
            Strategy = "l",
            KeyColumns = 1,
            Leaves = 200,
            LeafIndexes = 400,
            PartitionedIndexes = 2,
            MaxLocksPerTransaction = 64,
            MaxConnections = maxConnections,
            ServerVersion = 170_011,
        };

        yield return Case<PartitionLockBudgetCheck>("past max_locks_per_transaction", new PartitionLockFacts(new PartitionFacts(At, [TwoHundred()], [Events]), PostgresPlatform.SelfHosted));
        yield return Case<PartitionLockBudgetCheck>("past half the lock table", new PartitionLockFacts(new PartitionFacts(At, [TwoHundred(10)], [Events]), PostgresPlatform.SelfHosted));
        yield return Case<PartitionLockBudgetCheck>("few partitions", new PartitionLockFacts(new PartitionFacts(At, [TwoHundred() with { Leaves = 20, LeafIndexes = 20 }], [Events]), PostgresPlatform.SelfHosted));
        yield return Case<PartitionLockBudgetCheck>("no partitioned table", new PartitionLockFacts(PartitionFacts.None(At), PostgresPlatform.SelfHosted));

        static double Epoch(DateTimeOffset at) => at.ToUnixTimeMilliseconds() / 1000d;
        HealthPartitionedTableRow Monthly(DateTimeOffset lower, DateTimeOffset upper, bool hasDefault = false, double defaultRows = 0) => new()
        {
            Schema = Events.Schema,
            Table = Events.Table,
            Strategy = "r",
            KeyColumns = 1,
            KeyType = "timestamp with time zone",
            Leaves = 12,
            BoundedPartitions = 12,
            NewestLower = Epoch(lower),
            NewestUpper = Epoch(upper),
            HasDefault = hasDefault,
            DefaultRows = defaultRows,
        };

        yield return Case<RangePartitionsCheck>(
            "the newest partition ends within a month",
            new PartitionFacts(At, [Monthly(new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))], [Events]));
        yield return Case<RangePartitionsCheck>(
            "past its end, with rows in the default",
            new PartitionFacts(At, [Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), hasDefault: true, defaultRows: 1_500)], [Events]));
        yield return Case<RangePartitionsCheck>(
            "months ahead",
            new PartitionFacts(At, [Monthly(new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))], [Events]));
        yield return Case<RangePartitionsCheck>("no partitioned table", PartitionFacts.None(At));

        // X4: an integer range whose rows reach its newest partition, and a time range written in another DateStyle.
        yield return Case<RangePartitionsCheck>(
            "rows in the newest integer partition",
            new PartitionFacts(At, [Monthly(default, default) with { KeyType = "bigint", NewestLower = 3_000, NewestUpper = 4_000, PopulatedUpper = 4_000 }], [Events]));
        yield return Case<RangePartitionsCheck>(
            "bounds in another DateStyle",
            new PartitionFacts(At, [Monthly(default, default) with { NewestLower = null, NewestUpper = null, UnreadBounds = 12, DateStyle = "SQL, DMY" }], [Events]));

        // T24 - a table, its TOAST table and some partitions switched off.
        yield return Case<AutovacuumDisabledCheck>(
            "switched off, and past half the freeze age",
            new AutovacuumDisabledFacts([Table(Orders, static row => row with { StorageOptions = ["autovacuum_enabled=false"], AutovacuumOffLeaves = 1, OldestXidAge = 150_000_000 })]));
        yield return Case<AutovacuumDisabledCheck>(
            "its TOAST table switched off",
            new AutovacuumDisabledFacts([Table(Orders, static row => row with { ToastAutovacuumOffLeaves = 1, ToastStorageOptions = ["autovacuum_enabled=false"] })]));
        yield return Case<AutovacuumDisabledCheck>(
            "some partitions switched off",
            new AutovacuumDisabledFacts([Table(Events, static row => row with { Kind = "p", PartitionLeaves = 12, AutovacuumOffLeaves = 3 })]));
        yield return Case<AutovacuumDisabledCheck>("nothing switched off", new AutovacuumDisabledFacts([Table(Orders)]));

        // T25 - a key-width screen and the issue's rule.
        HealthIndexRow Int4Key(StudioTable table, long bytes, double entries) =>
            IndexRow(table, table.Table + "_pkey", "id") with { Bytes = bytes, EstimatedTuples = entries, IsPrimaryKey = true, IsUnique = true };
        long issueBytes = 128 * MiB;

        yield return Case<BloatedIndexCheck>("a mostly empty key", new BloatedIndexFacts([Index(Orders, Int4Key(Orders, 4_513_792L * 20, 200_000))]));
        yield return Case<BloatedIndexCheck>("ten times the issue's rule", new BloatedIndexFacts([Index(Events, Int4Key(Events, issueBytes, issueBytes / 640.0))]));
        yield return Case<BloatedIndexCheck>("a dense key", new BloatedIndexFacts([Index(Orders, Int4Key(Orders, 4_513_792L * 20, 4_000_000))]));

        // T26 - an identity at 81%, a bigint column on an integer sequence, statistics not readable.
        HealthIntegerKeyRow Identity(long largest, string columnType = "integer") => new()
        {
            Schema = Orders.Schema,
            Table = Orders.Table,
            Column = "id",
            ColumnType = columnType,
            SequenceSchema = Orders.Schema,
            Sequence = "orders_id_seq",
            SequenceType = "integer",
            FedBy = "identity",
            Maximum = int.MaxValue,
            Rows = 1_000,
            LargestSampled = largest,
        };

        yield return Case<IntegerKeyHeadroomCheck>("an integer identity at 81%", new IntegerKeyFacts([Identity((long) (int.MaxValue * 0.81))], [Orders]));
        yield return Case<IntegerKeyHeadroomCheck>(
            "a bigint column on an integer sequence",
            new IntegerKeyFacts([Identity((long) (int.MaxValue * 0.6), "bigint") with { FedBy = "default" }], [Orders]));
        yield return Case<IntegerKeyHeadroomCheck>("half its range", new IntegerKeyFacts([Identity((long) (int.MaxValue * 0.4))], [Orders]));
        yield return Case<IntegerKeyHeadroomCheck>(
            "statistics not readable",
            new IntegerKeyFacts([Identity(0) with { LargestSampled = null }], [Orders]) { ReadsAs = "app" });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Statements: T02 and T23.

    private static IEnumerable<CheckWordCase> Statements()
    {
        StatementExtension pg17 = new(170011, "public", "1.11");

        ObservedStatement Statement(long queryId, string text, long calls, double totalMs, long rows = 0, double? jitMs = null, long temp = 0, double wal = 0) =>
            new(
                new StatementCounterRow
                {
                    QueryId = queryId,
                    Calls = calls,
                    Rows = rows == 0 ? calls : rows,
                    TotalExecMs = totalMs,
                    MeanExecMs = calls > 0 ? totalMs / calls : 0,
                    TempBlocksWritten = temp,
                    WalBytes = wal,
                    WalRecords = wal > 0 ? calls : 0,
                    JitMs = jitMs,
                    StatsSince = At.AddHours(-1),
                },
                text,
                false,
                [Orders]);

        StatementRead Read(params ObservedStatement[] statements) =>
            new(At, pg17, statements.Length, statements.Sum(static x => x.Counters.TotalExecMs) * 1.2, statements.Sum(static x => x.Counters.WalBytes) + 1, 0, At.AddDays(-1), statements);

        ObservedStatement slow = Statement(11, "select o.id, o.status from public.orders as o where o.status = $1", 1_000, 900_000, rows: 2_000_000, temp: 512, wal: 1_000);
        ObservedStatement heavy = Statement(12, "select count(*) from public.orders", 50_000, 1_000_000, rows: 50_000);

        // T02 - a slow statement and a heavy one, each with a plan: one scanning the table, one reading only a relation it does not name.
        yield return Case<ExpensiveStatementsCheck>(
            "a slow statement and a heavy one",
            new ExpensiveStatementFacts(
                Read(slow, heavy),
                [
                    new StatementPlan(11, [new ExplainPlanScan("Seq Scan", "orders", null, "(status = $1)", null), new ExplainPlanScan("Seq Scan", "orders_archive", null, null, null)], null),
                    new StatementPlan(12, [new ExplainPlanScan("Seq Scan", "orders_2026_10", null, null, null)], null),
                ],
                []));
        yield return Case<ExpensiveStatementsCheck>(
            "a slow statement not planned",
            new ExpensiveStatementFacts(Read(slow), [new StatementPlan(11, [], "Not planned: the plan could not be read.")], []));
        yield return Case<ExpensiveStatementsCheck>(
            "cheap statements",
            new ExpensiveStatementFacts(Read(Statement(13, "select o.id from public.orders as o where o.id = $1", 10, 5)), [], []));

        // T23 - JIT time over a fifth, and none.
        ObservedStatement compiling = Statement(21, "select o.id from public.orders as o where o.total > $1", 5_000, 50_000, jitMs: 25_000);

        yield return Case<JitShareCheck>("a statement that compiles", new JitFacts(Read(compiling)));
        yield return Case<JitShareCheck>(
            "two statements that compile",
            new JitFacts(Read(compiling, Statement(22, "select o.status from public.orders as o where o.total < $1", 5_000, 40_000, jitMs: 30_000))));
        yield return Case<JitShareCheck>("no JIT", new JitFacts(Read(Statement(23, "select o.id from public.orders as o where o.id = $1", 5_000, 5_000, jitMs: 0))));
    }
}
