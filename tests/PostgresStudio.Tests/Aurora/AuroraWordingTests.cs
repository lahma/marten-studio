using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// W5-AUR: what changes on Aurora - C03's remedy against the capacity (A6, A8) and its role and database limits, C08's temp
/// files (A6), C11's cost of a miss (A5), <c>pg_stat_statements</c>' preload (A7) and R03's <c>aurora-reader</c> view (A3) -
/// and that every other platform's text is what it was.
/// </summary>
public class AuroraWordingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly PostgresPlatform Aurora = new(PostgresPlatformKind.AmazonAurora, "marker");

    private static HealthEvaluation At() => HealthEvaluation.At(Now);

    private static ServerConnectionLimits Limits(long clients = 96, long thisDatabase = 10, int max = 100) => new(clients, thisDatabase, max, 3, 0, 170004);

    private static CapacityReading Acu(double maximum = 16, bool? ioOptimized = null, double minimum = 0.5) =>
        new(PostgresComputeCapacity.AuroraServerlessV2(4.5, minimum, maximum, Now.AddMinutes(-1)) with { IoOptimized = ioOptimized }, Now, null, true);

    private static HealthRemedy AuroraC03(ServerConnectionLimits limits, CapacityReading? capacity) =>
        new ServerConnectionHeadroomCheck()
            .Evaluate(new ServerConnectionHeadroomFacts(limits, Aurora, capacity, null), At())
            .Findings.Single().Remedy!;

    private const string Override = "A max_connections set in a custom DB cluster or DB parameter group replaces Aurora's formula";

    // ---- C03 on Aurora: the capacity, not postgresql.conf (A6, A8, W5-AUR-fix F1) ---------------------

    [Fact]
    public void With_an_ACU_reading_below_Auroras_caps_C03_says_raise_the_clusters_maximum_capacity_and_names_it()
    {
        foreach (double minimum in new[] { 0.5, 1 })
        {
            HealthFinding finding = new ServerConnectionHeadroomCheck()
                .Evaluate(new ServerConnectionHeadroomFacts(Limits(), Aurora, Acu(16, minimum: minimum), 100), At())
                .Findings.Single();

            finding.Variant.Should().Be("server", "the API packet's split names the server's own finding's kind, as the role's and the database's are");
            finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
            finding.Remedy.Code.Should().Be("Raise the cluster's maximum capacity (now 16 ACU)", "max_connections 100 is below the 2,000 cap a low minimum sets");
            finding.Remedy.Explanation.Should().StartWith("On Aurora Serverless v2 max_connections follows the maximum capacity:")
                .And.Contain("Performance and scaling for Aurora serverless").And.Contain(Override).And.NotContain("postgresql.conf");
        }

        new ServerConnectionHeadroomCheck()
            .Evaluate(new ServerConnectionHeadroomFacts(Limits(), Aurora, Acu(16), 100), At())
            .Findings.Single().Evidence.Should().Contain(new HealthEvidence("Compute capacity", "4.5 ACU (min 0.5, max 16), measured 11:59 UTC"));
    }

    [Theory]
    [InlineData(0.5, 16)]
    [InlineData(0, 16)]
    [InlineData(0.5, 64)]
    public void A_minimum_of_half_an_ACU_or_less_at_the_2000_cap_says_raise_the_minimum(double minimum, double maximum)
    {
        HealthRemedy remedy = AuroraC03(Limits(clients: 1_990, max: 2_000), Acu(maximum, minimum: minimum));

        remedy.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
        remedy.Code.Should().Be(
            "Raise the cluster's minimum capacity to 1 ACU or more (now " + CapacityReading.Amount(minimum) + " ACU)",
            "a maximum adds nothing while the minimum caps max_connections at 2,000");
        remedy.Explanation.Should().StartWith("max_connections is 2,000, Aurora's cap for a minimum capacity of 0 or 0.5 ACU")
            .And.Contain("once the instances reboot").And.Contain(Override).And.NotContain("postgresql.conf");
    }

    [Theory]
    [InlineData(1, 64, 3_000)]
    [InlineData(1, 32, 5_000)]
    [InlineData(0.5, 16, 5_000)]
    public void At_Auroras_ceiling_C03_says_fewer_connections_or_a_pooler(double minimum, double maximum, int maxConnections)
    {
        HealthRemedy remedy = AuroraC03(Limits(clients: maxConnections - 10, max: maxConnections), Acu(maximum, minimum: minimum));

        remedy.Code.Should().Be(
            "Lower the pool ceilings of the applications that share the cluster, or put RDS Proxy or PgBouncer in front of it",
            "from 32 ACU, or at 5,000, more capacity adds no connections");
        remedy.Explanation.Should().Contain("with a maximum capacity of " + CapacityReading.Amount(maximum) + " ACU: at Aurora's ceiling")
            .And.Contain(Override).And.NotContain("Raise the cluster's maximum");
    }

    [Fact]
    public void Without_a_reading_C03_on_Aurora_says_both_and_asks_for_a_capacity_source()
    {
        foreach (CapacityReading? capacity in new CapacityReading?[] { null, new(null, Now, null, false), new(PostgresComputeCapacity.Of(8, 8, 8, "vCPU", Now), Now, null, true) })
        {
            foreach (int maxConnections in new[] { 100, 2_000 })
            {
                HealthRemedy remedy = AuroraC03(Limits(clients: maxConnections - 4, max: maxConnections), capacity);

                remedy.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
                remedy.Code.Should().Be(
                    "On Aurora Serverless v2: raise the cluster's maximum capacity (ACU), or its minimum to 1 ACU or more if it is 0 or 0.5\n"
                    + "On a provisioned instance: a larger instance class, or max_connections in the DB cluster parameter group");
                remedy.Explanation.Should().Contain("caps it at 2,000").And.Contain("5,000, Aurora's ceiling").And.Contain(Override)
                    .And.Contain("register an IPostgresCapacitySource (docs/aurora.md)").And.NotContain("postgresql.conf");
            }

            HealthRemedy ceiling = AuroraC03(Limits(clients: 4_990, max: 5_000), capacity);
            ceiling.Code.Should().StartWith("Lower the pool ceilings", "5,000 is Aurora's ceiling, whatever the capacity");
            ceiling.Explanation.Should().StartWith("max_connections is 5,000: at Aurora's ceiling.");
        }
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted)]
    [InlineData(PostgresPlatformKind.AmazonRds)]
    [InlineData(PostgresPlatformKind.Azure)]
    public void Off_Aurora_C03_is_the_finding_it_always_was_with_a_capacity_reading_as_evidence_only(PostgresPlatformKind kind)
    {
        PostgresPlatform platform = kind == PostgresPlatformKind.SelfHosted ? PostgresPlatform.SelfHosted : new PostgresPlatform(kind, "marker");

        HealthFinding plain = new ServerConnectionHeadroomCheck().Evaluate(Limits(), At()).Findings.Single();
        HealthFinding there = new ServerConnectionHeadroomCheck().Evaluate(new ServerConnectionHeadroomFacts(Limits(), platform, null, null), At()).Findings.Single();

        there.Should().BeEquivalentTo(plain, "every word of the non-Aurora finding stays as it was");
        there.Remedy!.Kind.Should().Be(HealthRemedyKind.Configuration);

        HealthFinding withCapacity = new ServerConnectionHeadroomCheck()
            .Evaluate(new ServerConnectionHeadroomFacts(Limits(), platform, new CapacityReading(PostgresComputeCapacity.Of(2, 1, 4, "vCPU", Now), Now, null, true), null), At())
            .Findings.Single();

        withCapacity.Remedy.Should().Be(plain.Remedy);
        withCapacity.Evidence.Select(static x => x.Label).Should().Equal("In use", "To this database", "Available to ordinary roles", "Compute capacity");
    }

    // ---- C03's role and database limits (the 2026-10-26 health-gap analysis) --------------------------

    [Theory]
    [InlineData(40, null)]
    [InlineData(41, "Warning")]
    [InlineData(47, "Warning")]
    [InlineData(48, "Critical")]
    public void The_login_roles_connection_limit_has_the_same_lines(long sessions, string? expected)
    {
        ServerConnectionLimits limits = Limits(clients: 50) with { LoginRole = "app", RoleLimit = 50, RoleSessions = sessions };

        HealthVerdict verdict = new ServerConnectionHeadroomCheck().Evaluate(new ServerConnectionHeadroomFacts(limits, PostgresPlatform.SelfHosted, null, 100), At());

        if (expected is null)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain("Role app has " + sessions + " of its 50 connections.")
                .And.Contain("This process alone may open 100 connections as role app, which may open 50.");
            return;
        }

        HealthFinding finding = verdict.Findings.Single();
        finding.Variant.Should().Be("role");
        finding.Severity.Should().Be(Enum.Parse<HealthSeverity>(expected));
        finding.WhyItMatters.Should().Contain("too many connections for role");
        finding.Evidence.Should().Contain(new HealthEvidence("This process's pool", "this process alone may open 100 connections as role app, which may open 50"));
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Configuration);
        finding.Remedy.Code.Should().Be("Maximum Pool Size=40");
        finding.Remedy.Explanation.Should().Contain("ALTER ROLE ... CONNECTION LIMIT").And.StartWith("This process's pool ceiling is 100 now.");
    }

    [Fact]
    public void The_nearest_limit_names_the_finding()
    {
        ServerConnectionLimits limits = Limits(clients: 90, thisDatabase: 19) with { LoginRole = "app", RoleLimit = 100, RoleSessions = 85, DatabaseLimit = 20 };

        HealthFinding finding = new ServerConnectionHeadroomCheck().Evaluate(limits, At()).Findings.Single();

        finding.Variant.Should().Be("database", "19 of 20 (95%) is nearer than the server's 90 of 97 or the role's 85 of 100");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.WhyItMatters.Should().Contain("too many connections for database");
        finding.Remedy!.Code.Should().Be("Maximum Pool Size=16");
    }

    [Fact]
    public void Neither_limit_binds_a_superuser_and_none_set_is_the_server_alone()
    {
        ServerConnectionLimits superuser = Limits(clients: 10, thisDatabase: 10) with { LoginRole = "postgres", RoleLimit = 5, RoleSessions = 10, DatabaseLimit = 5, RoleSuperuser = true };

        HealthVerdict verdict = new ServerConnectionHeadroomCheck().Evaluate(superuser, At());
        verdict.Findings.Should().BeEmpty("Postgres enforces neither limit against a superuser (S04 says why that is a problem of its own)");
        verdict.Note.Should().Be("10 of 97 connections an ordinary role may open are in use on this server.", "the note is the server's alone, as it always was");
    }

    [Fact]
    public void C03_lists_its_variants()
    {
        HealthCheckRegistry.Describe(typeof(ServerConnectionHeadroomCheck)).Variants.Should().Equal("server", "role", "database");
    }

    // ---- C08: temporary files use the instance's local storage (A6) ---------------------------------

    [Fact]
    public void On_Aurora_C08_says_where_temporary_files_go_and_nowhere_else_does()
    {
        DatabaseCounterCheck.AuroraTemp(Aurora).Should().Contain("temporary files use the instance's local storage, not the cluster volume")
            .And.Contain("FreeLocalStorage").And.Contain("Managing temporary files with PostgreSQL");

        DatabaseCounterCheck.AuroraTemp(null).Should().BeEmpty();
        DatabaseCounterCheck.AuroraTemp(PostgresPlatform.SelfHosted).Should().BeEmpty();
        DatabaseCounterCheck.AuroraTemp(new PostgresPlatform(PostgresPlatformKind.AmazonRds, "marker")).Should().BeEmpty();
    }

    // ---- C11: what a miss costs on Aurora (A5) ------------------------------------------------------------

    [Fact]
    public void C11_words_the_cost_of_a_miss_by_the_capacity_sources_IoOptimized()
    {
        StatisticsReading reading = new() { ReadAt = Now };

        CacheHitCheck.PlatformCost(new CacheHitFacts(reading, PostgresPlatform.SelfHosted)).Should().BeEmpty();
        CacheHitCheck.PlatformCost(new CacheHitFacts(reading, new PostgresPlatform(PostgresPlatformKind.AmazonRds, "marker"))).Should().BeEmpty();

        CacheHitCheck.PlatformCost(new CacheHitFacts(reading, Aurora) { Capacity = Acu(ioOptimized: true) })
            .Should().Contain("Aurora I/O-Optimized").And.Contain("not money").And.NotContain("Aurora Standard");
        CacheHitCheck.PlatformCost(new CacheHitFacts(reading, Aurora) { Capacity = Acu(ioOptimized: false) })
            .Should().Contain("Aurora Standard").And.Contain("billed I/O").And.NotContain("I/O-Optimized");

        foreach (CapacityReading? unsaid in new CapacityReading?[] { null, Acu(ioOptimized: null) })
        {
            CacheHitCheck.PlatformCost(new CacheHitFacts(reading, Aurora) { Capacity = unsaid })
                .Should().Contain("on Aurora Standard reads that miss the cache are billed I/O requests, on Aurora I/O-Optimized they are not")
                .And.Contain("IoOptimized");
        }
    }

    // ---- A7: pg_stat_statements is preloaded on Aurora ----------------------------------------------------

    [Fact]
    public void On_Aurora_a_missing_pg_stat_statements_says_it_is_preloaded_and_what_to_do_if_not()
    {
        string aurora = PostgresGrants.ExtensionNotInstalled("pg_stat_statements", false, Aurora);

        aurora.Should().StartWith("Not checked: this needs the pg_stat_statements extension, which this database does not have. On Amazon Aurora PostgreSQL the master user");
        aurora.Should().EndWith(
            " On Aurora pg_stat_statements is preloaded by Aurora's default DB cluster parameter group (\"Typically, the default DB cluster "
            + "parameter group loads only the pg_stat_statements\" - Working with extensions and foreign data wrappers, Aurora User Guide, "
            + "read 2026-10-26), so CREATE EXTENSION pg_stat_statements is usually all it takes; if it then reports that the library is "
            + "not loaded, add it to shared_preload_libraries in the DB cluster parameter group and reboot the instances.");

        PostgresGrants.ExtensionNotInstalled("pg_stat_statements", false, new PostgresPlatform(PostgresPlatformKind.AmazonRds, "marker"))
            .Should().Be("Not checked: this needs the pg_stat_statements extension, which this database does not have. On Amazon RDS for "
                + "PostgreSQL the master user (a member of rds_superuser) can run CREATE EXTENSION pg_stat_statements, where the engine version offers it.");
        PostgresGrants.ExtensionNotInstalled("pgstattuple", false, Aurora).Should().NotContain("preloaded");
    }

    // ---- R03 aurora-reader (A3) ----------------------------------------------------------------------------

    private static StandbyLagFacts AuroraFacts(params string[] rows) =>
        new(new PostgresServerFacts(Now, 160004, "16.4", Aurora),
            new ReplicationRole(false, null, null, null, null, null, null, Now),
            null,
            new PostgresGrants("app", false, []))
        {
            Aurora = AuroraReplicaStatus.FromJson(Now, rows),
        };

    private const string Writer = """{"server_id": "db-instance-1", "session_id": "MASTER_SESSION_ID", "replica_lag_in_msec": null}""";

    private static string Reader(string name, double lagMs) =>
        "{\"server_id\": \"" + name + "\", \"session_id\": \"u-" + name + "\", \"replica_lag_in_msec\": "
        + lagMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"cur_replay_latency_in_usec\": 120}";

    [Theory]
    [InlineData(13, null)]
    [InlineData(299_999, null)]
    [InlineData(300_000, "Warning")]
    [InlineData(400_000, "Warning")]
    [InlineData(3_600_000, "Critical")]
    public void An_Aurora_reader_is_judged_on_its_replica_lag_in_time_alone(double lagMs, string? expected)
    {
        HealthVerdict verdict = new StandbyLagCheck().Evaluate(AuroraFacts(Writer, Reader("db-instance-2", lagMs)), At());

        if (expected is null)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().StartWith("1 Aurora reader, none behind: the furthest, db-instance-2, is ");
            return;
        }

        HealthFinding finding = verdict.Findings.Single();
        finding.Variant.Should().Be("aurora-reader");
        finding.Severity.Should().Be(Enum.Parse<HealthSeverity>(expected));
        finding.Title.Should().StartWith("An Aurora reader is ").And.EndWith(" behind");
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in WAL", "not measured: this source reports time only"));
        finding.Evidence.Should().Contain(new HealthEvidence("Thresholds", "Warning at 5 min; Critical at 1 h 0 min"));

        HealthEvidence named = finding.Evidence.Single(static x => x.Label.StartsWith("Replica 1", StringComparison.Ordinal));
        named.Gate.Should().Be(HealthGate.Database, "an instance id is the cluster's own name for an instance, not a session's (D29)");
        named.Value.Should().StartWith("db-instance-2, ");
    }

    [Fact]
    public void The_writer_is_never_judged_and_a_cluster_without_readers_says_so()
    {
        HealthVerdict alone = new StandbyLagCheck().Evaluate(AuroraFacts(Writer), At());

        alone.Findings.Should().BeEmpty();
        alone.Note.Should().Be("This Aurora cluster has no reader instance: aurora_replica_status() lists only its writer, db-instance-1.");
    }

    [Fact]
    public void Several_readers_behind_are_one_finding_worst_first()
    {
        HealthFinding finding = new StandbyLagCheck()
            .Evaluate(AuroraFacts(Writer, Reader("db-instance-2", 400_000), Reader("db-instance-3", 4_000_000), Reader("db-instance-4", 20)), At())
            .Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().StartWith("2 Aurora readers are ").And.EndWith(" behind at most");
        finding.Evidence.Should().Contain(new HealthEvidence("Behind", "2 of 3 replicas judged"));
        finding.Evidence.Where(static x => x.Label.StartsWith("Replica", StringComparison.Ordinal)).Select(static x => x.Value.Split(',')[0])
            .Should().Equal("db-instance-3", "db-instance-2");
    }

    [Fact]
    public void A_standbys_evidence_is_still_the_activity_gates()
    {
        HealthFinding finding = StandbyLagCheck.Finding(
            "primary-view",
            [new StandbyLagReading("'replica'", null, TimeSpan.FromHours(2))],
            1,
            "why",
            "caveat",
            [])!;

        finding.Title.Should().Be("A standby is 2 h 0 min behind");
        finding.Evidence.Single(static x => x.Label.StartsWith("Replica 1", StringComparison.Ordinal)).Gate.Should().Be(HealthGate.Activity);
        finding.Evidence.Should().Contain(new HealthEvidence("Thresholds", "Warning at 5 min or 1.0 GiB; Critical at 1 h 0 min"));
    }
}
