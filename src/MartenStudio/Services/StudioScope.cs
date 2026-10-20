namespace MartenStudio.Services;

/// <summary>
/// What the studio is currently about: one source, one of its databases, and optionally one tenant.
/// </summary>
/// <remarks>
/// <para>
/// Every data-service method takes one of these and resolves it; none accepts a source or a database
/// object. That is the whole point: a resolved source or database has already passed authorization, and a
/// method that accepted one would make it possible to skip the check by passing something that was
/// resolved for a different purpose.
/// </para>
/// <para>
/// All three members are strings because all three arrive from the URL (plan D9): a link someone pastes
/// into a chat has to reopen the same thing. None of them is ever concatenated into SQL or handed to
/// anything that would create a database; the resolver matches them against what the source already knows.
/// </para>
/// </remarks>
/// <param name="StoreKey">The source's registration key - <c>default</c>, or an ancillary store's marker type name.</param>
/// <param name="DatabaseId">The database's stable identity (<c>IStudioDatabase.Identity</c>), never a connection string.</param>
/// <param name="TenantId">The tenant, or <see langword="null" /> for a view that spans all of them.</param>
internal sealed record StudioScope(string StoreKey, string DatabaseId, string? TenantId);

/// <summary>
/// What resolving a scope throws when the visitor may not have it.
/// </summary>
/// <remarks>
/// Thrown <em>before</em> anything is looked up, so a source the visitor may not see and a source that does
/// not exist are the same answer. A refusal that named the source would let anyone enumerate the sources,
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
/// What resolving a scope throws when the source is registered but will not build.
/// </summary>
internal sealed class StudioStoreUnavailableException : Exception
{
    /// <param name="storeKey">The registration key.</param>
    /// <param name="reason">What the failure said.</param>
    /// <param name="sourceNoun">The studio's noun for a source: <c>Marten store</c>.</param>
    public StudioStoreUnavailableException(string storeKey, string reason, string sourceNoun)
        : base($"{sourceNoun} '{storeKey}' could not be built: {reason}")
    {
        StoreKey = storeKey;
        Reason = reason;
    }

    /// <summary>The registration key of the source that would not build.</summary>
    public string StoreKey { get; }

    /// <summary>What the failure said, rendered in the region that could not be drawn.</summary>
    public string Reason { get; }
}
