using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Acceptance 3, both ways: a stream id and a tenant's name reach a visitor only through their gates.
/// </summary>
/// <remarks>
/// E03's own evaluator produces the finding - over a stated profile - and the framework's real report shows it
/// to each visitor; the scripted tier-2 check E90 only carries it into the sweep, recoded, because a sweep
/// accepts a finding only from the check whose code it bears.
/// </remarks>
public class EventHealthGateTests
{
    private const string StreamId = "order-1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly HealthReportRequest Deeper = new(Deeper: HealthDeeperChecks.Run);

    private static HealthHarness Harness(bool conjoined, Action<MartenStudioOptions>? studio = null)
    {
        HealthHarness harness = HealthHarness.Create(checks: [typeof(ScriptedDeeperCheck)], studio: studio, policies: true);

        TrafficProfile profile = Profile(
            Totals(10_000, tenants: conjoined ? 2 : 1),
            streams: [StreamOf(StreamId, 4_000, tenant: conjoined ? "acme" : "*DEFAULT*")],
            tenants: conjoined ? [TenantOf("acme", 7_000), TenantOf("globex", 3_000)] : null,
            conjoined: conjoined);

        harness.Script.Judge["E90"] = (_, evaluation) =>
        {
            HealthVerdict verdict = new HotStreamCheck().Evaluate(profile, evaluation);
            return verdict with { Findings = [.. verdict.Findings.Select(static x => x with { Code = "E90" })] };
        };

        return harness;
    }

    private static string Everything(HealthReport report) =>
        string.Join("|", report.Findings.SelectMany(static x => x.Evidence.Select(e => e.Value).Append(x.Finding.Title).Append(x.Finding.Subject ?? string.Empty)));

    [Fact]
    public void The_shape_this_test_relies_on_is_E03s()
    {
        HealthFinding finding = new HotStreamCheck()
            .Evaluate(Profile(Totals(10_000), streams: [StreamOf(StreamId, 4_000, tenant: "acme")], conjoined: true), At())
            .Findings.Should().ContainSingle().Subject;

        finding.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task On_a_conjoined_store_without_the_activity_gate_the_hot_stream_is_withheld_whole()
    {
        await using HealthHarness harness = Harness(conjoined: true);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, Deeper, Token);

        report.Findings.Should().BeEmpty();
        report.WithheldCount.Should().Be(1);
        report.WithheldReason.Should().Contain("ViewDatabaseActivity");
        Everything(report).Should().NotContain(StreamId).And.NotContain("acme");
    }

    [Fact]
    public async Task On_a_conjoined_store_a_visitor_the_store_policy_does_not_show_the_tenant_is_not_shown_it()
    {
        await using HealthHarness harness = Harness(conjoined: true, static options => options.Capabilities.ViewDatabaseActivity = true);
        harness.Policies.Allow(static resource => resource.TenantId != "acme");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, Deeper, Token);

        report.Findings.Should().BeEmpty("the stream is acme's, and this visitor may not see acme");
        report.WithheldReason.Should().Be("names a tenant the store policy does not show you");
    }

    [Fact]
    public async Task On_a_conjoined_store_through_the_activity_gate_and_the_tenants_policy_the_stream_and_tenant_are_shown()
    {
        await using HealthHarness harness = Harness(conjoined: true, static options => options.Capabilities.ViewDatabaseActivity = true);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, Deeper, Token);

        HealthReportFinding shown = report.Findings.Should().ContainSingle().Subject;
        shown.Evidence.Should().Contain(new HealthEvidence("Stream", StreamId));
        shown.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
        harness.Policies.Calls.Should().Contain(static x => x.Resource.TenantId == "acme", "the tenant's own store policy was asked");
    }

    [Fact]
    public async Task On_a_single_tenant_store_a_visitor_short_of_the_database_as_a_whole_is_not_shown_the_stream()
    {
        await using HealthHarness harness = Harness(conjoined: false, static options => options.KnownTenantIds.Add("acme"));

        // Somebody through the Database gate runs the deeper checks; a tenant-scoped visitor then sees the cached sweep.
        await harness.Health.GetReportAsync(harness.Scope, Deeper, Token);
        harness.Policies.Allow(static resource => resource.Capability is null && resource.TenantId == "acme");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope with { TenantId = "acme" }, HealthReportRequest.Regular, Token);

        report.DeeperChecksAllowed.Should().BeFalse("tier 2 starts only for a visitor through the Database gate");
        report.Findings.Should().BeEmpty();
        report.WithheldReason.Should().Contain("as a whole");
        Everything(report).Should().NotContain(StreamId);
    }

    [Fact]
    public async Task On_a_single_tenant_store_the_store_policy_for_the_database_shows_the_stream_without_the_activity_gate()
    {
        await using HealthHarness harness = Harness(conjoined: false);

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, Deeper, Token);

        report.Findings.Should().ContainSingle().Which.Evidence.Should().Contain(new HealthEvidence("Stream", StreamId));
    }
}
