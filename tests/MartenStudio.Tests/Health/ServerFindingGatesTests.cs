using JasperFx.Events.Daemon;

using MartenStudio.Services.Health.Checks;
using PostgresStudio;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Acceptance 2: a replication slot's name, its plugin and its database - and C10's two process ids - are shown to a
/// visitor through the <c>ViewDatabaseActivity</c> gate and to nobody else, while the findings, their counts and their
/// sizes are shown to both; S01 is shown to a visitor the store policy admits for one tenant, R01 is not. Through the
/// real <see cref="HealthService" />, over the real evaluators fed stated facts.
/// </summary>
public class ServerFindingGatesTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly Type[] Stated = [typeof(StatedSlotsCheck), typeof(StatedProbeCheck), typeof(StatedVersionCheck)];

    /// <summary>Every identifying value the stated facts carry: two slots' names, a plugin, a database, two process ids.</summary>
    private static readonly string[] Identifying = ["debezium_orders", "old_standby", "pgoutput", "orders_db", "pid 4101", "pid 4202"];

    /// <summary>Another database's logical slot, its plugin and its database: named to nobody here, whatever their gate.</summary>
    private const string OtherSlot = "debezium_acme";

    private const string OtherDatabase = "tenant_acme";

    private static readonly string[] AnotherDatabases = [OtherSlot, OtherDatabase, "wal2json"];

    private static async Task<HealthReport> ReportAsync(HealthHarness harness, StudioScope? scope = null) =>
        await harness.Health.GetReportAsync(scope ?? harness.Scope, HealthReportRequest.Regular, Token);

    [Fact]
    public void The_stated_facts_carry_every_identifying_value_so_the_tests_below_are_not_vacuous()
    {
        HealthVerdict slots = new ReplicationSlotCheck().Evaluate(StatedSlotsCheck.Facts(DateTimeOffset.UnixEpoch), HealthEvaluation.At(DateTimeOffset.UnixEpoch));
        HealthVerdict probe = new PooledDaemonLockProofCheck().Evaluate(StatedProbeCheck.Facts, HealthEvaluation.At(DateTimeOffset.UnixEpoch));

        string all = string.Join("\n", slots.Findings.Concat(probe.Findings).SelectMany(static x => x.Evidence).Select(static x => x.Value));

        foreach (string value in Identifying)
        {
            all.Should().Contain(value);
        }
    }

    [Fact]
    public async Task Without_the_capability_the_slots_and_the_processes_are_withheld_and_the_findings_are_still_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = new MartenStudioCapabilities());

        HealthReport report = await ReportAsync(harness);

        report.Findings.Select(static x => x.Code).Should().BeEquivalentTo(["R91", "R91", "C96", "S91"],
            "the kinds, the counts, the sizes and the proof are the database's");

        foreach (HealthReportFinding finding in report.Findings)
        {
            string shown = string.Join("\n", finding.Evidence.Select(static x => x.Value))
                + "\n" + finding.Finding.Title + "\n" + finding.MuteKey + "\n" + finding.Anchor;

            foreach (string value in Identifying)
            {
                shown.Should().NotContain(value, "{0} withholds it without the capability", finding.Code);
            }
        }

        report.Findings.Where(static x => x.Code is "R91" or "C96").SelectMany(static x => x.Evidence)
            .Where(static x => x.Value.StartsWith("withheld - ", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(static x => x.Value.Contains("MartenStudioOptions.Capabilities.ViewDatabaseActivity", StringComparison.Ordinal));

        HealthReportFinding inactive = report.Findings.Single(static x => x.Finding.Subject == "inactive");
        inactive.Evidence.Should().Contain(new HealthEvidence("WAL they hold", "5.0 GiB"), "a size is the database's, another database's slot's included");
    }

    [Fact]
    public async Task With_the_capability_and_the_write_policy_every_slot_and_process_is_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        HealthReport report = await ReportAsync(harness);

        string shown = string.Join("\n", report.Findings.SelectMany(static x => x.Evidence).Select(static x => x.Value));

        foreach (string value in Identifying)
        {
            shown.Should().Contain(value);
        }
    }

    /// <summary>
    /// B1: with every capability and every policy passing, another database's logical slot is still never named - its
    /// slot, plugin and database are absent from every evidence value, title, caveat, mute key and anchor - while it is
    /// counted and sized with the rest.
    /// </summary>
    [Fact]
    public async Task Another_databases_logical_slot_is_never_named_even_through_the_activity_gate()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        HealthReport report = await ReportAsync(harness);

        HealthReportFinding inactive = report.Findings.Single(static x => x.Finding.Subject == "inactive");
        inactive.Evidence.Should().Contain(new HealthEvidence("Inactive slots holding 1.0 GiB or more", "2 of 3 slots on this server"),
            "another database's slot is counted");
        inactive.Evidence.Should().Contain(static x => x.Value.StartsWith(ReplicationSlot.AnotherDatabase, StringComparison.Ordinal));

        string everything = string.Join("\n", report.Findings.SelectMany(static x =>
            new[] { x.Finding.Title, x.Finding.WhyItMatters, x.Finding.Caveat, x.MuteKey, x.Anchor }
                .Concat(x.Evidence.Select(static e => e.Label + " " + e.Value))));

        foreach (string value in AnotherDatabases)
        {
            everything.Should().NotContain(value);
        }
    }

    [Fact]
    public async Task A_visitor_admitted_for_one_tenant_sees_the_version_and_neither_the_slots_nor_the_probe()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: Stated,
            studio: static options =>
            {
                options.KnownTenantIds.Add("acme");
                options.Capabilities = MartenStudioCapabilities.All();
            },
            policies: true);

        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        HealthReport report = await ReportAsync(harness, harness.Scope with { TenantId = "acme" });

        report.Findings.Select(static x => x.Code).Should().Equal("S91");
        report.Findings.Single().Evidence.Should().NotContain(static x => x.Label == "Platform" && !x.Value.StartsWith("withheld", StringComparison.Ordinal),
            "the platform is the database's, shown at the database-wide gate");
        report.WithheldCount.Should().Be(3);
    }

    // ---------------------------------------------------------------------------------------------------
    // The real evaluators over stated facts, under codes of their own
    // ---------------------------------------------------------------------------------------------------

    private static HealthVerdict Recode(HealthVerdict verdict, string code) =>
        new([.. verdict.Findings.Select(x => x with { Code = code })], verdict.Note);

    [HealthCheck("R91", HealthArea.Replication, HealthCostTier.Catalog, "Stated replication slots")]
    private sealed class StatedSlotsCheck : HealthCheck<ReplicationSlotFacts>
    {
        public static ReplicationSlotFacts Facts(DateTimeOffset at) => new(
            new ReplicationSlotSnapshot(at, 170004, false, -1, 0, 3,
            [
                new ReplicationSlot("debezium_orders", "logical", "pgoutput", "orders_db", false, false, 3L << 30, "extended", null, null, 812,
                    TimeSpan.FromHours(5), null, false, ThisDatabase: true),
                new ReplicationSlot("old_standby", "physical", null, null, false, false, null, "lost", null, null, null,
                    TimeSpan.FromDays(3), "wal_removed", null, ThisDatabase: true),

                // Another tenant's database on the same server: counted, and never named to this database's visitors.
                new ReplicationSlot(OtherSlot, "logical", "wal2json", OtherDatabase, false, false, 2L << 30, "extended", null, null, 640,
                    TimeSpan.FromHours(7), null, false, ThisDatabase: false),
            ]),
            new PostgresServerFacts(at, 170004, "17.4", PostgresPlatform.SelfHosted));

        public override Task<ReplicationSlotFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Facts(context.Now));

        public override HealthVerdict Evaluate(ReplicationSlotFacts facts, HealthEvaluation evaluation) =>
            Recode(new ReplicationSlotCheck().Evaluate(facts, evaluation), "R91");
    }

    [HealthCheck("C96", HealthArea.Connections, HealthCostTier.Catalog, "Stated pooler proof")]
    private sealed class StatedProbeCheck : HealthCheck<PooledLockProbeFacts>
    {
        public static PooledLockProbeFacts Facts { get; } = new(
            new DaemonLockPooling(DaemonMode.HotCold, false, new DataSourceEndpoints([("pgbouncer.internal", 6432)], false)),
            4101,
            4202);

        public override Task<PooledLockProbeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Facts);

        public override HealthVerdict Evaluate(PooledLockProbeFacts facts, HealthEvaluation evaluation) =>
            Recode(new PooledDaemonLockProofCheck().Evaluate(facts, evaluation), "C96");
    }

    [HealthCheck("S91", HealthArea.Server, HealthCostTier.Catalog, "Stated version", Gate = HealthGate.None)]
    private sealed class StatedVersionCheck : HealthCheck<PostgresServerFacts>
    {
        public override Task<PostgresServerFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PostgresServerFacts(context.Now, 140019, "14.19", new PostgresPlatform(PostgresPlatformKind.AmazonRds, "rds")));

        public override HealthVerdict Evaluate(PostgresServerFacts facts, HealthEvaluation evaluation) =>
            Recode(new PostgresEndOfLifeCheck().Evaluate(facts, evaluation), "S91");
    }
}
