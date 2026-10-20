using MartenStudio.Services;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The sidebar's health badge: the last sweep, for this visitor, unmuted - and not one query more on the
/// poll that runs every five seconds on every page (acceptance 6).
/// </summary>
public class HealthNavBadgeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_badge_reads_the_last_sweep_and_adds_no_read_and_no_sweep_to_the_nav_poll()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Warning, "One warning");
        harness.Script.Finds("K91", HealthSeverity.Critical, "One critical");

        // What the Health page or the Overview did: one sweep.
        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);
        int sweeps = harness.Cache.SweepCount;
        int gathered = harness.Script.Runs.Values.Sum();

        Badge badge = await Badge.ReadyAsync(harness);

        NavIndicators first = await badge.Service.ReadAsync(Token);
        for (int poll = 0; poll < 10; poll++)
        {
            await badge.Service.ReadAsync(Token);
        }

        first.Health.Should().NotBeNull();
        first.Health!.Critical.Should().Be(1);
        first.Health.Warning.Should().Be(1);
        first.ShowHealthBadge.Should().BeTrue();

        harness.Cache.SweepCount.Should().Be(sweeps, "the badge never starts a sweep");
        harness.Script.Runs.Values.Sum().Should().Be(gathered, "no check's gatherer ran for the badge");
        badge.Events.DeadLetterCountReads.Should().Be(1, "the dead-letter half is cached exactly as it was before the badge existed");
        badge.Projections.Reads.Should().Be(1);
    }

    [Fact]
    public async Task With_no_recent_sweep_the_entry_carries_no_badge_and_nothing_is_swept_to_find_out()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Critical);

        Badge badge = await Badge.ReadyAsync(harness);

        NavIndicators indicators = await badge.Service.ReadAsync(Token);

        indicators.Health.Should().BeNull();
        indicators.ShowHealthBadge.Should().BeFalse();
        harness.Cache.SweepCount.Should().Be(0);
    }

    [Fact]
    public async Task A_finding_muted_in_this_browser_leaves_the_badge()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Critical, "Muted critical");

        HealthReport report = await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        using Bunit.BunitContext js = new();
        js.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        await harness.Mutes.SetAsync(js.JSInterop.JSRuntime, report.Findings.Single().MuteKey, muted: true, Token);

        HealthGlance glance = (await harness.Health.GlanceAsync(harness.Scope, Token))!;

        glance.Critical.Should().Be(0);
        glance.Worst.Should().BeNull("a muted finding is not worth a badge in this browser");
    }

    [Fact]
    public async Task Info_findings_are_suggestions_and_draw_no_badge()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Info);

        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        HealthGlance glance = (await harness.Health.GlanceAsync(harness.Scope, Token))!;
        glance.Worst.Should().BeNull();
    }

    /// <summary>
    /// The toast compares a glance's stamp with the scope the sidebar is on, so the navigation service hands the health
    /// service's stamp on exactly as it is. A glance re-stamped on the way - the tenant dropped, the database resolved -
    /// would be ignored by the toast, and no Critical would ever toast again; nothing else would notice.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("acme")]
    public async Task The_badge_hands_the_glance_on_stamped_with_the_scope_it_was_read_for(string? tenant)
    {
        await using HealthHarness harness = HealthHarness.Create(studio: static options => options.KnownTenantIds.Add("acme"));
        harness.Script.Finds("K91", HealthSeverity.Critical, "One critical");
        await harness.Health.GetReportAsync(harness.Scope, HealthReportRequest.Regular, Token);

        Badge badge = await Badge.ReadyAsync(harness, static catalog => catalog.WithTenants(
            "default",
            new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured)));
        await badge.State.SetScopeAsync("default", harness.Scope.DatabaseId, tenant, Token);

        badge.State.ActiveScope!.TenantId.Should().Be(tenant);

        NavIndicators indicators = await badge.Service.ReadAsync(Token);

        indicators.Health.Should().NotBeNull("there is a recent sweep, and this visitor may see its finding");
        indicators.Health!.Scope.Should().Be(badge.State.ActiveScope);
        indicators.Health.Criticals.Should().ContainSingle();
    }

    /// <summary>The navigation service over the harness's health service, with the dead-letter and projection fakes.</summary>
    private sealed class Badge
    {
        private Badge(NavIndicatorService service, StudioState state, FakeEventDataService events, FakeProjectionDataService projections)
        {
            Service = service;
            State = state;
            Events = events;
            Projections = projections;
        }

        public NavIndicatorService Service { get; }

        /// <summary>The circuit's state the service reads its scope from, for a test that moves it.</summary>
        public StudioState State { get; }

        public FakeEventDataService Events { get; }

        public FakeProjectionDataService Projections { get; }

        public static async Task<Badge> ReadyAsync(HealthHarness harness, Action<FakeStudioScopeCatalog>? configureCatalog = null)
        {
            FakeStudioScopeCatalog catalog = new();
            catalog.WithStore("default", "Default", databaseIdentities: harness.Scope.DatabaseId);
            configureCatalog?.Invoke(catalog);

            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            StudioState state = new(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
            await state.EnsureInitializedAsync(Token);

            FakeEventDataService events = new();
            FakeProjectionDataService projections = new();
            projections.WithProjection("OrderSummary");

            NavIndicatorService service = new(
                state,
                MartenSpine.Authorization(Options.Create(new MartenStudioOptions()), new TestStoreAuthorizationService(), users),
                [new MartenNavIndicatorContributor(events, projections)],
                new StudioSnapshotCache(),
                NullLogger<NavIndicatorService>.Instance,
                harness.Health);

            return new Badge(service, state, events, projections);
        }
    }
}
