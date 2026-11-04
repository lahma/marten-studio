namespace PostgresStudio.Services.Query;

/// <summary>
/// 1.2-INDEX-A: a studio's own indexes for the Statements page's index advisor, beside the ones every studio reads off a plan
/// (<see cref="WorkloadIndexCandidates" />) - the seam MartenStudio registers its document-table indexes through, in the shape of
/// <see cref="IPlanInsightContributor" />.
/// </summary>
/// <remarks>
/// <para>
/// PostgresStudio defines the seam and runs whatever is registered (<c>IEnumerable&lt;IIndexCandidateContributor&gt;</c>, empty in
/// Postgres Studio); it names nothing a contributor knows (hard rule 16). The advisor calls each once per admitted plan, in
/// registration order, each given the previous one's output.
/// </para>
/// <para>
/// A contributor reads no database and calls nothing that ensures storage (hard rule 14). The indexes it returns are written by a
/// builder - never typed, never a visitor's - and reach hypopg as a parameter; the advisor imagines them and runs nothing.
/// </para>
/// </remarks>
internal interface IIndexCandidateContributor
{
    /// <summary>
    /// The plan's candidates as this studio offers them: those on tables it owns replaced by its own (with their
    /// <c>StoreOptions</c> line, <see cref="HypotheticalIndex.Line" />) or dropped, and its own added. Reads no database; never a
    /// storage-ensuring call.
    /// </summary>
    /// <param name="plan">A plan of the workload, drawn with <c>VERBOSE</c>, that reads nothing the visitor may not see.</param>
    /// <param name="fromPlan">The indexes <see cref="WorkloadIndexCandidates.FromPlan" /> found on it, or the previous contributor's.</param>
    /// <param name="scope">The scope the visitor selected, for the database as a whole.</param>
    /// <param name="cancellationToken">Cancels the lookup of what the studio declares.</param>
    ValueTask<IReadOnlyList<HypotheticalIndex>> ContributeAsync(
        QueryPlan plan,
        IReadOnlyList<HypotheticalIndex> fromPlan,
        StudioScope scope,
        CancellationToken cancellationToken);
}
