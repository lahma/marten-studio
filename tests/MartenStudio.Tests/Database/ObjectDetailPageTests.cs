using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;

using DetailPage = MartenStudio.Components.Pages.Database.ObjectDetail;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Object detail over <see cref="FakeDatabaseObjects" />: Quartz's <c>qrtz_triggers</c> in full, a Marten
/// document table that must never get a row grid, a view with a definition, and the neutral panel for a
/// link to something that is not there or not visible.
/// </summary>
public class ObjectDetailPageTests
{
    [Fact]
    public void The_studios_router_reaches_the_page()
    {
        StudioRouteTable.Match("database/object")!.PageType.Should().Be<DetailPage>();
    }

    [Fact]
    public void The_header_names_the_relation_and_says_what_it_is()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.Find(".ms-db-object-name").TextContent.Trim().Should().Be("qrtz_triggers");
        page.Find(".ms-db-object-meta .ms-copy-badge-value").TextContent.Trim().Should().Be("quartz.qrtz_triggers");
        page.Find(".ms-db-object-meta .ms-db-badge-kind").TextContent.Trim().Should().Be("table");
        page.Find(".ms-db-object-meta .ms-db-hint").TextContent.Trim().Should().Be("Quartz.NET");
        page.Find(".ms-db-object-meta").TextContent.Should().Contain("~1,204 rows");

        page.FindAll(".ms-db-breadcrumb a").Select(x => x.GetAttribute("href"))
            .Should().SatisfyRespectively(
                x => x.Should().StartWith("database?").And.Contain("store=default"),
                x => x.Should().StartWith("database?schema=quartz&kind=tables"));
    }

    [Fact]
    public void A_browsable_table_opens_on_its_rows_and_the_slot_is_handed_the_relation()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        ActiveTab(page).Should().Be("Rows");

        IElement slot = page.Find(".ms-db-rows-slot");
        slot.GetAttribute("data-schema").Should().Be("quartz");
        slot.GetAttribute("data-name").Should().Be("qrtz_triggers");
        slot.TextContent.Should().Contain("sched_name, trigger_name, trigger_group");
    }

    [Fact]
    public void A_marten_document_table_has_no_row_grid_but_a_card_that_links_to_documents_with_the_scope()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.StoreSchema, "mt_doc_customer", "rows");

        page.FindAll(".ms-db-rows-slot").Should().BeEmpty("a document table's rows are never read raw");

        IElement card = page.Find(".ms-db-rows-card");
        card.TextContent.Should().Contain("Browsed in Documents, where tenancy, soft delete and the serializer apply");

        IElement link = card.QuerySelector("a.ms-btn-primary")!;
        link.GetAttribute("href").Should().StartWith("documents/customer?").And.Contain("store=default").And.Contain("db=localhost.marten");
    }

    [Fact]
    public void A_table_whose_rows_may_not_be_read_opens_on_its_columns()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.StoreSchema, "mt_doc_customer");

        ActiveTab(page).Should().Be("Columns", "nobody should land on a refusal they did not ask for");
        page.FindAll(".ms-db-columns").Should().ContainSingle();
    }

    [Fact]
    public void An_event_table_s_rows_card_links_to_streams_the_feed_and_projections()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.EventSchema, "mt_events", "rows");

        List<string?> hrefs = [.. page.FindAll(".ms-db-rows-card a").Select(x => x.GetAttribute("href"))];

        hrefs.Should().HaveCount(3);
        hrefs[0].Should().StartWith("events/streams");
        hrefs[1].Should().StartWith("events/feed");
        hrefs[2].Should().StartWith("projections");
        hrefs.Should().OnlyContain(x => x!.Contains("store=default", StringComparison.Ordinal));
    }

    [Fact]
    public void A_foreign_table_s_rows_card_says_why_and_names_no_option()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "remote_orders", "rows");

        IElement card = page.Find(".ms-db-rows-card");
        card.GetAttribute("data-refusal").Should().Be(nameof(DatabaseRefusal.ForeignTable));
        card.TextContent.Should().Contain("A foreign table is listed, never read");
    }

    [Fact]
    public void With_the_gate_shut_the_rows_card_names_the_option()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail(FakeDatabaseObjects.StoreSchema, "host_settings");
        context.DatabaseObjects.Detail = detail with
        {
            Relation = detail.Relation! with
            {
                Rows = DatabaseRowAccess.Refused(DatabaseRefusal.CapabilityOff, DatabaseGate.CapabilityDenial),
            },
        };

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.StoreSchema, "host_settings", "rows");

        page.Find(".ms-db-rows-card").TextContent.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
    }

    [Fact]
    public void The_columns_tab_marks_the_key_and_links_each_foreign_key_to_its_parent()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "columns");

        List<IElement> rows = [.. page.FindAll(".ms-db-columns tbody tr")];
        rows.Should().HaveCount(8);

        rows.Where(x => x.QuerySelector(".ms-db-badge-key") is not null)
            .Select(x => x.QuerySelector(".ms-db-name")!.TextContent.Trim())
            .Should().Equal("sched_name", "trigger_name", "trigger_group");

        IElement jobName = rows.Single(x => x.QuerySelector(".ms-db-name")!.TextContent.Trim() == "job_name");
        IElement parent = jobName.QuerySelector("a.ms-db-fk-link")!;
        parent.TextContent.Trim().Should().Be("→ qrtz_job_details");
        parent.GetAttribute("href").Should().StartWith("database/object?schema=quartz&name=qrtz_job_details");

        rows.Single(x => x.QuerySelector(".ms-db-name")!.TextContent.Trim() == "next_fire_time")
            .TextContent.Should().Contain("bigint").And.Contain("null");
    }

    [Fact]
    public void The_keys_tab_shows_the_row_key_constraints_indexes_and_both_directions_of_foreign_key()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        page.Find(".ms-db-rowkey").TextContent.Should().Contain("(sched_name, trigger_name, trigger_group)").And.Contain("primary key");

        page.Find(".ms-db-constraints").TextContent.Should().Contain("PRIMARY KEY (sched_name, trigger_name, trigger_group)");

        List<IElement> indexes = [.. page.FindAll(".ms-db-indexes tbody tr")];
        indexes.Should().HaveCount(2);
        indexes[0].QuerySelector(".ms-db-badge-key")!.TextContent.Trim().Should().Be("primary");
        indexes[1].TextContent.Should().Contain("CREATE INDEX idx_qrtz_t_next_fire_time");

        IElement outbound = page.Find(".ms-db-fks-out tbody tr");
        outbound.QuerySelector("a.ms-db-name")!.GetAttribute("href").Should().StartWith("database/object?schema=quartz&name=qrtz_job_details");
        outbound.TextContent.Should().Contain("sched_name, job_name, job_group").And.Contain("no action");

        IElement inbound = page.Find(".ms-db-fks-in tbody tr");
        inbound.QuerySelector("a.ms-db-name")!.TextContent.Trim().Should().Be("quartz.qrtz_simprop_triggers");
        inbound.QuerySelector(".ms-db-fk-action-strong")!.TextContent.Trim().Should().Be("cascade", "a cascade reaches rows somewhere else and is drawn so it is seen");
    }

    [Fact]
    public void A_foreign_key_into_a_table_you_cannot_see_is_said_to_be_out_of_view_and_never_named()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail("quartz", "qrtz_triggers");
        context.DatabaseObjects.Detail = detail with
        {
            ForeignKeysOut =
            [
                new DatabaseForeignKeyInfo("fk_hidden", "quartz", "qrtz_triggers", ["job_name"], null, null, [], false, null, true, "no action", "no action"),
            ],
        };

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        page.Find(".ms-db-fks-out tbody tr").TextContent.Should().Contain("not visible");
        page.FindAll(".ms-db-fks-out a").Should().BeEmpty();
    }

    [Fact]
    public void The_triggers_tab_lists_the_relation_s_triggers_without_repeating_the_table()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail(FakeDatabaseObjects.Legacy, "orders");
        DatabaseTriggerSummary[] triggers =
        [
            .. FakeDatabaseObjects.Objects().OfType<DatabaseTriggerSummary>().Where(x => x.Table == "orders"),
        ];
        context.DatabaseObjects.Detail = detail with { Triggers = triggers };

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "orders", "triggers");

        page.FindAll(".ms-db-triggers thead th").Select(x => x.TextContent.Trim()).Should().NotContain("Table");
        page.FindAll(".ms-db-triggers tbody .ms-db-state").Select(x => x.TextContent.Trim()).Should().Equal("enabled", "disabled");
        page.ShouldPutEveryTableInALabelledScrollRegion();
    }

    [Fact]
    public void The_relationships_slot_lists_the_foreign_keys_both_ways_as_text()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "relationships");

        IElement slot = page.Find(".ms-db-relationships");
        slot.GetAttribute("data-name").Should().Be("qrtz_triggers");

        List<IElement> groups = [.. slot.QuerySelectorAll(".ms-db-relationships-group")];
        groups[0].QuerySelector("a.ms-db-name")!.TextContent.Trim().Should().Be("quartz.qrtz_job_details");
        groups[1].QuerySelector("a.ms-db-name")!.TextContent.Trim().Should().Be("quartz.qrtz_simprop_triggers");
        groups[1].TextContent.Should().Contain("on delete cascade");
    }

    [Fact]
    public void A_table_s_definition_tab_says_tables_have_none()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "definition");

        page.Find(".ms-db-definition-tab .ms-db-refusal").TextContent.Trim()
            .Should().Be("Tables have no stored definition; see Columns and Keys.");
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty("there is nothing to ask for");
    }

    [Fact]
    public void A_view_s_definition_tab_reads_its_query_once_and_colours_it()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "order_totals", "definition");

        page.WaitForAssertion(() => page.Find(".ms-db-definition-tab .ms-sql").TextContent.Should().Contain("GROUP BY o.customer_id"));
        page.Find(".ms-db-definition-tab").TextContent.Should().Contain("every read of it runs this query");
        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle()
            .Which.Should().Be(new DatabaseObjectRef(DatabaseObjectKind.View, FakeDatabaseObjects.Legacy, "order_totals"));
    }

    [Fact]
    public void A_view_that_reads_a_hidden_table_says_so_without_naming_it()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "secret_view", "definition");

        page.WaitForAssertion(() => page.Find(".ms-db-dependencies").TextContent.Should().Contain("a relation you cannot see"));
    }

    [Fact]
    public void Every_structure_tab_renders_from_the_fake_and_every_table_is_in_a_scroll_region()
    {
        using var context = DatabasePageData.Context();

        foreach (string tab in (string[])["columns", "keys"])
        {
            var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", tab);

            page.ShouldPutEveryTableInALabelledScrollRegion();
        }
    }

    [Fact]
    public void The_tab_round_trips_through_the_url()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        ActiveTab(page).Should().Be("Keys & indexes");

        foreach (IElement tab in page.FindAll(".ms-db-object-tabs a.ms-tab"))
        {
            string href = tab.GetAttribute("href")!;
            DatabaseRowLink link = DatabaseLinks.ReadRow(href);

            link.Schema.Should().Be("quartz");
            link.Name.Should().Be("qrtz_triggers");
            href.Should().Contain("store=default");

            DatabaseObjectTab? parsed = DatabaseLinks.ParseTab(Parameter(href, "tab"));
            parsed.Should().NotBeNull();
            tab.TextContent.Should().Contain(Label(parsed!.Value));
        }

        string columns = page.FindAll(".ms-db-object-tabs a.ms-tab").Single(x => x.TextContent.Contains("Columns", StringComparison.Ordinal)).GetAttribute("href")!;
        context.Navigate("marten/" + columns);

        page.WaitForAssertion(() => ActiveTab(page).Should().Be("Columns"));
        page.FindAll(".ms-db-columns").Should().ContainSingle();
    }

    [Fact]
    public void Open_in_query_is_offered_only_where_run_sql_is_on()
    {
        using var context = DatabasePageData.Context();

        var off = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        off.FindAll(".ms-db-object-actions a").Should().NotContain(x => x.TextContent.Trim() == "Open in Query");

        using var enabled = DatabasePageData.Context();
        enabled.WithAllCapabilities();

        var on = DatabasePageData.RenderObject(enabled, "quartz", "qrtz_triggers");

        IElement link = on.FindAll(".ms-db-object-actions a").Single(x => x.TextContent.Trim() == "Open in Query");
        string href = link.GetAttribute("href")!;

        href.Should().StartWith("query?mode=sql&sql=");
        Parameter(href, "sql").Should().Be("select * from \"quartz\".\"qrtz_triggers\" limit 100");
        href.Should().Contain("store=default");
    }

    [Fact]
    public void Open_in_query_is_not_offered_to_somebody_the_write_policy_refuses_run_sql()
    {
        using var context = DatabasePageData.Context();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = "Writers";
        context.AuthorizationService.Allow(static resource => resource.Capability != "RunSql");

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.FindAll(".ms-db-object-actions a").Should().NotContain(x => x.TextContent.Trim() == "Open in Query");
    }

    [Fact]
    public async Task The_copy_menu_copies_the_qualified_name_through_the_clipboard_helper()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.TextOfAll(".ms-db-copy-popover .ms-menu-item-label").Should().Equal("Copy qualified name", "Copy SELECT statement");
        page.Find(".ms-db-copy-menu-button").GetAttribute("popovertarget").Should().Be(page.Find(".ms-db-copy-popover").GetAttribute("id"));

        await page.FindAll(".ms-db-copy-popover .ms-menu-item")[0].ClickAsync(new MouseEventArgs());

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single()
            .Arguments[0].Should().Be("quartz.qrtz_triggers");

        await page.FindAll(".ms-db-copy-popover .ms-menu-item")[1].ClickAsync(new MouseEventArgs());

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"][^1]
            .Arguments[0].Should().Be("select * from \"quartz\".\"qrtz_triggers\" limit 100");
    }

    [Fact]
    public void A_name_that_cannot_be_quoted_offers_no_select_to_copy_or_to_open()
    {
        using var context = DatabasePageData.Context();
        context.WithAllCapabilities();

        var page = DatabasePageData.RenderObject(context, FakeDatabaseObjects.Legacy, "bad\"name");

        page.TextOfAll(".ms-db-copy-popover .ms-menu-item-label").Should().Equal("Copy qualified name");
        page.FindAll(".ms-db-object-actions a").Should().NotContain(x => x.TextContent.Trim() == "Open in Query");
    }

    [Fact]
    public void A_link_to_something_that_is_not_there_is_a_neutral_panel()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "no_such_table");

        IElement panel = page.Find(".ms-db-not-found");
        panel.TextContent.Should().Contain(DatabaseObjectAssembler.NotFound("quartz", "no_such_table"));
        page.FindAll(".ms-db-object-header, .ms-db-object-tabs").Should().BeEmpty();
    }

    [Fact]
    public void A_link_outside_the_gate_gets_the_same_neutral_panel_and_leaks_nothing()
    {
        using var context = DatabasePageData.Context();
        string sentence = DatabaseGate.SchemaDenial("secret", ["quartz", "legacy"]);
        context.DatabaseObjects.Detail = DatabaseObjectDetail.Unavailable(DatabaseRefusal.SchemaNotBrowsable, sentence);

        var page = DatabasePageData.RenderObject(context, "secret", "payroll");

        page.Find(".ms-db-not-found").TextContent.Should().Contain(sentence);
        page.FindAll(".ms-db-object-header, .ms-db-object-tabs, .ms-db-columns").Should().BeEmpty();
        page.Find(".ms-db-rail").TextContent.Should().NotContain("secret", "the rail lists what the visitor may see, not what the link named");
    }

    [Fact]
    public void The_rail_stays_with_the_schema_s_tables_and_marks_this_one_and_starts_closed_on_a_phone()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        IElement active = page.Find(".ms-db-rail-tables .ms-rail-link-active");
        active.TextContent.Should().Contain("qrtz_triggers");
        active.GetAttribute("aria-current").Should().Be("page");
        page.FindAll(".ms-db-rail-tables .ms-db-rail-name").Should().HaveCount(3);

        page.Find("details.ms-db-rail-collapse").HasAttribute("open").Should().BeFalse("an object is picked, so a phone opens on it");
        page.Find(".ms-db-rail-summary").TextContent.Trim().Should().Be("quartz · 3 tables");
    }

    [Fact]
    public async Task A_scope_change_reads_the_object_again_for_the_new_scope()
    {
        using var context = DatabasePageData.Context();
        context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");
        int asked = context.DatabaseObjects.ObjectsAsked.Count;

        await page.InvokeAsync(() => context.State.SetScopeAsync("second", null, null));

        page.WaitForAssertion(() => context.DatabaseObjects.ObjectsAsked.Count.Should().BeGreaterThan(asked));
        context.DatabaseObjects.LastScope!.StoreKey.Should().Be("second");
    }

    [Fact]
    public void A_failed_read_is_an_error_with_a_retry()
    {
        using var context = DatabasePageData.Context();
        context.DatabaseObjects.Failure = new InvalidOperationException("the catalog went away");

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers");

        page.Find(".ms-error-alert").TextContent.Should().Contain("the catalog went away");

        context.DatabaseObjects.Failure = null;
        page.Find(".ms-error-alert button").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-db-object-header").Should().ContainSingle());
    }

    private static string ActiveTab(IRenderedComponent<DetailPage> page) =>
        page.Find(".ms-db-object-tabs a[aria-current=page]").ChildNodes.OfType<IText>().First().TextContent.Trim();

    private static string Label(DatabaseObjectTab tab) => tab switch
    {
        DatabaseObjectTab.Rows => "Rows",
        DatabaseObjectTab.Keys => "Keys & indexes",
        DatabaseObjectTab.Triggers => "Triggers",
        DatabaseObjectTab.Relationships => "Relationships",
        DatabaseObjectTab.Definition => "Definition",
        _ => "Columns",
    };

    private static string? Parameter(string url, string name)
    {
        int start = url.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query =
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url[start..]);

        return query.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues value) ? value.ToString() : null;
    }
}
