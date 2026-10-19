using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Layout;
using MartenStudio.Components.Pages;
using MartenStudio.Components.Pages.Health;
using MartenStudio.Services.Health;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Health;

/// <summary>
/// SEC2 M1, as drawn: the Health page's checks table and empty state, the Overview's tile and region, and the
/// sidebar's badge never read as an all-clear while the advisor has findings this visitor is not shown - for the
/// review's operator, allowed tenant <c>acme</c> only, while C04, C06 and T14 have findings behind the database-wide
/// gate. The service's half is <see cref="HealthWithheldStatusTests" />.
/// </summary>
public class HealthWithheldPageTests
{
    private const string Reason = "needs the store policy to allow this database as a whole, with no tenant selected in the policy's resource";

    private static async Task<StudioComponentContext> ReadyAsync(Action<StudioComponentContext>? configure = null)
    {
        StudioComponentContext context = new();
        configure?.Invoke(context);
        await context.ReadyAsync();
        return context;
    }

    private static HealthCheckStatus Row(string code, HealthArea area, int shown, bool withheld) =>
        new(code, code + " check", area, HealthCostTier.Catalog, HealthCheckOutcome.Checked, shown, null, null, null,
            "https://example.invalid/" + code.ToLowerInvariant(), [], withheld);

    /// <summary>What the service hands the tenant operator: nothing shown, three withheld, every database-wide row marked.</summary>
    private static HealthReport TenantOperatorsReport(HealthReport report) => report with
    {
        Findings = [],
        WithheldCount = 3,
        WithheldReason = Reason,
        Checks =
        [
            Row("C04", HealthArea.Connections, 0, withheld: true),
            Row("C06", HealthArea.Connections, 0, withheld: true),
            Row("T14", HealthArea.Tables, 0, withheld: true),
            Row("T90", HealthArea.Tables, 0, withheld: true),
            Row("K01", HealthArea.Configuration, 0, withheld: false),
        ],
    };

    // ----------------------------------------------------------------------------------------------
    // The Health page
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Short_of_the_gate_every_row_says_its_findings_are_not_shown_and_none_says_nothing_found()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = TenantOperatorsReport(context.Health.Report);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-check-table tbody tr").Should().HaveCount(5));
        page.TextOfAll(".ms-health-outcome").Should().Equal(
            "checked, findings not shown to you",
            "checked, findings not shown to you",
            "checked, findings not shown to you",
            "checked, findings not shown to you",
            "checked, nothing found");

        page.Find(".ms-health-withheld").TextContent.Trim().Should().Be("3 findings not shown to you: " + Reason + ".");
    }

    [Fact]
    public async Task With_every_finding_withheld_the_empty_state_is_not_an_all_clear()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = TenantOperatorsReport(context.Health.Report);

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-empty").Should().ContainSingle());
        page.Find(".ms-health-empty .ms-empty-title").TextContent.Trim().Should().Be("Nothing to show you");
        page.Find(".ms-health-empty .ms-empty-description").TextContent.Should()
            .Contain("not shown to you").And.NotContain("No check found anything");
    }

    [Fact]
    public async Task A_row_that_shows_some_of_its_findings_says_how_many_and_that_others_are_not_shown()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.WithFindings(FakeHealthService.Finding("K01", HealthSeverity.Warning, "About the store"));
        context.Health.Report = context.Health.Report with
        {
            WithheldCount = 1,
            WithheldReason = "names a tenant the store policy does not show you",
            Checks = [Row("K01", HealthArea.Configuration, 1, withheld: true)],
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.TextOfAll(".ms-health-outcome").Should().Equal("checked, 1 shown, others not shown to you"));
    }

    /// <summary>The quiet half: nothing withheld, and the page says "nothing found" and "Nothing to report" as before.</summary>
    [Fact]
    public async Task With_nothing_withheld_the_page_says_nothing_was_found()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with { Checks = [Row("T90", HealthArea.Tables, 0, withheld: false)] };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.TextOfAll(".ms-health-outcome").Should().Equal("checked, nothing found"));
        page.Find(".ms-health-empty .ms-empty-title").TextContent.Trim().Should().Be("Nothing to report");
        page.FindAll(".ms-health-withheld").Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------------------
    // The Overview
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overview_tile_is_never_green_nothing_to_report_while_findings_are_withheld()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());
        context.Health.Report = TenantOperatorsReport(context.Health.Report);

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.StatCardValue("Health").Should().Be("3 findings not shown"));
        page.StatCardClasses("Health").Should().Contain("ms-stat-card-info").And.NotContain("ms-stat-card-success");

        string region = page.Find(".ms-overview-health").TextContent;
        region.Should().Contain("3 findings not shown to you: " + Reason).And.Contain("See what was checked")
            .And.NotContain("Nothing to report");
    }

    [Fact]
    public async Task Beside_findings_the_visitor_may_see_the_overview_says_how_many_others_are_not_shown()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());
        context.Health.WithFindings(FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));
        context.Health.Report = context.Health.Report with { WithheldCount = 3, WithheldReason = Reason };

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.StatCardValue("Health").Should().Be("1 warning"));
        page.StatCardClasses("Health").Should().Contain("ms-stat-card-warning");
        page.Find(".ms-overview-health-withheld").TextContent.Trim().Should().Be("3 findings not shown to you: " + Reason + ".");
    }

    /// <summary>The quiet half: nothing withheld, nothing found, and the tile is the green "nothing to report" it was.</summary>
    [Fact]
    public async Task With_nothing_withheld_the_overview_tile_is_green_nothing_to_report()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.StatCardValue("Health").Should().Be("nothing to report"));
        page.StatCardClasses("Health").Should().Contain("ms-stat-card-success");
        page.FindAll(".ms-overview-health-withheld").Should().BeEmpty();
        page.Find(".ms-overview-health").TextContent.Should().Contain("Nothing to report.");
    }

    // ----------------------------------------------------------------------------------------------
    // The sidebar's badge
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_badge_counts_what_the_visitor_may_see_and_its_name_says_how_many_are_not_shown()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 1, 0, [new HealthGlanceItem("default|db|D01", "Pinned")], Withheld: 3);

        var nav = context.Render<NavMenu>();

        IElement badge = HealthEntry(nav).QuerySelector(".ms-nav-badge")!;
        badge.TextContent.Trim().Should().Be("1", "a withheld finding is said, never counted");
        badge.ClassList.Should().Contain("ms-nav-badge-danger");
        badge.GetAttribute("aria-label").Should().StartWith("1 critical finding, and 3 findings not shown to you, as of ");
    }

    /// <summary>
    /// Findings the visitor may not see draw no badge of their own: their severity is the gate's to withhold, and a badge
    /// that cannot say how bad would sit on every page. An absent badge is no claim; the page and the Overview say it.
    /// </summary>
    [Fact]
    public async Task Withheld_findings_alone_draw_no_badge()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 0, 0, [], Withheld: 3);

        var nav = context.Render<NavMenu>();

        HealthEntry(nav).QuerySelector(".ms-nav-badge").Should().BeNull();
    }

    private static IElement HealthEntry(IRenderedComponent<NavMenu> nav) =>
        nav.FindAll("a.ms-nav-link").Single(static x => x.GetAttribute("href") == "health");
}
