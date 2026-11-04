using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// 1.2-SCOPE-1 S6: <c>IHealthService.CheckAsync</c>, the Stores page's "Check": it resolves the scope first, starts the one regular
/// sweep a database's Overview would start, and answers the glance - without the audit entry a report that shows session
/// details records.
/// </summary>
public class HealthCheckAsyncTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scope_the_store_policy_refuses_starts_no_sweep()
    {
        await using HealthHarness harness = HealthHarness.Create(policies: true);
        harness.Script.Finds("K90", HealthSeverity.Warning);
        harness.Policies.DenyEverything();

        HealthGlance? glance = await harness.Health.CheckAsync(harness.Scope, Token);

        glance.Should().BeNull("a refusal is a value, and a chip stays 'not checked recently'");
        harness.Cache.SweepCount.Should().Be(0, "the scope is resolved - the store policy asked - before anything is swept");
    }

    [Fact]
    public async Task A_store_nothing_is_registered_under_starts_no_sweep()
    {
        await using HealthHarness harness = HealthHarness.Create();

        HealthGlance? glance = await harness.Health.CheckAsync(harness.Scope with { StoreKey = "ghost" }, Token);

        glance.Should().BeNull();
        harness.Cache.SweepCount.Should().Be(0);
    }

    [Fact]
    public async Task Check_starts_one_regular_sweep_and_returns_its_glance()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Warning, "One warning");
        harness.Script.Finds("K91", HealthSeverity.Critical, "One critical");

        (await harness.Health.GlanceAsync(harness.Scope, Token)).Should().BeNull("the premise: nothing has been swept");

        HealthGlance? glance = await harness.Health.CheckAsync(harness.Scope, Token);

        glance.Should().NotBeNull();
        glance!.Critical.Should().Be(1);
        glance.Warning.Should().Be(1);
        harness.Cache.SweepCount.Should().Be(1, "one sweep, and the regular one");

        // Single flight: a second Check inside the interval takes the finished sweep.
        (await harness.Health.CheckAsync(harness.Scope, Token))!.MeasuredAt.Should().Be(glance.MeasuredAt);
        harness.Cache.SweepCount.Should().Be(1);

        // And the sidebar's cache-only read now finds it, so the sweep Check started is the shared one.
        (await harness.Health.GlanceAsync(harness.Scope, Token)).Should().NotBeNull();
    }

    [Fact]
    public async Task Check_counts_the_suggestions_the_chip_says()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Info, "A suggestion");

        HealthGlance glance = (await harness.Health.CheckAsync(harness.Scope, Token))!;

        glance.Critical.Should().Be(0);
        glance.Warning.Should().Be(0);
        glance.Suggestions.Should().Be(1);
    }

    /// <summary>
    /// A report that shows an Activity-gated finding records "Read health session details"; Check shows no evidence and records
    /// nothing. The control proves the finding would have been audited by a report.
    /// </summary>
    [Fact]
    public async Task Check_records_nothing_in_the_action_log()
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Script.Finds("C90", HealthSeverity.Warning, "Activity-gated");
        StudioActionLogService log = harness.Services.GetRequiredService<StudioActionLogService>();

        HealthGlance? glance = await harness.Health.CheckAsync(harness.Scope, Token);

        glance.Should().NotBeNull();
        log.GetLatest().Should().NotContain(static x => x.Action == HealthService.ActivityAction, "Check answers through the glance, with audit: false");

        _ = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        log.GetLatest().Should().Contain(static x => x.Action == HealthService.ActivityAction, "the control: a report showing the same finding is audited");
    }
}
