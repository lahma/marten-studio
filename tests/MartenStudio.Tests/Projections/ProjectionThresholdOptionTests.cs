using System.Text.RegularExpressions;

using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// 1.2-PROJ (c): <c>MartenStudioOptions.ProjectionThresholds</c> - its validation at startup, its precedence over a progression
/// row's columns and the studio's defaults for every consumer of a shard's lag thresholds, and the disclosure that says which
/// one applies to each projection. D03's recorded series and D04's judgement are in <c>ShardStalledCheckTests</c> and
/// <c>ProjectionFallingBehindCheckTests</c>.
/// </summary>
public class ProjectionThresholdOptionTests
{
    private static MartenStudioOptions Resolve(Action<MartenStudioOptions> configure)
    {
        ServiceCollection services = new();
        services.AddMartenStudio(configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
    }

    private static Dictionary<string, ProjectionLagThreshold> Option(string name, long warning, long critical) =>
        new(StringComparer.Ordinal) { [name] = new ProjectionLagThreshold(warning, critical) };

    private static ShardProgress Shard(string projection, long lag, long? warning = null, long? critical = null) =>
        new($"{projection}:All", projection, 0, lag, null, null, null, null, null, null, true, false,
            WarningThreshold: warning, CriticalThreshold: critical);

    // ---- The option and its validation --------------------------------------------------------------------------------

    [Fact]
    public void The_dictionary_is_never_null_empty_by_default_and_compared_exactly()
    {
        MartenStudioOptions options = new();

        options.ProjectionThresholds.Should().NotBeNull().And.BeEmpty();

        options.ProjectionThresholds["Trips"] = new ProjectionLagThreshold(WarningBehind: 5_000, CriticalBehind: 50_000);

        options.ProjectionThresholds.ContainsKey("trips").Should().BeFalse("a projection name is compared ordinally, as Marten compares it");
        options.ProjectionThresholds["Trips"].Should().Be(new ProjectionLagThreshold(5_000, 50_000));
    }

    [Fact]
    public void A_valid_entry_starts_and_an_unknown_projection_name_is_not_an_error()
    {
        MartenStudioOptions options = Resolve(static x =>
        {
            x.ProjectionThresholds["Trips"] = new(WarningBehind: 5_000, CriticalBehind: 50_000);
            x.ProjectionThresholds["NotRegisteredHere"] = new(1, 1);
        });

        options.ProjectionThresholds.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0L, 10L)]
    [InlineData(-5L, 10L)]
    [InlineData(5L, 0L)]
    [InlineData(5L, -1L)]
    [InlineData(100L, 99L)]
    public void An_entry_that_is_not_positive_or_has_critical_below_warning_is_refused_naming_the_projection(long warning, long critical)
    {
        Action starting = () => Resolve(x => x.ProjectionThresholds["Trips"] = new ProjectionLagThreshold(warning, critical));

        starting.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("MartenStudioOptions.ProjectionThresholds['Trips']");
    }

    [Fact]
    public void Critical_equal_to_warning_is_allowed()
    {
        Resolve(static x => x.ProjectionThresholds["Trips"] = new ProjectionLagThreshold(100, 100))
            .ProjectionThresholds["Trips"].CriticalBehind.Should().Be(100);
    }

    [Fact]
    public void A_null_entry_and_a_blank_name_are_refused()
    {
        Action nullEntry = () => Resolve(static x => x.ProjectionThresholds["Trips"] = null!);
        Action blankName = () => Resolve(static x => x.ProjectionThresholds[" "] = new ProjectionLagThreshold(1, 2));

        nullEntry.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("ProjectionThresholds['Trips'] cannot be null");
        blankName.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("cannot be empty or whitespace");
    }

    [Fact]
    public void A_hostile_name_is_escaped_and_bounded_in_the_message()
    {
        string hostile = "evil\n" + new string('x', 2_000);

        Action starting = () => Resolve(x => x.ProjectionThresholds[hostile] = new ProjectionLagThreshold(0, 0));

        string message = starting.Should().Throw<OptionsValidationException>().Which.Message;
        message.Should().NotContain("\n");
        message.Length.Should().BeLessThan(1_000);
    }

    // ---- Precedence: option > the row's columns > the studio's defaults ------------------------------------------------

    [Fact]
    public void The_option_wins_over_the_rows_columns_and_the_rows_win_over_the_defaults()
    {
        List<ShardProgress> shards =
        [
            Shard("Trips", lag: 6_000),
            Shard("Orders", lag: 6_000, warning: 10_000, critical: 20_000),
            Shard("Plain", lag: 6_000),
        ];

        // Trips: the option says 5,000 / 50,000 and the shard has no row of its own; Orders has a row.
        Dictionary<string, ProjectionLagThreshold> options = Option("Trips", 5_000, 50_000);
        options["Orders"] = new ProjectionLagThreshold(100, 200);

        List<ShardProgress> applied = LagThresholdResolver.Apply(shards, options);

        ShardProgress trips = applied.Single(static x => x.ProjectionName == "Trips");
        trips.EffectiveWarningThreshold.Should().Be(5_000);
        trips.EffectiveCriticalThreshold.Should().Be(50_000);
        trips.ThresholdsFromOption.Should().BeTrue();
        trips.Severity.Should().Be(LagSeverity.Warning, "6,000 is past the option's 5,000, not past the default red");

        ShardProgress orders = applied.Single(static x => x.ProjectionName == "Orders");
        orders.EffectiveWarningThreshold.Should().Be(100, "the option replaces the row's 10,000");
        orders.Severity.Should().Be(LagSeverity.Critical, "6,000 is past the option's 200");

        ShardProgress plain = applied.Single(static x => x.ProjectionName == "Plain");
        plain.ThresholdsFromOption.Should().BeFalse();
        plain.EffectiveWarningThreshold.Should().Be(ProjectionLagThresholds.Amber);
        plain.Severity.Should().Be(LagSeverity.Warning, "the studio's 1,000");

        // And without the option the row still wins over the default, as before.
        ShardProgress rowOnly = Shard("Orders", lag: 6_000, warning: 10_000, critical: 20_000);
        rowOnly.Severity.Should().Be(LagSeverity.Ok);
    }

    [Fact]
    public void An_option_can_make_a_lag_the_defaults_would_call_critical_fine()
    {
        Shard("Trips", lag: 150_000).Severity.Should().Be(LagSeverity.Critical);

        LagThresholdResolver.Apply([Shard("Trips", lag: 150_000)], Option("Trips", 200_000, 500_000))
            .Single().Severity.Should().Be(LagSeverity.Ok);
    }

    [Fact]
    public void No_options_changes_nothing()
    {
        ShardProgress shard = Shard("Trips", lag: 6_000, warning: 10_000);

        LagThresholdResolver.Apply([shard], new Dictionary<string, ProjectionLagThreshold>()).Should().Equal(shard);
        LagThresholdResolver.Apply([shard], null).Should().Equal(shard);
    }

    [Fact]
    public void A_trend_takes_the_option_too_and_says_so()
    {
        ShardTrend trend = new("Trips:All", LagDirection.Growing, 3_800, null, null, null, null, null, TimeSpan.Zero, 3, 100, null);

        ShardTrend applied = LagThresholdResolver.Apply(trend, new ProjectionLagThreshold(500, 3_000));

        applied.EffectiveWarning.Should().Be(500);
        applied.EffectiveCritical.Should().Be(3_000);
        applied.ThresholdsFromOption.Should().BeTrue();

        LagThresholdResolver.Apply(trend, null).Should().BeSameAs(trend);
    }

    [Fact]
    public void The_overview_tile_and_the_dot_judge_each_shard_against_its_own_thresholds()
    {
        ShardProgress lenient = LagThresholdResolver.Apply([Shard("Trips", lag: 90_000)], Option("Trips", 200_000, 500_000)).Single();
        ShardProgress strict = Shard("Other", lag: 1_500);

        ProjectionSummary summary = new(2, 2, 90_000, "Trips:All", 0, 0, null!, null, 100_000, [lenient, strict]);

        // The worst lag is 90,000 - red against the defaults - but it is fine for Trips; Other at 1,500 is the amber one.
        summary.Severity.Should().Be(LagSeverity.Warning);
        summary.WorstOverThreshold!.ShardName.Should().Be("Other:All");
        summary.NeedsAttention.Should().BeTrue();

        ProjectionSummary calm = new(1, 1, 90_000, "Trips:All", 0, 0, null!, null, 100_000, [lenient]);
        calm.Severity.Should().Be(LagSeverity.Ok);
        calm.NeedsAttention.Should().BeFalse();
        MartenNavBadges.Explain(calm).Should().BeNull();

        MartenNavBadges.Explain(summary).Should().Be("Other:All is 1,500 events behind");
    }

    [Fact]
    public void A_summary_without_a_shard_list_still_judges_the_worst_lag_by_the_defaults()
    {
        ProjectionSummary summary = new(1, 1, 2_000, "Trips:All", 0, 0, null!, null);

        summary.Severity.Should().Be(LagSeverity.Warning);
        summary.NeedsAttention.Should().BeTrue();
        MartenNavBadges.Explain(summary).Should().Be("Trips:All is 2,000 events behind");
    }

    // ---- Where each projection's thresholds come from (the disclosure) -----------------------------------------------

    [Fact]
    public void The_disclosure_says_which_source_applies_to_each_projection_and_which_option_matches_nothing()
    {
        List<ProjectionInfo> projections =
        [
            new("Trips", JasperFx.Events.Projections.ProjectionLifecycle.Async, "Projection", 1, "Trips", ["Trips:All"]),
            new("Orders", JasperFx.Events.Projections.ProjectionLifecycle.Async, "Projection", 1, "Orders", ["Orders:All"]),
            new("Plain", JasperFx.Events.Projections.ProjectionLifecycle.Async, "Projection", 1, "Plain", ["Plain:All"]),
        ];

        ShardProgress[] shards = [Shard("Trips", 10), Shard("Orders", 10, warning: 7, critical: 70), Shard("Plain", 10)];

        Dictionary<string, ProjectionLagThreshold> options = Option("Trips", 5_000, 50_000);
        options["Elsewhere"] = new ProjectionLagThreshold(1, 2);

        ThresholdDisclosure disclosure = LagThresholdResolver.Describe(projections, shards, options);

        disclosure.Projections.Should().Equal(
            new ProjectionThresholdSource("Orders", LagThresholdSource.ProgressionRow, 7, 70),
            new ProjectionThresholdSource("Trips", LagThresholdSource.Option, 5_000, 50_000));
        disclosure.UnmatchedOptions.Should().Equal("Elsewhere");
        disclosure.IsEmpty.Should().BeFalse();

        LagThresholdResolver.Describe(projections, [Shard("Plain", 1)], null).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task The_page_lists_the_thresholds_that_apply_to_each_projection_and_the_option_that_matches_none()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.Options.ProjectionThresholds["DailySales"] = new ProjectionLagThreshold(5_000, 50_000);
        context.Options.ProjectionThresholds["GoneProjection"] = new ProjectionLagThreshold(10, 20);
        context.ProjectionData.WithProjection("DailySales", sequence: 900).WithProjection("Plain", sequence: 900);
        context.ProjectionData.Thresholds = new ProjectionThresholds(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement thresholds = page.Find("details.ms-projection-thresholds");
        string text = Regex.Replace(thresholds.TextContent, @"\s+", " ");

        text.Should().Contain("MartenStudioOptions.ProjectionThresholds names a shard's projection");

        IElement trips = page.Find("li[data-threshold-projection='DailySales']");
        trips.GetAttribute("data-threshold-source").Should().Be("option");
        Regex.Replace(trips.TextContent, @"\s+", " ").Should()
            .Contain("DailySales: amber above 5,000, red above 50,000 (MartenStudioOptions.ProjectionThresholds)");

        page.FindAll("li[data-threshold-projection='Plain']").Should().BeEmpty("it runs on the studio's defaults");
        text.Should().Contain("Every other projection runs on the studio's defaults.");

        Regex.Replace(page.Find("li[data-threshold-unmatched='GoneProjection']").TextContent, @"\s+", " ").Should()
            .Contain("Configured for GoneProjection, which this store has no projection named.");
    }

    [Fact]
    public async Task A_projection_whose_row_carries_thresholds_is_said_to_take_them_from_the_row()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900).WithThresholds("DailySales:All", 50, 500);
        context.ProjectionData.Thresholds = new ProjectionThresholds(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        await context.ReadyAsync();

        IRenderedComponent<Page> page = context.Render<Page>();

        IElement row = page.Find("li[data-threshold-projection='DailySales']");
        row.GetAttribute("data-threshold-source").Should().Be("progressionrow");
        Regex.Replace(row.TextContent, @"\s+", " ").Should().Contain("amber above 50, red above 500 (its progression row's columns)");
    }

    [Fact]
    public async Task With_no_option_and_no_row_thresholds_the_disclosure_adds_nothing()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);
        context.ProjectionData.Thresholds = new ProjectionThresholds(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        await context.ReadyAsync();

        context.Render<Page>().FindAll(".ms-projection-threshold-sources").Should().BeEmpty();
    }

    [Fact]
    public async Task A_shard_the_option_judges_wears_the_options_numbers_in_its_hover_text()
    {
        await using StudioComponentContext context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales", sequence: 900);
        context.ProjectionData.WithThresholds("DailySales:All", 5_000, 50_000);
        context.ProjectionData.WithThresholdsFromOption("DailySales:All");
        await context.ReadyAsync();

        IElement lag = context.Render<Page>().Find("[data-shard='DailySales:All'] .ms-shard-lag");

        lag.GetAttribute("title").Should().Contain("Amber above 5,000, red above 50,000 (MartenStudioOptions.ProjectionThresholds for DailySales).");
        lag.ClassList.Should().Contain("ms-lag-ok");
    }
}
