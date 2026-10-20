using AngleSharp.Dom;

using Bunit;

using PostgresStudio.Components.Layout;
using MartenStudio.Components.Pages;
using PostgresStudio.Components.Pages.Health;
using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Database;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The Health page, the Overview's tile and list, the sidebar's badge and the toast - in bUnit, over the fake
/// health service (acceptance 6).
/// </summary>
public class HealthPageTests
{
    private static async Task<StudioComponentContext> ReadyAsync(Action<StudioComponentContext>? configure = null)
    {
        StudioComponentContext context = new();
        configure?.Invoke(context);
        await context.ReadyAsync();
        return context;
    }

    private static HealthFinding WithStatement(HealthFinding finding) => finding with
    {
        Statements = [new HealthStatement("select relname from pg_class where relnamespace = @schema", ["@schema"])],
    };

    // ----------------------------------------------------------------------------------------------
    // No I/O in prerender
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_reads_nothing_while_prerendered_and_once_in_the_circuit()
    {
        await using StudioComponentContext context = await ReadyAsync();

        string html = await DatabasePagePrerenderTests.StaticAsync<HealthPage>(context);

        html.Should().Contain("ms-loading-spinner", "a static render draws the loading state");
        context.Health.Requests.Should().BeEmpty("a sweep is not something a prerender should start");

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-checks").Should().ContainSingle());
        context.Health.Requests.Should().ContainSingle().Which.Should().Be(HealthReportRequest.Regular);
    }

    [Fact]
    public async Task The_overview_reads_no_health_while_prerendered()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());

        await DatabasePagePrerenderTests.StaticAsync<Overview>(context);

        context.Health.Requests.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------------------
    // The page
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Findings_are_grouped_by_area_worst_first_with_everything_a_finding_carries()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.WithFindings(
            WithStatement(FakeHealthService.Finding("D01", HealthSeverity.Critical, "The high-water mark is pinned", HealthArea.Daemon)),
            FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"),
            FakeHealthService.Finding("K02", HealthSeverity.Info, "Extended tracking is off"));

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-finding").Should().HaveCount(3));

        page.TextOfAll(".ms-health-area > .ms-section-title").Should().Equal("Daemon and high water", "Configuration");

        IElement pinned = page.Find("#finding-d01");
        pinned.QuerySelector(".ms-health-severity")!.TextContent.Trim().Should().Be("Critical");
        pinned.QuerySelector(".ms-health-finding-title")!.TextContent.Trim().Should().Be("The high-water mark is pinned");
        pinned.QuerySelector(".ms-health-evidence dt")!.TextContent.Trim().Should().Be("Setting");
        pinned.QuerySelector(".ms-health-measured")!.TextContent.Should().Contain("2026-10-15 09:00:00", "in the visitor's time zone, UTC in the test");
        pinned.QuerySelector(".ms-sql-disclosure code")!.TextContent.Should().Contain("@schema");
        pinned.QuerySelector(".ms-sql-parameters")!.TextContent.Should().Contain("@schema");
        pinned.QuerySelector(".ms-health-remedy-code")!.TextContent.Should().Be("opts.Something = true;");
        pinned.QuerySelector(".ms-copy-button")!.GetAttribute("aria-label").Should().Be("Copy the remedy for D01");
        pinned.QuerySelector(".ms-health-docs")!.GetAttribute("href").Should().Be("https://example.invalid/docs/health/d01");
    }

    [Fact]
    public async Task A_remedy_that_needs_a_newer_Marten_says_which_and_what_this_host_runs()
    {
        await using StudioComponentContext context = await ReadyAsync();

        HealthFinding finding = FakeHealthService.Finding("T06", HealthSeverity.Warning, "Low HOT ratio", HealthArea.Tables) with
        {
            Remedy = new HealthRemedy("opts.Schema.For<Order>().FillFactor(80);", requires: RuntimeFeature.DocumentFillFactor),
        };

        context.Health.Report = context.Health.Report with
        {
            Findings = [new HealthReportFinding(finding, finding.Evidence, "default|localhost.marten|T06", "https://example.invalid", false, "Marten 9.31.0")],
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-requirement").TextContent.Trim()
            .Should().Be("Marten 9.47+; this host runs Marten 9.31.0"));
        page.Find(".ms-health-requirement").ClassList.Should().Contain("ms-health-requirement-missing");
    }

    [Fact]
    public async Task Muting_a_finding_moves_it_out_of_the_list_and_into_this_browsers_storage()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.WithFindings(FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-area .ms-health-finding").Should().ContainSingle());

        page.Find(".ms-health-mute").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-health-area .ms-health-finding").Should().BeEmpty());
        page.Find(".ms-health-muted summary").TextContent.Should().Contain("1 finding muted in this browser");
        page.Find(".ms-health-empty .ms-empty-description").TextContent.Should().Contain("muted in this browser");

        context.JSInterop.Invocations.Should().Contain(static x => x.Identifier == "martenStudio.health.setMuted");

        page.Find(".ms-health-muted .ms-health-mute").Click();
        page.WaitForAssertion(() => page.FindAll(".ms-health-area .ms-health-finding").Should().ContainSingle());
    }

    /// <summary>Blocked storage: the page is correct without it - nothing is remembered, nothing breaks.</summary>
    [Fact]
    public async Task A_browser_whose_storage_refuses_still_gets_a_working_page()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetException(new Microsoft.JSInterop.JSException("blocked"));
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetException(new Microsoft.JSInterop.JSException("blocked"));
        context.Health.WithFindings(FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"));

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-health-area .ms-health-finding").Should().ContainSingle());

        page.Find(".ms-health-mute").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-health-area .ms-health-finding").Should().BeEmpty());
        page.FindAll(".ms-error-alert").Should().BeEmpty("a mute the browser could not keep is still a mute for this tab");
    }

    [Fact]
    public async Task With_no_deeper_check_the_button_is_off_and_the_page_says_why()
    {
        await using StudioComponentContext context = await ReadyAsync();

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-deeper").HasAttribute("disabled").Should().BeTrue());
        page.Find(".ms-health-deeper-note").TextContent.Should().Contain("no such check yet");
    }

    [Fact]
    public async Task With_a_deeper_check_the_button_runs_it_and_only_the_button_does()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with { DeeperChecksExist = true, DeeperChecksAllowed = true };

        var page = context.Render<HealthPage>();
        page.WaitForAssertion(() => page.Find(".ms-health-deeper").HasAttribute("disabled").Should().BeFalse());

        context.Health.Requests.Should().OnlyContain(static x => x.Deeper == HealthDeeperChecks.IfCached);

        page.Find(".ms-health-deeper").Click();

        page.WaitForAssertion(() => context.Health.Requests.Should().Contain(static x => x.Deeper == HealthDeeperChecks.Run));
    }

    /// <summary>
    /// H1-fix item 1, the page's half: a visitor the service would not let start the deeper checks sees the
    /// button off and is told the policy it needs. The refusal itself is the service's (HealthGateTests).
    /// </summary>
    [Fact]
    public async Task A_visitor_who_may_not_start_the_deeper_checks_sees_the_button_off_and_why()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            DeeperChecksExist = true,
            DeeperChecksAllowed = false,
            DeeperChecksRefused = "needs the store policy to allow this database as a whole",
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-deeper").HasAttribute("disabled").Should().BeTrue());
        page.Find(".ms-health-deeper").GetAttribute("title").Should().Contain("as a whole");
        page.Find(".ms-health-deeper-note").TextContent.Should().Contain("off for you").And.Contain("needs the store policy");
    }

    [Fact]
    public async Task The_studio_cost_line_says_what_the_studio_spent_and_what_it_does_not_count()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            Cost = new StudioCost(TimeSpan.FromMilliseconds(1_234), 37, TimeSpan.FromMinutes(5)),
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-cost").TextContent.Should()
            .Contain("1.23 s of database time in 37 read-only sessions against this database over the last 5 minutes")
            .And.Contain("not counted yet"));
    }

    [Fact]
    public async Task A_withheld_studio_cost_says_why()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            Cost = null,
            CostWithheld = "needs the store policy to allow this database as a whole",
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-cost").TextContent.Trim()
            .Should().Be("Studio cost: withheld - needs the store policy to allow this database as a whole."));
    }

    /// <summary>Review item 14: a finding can point at the studio screen that helps, with the visitor's scope.</summary>
    [Fact]
    public async Task A_finding_links_to_the_screen_that_helps_with_it()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.WithFindings(FakeHealthService.Finding("D06", HealthSeverity.Warning, "Dead letters accumulating", HealthArea.Daemon) with
        {
            Link = new HealthLink("Open the dead letters", "events/dead-letters", [new("projection", "OrderSummary")]),
        });

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-link a").TextContent.Should().Be("Open the dead letters"));
        page.Find(".ms-health-link a").GetAttribute("href").Should()
            .StartWith("events/dead-letters?projection=OrderSummary").And.Contain("store=default");
    }

    [Fact]
    public async Task A_refusal_is_a_notice_and_nothing_else()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = HealthReport.Refused(HealthRefusal.StorePolicy, "You are not authorized for this store, database or tenant.");

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-health-refusal").TextContent.Should().Contain("not authorized"));
        page.FindAll(".ms-health-checks").Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_that_throws_is_an_error_on_the_page_not_a_dead_circuit()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Failure = new InvalidOperationException("the sweep fell over");

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("the sweep fell over"));
    }

    [Fact]
    public async Task Every_check_is_listed_with_its_outcome_inside_a_scroll_region()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.Health.Report = context.Health.Report with
        {
            Checks =
            [
                new HealthCheckStatus("K01", "AutoCreate in Production", HealthArea.Configuration, HealthCostTier.InProcess, HealthCheckOutcome.Checked, 0, "The environment is Development; this check is about Production.", null, null, "https://example.invalid/k01", []),
                new HealthCheckStatus("D90", "Needs newer", HealthArea.Daemon, HealthCostTier.Catalog, HealthCheckOutcome.NotApplicable, 0, "Needs Marten 9.50+; this host runs Marten 9.31.0.", null, null, "https://example.invalid/d90", []),
                new HealthCheckStatus("T90", "Broke", HealthArea.Tables, HealthCostTier.Catalog, HealthCheckOutcome.Failed, 0, null, "Postgres refused the check's statement (SQLSTATE 42P01).", "42P01", "https://example.invalid/t90", []),
            ],
        };

        var page = context.Render<HealthPage>();

        page.WaitForAssertion(() => page.FindAll(".ms-health-check-table tbody tr").Should().HaveCount(3));
        page.Find(".ms-health-checks [role=region]").GetAttribute("aria-label").Should().Be("Health checks table");
        page.TextOfAll(".ms-health-outcome").Should().Equal("checked, nothing found", "not applicable on this host", "could not check");
        page.FindAll(".ms-health-outcome-detail")[2].TextContent.Should().Contain("42P01");
    }

    // ----------------------------------------------------------------------------------------------
    // The Overview's tile and its three worst findings
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overview_tile_counts_the_worst_and_lists_the_three_worst_findings()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());
        context.Health.WithFindings(
            FakeHealthService.Finding("D01", HealthSeverity.Critical, "Pinned", HealthArea.Daemon),
            FakeHealthService.Finding("D02", HealthSeverity.Critical, "Nobody runs the daemon", HealthArea.Daemon),
            FakeHealthService.Finding("K01", HealthSeverity.Warning, "AutoCreate in Production"),
            FakeHealthService.Finding("K02", HealthSeverity.Info, "Extended tracking is off"));

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.StatCardValue("Health").Should().Be("2 criticals"));
        page.StatCardClasses("Health").Should().Contain("ms-stat-card-danger");
        page.TextOfAll(".ms-overview-health-list .ms-overview-list-name").Should().Equal("Pinned", "Nobody runs the daemon", "AutoCreate in Production");
        page.Find(".ms-overview-health-list a").GetAttribute("href").Should().StartWith("health?").And.EndWith("#finding-d01");
    }

    [Fact]
    public async Task An_overview_with_nothing_to_report_says_so_and_links_to_what_was_checked()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.StoreInfo.WithStore());

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.StatCardValue("Health").Should().Be("nothing to report"));
        page.StatCardClasses("Health").Should().Contain("ms-stat-card-success");
        page.Find(".ms-overview-health").TextContent.Should().Contain("See what was checked");
    }

    // ----------------------------------------------------------------------------------------------
    // The sidebar's badge and the toast
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_health_entry_wears_a_red_count_when_a_finding_is_critical()
    {
        await using StudioComponentContext context = await ReadyAsync();
        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 1, 2, [new HealthGlanceItem("default|db|D01", "Pinned")]);

        var nav = context.Render<NavMenu>();

        IElement health = nav.FindAll("a.ms-nav-link").Single(static x => x.GetAttribute("href") == "health");
        IElement badge = health.QuerySelector(".ms-nav-badge")!;
        badge.TextContent.Trim().Should().Be("3");
        badge.ClassList.Should().Contain("ms-nav-badge-danger");
        badge.GetAttribute("aria-label").Should().StartWith("1 critical finding and 2 warnings");
        health.QuerySelector(".ms-nav-link-text")!.TextContent.Trim().Should().Be("Health", "a badge never joins the link's name");
    }

    [Fact]
    public async Task No_glance_is_no_badge()
    {
        await using StudioComponentContext context = await ReadyAsync();

        var nav = context.Render<NavMenu>();

        nav.FindAll("a.ms-nav-link").Single(static x => x.GetAttribute("href") == "health")
            .QuerySelector(".ms-nav-badge").Should().BeNull();
    }

    [Fact]
    public async Task A_Critical_that_appears_while_somebody_watches_is_toasted_once()
    {
        await using StudioComponentContext context = await ReadyAsync(static x => x.Options.RefreshInterval = TimeSpan.FromMilliseconds(20));

        // Stamped with the scope they are read for, as the real health service stamps a glance: the toast ignores one
        // read for any other scope.
        PostgresStudio.Services.StudioScope scope = context.State.ActiveScope!;

        // The first look at a database only takes note of what is there: a warning, and no Critical.
        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 0, 1, []) { Scope = scope };

        var nav = context.Render<NavMenu>();
        nav.WaitForAssertion(() => context.NavBadges.Reads.Should().BeGreaterThan(2), TimeSpan.FromSeconds(5));
        context.Toasts.Messages.Should().BeEmpty();

        context.NavBadges.Health = new HealthGlance(DateTimeOffset.UtcNow, 1, 0, [new HealthGlanceItem("default|db|D01", "The high-water mark is pinned")])
        {
            Scope = scope,
        };
        int readsWhenItAppeared = context.NavBadges.Reads;

        nav.WaitForAssertion(() => context.Toasts.Messages.Should().ContainSingle(), TimeSpan.FromSeconds(5));
        nav.WaitForAssertion(() => context.NavBadges.Reads.Should().BeGreaterThan(readsWhenItAppeared + 3), TimeSpan.FromSeconds(5));

        context.Toasts.Messages.Should().ContainSingle("a Critical that stays is toasted once")
            .Which.Message.Should().Contain("The high-water mark is pinned");
    }
}
