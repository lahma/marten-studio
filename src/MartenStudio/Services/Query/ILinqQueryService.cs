namespace MartenStudio.Services.Query;

/// <summary>
/// LINQ mode on the Query page: a typed predicate over one document type, parsed under the studio's
/// allow-list and translated by Marten's own <c>Query&lt;T&gt;()</c> (F7).
/// </summary>
/// <remarks>
/// <para>
/// A separate seam from <see cref="IQueryService" /> because its gate is separate:
/// <see cref="MartenStudioCapabilities.RunLinqQueries" />, asked like a write (D13), where a Marten
/// <c>where</c> clause needs no capability and the console needs <c>RunSql</c>. It is an interface for the
/// same reason every data seam is (D12): the page tests drive every refusal without a Postgres.
/// </para>
/// </remarks>
internal interface ILinqQueryService
{
    /// <summary>
    /// Whether LINQ mode is available here, why not, and - when it is - the document types the visitor may
    /// query and whether the policies let them.
    /// </summary>
    /// <remarks>
    /// Never throws for a refusal and runs nothing: a capability that is off, <c>ReadOnly</c>, or a Marten
    /// older than 9.48 is a value the page draws. A scope the store policy refuses is
    /// <see cref="StudioNotAuthorizedException" />, as for every other read.
    /// </remarks>
    Task<LinqModeStatus> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Runs one page of a LINQ query.</summary>
    /// <exception cref="StudioCapabilityDeniedException">
    /// <see cref="MartenStudioCapabilities.RunLinqQueries" /> is off, or the studio is read-only. Nothing is parsed.
    /// </exception>
    /// <exception cref="StudioNotAuthorizedException">
    /// The store policy, or the write policy asked with <c>RunLinqQueries</c>, refused this scope. Nothing is parsed.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No such store or database.</exception>
    /// <remarks>
    /// Every other outcome is a value: a mode this host cannot run, a type it does not offer, a refusal by the
    /// screen, the parser, the guard or Marten's translation, and a Postgres error - each audited, success and
    /// failure alike, and a success only on the first page of its signature.
    /// </remarks>
    Task<LinqQueryResult> RunAsync(
        StudioScope scope,
        LinqQueryRequest request,
        CancellationToken cancellationToken = default);
}
