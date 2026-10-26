using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Sources;

/// <summary>
/// Turns the three strings in a URL into the source and database behind them, refusing before it looks anything up.
/// </summary>
/// <remarks>
/// <para>
/// The order is the security property. Authorization runs <em>first</em>, so a source the visitor may not see and a
/// source that was never registered produce the same refusal - the alternative leaks the shape of a process to anyone
/// willing to try keys and watch which error comes back. Only once the policy has said yes is the registration looked
/// up, the source built, the database matched, and the source asked to complete the scope (the tenant, and which
/// instance that tenant was built from).
/// </para>
/// <para>
/// The database is matched by <see cref="IStudioDatabase.Identity" /> against what the source enumerates, and never
/// handed to anything that would create one: a string a browser supplied is how a UI creates a database nobody asked for.
/// </para>
/// <para>
/// <b>The question is asked again by the names a policy knows.</b> A source key and a database id match in any case,
/// and an empty database id is the source's first database, so the question asked before the lookup carries the
/// visitor's spelling - <c>?store=ADMIN</c>, or no <c>?db=</c> at all. A handler that keys on the registered names
/// (<c>resource.SourceName != "admin"</c>) would pass that spelling and then be answered with <c>admin</c>. So once the
/// registration and the database are matched, and whenever either spelling differs from the registered key or the
/// matched identity, both policies are asked again about those - before the source completes the scope, opens anything
/// or reads anything. The public resources promise a handler those names (<c>PostgresStudioResource.DatabaseIdentifier</c>
/// is "the name a host's <c>IPostgresDatabaseSource</c> lists the database under"); this is where that promise is kept,
/// whichever caller built the scope. A scope already spelled that way is asked once.
/// </para>
/// </remarks>
internal sealed class StudioSourceResolver
{
    private readonly IStudioSourceProvider provider;
    private readonly StudioAuthorization authorization;
    private readonly StudioDescriptor descriptor;

    public StudioSourceResolver(IStudioSourceProvider provider, StudioAuthorization authorization, StudioDescriptor descriptor)
    {
        this.provider = provider;
        this.authorization = authorization;
        this.descriptor = descriptor;
    }

    /// <summary>
    /// Resolves <paramref name="scope" />, or throws.
    /// </summary>
    /// <param name="scope">The source, database and tenant to reach.</param>
    /// <param name="capability">
    /// The capability being exercised, for a mutating call or a read authorized like one. Non-null adds a second check
    /// against the write policy with the capability named on the resource; the scope policy has already had to pass.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="StudioNotAuthorizedException">
    /// The visitor may not have this scope - as spelled, or by the registered key and the matched identity, in which case
    /// the exception carries the latter, the scope that was refused.
    /// </exception>
    /// <exception cref="KeyNotFoundException">No such source, no such database in it, or no such tenant of it.</exception>
    /// <exception cref="StudioStoreUnavailableException">The source is registered but will not build.</exception>
    public async ValueTask<ResolvedSource> ResolveAsync(
        StudioScope scope,
        string? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1. Authorize, before anything is looked up. A denied scope and an unknown one are one answer.
        await AuthorizeAsync(scope, capability, cancellationToken).ConfigureAwait(false);

        // 2. The registration.
        StudioSourceRegistration registration = provider.Find(scope.StoreKey)
            ?? throw new KeyNotFoundException($"No {descriptor.SourceNoun} is registered under '{StudioLogValue.Cap(scope.StoreKey)}'.");

        // 3. The source itself, which may refuse to build.
        StoreAvailability availability = provider.TryResolve(registration, out IStudioSource? source);
        if (!availability.IsAvailable || source is null)
        {
            throw new StudioStoreUnavailableException(registration.Key, availability.Message ?? "unknown reason", descriptor.SourceNoun);
        }

        // 4. The database, matched against what the source already knows. The visitor's spelling goes into the message
        //    escaped and bounded (StudioLogValue.Cap): a message like this one is logged and audited whole.
        IReadOnlyList<IStudioDatabase> databases = await source.DatabasesAsync(cancellationToken).ConfigureAwait(false);
        IStudioDatabase database = Match(databases, scope.DatabaseId)
            ?? throw new KeyNotFoundException(
                $"{descriptor.SourceNoun} '{registration.Key}' has no database with identity '{StudioLogValue.Cap(scope.DatabaseId)}'.");

        // 5. Authorize again, by the registered key and the matched identity, when the visitor spelled either another way:
        //    a key in another case, a database id in another case, or none at all (the first database). The policies
        //    must have said yes to what is about to be read, not only to how a link spelled it. Before the source
        //    completes the scope, because completing it may open the database (a host's database source does).
        StudioScope named = Named(scope, registration, database);
        if (!ReferenceEquals(named, scope))
        {
            await AuthorizeAsync(named, capability, cancellationToken).ConfigureAwait(false);
        }

        // 6-7. The tenant and the instance it was built from - the source's own steps, asked with the list just read.
        return await source.CompleteAsync(scope, database, databases, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="scope" /> by the registered key and the matched identity - or <paramref name="scope" /> itself,
    /// the same instance, when it already spells both that way (compared ordinally: a policy compares however it likes).
    /// The tenant is kept as it is, because a tenant is matched ordinally and is therefore already spelled as listed.
    /// </summary>
    internal static StudioScope Named(StudioScope scope, StudioSourceRegistration registration, IStudioDatabase database)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(database);

        return string.Equals(scope.StoreKey, registration.Key, StringComparison.Ordinal)
               && string.Equals(scope.DatabaseId, database.Identity, StringComparison.Ordinal)
            ? scope
            : scope with { StoreKey = registration.Key, DatabaseId = database.Identity };
    }

    /// <summary>
    /// The scope policy about <paramref name="scope" />, then - for a capability - the write policy, which names it; throws
    /// on the first refusal, carrying <paramref name="scope" /> for the caller's audit entry.
    /// </summary>
    private async ValueTask AuthorizeAsync(StudioScope scope, string? capability, CancellationToken cancellationToken)
    {
        if (!await authorization.IsAuthorizedAsync(scope, null, cancellationToken).ConfigureAwait(false))
        {
            throw new StudioNotAuthorizedException(scope);
        }

        if (capability is not null
            && !await authorization.IsAuthorizedAsync(scope, capability, cancellationToken).ConfigureAwait(false))
        {
            throw new StudioNotAuthorizedException(scope);
        }
    }

    /// <summary>
    /// The first database in <paramref name="databases" /> with <paramref name="databaseId" /> as its identity, ignoring
    /// case - or, for an empty identity, the source's first database: the shape every single-database source has, and
    /// the one a link without a <c>?db=</c> carries.
    /// </summary>
    internal static IStudioDatabase? Match(IReadOnlyList<IStudioDatabase> databases, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(databases);

        if (string.IsNullOrEmpty(databaseId))
        {
            return databases.Count > 0 ? databases[0] : null;
        }

        foreach (IStudioDatabase database in databases)
        {
            if (string.Equals(database.Identity, databaseId, StringComparison.OrdinalIgnoreCase))
            {
                return database;
            }
        }

        return null;
    }
}
