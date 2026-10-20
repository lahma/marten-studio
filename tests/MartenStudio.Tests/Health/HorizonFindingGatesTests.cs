using PostgresStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Acceptance 2: who holds the horizon - a session's process id, role, application and client, a prepared transaction's
/// gid and owner, a slot's name - is shown only through the <c>ViewDatabaseActivity</c> gate; the kind, the age and the
/// "cannot see" caveat are the database's, shown to a visitor the store policy passes for the database as a whole and to
/// nobody else. Through the real <see cref="HealthService" />, over the real evaluators fed stated facts.
/// </summary>
public class HorizonFindingGatesTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static readonly Type[] Stated = [typeof(StatedHorizonCheck), typeof(StatedWraparoundCheck)];

    /// <summary>Every identifying value the stated holders carry.</summary>
    private static readonly string[] Identifying = ["4242", "app_user", "billing-api", "10.1.2.3"];

    private const string CannotSee = "cannot see when 2 sessions of other roles";

    private static Task<HealthReport> ReportAsync(HealthHarness harness, StudioScope? scope = null) =>
        harness.Health.GetReportAsync(scope ?? harness.Scope, HealthReportRequest.Regular, Token);

    [Fact]
    public void The_horizon_checks_are_at_the_database_wide_gate()
    {
        StudioHealthChecks.Registry.All.Where(static x => x.Info.Code is "T12" or "T13" or "T14")
            .Should().HaveCount(3).And.OnlyContain(static x => x.Info.Gate == HealthGate.Database);
    }

    [Fact]
    public async Task Without_the_capability_who_holds_it_is_withheld_and_the_kind_the_age_and_the_caveat_are_shown()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = new MartenStudioCapabilities());

        HealthReport report = await ReportAsync(harness);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T94").Subject;

        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's snapshot"));
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().StartWith("withheld - ")
            .And.Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity");
        finding.Finding.Caveat.Should().Contain(CannotSee);

        string shown = string.Join("\n", finding.Evidence.Select(static x => x.Value)) + "\n" + finding.Finding.Title + "\n" + finding.Finding.Caveat;
        foreach (string value in Identifying)
        {
            shown.Should().NotContain(value);
        }

        ActivityEntries(harness).Should().BeEmpty("nothing behind the activity gate was shown");
    }

    [Fact]
    public async Task With_the_capability_and_the_write_policy_who_holds_it_is_shown_and_audited()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: Stated, studio: static options => options.Capabilities = MartenStudioCapabilities.All());

        HealthReport report = await ReportAsync(harness);

        string shown = string.Join("\n", report.Findings.SelectMany(static x => x.Evidence).Select(static x => x.Value));
        foreach (string value in Identifying)
        {
            shown.Should().Contain(value);
        }

        ActivityEntries(harness).Should().ContainSingle().Which.Message.Should().Contain("T94").And.NotContain("4242");
    }

    [Fact]
    public async Task A_visitor_the_store_policy_admits_for_one_tenant_sees_neither_the_finding_nor_its_caveat()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: Stated,
            studio: static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.KnownTenantIds.Add("acme");
            },
            policies: true);

        harness.Policies.Allow(static resource => resource.TenantId == "acme");

        HealthReport report = await ReportAsync(harness, harness.Scope with { TenantId = "acme" });

        report.Refusal.Should().Be(HealthRefusal.None);
        report.Findings.Should().BeEmpty("a database-wide number is about every tenant at once");
        report.WithheldCount.Should().Be(2);
        report.Checks.Where(static x => x.Code is "T92" or "T94").Should().OnlyContain(static x => x.Note == null || !x.Note.Contains(CannotSee, StringComparison.Ordinal));

        harness.Policies.Allow(static _ => true);
        (await ReportAsync(harness)).Findings.Select(static x => x.Code).Should().BeEquivalentTo(["T92", "T94"], "and through the gate both are shown");
    }

    private static List<StudioActionLogEntry> ActivityEntries(HealthHarness harness) =>
    [
        .. harness.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .Where(static x => x.Action == HealthService.ActivityAction),
    ];

    private static readonly DateTimeOffset Connected = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static HealthVerdict Recode(HealthVerdict verdict, string code) =>
        new([.. verdict.Findings.Select(x => x with { Code = code })], verdict.Note);

    [HealthCheck("T94", HealthArea.Tables, HealthCostTier.Catalog, "Stated vacuum held back")]
    private sealed class StatedHorizonCheck : HealthCheck<HorizonFacts>
    {
        public override Task<HorizonFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new HorizonFacts(
                context.Now,
                new SessionVisibility(CanReadAllStats: false, HiddenSessions: 2),
                [
                    new HorizonHolder
                    {
                        Kind = HealthHorizonHolderKind.Session,
                        XidAge = 80_000,
                        AtLeast = TimeSpan.FromHours(3),
                        AtMost = TimeSpan.FromHours(3),
                        ProcessId = 4242,
                        User = "app_user",
                        Application = "billing-api",
                        Client = "10.1.2.3",
                        BackendStart = Connected,
                        State = "idle in transaction",
                    },
                ],
                new HealthWraparoundDatabase { DatabaseXidAge = 90_000, ServerXidAge = 90_000 },
                [],
                HasAsyncShards: false,
                LeaderLockInTransaction: false));

        public override HealthVerdict Evaluate(HorizonFacts facts, HealthEvaluation evaluation) =>
            Recode(new HorizonHeldCheck().Evaluate(facts, evaluation), "T94");
    }

    [HealthCheck("T92", HealthArea.Tables, HealthCostTier.Catalog, "Stated transaction-id age")]
    private sealed class StatedWraparoundCheck : HealthCheck<TransactionIdAgeFacts>
    {
        public override Task<TransactionIdAgeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new TransactionIdAgeFacts(
                [],
                new WraparoundFacts(new HealthWraparoundDatabase { DatabaseXidAge = 1_200_000_000, ServerXidAge = 1_200_000_000 }, [])));

        public override HealthVerdict Evaluate(TransactionIdAgeFacts facts, HealthEvaluation evaluation) =>
            Recode(new TransactionIdAgeCheck().Evaluate(facts, evaluation), "T92");
    }
}
