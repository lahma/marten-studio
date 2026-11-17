using Bunit;

using DataStudio.Marten.Tests.Components;

using DataStudio.Postgres.Components.Pages.Query;
using DataStudio.Postgres.Services.Query;

namespace DataStudio.Marten.Tests.Query;

/// <summary>
/// The Plan page under Data Studio's own shell wiring (X3, issue #28): the page is the Postgres module's, one copy of it, and
/// Data Studio gets it at its own route with its own words. A smoke test - the page's behaviour is the Postgres module's tests'.
/// </summary>
public class PlanPageMartenTests
{
    private const string Plan = """
        [{"Plan": {"Node Type": "Seq Scan", "Relation Name": "mt_doc_person", "Alias": "d", "Startup Cost": 0.0, "Total Cost": 12.5, "Plan Rows": 100, "Plan Width": 32,
          "Actual Startup Time": 0.01, "Actual Total Time": 0.9, "Actual Rows": 100.00, "Actual Loops": 1}, "Planning Time": 0.1, "Triggers": [], "Execution Time": 1.2}]
        """;

    /// <summary>The Plan page's facts read (0.10-A): the page is smoke-tested here, so it reads no relation.</summary>
    private sealed class NoFacts : IPlanFactsService
    {
        public Task<PlanFacts> ReadAsync(DataStudio.Postgres.Services.StudioScope scope, QueryPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlanFacts(DateTimeOffset.UnixEpoch, [], []));
    }

    private sealed class Services(FakeQueryService instance) : IServiceProvider
    {
        private readonly NoFacts facts = new();

        public object? GetService(Type requested) =>
            requested == typeof(DataStudio.Postgres.Services.Sources.ISqlConsoleService) ? instance
            : requested == typeof(IPlanFactsService) ? facts
            : null;
    }

    private static StudioComponentContext CreateContext(FakeQueryService service)
    {
        StudioComponentContext context = new();
        context.WithStores("default");
        context.Options.Capabilities.RunSql = true;
        context.Services.AddFallbackServiceProvider(new Services(service));

        return context;
    }

    [Fact]
    public void The_page_renders_under_Marten_Studios_shell_asks_the_console_and_draws_the_plan_in_Marten_Studios_words()
    {
        FakeQueryService service = new() { PlanResult = new PlanRunResult { PlanJson = Plan, Duration = TimeSpan.FromMilliseconds(3), Options = "format json", ServerMajor = 17 } };
        using StudioComponentContext context = CreateContext(service);

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find(".ms-page-title").TextContent.Should().Be("Plan");
        page.Find("#ms-plan-sql").Should().NotBeNull();

        page.Find("#ms-plan-sql").Change("select * from mt_doc_person");
        page.Find(".ms-query-run").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-plan-tree").Should().ContainSingle());
        page.Find(".ms-plan-strip").TextContent.Should().Contain("1.2 ms");
        page.Find(".ms-plan-caption").TextContent.Should().Contain("Planned with EXPLAIN (FORMAT JSON)");
    }

    [Fact]
    public void Without_RunSql_the_disabled_state_names_Marten_Studios_own_option()
    {
        using StudioComponentContext context = new();
        context.WithStores("default");
        context.Options.Capabilities.RunSql = false;
        context.Services.AddFallbackServiceProvider(new Services(new FakeQueryService()));

        IRenderedComponent<PlanPage> page = context.Render<PlanPage>();

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("MartenOptions.Capabilities.RunSql");
        page.FindAll("#ms-plan-sql").Should().BeEmpty();
    }
}
