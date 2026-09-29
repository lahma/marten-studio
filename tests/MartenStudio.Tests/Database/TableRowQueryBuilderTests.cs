using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using NpgsqlTypes;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The shape of every statement the row reader sends: both keyset texts, the key-only walk, ctid and offset
/// paging, the server-side cell caps, and the rules of hard rule 4 - no value and no type name in the
/// text, every built-in <c>pg_catalog.</c>-qualified, only the names handed in quoted.
/// </summary>
public class TableRowQueryBuilderTests
{
    private static readonly string[] QuartzKey = ["sched_name", "trigger_name", "trigger_group"];

    private static readonly Regex UnqualifiedBuiltin = new(
        @"(?<!pg_catalog\.)\b(substring|octet_length|lower|strpos|count|left|pg_column_size)\s*\(",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex CastOtherThanText = new(@"::(?!text\b)", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static TableRowListSpec Triggers(
        string? sort = null,
        bool descending = false,
        TableRowCursor? cursor = null,
        IReadOnlyList<RowFilterTerm>? filter = null) => new()
    {
        Schema = "quartz",
        Name = "qrtz_triggers",
        Columns =
        [
            new TableRowColumnRead("sched_name", "text"),
            new TableRowColumnRead("next_fire_time", "bigint"),
        ],
        Paging = TableRowPagingMode.Key,
        KeyColumns = QuartzKey,
        SortColumn = sort,
        Descending = descending,
        Cursor = cursor,
        PageSize = 5,
        Filter = filter ?? [],
    };

    // ---------------------------------------------------------------------------------------------------
    // Keyset: the two texts, and the key alone
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void After_a_row_whose_sort_value_was_set_the_walk_takes_the_three_way_predicate()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            cursor: new TableRowCursor("next_fire_time", false, "638000000000000000", ["s", "t", "g"])));

        statement.Sql.Should().Contain(
            "(t.\"next_fire_time\" > @s or t.\"next_fire_time\" is null or (t.\"next_fire_time\" = @s and " +
            "(t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\") > (@k1, @k2, @k3)))");
        statement.Sql.Should().Contain(
            "order by t.\"next_fire_time\" asc nulls last, t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\"");
        statement.Sql.Should().NotContain("@s is null", "an untyped parameter compared with null is 42P18");
        statement.Sql.Should().EndWith("limit @limit");
        Value(statement, "limit").Should().Be(6, "one row past the page, to know there is another");
    }

    [Fact]
    public void After_a_row_whose_sort_value_was_null_the_walk_is_a_different_text()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            cursor: new TableRowCursor("next_fire_time", false, null, ["s", "t", "g"])));

        statement.Sql.Should().Contain(
            "(t.\"next_fire_time\" is null and (t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\") > (@k1, @k2, @k3))");
        statement.Sql.Should().NotContain("@s");
        statement.Parameters.Select(static x => x.Name).Should().NotContain("s");
    }

    [Fact]
    public void A_descending_sort_turns_the_sort_comparison_and_keeps_the_key_tiebreak_ascending()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            descending: true,
            cursor: new TableRowCursor("next_fire_time", true, "5", ["s", "t", "g"])));

        statement.Sql.Should().Contain("(t.\"next_fire_time\" < @s or t.\"next_fire_time\" is null or (t.\"next_fire_time\" = @s and (");
        statement.Sql.Should().Contain(") > (@k1, @k2, @k3)))");
        statement.Sql.Should().Contain(
            "order by t.\"next_fire_time\" desc nulls last, t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\"");
    }

    [Fact]
    public void Sorting_by_the_key_alone_is_a_plain_row_value_comparison_in_either_direction()
    {
        TableRowStatement ascending = TableRowQueryBuilder.BuildList(Triggers(
            cursor: new TableRowCursor(null, false, null, ["s", "t", "g"])));

        ascending.Sql.Should().Contain("where (t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\") > (@k1, @k2, @k3)\n");
        ascending.Sql.Should().Contain("order by t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\"\n");
        ascending.Sql.Should().NotContain("nulls last");

        TableRowStatement descending = TableRowQueryBuilder.BuildList(Triggers(
            descending: true,
            cursor: new TableRowCursor(null, true, null, ["s", "t", "g"])));

        descending.Sql.Should().Contain("where (t.\"sched_name\", t.\"trigger_name\", t.\"trigger_group\") < (@k1, @k2, @k3)\n");
        descending.Sql.Should().Contain("order by t.\"sched_name\" desc, t.\"trigger_name\" desc, t.\"trigger_group\" desc\n");

        TableRowStatement single = TableRowQueryBuilder.BuildList(Triggers() with
        {
            KeyColumns = ["id"],
            Cursor = new TableRowCursor(null, false, null, ["42"]),
        });

        single.Sql.Should().Contain("where t.\"id\" > @k1\n");
    }

    [Fact]
    public void The_first_page_has_no_keyset_predicate()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(sort: "next_fire_time"));

        statement.Sql.Should().NotContain("where");
        statement.Parameters.Select(static x => x.Name).Should().NotContain(x => x.StartsWith('k') || x == "s");
    }

    [Fact]
    public void A_cursor_from_another_walk_is_refused_by_the_builder_too()
    {
        Action otherSort = () => TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            cursor: new TableRowCursor("calendar_name", false, "x", ["s", "t", "g"])));

        Action otherDirection = () => TableRowQueryBuilder.BuildList(Triggers(
            cursor: new TableRowCursor(null, true, null, ["s", "t", "g"])));

        Action shortKey = () => TableRowQueryBuilder.BuildList(Triggers(cursor: new TableRowCursor(null, false, null, ["s"])));

        Action ctidOnKey = () => TableRowQueryBuilder.BuildList(Triggers(cursor: new TableRowCursor(null, false, null, ["(0,1)"], IsCtid: true)));

        otherSort.Should().Throw<ArgumentException>();
        otherDirection.Should().Throw<ArgumentException>();
        shortKey.Should().Throw<ArgumentException>();
        ctidOnKey.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------------------------------------------
    // ctid and offset
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_keyless_heap_table_pages_on_ctid_bound_as_untyped_text()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(new TableRowListSpec
        {
            Schema = "legacy",
            Name = "audit_log",
            Columns = [new TableRowColumnRead("actor", "text")],
            Paging = TableRowPagingMode.Ctid,
            SelectLocator = true,
            Cursor = new TableRowCursor(null, false, null, ["(3,17)"], IsCtid: true),
            PageSize = 50,
        });

        statement.Sql.Should().StartWith("select t.ctid::text,");
        statement.Sql.Should().Contain("where t.ctid > @c\n");
        statement.Sql.Should().Contain("order by t.ctid\n");
        statement.Sql.Should().NotContain("::tid", "the text is untyped and Postgres resolves it against ctid");
        statement.Layout.LocatorOrdinal.Should().Be(0);

        TableRowParameter c = statement.Parameters.Single(static x => x.Name == "c");
        c.Type.Should().Be(NpgsqlDbType.Unknown);
        c.Value.Should().Be("(3,17)");
    }

    [Fact]
    public void A_ctid_walk_after_a_sort_column_uses_ctid_as_the_tiebreak()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(new TableRowListSpec
        {
            Schema = "legacy",
            Name = "audit_log",
            Columns = [new TableRowColumnRead("actor", "text")],
            Paging = TableRowPagingMode.Ctid,
            SelectLocator = true,
            SortColumn = "entity_key",
            Cursor = new TableRowCursor("entity_key", false, "7", ["(3,17)"], IsCtid: true),
            PageSize = 50,
        });

        statement.Sql.Should().Contain(
            "(t.\"entity_key\" > @s or t.\"entity_key\" is null or (t.\"entity_key\" = @s and t.ctid > @c))");
        statement.Sql.Should().Contain("order by t.\"entity_key\" asc nulls last, t.ctid\n");
    }

    [Fact]
    public void A_view_pages_by_offset_over_every_sortable_column()
    {
        TableRowListSpec spec = new()
        {
            Schema = "legacy",
            Name = "active_employees",
            Columns = [new TableRowColumnRead("employee_id", "integer"), new TableRowColumnRead("display_name", "text")],
            Paging = TableRowPagingMode.Offset,
            TieBreakColumns = ["employee_id", "display_name", "hired_on"],
            Offset = 40,
            PageSize = 20,
        };

        TableRowStatement statement = TableRowQueryBuilder.BuildList(spec);

        statement.Sql.Should().Contain("order by t.\"employee_id\", t.\"display_name\", t.\"hired_on\"\n");
        statement.Sql.Should().EndWith("limit @limit offset @offset");
        Value(statement, "offset").Should().Be(40);
        Value(statement, "limit").Should().Be(21);

        TableRowStatement sorted = TableRowQueryBuilder.BuildList(spec with { SortColumn = "hired_on", Descending = true });

        sorted.Sql.Should().Contain("order by t.\"hired_on\" desc nulls last, t.\"employee_id\", t.\"display_name\"\n");
        sorted.Layout.SortOrdinal.Should().Be(-1, "an offset page needs no cursor");
    }

    [Fact]
    public void An_offset_past_the_cap_or_below_zero_is_refused()
    {
        TableRowListSpec spec = Triggers() with { Paging = TableRowPagingMode.Offset };

        FluentActions.Invoking(() => TableRowQueryBuilder.BuildList(spec with { Offset = TableRowQueryBuilder.MaxOffset + 1 }))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => TableRowQueryBuilder.BuildList(spec with { Offset = -1 }))
            .Should().Throw<ArgumentOutOfRangeException>();

        TableRowQueryBuilder.BuildList(spec with { Offset = TableRowQueryBuilder.MaxOffset }).Sql.Should().Contain("offset @offset");
        TableRowQueryBuilder.MaxOffset.Should().Be(10_000);
    }

    // ---------------------------------------------------------------------------------------------------
    // Cells
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Cells_are_cut_on_the_server_and_the_keyset_is_read_raw_beside_them()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(sort: "next_fire_time") with
        {
            Columns =
            [
                new TableRowColumnRead("sched_name", "text"),
                new TableRowColumnRead("payload", "jsonb"),
                new TableRowColumnRead("job_data", "bytea"),
                new TableRowColumnRead("next_fire_time", "bigint"),
                new TableRowColumnRead("total", "numeric(12,2)"),
                new TableRowColumnRead("tags", "character varying(20)[]"),
            ],
        });

        string[] lines = statement.Sql.Split('\n');

        lines[0].Should().Be("select t.\"sched_name\"::text,");
        lines[1].Trim().Should().Be("t.\"trigger_name\"::text,");
        lines[2].Trim().Should().Be("t.\"trigger_group\"::text,");
        lines[3].Trim().Should().Be("t.\"next_fire_time\"::text,", "the sort column, raw, for the cursor");

        statement.Sql.Should().Contain("pg_catalog.substring(t.\"sched_name\"::text, 1, @cap)");
        statement.Sql.Should().Contain("pg_catalog.octet_length(t.\"sched_name\"::text)");
        statement.Sql.Should().Contain("pg_catalog.substring(t.\"payload\"::text, 1, @jsonCap)");
        statement.Sql.Should().Contain("pg_catalog.substring(t.\"job_data\", 1, @bytes)");
        statement.Sql.Should().Contain("pg_catalog.octet_length(t.\"job_data\")");
        statement.Sql.Should().Contain("pg_catalog.substring(t.\"total\"::text, 1, @cap)", "numeric is read as the text Postgres prints");
        statement.Sql.Should().Contain("pg_catalog.substring(t.\"tags\"::text, 1, @cap)");
        statement.Sql.Should().MatchRegex("(?m)^\\s+t\\.\"next_fire_time\",$", "a bigint cannot be large and is read as itself");

        Value(statement, "cap").Should().Be(TableRowCaps.List.Text + 1, "one past the cap is how a cut is known");
        Value(statement, "jsonCap").Should().Be(TableRowCaps.List.Json + 1);
        Value(statement, "bytes").Should().Be(64);
        statement.Parameters.Count(static x => x.Name == "cap").Should().Be(1, "one cap, however many columns");

        statement.Layout.KeyColumns.Should().Equal(QuartzKey);
        statement.Layout.KeyOrdinals.Should().Equal(0, 1, 2);
        statement.Layout.SortOrdinal.Should().Be(3);
        statement.Layout.Cells.Select(static x => x.Shape).Should().Equal(
            TableRowCellShape.Text, TableRowCellShape.Json, TableRowCellShape.Binary, TableRowCellShape.Native,
            TableRowCellShape.Number, TableRowCellShape.Array);
        statement.Layout.Cells.Single(static x => x.Column == "next_fire_time").LengthOrdinal.Should().Be(-1);
    }

    // Shape and kind travel as their names: both enums are internal, and a public theory cannot take them.
    [Theory]
    [InlineData("integer", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Number))]
    [InlineData("bigint", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Number))]
    [InlineData("double precision", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Number))]
    [InlineData("boolean", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Boolean))]
    [InlineData("uuid", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Uuid))]
    [InlineData("timestamp with time zone", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Timestamp))]
    [InlineData("timestamp(3) without time zone", nameof(TableRowCellShape.Native), nameof(SqlCellKind.Timestamp))]
    [InlineData("numeric(10,2)", nameof(TableRowCellShape.Number), nameof(SqlCellKind.Number))]
    [InlineData("date", nameof(TableRowCellShape.Temporal), nameof(SqlCellKind.Timestamp))]
    [InlineData("interval", nameof(TableRowCellShape.Temporal), nameof(SqlCellKind.Timestamp))]
    [InlineData("interval year to month", nameof(TableRowCellShape.Temporal), nameof(SqlCellKind.Timestamp))]
    [InlineData("time with time zone", nameof(TableRowCellShape.Temporal), nameof(SqlCellKind.Timestamp))]
    [InlineData("jsonb", nameof(TableRowCellShape.Json), nameof(SqlCellKind.Json))]
    [InlineData("json", nameof(TableRowCellShape.Json), nameof(SqlCellKind.Json))]
    [InlineData("bytea", nameof(TableRowCellShape.Binary), nameof(SqlCellKind.Binary))]
    [InlineData("text[]", nameof(TableRowCellShape.Array), nameof(SqlCellKind.Array))]
    [InlineData("character varying(200)", nameof(TableRowCellShape.Text), nameof(SqlCellKind.Text))]
    [InlineData("legacy.order_status", nameof(TableRowCellShape.Text), nameof(SqlCellKind.Text))]
    [InlineData("public.citext", nameof(TableRowCellShape.Text), nameof(SqlCellKind.Text))]
    public void Every_type_has_a_shape(string type, string shape, string kind)
    {
        TableRowQueryBuilder.ShapeOf(type).Should().Be(Enum.Parse<TableRowCellShape>(shape));
        TableRowQueryBuilder.KindOf(type).Should().Be(Enum.Parse<SqlCellKind>(kind));
    }

    // ---------------------------------------------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_filter_form_is_emitted_with_its_value_as_a_parameter()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(filter:
        [
            new RowFilterTerm("trigger_state", RowFilterOperator.Equal, "WAITING", 0, 1),
            new RowFilterTerm("priority", RowFilterOperator.NotEqual, "5", 0, 1),
            new RowFilterTerm("next_fire_time", RowFilterOperator.GreaterThanOrEqual, "007", 0, 1),
            new RowFilterTerm("description", RowFilterOperator.Contains, "Nightly", 0, 1),
            new RowFilterTerm("calendar_name", RowFilterOperator.IsNull, null, 0, 1),
            new RowFilterTerm("end_time", RowFilterOperator.IsNotNull, null, 0, 1),
        ]));

        statement.Sql.Should().Contain("where t.\"trigger_state\" = @f1\n");
        statement.Sql.Should().Contain("  and t.\"priority\" <> @f2\n");
        statement.Sql.Should().Contain("  and t.\"next_fire_time\" >= @f3\n");
        statement.Sql.Should().Contain("  and pg_catalog.strpos(pg_catalog.lower(t.\"description\"::text), pg_catalog.lower(@f4)) > 0\n");
        statement.Sql.Should().Contain("  and t.\"calendar_name\" is null\n");
        statement.Sql.Should().Contain("  and t.\"end_time\" is not null\n");

        Value(statement, "f3").Should().Be("007", "the raw token, leading zeros and all");
        statement.Parameters.Where(static x => x.Name.StartsWith('f')).Should().OnlyContain(static x => x.Type == NpgsqlDbType.Unknown);
    }

    [Fact]
    public void No_value_and_no_type_name_ever_reaches_the_text()
    {
        const string hostile = "x'); drop table legacy.audit_log; --";

        List<TableRowStatement> statements = [.. EveryStatement(hostile)];

        foreach (TableRowStatement statement in statements)
        {
            statement.Sql.Should().NotContain(hostile);
            statement.Sql.Should().NotContain("'", "there is no literal of any kind in a row statement");
            CastOtherThanText.IsMatch(statement.Sql).Should().BeFalse(
                "the one cast written is ::text, to read a text form; values are typed by Postgres: " + statement.Sql);

            foreach (TableRowParameter parameter in statement.Parameters)
            {
                if (parameter.Name is "limit" or "offset" or "cap" or "jsonCap" or "bytes")
                {
                    parameter.Type.Should().BeOneOf(NpgsqlDbType.Integer, NpgsqlDbType.Bigint);
                    continue;
                }

                parameter.Type.Should().Be(NpgsqlDbType.Unknown,
                    "@" + parameter.Name + " carries a key, cursor or filter value, which Postgres types from its column");
            }
        }

        statements.SelectMany(static x => x.Parameters).Should().Contain(x => Equals(x.Value, hostile));
    }

    [Fact]
    public void Every_builtin_is_pg_catalog_qualified()
    {
        foreach (TableRowStatement statement in EveryStatement("v"))
        {
            UnqualifiedBuiltin.Matches(statement.Sql).Should().BeEmpty(statement.Sql);
        }

        string.Join("\n", EveryStatement("v").Select(static x => x.Sql)).Should()
            .Contain("pg_catalog.substring(").And.Contain("pg_catalog.octet_length(").And.Contain("pg_catalog.lower(")
            .And.Contain("pg_catalog.strpos(").And.Contain("pg_catalog.count(*)");
    }

    [Fact]
    public void Only_the_names_handed_in_are_quoted_and_a_name_quoting_refuses_is_never_written()
    {
        // The filter's column was typed NEXT_FIRE_TIME; the grammar resolved it to the catalog's spelling,
        // and that is the only spelling the builder ever sees.
        RowFilterParse parse = RowFilterGrammar.Parse(
            "NEXT_FIRE_TIME > 5",
            [TableRowTestData.Column("next_fire_time", "bigint", position: 1)]);

        parse.Errors.Should().BeEmpty();

        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(filter: parse.Terms));

        statement.Sql.Should().Contain("t.\"next_fire_time\" > @f1");
        statement.Sql.Should().NotContain("NEXT_FIRE_TIME");

        FluentActions.Invoking(() => TableRowQueryBuilder.BuildList(Triggers() with { Name = "bad\"name" }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TableRowQueryBuilder.BuildList(Triggers() with { Columns = [new TableRowColumnRead("x\"y", "text")] }))
            .Should().Throw<ArgumentException>();

        Regex.Matches(statement.Sql, "\"([^\"]*)\"").Select(static x => x.Groups[1].Value).Distinct()
            .Should().BeSubsetOf(["quartz", "qrtz_triggers", "sched_name", "trigger_name", "trigger_group", "next_fire_time"]);
    }

    // ---------------------------------------------------------------------------------------------------
    // One row, one cell, references, counts
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_row_is_read_by_its_whole_key_under_the_detail_caps()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildRow(
            "legacy",
            "stock_levels",
            [new TableRowColumnRead("sku", "text"), new TableRowColumnRead("quantity", "integer")],
            ["region_code", "warehouse_no", "sku"],
            ["FI", "1", "SKU-001"],
            null,
            TableRowCaps.Detail);

        statement.Sql.Should().Contain("from \"legacy\".\"stock_levels\" as t\nwhere t.\"region_code\" = @k1\n  and t.\"warehouse_no\" = @k2\n  and t.\"sku\" = @k3\nlimit 1");
        Value(statement, "cap").Should().Be(256 * 1024 + 1);
        statement.Layout.KeyOrdinals.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void A_cell_is_read_whole_up_to_its_cap_by_key_or_by_ctid()
    {
        TableRowStatement text = TableRowQueryBuilder.BuildCell(
            "legacy", "integration_messages", new TableRowColumnRead("raw_body", "text"), ["message_id"], ["m"], null, 512 * 1024);

        text.Sql.Should().Be(
            "select pg_catalog.substring(t.\"raw_body\"::text, 1, @cap),\n" +
            "       pg_catalog.octet_length(t.\"raw_body\"::text)\n" +
            "from \"legacy\".\"integration_messages\" as t\n" +
            "where t.\"message_id\" = @k1\n" +
            "limit 1");
        Value(text, "cap").Should().Be(512 * 1024 + 1);

        TableRowStatement bytes = TableRowQueryBuilder.BuildCell(
            "legacy", "audit_log", new TableRowColumnRead("blob", "bytea"), [], [], "(0,3)", 1000);

        bytes.Sql.Should().Contain("pg_catalog.substring(t.\"blob\", 1, @cap)");
        bytes.Sql.Should().Contain("where t.ctid = @c\n");
        Value(bytes, "cap").Should().Be(1000, "bytes are cut exactly; octet_length says whether they were");
    }

    [Fact]
    public void A_parent_row_is_an_exists_and_an_inbound_count_is_bounded()
    {
        TableRowStatement exists = TableRowQueryBuilder.BuildExists(
            "legacy", "warehouses", ["region_code", "warehouse_no"], ["FI", "1"]);

        exists.Sql.Should().Be(
            "select exists (\n    select 1\n    from \"legacy\".\"warehouses\" as p\n" +
            "    where p.\"region_code\" = @v1\n      and p.\"warehouse_no\" = @v2)");

        TableRowStatement inbound = TableRowQueryBuilder.BuildInboundCount(
            "quartz", "qrtz_triggers", QuartzKey, ["s", "j", "g"]);

        inbound.Sql.Should().Be(
            "select pg_catalog.count(*)\nfrom (\n    select 1\n    from \"quartz\".\"qrtz_triggers\" as c\n" +
            "    where c.\"sched_name\" = @v1\n      and c.\"trigger_name\" = @v2\n      and c.\"trigger_group\" = @v3\n" +
            "    limit @cap) as bounded");
        Value(inbound, "cap").Should().Be(1001, "one more than the 1,000 it reports");
    }

    [Fact]
    public void A_count_is_the_whole_relation_or_a_bounded_filtered_one()
    {
        TableRowQueryBuilder.BuildCount("legacy", "audit_log", [], 100_001).Sql
            .Should().Be("select pg_catalog.count(*)\nfrom \"legacy\".\"audit_log\" as t");

        TableRowStatement filtered = TableRowQueryBuilder.BuildCount(
            "legacy", "audit_log", [new RowFilterTerm("actor", RowFilterOperator.Equal, "system", 0, 1)], 100_001);

        filtered.Sql.Should().Be(
            "select pg_catalog.count(*)\nfrom (\n    select 1\n    from \"legacy\".\"audit_log\" as t\n" +
            "    where t.\"actor\" = @f1\n    limit @cap) as bounded");
        Value(filtered, "cap").Should().Be(100_001L);
    }

    [Fact]
    public void The_parameter_names_are_disclosed_and_the_values_are_not()
    {
        TableRowStatement statement = TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            cursor: new TableRowCursor("next_fire_time", false, "12345", ["secret-scheduler", "t", "g"])));

        statement.ParameterNames.Should().Contain(["@cap", "@k1", "@k2", "@k3", "@s", "@limit"]);
        string.Join(" ", statement.ParameterNames).Should().NotContain("secret-scheduler");
    }

    private static object Value(TableRowStatement statement, string name) =>
        statement.Parameters.Single(x => x.Name == name).Value;

    /// <summary>One of every statement, each carrying <paramref name="value" /> wherever it takes a value.</summary>
    private static IEnumerable<TableRowStatement> EveryStatement(string value)
    {
        IReadOnlyList<TableRowColumnRead> columns =
        [
            new TableRowColumnRead("sched_name", "text"),
            new TableRowColumnRead("payload", "jsonb"),
            new TableRowColumnRead("job_data", "bytea"),
            new TableRowColumnRead("next_fire_time", "bigint"),
        ];

        IReadOnlyList<RowFilterTerm> filter =
        [
            new RowFilterTerm("sched_name", RowFilterOperator.Equal, value, 0, 1),
            new RowFilterTerm("payload", RowFilterOperator.Contains, value, 0, 1),
        ];

        yield return TableRowQueryBuilder.BuildList(Triggers(
            sort: "next_fire_time",
            cursor: new TableRowCursor("next_fire_time", false, value, [value, value, value]),
            filter: filter) with { Columns = columns });

        yield return TableRowQueryBuilder.BuildList(new TableRowListSpec
        {
            Schema = "legacy",
            Name = "audit_log",
            Columns = columns,
            Paging = TableRowPagingMode.Ctid,
            SelectLocator = true,
            Cursor = new TableRowCursor(null, false, null, [value], IsCtid: true),
            PageSize = 5,
            Filter = filter,
        });

        yield return TableRowQueryBuilder.BuildList(Triggers(filter: filter) with
        {
            Paging = TableRowPagingMode.Offset,
            Offset = 10,
            Columns = columns,
        });

        yield return TableRowQueryBuilder.BuildRow("quartz", "qrtz_triggers", columns, QuartzKey, [value, value, value], null, TableRowCaps.Detail);
        yield return TableRowQueryBuilder.BuildCell("quartz", "qrtz_triggers", columns[1], QuartzKey, [value, value, value], null, 100);
        yield return TableRowQueryBuilder.BuildCell("legacy", "audit_log", columns[2], [], [], value, 100);
        yield return TableRowQueryBuilder.BuildKeyRead("quartz", "qrtz_triggers", QuartzKey, [value, value, value], ["job_name", "job_group"]);
        yield return TableRowQueryBuilder.BuildExists("quartz", "qrtz_job_details", ["sched_name", "job_name"], [value, value]);
        yield return TableRowQueryBuilder.BuildInboundCount("quartz", "qrtz_triggers", ["sched_name", "job_name"], [value, value]);
        yield return TableRowQueryBuilder.BuildCount("quartz", "qrtz_triggers", [], 10);
        yield return TableRowQueryBuilder.BuildCount("quartz", "qrtz_triggers", filter, 10);
    }
}
