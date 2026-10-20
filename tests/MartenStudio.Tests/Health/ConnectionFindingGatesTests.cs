using PostgresStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Acceptance 3, the findings half: every detail field the session checks put in a finding - process id, role,
/// application, client address, connection time, a prepared transaction's gid and owner - is shown to a visitor
/// through the <c>ViewDatabaseActivity</c> gate and withheld from every other, while the finding itself, its
/// counts and its ages are shown to both. Through the real <see cref="HealthService" />, over the real
/// evaluators fed stated facts.
/// </summary>
public class ConnectionFindingGatesTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly Type[] Stated =
    [
        typeof(StatedIdleCheck),
        typeof(StatedLongRunningCheck),
        typeof(StatedLockWaitCheck),
        typeof(StatedPreparedCheck),
    ];

    /// <summary>
    /// Every identifying value the stated facts carry: the waiter's and the idle session's, the blocker's - a session
    /// of its own, so that its gating is proven rather than borrowed from the waiter's - and the prepared transaction's.
    /// </summary>
    private static readonly string[] Identifying =
        ["4242", "app_user", "billing-api", "10.1.2.3", "5151", "report_user", "reports-job", "10.9.9.9", "gid-orphan", "tx_owner"];

    private static async Task<HealthReport> ReportAsync(HealthHarness harness) =>
        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

    [Fact]
    public async Task Without_the_capability_every_detail_is_withheld_and_the_findings_are_still_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = new MartenStudioCapabilities());

        HealthReport report = await ReportAsync(harness);

        report.Findings.Select(static x => x.Code).Should().BeEquivalentTo(["C91", "C92", "C93", "C94"],
            "a count and an age are the database's, shown to anybody its store policy passes");

        foreach (HealthReportFinding finding in report.Findings)
        {
            HealthEvidence[] gated = [.. finding.Finding.Evidence.Where(static x => x.Gate == HealthGate.Activity)];
            gated.Should().NotBeEmpty("{0} names somebody", finding.Code);

            foreach (HealthEvidence shown in finding.Evidence)
            {
                foreach (string value in Identifying)
                {
                    shown.Value.Should().NotContain(value, "{0} {1} is withheld without the capability", finding.Code, shown.Label);
                }
            }

            finding.Evidence.Where(x => gated.Any(g => g.Label == x.Label))
                .Should().OnlyContain(static x => x.Value.StartsWith("withheld - ", StringComparison.Ordinal)
                    && x.Value.Contains("MartenStudioOptions.Capabilities.ViewDatabaseActivity", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task With_the_capability_and_the_write_policy_every_detail_is_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        HealthReport report = await ReportAsync(harness);

        string shown = string.Join("\n", report.Findings.SelectMany(static x => x.Evidence).Select(static x => x.Value));

        foreach (string value in Identifying)
        {
            shown.Should().Contain(value);
        }
    }

    [Fact]
    public async Task ReadOnly_withholds_them_as_the_capability_off_does()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        HealthReport report = await ReportAsync(harness);

        report.Findings.SelectMany(static x => x.Evidence).Where(static x => x.Value.StartsWith("withheld", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(static x => x.Value.Contains("ReadOnly", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDatabaseActivity_withholds_them_and_is_asked_with_no_tenant()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: Stated,
            studio: static options => options.Capabilities = MartenStudioCapabilities.All(),
            policies: true);

        harness.Policies.Allow(static resource => resource.Capability is null);

        HealthReport report = await ReportAsync(harness);

        report.Findings.Should().HaveCount(4, "the store policy passes the database as a whole");
        string shown = string.Join("\n", report.Findings.SelectMany(static x => x.Evidence).Select(static x => x.Value));

        foreach (string value in Identifying)
        {
            shown.Should().NotContain(value);
        }

        shown.Should().Contain("write policy");
        harness.Policies.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity))
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null);
        ActivityEntries(harness).Should().BeEmpty("nothing behind the activity gate was shown");
    }

    // ----------------------------------------------------------------------------------------------
    // The Health page's half of the audit: shown once per circuit, store and database, by signature
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Session_details_shown_in_findings_are_audited_once_per_circuit_and_a_poll_records_nothing()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        await ReportAsync(harness);
        await ReportAsync(harness);
        await harness.Health.GlanceAsync(harness.Scope, Token);

        StudioActionLogEntry entry = ActivityEntries(harness).Should().ContainSingle("the page's interval reads repeat the signature, and the badge shows no evidence").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        entry.TenantId.Should().BeNull("a session belongs to no tenant");
        entry.Message.Should().Contain("C91, C92, C93, C94");

        foreach (string value in Identifying)
        {
            entry.Message.Should().NotContain(value, "the ring is readable by more people than the page");
        }

        // A circuit of its own is a page view of its own.
        await using AsyncServiceScope other = harness.Services.CreateAsyncScope();
        await other.ServiceProvider.GetRequiredService<IHealthService>().GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        ActivityEntries(harness).Should().HaveCount(2);
    }

    [Fact]
    public async Task Session_details_withheld_from_a_visitor_are_not_audited_as_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = new MartenStudioCapabilities());

        await ReportAsync(harness);

        ActivityEntries(harness).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_findings_that_show_session_details_are_named_in_the_audit()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(StatedIdleCheck), typeof(StatedConfigurationOnlyCheck)],
            studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        await ReportAsync(harness);

        ActivityEntries(harness).Should().ContainSingle().Which.Message.Should().Contain("C91").And.NotContain("C95",
            "a finding with no evidence behind the activity gate shows nobody");
    }

    private static List<StudioActionLogEntry> ActivityEntries(HealthHarness harness) =>
    [
        .. harness.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .Where(static x => x.Action == HealthService.ActivityAction),
    ];

    // ----------------------------------------------------------------------------------------------
    // The real evaluators over stated facts, under codes of their own so the registry holds them beside nothing
    // ----------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Connected = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static HealthVerdict Recode(HealthVerdict verdict, string code) =>
        new([.. verdict.Findings.Select(x => x with { Code = code })], verdict.Note);

    [HealthCheck("C91", HealthArea.Connections, HealthCostTier.Catalog, "Stated idle in transaction")]
    private sealed class StatedIdleCheck : HealthCheck<IdleInTransactionFacts>
    {
        public override Task<IdleInTransactionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new IdleInTransactionFacts(context.Now, SessionVisibility.Everything, 1,
            [
                new IdleSession(4242, "app_user", "billing-api", "10.1.2.3", Connected, "idle in transaction",
                    TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(4), true, true, 1, false),
            ]));

        public override HealthVerdict Evaluate(IdleInTransactionFacts facts, HealthEvaluation evaluation) =>
            Recode(new IdleInTransactionCheck().Evaluate(facts, evaluation), "C91");
    }

    [HealthCheck("C92", HealthArea.Connections, HealthCostTier.Catalog, "Stated long-running statement")]
    private sealed class StatedLongRunningCheck : HealthCheck<LongRunningFacts>
    {
        public override Task<LongRunningFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new LongRunningFacts(context.Now, SessionVisibility.Everything, 1,
                [new RunningStatement(4242, "app_user", "billing-api", "10.1.2.3", Connected, null, TimeSpan.FromMinutes(9))]));

        public override HealthVerdict Evaluate(LongRunningFacts facts, HealthEvaluation evaluation) =>
            Recode(new LongRunningStatementCheck().Evaluate(facts, evaluation), "C92");
    }

    [HealthCheck("C93", HealthArea.Connections, HealthCostTier.Catalog, "Stated lock wait")]
    private sealed class StatedLockWaitCheck : HealthCheck<LockWaitFacts>
    {
        public override Task<LockWaitFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new LockWaitFacts(context.Now, SessionVisibility.Everything, 1,
                [
                    new LockWaiter(4242, "app_user", "billing-api", "10.1.2.3", "ShareLock",
                        new LockTarget(LockTargetKind.EventTable, "an mt_streams row (health_unit)", "health_unit", "mt_streams", Row: true),
                        true, TimeSpan.FromMinutes(1), [5151]),
                ],
                [new LockBlocker(5151, true, "idle in transaction", "report_user", "reports-job", "10.9.9.9", TimeSpan.FromMinutes(2), true, false)]));

        public override HealthVerdict Evaluate(LockWaitFacts facts, HealthEvaluation evaluation) =>
            Recode(new LockWaitCheck().Evaluate(facts, evaluation), "C93");
    }

    /// <summary>A finding whose evidence is all the database's: counted, never a reason to audit.</summary>
    [HealthCheck("C95", HealthArea.Connections, HealthCostTier.Catalog, "Stated finding with no session in it")]
    private sealed class StatedConfigurationOnlyCheck : HealthCheck<LongRunningFacts>
    {
        public override Task<LongRunningFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new LongRunningFacts(context.Now, SessionVisibility.Everything, 0, []));

        public override HealthVerdict Evaluate(LongRunningFacts facts, HealthEvaluation evaluation) =>
            HealthVerdict.Of(new HealthFinding
            {
                Code = "C95",
                Severity = HealthSeverity.Warning,
                Title = "Something about the database as a whole",
                WhyItMatters = "A stated finding.",
                Evidence = [new HealthEvidence("Sessions", "3")],
            });
    }

    [HealthCheck("C94", HealthArea.Connections, HealthCostTier.Catalog, "Stated prepared transaction")]
    private sealed class StatedPreparedCheck : HealthCheck<PreparedTransactionFacts>
    {
        public override Task<PreparedTransactionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new PreparedTransactionFacts(context.Now, 10,
                [new PreparedRow("gid-orphan", "tx_owner", context.Now.AddMinutes(-30), TimeSpan.FromMinutes(30), "901")]));

        public override HealthVerdict Evaluate(PreparedTransactionFacts facts, HealthEvaluation evaluation) =>
            Recode(new PreparedTransactionCheck().Evaluate(facts, evaluation), "C94");
    }
}
