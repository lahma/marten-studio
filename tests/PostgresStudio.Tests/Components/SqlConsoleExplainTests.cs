using Bunit;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The SQL console's Explain buttons (X1, K1): the panel asks the console for a plan or a measured plan of the statement in the
/// editor and draws the plan in place of the grid; the console guards what was typed - an Explain button on a write is refused
/// by the guard, one on an EXPLAIN by the console - and records every refusal as a RunSql run, with nothing sent.
/// </summary>
public class SqlConsoleExplainTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static SqlConsoleResult Plan(string fixture) =>
        new([new SqlResultColumn(ExplainStatements.PlanColumn, "json")], [], false, 500, TimeSpan.FromMilliseconds(3), [], null, null)
        {
            PlanJson = ExplainFixtures.Get(fixture),
        };

    [Fact]
    public async Task Explain_asks_for_a_plan_and_draws_it_in_place_of_the_grid()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        context.SqlConsole.Next = Plan("15-offset-plain");

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select * from t order by id offset 150000 limit 10");
        await panel.InvokeAsync(() => panel.Find(".ms-query-explain").Click());

        panel.WaitForAssertion(() => panel.FindAll(".ms-plan-tree").Should().ContainSingle());
        context.SqlConsole.Runs.Should().ContainSingle().Which.Request.Should().Be(
            new SqlConsoleRequest("select * from t order by id offset 150000 limit 10") { Explain = SqlExplain.Plan });

        panel.Find(".ms-query-tab-active").TextContent.Trim().Should().Be(QueryStats.PlanTab, "a plan is what Explain answers");
        panel.FindAll(".ms-query-grid, .ms-query-export").Should().BeEmpty("the plan replaces the grid's one capped cell of it");
        panel.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Equal("P06");
        panel.Find(".ms-query-editor-ok").TextContent.Should().StartWith("Planned in 3 ms").And.EndWith("nothing ran.");
    }

    [Fact]
    public async Task Explain_analyze_asks_for_a_measured_plan_and_says_it_ran_and_was_rolled_back()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        context.SqlConsole.Next = Plan("18-sort-spill-analyze");

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select * from t order by c");
        await panel.InvokeAsync(() => panel.Find(".ms-query-explain-analyze").Click());

        panel.WaitForAssertion(() => panel.FindAll(".ms-plan-tree").Should().ContainSingle());
        context.SqlConsole.Runs.Single().Request.Explain.Should().Be(SqlExplain.Analyze);
        panel.Find(".ms-query-editor-ok").TextContent.Should().Be("Ran under EXPLAIN ANALYZE in 3 ms, and rolled back.");
        panel.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Equal("P02");
    }

    [Fact]
    public async Task Run_sends_the_statement_as_typed_and_the_plan_tab_says_how_to_get_a_plan()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select 1");
        await panel.InvokeAsync(() => panel.Find(".ms-query-run").Click());

        panel.WaitForAssertion(() => context.SqlConsole.Runs.Should().ContainSingle());
        context.SqlConsole.Runs[0].Request.Explain.Should().Be(SqlExplain.None);

        panel.Find("#ms-query-tab-plan").Click();
        panel.Find(".ms-query-no-plan").TextContent.Should().Be(SqlConsolePanel.NoPlan);
    }

    [Fact]
    public async Task A_postgres_error_in_an_explained_statement_points_at_the_typed_text()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        // "select nope from t": Postgres counts from one in the statement it was sent, the Explain prefix included.
        int sentPosition = ExplainStatements.PlanPrefix.Length + "select ".Length + 1;
        context.SqlConsole.Next = new SqlConsoleResult([], [], false, 500, TimeSpan.Zero, [], new SqlError("42703", "column \"nope\" does not exist", sentPosition, null, null), null);

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select nope from t");
        await panel.InvokeAsync(() => panel.Find(".ms-query-explain").Click());

        panel.WaitForAssertion(() => panel.Find(".ms-query-caret").TextContent.Should().EndWith("       ^"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The console itself: the guard over what was typed, and the audit
    // ---------------------------------------------------------------------------------------------------------------

    private static SqlConsoleService Console(PostgresStudioComponentContext context) =>
        new(
            ActivatorUtilities.CreateInstance<StudioSourceResolver>(context.Services),
            context.Services.GetRequiredService<StudioCapabilityGuard>(),
            context.Services.GetRequiredService<StudioActionLog>(),
            context.Services.GetRequiredService<IStudioSettings>(),
            NullLogger<SqlConsoleService>.Instance,
            context.Services.GetRequiredService<AuthenticationStateProvider>());

    private static IReadOnlyList<StudioActionLogEntry> Ring(PostgresStudioComponentContext context) =>
        context.Services.GetRequiredService<StudioActionLogService>().GetLatest();

    [Fact]
    public async Task Explain_analyze_of_a_write_is_the_guards_refusal_of_what_was_typed_and_a_failed_RunSql_run()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        SqlConsoleResult result = await Console(context).RunSqlAsync(Scope, new SqlConsoleRequest("delete from t where id = 1") { Explain = SqlExplain.Analyze }, Token);

        result.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.DisallowedStatement));
        result.Rejection.Position.Should().Be(0, "the guard read the statement as typed, not behind the prefix");
        result.PlanJson.Should().BeNull();

        StudioActionLogEntry entry = Ring(context).Should().ContainSingle().Subject;
        entry.Action.Should().Be(SqlConsoleService.RunSqlAction);
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Be("explain (analyze, buffers, format json) delete from t where id = 1", "what would have been sent is what is recorded");
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
    }

    [Theory]
    [InlineData("explain select 1")]
    [InlineData("  EXPLAIN (format json) select 1")]
    [InlineData("/* why */ explain analyze select 1")]
    public async Task An_explain_button_on_an_EXPLAIN_is_refused_and_audited(string statement)
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        SqlConsoleResult result = await Console(context).RunSqlAsync(Scope, new SqlConsoleRequest(statement) { Explain = SqlExplain.Plan }, Token);

        result.Rejection!.Reason.Should().Be(SqlConsoleService.AlreadyExplainReason);
        result.Rejection.Message.Should().Be(SqlConsoleService.AlreadyExplainMessage);
        result.Rejection.Position.Should().Be(statement.IndexOf("xplain", StringComparison.OrdinalIgnoreCase) - 1);
        Ring(context).Should().ContainSingle(static x => !x.Succeeded && x.Action == SqlConsoleService.RunSqlAction);
    }

    [Fact]
    public async Task With_RunSql_off_an_explain_is_refused_before_anything_and_names_what_it_would_have_sent()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        Func<Task> run = () => Console(context).RunSqlAsync(Scope, new SqlConsoleRequest("select 1") { Explain = SqlExplain.Analyze }, Token);

        await run.Should().ThrowAsync<StudioCapabilityDeniedException>();
        Ring(context).Should().ContainSingle().Which.Target.Should().Be("explain (analyze, buffers, format json) select 1");
    }

    [Fact]
    public void The_prefixes_are_the_plan_statements_and_wrap_nothing_else()
    {
        ExplainStatements.Wrap("select 1", SqlExplain.None).Should().Be("select 1");
        ExplainStatements.Wrap("select 1", SqlExplain.Plan).Should().Be("explain (format json) select 1");
        ExplainStatements.Wrap("select 1", SqlExplain.Analyze).Should().Be("explain (analyze, buffers, format json) select 1");
        ExplainStatements.IsPlanColumn("QUERY PLAN", "json").Should().BeTrue();
        ExplainStatements.IsPlanColumn("QUERY PLAN", "text").Should().BeFalse("FORMAT TEXT is rows of text, not a plan to draw");
        QueryPlanReader.MaxCharacters.Should().Be(ExplainStatements.MaxPlanCharacters, "the console keeps whole exactly what the reader reads");
    }
}
