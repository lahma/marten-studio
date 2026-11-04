using System.Globalization;

using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Tests.Database;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-1 S3 to S7: the Stores page reads nothing while it is prerendered, enumerates once when the circuit renders it,
/// draws each store and database with a health chip from the shared sweep's last result, never starts a sweep by being opened,
/// and bounds what a click costs.
/// </summary>
public class StoresPageTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static HealthGlance Glance(int critical = 0, int warning = 0, int withheld = 0, int suggestions = 0) =>
        new(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero), critical, warning, [], withheld) { Suggestions = suggestions };

    // -----------------------------------------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_read_while_prerendered_and_the_circuit_enumerates_once()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a");

        string html = await DatabasePagePrerenderTests.StaticAsync<Stores>(context);

        html.Should().Contain("Loading stores");
        context.StoreInfo.Loads.Should().Be(0, "a prerender reads nothing");
        context.Health.Glances.Should().Be(0);

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());
        context.StoreInfo.Loads.Should().Be(1, "exactly one enumeration once the page is interactive");
        context.StoreInfo.Keys.Should().Equal((string?) null);
    }

    [Fact]
    public async Task Opening_the_page_starts_no_sweep()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a", "db-b");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => context.Health.Glanced.Should().HaveCount(2));
        context.Health.Checks.Should().BeEmpty("the chips are the shared sweep's last result; only Check runs one");
        context.Health.Requests.Should().BeEmpty("the page never asks for a report");
    }

    [Fact]
    public async Task Polling_reads_the_glances_and_never_the_stores_again()
    {
        await using StudioComponentContext context = new();
        context.Options.RefreshInterval = TimeSpan.FromMilliseconds(50);
        context.StoreInfo.WithDatabases("default", "Default", "db-a");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => context.Health.Glanced.Count.Should().BeGreaterThan(3), TimeSpan.FromSeconds(10));
        context.StoreInfo.Loads.Should().Be(1, "a poll re-reads the in-process glances and never GetOverviewAsync");
    }

    [Fact]
    public async Task A_failure_to_enumerate_is_shown_with_a_retry()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.Failure = new InvalidOperationException("the master database did not answer");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("did not answer"));

        context.StoreInfo.Failure = null;
        context.StoreInfo.WithDatabases("default", "Default", "db-a");
        page.Find(".ms-error-alert button").Click();

        page.WaitForAssertion(() =>
        {
            context.StoreInfo.Loads.Should().Be(2);
            page.FindAll(".ms-error-alert").Should().BeEmpty();
            page.FindAll("tr.ms-table-row").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task No_store_at_all_is_an_empty_state_that_says_what_to_do()
    {
        await using StudioComponentContext context = new();

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.Find(".ms-empty-title").TextContent.Should().Be("No Marten store to show"));
        page.FindAll(".ms-stores-check-all").Should().BeEmpty("there is nothing to check");
    }

    // -----------------------------------------------------------------------------------------------
    // Rows
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_that_will_not_build_is_shown_with_its_reason_bounded()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo
            .WithDatabases("default", "Default", "db-a")
            .WithUnavailableStore("broken", "Broken", "no connection string was configured " + new string('x', 5_000));

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll(".ms-stores-store").Should().HaveCount(2));

        IElement broken = page.FindAll(".ms-stores-store")[1];
        broken.QuerySelector(".ms-section-key")!.TextContent.Trim().Should().Be("broken");
        string reason = broken.QuerySelector(".ms-error-alert span")!.TextContent;
        reason.Should().StartWith("This store could not be built: no connection string was configured");
        reason.Length.Should().BeLessThan(4_100, "the host's exception text is bounded at 4,000 characters (S7)");
        broken.QuerySelectorAll("table").Should().BeEmpty("no databases are listed for a store that did not build");
    }

    [Fact]
    public async Task A_store_row_says_its_tenancy_document_types_event_store_and_daemon()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a", "db-b");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll(".ms-stores-store").Should().ContainSingle());

        page.Find(".ms-stores-tenancy .ms-kv-value").TextContent.Trim().Should().Be("A database per tenant, configured");
        page.Find(".ms-stores-documents .ms-kv-value").TextContent.Trim().Should().Be("3 (2 conjoined)");
        string events = string.Join(' ', page.Find(".ms-stores-events .ms-kv-value").TextContent.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
        events.Should().Be("Guid stream ids, Quick append, Single tenancy, partitioned by tenant");
        page.Find(".ms-stores-daemon .ms-kv-value").TextContent.Trim()
            .Should().Be("A projection coordinator is registered in this process (Solo); Projections says whether it runs");
        page.Find(".ms-stores-postgres .ms-kv-value").TextContent.Trim().Should().Be("17.2");
    }

    [Fact]
    public async Task The_current_store_is_marked_and_each_row_links_to_its_overview_and_health()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a").WithDatabases("other", "Other", "db-x");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "db-a")
            .WithStore("other", "Other", databaseIdentities: "db-x");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll(".ms-stores-store").Should().HaveCount(2));

        IElement[] sections = [.. page.FindAll(".ms-stores-store")];
        sections[0].QuerySelector(".ms-stores-current")!.TextContent.Trim().Should().Be("Current");
        sections[1].QuerySelector(".ms-stores-current").Should().BeNull();

        sections[1].QuerySelector(".ms-stores-open-overview")!.GetAttribute("href").Should().Be("?store=other");

        string[] rowLinks = [.. sections[1].QuerySelectorAll("tbody td:last-child a").Select(static x => x.GetAttribute("href")!)];
        rowLinks.Should().Equal("?store=other&db=db-x", "health?store=other&db=db-x");
    }

    [Fact]
    public async Task A_store_lists_twenty_databases_and_points_at_the_pickers_for_the_rest()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("big", "Big", [.. Enumerable.Range(0, 23).Select(static x => "db-" + x.ToString("00", CultureInfo.InvariantCulture))]);

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().HaveCount(20));
        page.FindAll("tr.ms-table-row td:first-child").Select(static x => x.TextContent.Trim()).Should().StartWith("db-00").And.EndWith("db-19");
        page.Find(".ms-stores-more").TextContent.Trim().Should().Be("3 more databases. The header's database picker lists every one.");
        context.Health.Glanced.Should().HaveCount(20, "a database with no row has no chip to read");
    }

    [Fact]
    public async Task Each_database_table_is_a_labelled_scroll_region()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());
        IElement region = page.Find("[role=region]");
        region.GetAttribute("aria-label").Should().Be("Databases of Default");
        region.GetAttribute("tabindex").Should().Be("0");
        region.QuerySelector("table").Should().NotBeNull("D25: a table sits in its own scroll region");
    }

    [Fact]
    public async Task Two_stores_naming_one_database_identity_render_and_keep_a_chip_each()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("a", "A", "shared").WithDatabases("b", "B", "shared");

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().HaveCount(2));
        context.Health.Glanced.Select(static x => x.StoreKey).Should().BeEquivalentTo(["a", "b"]);
    }

    // -----------------------------------------------------------------------------------------------
    // The chip
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_chip_state_reads_as_the_design_says_and_a_withheld_finding_is_never_green()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "crit", "warn", "hidden", "tips", "clean", "stale");
        context.Health.GlanceResults["crit"] = Glance(critical: 2, warning: 1);
        context.Health.GlanceResults["warn"] = Glance(warning: 1, suggestions: 4);
        context.Health.GlanceResults["hidden"] = Glance(withheld: 3, suggestions: 2);
        context.Health.GlanceResults["tips"] = Glance(suggestions: 1);
        context.Health.GlanceResults["clean"] = Glance();

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll(".ms-stores-chip").Should().HaveCount(6));

        Dictionary<string, IElement> chips = page.FindAll("tr.ms-table-row")
            .ToDictionary(static x => x.QuerySelector("td")!.TextContent.Trim(), static x => x.QuerySelector(".ms-stores-chip")!);

        chips["crit"].TextContent.Trim().Should().Be("2 critical");
        chips["crit"].ClassList.Should().Contain("ms-health-severity-critical");
        chips["warn"].TextContent.Trim().Should().Be("1 warning");
        chips["warn"].ClassList.Should().Contain("ms-health-severity-warning");

        chips["hidden"].TextContent.Trim().Should().Be("3 not shown");
        chips["hidden"].ClassList.Should().Contain("ms-health-severity-info").And.NotContain("ms-stores-chip-ok", "never green while anything is withheld");

        chips["tips"].TextContent.Trim().Should().Be("1 suggestion");
        chips["clean"].TextContent.Trim().Should().Be("nothing to report");
        chips["clean"].ClassList.Should().Contain("ms-stores-chip-ok");

        chips["stale"].TextContent.Trim().Should().Be("not checked recently");
        chips["stale"].ClassList.Should().Contain("ms-stores-chip-muted");
    }

    [Fact]
    public async Task Only_a_stale_row_offers_Check()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "fresh", "stale");
        context.Health.GlanceResults["fresh"] = Glance();

        IRenderedComponent<Stores> page = context.Render<Stores>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().HaveCount(2));
        IElement[] rows = [.. page.FindAll("tr.ms-table-row")];
        rows[0].QuerySelectorAll(".ms-stores-check").Should().BeEmpty();
        rows[1].QuerySelectorAll(".ms-stores-check").Should().ContainSingle();
    }

    // -----------------------------------------------------------------------------------------------
    // Check and Check all
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_runs_one_sweep_for_that_database_and_draws_its_chip()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "db-a", "db-b");
        context.Health.CheckResults["db-b"] = Glance(warning: 2);

        IRenderedComponent<Stores> page = context.Render<Stores>();
        page.WaitForAssertion(() => page.FindAll(".ms-stores-check").Should().HaveCount(2));

        page.FindAll("tr.ms-table-row")[1].QuerySelector(".ms-stores-check")!.Click();

        page.WaitForAssertion(() =>
        {
            context.Health.Checks.Should().ContainSingle().Which.Should().Be(new StudioScope("default", "db-b", null));
            page.FindAll("tr.ms-table-row")[1].QuerySelector(".ms-stores-chip")!.TextContent.Trim().Should().Be("2 warnings");
            page.FindAll("tr.ms-table-row")[1].QuerySelectorAll(".ms-stores-check").Should().BeEmpty("a checked row has a recent sweep");
        });

        context.StoreInfo.Loads.Should().Be(1, "Check re-reads nothing of the stores");
    }

    [Fact]
    public async Task Check_all_sweeps_at_most_eight_and_says_how_many_are_left()
    {
        await using StudioComponentContext context = new();
        string[] identities = [.. Enumerable.Range(0, 12).Select(static x => "db-" + x.ToString("00", CultureInfo.InvariantCulture))];
        context.StoreInfo.WithDatabases("default", "Default", identities);
        foreach (string identity in identities)
        {
            context.Health.CheckResults[identity] = Glance();
        }

        IRenderedComponent<Stores> page = context.Render<Stores>();
        page.WaitForAssertion(() => page.FindAll(".ms-stores-check").Should().HaveCount(12));

        page.Find(".ms-stores-check-all").Click();

        page.WaitForAssertion(() => page.Find(".ms-stores-check-message").TextContent.Trim()
            .Should().Be("Checked 8 of 12; press again for the next 4."));
        context.Health.Checks.Select(static x => x.DatabaseId).Should().Equal(identities.Take(8), "sequential, in page order");

        page.Find(".ms-stores-check-all").Click();

        page.WaitForAssertion(() => page.Find(".ms-stores-check-message").TextContent.Trim().Should().Be("Checked 4 of 4."));
        context.Health.Checks.Select(static x => x.DatabaseId).Should().Equal(identities, "the second press took the remaining four");
        page.FindAll(".ms-stores-check").Should().BeEmpty();
    }

    [Fact]
    public async Task Check_all_leaves_a_row_with_a_recent_sweep_alone()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "fresh", "stale");
        context.Health.GlanceResults["fresh"] = Glance();
        context.Health.CheckResults["stale"] = Glance();

        IRenderedComponent<Stores> page = context.Render<Stores>();
        page.WaitForAssertion(() => page.FindAll(".ms-stores-check").Should().ContainSingle());

        page.Find(".ms-stores-check-all").Click();

        page.WaitForAssertion(() => context.Health.Checks.Should().ContainSingle().Which.DatabaseId.Should().Be("stale"));
    }

    [Fact]
    public async Task A_check_that_leaves_a_row_without_a_sweep_is_not_tried_again_by_Check_all()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithDatabases("default", "Default", "refused");

        IRenderedComponent<Stores> page = context.Render<Stores>();
        page.WaitForAssertion(() => page.FindAll(".ms-stores-check").Should().ContainSingle());

        page.Find(".ms-stores-check-all").Click();
        page.WaitForAssertion(() => page.Find(".ms-stores-check-message").TextContent.Trim().Should().Be("Checked 1 of 1."));

        page.Find(".ms-stores-check-all").Click();

        page.WaitForAssertion(() => page.Find(".ms-stores-check-message").TextContent.Trim().Should().Be("Every database listed has a recent sweep."));
        context.Health.Checks.Should().ContainSingle("the refused database was tried once");
    }

    // -----------------------------------------------------------------------------------------------
    // Chip text, as a pure function
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0, 0, 0, "1 critical")]
    [InlineData(0, 1, 0, 0, "1 warning")]
    [InlineData(0, 3, 0, 0, "3 warnings")]
    [InlineData(0, 0, 1, 0, "1 not shown")]
    [InlineData(0, 0, 0, 2, "2 suggestions")]
    [InlineData(0, 0, 0, 0, "nothing to report")]
    public void The_chip_text(int critical, int warning, int withheld, int suggestions, string text) =>
        Stores.Chip(Glance(critical, warning, withheld, suggestions)).Text.Should().Be(text);

    [Fact]
    public void No_glance_is_not_checked_recently() => Stores.Chip(null).Text.Should().Be("not checked recently");
}
