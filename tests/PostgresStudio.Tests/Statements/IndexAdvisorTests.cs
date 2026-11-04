using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.2-INDEX-A, the pure half of the Statements page's index advisor: which statements are planned and how each drop is counted;
/// how the indexes their plans suggest are merged, ordered and capped; the trials those make and the plan bound; how what was tried
/// is ranked (the 10% threshold, calls times cost saved, the share of the workload, the prefix fold); the visitor's own columns.
/// </summary>
public class IndexAdvisorTests
{
    private static readonly StudioTable Orders = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);

    private static readonly StudioTable Lines = new("public", "order_lines", StudioTableKind.Relational, StudioTableOwnership.Owned);

    private static QueryPlan Plan(string json) => QueryPlanReader.Read(json).Plan!;

    private static string SeqScan(string schema, string table, string filter, double cost) =>
        $$$"""[{"Plan": {"Node Type": "Seq Scan", "Relation Name": "{{{table}}}", "Schema": "{{{schema}}}", "Alias": "{{{table}}}", "Total Cost": {{{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}, "Plan Rows": 100, "Plan Width": 8, "Filter": "{{{filter}}}"}}]""";

    private static StatementCounts Counts(double calls, double execMs) => new(calls, execMs, 0, 0, 0, 0, 0, null);

    private static (StatementFact Fact, StatementRowView Row) Statement(
        long queryId,
        string? text,
        double calls = 10,
        double execMs = 100,
        IReadOnlyList<StudioTable>? tables = null,
        bool truncated = false,
        bool shown = true)
    {
        StatementFact fact = new()
        {
            QueryId = queryId,
            Text = text,
            TextTruncated = truncated,
            TextRead = text is not null,
            SinceBegan = Counts(calls, execMs),
            Window = Counts(calls, execMs),
            Tables = tables ?? [Orders],
        };

        StatementRowView row = new(queryId, shown ? text : null, null, "g", "g", fact.Window, fact.SinceBegan, null, null, null);
        return (fact, row);
    }

    private static WorkloadStatements Select(string? table, IReadOnlySet<string>? withheld, params (StatementFact Fact, StatementRowView Row)[] statements) =>
        IndexAdvisor.Select([.. statements.Select(static x => x.Fact)], statements.ToDictionary(static x => x.Fact.QueryId, static x => x.Row), withheld, table);

    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    // ---- Which statements -------------------------------------------------------------------------------------------------

    [Fact]
    public void Selection_keeps_only_what_the_drill_in_would_plan_and_show_and_counts_each_drop_by_reason()
    {
        WorkloadStatements selection = Select(
            null,
            new HashSet<string> { "hr" },
            Statement(1, "select * from public.orders where customer_id = $1", execMs: 900),
            Statement(2, "insert into public.orders values ($1)"),
            Statement(3, "update public.orders set a = $1"),
            Statement(4, "vacuum public.orders"),
            Statement(5, "select * from public.orders where id = $1 for update"),
            Statement(6, "select 1; select 2"),
            Statement(7, "select * from public.orders where note = 'cut", truncated: true),
            Statement(8, "select * from public.orders", shown: false),
            Statement(9, "select * from hr.salaries"),
            Statement(10, "select now()", tables: []),
            Statement(11, "select * from public.orders where a = $1", calls: 0),
            Statement(12, "select * from public.orders where b = $1", execMs: 800));

        selection.Kept.Select(static x => x.QueryId).Should().Equal([1L, 12L], "in the page's order: the most execution time first");
        selection.Skipped.Should().Equal(
            "2 writes",
            "1 utility statement",
            "1 locking read",
            "1 statement the console's guard refuses",
            "1 cut text",
            "2 statements not shown to you",
            "1 statement the checks could not tie to a table",
            "1 statement not called in the window");
    }

    [Fact]
    public void A_visitor_whose_gate_could_not_be_read_is_shown_no_statement_and_nothing_is_planned()
    {
        WorkloadStatements selection = Select(null, null, Statement(1, "select * from public.orders"));

        selection.Kept.Should().BeEmpty();
        selection.Skipped.Should().Equal("1 statement not shown to you");
    }

    [Fact]
    public void Selection_stops_at_twenty_statements_the_most_execution_time_first()
    {
        (StatementFact, StatementRowView)[] many = [.. Enumerable.Range(1, 30).Select(static i => Statement(i, "select * from public.orders where a = $1", execMs: i))];

        WorkloadStatements selection = Select(null, Nothing, many);

        selection.Kept.Should().HaveCount(IndexAdvisor.MaxStatements);
        selection.Kept.Select(static x => x.QueryId).Should().Equal(Enumerable.Range(11, 20).Reverse().Select(static x => (long) x));
    }

    [Fact]
    public void A_request_naming_a_table_counts_only_the_statements_that_name_it()
    {
        WorkloadStatements selection = Select(
            Lines.Key,
            Nothing,
            Statement(1, "select * from public.orders", tables: [Orders]),
            Statement(2, "select * from public.order_lines", tables: [Lines]),
            Statement(3, "insert into public.order_lines values ($1)", tables: [Lines]),
            Statement(4, "select * from public.orders o join public.order_lines l on l.order_id = o.id", tables: [Orders, Lines]));

        selection.Kept.Select(static x => x.QueryId).Should().Equal(2L, 4L);
        selection.Skipped.Should().Equal("1 write");
    }

    [Fact]
    public void A_table_nobody_names_keeps_nothing()
    {
        Select("public.nowhere", Nothing, Statement(1, "select * from public.orders")).Kept.Should().BeEmpty();
    }

    private static readonly StudioTable Salaries = new("hr", "salaries", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["hr"] };

    [Fact]
    public void A_guessed_table_in_a_schema_the_visitor_may_not_see_answers_as_a_table_nobody_uses()
    {
        HashSet<string> withheld = ["hr"];
        (StatementFact, StatementRowView)[] statements =
        [
            Statement(1, "select * from hr.salaries where who = $1", tables: [Salaries]),
            Statement(2, "select * from hr.salaries where who = $1 for update", tables: [Salaries], shown: false),
            Statement(3, "insert into salaries values ($1)", tables: [Salaries]),
            Statement(4, "vacuum salaries", tables: [Salaries]),
            Statement(5, "select * from public.orders", tables: [Orders]),
        ];

        WorkloadStatements guessed = Select(Salaries.Key, withheld, statements);
        WorkloadStatements nowhere = Select("hr.nowhere", withheld, statements);

        guessed.Kept.Should().BeEmpty();
        guessed.Skipped.Should().BeEmpty("nothing about a statement that names a table the visitor may not see is counted");
        guessed.Kept.Should().Equal(nowhere.Kept);
        guessed.Skipped.Should().Equal(nowhere.Skipped);

        // Asking about every table still counts what the visitor may see counted, as it always did.
        Select(null, withheld, statements).Skipped.Should().NotBeEmpty();
    }

    [Fact]
    public void A_guessed_table_whose_gate_could_not_be_read_answers_the_same_way()
    {
        (StatementFact, StatementRowView)[] statements = [Statement(1, "insert into salaries values ($1)", tables: [Salaries])];

        Select(Salaries.Key, null, statements).Skipped.Should().BeEmpty();
        Select("hr.nowhere", null, statements).Skipped.Should().BeEmpty();
    }

    [Fact]
    public void A_statement_with_no_window_is_ranked_by_its_own_counters_not_last()
    {
        StatementFact fresh = new()
        {
            QueryId = 1,
            Text = "select * from public.orders where a = $1",
            TextRead = true,
            SinceBegan = Counts(10, 900),
            Window = null,
            Tables = [Orders],
        };
        (StatementFact other, StatementRowView otherRow) = Statement(2, "select * from public.orders where b = $1", execMs: 100);
        StatementRowView freshRow = new(1, fresh.Text, null, "g", "g", null, fresh.SinceBegan, null, null, null);

        WorkloadStatements selection = IndexAdvisor.Select([other, fresh], new Dictionary<long, StatementRowView> { [1] = freshRow, [2] = otherRow }, Nothing, null, watched: true);

        selection.Kept.Select(static x => x.QueryId).Should().Equal(1L, 2L);
        selection.Ranking.Should().Be(IndexAdvisor.RankedByWindow);
        Select(null, Nothing, Statement(1, "select * from public.orders")).Ranking.Should().Be(IndexAdvisor.RankedSinceBegan);
    }

    // ---- Which indexes ----------------------------------------------------------------------------------------------------

    private static HypotheticalIndex Index(string table, params string[] columns) =>
        PlanWhatIfCandidates.Candidate("public", table, columns)!;

    private static CandidateSource Source(int statement, double calls, double cost, params HypotheticalIndex[] offered) =>
        new(statement, calls, Plan(SeqScan("public", offered.Length > 0 ? offered[0].Table : "orders", "(a = 1)", cost)), offered);

    [Fact]
    public void The_same_index_suggested_by_two_statements_is_one_and_their_potential_adds_up()
    {
        HypotheticalIndex a = Index("orders", "customer_id");
        HypotheticalIndex b = Index("order_lines", "order_id");

        IReadOnlyList<AdvisedCandidate> merged = IndexAdvisor.Merge(
            [Source(0, 10, 100, a), Source(1, 5, 100, a), Source(2, 1000, 50, b)],
            []);

        merged.Should().HaveCount(2);
        merged[0].Index.Definition.Should().Be(b.Definition, "1000 calls x 50 beats (10 + 5) calls x 100");
        merged[0].Potential.Should().Be(50_000);
        merged[1].Index.Definition.Should().Be(a.Definition);
        merged[1].Potential.Should().Be(1_500);
        merged.Should().OnlyContain(static x => !x.Own);
    }

    [Fact]
    public void Equal_potential_prefers_fewer_columns_then_the_key()
    {
        HypotheticalIndex wide = Index("orders", "a", "b");
        HypotheticalIndex narrow = Index("orders", "b");

        IndexAdvisor.Merge([Source(0, 10, 100, wide, narrow)], []).Select(static x => x.Index.Columns.Count).Should().Equal(1, 2);
    }

    [Fact]
    public void The_visitors_own_index_is_first_flagged_and_never_counted_by_the_cap()
    {
        HypotheticalIndex mine = Index("orders", "status") with { Key = "visitor:" + Index("orders", "status").Key };
        HypotheticalIndex[] others = [.. Enumerable.Range(0, 15).Select(static i => Index("orders", "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))];

        IReadOnlyList<AdvisedCandidate> merged = IndexAdvisor.Merge([Source(0, 10, 100, others)], [mine]);
        merged[0].Own.Should().BeTrue();
        merged[0].Index.Definition.Should().Be(mine.Definition);

        IReadOnlyList<AdvisedCandidate> capped = IndexAdvisor.Cap(merged, out int notTried);

        capped.Should().HaveCount(IndexAdvisor.MaxCandidates + 1, "twelve of the plans' own, and the visitor's");
        notTried.Should().Be(3);
    }

    [Fact]
    public void A_visitors_index_a_plan_suggested_already_is_that_one_flagged()
    {
        HypotheticalIndex suggested = Index("orders", "customer_id");

        IReadOnlyList<AdvisedCandidate> merged = IndexAdvisor.Merge([Source(0, 10, 100, suggested)], [suggested with { Key = "visitor:x" }]);

        merged.Should().ContainSingle().Which.Should().Match<AdvisedCandidate>(static x => x.Own && x.Potential == 1_000);
    }

    // ---- The trials -------------------------------------------------------------------------------------------------------

    private static AdvisedStatement Advised(long queryId, string table, double cost = 100) =>
        new(queryId, "select 1", false, 1, cost, Plan(SeqScan("public", table, "(a = 1)", cost)), "batch");

    [Fact]
    public void A_trial_plans_again_only_the_statements_whose_plan_reads_its_table()
    {
        AdvisedStatement[] statements = [Advised(1, "orders"), Advised(2, "order_lines"), Advised(3, "orders")];

        IReadOnlyList<HypotheticalIndexTrial> trials = IndexAdvisor.Trials(
            [new AdvisedCandidate(Index("orders", "a"), 0, false), new AdvisedCandidate(Index("order_lines", "b"), 0, false)],
            statements,
            out int over);

        over.Should().Be(0);
        trials.Select(static x => x.Statements).Should().SatisfyRespectively(
            first => first.Should().Equal(0, 2),
            second => second.Should().Equal(1));
    }

    [Fact]
    public void The_plan_bound_ends_the_list_at_the_first_trial_that_would_not_fit_and_counts_the_rest()
    {
        AdvisedStatement[] statements = [.. Enumerable.Range(0, 20).Select(static i => Advised(i, "orders"))];
        AdvisedCandidate[] candidates = [.. Enumerable.Range(0, 10).Select(static i => new AdvisedCandidate(Index("orders", "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), 0, false))];

        IReadOnlyList<HypotheticalIndexTrial> trials = IndexAdvisor.Trials(candidates, statements, out int over);

        trials.Should().HaveCount(IndexAdvisor.MaxTrialPlans / 20, "seven trials of twenty plans are 140; an eighth would be 160");
        over.Should().Be(3);
        trials.Sum(static x => x.Statements.Count).Should().BeLessThanOrEqualTo(IndexAdvisor.MaxTrialPlans);
    }

    // ---- Ranking ----------------------------------------------------------------------------------------------------------

    private static AdvisedStatement Costed(long queryId, double calls, double before) =>
        new(queryId, "select 1", false, calls, before, Plan(SeqScan("public", "orders", "(a = 1)", before)), "batch");

    private static IndexTrialResult Result(HypotheticalIndex index, params (int Statement, double? After, bool Used)[] compared) =>
        new(new AdvisedCandidate(index, 0, false), null, [.. compared.Select(static x => new IndexTrialStatement(x.Statement, x.After, x.Used))]);

    [Fact]
    public void An_index_helps_a_statement_only_when_it_is_used_and_costs_at_least_ten_percent_less()
    {
        AdvisedStatement[] statements = [Costed(1, 10, 1000), Costed(2, 10, 1000), Costed(3, 10, 1000), Costed(4, 10, 1000)];
        HypotheticalIndex index = Index("orders", "a");

        IndexRanking ranking = IndexAdvisor.Rank(
            statements,
            [Result(index, (0, 900, true), (1, 910, true), (2, 100, false), (3, null, false))],
            static _ => null);

        IndexAdviceItem item = ranking.Recommended.Should().ContainSingle().Subject;
        item.Helped.Should().Be(1, "a 10% drop helps, a 9% one does not, an index the plan does not read does not, and a plan that could not be compared does not");
        item.Saved.Should().Be(10 * 100, "calls x (before - after)");
        item.Share.Should().BeApproximately(1000.0 / 40_000, 1e-9, "against every tried statement's calls x cost now");
        item.Statements.Should().HaveCount(4);
        ranking.NotUsed.Should().BeEmpty();
    }

    [Fact]
    public void An_index_that_helps_nothing_is_tried_not_used()
    {
        IndexRanking ranking = IndexAdvisor.Rank([Costed(1, 10, 1000)], [Result(Index("orders", "a"), (0, 990, true))], static _ => null);

        ranking.Recommended.Should().BeEmpty();
        ranking.NotUsed.Should().ContainSingle();
    }

    [Fact]
    public void Recommendations_are_ordered_by_what_they_save_weighted_by_calls()
    {
        AdvisedStatement[] statements = [Costed(1, 1, 1000), Costed(2, 100, 100)];

        IndexRanking ranking = IndexAdvisor.Rank(
            statements,
            [
                Result(Index("orders", "a"), (0, 100, true)),
                Result(Index("orders", "b"), (1, 10, true)),
            ],
            static _ => null);

        ranking.Recommended.Select(static x => x.Index.Columns[0]).Should().Equal("b", "a");
        ranking.Recommended[0].Saved.Should().Be(100 * 90);
        ranking.Recommended[1].Saved.Should().Be(900);
    }

    [Fact]
    public void A_trial_postgres_refused_is_listed_as_not_tried_in_the_studios_words()
    {
        HypotheticalIndex index = Index("orders", "a");

        IndexRanking ranking = IndexAdvisor.Rank([Costed(1, 1, 100)], [new IndexTrialResult(new AdvisedCandidate(index, 0, false), "XX000", [])], static _ => null);

        ranking.NotTried.Should().ContainSingle().Which.Should().StartWith(index.Caption + ": Not compared: Postgres refused the what-if (SQLSTATE XX000)");
        ranking.Recommended.Should().BeEmpty();
    }

    [Fact]
    public void A_longer_index_that_saves_under_one_point_one_times_as_much_is_folded_into_the_one_it_extends()
    {
        AdvisedStatement[] statements = [Costed(1, 10, 1000)];

        IndexRanking ranking = IndexAdvisor.Rank(
            statements,
            [Result(Index("orders", "a"), (0, 100, true)), Result(Index("orders", "a", "b"), (0, 95, true))],
            static _ => null);

        IndexAdviceItem kept = ranking.Recommended.Should().ContainSingle().Subject;
        kept.Index.Columns.Should().Equal("a");
        kept.FoldedNote.Should().Be("Also tried (a, b), no better.");
        ranking.NotUsed.Should().BeEmpty("a folded index did help; it is not unused");
    }

    [Fact]
    public void A_longer_index_that_saves_a_good_deal_more_is_kept_beside_the_one_it_extends_and_says_so()
    {
        AdvisedStatement[] statements = [Costed(1, 10, 1000)];

        IndexRanking ranking = IndexAdvisor.Rank(
            statements,
            [Result(Index("orders", "a"), (0, 800, true)), Result(Index("orders", "a", "b"), (0, 100, true))],
            static _ => null);

        ranking.Recommended.Select(static x => x.Index.Columns.Count).Should().Equal(2, 1);
        ranking.Recommended[0].FoldedNote.Should().Be("Extends (a).");
        ranking.Recommended[1].FoldedNote.Should().BeNull();
    }

    [Fact]
    public void The_visitors_own_index_is_never_folded_away()
    {
        AdvisedStatement[] statements = [Costed(1, 10, 1000)];
        AdvisedCandidate mine = new(Index("orders", "a", "b"), 0, true);

        IndexRanking ranking = IndexAdvisor.Rank(
            statements,
            [Result(Index("orders", "a"), (0, 100, true)), new IndexTrialResult(mine, null, [new IndexTrialStatement(0, 95, true)])],
            static _ => null);

        ranking.Recommended.Should().HaveCount(2);
        ranking.Recommended.Should().Contain(static x => x.Own);
    }

    [Fact]
    public void The_writes_that_would_maintain_the_index_come_from_the_catalog_read()
    {
        IndexRanking ranking = IndexAdvisor.Rank([Costed(1, 10, 1000)], [Result(Index("orders", "a"), (0, 100, true))], static _ => 1_204_331);

        IndexAdvisor.Writes(ranking.Recommended[0]).Should().Be("Writes that would maintain it: 1,204,331 inserts and updates on public.orders since the counters began.");
    }

    // ---- What to do about it ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_Marten_does_not_own_is_created_concurrently_and_one_it_does_is_left_to_the_note()
    {
        IndexAdvisor.Remedy(Index("orders", "customer_id")).Should().Be(("CREATE INDEX CONCURRENTLY ON \"public\".\"orders\" (\"customer_id\");", null));

        (string? remedy, string? note) = IndexAdvisor.Remedy(Index("mt_doc_order", "x"));
        remedy.Should().BeNull();
        note.Should().Be(HypotheticalIndexCandidates.MartenTableNote);
    }

    [Fact]
    public void A_Marten_index_is_its_StoreOptions_line_and_never_SQL()
    {
        HypotheticalIndex marten = new()
        {
            Key = "marten:x",
            Definition = "CREATE INDEX mt_doc_order_idx_status ON public.mt_doc_order USING btree ((data ->> 'Status'));",
            Caption = "The index Marten would create for options.Schema.For<Order>().Index(x => x.Status);",
            Origin = HypotheticalIndexOrigin.Marten,
            Schema = "public",
            Table = "mt_doc_order",
            Note = "Declare it in StoreOptions.",
            Line = "options.Schema.For<Order>().Index(x => x.Status);",
        };

        IndexAdvisor.Remedy(marten).Should().Be(("options.Schema.For<Order>().Index(x => x.Status);", "Declare it in StoreOptions."));
    }

    // ---- The visitor's own columns ----------------------------------------------------------------------------------------

    [Fact]
    public void The_columns_offered_are_the_ones_the_plans_compare_on_the_table_first_seen_first()
    {
        QueryPlan filtered = Plan(SeqScan("public", "orders", "((orders.status = 'x'::text) AND (orders.total > 10))", 100));
        QueryPlan joined = Plan(
            """
            [{"Plan": {"Node Type": "Nested Loop", "Total Cost": 50, "Join Filter": "(l.order_id = o.customer_id)",
              "Plans": [
                {"Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "orders", "Schema": "public", "Alias": "o", "Total Cost": 10},
                {"Node Type": "Seq Scan", "Parent Relationship": "Inner", "Relation Name": "order_lines", "Schema": "public", "Alias": "l", "Total Cost": 10}]}}]
            """);

        IndexAdvisor.ComparedColumns([filtered, joined], "public", "orders").Should().Equal("status", "total", "customer_id");
        IndexAdvisor.ComparedColumns([joined], "public", "order_lines").Should().Equal("order_id");
        IndexAdvisor.ComparedColumns([filtered], "public", "order_lines").Should().BeEmpty();
        IndexAdvisor.ComparedColumns([filtered], "other", "orders").Should().BeEmpty("the table is found by the schema and name a plan prints");
    }

    // ---- Words ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.38, "Add this index: saves ~38% of the planned cost of the 20 statements tried, weighted by calls, across 4 statements.")]
    [InlineData(0.054, "Add this index: saves ~5.4% of the planned cost of the 20 statements tried, weighted by calls, across 4 statements.")]
    [InlineData(0.004, "Add this index: saves under 1% of the planned cost of the 20 statements tried, weighted by calls, across 4 statements.")]
    public void The_headline_says_what_the_index_saves_across_how_many_statements(double share, string expected)
    {
        IndexAdviceItem item = new() { Index = Index("orders", "a"), TableKey = "public.orders", Share = share, Helped = 4 };

        IndexAdvisor.Headline(item, 20).Should().Be(expected);
    }

    [Fact]
    public void One_statement_is_singular()
    {
        IndexAdviceItem item = new() { Index = Index("orders", "a"), TableKey = "public.orders", Share = 0.5, Helped = 1 };

        IndexAdvisor.Headline(item, 1).Should().Be("Add this index: saves ~50% of the planned cost of the 1 statement tried, weighted by calls, across 1 statement.");
    }

    [Fact]
    public void The_caveat_says_what_the_numbers_are_not()
    {
        IndexAdvisor.Caveat.Should().Contain("Planner estimates, not measurements").And.Contain("not time").And.Contain("generic plan")
            .And.Contain("Every insert and update maintains every index").And.Contain("never planned").And.Contain("statements another role ran are never among them");
    }

    [Fact]
    public void The_flights_refuse_a_second_run_on_the_same_database_and_let_the_first_release_it()
    {
        IndexAdvisorFlights flights = new();

        flights.TryEnter("default\u001fdb").Should().BeTrue();
        flights.TryEnter("default\u001fdb").Should().BeFalse("refused, never joined");
        flights.TryEnter("default\u001fother").Should().BeTrue();

        flights.Exit("default\u001fdb");

        flights.TryEnter("default\u001fdb").Should().BeTrue();
        flights.Count.Should().Be(2);
    }
}
