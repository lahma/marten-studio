using MartenStudio.Services.Query;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// 1.0-P3C: Marten Studio's plan insights on a real store. A real document query is explained through the Plan page's own service
/// (<c>ISqlConsoleService.PlanAsync</c>, the console's gate), and the registered contributor - the one a circuit's Plan view asks -
/// judges the plan: P26 fires while the store declares nothing for the member, and goes quiet once the store declares
/// <c>Duplicate(...)</c> for it. Run at the Marten floor and at the latest-tested Marten.
/// </summary>
public class MartenPlanInsightsLiveTests(PostgresFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(QueryPlan Plan, PlanContribution Contribution)> ExplainAsync(QueryHarness harness, PlanMode mode)
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);
        string table = types.Single(static x => x.TypeName == nameof(QueryTag)).QualifiedTableName;

        PlanRunResult result = await harness.Console.PlanAsync(
            harness.Scope,
            new PlanRunRequest("select data from " + table + " where data ->> 'Label' = 'Red'", mode) { Options = PlanOptions.PageDefault },
            Token);

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);

        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        IPlanInsightContributor contributor = harness.Services.GetServices<IPlanInsightContributor>().Single();

        return (plan, await contributor.ContributeAsync(plan, new PlanInsightContext(harness.Scope, null), Token));
    }

    [PostgresFact]
    public async Task P26_fires_on_a_real_JSON_filter_and_goes_quiet_once_the_store_declares_Duplicate()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await using (QueryHarness bare = await QueryHarness.CreateAsync(fixture, "planinsightsbare"))
        {
            (QueryPlan plan, PlanContribution contribution) = await ExplainAsync(bare, PlanMode.Analyze);

            plan.Nodes.Should().Contain(static x => x.NodeType == "Seq Scan" && x.RelationName == "mt_doc_querytag", "a two-row table is read whole");

            PlanInsightFinding finding = contribution.Findings.Should().ContainSingle(static x => x.Insight.Code == "P26").Subject;
            finding.Remedy.Should().Contain("options.Schema.For<QueryTag>().Duplicate(x => x.Label);");
            contribution.NotJudged.Should().BeEmpty();
        }

        await using QueryHarness declared = await QueryHarness.CreateAsync(
            fixture, "planinsightsdeclared", configureStore: static options => options.Schema.For<QueryTag>().Duplicate(x => x.Label));

        (_, PlanContribution quiet) = await ExplainAsync(declared, PlanMode.Analyze);

        quiet.Findings.Should().NotContain(static x => x.Insight.Code == "P26", "the store now declares a duplicated field with its index for the member");
    }

    [PostgresFact]
    public async Task A_plain_plan_is_judged_too_and_a_hidden_document_type_is_never_named()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await using QueryHarness bare = await QueryHarness.CreateAsync(fixture, "planinsightsplain");

        (_, PlanContribution plain) = await ExplainAsync(bare, PlanMode.Plan);

        plain.Findings.Should().ContainSingle(static x => x.Insight.Code == "P26");

        await using QueryHarness hidden = await QueryHarness.CreateAsync(
            fixture, "planinsightshidden", configure: static options => options.IsDocumentTypeVisible = static type => type != typeof(QueryTag));

        // The type is hidden, so the scan of its table is any table's: nothing about it is judged or named.
        PlanRunResult result = await hidden.Console.PlanAsync(
            hidden.Scope,
            new PlanRunRequest("select data from " + hidden.Schema + ".mt_doc_querytag where data ->> 'Label' = 'Red'", PlanMode.Plan) { Options = PlanOptions.PageDefault },
            Token);
        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        PlanContribution contribution = await hidden.Services.GetServices<IPlanInsightContributor>().Single()
            .ContributeAsync(plan, new PlanInsightContext(hidden.Scope, null), Token);

        contribution.Findings.Should().BeEmpty();
        string.Join(" ", contribution.NotJudged.Select(static x => x.Title)).Should().NotContain("QueryTag");
    }
}
