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
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
        IMartenDatabase database = MatchDatabase(databases, scope.DatabaseId)
            ?? throw new KeyNotFoundException(
                $"Marten store '{registration.Key}' has no database with identity '{scope.DatabaseId}'.");

        // 5. The tenant, which is only ever one the store could actually answer for - asked with the list
        //    just read, so that a database's same-identity twins answer for their tenants too.
        if (scope.TenantId is { } tenantId)
        {
            TenantList tenants = await tenantDiscovery
                .DiscoverAsync(registration.Key, store, database, databases, cancellationToken)
                .ConfigureAwait(false);

            if (!TenantDiscovery.IsAcceptable(tenants, scope.TenantId))
            {
                throw new KeyNotFoundException(
                    $"'{scope.TenantId}' is not a tenant of database '{scope.DatabaseId}' in Marten store '{registration.Key}'.");
            }

            // 6. And the instance that tenant was built from, when the database has twins.
            database = InstanceFor(databases, database, tenantId);
        }

        return new ResolvedScope(scope, registration, store, database);
    }

    /// <summary>
    /// Of the instances that share <paramref name="database" />'s identity, the one whose own tenant list holds
    /// <paramref name="tenantId" /> - or <paramref name="database" /> itself, the first of them, when it does or none
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Twins.</b> Two master-table rows with one connection string are two <c>MartenDatabase</c> objects with one
    /// <c>Id.Identity</c>, each listing its own row's tenant (<c>MasterTableTenancy.BuildDatabases</c>). The studio scopes by
    /// identity, so the database matched above is the first of them, and tenant discovery answers for every twin's
    /// tenants (<see cref="TenantDiscovery" />). A scope naming the second row's tenant then opened every session, and
    /// every connection, on the <em>first</em> row's instance: its data source - that row's role, its
    /// <c>Options=-c</c>, its pool - and not the one Marten itself would use for that tenant. For a host that gives
    /// each tenant a role of its own, or row-level security keyed on one, that is the wrong tenant's connection under
    /// the right tenant's header (SEC1 L1). The policies were asked the right question; only the instance was wrong.
    /// </para>
    /// <para>
    /// A scope with no tenant keeps the first instance, which is what the picker lists. Matched ordinally, as
    /// <see cref="TenantDiscovery.IsAcceptable" /> matched the tenant against the same lists, each copied before it is
    /// read because a sharded tenancy reconciles them from whichever thread enumerates.
    /// </para>
    /// </remarks>
    internal static IMartenDatabase InstanceFor(IReadOnlyList<IMartenDatabase> databases, IMartenDatabase database, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(database);

        if (Lists(database, tenantId))
        {
            return database;
        }

        foreach (IMartenDatabase twin in databases)
        {
            if (!ReferenceEquals(twin, database)
                && string.Equals(twin.Id.Identity, database.Id.Identity, StringComparison.OrdinalIgnoreCase)
                && Lists(twin, tenantId))
            {
                return twin;
            }
        }

        return database;

        static bool Lists(IMartenDatabase instance, string tenantId)
        {
            string[] tenants = [.. instance.TenantIds];
            return tenants.Contains(tenantId, StringComparer.Ordinal);
        }
    }

    private static IMartenDatabase? MatchDatabase(IReadOnlyList<IMartenDatabase> databases, string databaseId)
    {
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
