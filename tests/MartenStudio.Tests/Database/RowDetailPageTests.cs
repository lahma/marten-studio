using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using DetailPage = MartenStudio.Components.Pages.Database.RowDetail;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Row detail over <see cref="FakeTableRows" />: every column, a value read whole on demand, "filter by this
/// value", what the row points at and what points at it, the Copy menu, the previous and next rows of the
/// page it came from, and the three panels for a row that cannot be shown.
/// </summary>
public class RowDetailPageTests
{
    private const string TriggerRow =
        "marten/database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-00&key.trigger_group=DEFAULT";

    [Fact]
    public void The_studios_router_reaches_the_page()
    {
        StudioRouteTable.Match("database/row")!.PageType.Should().Be<DetailPage>();
    }

    [Fact]
    public async Task Every_column_is_listed_with_its_type_and_the_key_is_asked_for_as_the_link_spells_it()
    {
        await using var context = await RowUiData.ContextAsync();

        var page = Render(context, TriggerRow);

        page.FindAll(".ms-row-kv .ms-row-kv-row").Should().HaveCount(FakeTableRows.Columns.Count);
        page.FindAll(".ms-row-kv-name").Select(static x => x.TextContent).Should().Equal(FakeTableRows.Columns.Select(static x => x.Name));
        page.FindAll(".ms-row-kv-type")[0].TextContent.Should().Be("text");

        context.TableRows.RowsAsked.Should().ContainSingle().Which.Should().BeEquivalentTo(FakeTableRows.KeyOf(0));
        context.TableRows.ReferencesAsked.Should().ContainSingle();

        page.Find(".ms-row-detail-key").TextContent.Should().Be("(QuartzScheduler, trigger-00, DEFAULT)");
        page.FindAll(".ms-db-breadcrumb a").Select(static x => x.TextContent).Should().Equal(["Database", "quartz", "qrtz_triggers"]);
        page.Find(".ms-row-breadcrumb-key").TextContent.Should().Be("(QuartzScheduler, trigger-00, DEFAULT)");
    }

    [Fact]
    public async Task Show_all_reads_a_value_that_was_cut_on_the_server()
    {
        await using var context = await RowUiData.ContextAsync();

        TableRowDetail detail = FakeTableRows.Detail();
        List<SqlCell> cells = [.. detail.Cells];
        int calendar = FakeTableRows.Columns.ToList().FindIndex(static x => x.Name == "calendar_name");
        cells[calendar] = new SqlCell(new string('b', 400) + "…", SqlCellKind.Text, true, 900_000);
        context.TableRows.Detail = detail with { Cells = cells };
        context.TableRows.Cell = new TableCellValue { Found = true, Text = new string('b', 600), FullLength = 900_000, Cap = 512 * 1024, Truncated = true };

        var page = Render(context, TriggerRow);

        IElement row = page.FindAll(".ms-row-kv-row")[calendar];
        row.QuerySelector(".ms-row-value-clamped").Should().NotBeNull("a long value is clamped to a few lines");
        row.QuerySelector("button.ms-row-show-all")!.Click();

        context.TableRows.CellsAsked.Should().ContainSingle().Which.Column.Should().Be("calendar_name");
        context.TableRows.CellsAsked[0].Key.Should().BeEquivalentTo(FakeTableRows.KeyOf(0));

        row = page.FindAll(".ms-row-kv-row")[calendar];
        row.QuerySelector(".ms-row-value-text")!.TextContent.Should().HaveLength(600);
        row.QuerySelector(".ms-row-value-note")!.TextContent.Should().Contain("MaxInlineDocumentBytes");
    }

    [Fact]
    public async Task A_value_links_to_the_rows_that_have_it_and_a_null_to_the_rows_that_have_none()
    {
        await using var context = await RowUiData.ContextAsync();

        var page = Render(context, TriggerRow);

        string group = FilterHref(page, "trigger_group")!;
        group.Should().StartWith("database/object?schema=quartz&name=qrtz_triggers&tab=rows");
        Query(group, "q").Should().Be("trigger_group = DEFAULT");
        Query(group, "store").Should().Be("default");

        // Row zero has no next fire time.
        Query(FilterHref(page, "next_fire_time")!, "q").Should().Be("next_fire_time is:null");

        FilterHref(page, "trigger_meta").Should().BeNull("a JSON value is not compared with =");
        FilterHref(page, "job_data").Should().BeNull("nor are bytes");
    }

    [Fact]
    public async Task References_resolve_to_the_parent_row_a_missing_badge_a_document_and_nothing_for_what_is_not_visible()
    {
        await using var context = await RowUiData.ContextAsync();

        TableRowReferences sample = FakeTableRows.References();
        RowOutboundReference present = sample.Outbound[0];

        context.TableRows.References = sample with
        {
            Outbound =
            [
                present,
                present with { ForeignKey = "fk_missing", State = RowReferenceState.Missing, Validated = false, ParentKey = null },
                new RowOutboundReference("fk_customer", ["customer_id"], ["6f9619ff-8b86-d011-b42d-00cf4fc964ff"], null, null, [], true,
                    RowReferenceState.MartenDocument, false, null, null, "customer", "6f9619ff-8b86-d011-b42d-00cf4fc964ff", "default", null),
                new RowOutboundReference("fk_secret", ["vault_id"], ["7"], null, null, [], true,
                    RowReferenceState.NotVisible, false, null, null, null, null, null, null),
            ],
        };

        var page = Render(context, TriggerRow);

        IReadOnlyList<IElement> outbound = page.FindAll("section[aria-label=References] li.ms-row-ref");
        outbound.Should().HaveCount(4);

        string parent = outbound[0].QuerySelector("a.ms-row-ref-key")!.GetAttribute("href")!;
        parent.Should().StartWith("database/row?schema=quartz&name=qrtz_job_details&key.sched_name=QuartzScheduler&key.job_name=job-0&key.job_group=DEFAULT");
        outbound[0].QuerySelector(".ms-graph-table-chip")!.TextContent.Should().Contain("qrtz_job_details");

        outbound[1].QuerySelector(".ms-badge-warning")!.TextContent.Should().Be("missing");
        outbound[1].QuerySelector(".ms-related-missing")!.TextContent.Should().Be("(QuartzScheduler, job-0, DEFAULT)");

        outbound[2].QuerySelector("a.ms-row-ref-key")!.GetAttribute("href")
            .Should().StartWith("documents/customer/doc?").And.Contain("id=6f9619ff-8b86-d011-b42d-00cf4fc964ff").And.Contain("store=default");

        outbound[3].TextContent.Should().Contain("a table you cannot see");
        outbound[3].QuerySelector("a").Should().BeNull();
        outbound[3].QuerySelector(".ms-graph-table-chip").Should().BeNull("a parent this visitor may not see is never named");
    }

    [Fact]
    public async Task Referenced_by_counts_are_bounded_link_to_the_child_rows_and_say_what_a_delete_does()
    {
        await using var context = await RowUiData.ContextAsync();

        var page = Render(context, TriggerRow);

        IReadOnlyList<IElement> inbound = page.FindAll("section[aria-label='Referenced by'] li.ms-row-ref");
        inbound.Should().HaveCount(2);

        inbound[0].QuerySelector(".ms-referenced-count")!.TextContent.Should().Be("1");
        inbound[0].QuerySelector(".ms-referenced-ondelete")!.TextContent.Should().Be("on delete cascade");

        inbound[1].QuerySelector(".ms-referenced-count")!.TextContent.Should().Be("1,000+");
        string href = inbound[1].QuerySelector("a.ms-row-ref-link")!.GetAttribute("href")!;
        href.Should().StartWith("database/object?schema=quartz&name=qrtz_fired_triggers&tab=rows");
        Query(href, "q").Should().Be("sched_name = QuartzScheduler trigger_name = trigger-00 trigger_group = DEFAULT");
        inbound[1].QuerySelector(".ms-referenced-ondelete")!.TextContent.Should().Be("on delete no action");
    }

    [Fact]
    public async Task The_copy_menu_copies_the_row_as_json_its_key_and_a_where_clause()
    {
        await using var context = await RowUiData.ContextAsync();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        var page = Render(context, TriggerRow);

        IReadOnlyList<IElement> items = page.FindAll("#ms-row-copy-menu .ms-menu-item");
        items.Select(static x => x.QuerySelector(".ms-menu-item-label")!.TextContent)
            .Should().Equal(["Copy row as JSON", "Copy key", "Copy WHERE clause"]);

        for (int index = 0; index < items.Count; index++)
        {
            // Found again each time: a click re-renders the menu, and bUnit's handler ids move with it.
            page.FindAll("#ms-row-copy-menu .ms-menu-item")[index].Click();
        }

        List<string> copied = [.. context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Select(static x => (string) x.Arguments[0]!)];

        copied.Should().HaveCount(3);
        copied[0].Should().Contain("\"sched_name\": \"QuartzScheduler\"")
            .And.Contain("\"next_fire_time\": null")
            .And.Contain("\"trigger_meta\": {", "a JSON column is copied as JSON, not as a string");
        copied[1].Should().Be("sched_name = QuartzScheduler trigger_name = trigger-00 trigger_group = DEFAULT");
        copied[2].Should().Be("where \"sched_name\" = 'QuartzScheduler'\n  and \"trigger_name\" = 'trigger-00'\n  and \"trigger_group\" = 'DEFAULT'");
        context.Toasts.Messages.Should().Contain(static x => x.Message == "Copied the WHERE clause.");
    }

    [Fact]
    public async Task Previous_and_next_are_the_rows_either_side_on_the_page_it_came_from()
    {
        await using var context = await RowUiData.ContextAsync();
        context.DatabaseBrowser.RememberPage(
            FakeTableRows.Schema,
            FakeTableRows.Table,
            [.. Enumerable.Range(0, 3).Select(static x => Ordered(FakeTableRows.KeyOf(x)))],
            "database/object?schema=quartz&name=qrtz_triggers&tab=rows");

        const string from = "&from=database%2Fobject%3Fschema%3Dquartz%26name%3Dqrtz_triggers%26tab%3Drows";
        var page = Render(context,
            "marten/database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-01&key.trigger_group=reports" + from);

        IReadOnlyList<IElement> arrows = page.FindAll(".ms-row-detail-actions .ms-doc-detail-nav a");
        arrows[0].GetAttribute("href").Should().Contain("key.trigger_name=trigger-00").And.Contain("from=");
        arrows[1].GetAttribute("href").Should().Contain("key.trigger_name=trigger-02");

        page.FindAll(".ms-row-detail-actions a").Single(static x => x.TextContent == "Back to the rows")
            .GetAttribute("href").Should().Be("database/object?schema=quartz&name=qrtz_triggers&tab=rows");
    }

    [Fact]
    public async Task A_return_link_that_leaves_the_database_browser_is_not_rendered()
    {
        await using var context = await RowUiData.ContextAsync();

        var page = Render(context, TriggerRow + "&from=javascript%3Aalert(1)");

        string back = page.FindAll(".ms-row-detail-actions a").Single(static x => x.TextContent == "Back to the rows").GetAttribute("href")!;
        back.Should().StartWith("database/object?schema=quartz&name=qrtz_triggers&tab=rows");
        page.Markup.Should().NotContain("javascript:");
    }

    [Fact]
    public async Task A_row_that_is_not_there_or_not_visible_gets_the_same_neutral_panel()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Detail = new TableRowDetail { Found = false, Reason = "There is no row with that key - it may have been deleted or changed." };

        var page = Render(context, TriggerRow);

        page.Find(".ms-row-not-found .ms-empty-title").TextContent.Should().Be("Not available here",
            "one heading for not there and not shown, as object detail's (UX-6)");
        page.FindAll(".ms-row-kv").Should().BeEmpty();
        context.TableRows.ReferencesAsked.Should().BeEmpty("nothing is read about a row that is not there");
        string notThere = page.Find(".ms-row-not-found").TextContent;

        context.TableRows.Detail = TableRowDetail.Refused(DatabaseRefusal.SchemaNotBrowsable, "Schema 'quartz' is not one MartenStudioOptions.BrowsableSchemas admits.");
        page = Render(context, TriggerRow.Replace("trigger-00", "trigger-01", StringComparison.Ordinal));

        page.Find(".ms-row-not-found").TextContent.Should().Be(notThere, "outside the gate reads exactly like not there");
        page.FindAll(".ms-db-breadcrumb a").Should().ContainSingle("the breadcrumb names no schema or table that was not shown");
    }

    [Fact]
    public async Task A_shut_gate_is_named_on_the_neutral_panel()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Detail = TableRowDetail.Refused(DatabaseRefusal.CapabilityOff, DatabaseGate.CapabilityDenial);

        var page = Render(context, TriggerRow);

        IElement panel = page.Find(".ms-row-not-found");
        panel.TextContent.Should().Contain("Capabilities.BrowseDatabase");
        panel.QuerySelector(".ms-empty-title")!.TextContent.Should().Be("Not available here",
            "the heading fits a refusal as well as an absence, so neither is said to be the other");
        panel.TextContent.Should().NotContain("deleted", "a gate that refused is not a row that went away");
        panel.QuerySelector("a.ms-db-back-link")!.TextContent.Should().Be("Back to the database browser");
    }

    [Fact]
    public async Task A_link_with_no_key_or_to_a_relation_with_none_says_to_open_the_row_in_the_grid()
    {
        await using var context = await RowUiData.ContextAsync();

        var page = Render(context, "marten/database/row?schema=legacy&name=audit_log");

        page.Find(".ms-row-keyless .ms-empty-description").TextContent
            .Should().Be("This table has no key, so a row cannot be linked; expand it in the grid.");
        context.TableRows.RowsAsked.Should().BeEmpty();

        context.TableRows.Detail = TableRowDetail.Refused(
            DatabaseRefusal.NotApplicable,
            "legacy.audit_log has no key, so a row of it cannot be opened on its own; its rows are shown inline.");

        page = Render(context, "marten/database/row?schema=legacy&name=audit_log&key.id=7");

        page.Find(".ms-row-keyless").TextContent.Should().Contain("its rows are shown inline");
        page.Find(".ms-row-keyless a").GetAttribute("href").Should().StartWith("database/object?schema=legacy&name=audit_log&tab=rows");
    }

    [Fact]
    public async Task A_failed_read_is_named_and_offers_a_retry()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Detail = new TableRowDetail
        {
            Error = new TableRowError("57014", "The read took longer than MartenStudioOptions.QueryTimeout (30 s) and Postgres cancelled it.", null),
        };

        var page = Render(context, TriggerRow);

        page.Find(".ms-row-state-failed .ms-row-state-title").TextContent.Should().Be("The read timed out");
        page.Find(".ms-row-state-failed button").Click();
        context.TableRows.RowsAsked.Should().HaveCount(2);
    }

    // ----------------------------------------------------------------------------------------------

    private static IRenderedComponent<DetailPage> Render(StudioComponentContext context, string url)
    {
        context.Navigate(url);

        var page = context.Render<DetailPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-row-detail-header, .ms-empty, .ms-error-alert, .ms-row-state").Should().NotBeEmpty());
        return page;
    }

    private static string? FilterHref(IRenderedComponent<DetailPage> page, string column) =>
        page.FindAll(".ms-row-kv-row")
            .Single(x => x.QuerySelector(".ms-row-kv-name")!.TextContent == column)
            .QuerySelector("a.ms-row-filter-by")?.GetAttribute("href");

    private static IReadOnlyList<KeyValuePair<string, string>> Ordered(IReadOnlyDictionary<string, string> key) =>
        [.. FakeTableRows.Key.Columns.Select(x => new KeyValuePair<string, string>(x, key[x]))];

    private static string? Query(string url, string name)
    {
        int start = url.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(start < 0 ? string.Empty : url[start..]);

        return query.TryGetValue(name, out StringValues value) ? value.ToString() : null;
    }
}
