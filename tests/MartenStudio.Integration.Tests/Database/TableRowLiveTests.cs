using System.Globalization;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Query;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// The row reader against the sample's real relational demo: keyset walks over composite keys with no
/// duplicate and no gap in every order, ctid and offset paging, filters Postgres types from the column,
/// server-side caps, references both ways, the gate refusing before any row SQL, the <c>SqlConsoleRole</c>
/// privilege sentence, and the audit split.
/// </summary>
public class TableRowLiveTests(TableRowLiveTests.Fixture fixture) : IClassFixture<TableRowLiveTests.Fixture>
{
    private static readonly string[] QuartzKey = ["sched_name", "trigger_name", "trigger_group"];

    /// <summary>This class's database.</summary>
    public sealed class Fixture(PostgresFixture postgres) : TableRowDemoFixture(postgres)
    {
        /// <inheritdoc />
        protected override string DatabaseName => "table_rows_live";
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // Keyset walks
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 2: <c>quartz.qrtz_triggers</c> in pages of five, by the key and by two nullable columns in
    /// both directions - every walk is exactly the order a single full read gives, so no row is repeated and
    /// none is missed.
    /// </summary>
    [PostgresFact]
    public async Task Quartz_triggers_walk_in_pages_of_five_with_no_duplicate_and_no_gap_in_every_order()
    {
        await using RowsHost host = fixture.Host();

        (await fixture.ScalarAsync("select count(*) from quartz.qrtz_triggers where next_fire_time is null"))
            .Should().BeGreaterThan(0, "a nullable sort with no NULLs in it would not test the second keyset text");
        (await fixture.ScalarAsync("select count(*) from quartz.qrtz_triggers where calendar_name is null"))
            .Should().BeGreaterThan(0);
        (await fixture.ScalarAsync("select count(*) from quartz.qrtz_triggers where calendar_name is not null"))
            .Should().BeGreaterThan(0);

        (string? Sort, SortDirection Direction, string Order)[] orders =
        [
            (null, SortDirection.Ascending, "sched_name, trigger_name, trigger_group"),
            (null, SortDirection.Descending, "sched_name desc, trigger_name desc, trigger_group desc"),
            ("next_fire_time", SortDirection.Ascending, "next_fire_time asc nulls last, sched_name, trigger_name, trigger_group"),
            ("next_fire_time", SortDirection.Descending, "next_fire_time desc nulls last, sched_name, trigger_name, trigger_group"),
            ("calendar_name", SortDirection.Ascending, "calendar_name asc nulls last, sched_name, trigger_name, trigger_group"),
            ("calendar_name", SortDirection.Descending, "calendar_name desc nulls last, sched_name, trigger_name, trigger_group"),
        ];

        long total = await fixture.ScalarAsync("select count(*) from quartz.qrtz_triggers");
        total.Should().BeGreaterThan(10);

        foreach ((string? sort, SortDirection direction, string order) in orders)
        {
            List<string> expected = [.. (await fixture.RowsAsync(
                    "select sched_name, trigger_name, trigger_group from quartz.qrtz_triggers order by " + order))
                .Select(static x => string.Join('|', x))];

            List<TableRowPage> pages = await host.WalkAsync(
                "quartz",
                "qrtz_triggers",
                new TableRowRequest { PageSize = 5, SortColumn = sort, Direction = direction });

            pages.Should().OnlyContain(x => x.State == TableRowPageState.Loaded, pages[^1].Reason ?? pages[^1].Error?.Sentence);
            pages.Should().OnlyContain(x => x.Paging!.Mode == TableRowPagingMode.Key);
            pages.Should().HaveCount((int) Math.Ceiling(total / 5.0), sort + " " + direction);

            List<string> walked = [.. pages.SelectMany(static x => x.Rows).Select(static x => string.Join('|', QuartzKey.Select(k => x.Key![k])))];

            walked.Should().OnlyHaveUniqueItems(sort + " " + direction);
            walked.Should().Equal(expected, "the walk " + sort + " " + direction + " is the full read's order, row for row");
        }
    }

    /// <summary>
    /// Acceptance 2: the keyless <c>legacy.audit_log</c> pages on ctid, and its exact duplicate row - which no
    /// key could tell apart - appears twice.
    /// </summary>
    [PostgresFact]
    public async Task The_keyless_audit_log_pages_by_ctid_and_its_duplicate_row_appears_twice()
    {
        await using RowsHost host = fixture.Host();

        List<TableRowPage> pages = await host.WalkAsync("legacy", "audit_log", new TableRowRequest { PageSize = 40 });

        pages.Should().OnlyContain(static x => x.State == TableRowPageState.Loaded && x.Paging!.Mode == TableRowPagingMode.Ctid);

        List<TableRow> rows = [.. pages.SelectMany(static x => x.Rows)];

        rows.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.audit_log"));
        rows.Select(static x => x.Locator).Should().OnlyHaveUniqueItems().And.NotContainNulls();
        rows.Should().OnlyContain(static x => x.Key == null);

        rows.GroupBy(static x => string.Join('\u001f', x.Cells.Select(static c => c.Kind + ":" + c.Text)))
            .Where(static x => x.Count() > 1)
            .Should().ContainSingle("the demo writes one entry twice").Which.Should().HaveCount(2);

        // A row with no key still has its cells expanded, by the ctid the page handed back.
        TableRow first = rows[0];
        int actor = pages[0].Columns.Select(static (x, i) => (x.Name, i)).Single(static x => x.Name == "actor").i;

        TableCellValue cell = await host.RowsAsync(x => x.GetCellAsync(
            RowsHost.Scope, "legacy", "audit_log", new Dictionary<string, string> { ["ctid"] = first.Locator! }, "actor", Token));

        cell.Found.Should().BeTrue(cell.Reason ?? cell.Error?.Sentence);
        cell.Text.Should().Be(first.Cells[actor].Text);
        cell.Sql.Should().Contain("where t.ctid = @c");
    }

    /// <summary>
    /// Above <c>ExactCountThreshold</c>, a filter or sort no index serves is withheld until "Run anyway" -
    /// the documents list's rule - with the SQL there to read and nothing run.
    /// </summary>
    [PostgresFact]
    public async Task A_large_relation_with_an_unindexed_filter_or_sort_waits_for_run_anyway()
    {
        // reltuples, which the threshold is compared with, is only there once Postgres has analysed the table.
        await fixture.ExecuteAsync("analyze legacy.audit_log");

        await using RowsHost host = fixture.Host(static options => options.ExactCountThreshold = 10);

        using (var log = new StatementLog())
        {
            TableRowPage withheld = await host.RowsAsync(x => x.ListRowsAsync(
                RowsHost.Scope, "legacy", "audit_log", new TableRowRequest { Filter = "details ~ changed" }, Token));

            withheld.State.Should().Be(TableRowPageState.Withheld, withheld.Error?.Sentence ?? withheld.Reason);
            withheld.Reason.Should().Contain("Run anyway").And.Contain("MartenStudioOptions.ExactCountThreshold");
            withheld.Rows.Should().BeEmpty();
            withheld.Sql.Should().Contain("pg_catalog.strpos(");
            withheld.ParameterNames.Should().Contain("@f1");
            log.Statements.Should().NotContain(static x => x.Contains("\"legacy\".\"audit_log\"", StringComparison.Ordinal),
                "a withheld read is not run");
        }

        TableRowPage ran = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope, "legacy", "audit_log", new TableRowRequest { Filter = "details ~ changed", RunAnyway = true }, Token));

        ran.State.Should().Be(TableRowPageState.Loaded, ran.Error?.Sentence);
        ran.Rows.Should().NotBeEmpty();

        (await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "audit_log", new TableRowRequest { SortColumn = "actor" }, Token)))
            .State.Should().Be(TableRowPageState.Withheld, "sorting ten times the threshold by a column no index leads with reads it all");

        (await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "audit_log", new TableRowRequest(), Token)))
            .State.Should().Be(TableRowPageState.Loaded, "the physical order needs no index");
    }

    /// <summary>Acceptance 2: a view has no key and pages by offset, in a deterministic order.</summary>
    [PostgresFact]
    public async Task A_view_pages_by_offset_and_every_row_appears_once()
    {
        await using RowsHost host = fixture.Host();

        List<TableRowPage> pages = await host.WalkAsync("legacy", "active_employees", new TableRowRequest { PageSize = 7 });

        pages.Should().OnlyContain(static x => x.State == TableRowPageState.Loaded);
        pages[0].Paging!.Mode.Should().Be(TableRowPagingMode.Offset);
        pages[0].Paging!.Note.Should().Contain("view");
        pages[0].Sql.Should().Contain("offset @offset");
        pages.Select(static x => x.Paging!.Offset).Should().BeInAscendingOrder();

        int id = pages[0].Columns.Select(static (x, i) => (x.Name, i)).Single(static x => x.Name == "employee_id").i;
        List<string> ids = [.. pages.SelectMany(static x => x.Rows).Select(x => x.Cells[id].Text)];

        ids.Should().OnlyHaveUniqueItems();
        ids.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.active_employees"));
    }

    /// <summary>A partitioned table with a key walks by it across both partitions.</summary>
    [PostgresFact]
    public async Task A_partitioned_table_walks_its_key_across_partitions()
    {
        await using RowsHost host = fixture.Host();

        List<TableRowPage> pages = await host.WalkAsync("legacy", "sensor_readings", new TableRowRequest { PageSize = 25, SortColumn = "read_at" });

        List<TableRow> rows = [.. pages.SelectMany(static x => x.Rows)];

        pages[0].Kind.Should().Be(DatabaseObjectKind.PartitionedTable);
        pages[0].Paging!.Mode.Should().Be(TableRowPagingMode.Key);
        rows.Select(static x => x.Key!["sensor_id"] + "|" + x.Key["read_at"]).Should().OnlyHaveUniqueItems();
        rows.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.sensor_readings"));
    }

    /// <summary>Acceptance 2: the materialized view created <c>WITH NO DATA</c> says it has never been refreshed.</summary>
    [PostgresFact]
    public async Task The_unpopulated_materialized_view_says_it_has_never_been_refreshed()
    {
        await using RowsHost host = fixture.Host();

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "monthly_order_totals", new TableRowRequest(), Token));

        page.State.Should().Be(TableRowPageState.Failed);
        page.Error!.SqlState.Should().Be("55000");
        page.Error.Sentence.Should().Be("This materialized view has never been refreshed.");

        TableRowPage populated = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "stock_by_region", new TableRowRequest(), Token));

        populated.State.Should().Be(TableRowPageState.Loaded, populated.Error?.Sentence);
        populated.Paging!.Mode.Should().Be(TableRowPagingMode.Ctid, "a populated materialized view is a heap with no key");
        populated.Rows.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 3: every value goes to Postgres as untyped text and the column's input function reads it
    /// - an enum label, a domain value, a numeric with leading zeros - and <c>~</c> reads jsonb's text.
    /// </summary>
    [PostgresFact]
    public async Task Filters_compare_through_the_columns_own_type()
    {
        await using RowsHost host = fixture.Host();

        async Task<TableRowPage> Rows(string table, string filter) =>
            await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", table, new TableRowRequest { Filter = filter, PageSize = 500 }, Token));

        TableRowPage approved = await Rows("purchase_orders", "status = approved");
        approved.State.Should().Be(TableRowPageState.Loaded, approved.Error?.Sentence ?? approved.Reason);
        approved.Rows.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.purchase_orders where status = 'approved'"))
            .And.NotBeEmpty();
        approved.Rows.Should().OnlyContain(row => row.Cells[Ordinal(approved, "status")].Text == "approved");

        string email = (await fixture.RowsAsync("select email::text from legacy.employees order by employee_id limit 1"))[0][0]!;
        TableRowPage byEmail = await Rows("employees", RowFilterGrammar.Format("email", RowFilterOperator.Equal, email));
        byEmail.Rows.Should().ContainSingle("a domain over text compares as its base type").Which.Cells[Ordinal(byEmail, "email")].Text.Should().Be(email);

        TableRowPage leadingZero = await Rows("purchase_order_lines", "unit_price = 012.50 line_no = 01");
        leadingZero.State.Should().Be(TableRowPageState.Loaded, leadingZero.Error?.Sentence);
        leadingZero.Rows.Should().Contain(row => row.Key!["order_id"] == "9001" && row.Key["line_no"] == "1",
            "012.50 is 12.50 to numeric's input function, and 01 is 1 to smallint's");
        leadingZero.Verdict.Terms.Select(static x => x.Value).Should().Equal("012.50", "01");

        TableRowPage desadv = await Rows("integration_messages", "payload ~ desadv");
        desadv.Rows.Should().HaveCount((int) await fixture.ScalarAsync(
            "select count(*) from legacy.integration_messages where payload::text ilike '%desadv%'")).And.NotBeEmpty();
        desadv.Verdict.Chips[0].Level.Should().Be(IndexVerdictLevel.Red, "a substring match reads every row");

        TableRowPage noManager = await Rows("employees", "manager_id is:null");
        noManager.Rows.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.employees where manager_id is null"))
            .And.NotBeEmpty();

        TableRowPage terminated = await Rows("employees", "terminated_on is:notnull");
        terminated.Rows.Should().HaveCount((int) await fixture.ScalarAsync("select count(*) from legacy.employees where terminated_on is not null"));

        TableRowPage wrong = await Rows("purchase_order_lines", "quantity = many");
        wrong.State.Should().Be(TableRowPageState.Failed);
        wrong.Error!.SqlState.Should().Be("22P02");
        wrong.Error.Sentence.Should().NotContain("many", "the sentence never repeats a value");
    }

    /// <summary>A comparison on a column with no btree opclass is refused before anything is read.</summary>
    [PostgresFact]
    public async Task A_filter_the_grammar_refuses_reads_nothing()
    {
        await using RowsHost host = fixture.Host();
        using var log = new StatementLog();

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope, "legacy", "warehouses", new TableRowRequest { Filter = "address > x nope = 1" }, Token));

        page.State.Should().Be(TableRowPageState.Invalid);
        page.Verdict.Errors.Should().HaveCount(2);
        log.Statements.Should().NotContain(static x => x.Contains("\"legacy\".\"warehouses\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------
    // Caps
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 4: a 48 KB TOASTed text is cut on the server with its full length reported, and read
    /// whole on demand; a bytea shows 64 bytes and its octet length.
    /// </summary>
    [PostgresFact]
    public async Task Long_values_are_cut_on_the_server_and_read_whole_on_demand()
    {
        await using RowsHost host = fixture.Host();

        string?[] big = (await fixture.RowsAsync(
            "select message_id::text, octet_length(raw_body), char_length(raw_body) from legacy.integration_messages " +
            "where octet_length(raw_body) > 40000 order by 2 desc limit 1"))[0];

        string messageId = big[0]!;
        int bytes = int.Parse(big[1]!, CultureInfo.InvariantCulture);
        int chars = int.Parse(big[2]!, CultureInfo.InvariantCulture);
        bytes.Should().BeGreaterThanOrEqualTo(48 * 1024);

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope,
            "legacy",
            "integration_messages",
            new TableRowRequest { Filter = "message_id = " + messageId, Columns = ["message_id", "raw_body", "attachment"] },
            Token));

        page.State.Should().Be(TableRowPageState.Loaded, page.Error?.Sentence);
        page.Columns.Select(static x => x.Name).Should().Equal("message_id", "raw_body", "attachment");
        page.Sql.Should().Contain("pg_catalog.substring(t.\"raw_body\"::text, 1, @cap)");

        SqlCell body = page.Rows.Should().ContainSingle().Which.Cells[1];
        body.IsTruncated.Should().BeTrue();
        body.FullLength.Should().Be(bytes);
        body.Text.Should().HaveLength(TableRowCaps.List.Text + 1).And.EndWith("…");

        IReadOnlyDictionary<string, string> key = page.Rows[0].Key!;

        TableCellValue whole = await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, "legacy", "integration_messages", key, "raw_body", Token));

        whole.Found.Should().BeTrue(whole.Reason ?? whole.Error?.Sentence);
        whole.Truncated.Should().BeFalse();
        whole.Text.Should().HaveLength(chars);
        whole.FullLength.Should().Be(bytes);

        string?[] binary = (await fixture.RowsAsync(
            "select message_id::text, octet_length(attachment), encode(attachment, 'hex') from legacy.integration_messages " +
            "where octet_length(attachment) > 64 order by message_id limit 1"))[0];

        int length = int.Parse(binary[1]!, CultureInfo.InvariantCulture);

        TableRowPage withBytes = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope,
            "legacy",
            "integration_messages",
            new TableRowRequest { Filter = "message_id = " + binary[0], Columns = ["attachment"] },
            Token));

        SqlCell cell = withBytes.Rows.Should().ContainSingle().Which.Cells[0];
        cell.Kind.Should().Be(SqlCellKind.Binary);
        cell.IsTruncated.Should().BeTrue();
        cell.FullLength.Should().Be(length);
        cell.Text.Should().Be("\\x" + binary[2]![..128] + "… (" + length.ToString(CultureInfo.InvariantCulture) + " bytes)");

        TableCellValue allBytes = await host.RowsAsync(x => x.GetCellAsync(
            RowsHost.Scope, "legacy", "integration_messages", withBytes.Rows[0].Key!, "attachment", Token));

        allBytes.Bytes.Should().HaveCount(length);
        Convert.ToHexStringLower(allBytes.Bytes!).Should().Be(binary[2]);
    }

    // ---------------------------------------------------------------------------------------------------
    // Row detail and references
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 5: a two-column foreign key resolves to the parent row, and the parent row opens by the
    /// key the reference hands back.
    /// </summary>
    [PostgresFact]
    public async Task A_two_column_foreign_key_resolves_to_its_parent_row()
    {
        await using RowsHost host = fixture.Host();

        var key = new Dictionary<string, string> { ["region_code"] = "FI", ["warehouse_no"] = "1", ["sku"] = "SKU-001" };

        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, "legacy", "stock_levels", key, Token));

        references.Found.Should().BeTrue(references.Reason ?? references.Error?.Sentence);

        RowOutboundReference warehouse = references.Outbound.Single(static x => x.Table == "warehouses");
        warehouse.State.Should().Be(RowReferenceState.Present);
        warehouse.Columns.Should().Equal("region_code", "warehouse_no");
        warehouse.TargetsRowKey.Should().BeTrue();
        warehouse.ParentKey.Should().BeEquivalentTo(new Dictionary<string, string> { ["region_code"] = "FI", ["warehouse_no"] = "1" });

        references.Outbound.Single(static x => x.Table == "product_catalogue").State.Should().Be(RowReferenceState.Present);

        TableRowDetail parent = await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, "legacy", "warehouses", warehouse.ParentKey!, Token));

        parent.Found.Should().BeTrue(parent.Reason ?? parent.Error?.Sentence);
        parent.Cells[Array.FindIndex([.. parent.Columns], static c => c.Name == "name")].Text.Should().Be("Helsinki central");
    }

    /// <summary>Acceptance 5: the NOT VALID foreign key's dangling line says its parent is missing.</summary>
    [PostgresFact]
    public async Task The_not_valid_dangling_line_reports_its_parent_missing()
    {
        await using RowsHost host = fixture.Host();

        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(
            RowsHost.Scope, "legacy", "purchase_order_lines",
            new Dictionary<string, string> { ["order_id"] = "9001", ["line_no"] = "1" }, Token));

        RowOutboundReference order = references.Outbound.Single(static x => x.Table == "purchase_orders");
        order.State.Should().Be(RowReferenceState.Missing);
        order.Validated.Should().BeFalse();
        order.Reason.Should().Contain("NOT VALID");
        order.ParentFilter.Should().Be("order_id = 9001");
    }

    /// <summary>Acceptance 5: the triggers pointing at a Quartz job are counted, composite key and all.</summary>
    [PostgresFact]
    public async Task A_quartz_job_counts_the_triggers_that_point_at_it()
    {
        await using RowsHost host = fixture.Host();

        string?[] job = (await fixture.RowsAsync(
            """
            select j.sched_name, j.job_name, j.job_group, count(t.trigger_name)
            from quartz.qrtz_job_details j
            join quartz.qrtz_triggers t on t.sched_name = j.sched_name and t.job_name = j.job_name and t.job_group = j.job_group
            group by 1, 2, 3
            order by 4 desc, 2
            limit 1
            """))[0];

        var key = new Dictionary<string, string> { ["sched_name"] = job[0]!, ["job_name"] = job[1]!, ["job_group"] = job[2]! };

        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, "quartz", "qrtz_job_details", key, Token));

        RowInboundReference triggers = references.Inbound.Single(static x => x.Table == "qrtz_triggers");
        triggers.State.Should().Be(RowInboundState.Counted, triggers.Reason);
        triggers.Count.Should().Be(long.Parse(job[3]!, CultureInfo.InvariantCulture));
        triggers.More.Should().BeFalse();
        triggers.Columns.Should().Equal("sched_name", "job_name", "job_group");

        TableRowPage children = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope, "quartz", "qrtz_triggers", new TableRowRequest { Filter = triggers.ChildFilter, PageSize = 500 }, Token));

        children.Rows.Should().HaveCount((int) triggers.Count!.Value, "the child filter selects exactly the rows counted");
        references.Statements.Should().Contain(static x => x.Contains("select pg_catalog.count(*)", StringComparison.Ordinal));
    }

    /// <summary>
    /// Acceptance 5: a foreign key into a Marten document table becomes a Documents link - the alias and
    /// the id - and nothing reads <c>mt_doc_customer</c> raw, as the statement log shows.
    /// </summary>
    [PostgresFact]
    public async Task A_key_into_a_marten_document_is_a_documents_link_and_is_never_read_raw()
    {
        await using RowsHost host = fixture.Host();

        string customerId = (await fixture.RowsAsync("select customer_id::text from legacy.customer_credit order by 1 limit 1"))[0][0]!;

        using var log = new StatementLog();

        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(
            RowsHost.Scope, "legacy", "customer_credit", new Dictionary<string, string> { ["customer_id"] = customerId }, Token));

        RowOutboundReference customer = references.Outbound.Should().ContainSingle().Which;
        customer.State.Should().Be(RowReferenceState.MartenDocument);
        customer.DocumentAlias.Should().Be("customer");
        customer.DocumentId.Should().Be(customerId);
        customer.Table.Should().Be("mt_doc_customer");

        references.Statements.Should().NotContain(static x => x.Contains("mt_doc_customer", StringComparison.Ordinal));

        log.Statements.Should().NotBeEmpty("the log has to have seen the row read to mean anything");
        log.Statements.Should().Contain(static x => x.Contains("\"legacy\".\"customer_credit\"", StringComparison.Ordinal));
        log.Statements.Should().NotContain(static x => x.Contains("mt_doc_customer", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task A_row_opens_by_its_key_and_a_relation_with_no_key_has_no_row_detail()
    {
        await using RowsHost host = fixture.Host();

        TableRowDetail row = await host.RowsAsync(x => x.GetRowAsync(
            RowsHost.Scope, "legacy", "stock_levels",
            new Dictionary<string, string> { ["REGION_CODE"] = "SE", ["warehouse_no"] = "1", ["sku"] = "SKU-002" }, Token));

        row.Found.Should().BeTrue(row.Reason ?? row.Error?.Sentence);
        row.Key.Should().BeEquivalentTo(new Dictionary<string, string> { ["region_code"] = "SE", ["warehouse_no"] = "1", ["sku"] = "SKU-002" });
        row.Cells.Should().HaveCount(row.Columns.Count);
        row.Sql.Should().Contain("where t.\"region_code\" = @k1");

        (await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, "legacy", "audit_log", new Dictionary<string, string> { ["actor"] = "x" }, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);

        (await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, "legacy", "stock_levels", new Dictionary<string, string> { ["sku"] = "SKU-002" }, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable, "a key is one value per key column");

        TableRowDetail gone = await host.RowsAsync(x => x.GetRowAsync(
            RowsHost.Scope, "legacy", "stock_levels",
            new Dictionary<string, string> { ["region_code"] = "DE", ["warehouse_no"] = "1", ["sku"] = "SKU-001" }, Token));

        gone.Found.Should().BeFalse();
        gone.Refusal.Should().Be(DatabaseRefusal.None);
    }

    // ---------------------------------------------------------------------------------------------------
    // Counts
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task An_exact_count_is_within_the_threshold_bounded_with_a_filter_and_refused_for_a_view()
    {
        await using RowsHost host = fixture.Host();

        TableRowCount all = await host.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", null, Token));
        all.Count.IsEstimate.Should().BeFalse(all.Error?.Sentence);
        all.Count.Value.Should().Be(await fixture.ScalarAsync("select count(*) from legacy.audit_log"));

        TableRowCount system = await host.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", "actor = system", Token));
        system.Count.Value.Should().Be(await fixture.ScalarAsync("select count(*) from legacy.audit_log where actor = 'system'"));
        system.Bounded.Should().BeFalse();
        system.Sql.Should().Contain("limit @cap) as bounded");

        (await host.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", "active_employees", null, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);

        await using RowsHost strict = fixture.Host(static options => options.ExactCountThreshold = 10);

        TableRowCount refused = await strict.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", null, Token));
        refused.Count.IsExactRefused.Should().BeTrue();
        refused.Count.Reason.Should().Contain("ExactCountThreshold");

        TableRowCount bounded = await strict.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", "entity ~ orders", Token));
        bounded.Bounded.Should().BeTrue();
        bounded.Count.Value.Should().Be(10, "more than the threshold, and said as the threshold");
    }

    // ---------------------------------------------------------------------------------------------------
    // The gate
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 6: every gate refuses every method before any row SQL runs - the statement log holds no
    /// statement that names the relation - as a value, and audited.
    /// </summary>
    [PostgresFact]
    public async Task Every_gate_refuses_every_method_before_any_row_sql_and_audits_it()
    {
        (string Case, Action<MartenStudioOptions>? Configure, ResourcePolicy? Policy, string Schema, string Table, DatabaseRefusal Refusal)[] cases =
        [
            ("capability off", static o => o.Capabilities.BrowseDatabase = false, null, "legacy", "departments", DatabaseRefusal.CapabilityOff),
            ("read-only", static o => o.ReadOnly = true, null, "legacy", "departments", DatabaseRefusal.ReadOnly),
            ("write policy", static o =>
            {
                o.StoreAuthorizationPolicy = "store";
                o.WriteAuthorizationPolicy = "write";
            }, new ResourcePolicy(static r => r.Capability is null), "legacy", "departments", DatabaseRefusal.WritePolicy),
            ("schema not browsable", null, null, "studio_sample", "app_settings", DatabaseRefusal.SchemaNotBrowsable),
            ("a Marten document table", static o => o.BrowsableSchemas.Add("studio_sample"), null, "studio_sample", "mt_doc_customer", DatabaseRefusal.MartenOwned),
            ("a foreign table", null, null, "legacy", TableRowDemoFixture.ForeignTable, DatabaseRefusal.ForeignTable),
            ("a view over a hidden type", static o => o.IsDocumentTypeVisible = static t => t != typeof(Customer), null, "legacy", TableRowDemoFixture.CustomerView, DatabaseRefusal.HiddenDependency),
        ];

        var key = new Dictionary<string, string> { ["id"] = "1" };

        foreach ((string what, Action<MartenStudioOptions>? configure, ResourcePolicy? policy, string schema, string table, DatabaseRefusal expected) in cases)
        {
            await using RowsHost host = fixture.Host(configure, policy);
            using var log = new StatementLog();

            DatabaseRefusal[] refusals =
            [
                (await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, schema, table, new TableRowRequest { Filter = "id = 1" }, Token))).Refusal,
                (await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, schema, table, key, Token))).Refusal,
                (await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, schema, table, key, Token))).Refusal,
                (await host.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, schema, table, null, Token))).Refusal,
                (await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, schema, table, key, "id", Token))).Refusal,
            ];

            refusals.Should().OnlyContain(x => x == expected, what);

            string quoted = SqlIdentifier.Qualify(schema, table);
            log.Statements.Should().NotContain(x => x.Contains(quoted, StringComparison.Ordinal),
                what + ": nothing that reads the relation may run once a gate has said no");

            List<StudioActionLogEntry> audited = [.. host.Ring.GetLatest().Where(x => x.Target == schema + "." + table)];
            audited.Should().HaveCount(5, what + ": one audited refusal per call");
            audited.Should().OnlyContain(static x => !x.Succeeded && x.TenantId == null, what);
        }
    }

    /// <summary>
    /// Acceptance 6: a visitor with a tenant selected is asked about the database as a whole - every
    /// resource the policy sees has no tenant - because nothing here is filtered by tenant.
    /// </summary>
    [PostgresFact]
    public async Task A_tenant_scoped_visitor_is_asked_about_the_database_with_no_tenant()
    {
        var policy = new ResourcePolicy(static _ => true);

        await using RowsHost host = fixture.Host(
            static options =>
            {
                options.StoreAuthorizationPolicy = "store";
                options.WriteAuthorizationPolicy = "write";
                options.KnownTenantIds.Add("acme");
            },
            policy);

        var acme = new StudioScope("default", string.Empty, "acme");

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(acme, "legacy", "departments", new TableRowRequest(), Token));

        page.State.Should().Be(TableRowPageState.Loaded, page.Reason ?? page.Error?.Sentence);
        policy.Calls.Should().NotBeEmpty();
        policy.Calls.Should().OnlyContain(static x => x.Resource.TenantId == null);
        policy.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.BrowseDatabase));

        host.Ring.GetLatest().Should().OnlyContain(static x => x.TenantId == null);
    }

    /// <summary>
    /// Acceptance 7: <c>SqlConsoleRole</c> set to a role that may select only one column of a table - so
    /// the gate, which asks whether it may select anything, lets it through - and Postgres' 42501 comes back
    /// as the sentence naming <c>SqlConsoleRole</c>.
    /// </summary>
    [PostgresFact]
    public async Task A_privilege_the_SqlConsoleRole_lacks_is_the_sentence_naming_it()
    {
        await using RowsHost host = fixture.Host(options => options.SqlConsoleRole = fixture.Role);

        TableRowPage departments = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "departments", new TableRowRequest(), Token));

        departments.State.Should().Be(TableRowPageState.Failed, departments.Reason);
        departments.Error!.SqlState.Should().Be("42501");
        departments.Error.Sentence.Should().Contain("MartenStudioOptions.SqlConsoleRole").And.Contain(fixture.Role);

        TableRowPage triggers = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "quartz", "qrtz_triggers", new TableRowRequest(), Token));

        triggers.State.Should().Be(TableRowPageState.Loaded, triggers.Error?.Sentence ?? triggers.Reason);
    }

    // ---------------------------------------------------------------------------------------------------
    // Audit
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Acceptance 8: opening a relation's rows and paging three pages is one ring entry; a filter change is
    /// one more; the filter's value is in event 9235 and nowhere in the ring.
    /// </summary>
    [PostgresFact]
    public async Task Opening_and_paging_is_one_entry_a_filter_change_is_one_more_and_the_value_stays_out_of_the_ring()
    {
        var logs = new LogCapture();
        await using RowsHost host = fixture.Host(logs: logs);
        await using AsyncServiceScope circuit = host.Circuit();

        ITableRowService rows = circuit.ServiceProvider.GetRequiredService<ITableRowService>();
        const string target = "legacy.purchase_orders";

        TableRowPage page = await rows.ListRowsAsync(RowsHost.Scope, "legacy", "purchase_orders", new TableRowRequest { PageSize = 5 }, Token);

        for (int turn = 0; turn < 3; turn++)
        {
            page.HasMore.Should().BeTrue();
            page = await rows.ListRowsAsync(
                RowsHost.Scope, "legacy", "purchase_orders", new TableRowRequest { PageSize = 5, Cursor = page.NextCursor }, Token);
            page.State.Should().Be(TableRowPageState.Loaded);
        }

        host.Ring.GetLatest().Where(static x => x.Target == target).Should().ContainSingle()
            .Which.Action.Should().Be(DatabaseAccess.RowsAction);

        // The same first page again is a refresh, not a second opening.
        await rows.ListRowsAsync(RowsHost.Scope, "legacy", "purchase_orders", new TableRowRequest { PageSize = 5 }, Token);
        host.Ring.GetLatest().Where(static x => x.Target == target).Should().ContainSingle();

        TableRowPage filtered = await rows.ListRowsAsync(
            RowsHost.Scope, "legacy", "purchase_orders", new TableRowRequest { PageSize = 5, Filter = "status = approved" }, Token);
        filtered.State.Should().Be(TableRowPageState.Loaded);

        List<StudioActionLogEntry> entries = [.. host.Ring.GetLatest().Where(static x => x.Target == target)];
        entries.Should().HaveCount(2);
        entries[0].Action.Should().Be(TableRowService.FilterAction);
        entries[0].Message.Should().Contain("status");
        entries.Should().OnlyContain(static x => x.Succeeded && x.TenantId == null);
        entries.Select(static x => x.Message + " " + x.Target + " " + x.Action).Should().NotContain(static x => x.Contains("approved", StringComparison.Ordinal),
            "the ring is readable by anyone with the read policy; values go to the log");

        List<StudioLogLine> reads = [.. logs.Lines.Where(static x => x.EventId.Id == 9235)];
        reads.Should().HaveCount(2);
        reads.Should().OnlyContain(static x => x.Level == LogLevel.Information);
        reads[1].Message.Should().Contain("status = approved").And.Contain("row-reader");

        await rows.GetRowAsync(RowsHost.Scope, "legacy", "purchase_orders", filtered.Rows[0].Key!, Token);

        host.Ring.GetLatest()[0].Action.Should().Be(TableRowService.RowAction);
        host.Ring.GetLatest()[0].Message.Should().NotContain(filtered.Rows[0].Key!["order_id"]);
        logs.Lines.Should().Contain(x => x.EventId.Id == 9236 && x.Message.Contains("order_id=" + filtered.Rows[0].Key!["order_id"], StringComparison.Ordinal));

        logs.WarningsOrWorse.Should().BeEmpty("reading rows one was granted is not an anomaly");
    }

    /// <summary>
    /// A failure the page explains in words - here the unrefreshed materialized view - is event 9237 at
    /// Debug, and nothing reaches Warning.
    /// </summary>
    [PostgresFact]
    public async Task An_expected_failure_is_a_debug_line_and_never_a_warning()
    {
        var logs = new LogCapture();
        await using RowsHost host = fixture.Host(logs: logs);

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "monthly_order_totals", new TableRowRequest(), Token));

        page.Error!.SqlState.Should().Be("55000");
        logs.Lines.Should().ContainSingle(static x => x.EventId.Id == 9237).Which.Level.Should().Be(LogLevel.Debug);
        logs.WarningsOrWorse.Should().BeEmpty();
    }

    private static int Ordinal(TableRowPage page, string column) =>
        page.Columns.Select(static (x, i) => (x.Name, i)).Single(x => x.Name == column).i;
}
