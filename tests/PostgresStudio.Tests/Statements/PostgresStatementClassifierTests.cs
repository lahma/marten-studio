using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// Postgres Studio's statement groups (W5-Q1): the verb on <c>SqlLexer</c>'s tokens - a comment or a literal is never a verb,
/// and a <c>with</c> is grouped by the statement it is for - and the first table of the judged set the text names.
/// </summary>
public class PostgresStatementClassifierTests
{
    [Theory]
    [InlineData("select o.id from public.orders o where o.id = $1", "select|public.orders", "select · public.orders")]
    [InlineData("SELECT * FROM orders WHERE id = $1", "select|public.orders", "select · public.orders")]
    [InlineData("insert into public.orders (id) values ($1)", "insert|public.orders", "insert · public.orders")]
    [InlineData("update \"public\".\"orders\" set total = $1 where id = $2", "update|public.orders", "update · public.orders")]
    [InlineData("delete from hr.salaries where id = $1", "delete|hr.salaries", "delete · hr.salaries")]
    [InlineData("/* select from public.orders */ begin", "begin|", "begin · other")]
    [InlineData("select 1", "select|", "select · other")]
    [InlineData("select * from pg_catalog.pg_class", "select|", "select · other")]
    [InlineData("with recent as (select * from public.orders) insert into hr.salaries select * from recent", "insert|public.orders", "insert · public.orders")]
    [InlineData("with recent as (select 1), more as (select 2) select * from recent", "select|", "select · other")]
    public void A_statement_is_grouped_by_its_verb_and_its_first_table_of_the_set(string text, string key, string label)
    {
        StatementGroup group = PostgresStatementClassifier.Instance.Classify(text, StatementsTestFacts.Tables)!;

        group.Key.Should().Be(key);
        group.Label.Should().Be(label);
    }

    [Fact]
    public void A_group_that_names_a_table_carries_it_so_the_page_can_gate_its_schema()
    {
        StatementGroup group = PostgresStatementClassifier.Instance.Classify("select * from hr.salaries", StatementsTestFacts.Tables)!;

        group.Table.Should().Be(Salaries);
        group.Verb.Should().Be("select");
        group.Table!.GatedSchemas.Should().Equal("hr");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("( select 1 )")]
    [InlineData("'not closed")]
    public void A_text_that_does_not_start_with_a_word_has_no_group(string text)
    {
        PostgresStatementClassifier.Instance.Classify(text, StatementsTestFacts.Tables).Should().BeNull();
    }
}
