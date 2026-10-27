using PostgresStudio.Services;

namespace MartenStudio.Services.Query;

/// <summary>
/// What the Query page's Marten mode reads and runs: the document types it can filter, a Marten <c>where</c>
/// clause, and the examples built from them.
/// </summary>
/// <remarks>
/// <para>
/// An interface so a component test can drive the mode - including every refusal - without a Postgres
/// (plan D12). The implementation is the only thing that resolves a scope, and it resolves one on every
/// call rather than trusting the caller (plan D5).
/// </para>
/// <para>
/// The asymmetry between this mode and the SQL console is the point. A <c>where</c> clause is a read against one
/// document type the host registered and needs no capability; the SQL console is arbitrary SQL against the whole
/// database, gated on <see cref="MartenStudioCapabilities.RunSql" />, resolved with write-policy semantics and
/// audited statement by statement (D13) - and it is the shared <see cref="PostgresStudio.Services.Sources.ISqlConsoleService" />, not a
/// member of this interface.
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
    /// <remarks>
    /// The clause is a predicate the studio composes <em>into</em> its own statement, behind its own
    /// tenant and soft-delete predicates. It is not passed to Marten's string-query overload, which
    /// composes neither and would return every tenant's rows and every deleted row - see
    /// <c>QuerySqlComposer</c>. Every run is audited, successes included.
    /// </remarks>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    /// <exception cref="KeyNotFoundException">No such store, database or document alias.</exception>
    Task<MartenQueryResult> RunMartenQueryAsync(
        StudioScope scope,
        MartenQueryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>The idle panel's examples, built from this store's own aliases and tables.</summary>
    Task<QueryExamples> BuildExamplesAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// X2: a Mode A statement's plan now and under one hypothetical index its plan suggested - composed again from the request
    /// exactly as <see cref="RunMartenQueryAsync" /> composes it, and planned through the SQL console's gate, as Mode A's plan
    /// is (<c>RunSql</c>). Every refusal is a value, audited; nothing is thrown but for cancellation.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="request">The clause the plan was of.</param>
    /// <param name="index">The index to imagine: one the plan suggested.</param>
    /// <param name="cancellationToken">Cancels the what-if; never the reset after it.</param>
    Task<PostgresStudio.Services.Query.WhatIfResult> WhatIfAsync(
        StudioScope scope,
        MartenQueryRequest request,
        PostgresStudio.Services.Query.HypotheticalIndex index,
        CancellationToken cancellationToken = default);
}
