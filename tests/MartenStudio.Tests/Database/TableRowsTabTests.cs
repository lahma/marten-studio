using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The Rows tab over <see cref="FakeTableRows" />: the header, the cells a table of somebody else's data
/// actually has, the links out of a row, the strip, the paging note and its count, and every state the
/// service answers with instead of rows.
/// </summary>
public class TableRowsTabTests
{
    // ----------------------------------------------------------------------------------------------
    // The header
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_header_says_its_type_the_key_and_the_parent_it_points_at()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        IElement sched = Header(tab, "sched_name");
        sched.QuerySelector(".ms-row-col-type")!.TextContent.Should().Be("text");
        sched.QuerySelector(".ms-row-pk")!.TextContent.Should().Be("PK 1", "a three-column key numbers its columns");

        IElement job = Header(tab, "job_name");
        IElement parent = job.QuerySelector("a.ms-row-fk-marker")!;
        parent.TextContent.Should().Be("→ qrtz_job_details");
        parent.GetAttribute("href").Should().StartWith("database/object?schema=quartz&name=qrtz_job_details").And.Contain("store=default");
        job.QuerySelector(".ms-row-pk").Should().BeNull();

        Header(tab, "trigger_meta").QuerySelector(".ms-row-col-type")!.TextContent.Should().Be("jsonb");

        tab.FindAll("thead th.ms-row-col").Select(static x => x.QuerySelector(".ms-row-col-name")!.TextContent)
            .Take(3).Should().Equal(["sched_name", "trigger_name", "trigger_group"], "the row key is first, in key order");
    }

    [Fact]
    public async Task Sorting_a_column_writes_sort_and_dir_and_the_key_order_header_turns_the_key_order_round()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        Header(tab, "next_fire_time").QuerySelector("button.ms-row-sort")!.Click();
        Query(context.CurrentUri, "sort").Should().Be("next_fire_time");
        Query(context.CurrentUri, "dir").Should().Be("asc");
        Query(context.CurrentUri, "tab").Should().Be("rows");

        // The key order has no sort column: its first column's header reverses it.
        Header(tab, "sched_name").GetAttribute("aria-sort").Should().Be("ascending");
        Header(tab, "sched_name").QuerySelector("button.ms-row-sort")!.Click();
        Query(context.CurrentUri, "sort").Should().BeNull();
        Query(context.CurrentUri, "dir").Should().Be("desc");

        // A page sorted by a column: clicking it again turns it round.
        context.TableRows.Page = FakeTableRows.Page() with
        {
            Paging = new TableRowPaging(TableRowPagingMode.Key, FakeTableRows.Key.Columns, "next_fire_time", SortDirection.Ascending, 0, null),
        };
        tab = RowUiData.RenderTab(context, "sort=next_fire_time&dir=asc");

        Header(tab, "next_fire_time").GetAttribute("aria-sort").Should().Be("ascending");
        Header(tab, "next_fire_time").QuerySelector("button.ms-row-sort")!.Click();
        Query(context.CurrentUri, "sort").Should().Be("next_fire_time");
        Query(context.CurrentUri, "dir").Should().Be("desc");
    }

    [Fact]
    public async Task An_unindexed_sort_is_marked_on_its_header()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Page() with
        {
            Paging = new TableRowPaging(TableRowPagingMode.Key, FakeTableRows.Key.Columns, "calendar_name", SortDirection.Ascending, 0, null),
            Verdict = new RowFilterVerdict
            {
                SortLevel = IndexVerdictLevel.Red,
                Chips = [new SearchChip(SearchChipKind.Sort, "sort", IndexVerdictLevel.Red, "No index covers calendar_name.", null)],
            },
        };

        var tab = RowUiData.RenderTab(context, "sort=calendar_name");

        Header(tab, "calendar_name").QuerySelector(".ms-doc-sort-hint-red")!.TextContent.Should().Be("!");
        Header(tab, "next_fire_time").QuerySelector(".ms-doc-sort-hint").Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // Cells
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Null_is_its_own_thing_and_a_number_is_right_aligned()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        // Row 1 has no calendar; row 0 has no next fire time.
        IElement calendar = Cell(tab, 1, "calendar_name");
        calendar.QuerySelector(".ms-query-null")!.TextContent.Should().Be("NULL");

        IElement next = Cell(tab, 1, "next_fire_time");
        next.ClassList.Should().Contain("ms-row-kind-number");
        Cell(tab, 1, "trigger_name").ClassList.Should().NotContain("ms-query-cell-text",
            "that is the class of the inline-block span inside a cell, and a cell that is an inline-block is not a table cell");
        next.QuerySelector(".ms-query-cell-text")!.TextContent.Should().Be((FakeTableRows.BaseTicks + TimeSpan.TicksPerHour).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "the number stays the value; the date is a hint under it");
    }

    [Fact]
    public async Task A_timestamptz_is_shown_in_the_chosen_zone_and_a_timestamp_is_never_converted()
    {
        await using var context = await RowUiData.ContextAsync();
        context.State.SelectedTimeZoneId = "Asia/Kolkata";
        context.TableRows.Page = RowUiData.Keyless();

        var tab = RowUiData.RenderTab(context, schema: "legacy", name: "audit_log");

        IElement zoned = Cell(tab, 0, "happened_at").QuerySelector(".ms-row-time")!;
        zoned.TextContent.Should().Be("2026-09-29 19:33:00", "14:03 UTC is 19:33 in India");
        zoned.GetAttribute("title").Should().Contain("2026-09-29T14:03:00.0000000Z", "the stored value is kept in the tooltip");

        IElement local = Cell(tab, 0, "logged_local");
        local.QuerySelector(".ms-row-time")!.TextContent.Should().Be("2026-09-29 14:03:00", "a timestamp has no zone to convert from");
        local.QuerySelector(".ms-row-notz")!.TextContent.Should().Be("no tz");
    }

    [Fact]
    public async Task A_json_cell_opens_in_the_viewer_without_reading_it_again()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        Cell(tab, 0, "trigger_meta").QuerySelector("button.ms-query-json-chip")!.Click();

        IElement panel = tab.Find(".ms-row-json-panel");
        panel.QuerySelector(".ms-query-json-panel-title")!.TextContent.Should().Contain("trigger_meta");
        panel.QuerySelector(".ms-json")!.TextContent.Should().Contain("retries");
        context.TableRows.CellsAsked.Should().BeEmpty("a JSON cell that was not cut is on the page already");
    }

    [Fact]
    public async Task A_bytea_holding_json_is_sniffed_and_read_whole_once()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        IElement chip = Cell(tab, 0, "job_data").QuerySelector("button.ms-row-bytes-json")!;
        chip.TextContent.Should().Contain("JSON · ").And.Contain(" B");

        chip.Click();
        tab.Find(".ms-row-json-panel .ms-json").TextContent.Should().Contain("reportId");

        Cell(tab, 0, "job_data").QuerySelector("button.ms-row-bytes-json")!.Click();

        context.TableRows.CellsAsked.Should().ContainSingle("the whole value is kept for as long as the page is on screen")
            .Which.Column.Should().Be("job_data");
        context.TableRows.CellsAsked[0].Key.Should().BeEquivalentTo(FakeTableRows.KeyOf(0));
    }

    /// <summary>
    /// UX-6, U8: a bytea of UTF-8 text opens under the grid as text, as a JSON one opens in the viewer -
    /// from the page when every byte is on it, and read whole (capped) when it was cut. A gzip stream or an
    /// image stays a label, and its title says why nothing opens.
    /// </summary>
    [Fact]
    public async Task A_bytea_holding_text_opens_as_text_and_binary_says_why_it_does_not()
    {
        await using var context = await RowUiData.ContextAsync();

        byte[] note = System.Text.Encoding.UTF8.GetBytes("sku;quantity\nSKU-001;4");
        byte[] longNote = System.Text.Encoding.UTF8.GetBytes(new string('a', 200) + " the end");
        byte[] gzip = [0x1f, 0x8b, 0x08, 0x00, 0x00];
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00];

        context.TableRows.Page = RowUiData.PageOf(
            [RowUiData.Column("attachment", 1, "bytea", sortable: false)],
            [
                new TableRow(null, "(0,1)", [Bytes(note, note.Length)]),
                new TableRow(null, "(0,2)", [Bytes(longNote[..64], longNote.Length)]),
                new TableRow(null, "(0,3)", [Bytes(gzip, 52)]),
                new TableRow(null, "(0,4)", [Bytes(png, 30)]),
            ],
            key: null,
            mode: TableRowPagingMode.Ctid,
            name: "integration_messages");
        context.TableRows.Cell = new TableCellValue { Found = true, Bytes = longNote, FullLength = longNote.Length, Cap = 512 * 1024 };

        var tab = RowUiData.RenderTab(context, schema: "legacy", name: "integration_messages");

        IElement whole = tab.FindAll("tbody tr.ms-row-item")[0].QuerySelector("button.ms-row-bytes-text")!;
        whole.TextContent.Should().StartWith("text · ");
        whole.Click();

        tab.Find(".ms-row-json-panel .ms-row-text-panel-body").TextContent.Should().Be("sku;quantity\nSKU-001;4");
        tab.Find(".ms-row-json-panel .ms-row-text-panel").GetAttribute("role").Should().Be("region");
        context.TableRows.CellsAsked.Should().BeEmpty("every byte of it was on the page");

        tab.FindAll("tbody tr.ms-row-item")[1].QuerySelector("button.ms-row-bytes-text")!.Click();

        tab.WaitForAssertion(() => tab.Find(".ms-row-json-panel .ms-row-text-panel-body").TextContent.Should().EndWith("the end"));
        context.TableRows.CellsAsked.Should().ContainSingle("a value cut on the page is read whole once")
            .Which.Key.Should().ContainKey("ctid");

        foreach (int row in (int[])[2, 3])
        {
            IElement cell = tab.FindAll("tbody tr.ms-row-item")[row].QuerySelector("td.ms-row-cell")!;
            cell.QuerySelector("button").Should().BeNull("nothing a browser can show is in a compressed stream or an image's bytes");
            cell.QuerySelector(".ms-row-bytes")!.GetAttribute("title").Should().Contain("shown as its size and type only").And.Contain("does not open");
        }

        static SqlCell Bytes(byte[] prefix, int fullLength) =>
            new("\\x" + Convert.ToHexStringLower(prefix) + (prefix.Length < fullLength ? "…" : string.Empty) + " (" + fullLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes)",
                SqlCellKind.Binary,
                prefix.Length < fullLength,
                fullLength);
    }

    [Fact]
    public async Task A_bigint_of_ticks_in_a_time_column_gets_a_date_hint()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        Cell(tab, 1, "next_fire_time").QuerySelector(".ms-row-date-hint")!.TextContent.Should().Be("≈ 2026-09-29 01:00");
        Cell(tab, 0, "next_fire_time").QuerySelector(".ms-row-date-hint").Should().BeNull("a NULL has nothing to read");
        Header(tab, "next_fire_time").QuerySelector("button.ms-row-date-toggle")!.GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public async Task No_date_hint_on_a_bigint_whose_name_is_not_a_time_or_on_a_page_of_mixed_units()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = RowUiData.Keyless();

        var tab = RowUiData.RenderTab(context, schema: "legacy", name: "audit_log");

        tab.FindAll(".ms-row-date-hint").Should().BeEmpty("retry_count holds tick-sized values, but its name is not a time");

        context.TableRows.Page = RowUiData.PageOf(
            [RowUiData.Column("fire_time", 1, "bigint")],
            [
                new TableRow(null, null, [RowUiData.Number(FakeTableRows.BaseTicks)]),
                new TableRow(null, null, [RowUiData.Number(1_790_000_000_000)]),
            ]);

        tab = RowUiData.RenderTab(context, schema: "legacy", name: "events");

        tab.FindAll(".ms-row-date-hint").Should().BeEmpty("ticks in one row and epoch milliseconds in the next are no one unit");
    }

    [Fact]
    public async Task The_date_hint_can_be_turned_off_for_the_table_and_the_choice_is_remembered()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        Header(tab, "next_fire_time").QuerySelector("button.ms-row-date-toggle")!.Click();

        tab.FindAll(".ms-row-date-hint").Should().BeEmpty();
        context.JSInterop.Invocations["martenStudio.prefs.set"].Should().Contain(x =>
            Equals(x.Arguments[0], "ms_db_datehint_default_quartz.qrtz_triggers") && Equals(x.Arguments[1], "off"));
    }

    // ----------------------------------------------------------------------------------------------
    // Links
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_key_cell_opens_the_row_with_its_key_the_scope_and_the_way_back()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        IElement link = Cell(tab, 0, "sched_name").QuerySelector("a.ms-row-open-link")!;
        string href = link.GetAttribute("href")!;

        href.Should().StartWith("database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler&key.trigger_name=trigger-00&key.trigger_group=DEFAULT");
        Query(href, "store").Should().Be("default");
        Query(href, "db").Should().Be("localhost.marten");
        Query(href, "from").Should().StartWith("marten/database/object?schema=quartz&name=qrtz_triggers&tab=rows");

        Cell(tab, 0, "sched_name").ClassList.Should().Contain("ms-row-sticky", "the row's name stays put while the rest scrolls");
        Cell(tab, 0, "trigger_name").QuerySelector("a").Should().BeNull("one link per row, not one per key column");
    }

    [Fact]
    public async Task A_foreign_key_cell_links_to_the_parent_row_when_the_key_is_the_parents_key()
    {
        await using var context = await RowUiData.ContextAsync();

        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail(FakeTableRows.Schema, FakeTableRows.Table) with
        {
            ForeignKeysOut =
            [
                new DatabaseForeignKeyInfo(
                    FakeTableRows.JobForeignKey,
                    FakeTableRows.Schema, FakeTableRows.Table, ["sched_name", "job_name"],
                    FakeTableRows.Schema, "qrtz_job_details", ["sched_name", "job_name"],
                    true, null, true, "no action", "no action"),
            ],
        };

        // What the tab asks about the parent: its row key is exactly the columns the key references.
        context.DatabaseObjects.Detail = FakeDatabaseObjects.Detail(FakeTableRows.Schema, "qrtz_job_details") with
        {
            RowKey = new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "qrtz_job_details_pkey", ["sched_name", "job_name"]),
        };

        var tab = RowUiData.RenderTab(context, detail: detail);

        IElement link = Cell(tab, 2, "job_name").QuerySelector("a.ms-row-fk-link")!;
        link.TextContent.Should().Be("job-1");
        link.GetAttribute("href").Should().StartWith("database/row?schema=quartz&name=qrtz_job_details&key.sched_name=QuartzScheduler&key.job_name=job-1");
        context.DatabaseObjects.ObjectsAsked.Should().Equal(["quartz.qrtz_job_details"], "one read of the parent, for its key");
    }

    [Fact]
    public async Task A_foreign_key_cell_links_to_the_parents_rows_filtered_when_the_key_is_not_the_parents_key()
    {
        await using var context = await RowUiData.ContextAsync();

        // The sample's key is (sched_name, job_name, job_group) and the parent's row key is (id): the cell
        // can only narrow the parent's rows by the values this page has.
        var tab = RowUiData.RenderTab(context);

        string href = Cell(tab, 2, "job_name").QuerySelector("a.ms-row-fk-link")!.GetAttribute("href")!;

        href.Should().StartWith("database/object?schema=quartz&name=qrtz_job_details&tab=rows");
        Query(href, "q").Should().Be("sched_name = QuartzScheduler job_name = job-1");
    }

    // ----------------------------------------------------------------------------------------------
    // Paging
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_paging_note_says_the_estimate_the_walk_and_the_order()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-row-paging-parts").TextContent.Should().Be("~24 rows · keyset on (sched_name, trigger_name, trigger_group) · key order ↑");

        context.TableRows.Page = FakeTableRows.Page() with
        {
            Paging = new TableRowPaging(TableRowPagingMode.Key, FakeTableRows.Key.Columns, "next_fire_time", SortDirection.Ascending, 0, null),
        };
        tab = RowUiData.RenderTab(context, "sort=next_fire_time");

        tab.Find(".ms-row-paging-parts").TextContent.Should().EndWith("· next_fire_time ↑");
    }

    [Fact]
    public async Task Next_writes_the_cursor_and_prev_takes_it_back()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-pager").GetAttribute("aria-label").Should().Be("Row pages");
        tab.FindAll(".ms-pager-jump").Should().BeEmpty("a row is opened by its key, not typed into a box");

        Pager(tab, "Next").Click();

        string cursor = Query(context.CurrentUri, "cursor")!;
        TableRowCursor.Decode(cursor)!.Key.Should().Equal([FakeTableRows.Scheduler, "trigger-04", "DEFAULT"]);

        // The address bar moved; the object page re-renders the tab on every navigation.
        tab.Render();
        context.TableRows.Requests[^1].Request.Cursor!.Key.Should().Equal([FakeTableRows.Scheduler, "trigger-04", "DEFAULT"]);

        Pager(tab, "Prev").Click();
        Query(context.CurrentUri, "cursor").Should().BeNull("the page before the second is the first");
    }

    [Fact]
    public async Task The_exact_count_is_asked_for_on_demand_and_a_refusal_is_said_inline()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        tab.Find("button.ms-row-count-button").Click();
        tab.Find(".ms-row-count").TextContent.Should().Be("= 24 rows");
        context.TableRows.CountsAsked.Should().Equal([null]);

        context.TableRows.Count = TableRowCount.Refused(DatabaseRefusal.WritePolicy, "Your account may not count these rows.");
        tab.Find("button.ms-row-count-button").Click();
        tab.Find(".ms-row-count-refused").TextContent.Should().Be("Your account may not count these rows.");
    }

    // ----------------------------------------------------------------------------------------------
    // The strip
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_withheld_read_runs_anyway_when_asked_and_the_answer_is_remembered()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Withheld();

        var tab = RowUiData.RenderTab(context, "q=calendar_name+~+business");

        tab.FindAll("table.ms-row-grid").Should().BeEmpty("nothing was read");
        tab.Find(".ms-row-withheld").TextContent.Should().Contain("ExactCountThreshold");
        context.TableRows.Requests[^1].Request.RunAnyway.Should().BeFalse();

        tab.Find(".ms-row-withheld button").Click();

        context.TableRows.Requests[^1].Request.RunAnyway.Should().BeTrue();
        context.DatabaseBrowser.HasAcceptedRead(FakeTableRows.Schema, FakeTableRows.Table, "calendar_name ~ business", null, null).Should().BeTrue();
    }

    [Fact]
    public async Task A_filter_that_does_not_parse_points_at_the_place()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Page(0, hasMore: false) with
        {
            State = TableRowPageState.Invalid,
            Reason = "The filter cannot be used as it is; the marked terms say why.",
            Verdict = new RowFilterVerdict
            {
                Errors = [new SearchGrammarError("'=' needs a value after it.", 14, 1)],
                Chips = [new SearchChip(SearchChipKind.Error, "'=' needs a value after it.", IndexVerdictLevel.Red, "'=' needs a value after it.", null)],
                FilterLevel = IndexVerdictLevel.Red,
            },
        };

        var tab = RowUiData.RenderTab(context, "q=" + Uri.EscapeDataString("trigger_state ="));

        tab.Find(".ms-row-filter-caret").TextContent.Should().Be("trigger_state =\n              ^");
        tab.Find(".ms-row-filter-error-text").TextContent.Should().Contain("'=' needs a value after it.");
        tab.Find(".ms-row-filter-error-at").TextContent.Should().Be("at character 15");
        tab.Find(".ms-verdict-badge").TextContent.Should().Be("cannot parse");
    }

    [Fact]
    public async Task Show_SQL_names_the_parameters_and_never_the_values()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context, "q=" + Uri.EscapeDataString("trigger_group = SECRET-VALUE"));

        IElement disclosure = tab.Find(".ms-row-filter-strip .ms-sql-disclosure");
        disclosure.QuerySelector("summary")!.TextContent.Should().Be("Show SQL");
        disclosure.QuerySelector(".ms-sql-parameters")!.TextContent.Should().Be("Parameters: @cap, @jsonCap, @bytes, @f1, @limit");
        disclosure.TextContent.Should().NotContain("SECRET-VALUE");
    }

    [Fact]
    public async Task Removing_a_chip_takes_out_its_term_and_keeps_the_others_as_typed()
    {
        await using var context = await RowUiData.ContextAsync();
        const string filter = "trigger_group = DEFAULT calendar_name ~ \"business days\"";

        context.TableRows.Page = FakeTableRows.Page() with
        {
            Verdict = new RowFilterVerdict
            {
                Terms =
                [
                    new RowFilterTerm("trigger_group", RowFilterOperator.Equal, "DEFAULT", 0, 23),
                    new RowFilterTerm("calendar_name", RowFilterOperator.Contains, "business days", 24, 32),
                ],
                Chips =
                [
                    new SearchChip(SearchChipKind.Predicate, "trigger_group = DEFAULT", IndexVerdictLevel.Amber, "Not first in an index.", null),
                    new SearchChip(SearchChipKind.Predicate, "calendar_name ~ \"business days\"", IndexVerdictLevel.Red, "A substring match reads every row.", null),
                    new SearchChip(SearchChipKind.Sort, "sort", IndexVerdictLevel.Green, "The row key's index serves this order.", null),
                ],
                FilterLevel = IndexVerdictLevel.Red,
            },
        };

        var tab = RowUiData.RenderTab(context, "q=" + Uri.EscapeDataString(filter));

        tab.FindAll(".ms-row-chip").Select(static x => x.QuerySelector(".ms-row-chip-text")!.TextContent)
            .Should().Equal(["trigger_group = DEFAULT", "calendar_name ~ \"business days\""]);
        tab.FindAll(".ms-row-chip.ms-verdict-red").Should().ContainSingle();

        tab.FindAll(".ms-row-chip-remove")[0].Click();

        Query(context.CurrentUri, "q").Should().Be("calendar_name ~ \"business days\"");
    }

    // ----------------------------------------------------------------------------------------------
    // States
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_relation_is_not_the_same_as_a_filter_that_matched_nothing()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Page(0, hasMore: false);

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-row-empty-none .ms-empty-title").TextContent.Should().Be("Empty (0 rows)");
        tab.FindAll(".ms-row-empty-nomatch").Should().BeEmpty();

        tab = RowUiData.RenderTab(context, "q=" + Uri.EscapeDataString("trigger_group = nightly"));

        IElement none = tab.Find(".ms-row-empty-nomatch");
        none.QuerySelector(".ms-empty-title")!.TextContent.Should().Be("No rows match trigger_group = nightly");
        none.QuerySelector("button")!.Click();

        Query(context.CurrentUri, "q").Should().BeNull();
    }

    [Theory]
    [InlineData("55000", "This materialized view has never been refreshed.", "Never refreshed")]
    [InlineData("42501", "The role 'studio_reader' (MartenStudioOptions.SqlConsoleRole) has no privilege to read this.", "No privilege to read these rows")]
    [InlineData("57014", "The read took longer than MartenStudioOptions.QueryTimeout (30 s) and Postgres cancelled it.", "The read timed out")]
    public async Task A_failed_read_is_named_by_what_happened_and_offers_a_retry(string sqlState, string sentence, string title)
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Page(0, hasMore: false) with
        {
            State = TableRowPageState.Failed,
            Error = new TableRowError(sqlState, sentence, "postgres said so"),
        };

        var tab = RowUiData.RenderTab(context);

        IElement failure = tab.Find(".ms-row-state-failed");
        failure.GetAttribute("data-sqlstate").Should().Be(sqlState);
        failure.QuerySelector(".ms-row-state-title")!.TextContent.Should().StartWith(title);
        failure.QuerySelector(".ms-row-state-text")!.TextContent.Should().Be(sentence);

        int reads = context.TableRows.Requests.Count;
        failure.QuerySelector("button")!.Click();
        context.TableRows.Requests.Should().HaveCount(reads + 1);
    }

    [Fact]
    public async Task Row_level_security_is_said_by_its_name()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = FakeTableRows.Page(0, hasMore: false) with
        {
            State = TableRowPageState.Failed,
            RowSecurity = true,
            Error = new TableRowError("42704", "Row-level security is on for this table, and one of its policies needs something this session does not have.", null),
        };

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-row-state-title").TextContent.Should().StartWith("Row-level security stopped the read");
    }

    [Fact]
    public async Task A_refusal_says_which_gate_and_draws_no_grid()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = TableRowPage.Refused(DatabaseRefusal.CapabilityOff, DatabaseGate.CapabilityDenial);

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-row-state-refused").TextContent.Should().Contain("Capabilities.BrowseDatabase");
        tab.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task With_a_tenant_selected_the_tab_says_the_tenant_does_not_filter_these_rows()
    {
        await using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));
        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);
        await context.State.SetScopeAsync("default", "localhost.marten", "acme", Xunit.TestContext.Current.CancellationToken);

        var tab = RowUiData.RenderTab(context);

        tab.Find(".ms-row-tenant-note").TextContent.Should().Contain("acme").And.Contain("does not filter these rows");
    }

    [Fact]
    public async Task Every_table_is_in_a_labelled_scroll_region_and_a_page_being_read_is_marked_busy()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        tab.ShouldPutEveryTableInALabelledScrollRegion();
        tab.Find(".ms-row-grid-scroll").GetAttribute("aria-label").Should().Be("Rows of quartz.qrtz_triggers");

        var grid = context.Render<RowGrid>(parameters => parameters
            .Add(x => x.Model, (object?) FakeTableRows.Page())
            .Add(x => x.Schema, FakeTableRows.Schema)
            .Add(x => x.Name, FakeTableRows.Table)
            .Add(x => x.Busy, true));

        grid.Find(".ms-row-grid-scroll").GetAttribute("aria-busy").Should().Be("true");
        grid.Find(".ms-row-grid-scroll").ClassList.Should().Contain("ms-row-grid-busy");
    }

    // ----------------------------------------------------------------------------------------------
    // The keyboard
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Space_opens_a_row_in_place_and_Enter_opens_its_detail()
    {
        await using var context = await RowUiData.ContextAsync();

        var tab = RowUiData.RenderTab(context);

        IElement row = tab.FindAll("tr.ms-row-item")[0];
        row.GetAttribute("tabindex").Should().Be("0");
        row.GetAttribute("data-ms-row").Should().NotBeNull("marten-studio.js keeps the browser's own Space off a row by this");

        row.KeyDown(new KeyboardEventArgs { Key = " " });

        IElement preview = tab.Find("tr.ms-row-preview-row");
        preview.QuerySelectorAll(".ms-row-preview-item").Should().HaveCount(FakeTableRows.Columns.Count, "every column, top to bottom");
        tab.FindAll("tr.ms-row-item")[0].GetAttribute("aria-expanded").Should().Be("true");

        tab.FindAll("tr.ms-row-item")[0].KeyDown(new KeyboardEventArgs { Key = "Enter" });

        context.CurrentUri.Should().Contain("database/row?schema=quartz&name=qrtz_triggers&key.sched_name=QuartzScheduler");
    }

    [Fact]
    public async Task A_keyless_row_opens_in_place_and_reads_a_cut_value_by_its_ctid()
    {
        await using var context = await RowUiData.ContextAsync();
        context.TableRows.Page = RowUiData.Keyless();
        context.TableRows.Cell = new TableCellValue { Found = true, Text = new string('x', 5000), FullLength = 5000, Cap = 512 * 1024 };

        var tab = RowUiData.RenderTab(context, schema: "legacy", name: "audit_log");

        tab.FindAll("a.ms-row-open-link").Should().BeEmpty("a row of a relation with no key has no page of its own");

        tab.FindAll("tr.ms-row-item")[0].KeyDown(new KeyboardEventArgs { Key = " " });
        tab.Find("tr.ms-row-preview-row button.ms-row-show-all").Click();

        context.TableRows.CellsAsked.Should().ContainSingle();
        context.TableRows.CellsAsked[0].Key.Should().Equal(new Dictionary<string, string> { ["ctid"] = "(0,1)" });
        context.TableRows.CellsAsked[0].Column.Should().Be("message");
        tab.Find("tr.ms-row-preview-row .ms-row-value-text").TextContent.Should().HaveLength(5000);
    }

    [Fact]
    public async Task The_object_page_hands_its_rows_tab_the_relation()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderObject(context, FakeTableRows.Schema, FakeTableRows.Table);

        page.Find(".ms-db-rows-slot table.ms-row-grid").Should().NotBeNull();
        context.TableRows.Requests.Should().ContainSingle().Which.Name.Should().Be(FakeTableRows.Table);
    }

    // ----------------------------------------------------------------------------------------------

    private static IElement Header(IRenderedComponent<TableRowsTab> tab, string column) =>
        tab.FindAll("thead th.ms-row-col").Single(x => x.QuerySelector(".ms-row-col-name")!.TextContent == column);

    private static IElement Cell(IRenderedComponent<TableRowsTab> tab, int row, string column)
    {
        List<string> headers = [.. tab.FindAll("thead th").Select(static x => x.QuerySelector(".ms-row-col-name")?.TextContent ?? string.Empty)];
        int index = headers.IndexOf(column);

        index.Should().BeGreaterThan(0, $"there is a column '{column}'");

        return tab.FindAll("tr.ms-row-item")[row].Children[index];
    }

    private static IElement Pager(IRenderedComponent<TableRowsTab> tab, string label) =>
        tab.FindAll(".ms-pager button").Single(x => x.TextContent.Trim() == label);

    private static string? Query(string url, string name)
    {
        int start = url.IndexOf('?', StringComparison.Ordinal);
        Dictionary<string, StringValues> query = QueryHelpers.ParseQuery(start < 0 ? string.Empty : url[start..]);

        return query.TryGetValue(name, out StringValues value) ? value.ToString() : null;
    }
}
