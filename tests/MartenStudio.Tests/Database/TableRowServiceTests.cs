using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The row reader's decisions that need no database: how a relation pages, whether a cursor belongs to
/// the walk, how a cut cell reads, what each SQLSTATE says, when a read waits for "Run anyway", and the
/// cursor's own encoding.
/// </summary>
public class TableRowServiceTests
{
    // ---------------------------------------------------------------------------------------------------
    // Paging plans
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_keyed_table_pages_by_its_key()
    {
        TableRowPlan plan = TableRowService.Plan(
            TableRowTestData.Relation(), TableRowTestData.QuartzColumns, TableRowTestData.QuartzKey,
            TableRowPagingPreference.Keyset, "next_fire_time", descending: false, tidRange: true);

        plan.Mode.Should().Be(TableRowPagingMode.Key);
        plan.KeyColumns.Should().Equal("sched_name", "trigger_name", "trigger_group");
        plan.SelectLocator.Should().BeFalse();
        plan.SortColumn.Should().Be("next_fire_time");
        plan.Note.Should().BeNull();
    }

    [Fact]
    public void A_keyless_heap_pages_by_ctid_on_14_and_by_offset_before()
    {
        IReadOnlyList<DatabaseColumnInfo> columns = [TableRowTestData.Column("actor"), TableRowTestData.Column("logged_at", "timestamp with time zone", 2)];

        TableRowPlan current = TableRowService.Plan(
            TableRowTestData.Relation(), columns, null, TableRowPagingPreference.Keyset, null, false, tidRange: true);

        current.Mode.Should().Be(TableRowPagingMode.Ctid);
        current.SelectLocator.Should().BeTrue();
        current.Describe(0).OrderColumns.Should().Equal("ctid");

        TableRowPlan thirteen = TableRowService.Plan(
            TableRowTestData.Relation(), columns, null, TableRowPagingPreference.Keyset, null, false, tidRange: false);

        thirteen.Mode.Should().Be(TableRowPagingMode.Offset);
        thirteen.SelectLocator.Should().BeTrue("the rows still carry their ctid, for expanding a cell");
        thirteen.Note.Should().Contain("before 14");
        thirteen.Describe(0).OrderColumns.Should().Equal("ctid");

        TableRowService.Plan(TableRowTestData.Relation("m"), columns, null, TableRowPagingPreference.Keyset, null, false, tidRange: true)
            .Mode.Should().Be(TableRowPagingMode.Ctid, "a materialized view is a heap too");
    }

    [Fact]
    public void A_view_and_a_keyless_partitioned_table_page_by_offset_in_a_deterministic_order()
    {
        IReadOnlyList<DatabaseColumnInfo> columns =
        [
            TableRowTestData.Column("payload", "json", 1, sortable: false),
            TableRowTestData.Column("name", "text", 2),
            TableRowTestData.Column("id", "integer", 3),
        ];

        TableRowPlan view = TableRowService.Plan(
            TableRowTestData.Relation("v", estimatedRows: null), columns, null, TableRowPagingPreference.Keyset, null, false, tidRange: true);

        view.Mode.Should().Be(TableRowPagingMode.Offset);
        view.SelectLocator.Should().BeFalse("a view has no ctid of its own");
        view.TieBreakColumns.Should().Equal("name", "id");
        view.Note.Should().Contain("view");

        TableRowPlan partitioned = TableRowService.Plan(
            TableRowTestData.Relation("p"), columns, null, TableRowPagingPreference.Keyset, null, false, tidRange: true);

        partitioned.Mode.Should().Be(TableRowPagingMode.Offset);
        partitioned.Note.Should().Contain("partition");

        TableRowService.Plan(
                TableRowTestData.Relation("v", estimatedRows: null), [columns[0]], null, TableRowPagingPreference.Keyset, null, false, tidRange: true)
            .Note.Should().Contain("may repeat or skip");
    }

    [Fact]
    public void Offset_is_used_when_asked_and_a_sort_by_the_single_key_column_is_the_key_walk()
    {
        TableRowService.Plan(
                TableRowTestData.Relation(), TableRowTestData.QuartzColumns, TableRowTestData.QuartzKey,
                TableRowPagingPreference.Offset, null, false, tidRange: true)
            .Mode.Should().Be(TableRowPagingMode.Offset);

        var key = new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "pk", ["id"]);

        TableRowPlan plan = TableRowService.Plan(
            TableRowTestData.Relation(), [TableRowTestData.Column("id", "integer")], key,
            TableRowPagingPreference.Keyset, "id", descending: true, tidRange: true);

        plan.Mode.Should().Be(TableRowPagingMode.Key);
        plan.SortColumn.Should().BeNull();
        plan.Descending.Should().BeTrue();
    }

    [Fact]
    public void A_key_whose_names_cannot_be_quoted_is_no_key()
    {
        var key = new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "pk", ["bad\"id"]);

        TableRowService.Plan(
                TableRowTestData.Relation(), [TableRowTestData.Column("bad\"id", quotable: false)], key,
                TableRowPagingPreference.Keyset, null, false, tidRange: true)
            .Mode.Should().Be(TableRowPagingMode.Ctid);
    }

    [Fact]
    public void A_cursor_is_accepted_only_by_the_walk_it_came_from()
    {
        TableRowPlan plan = TableRowService.Plan(
            TableRowTestData.Relation(), TableRowTestData.QuartzColumns, TableRowTestData.QuartzKey,
            TableRowPagingPreference.Keyset, "next_fire_time", descending: false, tidRange: true);

        plan.Accepts(null).Should().BeTrue();
        plan.Accepts(new TableRowCursor("next_fire_time", false, "1", ["a", "b", "c"])).Should().BeTrue();
        plan.Accepts(new TableRowCursor("next_fire_time", false, null, ["a", "b", "c"])).Should().BeTrue();
        plan.Accepts(new TableRowCursor("next_fire_time", true, "1", ["a", "b", "c"])).Should().BeFalse();
        plan.Accepts(new TableRowCursor(null, false, null, ["a", "b", "c"])).Should().BeFalse();
        plan.Accepts(new TableRowCursor("next_fire_time", false, "1", ["a", "b"])).Should().BeFalse();
        plan.Accepts(new TableRowCursor("next_fire_time", false, "1", ["(0,1)"], IsCtid: true)).Should().BeFalse();
    }

    [Fact]
    public void The_next_cursor_is_the_last_rows_raw_key_and_sort_value()
    {
        TableRowPlan plan = TableRowService.Plan(
            TableRowTestData.Relation(), TableRowTestData.QuartzColumns, TableRowTestData.QuartzKey,
            TableRowPagingPreference.Keyset, "next_fire_time", descending: true, tidRange: true);

        var last = new TableRow(
            new Dictionary<string, string> { ["sched_name"] = "s", ["trigger_name"] = "t", ["trigger_group"] = "g" },
            null,
            []);

        TableRowCursor cursor = plan.CursorAfter(last, "638000000000000000", descending: true);

        cursor.Should().BeEquivalentTo(new TableRowCursor("next_fire_time", true, "638000000000000000", ["s", "t", "g"]));
        plan.Accepts(cursor).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------------
    // Cells
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_cell_one_character_past_its_cap_was_cut_and_one_at_the_cap_was_not()
    {
        TableRowService.CutToCodePoints("abcd", 4).Should().Be(("abcd", false));
        TableRowService.CutToCodePoints("abcde", 4).Should().Be(("abcd", true));
        TableRowService.CutToCodePoints(string.Empty, 4).Should().Be((string.Empty, false));

        // Two code points of four UTF-16 units each side of the cut: a surrogate pair is never split.
        const string emoji = "😀😁😂";
        TableRowService.CutToCodePoints(emoji, 2).Should().Be(("😀😁", true));
        TableRowService.CutToCodePoints(emoji, 3).Should().Be((emoji, false));
    }

    [Fact]
    public void A_cut_text_cell_says_so_and_keeps_the_full_size()
    {
        SqlCell cell = TableRowService.TextCell(new string('x', 1025), 49_152, 1024, SqlCellKind.Text);

        cell.IsTruncated.Should().BeTrue();
        cell.Text.Should().HaveLength(1025).And.EndWith("…");
        cell.FullLength.Should().Be(49_152);

        SqlCell whole = TableRowService.TextCell("{\"a\":1}", 7, 4096, SqlCellKind.Json);

        whole.Should().Be(new SqlCell("{\"a\":1}", SqlCellKind.Json, false, 7));
    }

    [Fact]
    public void A_binary_cell_is_its_first_bytes_as_hex_and_its_octet_length()
    {
        byte[] prefix = [.. Enumerable.Range(0, 64).Select(static x => (byte) x)];

        SqlCell cut = TableRowService.BinaryCell(prefix, 920, 64);

        cut.Kind.Should().Be(SqlCellKind.Binary);
        cut.IsTruncated.Should().BeTrue();
        cut.FullLength.Should().Be(920);
        cut.Text.Should().StartWith("\\x000102").And.EndWith("3f… (920 bytes)");

        TableRowService.BinaryCell([0x7b, 0x7d], 2, 64).Should().Be(new SqlCell("\\x7b7d (2 bytes)", SqlCellKind.Binary, false, 2));
    }

    // ---------------------------------------------------------------------------------------------------
    // What Postgres' refusals say
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_unpopulated_materialized_view_says_it_has_never_been_refreshed()
    {
        TableRowError error = TableRowService.Describe(
            Postgres("55000", "materialized view \"monthly_order_totals\" has not been populated"),
            TableRowTestData.Relation("m"),
            new MartenStudioOptions());

        error.SqlState.Should().Be("55000");
        error.Sentence.Should().Be("This materialized view has never been refreshed.");
        error.PostgresMessage.Should().Contain("has not been populated");

        TableRowService.Describe(Postgres("55000", "x"), TableRowTestData.Relation("r"), new MartenStudioOptions())
            .Sentence.Should().NotContain("materialized view");
    }

    [Fact]
    public void A_missing_privilege_names_SqlConsoleRole_when_one_is_set()
    {
        var options = new MartenStudioOptions { SqlConsoleRole = "studio_reader" };

        TableRowService.Describe(Postgres("42501", "permission denied for table departments"), TableRowTestData.Relation(), options)
            .Sentence.Should().Contain("'studio_reader'").And.Contain("MartenStudioOptions.SqlConsoleRole");

        TableRowService.Describe(Postgres("42501", "permission denied"), TableRowTestData.Relation(), new MartenStudioOptions())
            .Sentence.Should().Contain("store's Postgres role").And.NotContain("SqlConsoleRole");
    }

    [Fact]
    public void A_timeout_names_QueryTimeout_and_a_view_says_it_runs_its_query_per_page()
    {
        var options = new MartenStudioOptions { QueryTimeout = TimeSpan.FromSeconds(12) };

        TableRowService.Describe(Postgres("57014", "canceling statement due to statement timeout"), TableRowTestData.Relation(), options)
            .Sentence.Should().Contain("MartenStudioOptions.QueryTimeout (12 s)");

        TableRowService.Describe(Postgres("57014", "canceling"), TableRowTestData.Relation("v"), options)
            .Sentence.Should().Contain("A view runs its whole query for every page.");
    }

    [Fact]
    public void An_undefined_object_on_a_table_with_row_level_security_says_so()
    {
        TableRowService.Describe(
                Postgres("42704", "unrecognized configuration parameter \"app.tenant\""),
                TableRowTestData.Relation(rowSecurity: true),
                new MartenStudioOptions())
            .Sentence.Should().Contain("Row-level security is on");

        TableRowService.Describe(Postgres("42704", "x"), TableRowTestData.Relation(), new MartenStudioOptions())
            .Sentence.Should().NotContain("Row-level security");
    }

    [Fact]
    public void A_value_that_does_not_fit_and_a_lost_connection_are_values_too()
    {
        TableRowService.Describe(Postgres("22P02", "invalid input syntax for type integer: \"abc\""), TableRowTestData.Relation(), new MartenStudioOptions())
            .Sentence.Should().Be("A filter or key value does not fit its column's type.", "the sentence never repeats the value");

        TableRowError lost = TableRowService.Describe(new NpgsqlException("Exception while reading from stream"), null, new MartenStudioOptions());
        lost.SqlState.Should().BeEmpty();
        lost.Sentence.Should().Be("The database could not be reached.");
    }

    [Theory]
    [InlineData("57014", true)]
    [InlineData("55000", true)]
    [InlineData("42501", true)]
    [InlineData("42704", true)]
    [InlineData("55P03", true)]
    [InlineData("22P02", true)]
    [InlineData("22008", true)]
    [InlineData("XX000", false)]
    [InlineData("53300", false)]
    [InlineData("", false)]
    public void Only_the_states_the_page_explains_are_expected(string sqlState, bool expected) =>
        TableRowService.IsExpected(sqlState, new InvalidOperationException()).Should().Be(expected);

    // ---------------------------------------------------------------------------------------------------
    // Verdicts and "Run anyway"
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_column_is_green_when_a_valid_index_leads_with_it_and_red_when_none_covers_it()
    {
        CatalogRelationDetail detail = TableRowTestData.Detail(
            TableRowTestData.Relation(),
            TableRowTestData.Index("idx_state", "sched_name", "trigger_state"),
            TableRowTestData.Index("idx_next", "next_fire_time"),
            new CatalogIndex("idx_partial", "create index idx_partial", false, false, true, true, false, ["priority"]),
            new CatalogIndex("idx_broken", "create index idx_broken", false, false, false, false, false, ["calendar_name"]));

        TableRowService.IndexVerdict(detail, "next_fire_time").Level.Should().Be(IndexVerdictLevel.Green);
        TableRowService.IndexVerdict(detail, "sched_name").Level.Should().Be(IndexVerdictLevel.Green);
        TableRowService.IndexVerdict(detail, "trigger_state").Should().Be(
            (IndexVerdictLevel.Amber, "trigger_state is in the index idx_state, but does not lead it."));
        TableRowService.IndexVerdict(detail, "priority").Level.Should().Be(IndexVerdictLevel.Amber);
        TableRowService.IndexVerdict(detail, "calendar_name").Level.Should().Be(IndexVerdictLevel.Red, "an invalid index serves nothing");
        TableRowService.IndexVerdict(detail, "description").Level.Should().Be(IndexVerdictLevel.Red);
        TableRowService.IndexVerdict(TableRowTestData.Detail(TableRowTestData.Relation("v")), "anything").Level
            .Should().Be(IndexVerdictLevel.Amber);
    }

    [Fact]
    public void The_verdict_has_a_chip_per_term_per_error_and_the_sort_last()
    {
        CatalogRelationDetail detail = TableRowTestData.Detail(TableRowTestData.Relation(), TableRowTestData.Index("idx_next", "next_fire_time"));

        RowFilterParse parse = RowFilterGrammar.Parse(
            "next_fire_time > 5 sched_name ~ nightly nope = 1",
            TableRowTestData.QuartzColumns);

        RowFilterVerdict verdict = TableRowService.BuildVerdict(detail, TableRowTestData.QuartzKey, parse, null, TableRowPagingMode.Key);

        verdict.Chips.Select(static x => (x.Kind, x.Level)).Should().Equal(
            (SearchChipKind.Predicate, IndexVerdictLevel.Green),
            (SearchChipKind.Predicate, IndexVerdictLevel.Red),
            (SearchChipKind.Error, IndexVerdictLevel.Red),
            (SearchChipKind.Sort, IndexVerdictLevel.Green));
        verdict.FilterLevel.Should().Be(IndexVerdictLevel.Red);
        verdict.SortLevel.Should().Be(IndexVerdictLevel.Green);
        verdict.HasErrors.Should().BeTrue();
        verdict.Chips[0].Text.Should().Be("next_fire_time > 5");
    }

    [Fact]
    public void A_large_relation_with_an_unindexed_filter_or_sort_waits_for_run_anyway()
    {
        RowFilterVerdict redFilter = new() { FilterLevel = IndexVerdictLevel.Red };
        RowFilterVerdict redSort = new() { SortLevel = IndexVerdictLevel.Red };
        RowFilterVerdict green = new();

        string? withheld = TableRowService.ShouldWithhold(TableRowTestData.Relation(estimatedRows: 2_000_000), redFilter, 100_000);

        withheld.Should().Contain("MartenStudioOptions.ExactCountThreshold").And.Contain("2,000,000").And.Contain("Run anyway");
        TableRowService.ShouldWithhold(TableRowTestData.Relation(estimatedRows: 2_000_000), redSort, 100_000).Should().Contain("the sort");
        TableRowService.ShouldWithhold(TableRowTestData.Relation(estimatedRows: 2_000_000), green, 100_000).Should().BeNull();
        TableRowService.ShouldWithhold(TableRowTestData.Relation(estimatedRows: 99_999), redFilter, 100_000).Should().BeNull();

        TableRowService.ShouldWithhold(
                TableRowTestData.Relation(estimatedRows: null, sizeBytes: (CountEstimator.MaxSpeculativePages + 1) * 8192L), redFilter, 100_000)
            .Should().Contain("never analysed");
        TableRowService.ShouldWithhold(TableRowTestData.Relation(estimatedRows: null, sizeBytes: 8192), redFilter, 100_000)
            .Should().BeNull();
        TableRowService.ShouldWithhold(TableRowTestData.Relation("v", estimatedRows: null, sizeBytes: 0), redFilter, 100_000)
            .Should().BeNull("a view has no estimate, and each of its pages is bounded by the timeout");
    }

    // ---------------------------------------------------------------------------------------------------
    // The cursor
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, false, null, false)]
    [InlineData("next_fire_time", true, "638000000000000000", false)]
    [InlineData("calendar_name", false, null, false)]
    [InlineData("name", false, "", false)]
    [InlineData(null, false, null, true)]
    public void A_cursor_survives_its_own_encoding(string? sort, bool descending, string? sortValue, bool ctid)
    {
        IReadOnlyList<string> key = ctid ? ["(12,7)"] : ["a,b:c;d", "", "n;", "😀 two words", "9"];
        var cursor = new TableRowCursor(sort, descending, sortValue, key, ctid);

        string encoded = TableRowCursor.Encode(cursor);

        encoded.Should().MatchRegex("^[A-Za-z0-9_-]+$", "it travels in a query string");
        TableRowCursor.Decode(encoded).Should().BeEquivalentTo(cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("MQ")]
    [InlineData("MWFrbjtuOzA7")]
    public void A_cursor_that_is_not_one_decodes_to_nothing(string? value) =>
        TableRowCursor.Decode(value).Should().BeNull();

    private static PostgresException Postgres(string sqlState, string message) =>
        new(message, "ERROR", "ERROR", sqlState);
}
