using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// X2's what-if, with no database: the builder (hypopg's calls, the index text from <see cref="MigrationSqlText" />), the indexes a
/// plan suggests - read off Postgres' own deparse of a filter, conservatively - and what a comparison says.
/// </summary>
public class HypotheticalIndexTests
{
    // ---- The builder ----------------------------------------------------------------------------------------------------

    [Fact]
    public void hypopgs_calls_name_its_schema_quoted_and_carry_the_definition_as_a_parameter()
    {
        HypotheticalIndexQueries.Create("public").Should().Be("select indexrelid, indexname from \"public\".hypopg_create_index(@definition)");
        HypotheticalIndexQueries.Create("Odd Schema").Should().Be("select indexrelid, indexname from \"Odd Schema\".hypopg_create_index(@definition)");
        HypotheticalIndexQueries.Reset("public").Should().Be("select \"public\".hypopg_reset()");

        HypotheticalIndexQueries.Create("evil\"; drop table x; --").Should().BeNull("a name the builder will not quote gets no statement");
        HypotheticalIndexQueries.Reset("").Should().BeNull();
        HypotheticalIndexQueries.Create("a\0b").Should().BeNull();
    }

    [Fact]
    public void The_probe_reads_the_extensions_own_functions_and_never_creates_anything()
    {
        string sql = HypotheticalIndexQueries.AvailabilitySql;

        sql.Should().Contain("from pg_catalog.pg_extension e").And.Contain("e.extname = 'hypopg'");
        sql.Should().Contain("has_function_privilege(c.oid, 'execute')").And.Contain("has_function_privilege(r.oid, 'execute')");
        sql.Should().Contain("has_schema_privilege(n.oid, 'usage')");
        sql.Should().Contain("pg_catalog.pg_depend").And.Contain("d.deptype = 'e'", "only the extension's own member is ever called");
        sql.Should().NotContainEquivalentOf("create extension");
        sql.Should().NotContain("hypopg_list_indexes", "a role short of a superuser is denied the view (42501, PROBE)");

        HypotheticalIndexQueries.TerminateSelfSql.Should().Be("select pg_catalog.pg_terminate_backend(pg_catalog.pg_backend_pid())");
    }

    [Fact]
    public void The_index_text_comes_from_MigrationSqlText_every_name_quoted()
    {
        MigrationSqlText.CreateIndex("public", "orders", ["customer_id"]).Should().Be("CREATE INDEX ON \"public\".\"orders\" (\"customer_id\");");
        MigrationSqlText.CreateIndex(null, "t", ["b"]).Should().Be("CREATE INDEX ON \"t\" (\"b\");", "a plain plan names no schema");
        MigrationSqlText.CreateIndex("public", "Line Items", ["Mixed Case", "order"]).Should()
            .Be("CREATE INDEX ON \"public\".\"Line Items\" (\"Mixed Case\", \"order\");");

        MigrationSqlText.CreateIndex("public", "t", []).Should().BeNull();
        MigrationSqlText.CreateIndex("public", "t", ["a\"b"]).Should().BeNull("a quote in a name gets no statement");
        MigrationSqlText.CreateIndex("public", "t‮", ["b"]).Should().BeNull("a right-to-left override makes the text say something else");
        MigrationSqlText.CreateIndex("pub\"lic", "t", ["b"]).Should().BeNull();
    }

    // ---- The columns a filter compares -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("(b = 5)", "t", "b")]
    [InlineData("((a = 3) AND (b = 5))", "t", "a,b")]
    [InlineData("((t.a = 3) AND (t.b = 5))", "t", "a,b")]
    [InlineData("((o.total > 100::numeric) AND (o.customer_id = $1))", "o", "customer_id,total")]
    [InlineData("(t.\"Mixed Case\" = 5)", "t", "Mixed Case")]
    [InlineData("(\"order\" = 5)", "t", "order")]
    [InlineData("(status = ANY ('{a,b}'::text[]))", "t", "status")]
    [InlineData("(created_at > '2026-01-01 00:00:00+00'::timestamp with time zone)", "t", "created_at")]
    [InlineData("((name)::text = 'x'::character varying(20))", "t", "name")]
    [InlineData("((x.a = 1) AND (t.b = 2))", "t", "b")]
    [InlineData("((a = 1) AND (b = 2) AND (c = 3) AND (d = 4))", "t", "a,b,c")]
    [InlineData("((a > 1) AND (b = 2))", "t", "b,a")]
    [InlineData("(NOT deleted)", "t", "deleted")]
    public void A_filter_names_the_plain_columns_it_compares_equality_first(string filter, string alias, string expected)
    {
        HypotheticalIndexCandidates.Columns(filter, alias).Should().Equal(expected.Split(','));
    }

    [Theory]
    [InlineData("((d ->> 'k'::text) = '7'::text)", "a JSON operator's operand: an index on the jsonb does not serve ->>")]
    [InlineData("((data #>> '{Name}'::text[]) = $1)", "the studio's own JSON path")]
    [InlineData("(data @> '{\"a\": 1}'::jsonb)", "containment wants GIN, which hypopg cannot imagine")]
    [InlineData("(lower(c) = 'x'::text)", "an index on c does not serve lower(c)")]
    [InlineData("(c ~~ '%abc%'::text)", "a LIKE wants a trigram index, which hypopg cannot imagine")]
    [InlineData("(c ~~* 'abc%'::text)", "an ILIKE likewise")]
    [InlineData("(SubPlan 1)", "a subplan names no column")]
    [InlineData("('abc = 1", "unreadable text is no guess")]
    [InlineData("(x.b = 5)", "another table's column")]
    [InlineData("($1 = 5)", "a parameter is no column")]
    public void A_filter_that_no_btree_on_its_columns_serves_suggests_nothing(string filter, string why)
    {
        HypotheticalIndexCandidates.Columns(filter, "t").Should().BeEmpty(why);
    }

    [Fact]
    public void A_join_filter_counts_only_the_inner_sides_own_prefixed_columns()
    {
        HypotheticalIndexCandidates.Columns("(u.t_id = t.id)", "u", prefixedOnly: true).Should().Equal("t_id");
        HypotheticalIndexCandidates.Columns("(t_id = id)", "u", prefixedOnly: true).Should().BeEmpty("a join filter always names two tables");
    }

    // ---- The indexes a plan suggests --------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NestedLoopPlans))]
    public void A_sequential_scan_that_filters_suggests_an_index_on_its_columns_on_every_major(string fixture)
    {
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get(fixture)).Plan!;

        HypotheticalIndex index = HypotheticalIndexCandidates.FromPlan(plan).Should().ContainSingle().Subject;

        index.Definition.Should().Be("CREATE INDEX ON \"u\" (\"v\");", "PROBE's nested loop reads u with (v < 2), and a plain plan names no schema");
        index.Caption.Should().Be("An index on u (v)");
        index.Origin.Should().Be(HypotheticalIndexOrigin.Plan);
        index.Columns.Should().Equal("v");
        index.Note.Should().BeNull();
        index.Key.Should().Be(HypotheticalIndexCandidates.Key(null, "u", ["v"]));
    }

    public static TheoryData<string> NestedLoopPlans() => [.. ExplainFixtures.Majors.Select(static x => ExplainFixtures.Name(x, "nestloop", analyze: false))];

    [Fact]
    public void A_leading_wildcard_scan_suggests_nothing()
    {
        HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(ExplainFixtures.Get("17-seqfilter-analyze")).Plan!).Should()
            .BeEmpty("P07's LIKE '%abc%' wants a trigram index, which hypopg cannot imagine");
    }

    [Fact]
    public void A_verbose_plan_names_the_schema_a_marten_table_carries_a_note_and_a_plan_offers_each_index_once()
    {
        const string Json =
            """
            [{"Plan": {"Node Type": "Append", "Total Cost": 300, "Plans": [
              {"Node Type": "Seq Scan", "Parent Relationship": "Member", "Relation Name": "mt_doc_order", "Schema": "public", "Alias": "d",
               "Filter": "(d.customer = $1)", "Total Cost": 100},
              {"Node Type": "Seq Scan", "Parent Relationship": "Member", "Relation Name": "mt_doc_order", "Schema": "public", "Alias": "d",
               "Filter": "(d.customer = $2)", "Total Cost": 100},
              {"Node Type": "Nested Loop", "Parent Relationship": "Member", "Join Filter": "(i.order_id = o.id)", "Total Cost": 100, "Plans": [
                {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "orders", "Schema": "app", "Alias": "o", "Total Cost": 10},
                {"Node Type": "Seq Scan", "Parent Relationship": "Inner", "Relation Name": "items", "Schema": "app", "Alias": "i", "Total Cost": 10}]}]}}]
            """;

        IReadOnlyList<HypotheticalIndex> indexes = HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(Json).Plan!);

        indexes.Select(static x => x.Definition).Should().Equal(
            "CREATE INDEX ON \"public\".\"mt_doc_order\" (\"customer\");",
            "CREATE INDEX ON \"app\".\"items\" (\"order_id\");");
        indexes[0].Note.Should().Be(HypotheticalIndexCandidates.MartenTableNote);
        indexes[0].Caption.Should().Be("An index on public.mt_doc_order (customer)");
        indexes[1].Note.Should().BeNull();
    }

    [Fact]
    public void A_key_cannot_be_forged_by_a_name_that_contains_the_separator()
    {
        HypotheticalIndexCandidates.Key("a", "b;c", ["d"]).Should().NotBe(HypotheticalIndexCandidates.Key("a;b", "c", ["d"]));
        HypotheticalIndexCandidates.Key(null, "t", ["a", "b"]).Should().NotBe(HypotheticalIndexCandidates.Key(null, "t", ["a,b"]));
    }

    // ---- What a comparison says -------------------------------------------------------------------------------------------

    private static HypotheticalIndex Index() =>
        HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(ExplainFixtures.Get("17-nestloop-plain")).Plan!)[0];

    private static string Plan(double cost, string? index = null)
    {
        string total = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return index is null
            ? "[{\"Plan\": {\"Node Type\": \"Seq Scan\", \"Relation Name\": \"u\", \"Alias\": \"u\", \"Filter\": \"(v < 2)\", \"Total Cost\": " + total + "}}]"
            : "[{\"Plan\": {\"Node Type\": \"Bitmap Heap Scan\", \"Relation Name\": \"u\", \"Alias\": \"u\", \"Total Cost\": " + total
              + ", \"Plans\": [{\"Node Type\": \"Bitmap Index Scan\", \"Parent Relationship\": \"Outer\", \"Index Name\": \"" + index + "\", \"Total Cost\": 5}]}}]";
    }

    private static HypotheticalIndexRead Read(string before, string after, string name = "<13630>btree_u_v") => new()
    {
        Availability = new HypotheticalIndexAvailability(HypotheticalIndexState.Available, "public", "1.4.4"),
        BeforeJson = before,
        AfterJson = after,
        Made = new HypotheticalIndexMade(13630, name),
    };

    [Fact]
    public void An_index_the_plan_reads_is_used_and_the_verdict_gives_both_costs()
    {
        WhatIfResult result = WhatIfResult.From(Index(), Read(Plan(5167), Plan(622.23, "<13630>btree_u_v")));

        result.Used.Should().BeTrue();
        result.IndexName.Should().Be("<13630>btree_u_v");
        result.Reason.Should().BeNull();
        result.Verdict.Should().Be("Postgres would use it: the plan's estimated cost goes from 5,167.00 to 622.23 (88% less).");
        result.Outcome().Should().Be("Compared: the hypothetical index would be used (estimated cost 5,167.00 to 622.23).");
        result.Outcome().Should().NotContain("btree").And.NotContain("u (v)", "the audit names no index");
    }

    [Fact]
    public void An_index_the_plan_does_not_read_is_not_used()
    {
        WhatIfResult result = WhatIfResult.From(Index(), Read(Plan(5167), Plan(5167)));

        result.Used.Should().BeFalse();
        result.Verdict.Should().Be("Postgres would not use it for this statement: the plan's estimated cost stays 5,167.00.");
        result.Outcome().Should().StartWith("Compared: the hypothetical index would not be used");
    }

    [Fact]
    public void Used_is_judged_before_the_mask_touches_a_name_and_the_plans_shown_are_masked()
    {
        WhatIfResult result = WhatIfResult.From(Index(), Read(Plan(10), Plan(5, "<1>btree_u_v"), "<1>btree_u_v"), static name => name.Replace("u", "‹withheld›", StringComparison.Ordinal));

        result.Used.Should().BeTrue("the index was read before anything was masked");
        result.After!.Nodes.Should().Contain(static x => x.RelationName == "‹withheld›");
    }

    [Fact]
    public void hypopgs_absence_and_a_role_that_may_not_use_it_are_said_plainly_and_say_which()
    {
        HypotheticalIndexAvailability.Of(new HypopgCatalog(false, null, null, false)).Sentence.Should()
            .Be(HypotheticalIndexAvailability.NotInstalledSentence);
        HypotheticalIndexAvailability.Of(new HypopgCatalog(true, "public", "1.4.4", false)).Sentence.Should()
            .Be(HypotheticalIndexAvailability.NoPrivilegeSentence);
        HypotheticalIndexAvailability.Of(new HypopgCatalog(true, "public", "1.4.4", true)).Sentence.Should().BeNull();

        HypotheticalIndexAvailability.NotInstalledSentence.Should().StartWith("hypopg is not installed in this database")
            .And.Contain("CREATE EXTENSION hypopg").And.Contain("is the host's decision")
            .And.Contain("the studio never installs an extension, and never creates an index");
        HypotheticalIndexAvailability.NoPrivilegeSentence.Should().StartWith("hypopg is installed in this database, but the role the studio plans as")
            .And.Contain("EXECUTE on hypopg_create_index and hypopg_reset").And.Contain("is the host's decision");

        WhatIfResult result = WhatIfResult.From(Index(), new HypotheticalIndexRead { Availability = new(HypotheticalIndexState.NotInstalled, null, null) });
        result.Reason.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence);
        result.Availability.Should().Be(HypotheticalIndexState.NotInstalled);
        result.Before.Should().BeNull();
        result.Refusal.Should().Be(DatabaseRefusal.None, "hypopg's absence is not the gate's refusal");
    }

    [Theory]
    [InlineData("XX000", "hypopg could not imagine this index")]
    [InlineData("42501", "may not read a table")]
    [InlineData("57014", "statement_timeout")]
    [InlineData("55P03", "holds a lock")]
    public void A_refusal_is_the_studios_sentence_by_SQLSTATE(string sqlState, string says)
    {
        WhatIfResult.Failed(sqlState).Should().StartWith("Not compared: Postgres refused the what-if (SQLSTATE " + sqlState + ")").And.Contain(says);
    }

    [Fact]
    public void Only_an_index_the_builder_writes_from_its_own_parts_is_imagined_by_the_console()
    {
        HypotheticalIndex index = Index();

        SqlConsoleService.IsBuilt(index).Should().BeTrue();
        SqlConsoleService.IsBuilt(index with { Definition = "CREATE INDEX ON \"u\" (\"v\"); CREATE INDEX ON \"u\" (\"w\");" }).Should().BeFalse();
        SqlConsoleService.IsBuilt(index with { Definition = "CREATE INDEX ON u USING gin (v)" }).Should().BeFalse();
        SqlConsoleService.IsBuilt(index with { Origin = HypotheticalIndexOrigin.Marten }).Should().BeFalse("Marten's DDL is not the console's to imagine");
    }
}
