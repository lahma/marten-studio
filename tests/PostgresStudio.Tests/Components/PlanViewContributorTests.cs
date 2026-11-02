using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.0-P3C: the seam a studio's own plan insights come through (<see cref="IPlanInsightContributor" />). The plan view asks whatever is
/// registered once, merges the findings with its own, lists what the contributor could not judge, and survives a contributor that throws.
/// </summary>
public class PlanViewContributorTests
{
    private static readonly PlanInsightInfo Mine = new("P90", "A studio's own insight", HealthSeverity.Warning, false);

    private static readonly PlanInsightInfo Other = new("P91", "Another of its insights", HealthSeverity.Info, true);

    private sealed class Fake(Func<QueryPlan, PlanContribution> contribute) : IPlanInsightContributor
    {
        public int Calls { get; private set; }

        public IReadOnlyList<PlanInsightInfo> Insights { get; } = [Mine, Other];

        public ValueTask<PlanContribution> ContributeAsync(QueryPlan plan, PlanInsightContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(contribute(plan));
        }
    }

    private static async Task<(PostgresStudioComponentContext Context, IRenderedComponent<PlanView> View)> RenderAsync(IPlanInsightContributor? contributor, string fixture = "14-nestloop-plain")
    {
        PostgresStudioComponentContext context = await new PostgresStudioComponentContext(services =>
        {
            if (contributor is not null)
            {
                services.AddSingleton(contributor);
            }
        }).ReadyAsync();

        IRenderedComponent<PlanView> view = context.Render<PlanView>(parameters => parameters.Add(x => x.Json, ExplainFixtures.Get(fixture)));

        if (contributor is not null)
        {
            view.WaitForAssertion(() => view.Markup.Should().Contain("P90"));
        }

        return (context, view);
    }

    [Fact]
    public async Task A_contributors_findings_are_listed_with_the_plans_own_and_what_it_could_not_judge_is_named()
    {
        Fake contributor = new(static plan => new PlanContribution(
            [new PlanInsightFinding(Mine, plan.Root.Id, plan.Root.Description, "It found something.", "Declare it.")],
            [Other]));

        (PostgresStudioComponentContext context, IRenderedComponent<PlanView> view) = await RenderAsync(contributor);
        await using (context)
        {
            view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P05").And.Contain("P90");
            view.Markup.Should().Contain("It found something.").And.Contain("Declare it.");
            view.Find(".ms-plan-not-judged-here").TextContent.Should().Contain("P91 Another of its insights");

            // Its insight that needs ANALYZE is listed with the others that do, since this plan is a plain one.
            view.Find(".ms-plan-not-judged").TextContent.Should().Contain("P91");
            contributor.Calls.Should().Be(1, "once for this plan, however often the view renders");
        }
    }

    [Fact]
    public async Task A_contributor_that_throws_costs_the_view_nothing_and_its_insights_are_listed_as_not_judged()
    {
        Fake contributor = new(static _ => throw new InvalidOperationException("the store would not read"));

        (PostgresStudioComponentContext context, IRenderedComponent<PlanView> view) = await RenderAsync(contributor);
        await using (context)
        {
            view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P05");
            view.Find(".ms-plan-not-judged-here").TextContent.Should().Contain("P90").And.Contain("P91");
            view.Markup.Should().NotContain("the store would not read");
        }
    }

    [Fact]
    public async Task Without_a_contributor_the_view_is_what_it_was()
    {
        (PostgresStudioComponentContext context, IRenderedComponent<PlanView> view) = await RenderAsync(null);
        await using (context)
        {
            view.FindAll(".ms-plan-not-judged-here").Should().BeEmpty();
            view.FindAll(".ms-plan-insight-code").Select(static x => x.TextContent).Should().Contain("P05");
        }
    }
}
