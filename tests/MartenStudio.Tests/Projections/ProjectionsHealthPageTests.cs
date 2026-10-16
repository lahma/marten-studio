using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Health;

using Microsoft.Extensions.DependencyInjection;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The projections screen's H2 half (F2): the D01/D02 banners from the advisor's cache, the Explain, the liveness
/// column, the per-row thresholds, the trend arrow and ETA, "already rebuilding" as a state, the rebuild estimate and
/// the drain wording - and, criterion 5, that the 5 s poll never asks for a sweep.
/// </summary>
/// <remarks>
/// Every click whose handler awaits anything is followed by a wait on the assertion (ProjectionsPageTests' rule).
/// </remarks>
public class ProjectionsHealthPageTests
{
    private const string ConfirmButton = ".ms-confirm-actions .ms-button-danger";

    private static readonly DateTimeOffset MeasuredAt = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A health service whose cache-only read answers what a test says and counts every call - so a test can prove the
    /// page read the cache and never asked for a report, which is what sweeps.
    /// </summary>
    private sealed class CachedHealth : IHealthService
    {
        public IReadOnlyList<HealthReportFinding> Cached { get; set; } = [];

        public int CachedReads { get; private set; }

        public int ReportRequests { get; private set; }

        public Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default)
        {
            ReportRequests++;
            return Task.FromResult(new HealthReport());
        }

        public Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult<HealthGlance?>(null);

        public Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
            StudioScope scope,
            IReadOnlyCollection<string> codes,
            CancellationToken cancellationToken = default)
        {
            CachedReads++;
            return Task.FromResult<IReadOnlyList<HealthReportFinding>>([.. Cached.Where(x => codes.Contains(x.Code))]);
        }
    }

    private static HealthReportFinding Shown(string code, HealthSeverity severity, string title) =>
        new(
            FakeHealthService.Finding(code, severity, title, HealthArea.Daemon) with { MeasuredAt = MeasuredAt },
            [],
            "default|localhost.marten|" + code,
            "https://example.invalid/docs/health/daemon.md#" + code.ToLowerInvariant(),
            RemedyAvailable: true,
            HostRuns: null);

    /// <summary>
    /// A ready context with <paramref name="health" /> as its health service - registered before the container is first
    /// used, which bUnit requires - one projection unless <paramref name="data" /> says otherwise, and the advisor's history
    /// when a test hands one over.
    /// </summary>
    private static async Task<StudioComponentContext> ReadyAsync(
        CachedHealth health,
        Action<FakeProjectionDataService>? data = null,
        HealthHistory? history = null,
        bool dailySales = true)
    {
        StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.Services.AddSingleton<IHealthService>(health);

        if (history is not null)
        {
            context.Services.AddSingleton(history);
        }

        if (dailySales)
        {
            context.ProjectionData.WithProjection("DailySales", sequence: 900);
        }

        data?.Invoke(context.ProjectionData);

        await context.ReadyAsync();
        return context;
    }

    // ---- Criterion 5: the poll reads the cache, never a sweep -----------------------------------------------------

    [Fact]
    public async Task Every_poll_reads_the_advisors_cache_and_never_asks_for_a_report()
    {
        CachedHealth health = new();
        await using StudioComponentContext context = await ReadyAsync(health);

        IRenderedComponent<Page> page = context.Render<Page>();

        for (int i = 0; i < 3; i++)
        {
            int before = health.CachedReads;
            page.Find(".ms-projections-refresh").Click();
            page.WaitForAssertion(() => health.CachedReads.Should().BeGreaterThan(before));
        }

        health.ReportRequests.Should().Be(0, "a report is what sweeps, and the projections poll never asks for one");
        context.ProjectionData.Explains.Should().Be(0, "Explain is asked only when somebody presses it");
    }

    /// <summary>
    /// The backstop for criterion 5 that does not depend on a fake: the screen's own code and the projections service never
    /// name the calls that sweep or read the lock table, and the screen does name the cache-only read (so the scan is
    /// reading the right files, and would notice the read being renamed away).
    /// </summary>
    [Fact]
    public void The_projections_screen_and_service_never_name_a_sweep_or_a_lock_table_read()
    {
        string page = File.ReadAllText(Conventions.RepositoryRoot.Combine("src/MartenStudio/Components/Pages/Projections/Projections.razor"));
        string[] service = [.. Directory.GetFiles(Conventions.RepositoryRoot.Combine("src/MartenStudio/Services/Projections"), "*.cs").Select(File.ReadAllText)];

        foreach (string source in (string[]) [page, .. service])
        {
            source.Should().NotContain("GetReportAsync").And.NotContain("HealthSweepCache").And.NotContain("HealthSweeper.SweepAsync")
                .And.NotContain("HealthDaemonQueries.DaemonLocks");
        }

        page.Should().Contain("CachedFindingsAsync", "the banners are read from the advisor's cache");
    }

    // ---- The banners --------------------------------------------------------------------------------------------

    [Fact]
    public async Task D01_and_D02_from_the_cache_are_banners_and_D02_stands_the_old_banner_down()
    {
        CachedHealth health = new()
        {
            Cached =
            [
                Shown("D01", HealthSeverity.Warning, "The high-water mark has been held behind a gap for 2 min"),
                Shown("D02", HealthSeverity.Critical, "Async projections are 1,000 events behind and nothing appears to be running the daemon"),
                Shown("T03", HealthSeverity.Critical, "Not this screen's"),
            ],
        };

        // Nothing has ever advanced and no daemon is here: the shape that raises the old banner on its own.
        await using StudioComponentContext context = await ReadyAsync(
            health,
            static data => data.WithNoDaemonHere().WithProjection("ShipmentTracker", sequence: 0, hasProgressRow: false),
            dailySales: false);

        IRenderedComponent<Page> page = context.Render<Page>();

        page.WaitForElement(".ms-health-banner-d01").TextContent.Should().Contain("held behind a gap");
        page.Find(".ms-health-banner-d02").GetAttribute("role").Should().Be("alert", "a Critical banner is an alert");
        page.Find(".ms-health-banner-d01").GetAttribute("role").Should().Be("status");
        page.FindAll(".ms-health-banner").Should().HaveCount(2, "only D01 and D02 are this screen's");
        page.FindAll(".ms-no-daemon-banner").Should().BeEmpty("D02 says it, better, and once is enough");
        page.Find(".ms-health-banner-d01 .ms-health-banner-link").GetAttribute("href").Should().Contain("health");
    }

    /// <summary>No recent sweep: nothing is drawn, and nothing claims the store is healthy either.</summary>
    [Fact]
    public async Task With_nothing_in_the_cache_there_is_no_banner_and_no_claim_of_health()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth());

        IRenderedComponent<Page> page = context.Render<Page>();

        page.FindAll(".ms-health-banner").Should().BeEmpty();
        page.Markup.Should().NotContain("healthy");
    }

    [Fact]
    public async Task Explain_asks_once_on_the_click_and_withholds_the_sessions_without_the_activity_gate()
    {
        CachedHealth health = new() { Cached = [Shown("D01", HealthSeverity.Critical, "The high-water mark has been held behind a gap for 12 min")] };
        await using StudioComponentContext context = await ReadyAsync(health, static data => data.Explanation = new HighWaterExplanation
        {
            IsPinned = true,
            Mark = 100,
            OpenTransactions = 1,
            DetailsShown = false,
            DetailsWithheldReason = "Naming sessions needs MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
        });

        IRenderedComponent<Page> page = context.Render<Page>();
        page.WaitForElement(".ms-high-water-explain").Click();

        page.WaitForElement(".ms-high-water-explanation-summary").TextContent.Should().Contain("1 transaction open since before the pin");
        context.ProjectionData.Explains.Should().Be(1);
        page.FindAll(".ms-high-water-sessions").Should().BeEmpty("the sessions are withheld");
        page.Find(".ms-high-water-withheld").TextContent.Should().Contain("ViewDatabaseActivity");
    }

    [Fact]
    public async Task Explain_names_the_sessions_through_the_activity_gate_in_a_scroll_region()
    {
        CachedHealth health = new() { Cached = [Shown("D01", HealthSeverity.Critical, "pinned")] };
        await using StudioComponentContext context = await ReadyAsync(health, static data => data.Explanation = new HighWaterExplanation
        {
            IsPinned = true,
            Mark = 100,
            OpenTransactions = 1,
            DetailsShown = true,
            Sessions = [new HighWaterSuspect(4242, "app", "billing-api", "10.0.0.5", MeasuredAt, "idle in transaction", MeasuredAt, null, "select 1", false, false, null)],
        });

        IRenderedComponent<Page> page = context.Render<Page>();
        page.WaitForElement(".ms-high-water-explain").Click();

        IElement sessions = page.WaitForElement(".ms-high-water-sessions");
        sessions.TextContent.Should().Contain("4242").And.Contain("billing-api");
        sessions.Closest("[role='region']").Should().NotBeNull("D25: every table sits in a labelled scroll region");
    }

    // ---- The table ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_liveness_column_says_how_long_ago_the_shard_last_progressed()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data => data.WithLastProgress("DailySales:All", TimeSpan.FromSeconds(12)));

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-last-advanced").TextContent.Trim().Should().StartWith("12 s ago");
    }

    [Fact]
    public async Task A_rows_own_threshold_colours_the_lag_and_says_so()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data => data.WithThresholds("DailySales:All", 50, 90));

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement lag = page.Find("[data-shard='DailySales:All'] .ms-shard-lag");
        lag.ClassList.Should().Contain("ms-lag-critical", "a lag of 100 is past this shard's own 90");
        lag.GetAttribute("title").Should().Contain("this shard's own thresholds");
    }

    [Fact]
    public async Task With_the_advisors_samples_the_lag_carries_a_trend_arrow_and_its_eta()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HealthHistoryView history = new([Sample(now - TimeSpan.FromMinutes(2), position: 400, highest: 1_000), Sample(now, position: 900, highest: 1_000)]);

        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), data =>
            data.Trends["DailySales:All"] = ProjectionTrend.For(history, "DailySales:All"));

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-trend").ClassList.Should().Contain("ms-shard-trend-draining");
        page.Find("[data-shard='DailySales:All'] .ms-shard-eta").TextContent.Should().StartWith("~");
    }

    /// <summary>
    /// The H2 review's follow-up for the arrow: a trend withheld (a pause, a rebuild, a shard or mark that did not move)
    /// draws no arrow, and the lag's hover says why instead.
    /// </summary>
    [Fact]
    public async Task A_withheld_trend_draws_no_arrow_and_says_why()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        HealthHistoryView history = new([Sample(now - TimeSpan.FromMinutes(2), position: 400, highest: 1_000), Sample(now, position: 400, highest: 5_000)]);
        ShardTrend growing = ProjectionTrend.For(history, "DailySales:All");

        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), data =>
            data.Trends["DailySales:All"] = ProjectionTrend.Judged(growing, pausedByStudio: true, rebuilding: false, agentStatusHere: null));

        IRenderedComponent<Page> page = context.Render<Page>();

        page.FindAll("[data-shard='DailySales:All'] .ms-shard-trend").Should().BeEmpty();
        page.Find("[data-shard='DailySales:All'] .ms-shard-lag").GetAttribute("title").Should().Contain("Not judged: Marten Studio paused the projection coordinator");
    }

    /// <summary>The same rule is the service's, judged where the daemon's state is: the studio's pause withholds a growing lag.</summary>
    [Fact]
    public void The_judgement_withholds_growth_for_each_explained_state_and_keeps_a_real_one()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ShardTrend growing = ProjectionTrend.For(
            new HealthHistoryView([Sample(now - TimeSpan.FromMinutes(2), position: 400, highest: 1_000, mark: 1_000), Sample(now, position: 600, highest: 5_000, mark: 5_000)]),
            "DailySales:All");

        growing.Direction.Should().Be(LagDirection.Growing);
        ProjectionTrend.Judged(growing, false, false, "Running").WithheldBecause.Should().BeNull("it moved, the mark moved, nothing explains it");
        ProjectionTrend.Judged(growing, true, false, null).Arrow.Should().BeEmpty();
        ProjectionTrend.Judged(growing, false, true, null).WithheldBecause.Should().Contain("rebuilt");
        ProjectionTrend.Judged(growing, false, false, "Stopped").WithheldBecause.Should().Contain("Stopped (D05)");
    }

    [Fact]
    public async Task On_a_marten_that_cannot_see_a_rebuild_start_says_so()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data => data.WithStudioPause());

        IRenderedComponent<Page> page = context.Render<Page>();
        string? title = page.Find("[data-shard='DailySales:All'] .ms-shard-start").GetAttribute("title");

        if (DaemonLightUp.CanTellRebuilding)
        {
            (title ?? string.Empty).Should().NotContain("cannot say whether this projection is being rebuilt");
        }
        else
        {
            title.Should().Be(Page.StartBlindToRebuilds, "below Marten 9.50 nothing can see a rebuild started outside the studio");
        }
    }

    [Fact]
    public async Task Without_samples_there_is_no_arrow()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth());

        context.Render<Page>().FindAll(".ms-shard-trend").Should().BeEmpty();
    }

    // ---- Rebuilds -----------------------------------------------------------------------------------------------

    /// <summary>"Already rebuilding" is a state: a badge, a disabled Rebuild that says by whom, and a Start that says why not.</summary>
    [Fact]
    public async Task A_rebuild_the_daemon_runs_is_a_state_on_the_row_and_its_buttons()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data => data.WithStudioPause().WithRebuildingHere("DailySales"));

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find("[data-projection='DailySales'] .ms-projection-rebuilding").TextContent.Should().Be("rebuilding");

        IElement rebuild = page.Find("[data-projection='DailySales'] .ms-projection-rebuild");
        rebuild.HasAttribute("disabled").Should().BeTrue();
        rebuild.GetAttribute("title").Should().Contain("by something other than Marten Studio");

        IElement start = page.Find("[data-shard='DailySales:All'] .ms-shard-start");
        start.HasAttribute("disabled").Should().BeTrue();
        start.GetAttribute("title").Should().Contain("would run beside the replay");
    }

    /// <summary>A refused rebuild is information, not a success and not an error.</summary>
    [Fact]
    public async Task A_refused_rebuild_is_said_as_information()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data =>
            data.RebuildRefusal = "DailySales is already being rebuilt on this daemon, by something other than Marten Studio.");

        IRenderedComponent<Page> page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();
        page.WaitForElement(".ms-confirm-input").Input("DailySales");
        page.WaitForAssertion(() => page.Find(ConfirmButton).HasAttribute("disabled").Should().BeFalse());
        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.Toasts.Messages.Should()
            .ContainSingle(x => x.Message.Contains("already being rebuilt on this daemon", StringComparison.Ordinal)));
        context.Toasts.Messages.Should().NotContain(x => x.Message.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_rebuild_dialog_says_no_estimate_yet_honestly_and_what_a_stop_does()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth());

        IRenderedComponent<Page> page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        page.WaitForElement(".ms-rebuild-estimate").TextContent.Should().Contain("No estimate yet");
        page.Find(".ms-rebuild-drain-note").TextContent.Should().Contain("Projections.StopAndDrainTimeout").And.Contain("at-least-once");
    }

    /// <summary>The Pause dialog quotes the store's own StopAndDrainTimeout, and promises no clean finish Marten does not make.</summary>
    [Fact]
    public async Task The_pause_dialog_quotes_the_stores_drain_timeout()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data =>
            data.Daemon = data.Daemon with { StopAndDrainMilliseconds = 30_000 });

        IRenderedComponent<Page> page = context.Render<Page>();
        page.Find(".ms-daemon-pause").Click();

        page.WaitForAssertion(() => page.Markup.Should().Contain("Stopping gives each agent up to 30 s (Projections.StopAndDrainTimeout)"));
    }

    /// <summary>
    /// JasperFx cancels a drain only after a timeout greater than zero: a store whose StopAndDrainTimeout is zero, negative
    /// or infinite has no bound, and the screen says so rather than "up to 0 ms".
    /// </summary>
    [Fact]
    public async Task A_drain_timeout_that_is_not_positive_is_said_as_no_bound()
    {
        await using StudioComponentContext context = await ReadyAsync(new CachedHealth(), static data =>
            data.Daemon = data.Daemon with { StopAndDrainMilliseconds = 0 });

        IRenderedComponent<Page> page = context.Render<Page>();
        page.Find(".ms-daemon-pause").Click();

        page.WaitForAssertion(() => page.Markup.Should().Contain("with no time limit").And.NotContain("up to 0 ms"));
    }

    private static HealthSample Sample(DateTimeOffset at, double position, double highest, double? mark = null) => new(
        at,
        new Dictionary<string, double>
        {
            [DaemonSeries.ShardPosition("DailySales:All")] = position,
            [DaemonSeries.HighestSequence] = highest,
            [DaemonSeries.HighWaterMark] = mark ?? highest,
        });
}
