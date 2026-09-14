using Marten;
using Marten.Storage;

namespace MartenStudio.Services;

/// <summary>
/// What the studio is currently about: one store, one of its databases, and optionally one tenant.
/// </summary>
/// <remarks>
/// <para>
/// Every data-service method takes one of these and resolves it; none accepts an
/// <see cref="IDocumentStore" /> or an <see cref="IMartenDatabase" />. That is the whole point: a
/// resolved store or database has already passed authorization, and a method that accepted one would
/// make it possible to skip the check by passing something that was resolved for a different purpose.
/// </para>
/// <para>
/// All three members are strings because all three arrive from the URL (plan D9): a link someone pastes
/// into a chat has to reopen the same thing. None of them is ever concatenated into SQL or handed to
/// <c>FindOrCreateDatabase</c>; the resolver matches them against what Marten already knows.
/// </para>
/// </remarks>
/// <param name="StoreKey">The registration key - <c>default</c>, or an ancillary store's marker type name.</param>
/// <param name="DatabaseId">Marten's <c>DatabaseId.Identity</c> for the database, never a connection string.</param>
/// <param name="TenantId">The tenant, or <see langword="null" /> for a view that spans all of them.</param>
internal sealed record StudioScope(string StoreKey, string DatabaseId, string? TenantId)
{
    /// <summary>
    /// This scope as the resource an authorization handler is asked about.
    /// </summary>
    /// <param name="capability">
    /// The capability being exercised (for example <c>EditDocuments</c>), or <see langword="null" /> for a
    /// read - which is what lets one handler answer both the store policy and the write policy.
    /// </param>
    public MartenStoreResource ToResource(string? capability) =>
        new(StoreKey, DatabaseId, TenantId, capability);
}

/// <summary>
/// A scope that passed every check, with the Marten objects it names.
/// </summary>
internal sealed record ResolvedScope(
    StudioScope Scope,
    MartenStoreRegistration Registration,
    IDocumentStore Store,
    IMartenDatabase Database)
{
    /// <summary>The tenant in scope, or <see langword="null" /> for all of them.</summary>
    public string? TenantId => Scope.TenantId;
}

/// <summary>
/// What resolving a scope throws when the visitor may not have it.
/// </summary>
/// <remarks>
/// Thrown <em>before</em> anything is looked up, so a store the visitor may not see and a store that does
/// not exist are the same answer. A refusal that named the store would let anyone enumerate the stores,
/// databases and tenants of a process by watching which refusals differ - which is the one thing a
/// per-tenant policy exists to prevent.
/// </remarks>
internal sealed class StudioNotAuthorizedException : Exception
{
    public StudioNotAuthorizedException(StudioScope scope)
        : base("Not authorized for the requested store, database or tenant.")
    {
        Scope = scope;
    }

    /// <summary>The scope that was refused, for the audit entry. Never rendered to the visitor.</summary>
    public StudioScope Scope { get; }
}

/// <summary>
/// What resolving a scope throws when the store is registered but will not build.
/// </summary>
internal sealed class StudioStoreUnavailableException : Exception
{
    public StudioStoreUnavailableException(string storeKey, string reason)
        : base($"Marten store '{storeKey}' could not be built: {reason}")
    {
        StoreKey = storeKey;
        Reason = reason;
    }

    /// <summary>The registration key of the store that would not build.</summary>
    public string StoreKey { get; }

    /// <summary>What the failure said, rendered in the region that could not be drawn.</summary>
    public string Reason { get; }
}
