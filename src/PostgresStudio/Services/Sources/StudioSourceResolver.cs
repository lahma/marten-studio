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
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    /// <exception cref="KeyNotFoundException">No such source, no such database in it, or no such tenant of it.</exception>
    /// <exception cref="StudioStoreUnavailableException">The source is registered but will not build.</exception>
    public async ValueTask<ResolvedSource> ResolveAsync(
        StudioScope scope,
        string? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1. Authorize, before anything is looked up. A denied scope and an unknown one are one answer.
        if (!await authorization.IsAuthorizedAsync(scope, null, cancellationToken).ConfigureAwait(false))
        {
            throw new StudioNotAuthorizedException(scope);
        }

        if (capability is not null
            && !await authorization.IsAuthorizedAsync(scope, capability, cancellationToken).ConfigureAwait(false))
        {
            throw new StudioNotAuthorizedException(scope);
        }

        // 2. The registration.
        StudioSourceRegistration registration = provider.Find(scope.StoreKey)
            ?? throw new KeyNotFoundException($"No {descriptor.SourceNoun} is registered under '{scope.StoreKey}'.");

        // 3. The source itself, which may refuse to build.
        StoreAvailability availability = provider.TryResolve(registration, out IStudioSource? source);
        if (!availability.IsAvailable || source is null)
        {
            throw new StudioStoreUnavailableException(registration.Key, availability.Message ?? "unknown reason", descriptor.SourceNoun);
        }

        // 4. The database, matched against what the source already knows.
        IReadOnlyList<IStudioDatabase> databases = await source.DatabasesAsync(cancellationToken).ConfigureAwait(false);
        IStudioDatabase database = Match(databases, scope.DatabaseId)
            ?? throw new KeyNotFoundException(
                $"{descriptor.SourceNoun} '{registration.Key}' has no database with identity '{scope.DatabaseId}'.");

        // 5-6. The tenant and the instance it was built from - the source's own steps, asked with the list just read.
        return await source.CompleteAsync(scope, database, databases, cancellationToken).ConfigureAwait(false);
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
