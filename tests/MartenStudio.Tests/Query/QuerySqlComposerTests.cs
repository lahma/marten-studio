using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The statement behind a Marten <c>where</c> clause.
/// </summary>
/// <remarks>
/// These are the rules Marten's own <c>UserSuppliedQueryHandler</c> applies, read from the 9.35 assembly:
/// a clause starting with <c>select</c> is the whole statement, <c>where</c> and <c>order</c> are appended
/// after a space, and anything else gets <c>where</c> put in front of it unless it already contains one.
/// The studio shows the person the SQL behind their clause, so getting this wrong would mean showing them
/// a statement that is not the one that ran.
/// </remarks>
public class QuerySqlComposerTests
{
    private const string Table = "\"studio\".\"mt_doc_person\"";

    [Fact]
    public void A_where_clause_is_appended_to_the_document_select()
    {
        var composed = QuerySqlComposer.Compose(Table, "where data ->> 'Name' = 'Alice'", 50);

        composed.Statement.Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d where data ->> 'Name' = 'Alice' limit 50");
    }

    [Fact]
    public void A_bare_predicate_gets_a_where_in_front_of_it()
    {
        QuerySqlComposer.Statement(Table, "d.data ->> 'Name' = 'Alice'").Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d where d.data ->> 'Name' = 'Alice'");
    }

    [Fact]
    public void An_order_by_is_appended_without_a_where()
    {
        QuerySqlComposer.Statement(Table, "order by d.id desc").Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d order by d.id desc");
    }

    [Fact]
    public void An_empty_clause_selects_the_whole_table()
    {
        QuerySqlComposer.Statement(Table, string.Empty).Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d");
    }

    [Fact]
    public void A_clause_that_is_already_a_select_is_the_whole_statement()
    {
        QuerySqlComposer.Statement(Table, "select count(*) from other").Should().Be("select count(*) from other");
    }

    [Fact]
    public void A_with_followed_by_select_is_the_whole_statement_too()
    {
        const string cte = "with x as (select 1) select * from x";

        QuerySqlComposer.Statement(Table, cte).Should().Be(cte);
    }

    /// <summary>
    /// The row cap is a server-side clamp (plan §4.8), but a clause that already carries a <c>limit</c> is
    /// left alone: appending a second one is a syntax error, and silently changing somebody's own limit
    /// would make the answer a different question from the one they asked.
    /// </summary>
    [Fact]
    public void The_row_cap_is_appended_only_when_the_clause_has_no_limit_of_its_own()
    {
        var capped = QuerySqlComposer.Compose(Table, "where age > 30", 25);

        capped.LimitApplied.Should().BeTrue();
        capped.EffectiveClause.Should().Be("where age > 30 limit 25");

        var own = QuerySqlComposer.Compose(Table, "where age > 30 limit 3", 25);

        own.LimitApplied.Should().BeFalse();
        own.EffectiveClause.Should().Be("where age > 30 limit 3");
        own.Statement.Should().EndWith("limit 3");
    }

    /// <summary>
    /// A bare <c>limit 10</c> would arrive at Postgres as <c>… as d where limit 10</c>, because Marten puts
    /// <c>where</c> in front of anything that does not start with <c>where</c> or <c>order</c>. So an empty
    /// clause becomes an explicit <c>where 1 = 1</c>, and the SQL tab shows the statement that really ran.
    /// </summary>
    [Fact]
    public void An_empty_clause_still_gets_the_row_cap_and_a_where_Marten_will_accept()
    {
        var composed = QuerySqlComposer.Compose(Table, null, 10);

        composed.EffectiveClause.Should().Be("where 1 = 1 limit 10");
        composed.Statement.Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d where 1 = 1 limit 10");
    }

    /// <summary>
    /// <c>limit</c> is not one of the words Marten appends after a space, so neither is it one here: the
    /// composed statement has to be the one that runs, broken or not.
    /// </summary>
    [Fact]
    public void A_clause_that_is_only_a_limit_is_composed_the_way_Marten_composes_it()
    {
        QuerySqlComposer.Statement(Table, "limit 5").Should().Be(
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d where limit 5");
    }

    // ------------------------------------------------------------------------------------------------
    // The clause guard. Mode A needs no capability, so this is the thing that keeps it from being one.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The clause runs through a Marten session rather than the console's read-only transaction, so a
    /// second statement in it would execute. <c>1 = 1; drop table x</c> is one Npgsql command holding two
    /// statements, and Postgres runs both.
    /// </summary>
    [Theory]
    [InlineData("where a = 1; drop table x")]
    [InlineData("a = 1;")]
    [InlineData("where a = 1 /* */; delete from x")]
    [InlineData("where a = 'unterminated")]
    public void A_clause_carrying_a_second_statement_is_refused(string clause)
    {
        SqlRejection? rejection = QuerySqlComposer.RejectionFor(clause);

        rejection.Should().NotBeNull();
        rejection!.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        rejection.Position.Should().BeGreaterThanOrEqualTo(0);
    }

    [Theory]
    [InlineData("select * from pg_authid")]
    [InlineData("  SELECT 1")]
    [InlineData("with x as (select 1) select * from x")]
    [InlineData("delete from mt_doc_person")]
    [InlineData("set role postgres")]
    [InlineData("copy x from '/etc/passwd'")]
    public void A_clause_that_is_a_statement_of_its_own_is_refused(string clause)
    {
        QuerySqlComposer.RejectionFor(clause).Should().NotBeNull(clause);
    }

    [Theory]
    [InlineData("where data ->> 'Name' = 'Alice'")]
    [InlineData("where name = 'it''s; fine'")]
    [InlineData("order by mt_last_modified desc")]
    [InlineData("age > 30 and name like 'A%'")]
    [InlineData("where d.data @> '{\"Name\": \"Alice\"}'")]
    [InlineData(null)]
    [InlineData("")]
    public void An_ordinary_clause_is_not_refused(string? clause)
    {
        QuerySqlComposer.RejectionFor(clause).Should().BeNull(clause ?? "(null)");
    }

    [Theory]
    [InlineData("where name = 'limit'", false)]
    [InlineData("where name = 'a limit b'", false)]
    [InlineData("where name = \"limit\"", false)]
    [InlineData("where x = 1 -- limit 5", false)]
    [InlineData("where x = 1 /* limit 5 */", false)]
    [InlineData("where x = $tag$ limit $tag$", false)]
    [InlineData("where x = 1 limit 5", true)]
    [InlineData("WHERE x = 1 LIMIT 5", true)]
    [InlineData("where x = 1\nlimit 5", true)]
    public void A_limit_is_only_a_limit_outside_strings_and_comments(string clause, bool expected)
    {
        QuerySqlComposer.ContainsKeyword(clause, "limit").Should().Be(expected);
    }

    /// <summary>
    /// A clause with an unterminated string counts as "already has one", because the safe failure is to
    /// add nothing: the statement guard and Postgres both refuse it a moment later anyway.
    /// </summary>
    [Fact]
    public void An_unterminated_string_makes_the_scanner_answer_conservatively()
    {
        QuerySqlComposer.ContainsKeyword("where name = 'unterminated", "limit").Should().BeTrue();
        QuerySqlComposer.ContainsKeyword("where x = 1 /* never closed", "limit").Should().BeTrue();
    }

    /// <summary>
    /// EXPLAIN, never EXPLAIN ANALYZE: ANALYZE <em>runs</em> the statement, and the plan is offered for
    /// clauses somebody is still writing.
    /// </summary>
    [Fact]
    public void The_explain_wrapper_never_analyzes()
    {
        var explained = QuerySqlComposer.Explain("select 1", json: true);

        explained.Should().Be("explain (format json) select 1");
        explained.Should().NotContain("analyze");
    }

    [Fact]
    public void The_composed_statement_quotes_the_table_and_never_the_clause()
    {
        var composed = QuerySqlComposer.Compose("\"s\".\"t\"", "where a = 'x'", 5);

        composed.Statement.Should().Contain("\"s\".\"t\"");
        composed.Statement.Should().Contain("where a = 'x'");
    }
}
