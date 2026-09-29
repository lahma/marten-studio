using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Database;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The database browser's rail: pins remembered per store in this browser, the filter, and the
/// narrow-screen summary.
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

        var page = DatabasePageData.RenderBrowser(context, "schema=quartz");

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

        var page = DatabasePageData.RenderBrowser(context, "schema=quartz");

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
    public void With_no_schema_picked_the_rail_says_how_to_list_tables_and_its_summary_counts_schemas()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        page.Find(".ms-db-rail-hint").TextContent.Should().Contain("Pick a schema");
        page.FindAll(".ms-db-rail-tables").Should().BeEmpty();
        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().Be("All schemas · 4 schemas");
        page.Find(".ms-db-rail-schemas a.ms-rail-link-active").TextContent.Should().Contain("All schemas");
    }

    [Fact]
    public void Marten_s_own_tables_carry_an_M_in_the_rail_too()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=studio_sample");

        List<IElement> entries = [.. page.FindAll(".ms-db-rail-tables .ms-rail-entry")];

        entries.Where(x => x.QuerySelector(".ms-db-flag-marten") is not null)
            .Select(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim())
            .Should().BeEquivalentTo("mt_doc_customer", "flat_orders");
    }

    private static IElement PinButton<T>(IRenderedComponent<T> page, string name)
        where T : IComponent =>
        page.FindAll(".ms-db-rail-tables .ms-rail-entry")
            .Single(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim() == name)
            .QuerySelector("button.ms-rail-action")!;
}
