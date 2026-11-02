using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The static lint, P22 to P25 (1.0-P3A, issue #28): judged from the statement's text with the studio's own lexer, so nothing in
/// a comment, a string, a quoted identifier or a dollar-quoted body matches, and never from a plan.
/// </summary>
public class PlanStatementLintTests
{
    private static string[] Codes(string statement) => [.. PlanInsights.EvaluateText(statement).Select(static x => x.Insight.Code)];

    [Theory]
    [InlineData("select * from orders where customer_id not in (select id from blocked)")]
    [InlineData("select id from orders where customer_id NOT IN ((select id from blocked))")]
    [InlineData("select id from orders where customer_id not in (with b as (select id from blocked) select id from b)")]
    [InlineData("select id from orders where (a, b) not in (values (1, 2))")]
    public void P22_fires_on_NOT_IN_over_a_subquery(string statement) => Codes(statement).Should().Contain("P22");

    [Theory]
    [InlineData("select id from orders where customer_id not in (1, 2, 3)")]
    [InlineData("select id from orders where not exists (select 1 from blocked b where b.id = orders.customer_id)")]
    [InlineData("select id from orders -- where customer_id not in (select id from blocked)")]
    [InlineData("select id from orders /* not in (select id from blocked) */")]
    [InlineData("select 'not in (select 1)' from orders")]
    [InlineData("select $$ not in (select 1) $$ from orders")]
    [InlineData("select \"not\" in (select 1) from orders")]
    public void P22_stays_quiet_on_lists_NOT_EXISTS_and_text_inside_comments_and_literals(string statement) =>
        Codes(statement).Should().NotContain("P22");

    [Theory]
    [InlineData("select * from orders order by id offset 10000", 10_000L)]
    [InlineData("select * from orders order by id limit 50 offset 250000 rows", 250_000L)]
    [InlineData("select * from orders order by id offset 10_000", 10_000L)]
    public void P23_fires_on_an_OFFSET_literal_of_ten_thousand_or_more(string statement, long offset)
    {
        PlanInsightFinding finding = PlanInsights.EvaluateText(statement).Single(static x => x.Insight.Code == "P23");
        finding.Message.Should().Contain(offset.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("select * from orders order by id offset 9999")]
    [InlineData("select * from orders order by id offset $1")]
    [InlineData("select * from orders order by id offset 10000.5")]
    [InlineData("select * from orders order by id -- offset 100000")]
    [InlineData("select 'offset 100000' from orders")]
    public void P23_stays_quiet_below_the_bound_on_parameters_and_in_comments_and_literals(string statement) =>
        Codes(statement).Should().NotContain("P23");

    [Theory]
    [InlineData("select * from orders o join customers c on c.id = o.customer_id")]
    [InlineData("select * from orders o, customers c where c.id = o.customer_id")]
    [InlineData("SELECT DISTINCT * FROM orders o LEFT JOIN customers c ON c.id = o.customer_id")]
    [InlineData("select * from orders o cross join regions r")]
    public void P24_fires_on_a_top_level_SELECT_star_over_a_join(string statement) => Codes(statement).Should().Contain("P24");

    [Theory]
    [InlineData("select o.* from orders o join customers c on c.id = o.customer_id")]
    [InlineData("select count(*) from orders o join customers c on c.id = o.customer_id")]
    [InlineData("select * from orders where exists (select * from customers c join regions r on r.id = c.region_id)")]
    [InlineData("select * from (select o.id from orders o join customers c on c.id = o.customer_id) s")]
    [InlineData("select * from orders where id = 1")]
    [InlineData("select * from orders o where o.id in (select c.id from customers c, regions r)")]
    public void P24_stays_quiet_on_qualified_stars_counts_subqueries_and_single_tables(string statement) =>
        Codes(statement).Should().NotContain("P24");

    [Theory]
    [InlineData("select * from orders order by random() limit 10")]
    [InlineData("select * from orders ORDER BY pg_catalog.random()")]
    public void P25_fires_on_ORDER_BY_random(string statement) => Codes(statement).Should().Contain("P25");

    [Theory]
    [InlineData("select * from orders order by random_key()")]
    [InlineData("select * from orders order by id, random()")]
    [InlineData("select 'order by random()' from orders")]
    [InlineData("select random() from orders order by id")]
    public void P25_stays_quiet_otherwise(string statement) => Codes(statement).Should().NotContain("P25");

    [Fact]
    public void A_finding_names_where_in_the_statement_it_is_and_is_about_no_node()
    {
        PlanInsightFinding finding = PlanInsights.EvaluateText("select id\nfrom orders\nwhere customer_id not in (select id from blocked)").Single();

        finding.Insight.Should().BeSameAs(PlanInsights.NotInSubquery);
        finding.NodeId.Should().Be(PlanInsights.StatementNodeId);
        finding.Node.Should().Be("The statement at line 3, column 19");
        finding.Message.Should().StartWith("The statement uses NOT IN over a subquery.");
        finding.Remedy.Should().Contain("NOT EXISTS");
    }

    [Fact]
    public void Each_insight_reports_at_most_its_bound()
    {
        string statement = string.Join(" union all ", Enumerable.Repeat("select id from t where x not in (select y from u)", 12));

        PlanInsights.EvaluateText(statement).Should().HaveCount(PlanInsights.MaxPerInsight);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("select 'unterminated")]
    [InlineData("select $$ unterminated")]
    [InlineData("/* never closed")]
    [InlineData("offset")]
    [InlineData("order by")]
    [InlineData("not in (")]
    [InlineData("select *")]
    public void Empty_unreadable_and_truncated_text_finds_nothing_and_never_throws(string? statement) =>
        PlanInsights.EvaluateText(statement).Should().BeEmpty();

    [Fact]
    public void The_lint_is_registered_as_insights_from_the_text_that_are_never_listed_as_not_judged()
    {
        PlanInsights.All.Where(static x => x.FromText).Select(static x => x.Code).Should().Equal("P22", "P23", "P24", "P25");
        PlanInsights.All.Where(static x => x.FromText).Should().OnlyContain(static x => !x.NeedsAnalyze && !x.NeedsFacts && !x.ComparesPlans);

        QueryPlan plain = QueryPlanTextReader.Read("Seq Scan on public.orders  (cost=0.00..1.01 rows=1 width=4)").Plan!;
        PlanInsights.NotJudged(plain).Should().NotContain(static x => x.FromText);
        PlanInsights.NotJudgedWithoutFacts(plain, null).Should().NotContain(static x => x.FromText);
        PlanInsights.Evaluate(plain, PostgresStudio.Services.Settings.StudioText.Marten).Should().NotContain(static x => x.Insight.FromText);
    }
}
