using System.Diagnostics;
using System.Text;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Query;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// DB-3-fix against a real Postgres 17, from the security review's probes: every first contact with a
/// relation's rows is audited whatever page it starts on (F1); a foreign table's rows are never reached
/// through a view, a partitioned parent or an inheritance parent (F2); a row's reference checks are bounded
/// (F3); a key map that names two columns at once is a refusal (F4); cells are capped in bytes and a page
/// has a byte budget (F5); and the reference far ends and the awkward-type keyset walks the review probed
/// (F6).
/// </summary>
public class TableRowSecurityLiveTests(TableRowSecurityLiveTests.Fixture fixture) : IClassFixture<TableRowSecurityLiveTests.Fixture>
{
    /// <summary>The demo, and everything these cases need on top of it.</summary>
    public sealed class Fixture(PostgresFixture postgres) : TableRowDemoFixture(postgres)
    {
        /// <inheritdoc />
        protected override string DatabaseName => "table_rows_security";

        /// <inheritdoc />
        protected override async Task InitializeExtrasAsync()
        {
            var builder = new NpgsqlConnectionStringBuilder(ConnectionString);

            await ExecuteAsync(ExtrasSql
                .Replace("{db}", builder.Database, StringComparison.Ordinal)
                .Replace("{user}", builder.Username, StringComparison.Ordinal)
                .Replace("{password}", builder.Password, StringComparison.Ordinal));

            // Its own command, once the rest has committed: the materialized view is filled by postgres_fdw
            // over a connection of its own, which sees only committed tables.
            await ExecuteAsync(
                """
                create materialized view legacy.remote_snapshot as select id, secret from legacy.remote_mirror;
                create view legacy.snapshot_peek as select id, secret from legacy.remote_snapshot;
                """);
        }

        private const string ExtrasSql =
            """
            -- F6: far ends a reference check must never name.
            create schema review_hidden;
            create table review_hidden.dept_notes (id int primary key, department_code text references legacy.departments (department_code));
            insert into review_hidden.dept_notes select 1, (select department_code from legacy.departments order by 1 limit 1);
            create table studio_sample.dept_pins (id int primary key, department_code text references legacy.departments (department_code));
            insert into studio_sample.dept_pins select 1, (select department_code from legacy.departments order by 1 limit 1);
            create table legacy.setting_refs (id int primary key, setting_key text references studio_sample.app_settings (setting_key));
            insert into legacy.setting_refs select 1, (select setting_key from studio_sample.app_settings order by 1 limit 1);

            -- F6: awkward types to walk.
            create table legacy.review_types (
              id int primary key, r real, d double precision, m money, dr daterange, iv interval,
              ts timestamp, tz timestamptz, bc char(4), st legacy.order_status, n numeric);
            insert into legacy.review_types
            select g,
              case when g % 7 = 0 then null else ((g % 5) / 3.0)::real end,
              case when g % 6 = 0 then null else (g % 4) * 0.1 end,
              case when g % 5 = 0 then null else ((g % 3) * 1.1)::numeric::money end,
              case when g % 4 = 0 then null else daterange(date '2024-01-01' + (g % 3), date '2024-02-01' + (g % 2)) end,
              case when g % 8 = 0 then null else make_interval(hours => g % 3, mins => g % 2) end,
              case when g % 9 = 0 then null else timestamp '2024-01-01 10:00:00.123456' + (g % 4) * interval '1 microsecond' end,
              case when g % 10 = 0 then null else timestamptz '2024-01-01 10:00:00.5+00' + (g % 3) * interval '1 hour' end,
              case when g % 3 = 0 then null else repeat(chr(97 + g % 2), 1 + g % 3) end,
              case when g % 11 = 0 then null else (enum_range(null::legacy.order_status))[1 + g % 3] end,
              case when g % 12 = 0 then null else (g % 4)::numeric / 3 end
            from generate_series(1, 60) g;

            -- F2: a remote server that answers - this very database, over postgres_fdw - and every way to reach it.
            create table legacy.remote_source (id int primary key, secret text);
            insert into legacy.remote_source values (42, 'from-the-remote-server');
            create table legacy.remote_parts (id int, region text);
            insert into legacy.remote_parts values (7, 'remote');
            create table legacy.remote_kids (id int, note text);
            insert into legacy.remote_kids values (9, 'a-remote-child');
            alter server ms_rows_remote options (set dbname '{db}', add port '5432');
            create user mapping for current_user server ms_rows_remote options (user '{user}', password '{password}');
            create foreign table legacy.remote_mirror (id int, secret text)
              server ms_rows_remote options (schema_name 'legacy', table_name 'remote_source');
            create view legacy.remote_peek as select id, secret from legacy.remote_mirror;
            create table legacy.mixed_parts (id int, region text) partition by list (region);
            create table legacy.mixed_parts_local partition of legacy.mixed_parts for values in ('local');
            create foreign table legacy.mixed_parts_remote partition of legacy.mixed_parts for values in ('remote')
              server ms_rows_remote options (schema_name 'legacy', table_name 'remote_parts');
            create view legacy.mixed_peek as select id, region from legacy.mixed_parts;
            create table legacy.family (id int, note text);
            create foreign table legacy.family_remote () inherits (legacy.family)
              server ms_rows_remote options (schema_name 'legacy', table_name 'remote_kids');
            create function legacy.remote_secret() returns text language sql stable
              return (select secret from legacy.remote_mirror limit 1);
            create view legacy.remote_via_function as select legacy.remote_secret() as secret;

            -- F2 beside it: a partitioned table one of whose partitions is in a schema nobody here may see.
            create table legacy.split_parts (id int, region text) partition by list (region);
            create table legacy.split_parts_here partition of legacy.split_parts for values in ('here');
            create table review_hidden.split_parts_there partition of legacy.split_parts for values in ('there');
            insert into legacy.split_parts values (1, 'here'), (2, 'there');

            -- F3: a parent, two small children, a large unindexed one and a large indexed one.
            create table legacy.budget_parent (id int primary key);
            insert into legacy.budget_parent values (1), (2);
            create table legacy.budget_a (id int primary key, parent_id int references legacy.budget_parent (id));
            create table legacy.budget_b (id int primary key, parent_id int references legacy.budget_parent (id));
            insert into legacy.budget_a values (1, 1);
            insert into legacy.budget_b values (1, 1);
            create table legacy.budget_big (id int primary key, parent_id int references legacy.budget_parent (id));
            insert into legacy.budget_big select g, 1 from generate_series(1, 50) g;
            create table legacy.budget_indexed (id int primary key, parent_id int references legacy.budget_parent (id));
            create index budget_indexed_parent on legacy.budget_indexed (parent_id);
            insert into legacy.budget_indexed select g, 1 from generate_series(1, 50) g;
            analyze legacy.budget_big;
            analyze legacy.budget_indexed;

            -- F5: wide cells in two-byte characters, and a page of wide rows.
            create table legacy.wide_cells (id int primary key, body text, doc jsonb);
            insert into legacy.wide_cells values (1, repeat('é', 3000), jsonb_build_object('note', repeat('é', 3000)));
            create table legacy.wide_rows (id int primary key, a text, b text, c text);
            insert into legacy.wide_rows select g, repeat('a', 2000), repeat('b', 2000), repeat('c', 2000) from generate_series(1, 500) g;
            """;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // F1: every first contact is audited, whatever page it starts on
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The review's probe, now the opposite way round: a read that starts from a crafted cursor, and one that
    /// starts at an offset - both of which a pasted URL can carry - are each one ring entry and one 9235 line
    /// with the filter, and turning their pages adds neither.
    /// </summary>
    [PostgresFact]
    public async Task A_read_that_starts_from_a_cursor_or_an_offset_is_audited_once_and_its_page_turns_are_not()
    {
        var logs = new LogCapture();
        await using RowsHost host = fixture.Host(logs: logs);
        await using AsyncServiceScope circuit = host.Circuit();
        ITableRowService rows = circuit.ServiceProvider.GetRequiredService<ITableRowService>();

        var viaCursor = new TableRowRequest { Filter = "cost_centre ~ c", PageSize = 2, Cursor = new TableRowCursor(null, false, null, [""]) };
        TableRowPage first = await rows.ListRowsAsync(RowsHost.Scope, "legacy", "departments", viaCursor, Token);

        first.State.Should().Be(TableRowPageState.Loaded, first.Error?.Sentence ?? first.Reason);
        first.Rows.Should().NotBeEmpty();

        StudioActionLogEntry departments = host.Ring.GetLatest().Should().ContainSingle(static x => x.Target == "legacy.departments").Which;
        departments.Action.Should().Be(DatabaseAccess.RowsAction);
        departments.Message.Should().Contain("cost_centre").And.Contain("starting past the first page").And.NotContain("~ c");

        TableRowPage next = first;
        while (next.HasMore)
        {
            next = await rows.ListRowsAsync(RowsHost.Scope, "legacy", "departments", viaCursor with { Cursor = next.NextCursor }, Token);
        }

        host.Ring.GetLatest().Where(static x => x.Target == "legacy.departments").Should().ContainSingle("a page turn is not a new read");

        var viaOffset = new TableRowRequest { Filter = "last_name ~ a", Paging = TableRowPagingPreference.Offset, Offset = 1, PageSize = 3 };
        TableRowPage offset = await rows.ListRowsAsync(RowsHost.Scope, "legacy", "employees", viaOffset, Token);

        offset.Rows.Should().NotBeEmpty(offset.Error?.Sentence ?? offset.Reason);
        await rows.ListRowsAsync(RowsHost.Scope, "legacy", "employees", viaOffset with { Offset = offset.NextOffset ?? 4 }, Token);

        host.Ring.GetLatest().Where(static x => x.Target == "legacy.employees").Should().ContainSingle();

        List<StudioLogLine> reads = [.. logs.Lines.Where(static x => x.EventId.Id == 9235)];
        reads.Should().HaveCount(2, "one line per first contact, with the filter as typed");
        reads[0].Message.Should().Contain("cost_centre ~ c").And.Contain("legacy.departments");
        reads[1].Message.Should().Contain("last_name ~ a").And.Contain("legacy.employees");
    }

    /// <summary>F1: a filtered count is a read of the rows a filter matches - audited once per filter, with the filter in 9235.</summary>
    [PostgresFact]
    public async Task A_filtered_count_is_audited_once_per_filter_and_an_unfiltered_one_is_not()
    {
        var logs = new LogCapture();
        await using RowsHost host = fixture.Host(logs: logs);
        await using AsyncServiceScope circuit = host.Circuit();
        ITableRowService rows = circuit.ServiceProvider.GetRequiredService<ITableRowService>();

        (await rows.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", null, Token)).Count.IsEstimate.Should().BeFalse();
        host.Ring.GetLatest().Should().BeEmpty("the relation's size is not a read of its rows");

        await rows.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", "actor = system", Token);
        await rows.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", "actor = system", Token);
        await rows.CountExactAsync(RowsHost.Scope, "legacy", "audit_log", "entity ~ orders", Token);

        List<StudioActionLogEntry> counts = [.. host.Ring.GetLatest().Where(static x => x.Target == "legacy.audit_log")];
        counts.Should().HaveCount(2).And.OnlyContain(static x => x.Action == TableRowService.CountAction && x.Succeeded);
        counts.Select(static x => x.Message).Should().NotContain(static x => x.Contains("system", StringComparison.Ordinal),
            "a value never reaches the ring");

        List<StudioLogLine> lines = [.. logs.Lines.Where(static x => x.EventId.Id == 9235)];
        lines.Should().HaveCount(2);
        lines[0].Message.Should().Contain("actor = system");
        lines[1].Message.Should().Contain("entity ~ orders");
    }

    // ---------------------------------------------------------------------------------------------------
    // F2: a foreign table's rows are never reached indirectly
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The review's probe: the remote server answers - the premise, read on a connection of the test's own -
    /// and yet no row of it is read through a view, a partitioned parent, an inheritance parent or a view over
    /// a function with a SQL-standard body; each is refused as a foreign table and audited, and nothing that
    /// reads it runs. A materialized view's rows are stored here, and are read.
    /// </summary>
    [PostgresFact]
    public async Task A_foreign_tables_rows_are_never_read_through_a_view_a_partition_or_an_inheritance_parent()
    {
        (await fixture.RowsAsync("select secret from legacy.remote_peek"))[0][0].Should().Be("from-the-remote-server",
            "the premise: the remote answers, so only the gate stands between a read and it");
        (await fixture.RowsAsync("select region from legacy.mixed_parts where region = 'remote'")).Should().NotBeEmpty();
        (await fixture.RowsAsync("select note from legacy.family where note = 'a-remote-child'")).Should().NotBeEmpty();

        await using RowsHost host = fixture.Host();
        using var log = new StatementLog();

        foreach (string relation in new[] { "remote_mirror", "remote_peek", "mixed_parts", "mixed_peek", "family", "remote_via_function" })
        {
            TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", relation, new TableRowRequest(), Token));

            page.State.Should().Be(TableRowPageState.Refused, relation);
            page.Refusal.Should().Be(DatabaseRefusal.ForeignTable, relation + ": " + page.Reason);
            page.Rows.Should().BeEmpty();

            TableRowCount count = await host.RowsAsync(x => x.CountExactAsync(RowsHost.Scope, "legacy", relation, null, Token));
            count.Refusal.Should().Be(DatabaseRefusal.ForeignTable, relation);

            host.Ring.GetLatest().Should().Contain(x => x.Target == "legacy." + relation && !x.Succeeded, relation + " is audited");
            log.Statements.Should().NotContain(x => x.Contains("\"legacy\".\"" + relation + "\"", StringComparison.Ordinal), relation);
        }

        foreach (string relation in new[] { "remote_snapshot", "snapshot_peek" })
        {
            TableRowPage local = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", relation, new TableRowRequest(), Token));

            local.State.Should().Be(TableRowPageState.Loaded, relation + ": " + (local.Reason ?? local.Error?.Sentence));
            local.Rows.SelectMany(static r => r.Cells).Select(static c => c.Text).Should().Contain("from-the-remote-server",
                relation + " is a materialized view's stored copy, read here and not from the server");
        }

        log.Statements.Should().Contain(static x => x.Contains("\"legacy\".\"remote_snapshot\"", StringComparison.Ordinal),
            "the log saw the reads it was allowed to see");
    }

    [PostgresFact]
    public async Task A_partition_in_a_withheld_schema_withholds_its_parents_rows()
    {
        await using RowsHost host = fixture.Host();

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "split_parts", new TableRowRequest(), Token));

        page.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, page.Reason);
        page.Reason.Should().Be(DatabaseGate.WithheldDescendantDenial).And.NotContain("review_hidden");

        await using RowsHost both = fixture.Host(static options => options.BrowsableSchemas.Add("review_hidden"));

        TableRowPage allowed = await both.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "split_parts", new TableRowRequest(), Token));
        allowed.State.Should().Be(TableRowPageState.Loaded, allowed.Reason ?? allowed.Error?.Sentence);
        allowed.Rows.Should().HaveCount(2);
    }

    // ---------------------------------------------------------------------------------------------------
    // F3: reference checks are bounded
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A pointing table above <c>ExactCountThreshold</c> with no index on its key is not counted - the count
    /// would scan it - and its name is in no statement; an indexed one of the same size is counted.
    /// </summary>
    [PostgresFact]
    public async Task A_large_pointing_table_no_index_serves_is_not_counted()
    {
        await using RowsHost host = fixture.Host(static options => options.ExactCountThreshold = 10);

        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(
            RowsHost.Scope, "legacy", "budget_parent", new Dictionary<string, string> { ["id"] = "1" }, Token));

        references.Found.Should().BeTrue(references.Reason ?? references.Error?.Sentence);

        RowInboundReference big = references.Inbound.Single(static x => x.Table == "budget_big");
        big.State.Should().Be(RowInboundState.NotChecked);
        big.Reason.Should().StartWith("Not counted").And.Contain("ExactCountThreshold").And.Contain("parent_id");
        references.Statements.Should().NotContain(static x => x.Contains("budget_big", StringComparison.Ordinal));

        RowInboundReference indexed = references.Inbound.Single(static x => x.Table == "budget_indexed");
        indexed.State.Should().Be(RowInboundState.Counted, indexed.Reason);
        indexed.Count.Should().Be(50);
    }

    /// <summary>
    /// Each reference check has a statement_timeout of its own - the lesser of QueryTimeout and five seconds -
    /// and all of them one budget: a check held up by somebody's lock stops at its own timeout (57014, not the
    /// three-second lock_timeout's 55P03), and the checks after it are left unchecked rather than queued.
    /// </summary>
    [PostgresFact]
    public async Task A_rows_reference_checks_stop_at_their_budget()
    {
        await using RowsHost host = fixture.Host(static options => options.QueryTimeout = TimeSpan.FromSeconds(1));
        var key = new Dictionary<string, string> { ["id"] = "1" };

        // Warm the catalog, so that the only thing the lock below can hold up is a row read.
        TableRowReferences warm = await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, "legacy", "budget_parent", key, Token));
        warm.Inbound.Single(static x => x.Table == "budget_a").State.Should().Be(RowInboundState.Counted, warm.Error?.Sentence);

        await using NpgsqlConnection holder = await fixture.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);

        await using (var lockTable = new NpgsqlCommand("lock table legacy.budget_a in access exclusive mode", holder, held))
        {
            await lockTable.ExecuteNonQueryAsync(Token);
        }

        var clock = Stopwatch.StartNew();
        TableRowReferences references = await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, "legacy", "budget_parent", key, Token));
        clock.Stop();

        RowInboundReference a = references.Inbound.Single(static x => x.Table == "budget_a");
        a.State.Should().Be(RowInboundState.Failed, a.Reason);
        a.Reason.Should().Contain("each reference check is given").And.Contain("1 s");

        RowInboundReference b = references.Inbound.Single(static x => x.Table == "budget_b");
        b.State.Should().Be(RowInboundState.NotChecked, "the budget was spent on the check before it: " + b.Reason);
        b.Reason.Should().StartWith("Not checked: this row's reference checks had 1 s");

        references.Statements.Should().NotContain(static x => x.Contains("\"legacy\".\"budget_b\"", StringComparison.Ordinal));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "four checks stopped by their budget, not four timeouts queued");

        await held.RollbackAsync(Token);
    }

    // ---------------------------------------------------------------------------------------------------
    // F4
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_key_map_with_two_case_variants_of_one_column_is_a_refusal_and_not_an_exception()
    {
        await using RowsHost host = fixture.Host();

        var key = new Dictionary<string, string> { ["REGION_CODE"] = "SE", ["Region_Code"] = "SE", ["sku"] = "SKU-002" };

        (await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, "legacy", "stock_levels", key, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);
        (await host.RowsAsync(x => x.GetReferencesAsync(RowsHost.Scope, "legacy", "stock_levels", key, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);
        (await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, "legacy", "stock_levels", key, "sku", Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);
    }

    // ---------------------------------------------------------------------------------------------------
    // F5: bytes, and a budget
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_cell_is_capped_in_bytes_and_a_json_value_is_rendered_once()
    {
        await using RowsHost host = fixture.Host(static options => options.MaxInlineDocumentBytes = 1000);
        var key = new Dictionary<string, string> { ["id"] = "1" };

        TableCellValue body = await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, "legacy", "wide_cells", key, "body", Token));

        body.Found.Should().BeTrue(body.Reason ?? body.Error?.Sentence);
        body.Truncated.Should().BeTrue();
        body.Cap.Should().Be(1000);
        Encoding.UTF8.GetByteCount(body.Text!).Should().Be(1000, "five hundred two-byte characters, not a thousand");
        body.Text.Should().Be(new string('é', 500));
        body.FullLength.Should().Be(6000);

        TableCellValue doc = await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, "legacy", "wide_cells", key, "doc", Token));

        doc.Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(doc.Text!).Should().BeLessThanOrEqualTo(1000);
        doc.Text.Should().StartWith("{\"note\": \"é");
        doc.Sql.Should().Contain("cross join lateral (select t.\"doc\"::text as v offset 0) as j1");
    }

    [PostgresFact]
    public async Task A_page_past_its_byte_budget_cuts_every_further_cell_short_and_says_so()
    {
        await using RowsHost host = fixture.Host();

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "wide_rows", new TableRowRequest { PageSize = 400 }, Token));

        page.State.Should().Be(TableRowPageState.Loaded, page.Reason ?? page.Error?.Sentence);
        page.Rows.Should().HaveCount(400);
        page.Shortened.Should().BeTrue("400 rows of three kilobyte cells is more than a mebibyte");

        int a = page.Columns.Select(static (x, i) => (x.Name, i)).Single(static x => x.Name == "a").i;

        page.Rows[0].Cells[a].Text.Should().HaveLength(TableRowCaps.List.Text + 1, "the first rows are cut at the cell cap");
        page.Rows[^1].Cells[a].Text.Should().Be(new string('a', TableRowCaps.List.OverBudget) + "…", "past the budget, a cell keeps 64 bytes");
        page.Rows[^1].Cells[a].IsTruncated.Should().BeTrue();
        page.Rows[^1].Cells[a].FullLength.Should().Be(2000, "and still says how long the value is");

        // Without the budget the page would hold 400 x 3 x 1,027 bytes - about 1.23 MB.
        long kept = page.Rows.SelectMany(static r => r.Cells).Sum(static c => (long) Encoding.UTF8.GetByteCount(c.Text));
        kept.Should().BeLessThan(TableRowCaps.List.Budget + (64 * 1024), "the budget bounds the page: past it, every cell is 67 bytes");

        TableRowPage small = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "wide_rows", new TableRowRequest { PageSize = 50 }, Token));
        small.Shortened.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------------
    // F6: the review's other probes, as regressions
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// An outbound key into a store-schema table outside BrowsableSchemas is not checked; an inbound key
    /// from a withheld schema is absent; an inbound key from a store-schema table is not counted - and none
    /// of those tables is named in any statement the studio ran.
    /// </summary>
    [PostgresFact]
    public async Task Reference_far_ends_outside_the_browsable_schemas_are_never_read()
    {
        await using RowsHost host = fixture.Host();

        string department = (await fixture.RowsAsync("select department_code from legacy.departments order by 1 limit 1"))[0][0]!;

        using var log = new StatementLog();

        TableRowReferences departmentReferences = await host.RowsAsync(x => x.GetReferencesAsync(
            RowsHost.Scope, "legacy", "departments", new Dictionary<string, string> { ["department_code"] = department }, Token));
        TableRowReferences settingReferences = await host.RowsAsync(x => x.GetReferencesAsync(
            RowsHost.Scope, "legacy", "setting_refs", new Dictionary<string, string> { ["id"] = "1" }, Token));

        log.Statements.Should().Contain(static x => x.Contains("\"legacy\".\"setting_refs\"", StringComparison.Ordinal), "the log saw the row reads");
        log.Statements.Should().Contain(static x => x.Contains("\"legacy\".\"employees\"", StringComparison.Ordinal), "and the counted inbound key");

        foreach (string never in new[] { "app_settings", "dept_pins", "dept_notes", "review_hidden" })
        {
            log.Statements.Should().NotContain(x => x.Contains(never, StringComparison.Ordinal), never);
        }

        departmentReferences.Inbound.Should().NotContain(static x => x.Table == "dept_notes", "a withheld schema is never named");
        departmentReferences.Inbound.Single(static x => x.Table == "dept_pins").State.Should().Be(RowInboundState.NotChecked);
        settingReferences.Outbound.Should().ContainSingle().Which.State.Should().Be(RowReferenceState.NotChecked);
    }

    /// <summary>
    /// A keyset walk over real, float, money, daterange, interval, timestamp, timestamptz, bpchar, an enum and
    /// numeric - with NULLs - in both directions is exactly the full read's order, and a filter on each finds
    /// what Postgres' own input function says it should.
    /// </summary>
    [PostgresFact]
    public async Task Keyset_walks_over_awkward_types_have_no_duplicate_and_no_gap()
    {
        await using RowsHost host = fixture.Host();

        foreach (string column in new[] { "r", "d", "m", "dr", "iv", "ts", "tz", "bc", "st", "n" })
        {
            foreach (SortDirection direction in new[] { SortDirection.Ascending, SortDirection.Descending })
            {
                string order = direction == SortDirection.Descending ? "desc" : "asc";
                List<string> expected = [.. (await fixture.RowsAsync(
                        "select t.id::text from legacy.review_types t order by t." + column + " " + order + " nulls last, t.id"))
                    .Select(static x => x[0]!)];

                List<TableRowPage> pages = await host.WalkAsync(
                    "legacy", "review_types", new TableRowRequest { PageSize = 4, SortColumn = column, Direction = direction });

                pages.Should().OnlyContain(static x => x.State == TableRowPageState.Loaded,
                    column + " " + order + ": " + pages[^1].Error?.SqlState + " " + pages[^1].Error?.PostgresMessage + " " + pages[^1].Reason);

                List<string> walked = [.. pages.SelectMany(static x => x.Rows).Select(static x => x.Key!["id"])];
                walked.Should().Equal(expected, column + " " + order);
            }
        }

        foreach (string filter in new[]
                 {
                     "dr = \"[2024-01-02,2024-02-02)\"", "iv >= 01:00:00", "ts > \"2024-01-01 10:00:00.123457\"",
                     "tz < \"2024-01-01 11:30:00+00\"", "bc = \"aa  \"", "st = draft", "m > 1.00", "r = 0.33333334",
                 })
        {
            TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(
                RowsHost.Scope, "legacy", "review_types", new TableRowRequest { Filter = filter, PageSize = 500 }, Token));

            page.State.Should().Be(TableRowPageState.Loaded, filter + ": " + page.Error?.SqlState + " " + page.Error?.PostgresMessage + " " + page.Reason);
            page.Rows.Should().NotBeEmpty(filter);
        }
    }

    /// <summary>Injection attempts in a filter, a cursor, a sort, a key or a column name are values, or refusals.</summary>
    [PostgresFact]
    public async Task Injection_attempts_are_values_and_names_never_reach_sql()
    {
        await using RowsHost host = fixture.Host();

        TableRowPage quoteFilter = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "departments",
            new TableRowRequest { Filter = "name = \"x' or '1'='1\" department_code = \"a'); drop table legacy.departments; --\"" }, Token));
        quoteFilter.State.Should().Be(TableRowPageState.Loaded, quoteFilter.Error?.Sentence ?? quoteFilter.Reason);
        quoteFilter.Rows.Should().BeEmpty();

        TableRowPage cursorInject = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "departments",
            new TableRowRequest { Cursor = new TableRowCursor(null, false, null, ["zzz') or (select pg_sleep(5)) is null --"]) }, Token));
        cursorInject.State.Should().Be(TableRowPageState.Loaded);
        cursorInject.Rows.Should().BeEmpty();

        (await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, "legacy", "departments",
                new TableRowRequest { SortColumn = "name\" desc, (select 1) --", Cursor = new TableRowCursor("name\" desc, (select 1) --", false, "a", ["a"]) }, Token)))
            .State.Should().Be(TableRowPageState.Invalid);

        (await host.RowsAsync(x => x.GetRowAsync(RowsHost.Scope, "legacy", "departments",
                new Dictionary<string, string> { ["department_code\" = '' or \"1"] = "x" }, Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotApplicable);

        string department = (await fixture.RowsAsync("select department_code from legacy.departments order by 1 limit 1"))[0][0]!;

        (await host.RowsAsync(x => x.GetCellAsync(RowsHost.Scope, "legacy", "departments",
                new Dictionary<string, string> { ["department_code"] = department }, "name\" from legacy.employees --", Token)))
            .Refusal.Should().Be(DatabaseRefusal.NotFound);

        (await fixture.ScalarAsync("select count(*) from legacy.departments")).Should().BeGreaterThan(0);
    }

    /// <summary>Under <c>"*"</c>, a system catalog, a Marten table and Marten's bookkeeping are still never read.</summary>
    [PostgresFact]
    public async Task A_star_list_never_reaches_system_schemas_or_marten_tables()
    {
        await using RowsHost host = fixture.Host(static options => options.BrowsableSchemas.Add("*"));

        foreach ((string schema, string name) in new[]
                 {
                     ("pg_catalog", "pg_authid"), ("pg_catalog", "pg_shadow"), ("information_schema", "tables"),
                     ("studio_sample", "mt_doc_customer"), ("studio_sample", "mt_events"), ("studio_sample", "mt_hilo"),
                 })
        {
            TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(RowsHost.Scope, schema, name, new TableRowRequest(), Token));

            page.Refusal.Should().NotBe(DatabaseRefusal.None, schema + "." + name);
            page.Rows.Should().BeEmpty();
        }
    }
}
