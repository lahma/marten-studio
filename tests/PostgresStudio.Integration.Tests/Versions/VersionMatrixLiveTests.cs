using System.Diagnostics;
using System.Globalization;
using System.Text;

using Npgsql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// The studio's core reads on PostgreSQL 14 to 18, and on 19's release candidate as a preview: every health check ends
/// read, not applicable or not checked - never failed, never on an unknown column, function, relation or syntax - as the
/// application's role and as a role with no grant at all; every database-browser, Sessions and relationship read runs;
/// the lists and the regular sweep return behind somebody's <c>ACCESS EXCLUSIVE</c> lock under a 300 ms
/// <c>lock_timeout</c>; nothing creates anything; and <see cref="PostgresFeatures" /> agrees with each server's own catalog.
/// </summary>
/// <remarks>
/// <para>
/// <b>The preview leg reports, and never fails.</b> A finding on 19's release candidate is written to the test's output
/// and the row is skipped with it as the reason, so a run on CI stays green while the report says what 19 broke; the
/// day its image is a released one (<see cref="MatrixVersion.Preview" />), the same finding fails.
/// </para>
/// <para>
/// <b>Wall clock</b> (this machine, images local): see the report's "matrix wall clock" line - the first test of each
/// major starts its server, the rest share it.
/// </para>
/// </remarks>
public class VersionMatrixLiveTests(PostgresVersionMatrix matrix)
{
    /// <summary>What a statement that names something missing, or does not parse, raises - a read that does not fit the version.</summary>
    private static readonly HashSet<string> VersionStates = ["42703", "42883", "42P01", "42601"];

    /// <summary>Every role a sweep and a read run as: the owner of the fixtures, and a role with no grant at all.</summary>
    public static TheoryData<int, MatrixRole> SweepLegs
    {
        get
        {
            TheoryData<int, MatrixRole> legs = [];

            foreach (MatrixVersion version in PostgresVersionMatrix.Versions)
            {
                legs.Add(version.Major, MatrixRole.App);
                legs.Add(version.Major, MatrixRole.Probe);
            }

            return legs;
        }
    }

    /// <summary>
    /// A regular, a deeper and a statements sweep of the core checks: each check read, was not applicable, or was not
    /// checked for a reason the host chose - never failed, and never on a version's missing column or function; a refused
    /// privilege says which grant and who gives it; and the database is exactly as it was.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(SweepLegs))]
    public async Task A_core_sweep_reads_or_says_why_not_and_creates_nothing(int major, MatrixRole role)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        IReadOnlySet<string> before = await server.SnapshotAsync();
        await using MatrixHost host = await MatrixHost.CreateAsync(server, role);

        List<string> problems = [];
        StringBuilder report = new();

        foreach (HealthSweepKind kind in Enum.GetValues<HealthSweepKind>())
        {
            HealthSweep sweep = await host.SweepAsync(kind);

            foreach (HealthCheckResult result in sweep.Results)
            {
                report.Append(CultureInfo.InvariantCulture, $"{kind} {result.Check.Code} {result.Outcome}");
                report.AppendLine(result.Problem is { } p ? " - " + p.SqlState + " " + p.Reason : string.Empty);

                if (result.Outcome == HealthCheckOutcome.Failed)
                {
                    problems.Add($"{result.Check.Code} failed ({kind}): {result.Problem?.SqlState} {result.Problem?.Reason} {result.Problem?.PostgresText}");
                }

                if (result.Problem?.SqlState is { } state && VersionStates.Contains(state))
                {
                    problems.Add($"{result.Check.Code} ran a statement this version does not have ({state}): {result.Problem.PostgresText}");
                }

                if (result.Problem?.SqlState == PostgresErrorCodes.InsufficientPrivilege
                    && (result.Outcome != HealthCheckOutcome.Unavailable || !result.Problem.Reason.StartsWith("Not checked: Postgres refused role ", StringComparison.Ordinal)))
                {
                    problems.Add($"{result.Check.Code} was refused a privilege and did not say which grant: {result.Outcome} {result.Problem.Reason}");
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine("PostgreSQL {0} as {1}:\n{2}", server.ServerVersion, role, report);

        (await server.SnapshotAsync()).Should().BeEquivalentTo(before, "a read path creates nothing, on any version");
        Report(server, problems);
    }

    /// <summary>Every <see cref="MatrixReads" /> read runs on every version, as the owner and as a role with no grant.</summary>
    [PostgresTheory]
    [MemberData(nameof(SweepLegs))]
    public async Task Every_matrix_read_runs(int major, MatrixRole role)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        IReadOnlySet<string> before = await server.SnapshotAsync();
        await using MatrixHost host = await MatrixHost.CreateAsync(server, role);

        List<string> problems = [];

        foreach (MatrixRead read in MatrixReads.All)
        {
            if (await host.RunAsync(read, TimeSpan.FromSeconds(3)) is { } failure)
            {
                problems.Add(read.Name + ": " + Describe(failure));
            }
        }

        (await server.SnapshotAsync()).Should().BeEquivalentTo(before, "a read path creates nothing, on any version");
        Report(server, problems);
    }

    /// <summary>
    /// D27 on every version: while a second session holds <c>ACCESS EXCLUSIVE</c> on a table and on a partition - what a
    /// migration's <c>ALTER</c> takes - every lock-free read and the regular sweep return under a 300 ms
    /// <c>lock_timeout</c>, so neither queues behind the migration (B0's "waits" method, the one that is proof).
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task Behind_an_access_exclusive_lock_every_list_and_the_sweep_return_under_300ms(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        TimeSpan lockTimeout = TimeSpan.FromMilliseconds(300);
        CancellationToken token = TestContext.Current.CancellationToken;

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App);
        await using NpgsqlConnection holder = await server.OpenAsync(cancellationToken: token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(token);

        await using (NpgsqlCommand lockThem = new("lock table store.child in access exclusive mode; lock table store.r_2026_10 in access exclusive mode", holder, held))
        {
            await lockThem.ExecuteNonQueryAsync(token);
        }

        List<string> problems = [];
        Stopwatch clock = Stopwatch.StartNew();

        foreach (MatrixRead read in MatrixReads.All.Where(static x => x.LockFree))
        {
            if (await host.RunAsync(read, lockTimeout) is { } failure)
            {
                problems.Add(read.Name + " behind the lock: " + Describe(failure));
            }
        }

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular, lockTimeout);

        problems.AddRange(sweep.Results
            .Where(static x => x.Problem?.SqlState == PostgresErrorCodes.LockNotAvailable || x.Outcome == HealthCheckOutcome.Failed)
            .Select(static x => $"{x.Check.Code} behind the lock: {x.Outcome} {x.Problem?.SqlState} {x.Problem?.Reason}"));

        // The index checks read the definitions of store.child's indexes, which deparse would open the locked table for:
        // they stand down for the sweep, saying so, rather than queue (HealthTableQueries.IndexesSql).
        string[] standingDown = [.. sweep.Results
            .Where(static x => x.Outcome == HealthCheckOutcome.Unavailable && x.Problem?.Reason.Contains("locked for a schema change", StringComparison.Ordinal) == true)
            .Select(static x => x.Check.Code)];

        if (!standingDown.Contains("T03"))
        {
            problems.Add("T03 did not stand down behind the lock on store.child: " + string.Join(", ", sweep.Results.Select(static x => x.Check.Code + " " + x.Outcome)));
        }

        TestContext.Current.TestOutputHelper?.WriteLine("PostgreSQL {0}: lists and the regular sweep behind the lock in {1:0} ms", server.ServerVersion, clock.Elapsed.TotalMilliseconds);

        await held.RollbackAsync(token);
        Report(server, problems);
    }

    /// <summary>
    /// The gate table against each server's own catalog: every feature that can be seen in the catalog is present exactly
    /// from the version <see cref="PostgresFeatures" /> says - which is also what proves the 19 block on 19's release
    /// candidate - and every feature that cannot is named here as such, so a new row must say which it is.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_gate_table_agrees_with_each_servers_catalog(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<string> problems = [];

        foreach (PostgresFeatureGate gate in PostgresFeatures.Gates)
        {
            if (Unprobed.Contains(gate.Feature))
            {
                continue;
            }

            bool present = await PresentAsync(connection, gate.Feature);
            bool expected = PostgresFeatures.Has(gate.Feature, server.VersionNumber);

            if (present != expected)
            {
                problems.Add($"{gate.Feature}: the gate says {(expected ? "present" : "absent")} from {gate.Since}, and PostgreSQL {server.ServerVersion} has it {(present ? "present" : "absent")}");
            }
        }

        Report(server, problems);
    }

    [Fact]
    public void Every_feature_is_probed_against_the_catalog_or_named_as_one_that_cannot_be()
    {
        PostgresFeatures.Gates.Select(static x => x.Feature).Should().OnlyContain(
            static x => Unprobed.Contains(x) || Probes.ContainsKey(x),
            "a new gate row says how the matrix proves it, or that it cannot");
        Unprobed.Should().NotIntersectWith(Probes.Keys);
    }

    /// <summary>The features no catalog read can show: behaviour after a restart, and a JIT plan's shape.</summary>
    private static readonly HashSet<PostgresFeature> Unprobed =
    [
        PostgresFeature.StatsPersistAcrossCleanRestart,
        PostgresFeature.ExplainGenerationObject,
    ];

    /// <summary>How the matrix sees each feature, as one boolean query; the null ones are answered in code.</summary>
    private static readonly Dictionary<PostgresFeature, string?> Probes = new()
    {
        [PostgresFeature.StatementsInfo] = Relation("public.pg_stat_statements_info"),
        [PostgresFeature.StatementsToplevel] = Column("public.pg_stat_statements", "toplevel"),
        [PostgresFeature.ActivityQueryId] = Column("pg_catalog.pg_stat_activity", "query_id"),
        [PostgresFeature.PgStatWal] = Relation("pg_catalog.pg_stat_wal"),
        [PostgresFeature.ReplicationSlotStats] = Relation("pg_catalog.pg_stat_replication_slots"),
        [PostgresFeature.WalReplayPauseState] = Function("pg_get_wal_replay_pause_state"),
        [PostgresFeature.LocksWaitStart] = Column("pg_catalog.pg_locks", "waitstart"),
        [PostgresFeature.DatabaseSessionCounters] = Column("pg_catalog.pg_stat_database", "sessions"),
        [PostgresFeature.VacuumFailsafeAge] = Setting("vacuum_failsafe_age"),
        [PostgresFeature.DatabaseOwnerRole] = Role("pg_database_owner"),
        [PostgresFeature.ReadAllDataRole] = Role("pg_read_all_data"),
        [PostgresFeature.ReltuplesUnknownIsMinusOne] = "select c.reltuples = -1 from pg_catalog.pg_class c where c.oid = 'store.l'::pg_catalog.regclass",
        [PostgresFeature.StatementsJit] = Column("public.pg_stat_statements", "jit_functions"),
        [PostgresFeature.ForceNextFlush] = Function("pg_stat_force_next_flush"),
        [PostgresFeature.PgStatIo] = Relation("pg_catalog.pg_stat_io"),
        [PostgresFeature.LastScanColumns] = Column("pg_catalog.pg_stat_all_tables", "last_seq_scan"),
        [PostgresFeature.NewPageUpdates] = Column("pg_catalog.pg_stat_all_tables", "n_tup_newpage_upd"),
        [PostgresFeature.ExplainGenericPlan] = null,
        [PostgresFeature.SlotConflicting] = Column("pg_catalog.pg_replication_slots", "conflicting"),
        [PostgresFeature.ReservedConnections] = Setting("reserved_connections"),
        [PostgresFeature.PgStatCheckpointer] = Relation("pg_catalog.pg_stat_checkpointer"),
        [PostgresFeature.SlotInactiveSince] = Column("pg_catalog.pg_replication_slots", "inactive_since"),
        [PostgresFeature.ProgressIndexesTotal] = Column("pg_catalog.pg_stat_progress_vacuum", "indexes_total"),
        [PostgresFeature.StatementsSharedBlkTimeNames] = Column("public.pg_stat_statements", "shared_blk_read_time"),
        [PostgresFeature.WaitEventsView] = Relation("pg_catalog.pg_wait_events"),
        [PostgresFeature.MaintainRole] = Role("pg_maintain"),
        [PostgresFeature.RelAllFrozen] = Column("pg_catalog.pg_class", "relallfrozen"),
        [PostgresFeature.VacuumMaxThreshold] = Setting("autovacuum_vacuum_max_threshold"),
        [PostgresFeature.PgStatIoBytes] = Column("pg_catalog.pg_stat_io", "read_bytes"),
        [PostgresFeature.PgStatIoWal] = "select not " + Column("pg_catalog.pg_stat_wal", "wal_write")[7..],
        [PostgresFeature.CheckpointerNumDone] = Column("pg_catalog.pg_stat_checkpointer", "num_done"),
        [PostgresFeature.NotNullConstraints] =
            "select exists (select 1 from pg_catalog.pg_constraint k where k.conrelid = 'store.child'::pg_catalog.regclass and k.contype = 'n')",
        [PostgresFeature.SequencesViewLocksAll] =
            "select pg_catalog.pg_get_viewdef('pg_catalog.pg_sequences'::pg_catalog.regclass) not like '%has_sequence_privilege%'",
        [PostgresFeature.ExplainFractionalRows] = null,
        [PostgresFeature.ExplainBuffersByDefault] = null,
        [PostgresFeature.IdleSlotTimeout] = Setting("idle_replication_slot_timeout"),
        [PostgresFeature.ChecksumsDefaultOn] = "select pg_catalog.current_setting('data_checksums') = 'on'",
        [PostgresFeature.TotalVacuumTime] = Column("pg_catalog.pg_stat_all_tables", "total_vacuum_time"),
        [PostgresFeature.StatsResetPerTable] = Column("pg_catalog.pg_stat_all_tables", "stats_reset"),
        [PostgresFeature.AutovacuumScores] = Relation("pg_catalog.pg_stat_autovacuum_scores"),
        [PostgresFeature.MultixactStats] = Function("pg_get_multixact_stats"),
        [PostgresFeature.PgStatLock] = Relation("pg_catalog.pg_stat_lock"),
        [PostgresFeature.JitOffByDefault] = "select coalesce((select s.boot_val = 'off' from pg_catalog.pg_settings s where s.name = 'jit'), false)",
        [PostgresFeature.MaxLocksDefault128] = "select (select s.boot_val from pg_catalog.pg_settings s where s.name = 'max_locks_per_transaction') = '128'",
        [PostgresFeature.WaitTypeBufferRenamed] = null,
        [PostgresFeature.SubscriptionSyncTableErrorCount] = Column("pg_catalog.pg_stat_subscription_stats", "sync_table_error_count"),
    };

    private static async Task<bool> PresentAsync(NpgsqlConnection connection, PostgresFeature feature)
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        switch (feature)
        {
            case PostgresFeature.ExplainGenericPlan:
                // No $1: Npgsql's extended protocol would bind no value to it. Below 16 the option itself is 42601.
                return await SucceedsAsync(connection, "explain (generic_plan) select * from store.child where id = 1");

            case PostgresFeature.ExplainFractionalRows:
                return (await ExplainAsync(connection)).Contains("\"Actual Rows\": 1.00", StringComparison.Ordinal);

            case PostgresFeature.ExplainBuffersByDefault:
                return (await ExplainAsync(connection)).Contains("\"Shared Hit Blocks\"", StringComparison.Ordinal);

            case PostgresFeature.WaitTypeBufferRenamed:
                return await ScalarAsync(connection, Relation("pg_catalog.pg_wait_events"))
                    && await ScalarAsync(connection, "select exists (select 1 from pg_catalog.pg_wait_events w where w.type = 'Buffer')");

            default:
                return await ScalarAsync(connection, Probes[feature]!);
        }

        async Task<bool> SucceedsAsync(NpgsqlConnection c, string sql)
        {
            await using NpgsqlCommand command = new(sql, c);

            try
            {
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                }

                return true;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SyntaxError)
            {
                return false;
            }
        }

        async Task<string> ExplainAsync(NpgsqlConnection c)
        {
            await using NpgsqlCommand command = new("explain (analyze, format json) select 1 from store.child where id = 1", c);
            return Convert.ToString(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static async Task<bool> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is true;
    }

    private static string Relation(string name) => "select pg_catalog.to_regclass('" + name + "') is not null";

    private static string Column(string relation, string column) =>
        "select exists (select 1 from pg_catalog.pg_attribute a where a.attrelid = pg_catalog.to_regclass('" + relation + "') and a.attname = '" + column + "' and not a.attisdropped)";

    private static string Function(string name) => "select exists (select 1 from pg_catalog.pg_proc p where p.proname = '" + name + "')";

    private static string Setting(string name) => "select exists (select 1 from pg_catalog.pg_settings s where s.name = '" + name + "')";

    private static string Role(string name) => "select exists (select 1 from pg_catalog.pg_roles r where r.rolname = '" + name + "')";

    private static string Describe(Exception failure) => failure switch
    {
        PostgresException postgres => postgres.SqlState + " " + postgres.MessageText,
        _ => failure.GetType().Name + ": " + failure.Message + (failure.InnerException is PostgresException inner ? " (" + inner.SqlState + " " + inner.MessageText + ")" : string.Empty),
    };

    /// <summary>Fails with every problem - or, on the preview leg, reports them and skips (it never fails a run).</summary>
    private static void Report(MatrixServer server, List<string> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        string text = "PostgreSQL " + server.ServerVersion + " (" + server.Version.Image + "):\n" + string.Join("\n", problems);

        if (server.Version.Preview)
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Preview leg findings - {0}", text);
            Assert.Skip("Preview leg (" + server.Version.Image + "), reported and not failed: " + text);
        }

        problems.Should().BeEmpty(text);
    }
}
