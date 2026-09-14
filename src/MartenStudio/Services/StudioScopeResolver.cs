using Marten;
using Marten.Storage;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// Turns the three strings in a URL into the Marten objects behind them, refusing before it looks
/// anything up.
/// </summary>
/// <remarks>
/// <para>
/// The order is the security property. Authorization runs <em>first</em>, so a store the visitor may not
/// see and a store that was never registered produce the same refusal - the alternative leaks the shape
/// of a process to anyone willing to try keys and watch which error comes back. Only once the policy has
/// said yes is the registration looked up, the store built, the database matched and the tenant checked.
/// </para>
/// <para>
/// The database is matched by <c>Id.Identity</c> against what <c>Storage.AllDatabases()</c> answers, and
/// never passed to <c>FindOrCreateDatabase</c>: that method's name is literal, and handing it a string a
/// browser supplied is how a UI creates a database nobody asked for.
/// </para>
/// </remarks>
internal sealed class StudioScopeResolver
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly MartenStoreRegistry registry;
    private readonly StudioAuthorization authorization;
    private readonly TenantDiscovery tenantDiscovery;
    private readonly IServiceProvider provider;

    public StudioScopeResolver(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        StudioAuthorization authorization,
        TenantDiscovery tenantDiscovery,
        IServiceProvider provider)
    {
        this.options = options;
        this.registry = registry;
        this.authorization = authorization;
        this.tenantDiscovery = tenantDiscovery;
        this.provider = provider;
    }

    /// <summary>
    /// Resolves <paramref name="scope" />, or throws.
    /// </summary>
    /// <param name="scope">The store, database and tenant to reach.</param>
    /// <param name="capability">
    /// The capability being exercised, for a mutating call. Non-null adds a second check against
    /// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> with the capability named on the
    /// resource; the store policy has already had to pass.
    /// </param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    /// <exception cref="KeyNotFoundException">No such store, or no such database in it.</exception>
    /// <exception cref="StudioStoreUnavailableException">The store is registered but will not build.</exception>
    public async ValueTask<ResolvedScope> ResolveAsync(
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
        MartenStoreRegistration registration = registry.Find(scope.StoreKey, options)
            ?? throw new KeyNotFoundException($"No Marten store is registered under '{scope.StoreKey}'.");

        // 3. The store itself, which may refuse to build.
        StoreAvailability availability = registry.TryResolve(registration, provider, out IDocumentStore? store);
        if (!availability.IsAvailable || store is null)
        {
            throw new StudioStoreUnavailableException(registration.Key, availability.Message ?? "unknown reason");
        }

        // 4. The database, matched against what Marten already knows.
        IMartenDatabase database = await FindDatabaseAsync(store, scope.DatabaseId).ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Marten store '{registration.Key}' has no database with identity '{scope.DatabaseId}'.");

        // 5. The tenant, which is only ever one the store could actually answer for.
        if (scope.TenantId is not null)
        {
            TenantList tenants = await tenantDiscovery
                .DiscoverAsync(registration.Key, store, database, cancellationToken)
                .ConfigureAwait(false);

            if (!TenantDiscovery.IsAcceptable(tenants, scope.TenantId))
            {
                throw new KeyNotFoundException(
                    $"'{scope.TenantId}' is not a tenant of database '{scope.DatabaseId}' in Marten store '{registration.Key}'.");
            }
        }

        return new ResolvedScope(scope, registration, store, database);
    }

    private static async ValueTask<IMartenDatabase?> FindDatabaseAsync(IDocumentStore store, string databaseId)
    {
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);

        // An empty identity means "whatever this store's single database is", which is the shape every
        // single-tenant store has and the one a link without a ?db= carries.
        if (string.IsNullOrEmpty(databaseId))
        {
            return databases.Count > 0 ? databases[0] : null;
        }

        foreach (IMartenDatabase database in databases)
        {
            if (string.Equals(database.Id.Identity, databaseId, StringComparison.OrdinalIgnoreCase))
            {
                return database;
            }
        }

        return null;
    }
}
