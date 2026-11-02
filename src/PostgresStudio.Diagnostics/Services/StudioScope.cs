namespace PostgresStudio.Services;

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
    /// <param name="innerException">
    /// What the host's code threw, when this wraps one: kept for the server log, never part of <see cref="Exception.Message" />.
    /// </param>
    public StudioStoreUnavailableException(string storeKey, string reason, string sourceNoun, Exception? innerException = null)
        : base($"{sourceNoun} '{storeKey}' could not be built: {reason}", innerException)
    {
        StoreKey = storeKey;
        Reason = reason;
    }

    /// <summary>The registration key of the source that would not build.</summary>
    public string StoreKey { get; }

    /// <summary>What the failure said, rendered in the region that could not be drawn.</summary>
    public string Reason { get; }
}

/// <summary>
/// A host's database source did not answer within the studio's <c>QueryTimeout</c>. The message is the studio's own sentence
/// (the source's registered key, a bounded name, the limit) and may be shown; a <see cref="TimeoutException" /> the host's
/// own code threw is not this type, and its words never are.
/// </summary>
internal sealed class StudioSourceTimeoutException : TimeoutException
{
    /// <param name="message">The studio's sentence.</param>
    /// <param name="innerException">The cancellation that ran out.</param>
    public StudioSourceTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// H1b's rule for what a host's own code threw while the studio resolved a scope - its list of databases, its completing the
/// scope, its opening one (1.0-HARD wraps every such failure in a <see cref="StudioStoreUnavailableException" /> carrying it):
/// Postgres' words are withheld, because they can name a schema no gate has been read to mask, and only the SQLSTATE is said; a
/// connection failure is said as one; the studio's own timeout sentence is said as it is; and anything else is the host's own
/// sentence about its own list, said escaped and bounded (<see cref="StudioLogValue.CapText" />).
/// </summary>
internal static class StudioHostFailure
{
    /// <summary>What a page may say of <paramref name="exception" />, a host source's failure.</summary>
    public static string Words(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            Npgsql.PostgresException postgres => "Postgres refused a read (" + postgres.SqlState
                + "); its own words are withheld: the schemas they may name could not be checked for you",
            Npgsql.NpgsqlException => "the database could not be reached",
            StudioSourceTimeoutException timeout => timeout.Message,
            _ => StudioLogValue.CapText(exception.Message),
        };
    }

    /// <summary>
    /// The failure a page describes: what the host threw, when <paramref name="exception" /> is the studio's wrap of it - so the
    /// page's own sentences about it are said as they always were - and <paramref name="exception" /> itself otherwise.
    /// </summary>
    public static Exception Unwrap(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is StudioStoreUnavailableException { InnerException: { } inner } && inner is not StudioSourceTimeoutException
            ? inner
            : exception;
    }
}
