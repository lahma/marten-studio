using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Projections;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using PostgresStudio.Services.Health;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// M2's chart, as bUnit renders it: on its own, from a shard's history, and on the projections screen - the SVG's title and
/// lines, the thresholds, the stall, a rebuild's "not enough samples yet", and the text equivalent beside it.
/// </summary>
public class ShardLagChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);

    private static LagPoint[] Every5s(params (long Position, long HighWater)[] samples) =>
        [.. samples.Select(static (x, i) => new LagPoint(Start + TimeSpan.FromSeconds(5 * i), x.Position, x.HighWater))];

    /// <summary>The chart as the page renders it: the figure from <see cref="LagChart" />, the sentences from the history.</summary>
    private static IRenderedComponent<ShardLagChart> Render(BunitContext context, ShardLagHistory history)
    {
        LagChartFigure figure = LagChart.Draw(history);

        return context.Render<ShardLagChart>(parameters => parameters
            .Add(x => x.ShardName, history.ShardName)
            .Add(x => x.Title, history.Title())
            .Add(x => x.Description, history.Description())
            .Add(x => x.HighWater, figure.HighWater)
            .Add(x => x.Position, figure.Position)
            .Add(x => x.Stalled, figure.Stalled)
            .Add(x => x.Warning, figure.Warning)
            .Add(x => x.Critical, figure.Critical)
            .Add(x => x.WarningTitle, "Warning threshold: " + DaemonCheckConfiguration.Number(history.EffectiveWarning) + " behind")
            .Add(x => x.CriticalTitle, "Critical threshold: " + DaemonCheckConfiguration.Number(history.EffectiveCritical) + " behind")
            .Add(x => x.Arrow, history.Arrow)
            .Add(x => x.Direction, history.DirectionClass)
            .Add(x => x.Summary, history.Summary())
            .Add(x => x.Stall, history.StallSentence())
            .Add(x => x.Rebuild, history.RebuildSentence())
            .Add(x => x.Thresholds, history.ThresholdSentence()));
    }

    // ---- The chart on its own ---------------------------------------------------------------------------------------

    [Fact]
    public void The_chart_is_an_SVG_image_with_a_title_and_its_text_equivalent_in_the_DOM()
    {
        using StudioComponentContext context = new();

        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            Every5s((0, 900), (100, 900), (200, 900), (300, 900), (400, 900)),
            new ShardLagContext(Spacing));

        IRenderedComponent<ShardLagChart> chart = Render(context, history);

        IElement svg = chart.Find("svg.ms-lag-chart-svg");
        svg.GetAttribute("role").Should().Be("img");
        svg.GetAttribute("viewBox").Should().Be("0 0 160 40");
        svg.GetAttribute("aria-label").Should().Be("Orders:All: its position against the high water over the last 20 s");
        chart.Find("svg > title").TextContent.Should().Be("Orders:All: its position against the high water over the last 20 s");
        chart.Find("svg > desc").TextContent.Should().Be("500 behind, catching up over the last 20 s, caught up in ~25 s.");

        chart.FindAll("polyline.ms-lag-chart-high-water").Should().ContainSingle();
        chart.FindAll("polyline.ms-lag-chart-position").Should().ContainSingle();

        // The text equivalent: the last value, the trend and the time to catch up, in the figure's caption.
        chart.Find("figcaption .ms-lag-chart-summary").TextContent.Should().Contain("500 behind, catching up over the last 20 s, caught up in ~25 s.");
        chart.Find("figcaption .ms-shard-trend").ClassList.Should().Contain("ms-shard-trend-draining");
        chart.Find("figcaption .ms-shard-trend").GetAttribute("aria-hidden").Should().Be("true", "the sentence beside it says the same");
        chart.Find("figure").GetAttribute("data-lag-chart").Should().Be("Orders:All");
    }

    [Fact]
    public void A_stalled_shard_is_coloured_and_said()
    {
        using StudioComponentContext context = new();

        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            Every5s((300, 400), (400, 500), (400, 700), (400, 900), (400, 1_100)),
            new ShardLagContext(Spacing));

        IRenderedComponent<ShardLagChart> chart = Render(context, history);

        chart.Find("figure").ClassList.Should().Contain("ms-lag-chart-is-stalled");
        chart.FindAll("polyline.ms-lag-chart-stalled").Should().HaveCount(3, "each still step while the high water rose");
        chart.Find("polyline.ms-lag-chart-stalled > title").TextContent.Should().StartWith("Stalled");
        chart.Find(".ms-lag-chart-stall").TextContent.Should().Be("Stalled: not moved for 15 s while the high water rose by 600.");
        chart.Find("figcaption .ms-shard-trend").ClassList.Should().Contain("ms-shard-trend-growing");
    }

    [Fact]
    public void A_shard_that_moves_is_not_coloured_stalled()
    {
        using StudioComponentContext context = new();

        IRenderedComponent<ShardLagChart> chart = Render(context, ShardLagHistory.Build(
            "Orders:All", Every5s((0, 100), (100, 200), (200, 300), (300, 400)), new ShardLagContext(Spacing)));

        chart.Find("figure").ClassList.Should().NotContain("ms-lag-chart-is-stalled");
        chart.FindAll("polyline.ms-lag-chart-stalled").Should().BeEmpty();
        chart.FindAll(".ms-lag-chart-stall").Should().BeEmpty();
    }

    [Fact]
    public void Thresholds_the_host_wrote_are_drawn_as_lines_and_said()
    {
        using StudioComponentContext context = new();

        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            Every5s((0, 200), (100, 400), (200, 600), (300, 800)),
            new ShardLagContext(Spacing, WarningThreshold: 500, CriticalThreshold: 5_000));

        IRenderedComponent<ShardLagChart> chart = Render(context, history);

        chart.FindAll("polyline.ms-lag-chart-threshold.ms-lag-chart-warning").Should().ContainSingle("a lag of 500 is within half of 500");
        chart.Find("polyline.ms-lag-chart-warning > title").TextContent.Should().Be("Warning threshold: 500 behind");
        chart.FindAll("polyline.ms-lag-chart-critical").Should().BeEmpty("500 is not within half of 5,000");
        chart.Find(".ms-lag-chart-thresholds").TextContent.Should().Be(
            "Thresholds: warning above 500 (its progression row's), critical above 5,000 (its progression row's).");
    }

    [Fact]
    public void The_studios_own_thresholds_are_drawn_but_not_said_as_the_rows()
    {
        using StudioComponentContext context = new();

        IRenderedComponent<ShardLagChart> chart = Render(context, ShardLagHistory.Build(
            "Orders:All", Every5s((0, 600), (0, 700)), new ShardLagContext(Spacing)));

        chart.FindAll("polyline.ms-lag-chart-warning").Should().ContainSingle("700 behind is within half of the studio's 1,000");
        chart.FindAll(".ms-lag-chart-thresholds").Should().BeEmpty("the host wrote none");
    }

    [Fact]
    public void A_rebuild_with_too_few_samples_says_not_enough_samples_yet()
    {
        using StudioComponentContext context = new();

        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            Every5s((100, 5_000), (200, 5_000)),
            new ShardLagContext(Spacing, Rebuilding: true));

        IRenderedComponent<ShardLagChart> chart = Render(context, history);

        chart.Find("figure").ClassList.Should().Contain("ms-lag-chart-rebuilding");
        chart.Find(".ms-lag-chart-rebuild").TextContent.Should().Be(
            "Rebuild: not enough samples yet - 1 of the 3 showing progress an estimate needs.");
        chart.Find("svg > desc").TextContent.Should().Contain("not enough samples yet");
    }

    [Fact]
    public void A_rebuild_with_three_samples_showing_progress_says_its_estimate_and_window()
    {
        using StudioComponentContext context = new();

        ShardLagHistory history = ShardLagHistory.Build(
            "Orders:All",
            Every5s((100, 5_000), (200, 5_000), (300, 5_000), (400, 5_000)),
            new ShardLagContext(Spacing, Rebuilding: true));

        Render(context, history).Find(".ms-lag-chart-rebuild").TextContent.Should().Be(
            "Rebuild: about 3 min left - 4,600 events at 20 ev/s, from the last 15 s.");
    }

    // ---- On the projections screen --------------------------------------------------------------------------------

    [Fact]
    public async Task The_screen_draws_each_shards_chart_in_its_row_and_its_own_arrow_wins_over_the_advisors()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);

        // The advisor says it is draining; this page's own samples say it is falling behind - and they win.
        HealthHistoryView advisor = new(
        [
            new HealthSample(Start, new Dictionary<string, double> { [DaemonSeries.ShardPosition("DailySales:All")] = 400, [DaemonSeries.HighestSequence] = 1_000 }),
            new HealthSample(Start + TimeSpan.FromMinutes(2), new Dictionary<string, double> { [DaemonSeries.ShardPosition("DailySales:All")] = 900, [DaemonSeries.HighestSequence] = 1_000 }),
        ]);
        context.ProjectionData.Trends["DailySales:All"] = ProjectionTrend.For(advisor, "DailySales:All");
        context.ProjectionData.LagHistories["DailySales:All"] = ShardLagHistory.Build(
            "DailySales:All",
            Every5s((870, 880), (880, 910), (890, 940), (900, 970)),
            new ShardLagContext(Spacing));
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement row = page.Find("[data-shard='DailySales:All']");
        row.QuerySelector(".ms-shard-history-cell figure.ms-lag-chart[data-lag-chart='DailySales:All']").Should().NotBeNull();
        row.QuerySelector(".ms-shard-history-cell svg > title")!.TextContent.Should().StartWith("DailySales:All: its position against the high water");

        IElement arrow = row.QuerySelector(".ms-shard-lag .ms-shard-trend")!;
        arrow.ClassList.Should().Contain("ms-shard-trend-growing");
        arrow.GetAttribute("aria-label").Should().StartWith("falling behind: the lag grew by");
        row.QuerySelector(".ms-shard-lag")!.GetAttribute("title").Should().Contain("Falling behind");
    }

    [Fact]
    public async Task While_the_screens_own_samples_are_too_few_the_advisors_arrow_stands()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);

        HealthHistoryView advisor = new(
        [
            new HealthSample(Start, new Dictionary<string, double> { [DaemonSeries.ShardPosition("DailySales:All")] = 400, [DaemonSeries.HighestSequence] = 1_000 }),
            new HealthSample(Start + TimeSpan.FromMinutes(2), new Dictionary<string, double> { [DaemonSeries.ShardPosition("DailySales:All")] = 900, [DaemonSeries.HighestSequence] = 1_000 }),
        ]);
        context.ProjectionData.Trends["DailySales:All"] = ProjectionTrend.For(advisor, "DailySales:All");
        context.ProjectionData.LagHistories["DailySales:All"] = ShardLagHistory.Build(
            "DailySales:All", Every5s((850, 1_000), (900, 1_000)), new ShardLagContext(Spacing));
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        page.Find("[data-shard='DailySales:All'] .ms-shard-lag .ms-shard-trend").ClassList.Should().Contain("ms-shard-trend-draining");
        page.Find("[data-shard='DailySales:All'] .ms-lag-chart-summary").TextContent.Should().Contain("no trend yet: 2 of the 4 samples");
    }

    [Fact]
    public async Task A_running_rebuild_puts_its_estimate_beside_the_lag()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 400);
        context.ProjectionData.LagHistories["DailySales:All"] = ShardLagHistory.Build(
            "DailySales:All",
            Every5s((100, 1_000), (200, 1_000), (300, 1_000), (400, 1_000)),
            new ShardLagContext(Spacing, Rebuilding: true));
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement eta = page.Find("[data-shard='DailySales:All'] .ms-shard-lag .ms-shard-eta");
        eta.TextContent.Trim().Should().Be("~30 s", "600 left at 20 ev/s");
        eta.GetAttribute("title").Should().Be("Rebuild: about 30 s left - 600 events at 20 ev/s, from the last 15 s.");
    }

    [Fact]
    public async Task A_shard_with_no_samples_and_one_never_started_draw_no_chart()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900).WithProjection("Never", hasProgressRow: false);
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        page.FindAll("figure.ms-lag-chart").Should().BeEmpty();
        page.Find("[data-shard='Never:All'] .ms-shard-history-cell").TextContent.Trim().Should().Be("never started");
    }

    [Fact]
    public async Task The_thresholds_that_apply_are_listed_with_the_stores_own_numbers()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);
        context.ProjectionData.Thresholds = new ProjectionThresholds(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), Spacing);
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement thresholds = page.Find("details.ms-projection-thresholds");
        thresholds.QuerySelector("summary")!.TextContent.Should().Be("Thresholds that apply");

        string text = Regex.Replace(thresholds.TextContent, @"\s+", " ");
        text.Should().Contain("amber above 1,000 events behind, red above 100,000");
        text.Should().Contain("warning_behind_threshold").And.Contain("critical_behind_threshold");
        text.Should().Contain("D04, falling behind:");
        text.Should().Contain("D03, alive but not moving:").And.Contain("no progress for 5 min").And.Contain("here 2 s").And.Contain("at most 20 min");
        text.Should().Contain("D05, failed or paused:").And.Contain("for 10 min after");
        text.Should().Contain("D01, high water held behind a gap:").And.Contain("Warning after 1 min, Critical after 10 min");
        text.Should().Contain("a sample at most every 5 s, the last 120 kept per shard (10 min)");
        text.Should().NotContain("AspNetCore", "Marten.AspNetCore's health checks keep their settings in their registration, which the studio cannot read");
    }

    [Fact]
    public async Task Without_thresholds_read_there_is_no_list()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);
        await context.ReadyAsync();

        context.Render<Page>().FindAll("details.ms-projection-thresholds").Should().BeEmpty();
    }
}
