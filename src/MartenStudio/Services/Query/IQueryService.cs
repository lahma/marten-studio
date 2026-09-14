namespace MartenStudio.Services.Query;

/// <summary>
/// What the Query page reads and runs: the document types it can filter, a Marten <c>where</c> clause,
/// and the read-only SQL console.
/// </summary>
/// <remarks>
/// <para>
/// An interface so a component test can drive both modes - including every refusal - without a Postgres
/// (plan D12). The implementation is the only thing that resolves a scope, and it resolves one on every
/// call rather than trusting the caller (plan D5).
/// </para>
/// <para>
/// The asymmetry between the two modes is the point. A <c>where</c> clause is a read against one document
/// type the host registered and needs no capability; the SQL console is arbitrary SQL against the whole
/// database and is gated on <see cref="MartenStudioCapabilities.RunSql" />, resolved with write-policy
/// semantics and audited statement by statement (D13).
/// </para>
/// </remarks>
internal interface IQueryService
{
    /// <summary>
    /// The document types the visitor may filter, honouring
    /// <see cref="MartenStudioOptions.IsDocumentTypeVisible" />.
    /// </summary>
    Task<IReadOnlyList<QueryDocumentTypeInfo>> ListDocumentTypesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a Marten <c>where</c> clause, and - when the SQL console capability is granted - fetches the
    /// plan for it.
    /// </summary>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    /// <exception cref="KeyNotFoundException">No such store, database or document alias.</exception>
    Task<MartenQueryResult> RunMartenQueryAsync(
        StudioScope scope,
        MartenQueryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one statement in the read-only SQL console.
    /// </summary>
    /// <exception cref="StudioCapabilityDeniedException">
    /// <see cref="MartenStudioCapabilities.RunSql" /> is off, or the studio is read-only. Nothing is sent.
    /// </exception>
    /// <exception cref="StudioNotAuthorizedException">
    /// The write policy refused this scope for <c>RunSql</c>. Nothing is sent.
    /// </exception>
    Task<SqlConsoleResult> RunSqlAsync(
        StudioScope scope,
        SqlConsoleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The idle panel's examples, built from this store's own aliases and tables.</summary>
    Task<QueryExamples> BuildExamplesAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
