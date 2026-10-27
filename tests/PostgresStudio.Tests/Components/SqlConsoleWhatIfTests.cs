using Bunit;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// X2 on the SQL console: under a plan an Explain button drew, the indexes it suggests are offered, and a what-if asks the console
/// for the statement that was planned - not whatever the editor holds by then - and draws hypopg's absence where it is absent.
/// A plan typed as an EXPLAIN offers none: its statement is not one to wrap again.
/// </summary>
public class SqlConsoleWhatIfTests
{
    private static SqlConsoleResult Plan(string fixture) =>
        new([new SqlResultColumn(ExplainStatements.PlanColumn, "json")], [], false, 500, TimeSpan.FromMilliseconds(3), [], null, null)
        {
            PlanJson = ExplainFixtures.Get(fixture),
        };

    [Fact]
    public async Task An_explain_plan_offers_its_index_and_a_what_if_asks_for_the_statement_that_was_planned()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        context.SqlConsole.Next = Plan("17-nestloop-plain");

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select * from u join t on t.id = u.t_id where u.v < 2");
        await panel.InvokeAsync(() => panel.Find(".ms-query-explain").Click());
        panel.WaitForAssertion(() => panel.FindAll(".ms-whatif-candidate").Should().ContainSingle());

        panel.Find(".ms-whatif-definition").TextContent.Should().Be("CREATE INDEX ON \"u\" (\"v\");");

        // The editor moves on; the what-if is still of the statement the plan is of.
        panel.Find("#ms-query-sql").Change("select 1");
        await panel.InvokeAsync(() => panel.Find(".ms-whatif-run").Click());

        panel.WaitForAssertion(() => panel.Find(".ms-whatif-reason").TextContent.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence));
        context.SqlConsole.WhatIfs.Should().ContainSingle().Which.Statement.Should().Be("select * from u join t on t.id = u.t_id where u.v < 2");
    }

    [Fact]
    public async Task A_plan_typed_as_an_EXPLAIN_and_a_run_offer_no_what_if()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        context.SqlConsole.Next = Plan("17-nestloop-plain");

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("explain (format json) select * from u join t on t.id = u.t_id where u.v < 2");
        await panel.InvokeAsync(() => panel.Find(".ms-query-run").Click());

        panel.WaitForAssertion(() => panel.FindAll(".ms-plan-tree").Should().ContainSingle());
        panel.FindAll(".ms-whatif").Should().BeEmpty("a typed EXPLAIN's statement is not one the what-if can plan again");
    }
}
