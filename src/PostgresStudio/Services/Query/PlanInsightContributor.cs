namespace PostgresStudio.Services.Query;

/// <summary>
/// 1.0-P3C: a studio's own plan insights, beside the ones every studio has (<see cref="PlanInsights" />) - the seam MartenStudio
/// registers its document-table insights (P26 to P28) through, in the shape of the health checks' MartenStudio half.
/// </summary>
/// <remarks>
/// <para>
/// PostgresStudio defines the seam and runs whatever is registered (<c>IEnumerable&lt;IPlanInsightContributor&gt;</c>, empty in a
/// host that registered none); it names nothing a contributor knows. The judging stays pure: a contributor reads the plan, the
/// facts beside it and what its studio declares, and reads no database. The only asynchronous step is the one a contributor needs
/// to learn what is declared <em>for this visitor's scope</em> - authorized as the studio authorizes any scope, never from the
/// URL's words alone.
/// </para>
/// <para>
/// A contributor that cannot judge a plan (it is not one of its tables, the scope is not one the visitor may have, the declarations
/// would not read) returns what it could not judge as <see cref="PlanContribution.NotJudged" /> - the insights' names only, never a
/// word about why.
/// </para>
/// </remarks>
internal interface IPlanInsightContributor
{
    /// <summary>The insights it can raise, in code order - for the codes table, the docs and the tests.</summary>
    IReadOnlyList<PlanInsightInfo> Insights { get; }

    /// <summary>The findings it makes on <paramref name="plan" />, and the insights of its own it could not judge.</summary>
    /// <param name="plan">The plan on screen.</param>
    /// <param name="context">What the view knows beside it.</param>
    /// <param name="cancellationToken">Cancels the lookup of what the studio declares.</param>
    ValueTask<PlanContribution> ContributeAsync(QueryPlan plan, PlanInsightContext context, CancellationToken cancellationToken);
}

/// <summary>What the plan view knows beside a plan, for a contributor.</summary>
/// <param name="Scope">The scope the visitor is looking at, or <see langword="null" /> when there is none.</param>
/// <param name="Facts">What the Plan page read beside a plan it ran, or <see langword="null" />.</param>
internal sealed record PlanInsightContext(StudioScope? Scope, PlanFacts? Facts);

/// <summary>What a contributor made of one plan.</summary>
/// <param name="Findings">Its findings, in code order, each insight's worst first.</param>
/// <param name="NotJudged">The insights of its own it could not judge on this plan.</param>
internal sealed record PlanContribution(IReadOnlyList<PlanInsightFinding> Findings, IReadOnlyList<PlanInsightInfo> NotJudged)
{
    /// <summary>Nothing found, nothing left unjudged.</summary>
    public static PlanContribution None { get; } = new([], []);
}
