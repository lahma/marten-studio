using Microsoft.Extensions.Options;

using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Query;

/// <summary>
/// MartenStudio's <see cref="IPlanInsightContributor" /> (1.0-P3C): P26 to P28, judged against the document tables the store in the
/// visitor's scope declares (<see cref="MartenPlanInsights" />).
/// </summary>
/// <remarks>
/// <para>
/// <b>The store is learned the way any scope is.</b> The scope on screen goes through <see cref="StudioScopeResolver" />, whose
/// order is authorization first, so a store the visitor may not have answers as one that does not exist - and then the insights
/// are simply not judged, with no word about the store. It is asked for the database as a whole (<c>TenantId = null</c>): what a
/// store declares is the Configuration page's information, not one tenant's.
/// </para>
/// <para>
/// <b>Nothing is read from the database</b> and nothing is touched that ensures storage (hard rule 14): <see cref="MartenPlanTables" />
/// reads the options and the mappings. A plan that reads no table named like a Marten document table is not even resolved.
/// </para>
/// </remarks>
internal sealed class MartenPlanInsightContributor : IPlanInsightContributor
{
    private readonly StudioScopeResolver resolver;
    private readonly IOptions<MartenStudioOptions> options;

    public MartenPlanInsightContributor(StudioScopeResolver resolver, IOptions<MartenStudioOptions> options)
    {
        this.resolver = resolver;
        this.options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<PlanInsightInfo> Insights => MartenPlanInsights.All;

    /// <inheritdoc />
    public async ValueTask<PlanContribution> ContributeAsync(QueryPlan plan, PlanInsightContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        if (!MartenPlanInsights.ReadsDocumentTables(plan))
        {
            return PlanContribution.None;
        }

        IReadOnlyList<MartenPlanTable>? tables = null;

        if (context.Scope is { } scope)
        {
            try
            {
                ResolvedScope resolved = await resolver.ResolveAsync(scope with { TenantId = null }, capability: null, cancellationToken).ConfigureAwait(false);
                tables = MartenPlanTables.Read(resolved.Store, options.Value.IsDocumentTypeVisible);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Refused, not found, will not build, or a mapping that will not read: the insights are not judged, and say no more.
                tables = null;
            }
        }

        return MartenPlanInsights.Evaluate(plan, context.Facts, tables);
    }
}
