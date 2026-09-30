using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The database browser's rail: pins remembered per store in this browser, the filter, the narrow-screen
/// summary, and - since the UX-6 pass - which page draws the tables and views bands, what each schema's
/// number counts, and the open object scrolled into view.
/// </summary>
public class DatabaseRailTests
{
    [Fact]
    public void Pins_come_from_this_browsers_storage_for_this_store_and_only_visible_schemas_are_offered()
    {
        using var context = DatabasePageData.Context();
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", "ms_db_pins_default")
            .SetResult(DatabaseRail.Format([("quartz", "qrtz_triggers"), ("secret", "payroll"), ("legacy", "bad\"name")]));

        var page = DatabasePageData.RenderBrowser(context);

        page.WaitForAssertion(() => page.Find(".ms-db-rail-pinned .ms-rail-count").TextContent.Trim().Should().Be("2"));

        List<IElement> pins = [.. page.FindAll(".ms-db-rail-pinned a.ms-rail-link")];
        pins.Select(x => x.TextContent.Trim()).Should().Equal("quartz.qrtz_triggers", "legacy.bad\"name");
        pins[0].GetAttribute("href").Should().StartWith("database/object?schema=quartz&name=qrtz_triggers").And.Contain("store=default");

        page.Find(".ms-db-rail-pinned").TextContent.Should().NotContain("secret", "a pin in a schema the visitor cannot see is not offered as a dead link");
    }

    [Fact]
    public async Task Pinning_a_table_remembers_it_for_this_store_and_puts_it_in_the_pinned_band()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        IElement pin = PinButton(page, "qrtz_job_details");
        pin.GetAttribute("aria-pressed").Should().Be("false");

        await pin.ClickAsync(new MouseEventArgs());

        var saved = context.JSInterop.Invocations["martenStudio.prefs.set"].Single();
        saved.Arguments[0].Should().Be("ms_db_pins_default");
        saved.Arguments[1].Should().Be("quartz/qrtz_job_details");

        PinButton(page, "qrtz_job_details").GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll(".ms-db-rail-pinned a.ms-rail-link").Select(x => x.TextContent.Trim()).Should().Equal("quartz.qrtz_job_details");

        await page.Find(".ms-db-rail-pinned button.ms-rail-action").ClickAsync(new MouseEventArgs());

        context.JSInterop.Invocations["martenStudio.prefs.set"][^1].Arguments[1].Should().Be(string.Empty);
        page.FindAll(".ms-db-rail-pinned a.ms-rail-link").Should().BeEmpty();
    }

    [Theory]
    [InlineData("quartz", "qrtz_triggers")]
    [InlineData("my schema", "a,b/c")]
    [InlineData("tëst", "表 \"quoted\"")]
    public void A_pin_round_trips_whatever_the_name_holds(string schema, string name)
    {
        string stored = DatabaseRail.Format([(schema, name), ("legacy", "orders")]);

        DatabaseRail.Parse(stored).Should().Equal((schema, name), ("legacy", "orders"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-slash,/leading,trailing/,a/b,a/b")]
    public void Anything_that_is_not_a_pin_is_ignored_rather_than_trusted(string? stored)
    {
        List<(string Schema, string Name)> pins = [.. DatabaseRail.Parse(stored)];

        pins.Should().BeEquivalentTo(stored is { Length: > 0 } ? [("a", "b")] : Array.Empty<(string, string)>());
    }

    [Fact]
    public async Task The_filter_narrows_the_rail_to_what_matches()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        await page.Find(".ms-db-rail-filter input").ChangeAsync(new ChangeEventArgs { Value = "simprop" });

        page.FindAll(".ms-db-rail-tables .ms-db-rail-name").Select(x => x.TextContent.Trim()).Should().Equal("qrtz_simprop_triggers");
        page.FindAll(".ms-db-rail-schemas .ms-db-rail-name").Should().BeEmpty("no schema is called anything like it");
        context.CurrentUri.Should().NotContain("q=", "the rail's filter is the rail's, not the grid's");
    }

    [Fact]
    public void Every_schema_s_count_is_for_the_kind_on_screen_with_the_rest_in_its_title()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions");

        IElement legacy = page.FindAll(".ms-db-rail-schemas a.ms-rail-link")
            .Single(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim() == "legacy");

        legacy.QuerySelector(".ms-rail-badge-count")!.TextContent.Trim().Should().Be("5");
        legacy.QuerySelector(".ms-rail-badge-count")!.GetAttribute("title")
            .Should().Be("6 tables · 3 views · 5 functions · 2 triggers · 1 sequence · 4 types");
    }

    [Fact]
    public void With_no_schema_picked_the_summary_counts_the_kind_on_screen_across_every_schema()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        int tables = FakeDatabaseObjects.Overview().Schemas.Sum(static x => x.Tables);

        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().Be($"All schemas · {tables} tables",
            "the summary says what the phone's closed rail would list, in the unit the page is counting");
        page.Find(".ms-db-rail-schemas a.ms-rail-link-active").TextContent.Should().Contain("All schemas");
    }

    /// <summary>
    /// UX-6, U1a: the browser page's grid is the schema's list of tables and views, and the rail's bands
    /// repeated it beside itself and were cut off at the bottom of the screen. They are object detail's.
    /// </summary>
    [Fact]
    public void The_browser_page_rail_is_the_filter_the_schemas_and_the_pins_and_nothing_else()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=quartz");

        page.FindAll(".ms-db-rail-filter").Should().ContainSingle();
        page.FindAll(".ms-db-rail-schemas").Should().ContainSingle();
        page.FindAll(".ms-db-rail-pinned").Should().ContainSingle();
        page.FindAll(".ms-db-rail-tables, .ms-db-rail-views, .ms-db-rail-hint").Should().BeEmpty();

        context.DatabaseObjects.Queries.Should().OnlyContain(static x => x.Category == DatabaseObjectCategory.Tables,
            "the browser no longer reads a list for bands it does not draw");
        page.Find(".ms-db-rail-pinned .ms-rail-empty").TextContent.Should().Contain("Open a table or view",
            "the stars are on object detail's bands, so the empty pin band says where to find them");
    }

    [Fact]
    public void Object_detail_keeps_the_bands_for_one_click_hops_between_tables()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.FindAll(".ms-db-rail-tables").Should().ContainSingle();
        page.FindAll(".ms-db-rail-views").Should().ContainSingle();
    }

    [Fact]
    public void Marten_s_own_tables_carry_an_M_in_the_rail_too()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "studio_sample", "host_settings");

        List<IElement> entries = [.. page.FindAll(".ms-db-rail-tables .ms-rail-entry")];

        entries.Where(x => x.QuerySelector(".ms-db-flag-marten") is not null)
            .Select(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim())
            .Should().BeEquivalentTo("mt_doc_customer", "flat_orders");
    }

    // ------------------------------------------------------------------------------------------------
    // U1b: the open object, scrolled into view inside the rail
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Object_detail_scrolls_the_open_object_into_view_inside_the_rail_once()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Should().ContainSingle());

        var call = context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Single();
        call.Arguments[0].Should().BeOfType<ElementReference>("the rail's own element, not the page, is what is scrolled");
        call.Arguments[1].Should().Be(DatabaseRail.ActiveEntrySelector);
        page.Find(DatabaseRail.ActiveEntrySelector).TextContent.Should().Contain("qrtz_triggers",
            "the selector finds the open object's band entry, not the pinned band's or the schema's");

        page.Render();

        context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Should().ContainSingle(
            "a re-render of the same object is not a reason to move the rail under the visitor's pointer");
    }

    [Fact]
    public void The_browser_page_and_a_missing_object_scroll_nothing()
    {
        using var context = DatabasePageData.Context();

        DatabasePageData.RenderBrowser(context, "schema=quartz");
        DatabasePageData.RenderObject(context, "quartz", "no_such_table");

        context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Should().BeEmpty();
    }

    [Fact]
    public void A_rail_whose_browser_went_away_while_scrolling_is_still_drawn()
    {
        using var context = DatabasePageData.Context();
        context.JSInterop.Setup<bool>("martenStudio.scroll.revealWithin", _ => true)
            .SetException(new Microsoft.JSInterop.JSDisconnectedException("the tab closed"));

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.WaitForAssertion(() => context.JSInterop.Invocations["martenStudio.scroll.revealWithin"].Should().ContainSingle());
        page.Find(".ms-db-rail-tables .ms-rail-link-active").TextContent.Should().Contain("qrtz_triggers");
    }

    // ------------------------------------------------------------------------------------------------
    // U1c: what the numbers count, said
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "tables")]
    [InlineData("kind=functions", "functions")]
    [InlineData("kind=views&schema=legacy", "views")]
    public void The_schemas_heading_says_what_the_browser_s_numbers_count(string query, string counted)
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, query);

        page.Find(".ms-db-rail-schemas .ms-rail-title").TextContent.Should().Contain("Schemas").And.Contain("· " + counted);
        page.Find(".ms-db-rail-counting").TextContent.Trim().Should().Be("· " + counted);
    }

    [Fact]
    public void Object_detail_counts_tables_and_views_whatever_kind_of_relation_is_open()
    {
        using var context = DatabasePageData.Context();

        DatabaseSchemaSummary legacy = FakeDatabaseObjects.Overview().Schemas.Single(static x => x.Name == FakeDatabaseObjects.Legacy);
        legacy.Views.Should().BeGreaterThan(0, "the premise: the schema has views, so a view's page is one");

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "order_totals");

        page.Find(".ms-db-rail-counting").TextContent.Trim().Should().Be("· tables and views");

        IElement entry = page.FindAll(".ms-db-rail-schemas a.ms-rail-link")
            .Single(static x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim() == FakeDatabaseObjects.Legacy);
        entry.QuerySelector(".ms-rail-badge-count")!.TextContent.Trim().Should().Be((legacy.Tables + legacy.Views).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "on a view's page the count is not the views alone - it counts what the bands below list");

        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().Be($"legacy · {legacy.Tables} tables, {legacy.Views} views");
    }

    // ------------------------------------------------------------------------------------------------
    // U1d: the phone rail starts closed on every page
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("schema=quartz")]
    [InlineData("kind=functions")]
    public void The_browser_page_s_rail_starts_closed_on_a_phone(string query)
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, query);

        page.Find("details.ms-db-rail-collapse").HasAttribute("open").Should().BeFalse(
            "a phone opens on the page, not on a screenful of schema cards; above 900 px the rail shows regardless");
    }

    /// <summary>
    /// A link followed from the open phone rail lands on a page with the rail shut - closed through the
    /// browser rather than by drawing a new element, which took the keyboard focus away with the link that
    /// had it.
    /// </summary>
    [Fact]
    public void Following_a_link_to_another_schema_closes_the_rail_through_the_browser()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);
        int before = context.JSInterop.Invocations["martenStudio.disclosure.close"].Count;

        context.Navigate("marten/database?schema=quartz");
        page.WaitForAssertion(() => page.Find(".ms-db-rail-schemas a.ms-rail-link-active").TextContent.Should().Contain("quartz"));

        var closes = context.JSInterop.Invocations["martenStudio.disclosure.close"];
        closes.Count.Should().BeGreaterThan(before);
        closes[^1].Arguments[0].Should().BeOfType<ElementReference>();

        int settled = closes.Count;
        page.Render();
        context.JSInterop.Invocations["martenStudio.disclosure.close"].Count.Should().Be(settled, "a render of the same page closes nothing");
    }

    private static IElement PinButton<T>(IRenderedComponent<T> page, string name)
        where T : IComponent =>
        page.FindAll(".ms-db-rail-tables .ms-rail-entry")
            .Single(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim() == name)
            .QuerySelector("button.ms-rail-action")!;
}
