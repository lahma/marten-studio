using AngleSharp.Dom;

using Bunit;

using Marten;

using MartenStudio.Components;
using MartenStudio.Components.Pages.Database;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using BrowserPage = MartenStudio.Components.Pages.Database.DatabaseObjects;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The database browser over <see cref="FakeDatabaseObjects" />' Quartz and legacy samples: the rail, the
/// kind tabs, the owner filter, the five grids and the gate's four closed states.
/// </summary>
public class DatabaseObjectsPageTests
{
    [Fact]
    public void The_studios_router_reaches_the_page()
    {
        StudioRouteTable.Match("database")!.PageType.Should().Be<BrowserPage>();
    }

    // ------------------------------------------------------------------------------------------------
    // The rail
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_rail_lists_the_schemas_with_the_stores_own_flagged_and_the_others_counted_never_named()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        IElement schemas = page.Find(".ms-db-rail-schemas");
        schemas.QuerySelectorAll(".ms-db-rail-name").Select(x => x.TextContent.Trim())
            .Should().Equal("All schemas", FakeDatabaseObjects.StoreSchema, FakeDatabaseObjects.EventSchema, FakeDatabaseObjects.Legacy, FakeDatabaseObjects.Quartz);

        List<string> flagged = [.. schemas.QuerySelectorAll(".ms-rail-item")
            .Where(x => x.QuerySelector(".ms-db-flag-marten") is not null)
            .Select(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim())];

        flagged.Should().Equal(FakeDatabaseObjects.StoreSchema, FakeDatabaseObjects.EventSchema);

        IElement withheld = page.Find(".ms-db-rail-withheld");
        withheld.TextContent.Should().Contain("1 other schema").And.Contain("not shown").And.Contain("BrowsableSchemas");
    }

    [Fact]
    public void The_rail_links_each_schema_keeping_the_kind_and_the_scope()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions");

        IElement quartz = page.FindAll(".ms-db-rail-schemas a.ms-rail-link")
            .Single(x => x.QuerySelector(".ms-db-rail-name")!.TextContent.Trim() == FakeDatabaseObjects.Quartz);

        quartz.GetAttribute("href").Should().StartWith("database?schema=quartz&kind=functions").And.Contain("store=default");
    }

    /// <summary>
    /// UX-6: picking a schema lists its tables and views in the grid, which is the page; the rail marks the
    /// schema and says so in its phone summary, and leaves the list to the grid rather than drawing it twice.
    /// The bands are object detail's (see <see cref="DatabaseRailTests" />).
    /// </summary>
    [Fact]
    public void Picking_a_schema_lists_its_tables_in_the_grid_and_marks_it_in_the_rail()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=legacy");

        page.FindAll(".ms-db-relations tbody .ms-db-name").Select(x => x.TextContent.Trim())
            .Should().Contain(["orders", "measurements", "payroll", "keyless_log"]);
        page.FindAll(".ms-db-rail-tables, .ms-db-rail-views").Should().BeEmpty();

        page.Find(".ms-db-rail-schemas a.ms-rail-link-active").TextContent.Should().Contain("legacy");
        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().Be("legacy · 6 tables");
        page.Find("details.ms-db-rail-collapse").HasAttribute("open").Should().BeFalse("a phone opens on the grid, not on the rail");
    }

    // ------------------------------------------------------------------------------------------------
    // Kind tabs and the owner filter
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_kind_tabs_carry_their_counts_and_link_to_their_kind()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        List<IElement> tabs = [.. page.FindAll(".ms-db-kind-tabs a.ms-tab")];

        tabs.Select(x => x.ChildNodes.OfType<IText>().First().TextContent.Trim())
            .Should().Equal("Tables", "Views", "Functions", "Triggers", "Sequences", "Types");
        tabs.Select(x => x.QuerySelector(".ms-tab-count")!.TextContent.Trim())
            .Should().Equal("13", "3", "6", "2", "2", "4");

        tabs[0].GetAttribute("aria-current").Should().Be("page");
        tabs[1].GetAttribute("href").Should().StartWith("database?kind=views").And.Contain("store=default");
    }

    [Fact]
    public void Switching_the_kind_asks_the_service_for_that_kind()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);
        string views = page.FindAll(".ms-db-kind-tabs a.ms-tab")[1].GetAttribute("href")!;

        context.Navigate("marten/" + views);

        page.WaitForAssertion(() => context.DatabaseObjects.Queries[^1].Category.Should().Be(DatabaseObjectCategory.Views));
        page.WaitForAssertion(() => page.Find(".ms-db-kind-tabs a[aria-current=page]").TextContent.Should().Contain("Views"));
        page.FindAll(".ms-db-relations tbody .ms-db-name").Select(x => x.TextContent.Trim())
            .Should().Contain("legacy.order_totals");
    }

    [Fact]
    public void An_empty_kind_is_still_a_tab_but_a_quiet_one()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Overview = FakeDatabaseObjects.Overview(DatabaseRefusal.CapabilityOff);

        var page = DatabasePageData.RenderBrowser(context);

        IElement views = page.FindAll(".ms-db-kind-tabs a.ms-tab").Single(x => x.TextContent.Contains("Views", StringComparison.Ordinal));
        views.ClassList.Should().Contain("ms-db-tab-empty");
        views.QuerySelector(".ms-tab-count")!.TextContent.Trim().Should().Be("0");
        views.HasAttribute("href").Should().BeTrue("none is an answer worth one click");
    }

    [Fact]
    public async Task The_owner_filter_counts_each_side_and_filters_through_the_url()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        List<IElement> options = [.. page.FindAll(".ms-db-owner-filter .ms-segmented-option")];
        options.Select(x => System.Text.RegularExpressions.Regex.Replace(x.TextContent, @"\s+", string.Empty))
            .Should().Equal("All13", "Marten3", "Other10");
        options.Select(x => x.GetAttribute("aria-pressed")).Should().Equal("true", "false", "false");

        await options[2].ClickAsync(new MouseEventArgs());

        context.CurrentUri.Should().Contain("owner=other");
        page.WaitForAssertion(() => context.DatabaseObjects.Queries[^1].Owner.Should().Be(DatabaseOwnerFilter.Other));
        page.WaitForAssertion(() => page.FindAll(".ms-db-owner-filter .ms-segmented-option")[2].GetAttribute("aria-pressed").Should().Be("true"));
        page.FindAll(".ms-db-relations tbody tr.ms-db-row-marten").Should().BeEmpty();
        page.FindAll(".ms-db-relations tbody .ms-db-name").Select(x => x.TextContent.Trim()).Should().NotContain("mt_doc_customer");
    }

    [Fact]
    public void The_name_filter_shows_what_the_url_says_and_asks_for_it()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "q=qrtz");

        page.Find(".ms-db-toolbar .ms-db-name-filter").GetAttribute("value").Should().Be("qrtz");
        context.DatabaseObjects.Queries[^1].NameFilter.Should().Be("qrtz");
        page.FindAll(".ms-db-relations tbody .ms-db-name").Should().HaveCount(3);
    }

    [Fact]
    public async Task Typing_a_name_filter_puts_it_in_the_url()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        await page.Find(".ms-db-toolbar .ms-db-name-filter").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "orders" });

        context.CurrentUri.Should().Contain("q=orders");
        page.WaitForAssertion(() => context.DatabaseObjects.Queries[^1].NameFilter.Should().Be("orders"));
    }

    // ------------------------------------------------------------------------------------------------
    // The tables grid
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_tables_grid_has_every_column()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        page.FindAll(".ms-db-relations thead th").Select(x => x.TextContent.Trim())
            .Should().Equal("Name", "Owner", "~Rows", "Size", "Key", "Foreign keys", "Comment");
    }

    [Fact]
    public void What_marten_does_not_own_comes_first_and_marten_s_own_is_drawn_quieter()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        List<IElement> rows = [.. page.FindAll(".ms-db-relations tbody tr")];
        int firstMarten = rows.FindIndex(x => x.ClassList.Contains("ms-db-row-marten"));

        firstMarten.Should().Be(10, "the ten objects Marten does not own sort first");
        rows.Skip(firstMarten).Should().OnlyContain(x => x.ClassList.Contains("ms-db-row-marten"));
        rows.Skip(firstMarten).Select(x => x.QuerySelector(".ms-db-name")!.TextContent.Trim())
            .Should().Equal("studio_sample.flat_orders", "studio_sample.mt_doc_customer", "studio_sample_events.mt_events");
    }

    [Fact]
    public async Task A_header_sorts_the_grid_by_its_column()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=quartz");

        IElement header = page.FindAll(".ms-db-relations thead th").Single(x => x.TextContent.Contains("~Rows", StringComparison.Ordinal));
        await header.QuerySelector("button")!.ClickAsync(new MouseEventArgs());

        page.FindAll(".ms-db-relations tbody .ms-db-name").Select(x => x.TextContent.Trim())
            .Should().Equal(["qrtz_triggers", "qrtz_job_details", "qrtz_simprop_triggers"], "the first click on a number sorts biggest first");
        page.FindAll(".ms-db-relations thead th")
            .Single(x => x.TextContent.Contains("~Rows", StringComparison.Ordinal))
            .GetAttribute("aria-sort").Should().Be("descending");
    }

    [Fact]
    public void Quartz_tables_wear_a_neutral_hint_that_says_it_is_a_guess()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=quartz");

        List<IElement> hints = [.. page.FindAll(".ms-db-relations .ms-db-hint")];
        hints.Should().HaveCount(3);
        hints.Should().OnlyContain(x => x.TextContent.Trim() == "Quartz.NET" && x.GetAttribute("title") == "Recognised by name");
    }

    [Fact]
    public void A_marten_document_table_s_owner_badge_links_to_its_documents_collection()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        IElement row = Row(page, "mt_doc_customer");
        IElement badge = row.QuerySelector("a.ms-db-owner-document")!;

        badge.GetAttribute("href").Should().StartWith("documents/customer?").And.Contain("store=default");
        badge.QuerySelector(".ms-collection-alias")!.TextContent.Trim().Should().Be("customer");
    }

    [Fact]
    public void An_event_table_s_owner_badge_links_to_streams()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        Row(page, "mt_events").QuerySelector("a.ms-db-owner-events")!.GetAttribute("href")
            .Should().StartWith("events/streams").And.Contain("store=default");
    }

    [Fact]
    public void The_badges_say_what_each_relation_is()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        Badges(Row(page, "measurements")).Should().Contain("partitioned · 2");
        Badges(Row(page, "remote_orders")).Should().Contain("foreign · archive");
        Badges(Row(page, "payroll")).Should().Contain("no SELECT");
        Badges(Row(page, "bad\"name")).Should().Contain("cannot browse");

        Row(page, "remote_orders").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("—", "a foreign table has no local rows to estimate");
        Row(page, "keyless_log").QuerySelector(".ms-db-cell-key")!.TextContent.Trim().Should().Be("none");
        Row(page, "orders").QuerySelector(".ms-db-cell-key")!.TextContent.Trim().Should().Be("primary key");
        Row(page, "qrtz_triggers").QuerySelector(".ms-db-cell-fk")!.TextContent.Should().Contain("1 out").And.Contain("1 in");
        Row(page, "qrtz_triggers").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("~1,204", "an estimate always wears its tilde (D8)");
    }

    /// <summary>
    /// UX-7: a relation with no badge renders no badge wrapper. The empty inline-flex span still took its
    /// margin and a line box, and beside a name that filled its cell - <c>mt_doc_deadletterevent</c> - it
    /// wrapped onto a line of its own and made that one row taller than the rest, with nothing on the line.
    /// </summary>
    [Fact]
    public void A_relation_with_no_badge_has_no_empty_badge_wrapper()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        Row(page, "orders").QuerySelectorAll(".ms-db-badges").Should().BeEmpty("a plain table has nothing to say beside its name");
        Row(page, "measurements").QuerySelectorAll(".ms-db-badges").Should().ContainSingle("the anti-vacuity half: a badge still has its wrapper");
    }

    [Fact]
    public void Unlogged_row_level_security_an_unanalysed_estimate_and_a_long_comment_are_all_said()
    {
        using var context = DatabasePageData.Context();
        string comment = new('x', 400);
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Tables,
            [DatabasePageData.Table("legacy", "events_raw", estimatedRows: null, comment: comment, unlogged: true, rowSecurity: true)]);

        var page = DatabasePageData.RenderBrowser(context);

        IElement row = Row(page, "events_raw");
        Badges(row).Should().Contain(["unlogged", "RLS"]);
        row.QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("not analyzed");
        row.QuerySelector(".ms-db-comment")!.GetAttribute("title").Should().Be(comment, "the whole comment is one hover away");
    }

    [Fact]
    public void A_materialized_view_nobody_refreshed_says_so()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=views");

        Badges(Row(page, "order_stats")).Should().Contain("never refreshed");
        Badges(Row(page, "secret_view")).Should().Contain("may read a hidden table");
        page.FindAll(".ms-db-relations thead th").Select(x => x.TextContent.Trim())
            .Should().NotContain("Key", "a view has no key to show");
    }

    [Fact]
    public void A_truncated_list_says_to_refine_the_filter()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Tables, [DatabasePageData.Table("legacy", "t1")], truncated: true);

        var page = DatabasePageData.RenderBrowser(context);

        page.Find(".ms-db-truncated").TextContent.Should().Contain("Showing the first 1").And.Contain("refine the filter");
    }

    [Theory]
    [InlineData("tables")]
    [InlineData("views")]
    [InlineData("functions")]
    [InlineData("triggers")]
    [InlineData("sequences")]
    [InlineData("types")]
    public void Every_grid_sits_in_a_labelled_scroll_region(string kind)
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=" + kind);

        page.ShouldPutEveryTableInALabelledScrollRegion();
    }

    // ------------------------------------------------------------------------------------------------
    // Functions, triggers, sequences, types
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Functions_show_their_overloads_and_warn_about_an_unpinned_security_definer()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");

        page.FindAll(".ms-db-functions thead th").Select(x => x.TextContent.Trim())
            .Should().Equal("Definition", "Name", "Kind", "Returns", "Language", "Volatility", "Security", "Owner");
        page.FindAll(".ms-db-functions tbody .ms-db-signature").Select(x => x.TextContent.Trim())
            .Should().Contain(["recalculate(order_id bigint)", "recalculate(order_id bigint, force boolean)"]);

        IElement definer = FunctionRow(page, "unsafe_definer");
        IElement badge = definer.QuerySelector(".ms-db-badge-warning")!;
        badge.TextContent.Trim().Should().Be("SECURITY DEFINER");
        badge.GetAttribute("title").Should().Contain("search_path");

        FunctionRow(page, "archive_orders").TextContent.Should().Contain("proc");
        FunctionRow(page, "sum_amounts").TextContent.Should().Contain("agg");
    }

    [Fact]
    public async Task Expanding_a_function_reads_its_definition_once()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty("a body is read only when somebody opens it");

        await Expander(page, "archive_orders").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-db-definition-row .ms-sql").TextContent.Should().Contain("CREATE OR REPLACE FUNCTION"));
        page.Find(".ms-db-definition-row").TextContent.Should().Contain("Copy SQL");
        Expander(page, "archive_orders").GetAttribute("aria-expanded").Should().Be("true");

        await Expander(page, "archive_orders").ClickAsync(new MouseEventArgs());
        page.FindAll(".ms-db-definition-row").Should().BeEmpty();

        await Expander(page, "archive_orders").ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".ms-db-definition-row .ms-sql").Should().ContainSingle());

        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle()
            .Which.Should().Be(new DatabaseObjectRef(DatabaseObjectKind.Procedure, "legacy", "archive_orders", "before date"));
    }

    /// <summary>
    /// DB-4 review F1: a function in the store's own schema that Marten does not own, with the gate shut. The
    /// list says its body is not available, so opening its row draws the gate's sentence and asks nothing -
    /// the ask would be a refused <c>BrowseDatabase</c> read, audited and logged as a 9202 security Warning,
    /// for a click the page itself offered.
    /// </summary>
    [Fact]
    public async Task A_definition_the_list_withholds_is_never_asked_for_and_its_row_says_why()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Functions,
            [WithheldRoutine(FakeDatabaseObjects.StoreSchema, "host_fn")],
            refusal: DatabaseRefusal.CapabilityOff);

        var page = DatabasePageData.RenderBrowser(context, "kind=functions");

        IElement expander = Expander(page, "host_fn");
        expander.GetAttribute("data-definition").Should().Be("withheld");
        expander.GetAttribute("aria-label").Should().Be("Show why there is no definition of studio_sample.host_fn()");

        await expander.ClickAsync(new MouseEventArgs());

        page.Find(".ms-db-definition-row .ms-db-refusal").TextContent.Trim().Should().Be(DatabaseGate.CapabilityDenial);
        page.FindAll(".ms-db-definition-row .ms-sql").Should().BeEmpty();
        Expander(page, "host_fn").GetAttribute("aria-expanded").Should().Be("true");

        await Expander(page, "host_fn").ClickAsync(new MouseEventArgs());
        await Expander(page, "host_fn").ClickAsync(new MouseEventArgs());

        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty("a definition the list withholds is never asked for, however often it is opened");
    }

    [Fact]
    public async Task With_the_gate_open_a_schema_BrowsableSchemas_does_not_list_is_named_in_the_withheld_rows_sentence()
    {
        using var context = DatabasePageData.Context();
        context.Options.BrowsableSchemas.Add(FakeDatabaseObjects.Legacy);
        context.Options.BrowsableSchemas.Add(FakeDatabaseObjects.Quartz);
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Functions,
            [WithheldRoutine(FakeDatabaseObjects.StoreSchema, "host_fn")]);

        var page = DatabasePageData.RenderBrowser(context, "kind=functions");

        await Expander(page, "host_fn").ClickAsync(new MouseEventArgs());

        page.Find(".ms-db-definition-row .ms-db-refusal").TextContent.Trim()
            .Should().Be(DatabaseGate.SchemaDenial(FakeDatabaseObjects.StoreSchema, [FakeDatabaseObjects.Legacy, FakeDatabaseObjects.Quartz]));
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty();
    }

    [Fact]
    public async Task An_aggregate_says_it_has_no_body_without_asking()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");

        await Expander(page, "sum_amounts").ClickAsync(new MouseEventArgs());

        page.Find(".ms-db-definition-row .ms-db-refusal").TextContent.Trim().Should().Be(DatabaseObjectService.AggregateHasNoBody);
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty("the list already says an aggregate has none");
    }

    [Fact]
    public async Task A_trigger_whose_definition_is_withheld_is_never_asked_for()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Triggers,
            [WithheldTrigger(FakeDatabaseObjects.Legacy, "orders", "orders_hr_sync")]);

        var page = DatabasePageData.RenderBrowser(context, "kind=triggers");

        IElement expander = page.FindAll(".ms-db-triggers tbody tr").First(x => x.TextContent.Contains("orders_hr_sync", StringComparison.Ordinal))
            .QuerySelector(".ms-db-expander")!;
        expander.GetAttribute("data-definition").Should().Be("withheld");

        await expander.ClickAsync(new MouseEventArgs());

        page.Find(".ms-db-triggers .ms-db-definition-row .ms-db-refusal").TextContent.Trim()
            .Should().Be(DatabaseDefinitionWithheld.TriggerFunctionWithheld);
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty();
    }

    /// <summary>
    /// The regression the review asked for, through the real <c>DatabaseObjectService</c> and a real
    /// <c>StudioActionLog</c>: opening withheld definitions on the page writes no Warning and no ring entry -
    /// and the same definition asked for directly does both, so the capture is proved to be listening.
    /// </summary>
    [Theory]
    [InlineData("functions")]
    [InlineData("triggers")]
    public async Task Opening_a_withheld_definition_logs_no_security_warning_and_audits_nothing(string kind)
    {
        await using RealDefinitionService real = RealDefinitionService.Create();

        FakeDatabaseObjectService fake = new()
        {
            List = kind == "functions"
                ? DatabasePageData.ListOf(DatabaseObjectCategory.Functions, [WithheldRoutine(FakeDatabaseObjects.StoreSchema, "host_fn")], refusal: DatabaseRefusal.CapabilityOff)
                : DatabasePageData.ListOf(DatabaseObjectCategory.Triggers, [WithheldTrigger(FakeDatabaseObjects.StoreSchema, "host_settings", "host_settings_audit")], refusal: DatabaseRefusal.CapabilityOff),
        };

        using var context = new StudioComponentContext(services => services.AddSingleton<IDatabaseObjectService>(real.Over(fake)));
        context.WithStores("default");

        var page = DatabasePageData.RenderBrowser(context, "kind=" + kind);

        foreach (IElement expander in page.FindAll(".ms-db-expander[data-definition=withheld]"))
        {
            await expander.ClickAsync(new MouseEventArgs());
        }

        page.FindAll(".ms-db-definition-row .ms-db-refusal").Should().ContainSingle();
        real.DefinitionsAsked.Should().Be(0);
        real.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
        real.Ring.GetLatest().Should().BeEmpty();

        // Anti-vacuity: had the page asked, this is what it would have caused.
        DatabaseObjectSummary item = fake.List!.Items.Single();
        DatabaseObjectDefinition refused = await real.Service.GetDefinitionAsync(context.State.ActiveScope!, item.Ref, Xunit.TestContext.Current.CancellationToken);

        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        real.Logs.Entries.Should().Contain(static x => x.Level == LogLevel.Warning && x.EventId.Id == 9202);
        real.Ring.GetLatest().Should().ContainSingle(static x => !x.Succeeded);
    }

    /// <summary>
    /// A definition the list said was available can still be refused when it is read - the gate may have
    /// changed in between - and the refusal is then drawn in the service's own words.
    /// </summary>
    [Fact]
    public async Task A_refusal_the_service_gives_after_the_list_was_read_is_drawn_as_its_sentence()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Definition = DatabaseObjectDefinition.Unavailable(
            new DatabaseObjectRef(DatabaseObjectKind.Function, "legacy", "unsafe_definer", string.Empty),
            DatabaseRefusal.CapabilityOff,
            DatabaseGate.CapabilityDenial);

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");

        await Expander(page, "unsafe_definer").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-db-definition-row .ms-db-refusal").TextContent.Trim()
            .Should().Be(DatabaseGate.CapabilityDenial));
        page.FindAll(".ms-db-definition-row .ms-sql").Should().BeEmpty();
        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle("the list said this one was available");
    }

    [Fact]
    public async Task A_failed_definition_read_is_an_error_with_a_retry_not_a_dead_circuit()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");
        context.DatabaseObjects.Failure = new InvalidOperationException("the catalog went away");

        await Expander(page, "archive_orders").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-db-definition-row .ms-error-alert").TextContent.Should().Contain("the catalog went away"));
    }

    [Fact]
    public void Triggers_say_whether_they_fire_and_link_their_table_and_function()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=triggers");

        page.FindAll(".ms-db-triggers tbody .ms-db-state").Select(x => x.TextContent.Trim())
            .Should().BeEquivalentTo("enabled", "disabled");

        IElement row = page.FindAll(".ms-db-triggers tbody tr").First(x => x.TextContent.Contains("orders_audit", StringComparison.Ordinal));
        List<string?> hrefs = [.. row.QuerySelectorAll("a.ms-db-name").Select(x => x.GetAttribute("href"))];

        hrefs[0].Should().StartWith("database/object?schema=legacy&name=orders");
        hrefs[1].Should().StartWith("database?schema=legacy&kind=functions&q=audit");
        row.TextContent.Should().Contain("AFTER").And.Contain("INSERT OR UPDATE").And.Contain("row");
    }

    [Fact]
    public void A_sequence_the_role_may_not_read_says_no_privilege_and_a_rolled_up_one_says_how_many()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Sequences,
            [
                DatabasePageData.Sequence("legacy", "payroll_id_seq", canReadValue: false),
                DatabasePageData.Sequence("legacy", "unused_seq", canReadValue: true),
            ]);

        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        SequenceRow(page, "payroll_id_seq").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("no privilege");

        // A readable sequence's value is read only when asked: reading it takes a brief lock on the sequence,
        // and a list must lock nothing it lists (the DB-1 review's B1).
        SequenceRow(page, "unused_seq").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("read");
        context.DatabaseObjects.SequenceValuesAsked.Should().BeEmpty("nothing is read until somebody asks");

        context.DatabaseObjects.SequenceValue = new DatabaseSequenceValue(
            new DatabaseObjectRef(DatabaseObjectKind.Sequence, "legacy", "unused_seq"), null, DatabaseRefusal.None, null);
        SequenceRow(page, "unused_seq").QuerySelector("button.ms-db-read-value")!.Click();

        SequenceRow(page, "unused_seq").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("never used");
        context.DatabaseObjects.SequenceValuesAsked.Should().Equal("legacy.unused_seq");
    }

    [Fact]
    public void The_sample_s_sequences_show_their_owner_and_the_per_tenant_roll_up()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        IElement owned = SequenceRow(page, "orders_id_seq");
        owned.QuerySelector("a.ms-db-name")!.TextContent.Trim().Should().Be("legacy.orders.id");
        owned.QuerySelector("a.ms-db-name")!.GetAttribute("href").Should().StartWith("database/object?schema=legacy&name=orders&tab=columns");
        owned.QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("read");

        context.DatabaseObjects.SequenceValue = new DatabaseSequenceValue(
            new DatabaseObjectRef(DatabaseObjectKind.Sequence, "legacy", "orders_id_seq"), 50_000, DatabaseRefusal.None, null);
        owned.QuerySelector("button.ms-db-read-value")!.Click();
        SequenceRow(page, "orders_id_seq").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("50,000");

        IElement events = SequenceRow(page, "mt_events_sequence");
        events.TextContent.Should().Contain("+2 per tenant");
        events.TextContent.Should().NotContain("mt_events_sequence_", "a per-tenant sequence's name is the tenant list");
    }

    [Fact]
    public void An_enum_s_labels_are_chips_in_their_declared_order()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=types");

        page.FindAll(".ms-db-enum-labels .ms-db-chip").Select(x => x.TextContent.Trim())
            .Should().Equal("pending", "shipped", "cancelled");

        IElement domain = page.FindAll(".ms-db-types tbody tr").First(x => x.TextContent.Contains("positive_amount", StringComparison.Ordinal));
        domain.TextContent.Should().Contain("numeric(12,2)").And.Contain("CHECK (VALUE > 0::numeric)").And.Contain("1 column");

        page.FindAll(".ms-db-types tbody tr").First(x => x.TextContent.Contains("address", StringComparison.Ordinal))
            .TextContent.Should().Contain("street").And.Contain("city");
    }

    // ------------------------------------------------------------------------------------------------
    // The gate
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(nameof(DatabaseRefusal.CapabilityOff), "MartenStudioOptions.Capabilities.BrowseDatabase")]
    [InlineData(nameof(DatabaseRefusal.ReadOnly), "MartenStudioOptions.ReadOnly")]
    [InlineData(nameof(DatabaseRefusal.WritePolicy), "MartenStudioOptions.WriteAuthorizationPolicy")]
    [InlineData(nameof(DatabaseRefusal.SchemaNotBrowsable), "MartenStudioOptions.BrowsableSchemas")]
    public void A_shut_gate_names_the_exact_option_that_would_open_it(string gate, string option)
    {
        DatabaseRefusal refusal = Enum.Parse<DatabaseRefusal>(gate);

        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Overview = FakeDatabaseObjects.Overview(refusal);
        context.DatabaseObjects.List = FakeDatabaseObjects.List(new DatabaseObjectQuery(DatabaseObjectCategory.Tables), refusal);

        var page = DatabasePageData.RenderBrowser(context);

        IElement notice = page.Find(".ms-db-access");
        notice.GetAttribute("data-refusal").Should().Be(refusal.ToString());
        notice.TextContent.Should().Contain(option);
    }

    [Fact]
    public void With_the_capability_off_the_store_s_own_schemas_still_render_and_the_notice_says_what_more_it_would_show()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Overview = FakeDatabaseObjects.Overview(DatabaseRefusal.CapabilityOff);
        context.DatabaseObjects.List = FakeDatabaseObjects.List(new DatabaseObjectQuery(DatabaseObjectCategory.Tables), DatabaseRefusal.CapabilityOff);

        var page = DatabasePageData.RenderBrowser(context);

        page.Find(".ms-db-rail-schemas").QuerySelectorAll(".ms-db-rail-name").Select(x => x.TextContent.Trim())
            .Should().Equal("All schemas", FakeDatabaseObjects.StoreSchema, FakeDatabaseObjects.EventSchema);
        page.FindAll(".ms-db-relations tbody .ms-db-name").Select(x => x.TextContent.Trim())
            .Should().Contain("studio_sample.host_settings").And.Contain("studio_sample.mt_doc_customer");

        string notice = page.Find(".ms-db-access").TextContent;
        notice.Should().Contain("structure of this store's own schemas");
        notice.Should().Contain("definitions of views, functions and triggers");
        notice.Should().Contain("this database has 3 more");
        notice.Should().NotContain(FakeDatabaseObjects.Quartz, "withheld schemas are a count, never names");

        page.Find(".ms-db-rail-withheld").TextContent.Should().Contain("3 other schemas").And.Contain("Capabilities.BrowseDatabase");
    }

    [Fact]
    public void An_open_gate_draws_no_notice()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context);

        page.FindAll(".ms-db-access").Should().BeEmpty();
    }

    [Fact]
    public void A_schema_the_gate_refuses_is_a_neutral_message_with_a_way_back()
    {
        using var context = DatabasePageData.Context();
        string sentence = DatabaseGate.SchemaDenial("secret", ["quartz"]);
        context.DatabaseObjects.List = DatabaseObjectList.Refused(
            new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "secret"), DatabaseRefusal.SchemaNotBrowsable, sentence);

        var page = DatabasePageData.RenderBrowser(context, "schema=secret");

        page.Find(".ms-db-refused").TextContent.Should().Contain(sentence);
        page.FindAll(".ms-db-relations").Should().BeEmpty();
    }

    [Fact]
    public void A_service_that_throws_is_an_error_with_a_retry()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Failure = new InvalidOperationException("the database refused the read");

        var page = DatabasePageData.RenderBrowser(context);

        page.Find(".ms-error-alert").TextContent.Should().Contain("the database refused the read");

        context.DatabaseObjects.Failure = null;
        page.Find(".ms-error-alert button").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-db-relations").Should().ContainSingle());
    }

    [Fact]
    public async Task A_scope_change_reads_again_for_the_new_scope()
    {
        using var context = DatabasePageData.Context();
        context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");

        var page = DatabasePageData.RenderBrowser(context);
        int reads = context.DatabaseObjects.Reads;

        await page.InvokeAsync(() => context.State.SetScopeAsync("second", null, null));

        page.WaitForAssertion(() => context.DatabaseObjects.LastScope!.StoreKey.Should().Be("second"));
        context.DatabaseObjects.Reads.Should().BeGreaterThan(reads);
    }

    [Fact]
    public void The_page_asks_with_the_scope_on_screen()
    {
        using var context = DatabasePageData.Context();

        DatabasePageData.RenderBrowser(context, "schema=quartz&store=default&db=localhost.marten");

        context.DatabaseObjects.LastScope!.StoreKey.Should().Be("default");
        context.DatabaseObjects.Queries.Should().Contain(x => x.Schema == "quartz" && x.Category == DatabaseObjectCategory.Tables);
    }

    /// <summary>
    /// DB-4 review F4: a <c>?schema=</c> is whatever somebody typed into a link. One the overview did not
    /// return is not repeated back in the title, the heading or the rail as if it were a schema to browse;
    /// the list says, in the service's words, that there is no such schema.
    /// </summary>
    [Fact]
    public void A_schema_the_overview_did_not_return_is_not_echoed_as_if_it_were_one()
    {
        const string Typed = "not_a_schema_of_yours";

        using var context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabaseObjectList.Refused(
            new DatabaseObjectQuery(DatabaseObjectCategory.Tables, Typed),
            DatabaseRefusal.NotFound,
            "There is no schema '" + Typed + "' among the schemas this studio shows you.",
            FakeDatabaseObjects.Access());

        var page = DatabasePageData.RenderBrowser(context, "schema=" + Typed);

        PageTitleText(context, page).Should().Be("Database");
        page.FindAll(".ms-db-title-schema").Should().BeEmpty();
        page.Find(".ms-page-header").TextContent.Should().NotContain(Typed);
        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().StartWith("All schemas");
        page.Find(".ms-db-rail").TextContent.Should().NotContain(Typed);
        page.FindAll(".ms-db-kind-tabs a").Select(static x => x.GetAttribute("title") ?? string.Empty)
            .Should().NotContain(x => x.Contains(Typed, StringComparison.Ordinal));

        page.Find(".ms-db-refused").TextContent.Should().Contain("There is no schema",
            "the service's own sentence is where the page says what happened to the link");
    }

    [Fact]
    public void A_schema_the_overview_did_return_is_the_title_the_heading_and_the_rail()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "schema=" + FakeDatabaseObjects.Quartz);

        PageTitleText(context, page).Should().Be("quartz — Database");
        page.Find(".ms-db-title-schema").TextContent.Trim().Should().Be("quartz");
        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().StartWith("quartz · ");
    }

    /// <summary>
    /// A sequence value is keyed by schema and name, which say nothing about the database it was read in:
    /// after a switch of database (one per tenant, say) a value read in the old one sat against the
    /// same-named sequence of the new one.
    /// </summary>
    [Fact]
    public async Task A_sequence_value_read_in_one_database_is_not_shown_against_the_same_name_in_another()
    {
        using var context = DatabasePageData.Context();
        context.Catalog.Databases["default"].Add(new DatabaseListing("tenant_b", "tenant_b", "localhost"));
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Sequences,
            [DatabasePageData.Sequence("legacy", "invoice_seq", canReadValue: true)]);
        context.DatabaseObjects.SequenceValue = new DatabaseSequenceValue(
            new DatabaseObjectRef(DatabaseObjectKind.Sequence, "legacy", "invoice_seq"), 4210, DatabaseRefusal.None, null);

        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        await SequenceRow(page, "invoice_seq").QuerySelector("button.ms-db-read-value")!.ClickAsync(new MouseEventArgs());
        SequenceRow(page, "invoice_seq").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("4,210");

        // The same list object comes back for the new database, as a fake's fixed answer does: the scope is
        // what changed, and it alone has to be enough to forget the value.
        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "tenant_b", null));

        page.WaitForAssertion(() => context.DatabaseObjects.LastScope!.DatabaseId.Should().Be("tenant_b"));
        page.WaitForAssertion(() => SequenceRow(page, "invoice_seq").QuerySelectorAll("td")[2].TextContent.Trim()
            .Should().Be("read", "the value on screen was read in the other database"));
    }

    [Fact]
    public async Task A_new_list_forgets_the_values_read_for_the_old_one()
    {
        using var context = DatabasePageData.Context();
        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        context.DatabaseObjects.SequenceValue = new DatabaseSequenceValue(
            new DatabaseObjectRef(DatabaseObjectKind.Sequence, "legacy", "invoice_seq"), 7, DatabaseRefusal.None, null);

        DatabaseObjectList first = DatabasePageData.ListOf(
            DatabaseObjectCategory.Sequences, [DatabasePageData.Sequence("legacy", "invoice_seq", canReadValue: true)]);

        var grid = context.Render<DatabaseSequencesGrid>(parameters => parameters.Add(x => x.Model, first));

        await grid.Find("button.ms-db-read-value").ClickAsync(new MouseEventArgs());
        grid.Find(".ms-db-sequences tbody tr").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("7");

        grid.Render(parameters => parameters.Add(x => x.Model, first));
        grid.Find(".ms-db-sequences tbody tr").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("7",
            "the same list, re-rendered, is the same question");

        DatabaseObjectList second = DatabasePageData.ListOf(
            DatabaseObjectCategory.Sequences, [DatabasePageData.Sequence("legacy", "invoice_seq", canReadValue: true)]);

        grid.Render(parameters => parameters.Add(x => x.Model, second));
        grid.Find(".ms-db-sequences tbody tr").QuerySelectorAll("td")[2].TextContent.Trim().Should().Be("read");
    }

    /// <summary>What the page's <c>PageTitle</c> says - its content renders into the head, not the page.</summary>
    private static string PageTitleText(StudioComponentContext context, IRenderedComponent<BrowserPage> page)
    {
        RenderFragment content = page.FindComponent<PageTitle>().Instance.ChildContent!;
        return context.Render(content).Markup.Trim();
    }

    private static IElement Row<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-db-relations tbody tr").First(x => Names(x.QuerySelector(".ms-db-name")!, name));

    private static IElement FunctionRow<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-db-functions tbody tr:not(.ms-db-definition-row)")
            .First(x => x.QuerySelector(".ms-db-signature")!.TextContent.Trim().StartsWith(name + "(", StringComparison.Ordinal)
                || x.QuerySelector(".ms-db-signature")!.TextContent.Contains("." + name + "(", StringComparison.Ordinal));

    private static IElement SequenceRow<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-db-sequences tbody tr").First(x => Names(x.QuerySelector(".ms-db-name")!, name));

    /// <summary>Whether a name cell names <paramref name="name" />, with or without its schema in front.</summary>
    private static bool Names(IElement cell, string name)
    {
        string text = cell.TextContent.Trim();
        return text == name || text.EndsWith("." + name, StringComparison.Ordinal);
    }

    private static IElement Expander<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        FunctionRow(page, name).QuerySelector(".ms-db-expander")!;

    private static List<string> Badges(IElement row) =>
        [.. row.QuerySelectorAll(".ms-db-badge").Select(x => System.Text.RegularExpressions.Regex.Replace(x.TextContent.Trim(), @"\s+", " "))];

    /// <summary>A function Marten does not own whose body the gate withholds.</summary>
    internal static DatabaseRoutineSummary WithheldRoutine(string schema, string name) =>
        new(
            DatabaseObjectKind.Function,
            schema,
            name,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            null,
            true,
            string.Empty,
            "integer",
            "sql",
            "volatile",
            false,
            false,
            [],
            DefinitionAvailable: false);

    /// <summary>A trigger whose function is in a schema this visitor is not shown, so its definition is withheld.</summary>
    internal static DatabaseTriggerSummary WithheldTrigger(string schema, string table, string name) =>
        new(
            DatabaseObjectKind.Trigger,
            schema,
            name,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            null,
            true,
            table,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            "BEFORE",
            ["INSERT"],
            true,
            true,
            "O",
            null,
            null,
            false,
            DefinitionAvailable: false);
}

/// <summary>
/// The real <see cref="IDatabaseObjectService" /> over a store that never connects, with a real
/// <see cref="StudioActionLog" /> and every line the studio logs captured - and a way to hand a page lists
/// from a fake while its definition reads go to the real service.
/// </summary>
/// <remarks>
/// <c>BrowseDatabase</c> is off, as it is by default (D4), so a definition read of anything Marten does not
/// own is refused at the capability - before a connection could be opened - with a ring entry and event
/// 9202 at Warning. That is exactly what the page must never cause by itself, and exactly what a page test
/// with a fake service cannot see.
/// </remarks>
internal sealed class RealDefinitionService : IAsyncDisposable
{
    private const string DummyConnectionString =
        "Host=marten-studio-db46-definitions.invalid;Database=none;Username=none;Password=none";

    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;
    private int asked;

    private RealDefinitionService(ServiceProvider provider, IServiceScope scope, CapturingLoggerProvider logs)
    {
        this.provider = provider;
        this.scope = scope;
        Logs = logs;
    }

    /// <summary>Everything the real studio logged, at every level.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The real studio's audit ring.</summary>
    public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

    /// <summary>The real service.</summary>
    public IDatabaseObjectService Service => scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>();

    /// <summary>How many definition reads a page handed to the real service.</summary>
    public int DefinitionsAsked => Volatile.Read(ref asked);

    public static RealDefinitionService Create()
    {
        var logs = new CapturingLoggerProvider();
        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });
        services.AddMarten(static options => options.Connection(DummyConnectionString));
        services.AddMartenStudio();
        services.AddSingleton<IAuthorizationService>(new TestStoreAuthorizationService());
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        ServiceProvider provider = services.BuildServiceProvider();

        return new RealDefinitionService(provider, provider.CreateScope(), logs);
    }

    /// <summary>A service that answers from <paramref name="lists" /> and reads definitions through the real one.</summary>
    public IDatabaseObjectService Over(IDatabaseObjectService lists) => new Split(lists, this);

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
    }

    private sealed class Split(IDatabaseObjectService lists, RealDefinitionService real) : IDatabaseObjectService
    {
        public Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            lists.GetOverviewAsync(scope, cancellationToken);

        public Task<DatabaseObjectList> ListAsync(StudioScope scope, DatabaseObjectQuery query, CancellationToken cancellationToken = default) =>
            lists.ListAsync(scope, query, cancellationToken);

        public Task<DatabaseObjectDetail> GetObjectAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) =>
            lists.GetObjectAsync(scope, schema, name, cancellationToken);

        public Task<DatabaseObjectDefinition> GetDefinitionAsync(StudioScope scope, DatabaseObjectRef reference, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref real.asked);
            return real.Service.GetDefinitionAsync(scope, reference, cancellationToken);
        }

        public Task<DatabaseSequenceValue> GetSequenceValueAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) =>
            lists.GetSequenceValueAsync(scope, schema, name, cancellationToken);
    }
}
