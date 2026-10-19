using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>K04 and C10 over stated configuration and stated probes, and over a real store's options.</summary>
public class PooledDaemonLockCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly MartenRuntime Floor = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.39.0" });

    private static readonly MartenRuntime Latest = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.50.2" });

    private static DataSourceEndpoints At(string host, int port) => new([(host, port)], Multiplexing: false);

    private static DaemonLockPooling HotCold(DataSourceEndpoints? endpoints, bool transactionScoped = false) =>
        new(DaemonMode.HotCold, transactionScoped, endpoints);

    private static HealthVerdict K04(DaemonLockPooling facts, MartenRuntime? runtime = null) =>
        new PooledDaemonLockCheck().Evaluate(facts, HealthEvaluation.At(Now, runtime ?? Latest));

    private static HealthVerdict C10(DaemonLockPooling facts, int? first, int? second, MartenRuntime? runtime = null) =>
        new PooledDaemonLockProofCheck().Evaluate(new PooledLockProbeFacts(facts, first, second), HealthEvaluation.At(Now, runtime ?? Latest));

    // ---------------------------------------------------------------------------------------------------
    // The pooler signs
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ep-cool-darkness-123456-pooler.us-east-2.aws.neon.tech", 5432, "Neon's pooled endpoint")]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 6543, "port 6543")]
    [InlineData("db.apbkobhfnmcqqzqeeqss.supabase.co", 6543, "port 6543")]
    [InlineData("studio.postgres.database.azure.com", 6432, "Azure server, its built-in PgBouncer")]
    [InlineData("pgbouncer.internal", 6432, "PgBouncer's default port")]
    public void A_documented_pooler_host_or_port_is_a_sign(string host, int port, string why)
    {
        DaemonLockPooling.SignsOf([(host, port)]).Should().ContainSingle().Which.Should().Contain(host + ":" + port).And.Contain(why);
    }

    [Theory]
    [InlineData("ep-cool-darkness-123456.us-east-2.aws.neon.tech", 5432)]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 5432)]
    [InlineData("db.apbkobhfnmcqqzqeeqss.supabase.co", 5432)]
    [InlineData("studio.postgres.database.azure.com", 5432)]
    [InlineData("localhost", 5432)]
    [InlineData("db-pooler", 5432)]
    public void A_direct_connection_or_a_session_mode_pooler_is_not_a_sign(string host, int port)
    {
        DaemonLockPooling.SignsOf([(host, port)]).Should().BeEmpty();
    }

    [Fact]
    public void Every_host_of_a_multi_host_data_source_is_looked_at()
    {
        DaemonLockPooling.SignsOf([("primary.example", 5432), ("pooled.example", 6432)]).Should().ContainSingle().Which.Should().StartWith("pooled.example:6432");
    }

    // ---------------------------------------------------------------------------------------------------
    // K04
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_HotCold_session_lock_behind_a_pooled_looking_host_is_a_warning_that_says_looks_like()
    {
        HealthFinding finding = K04(HotCold(At("ep-cool-darkness-123456-pooler.us-east-2.aws.neon.tech", 5432))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("looks like it goes through a transaction-mode pooler");
        finding.Evidence.Should().Contain(new HealthEvidence("Async mode", "HotCold"));
        finding.Evidence.Should().Contain(new HealthEvidence("UseAdvisoryLockTransaction", "false - the default on Marten 9.50.2"));
        finding.Evidence.Should().Contain(static x => x.Label == "Looks pooled" && x.Value.Contains("-pooler", StringComparison.Ordinal));

        // The host and port are the database list's, which the Configuration page shows at the database-wide gate; the
        // mode and the lock are its daemon card's, which every visitor of the scope sees.
        finding.Evidence.Single(static x => x.Label == "Looks pooled").Gate.Should().Be(HealthGate.Database);
        finding.Evidence.Where(static x => x.Label != "Looks pooled").Should().OnlyContain(static x => x.Gate == HealthGate.None);
        (finding.Title + finding.WhyItMatters + finding.Caveat).Should().NotContain("neon.tech", "the host is evidence, never prose");
        finding.Remedy!.Code.Should().Contain("opts.Events.UseAdvisoryLockTransaction = true;");
        finding.Caveat.Should().Contain("not a proof").And.Contain("C10");
    }

    /// <summary>At the floor the lock is transaction-scoped by default, so a host on 9.39 that sees K04 turned it off.</summary>
    [Fact]
    public void At_the_floor_a_session_lock_is_one_the_host_chose_and_the_evidence_says_so()
    {
        HealthFinding finding = K04(HotCold(At("pgbouncer.internal", 6432)), Floor).Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("UseAdvisoryLockTransaction", "false - set by the host; the default on Marten 9.39.0 is true"));
    }

    [Fact]
    public void The_lock_setting_is_worded_against_the_hosts_own_default()
    {
        HotCold(null, transactionScoped: true).DescribeLockSetting(Floor).Should().Be("true - the default on Marten 9.39.0");
        HotCold(null, transactionScoped: true).DescribeLockSetting(Latest).Should().Be("true - set by the host; the default on Marten 9.50.2 is false");
        HotCold(null).DescribeLockSetting(Latest).Should().Be("false - the default on Marten 9.50.2");
    }

    [Theory]
    [InlineData(DaemonMode.Solo, "Solo, which takes no advisory lock")]
    [InlineData(DaemonMode.ExternallyManaged, "ExternallyManaged")]
    [InlineData(DaemonMode.Disabled, "runs no async daemon")]
    public void Neither_check_fires_for_a_daemon_that_takes_no_HotCold_lock(DaemonMode mode, string note)
    {
        DaemonLockPooling facts = new(mode, false, At("pgbouncer.internal", 6432));

        HealthVerdict k04 = K04(facts);
        k04.Findings.Should().BeEmpty();
        k04.Note.Should().Contain(note);

        HealthVerdict c10 = C10(facts, 101, 202);
        c10.Findings.Should().BeEmpty("even a proven pooler splits nothing that takes no lock");
        c10.Note.Should().Contain(note);
    }

    [Fact]
    public void A_transaction_scoped_lock_is_quiet_behind_any_pooler()
    {
        DaemonLockPooling facts = HotCold(At("aws-0-eu-central-1.pooler.supabase.com", 6543), transactionScoped: true);

        K04(facts).Findings.Should().BeEmpty();
        K04(facts).Note.Should().Contain("transaction-scoped");
        C10(facts, 101, 202).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_direct_looking_connection_is_quiet_and_points_at_C10()
    {
        HealthVerdict verdict = K04(HotCold(At("db.internal", 5432)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("C10 tests the connection itself")
            .And.NotContain("db.internal", "a status row is shown at the check's gate, None, and a host only at the database-wide one");
    }

    [Fact]
    public void A_data_source_the_studio_cannot_read_is_quiet_and_says_so()
    {
        K04(HotCold(null)).Note.Should().Contain("not one the studio can read");
    }

    // ---------------------------------------------------------------------------------------------------
    // C10
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Two_server_processes_for_two_transactions_is_proof_and_critical()
    {
        HealthFinding finding = C10(HotCold(At("pgbouncer.internal", 15432)), 4101, 4202).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("A transaction-mode pooler is in front of this database, and the HotCold daemon's leadership lock is session-scoped");
        finding.Evidence.Should().Contain(new HealthEvidence("Server process of the first transaction", "pid 4101", HealthGate.Activity));
        finding.Evidence.Should().Contain(new HealthEvidence("Server process of the second transaction", "pid 4202", HealthGate.Activity));
        finding.Evidence.Should().Contain(new HealthEvidence("Data source connects to", "pgbouncer.internal:15432"),
            "C10 proves what K04 cannot see: a pooler on a port of its own");
        finding.Remedy.Should().BeSameAs(DaemonLockPooling.Remedy);
    }

    [Fact]
    public void The_same_server_process_twice_is_inconclusive_and_quiet()
    {
        HealthVerdict verdict = C10(HotCold(At("localhost", 5432)), 4101, 4101);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("same Postgres server process").And.Contain("not proof that there is none");
    }

    [Fact]
    public void The_process_ids_are_the_only_gated_evidence()
    {
        HealthFinding finding = C10(HotCold(At("localhost", 6432)), 1, 2).Findings.Single();

        finding.Evidence.Where(static x => x.Gate == HealthGate.Activity).Select(static x => x.Label)
            .Should().Equal("Server process of the first transaction", "Server process of the second transaction");
        (finding.Title + finding.WhyItMatters + finding.Caveat).Should().NotContain("pid ");
    }

    // ---------------------------------------------------------------------------------------------------
    // Over a real store's options, through the real sweeper, with no connection
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the wording claims about Marten's default is what this Marten does: false from 9.41, true on 9.39 and 9.40.
    /// Run at both ends of what CI tests, this is the probe that the 9.41 change is where the docs say it is.
    /// </summary>
    [Fact]
    public void Marten_s_own_default_is_the_one_the_wording_names_for_the_Marten_this_process_runs()
    {
        bool defaultValue = new StoreOptions().Events.UseAdvisoryLockTransaction;
        Version marten = MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!;

        defaultValue.Should().Be(marten < DaemonLockPooling.SessionLockDefaultSince, "Marten {0}", marten);
        new DaemonLockPooling(DaemonMode.HotCold, defaultValue, null).DescribeLockSetting(MartenRuntime.Current).Should().Contain("the default on");
    }

    [Fact]
    public async Task K04_reads_the_mode_the_lock_and_the_hosts_from_a_real_store_and_opens_nothing()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(PooledDaemonLockCheck)],
            store: static options =>
            {
                // A pooler-shaped name under .invalid (RFC 6761), so that nothing could ever resolve it, let alone connect.
                options.Connection("Host=ep-studio-h10-pooler.example.invalid;Port=5432;Database=none;Username=none;Password=none");
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            });

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Should().ContainSingle().Subject;
        result.Outcome.Should().Be(HealthCheckOutcome.Checked);
        result.Statements.Should().BeEmpty("tier 0 asks the server nothing");
        result.Findings.Should().ContainSingle().Which.Evidence.Should().Contain(static x => x.Label == "Looks pooled"
            && x.Value.StartsWith("ep-studio-h10-pooler.example.invalid:5432", StringComparison.Ordinal));
    }

    [Fact]
    public async Task C10_does_not_probe_a_multiplexing_data_source_and_says_why()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(PooledDaemonLockProofCheck)],
            store: static options =>
            {
                options.Connection(HealthHarness.RefusedConnectionString + ";Multiplexing=true");
                options.Projections.AsyncMode = DaemonMode.HotCold;
                options.Events.UseAdvisoryLockTransaction = false;
            });

        HealthCheckResult result = (await harness.SweepAsync()).Results.Single();

        result.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        result.Problem!.Reason.Should().Contain("Multiplexing=true");
        result.Statements.Should().BeEmpty();
    }

    [Fact]
    public async Task C10_on_a_store_with_no_daemon_asks_the_server_nothing()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: [typeof(PooledDaemonLockProofCheck)]);

        HealthCheckResult result = (await harness.SweepAsync()).Results.Single();

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, "the refused connection string would have failed it had it connected");
        result.Statements.Should().BeEmpty();
        result.Note.Should().Contain("runs no async daemon");
    }

    /// <summary>A configuration finding says nothing the Configuration page does not: the lock's scope is on its daemon card.</summary>
    [Fact]
    public void The_configuration_page_shows_the_leadership_locks_scope()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(HealthHarness.RefusedConnectionString);
            options.Events.UseAdvisoryLockTransaction = true;
        });

        EventStoreConfiguration events = ConfigurationDescriber.DescribeEvents(((IDocumentStore) store).Options.Events);

        ConfigurationValue row = events.Daemon.Should().ContainSingle(static x => x.Name == "Transaction-scoped leadership lock").Subject;
        row.Value.Should().Be("yes");
        row.Member.Should().Be("StoreOptions.Events.UseAdvisoryLockTransaction");
    }
}
