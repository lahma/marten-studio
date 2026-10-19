using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Internal.Sql;
using MartenStudio.Sample.Generation;
using MartenStudio.SampleDomain;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.DaemonSweeps;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The sample's "make it unhealthy" switches against a real Postgres, judged by the studio's own checks: each switch
/// on makes its check fire, off makes it clear, and every way a switch ends - turned off, its timeout, the host stopping
/// - leaves nothing behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>The sample's own switches, in this process.</b> The switch classes and the board are the sample host's, built over
/// a store configured as the sample host configures its own (<see cref="SampleStore.Configure" /> and
/// <see cref="UnhealthyDemoStore.Configure" />), beside a studio container that judges them. In this process because C01
/// reads the connection pool of the process it runs in: a sample host in a child process would hog a pool no check here
/// could see. <c>UnhealthySwitchesSampleHostTests</c> drives the same switches through the real host's endpoints.
/// </para>
/// <para>
/// <b>A database each</b> for the connection checks (<see cref="ConnectionsLiveHost" />), whose reads are bounded to the
/// database and would otherwise see another test class's sessions; the pinned gap needs a daemon and is a schema of the
/// shared database, as D01's own live tests are (<see cref="DaemonHealthHost" />).
/// </para>
/// <para>
/// <b>No real minute is waited out.</b> C04's and D01's thresholds are a minute of idleness or of waiting; a read is
/// judged at a later time (<c>SnapshotAge</c>) or the sweeper's clock is moved, the way their own live tests do it.
/// Every wait is on a condition.
/// </para>
/// </remarks>
public class UnhealthySwitchesLiveTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    /// <summary>The demo table, quoted, in the sample's document schema.</summary>
    private static readonly string DemoTable = "\"" + SampleStore.DocumentSchema + "\".\"" + UnhealthyDemoStore.BloatTableName + "\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Pinned gap: D01 -----------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("D01")]
    public async Task The_pinned_gap_holds_the_mark_D01_fires_and_it_clears_once_turned_off()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "unhealthy_pinned_gap",
            DaemonMode.Solo,
            store: UnhealthyDemoStore.Configure);
        await host.WaitForDaemonAsync();
        await WaitForMarkAsync(host, async mark => mark >= await host.HighestSequenceAsync(postgres), "the daemon to catch up with the seeded events");

        await using SwitchRig rig = new(host.Store);
        rig.TurnOn(UnhealthySwitchKind.PinnedGap);
        await rig.WaitForAsync(UnhealthySwitchKind.PinnedGap, UnhealthySwitchState.On);

        rig.Status(UnhealthySwitchKind.PinnedGap).Detail.Should().Contain(SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence"),
            "the sequence is the store's own, read from its event schema");

        long held = await HeldNumberAsync(host);

        await LiveWait.UntilAsync(
            async () => await PinnedAsync(host),
            Settle,
            "the daemon to record the mark as pinned under the held number");

        // The wait is measured from the advisor's first sample that saw the marker behind the mark, so two sweeps a
        // (moved) minute and more apart.
        Findings(await host.SweepAsync(), "D01").Should().BeEmpty("the first sweep that sees the wait has nothing to measure it from");
        host.Advance(TimeSpan.FromMinutes(2));

        HealthFinding finding = Findings(await host.SweepAsync(), "D01").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);

        (await rig.Board.TurnOffAsync(UnhealthySwitchKind.PinnedGap)).Should().BeTrue("the clean-up is a rollback");
        rig.Status(UnhealthySwitchKind.PinnedGap).LastOutcome.Should().StartWith("Turned off");

        (await SessionsNamedAsync(postgres.ConnectionString, rig.Switch(UnhealthySwitchKind.PinnedGap))).Should().Be(0, "the transaction is rolled back");

        // Nothing can fill the abandoned number now, so Marten skips it within its stale threshold.
        await WaitForMarkAsync(host, mark => Task.FromResult(mark > held), "the daemon to skip the abandoned number");

        host.Advance(TimeSpan.FromMinutes(2));
        Findings(await host.SweepAsync(), "D01").Should().BeEmpty("nothing holds the mark any more");
    }

    /// <summary>
    /// The panel names C04 for the pinned gap too: its session idles in the transaction that drew the number. Proved with
    /// C04's own read and judgement, as the idle switch's test does - a database of its own, so no other class's session
    /// is in the read, and no daemon, which C04 does not need.
    /// </summary>
    [PostgresFact]
    [HealthFires("C04")]
    public async Task The_pinned_gaps_open_transaction_is_C04_after_a_minute()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_gap_c04");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: UnhealthyDemoStore.Configure);

        await using SwitchRig rig = new(host.Store);
        UnhealthySwitch gap = rig.Switch(UnhealthySwitchKind.PinnedGap);
        IdleInTransactionCheck check = new();

        rig.TurnOn(UnhealthySwitchKind.PinnedGap);
        await rig.WaitForAsync(UnhealthySwitchKind.PinnedGap, UnhealthySwitchState.On);

        IdleInTransactionFacts facts = null!;
        await LiveWait.UntilAsync(
            async () => (facts = await host.GatherAsync(check)).Sessions.Any(x => x.Application == gap.ApplicationName),
            Settle,
            "C04's read to list the pinned gap's session");

        facts.Sessions.Single(x => x.Application == gap.ApplicationName).PinsSomething
            .Should().BeTrue("a transaction that drew a sequence number holds that sequence's lock until it ends");

        HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(2))).Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("C04");

        (await rig.Board.TurnOffAsync(UnhealthySwitchKind.PinnedGap)).Should().BeTrue();

        IdleInTransactionFacts after = await host.GatherAsync(check);
        check.Evaluate(after, HealthEvaluation.At(after.ReadAt.AddMinutes(2))).Findings.Should().BeEmpty("the transaction is rolled back");
    }

    // ---- Idle in transaction: C04 --------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C04")]
    public async Task The_idle_session_is_C04_after_a_minute_and_gone_once_turned_off()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_idle");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: UnhealthyDemoStore.Configure);

        await using SwitchRig rig = new(host.Store);
        UnhealthySwitch idle = rig.Switch(UnhealthySwitchKind.IdleInTransaction);
        IdleInTransactionCheck check = new();

        rig.TurnOn(UnhealthySwitchKind.IdleInTransaction);
        await rig.WaitForAsync(UnhealthySwitchKind.IdleInTransaction, UnhealthySwitchState.On);

        IdleInTransactionFacts facts = null!;
        await LiveWait.UntilAsync(
            async () => (facts = await host.GatherAsync(check)).Sessions.Any(x => x.Application == idle.ApplicationName),
            Settle,
            "C04's read to list the switch's session");

        IdleSession session = facts.Sessions.Single(x => x.Application == idle.ApplicationName);
        session.HasSnapshot.Should().BeTrue("a repeatable-read transaction keeps the snapshot its statement took");
        session.PinsSomething.Should().BeTrue();

        check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty("a second is not a minute");

        HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(2))).Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("C04");
        finding.Severity.Should().Be(HealthSeverity.Warning);

        (await rig.Board.TurnOffAsync(UnhealthySwitchKind.IdleInTransaction)).Should().BeTrue();

        IdleInTransactionFacts after = await host.GatherAsync(check);
        after.Sessions.Should().NotContain(x => x.Application == idle.ApplicationName, "the transaction is rolled back");
        check.Evaluate(after, HealthEvaluation.At(after.ReadAt.AddMinutes(2))).Findings.Should().BeEmpty();
    }

    // ---- Bloated table: T07 --------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("T07")]
    public async Task The_bloated_table_is_T07_at_once_and_vacuumed_with_autovacuum_restored_once_turned_off()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_bloat");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: UnhealthyDemoStore.Configure, clock: clock);

        string table = SchemaKey.For(SampleStore.DocumentSchema, UnhealthyDemoStore.BloatTableName);

        await using SwitchRig rig = new(host.Store);
        rig.TurnOn(UnhealthySwitchKind.BloatedTable);
        await rig.WaitForAsync(UnhealthySwitchKind.BloatedTable, UnhealthySwitchState.On);

        (await ReloptionsAsync(database)).Should().Contain("autovacuum_enabled=false");

        // The statistics collector hears about the dead rows when the backend that made them flushes its counters.
        await LiveWait.UntilAsync(
            // T07's own line is 10,000; the switch leaves one dead row per row it holds, and ANALYZE's estimate of them
            // is what the view shows last.
            async () => await DeadRowsAsync(database) >= 10_000,
            Settle,
            "pg_stat_user_tables to count the dead rows");

        HealthReport report = await host.ReportAsync();
        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T07").Subject;
        finding.Finding.Subject.Should().Be(table);
        finding.Severity.Should().Be(HealthSeverity.Warning);
        report.Findings.Should().NotContain(static x => x.Code == "T08", "the switch analyses the table, so its statistics are current");

        (await rig.Board.TurnOffAsync(UnhealthySwitchKind.BloatedTable)).Should().BeTrue();

        (await ReloptionsAsync(database)).Should().BeNull("autovacuum_enabled is reset to the table's own default");
        (await LastVacuumAsync(database)).Should().NotBeNull("turning the switch off vacuums the table");
        await LiveWait.UntilAsync(async () => await DeadRowsAsync(database) == 0, Settle, "the vacuum to clear the dead rows");

        clock.Advance(Interval(host) + TimeSpan.FromSeconds(1));
        (await host.ReportAsync()).Findings.Should().NotContain(static x => x.Code == "T07");
    }

    // ---- Pool hog: C01 -------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("C01")]
    public async Task The_pool_hog_is_C01_at_once_and_its_connections_are_closed_once_turned_off()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_pool_hog");
        string small = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 20, Timeout = 5 }.ConnectionString;
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(small, store: UnhealthyDemoStore.Configure, clock: clock);

        await using SwitchRig rig = new(host.Store);
        UnhealthySwitch hog = rig.Switch(UnhealthySwitchKind.PoolHog);

        rig.TurnOn(UnhealthySwitchKind.PoolHog);
        await rig.WaitForAsync(UnhealthySwitchKind.PoolHog, UnhealthySwitchState.On);

        rig.Status(UnhealthySwitchKind.PoolHog).Detail.Should().StartWith("Holding 18 of the pool's 20 connections (90%)");
        (await SessionsNamedAsync(database, hog)).Should().Be(18);

        HealthReport report = await host.ReportAsync();
        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "C01").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning, "past 80% of the pool, and never all of it");

        (await rig.Board.TurnOffAsync(UnhealthySwitchKind.PoolHog)).Should().BeTrue();

        await LiveWait.UntilAsync(async () => await SessionsNamedAsync(database, hog) == 0, Settle, "the pool to close the hog's connections");

        clock.Advance(Interval(host) + TimeSpan.FromSeconds(1));
        (await host.ReportAsync()).Findings.Should().NotContain(static x => x.Code == "C01");
    }

    // ---- How a switch ends ---------------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData(nameof(UnhealthySwitchKind.PinnedGap))]
    [InlineData(nameof(UnhealthySwitchKind.IdleInTransaction))]
    [InlineData(nameof(UnhealthySwitchKind.BloatedTable))]
    [InlineData(nameof(UnhealthySwitchKind.PoolHog))]
    public async Task A_switch_past_its_timeout_turns_itself_off_and_leaves_nothing_behind(string name)
    {
        UnhealthySwitchKind kind = Enum.Parse<UnhealthySwitchKind>(name);
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(
            postgres.ConnectionString, "unhealthy_timeout_" + kind.ToString().ToLowerInvariant());
        string pooled = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 20, Timeout = 5 }.ConnectionString;
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(pooled, store: UnhealthyDemoStore.Configure);

        await using SwitchRig rig = new(host.Store);
        rig.TurnOn(kind, TimeSpan.FromSeconds(4));

        await rig.WaitForAsync(kind, UnhealthySwitchState.On);
        (await HeldAsync(database, rig.Switch(kind))).Should().BeTrue("{0} holds its condition while it is on", kind);

        await rig.WaitForAsync(kind, UnhealthySwitchState.Off);

        rig.Status(kind).LastOutcome.Should().StartWith("Turned itself off after 4 s");
        await LiveWait.UntilAsync(async () => !await HeldAsync(database, rig.Switch(kind)), Settle, kind + " to leave nothing behind");

        if (kind == UnhealthySwitchKind.BloatedTable)
        {
            (await LastVacuumAsync(database)).Should().NotBeNull("the timeout's clean-up vacuums the table, as turning it off does");
        }
    }

    [PostgresFact]
    public async Task Stopping_the_host_turns_every_switch_off_and_leaves_nothing_behind()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_shutdown");
        string pooled = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 30, Timeout = 5 }.ConnectionString;
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(pooled, store: UnhealthyDemoStore.Configure);

        await using SwitchRig rig = new(host.Store);

        foreach (UnhealthySwitchKind kind in Enum.GetValues<UnhealthySwitchKind>())
        {
            rig.TurnOn(kind);
            await rig.WaitForAsync(kind, UnhealthySwitchState.On);
            (await HeldAsync(database, rig.Switch(kind))).Should().BeTrue("{0} is on", kind);
        }

        // What the host does on Ctrl+C: the stopping token first, then every hosted service's StopAsync.
        rig.Lifetime.StopApplication();
        await rig.Board.StopAsync(Token);

        foreach (UnhealthySwitchKind kind in Enum.GetValues<UnhealthySwitchKind>())
        {
            rig.Status(kind).State.Should().Be(UnhealthySwitchState.Off, "{0} is cleaned up before StopAsync returns", kind);
            rig.Status(kind).LastOutcome.Should().StartWith("Turned off when the host stopped");
            (await HeldAsync(database, rig.Switch(kind))).Should().BeFalse("{0} leaves nothing behind", kind);
        }

        (await LastVacuumAsync(database)).Should().NotBeNull("the host's stop vacuums the demo table, as turning the switch off does");
    }

    /// <summary>
    /// F3: the pool hog stops growing, rather than wait out the pool's Timeout or fail, once no more than LeaveFree
    /// connections can still be had - here because the "host" holds six of the twenty itself.
    /// </summary>
    [PostgresFact]
    public async Task The_pool_hog_stops_growing_while_the_host_still_has_its_share_of_the_pool()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_pool_hog_short");
        string small = new NpgsqlConnectionStringBuilder(database) { MaxPoolSize = 20, Timeout = 60 }.ConnectionString;
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(small, store: UnhealthyDemoStore.Configure);

        List<NpgsqlConnection> hostsOwn = [];

        try
        {
            for (int i = 0; i < 6; i++)
            {
                NpgsqlConnection connection = host.Store.Storage.Database.CreateConnection();
                hostsOwn.Add(connection);
                await connection.OpenAsync(Token);
            }

            await using SwitchRig rig = new(host.Store);
            long started = System.Diagnostics.Stopwatch.GetTimestamp();

            rig.TurnOn(UnhealthySwitchKind.PoolHog);
            await rig.WaitForAsync(UnhealthySwitchKind.PoolHog, UnhealthySwitchState.On);

            System.Diagnostics.Stopwatch.GetElapsedTime(started).Should().BeLessThan(TimeSpan.FromSeconds(30),
                "a pool with nothing left to give stops the hog at once, never after the pool's sixty-second Timeout");

            rig.Status(UnhealthySwitchKind.PoolHog).Detail.Should()
                .StartWith("Holding 12 of the pool's 20 connections (60%) open and idle; it stopped growing there",
                    "twenty, less the host's six, less the two it leaves");
            (await SessionsNamedAsync(database, rig.Switch(UnhealthySwitchKind.PoolHog))).Should().Be(12);

            // And the two it left are really there for the host to take.
            await using NpgsqlConnection one = host.Store.Storage.Database.CreateConnection();
            await using NpgsqlConnection two = host.Store.Storage.Database.CreateConnection();
            using CancellationTokenSource quickly = CancellationTokenSource.CreateLinkedTokenSource(Token);
            quickly.CancelAfter(TimeSpan.FromSeconds(10));
            await one.OpenAsync(quickly.Token);
            await two.OpenAsync(quickly.Token);
        }
        finally
        {
            foreach (NpgsqlConnection connection in hostsOwn)
            {
                await connection.DisposeAsync();
            }
        }
    }

    // ---- The demo table at start-up (F2) ------------------------------------------------------------------

    /// <summary>
    /// A host killed with the bloated table on runs no clean-up: on a persistent database the table keeps autovacuum off.
    /// The next start puts it back - reset and vacuumed - as turning the switch off would have.
    /// </summary>
    [PostgresFact]
    public async Task Start_up_puts_autovacuum_back_on_a_table_a_killed_host_left_switched_off()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_killed_host");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: UnhealthyDemoStore.Configure);

        // What a killed host leaves: the table there, autovacuum off for it.
        await LiveWait.ScalarAsync<int>(
            database,
            "alter table " + DemoTable + " set (autovacuum_enabled = false)");
        (await ReloptionsAsync(database)).Should().Contain("autovacuum_enabled=false");

        await new UnhealthyDemoTableInitializer().Populate(host.Store, Token);

        (await ReloptionsAsync(database)).Should().BeNull("autovacuum_enabled is reset");
        (await LastVacuumAsync(database)).Should().NotBeNull("and the table vacuumed");
    }

    /// <summary>
    /// The other half of start-up, and of a clean-up cancelled between dropping and recreating the table: a declared table
    /// that is missing is created again from its declaration, and an existing healthy one is left alone, rows and all.
    /// </summary>
    [PostgresFact]
    public async Task Start_up_creates_a_missing_demo_table_and_leaves_a_healthy_one_alone()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "unhealthy_missing_table");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: UnhealthyDemoStore.Configure);

        await LiveWait.ScalarAsync<int>(database, "drop table " + DemoTable);

        await new UnhealthyDemoTableInitializer().Populate(host.Store, Token);
        (await LiveWait.ScalarAsync<bool>(database, "select pg_catalog.to_regclass(@table) is not null", ("table", DemoTable)))
            .Should().BeTrue("a missing declared table is created again");

        await LiveWait.ScalarAsync<int>(database, "insert into " + DemoTable + " (id, revision) values (1, 0)");
        await new UnhealthyDemoTableInitializer().Populate(host.Store, Token);

        (await LiveWait.ScalarAsync<long>(database, "select count(*) from " + DemoTable)).Should().Be(1, "a healthy table is left alone");
        (await LastVacuumAsync(database)).Should().BeNull("nothing needed vacuuming");
    }

    // ---- Helpers -------------------------------------------------------------------------------------------

    /// <summary>The sample's four switches and their board, over a store of the test's own.</summary>
    private sealed class SwitchRig : IAsyncDisposable
    {
        public SwitchRig(IDocumentStore store)
        {
            Switches =
            [
                new PinnedGapSwitch(store),
                new IdleInTransactionSwitch(store),
                new BloatedTableSwitch(store),
                new PoolHogSwitch(store),
            ];

            // Each test's database is its own, made to be broken: as if the host had been started with --allow-data-generation.
            Board = new UnhealthySwitchBoard(
                Switches,
                new UnhealthySwitchesDatabase(IsThrowaway: false, AllowedExplicitly: true),
                Lifetime,
                NullLogger<UnhealthySwitchBoard>.Instance);
        }

        public UnhealthySwitchEndpointTests.StandAloneLifetime Lifetime { get; } = new();

        public UnhealthySwitch[] Switches { get; }

        public UnhealthySwitchBoard Board { get; }

        public UnhealthySwitch Switch(UnhealthySwitchKind kind) => Switches.Single(x => x.Kind == kind);

        public UnhealthySwitchStatus Status(UnhealthySwitchKind kind) => Board.Status(kind);

        public void TurnOn(UnhealthySwitchKind kind, TimeSpan? timeout = null) =>
            Board.TryTurnOn(kind, out string? error, timeout).Should().BeTrue(error);

        /// <summary>Waits for a state; a switch that failed instead fails the test with its reason.</summary>
        public Task WaitForAsync(UnhealthySwitchKind kind, UnhealthySwitchState state) =>
            LiveWait.UntilAsync(
                () =>
                {
                    UnhealthySwitchStatus status = Board.Status(kind);

                    if (status.State == UnhealthySwitchState.Failed && state != UnhealthySwitchState.Failed)
                    {
                        throw new InvalidOperationException(kind + " failed: " + status.LastOutcome);
                    }

                    return Task.FromResult(status.State == state);
                },
                Settle,
                kind + " to be " + state);

        public async ValueTask DisposeAsync()
        {
            // A test that failed half-way still turns its switches off, so the next test's database is not held open.
            Lifetime.StopApplication();
            await Board.StopAsync(CancellationToken.None);
            Board.Dispose();
            Lifetime.Dispose();
        }
    }

    /// <summary>Whether the switch's condition is in the database: its sessions, or its table's switched-off autovacuum.</summary>
    private static async Task<bool> HeldAsync(string connectionString, UnhealthySwitch @switch) =>
        @switch.Kind == UnhealthySwitchKind.BloatedTable
            ? await ReloptionsAsync(connectionString) is { } options && options.Contains("autovacuum_enabled=false", StringComparison.Ordinal)
            : await SessionsNamedAsync(connectionString, @switch) > 0;

    /// <summary>Sessions in the database that carry the switch's application name.</summary>
    private static async Task<long> SessionsNamedAsync(string connectionString, UnhealthySwitch @switch) =>
        await LiveWait.ScalarAsync<long>(
            connectionString,
            "select count(*) from pg_catalog.pg_stat_activity where datname = current_database() and application_name = @name",
            ("name", @switch.ApplicationName));

    private static Task<string?> ReloptionsAsync(string connectionString) =>
        LiveWait.ScalarAsync<string>(
            connectionString,
            "select pg_catalog.array_to_string(c.reloptions, ',') from pg_catalog.pg_class c "
            + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema and c.relname = @table",
            ("schema", SampleStore.DocumentSchema),
            ("table", UnhealthyDemoStore.BloatTableName));

    private static async Task<long> DeadRowsAsync(string connectionString) =>
        await LiveWait.ScalarAsync<long>(
            connectionString,
            "select coalesce(max(n_dead_tup), -1) from pg_catalog.pg_stat_user_tables where schemaname = @schema and relname = @table",
            ("schema", SampleStore.DocumentSchema),
            ("table", UnhealthyDemoStore.BloatTableName));

    private static Task<DateTime?> LastVacuumAsync(string connectionString) =>
        LiveWait.ScalarAsync<DateTime?>(
            connectionString,
            "select last_vacuum from pg_catalog.pg_stat_user_tables where schemaname = @schema and relname = @table",
            ("schema", SampleStore.DocumentSchema),
            ("table", UnhealthyDemoStore.BloatTableName));

    private static TimeSpan Interval(ConnectionsLiveHost host) =>
        host.Provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value.EffectiveHealthInterval;

    /// <summary>The number the pinned gap holds: the switch's own report names it, and the sequence is one past the mark.</summary>
    private async Task<long> HeldNumberAsync(DaemonHealthHost host)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select h.last_seq_id + 1 from " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " h where h.name = 'HighWaterMark'",
            connection);

        return await command.ExecuteScalarAsync(Token) is long next ? next : 0;
    }

    /// <summary>Marten's own record of the pin: the stuck-gap row's mark equals the mark's, and the sequence is ahead of it.</summary>
    private async Task<bool> PinnedAsync(DaemonHealthHost host)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        string progression = SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression");
        await using NpgsqlCommand command = new(
            "select g.last_seq_id = h.last_seq_id and h.last_seq_id < (select last_value from "
            + SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence") + ") from " + progression + " g, " + progression
            + " h where g.name = 'HighWaterStuckGap' and h.name = 'HighWaterMark'",
            connection);

        return await command.ExecuteScalarAsync(Token) is true;
    }

    private async Task WaitForMarkAsync(DaemonHealthHost host, Func<long, Task<bool>> condition, string what) =>
        await LiveWait.UntilAsync(
            async () =>
            {
                await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
                await using NpgsqlCommand command = new(
                    "select last_seq_id from " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " where name = 'HighWaterMark'",
                    connection);

                return await command.ExecuteScalarAsync(Token) is long mark && await condition(mark);
            },
            Settle,
            what);
}
