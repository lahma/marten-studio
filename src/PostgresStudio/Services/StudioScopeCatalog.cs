using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

/// <summary>One source as the picker offers it.</summary>
internal sealed record StoreListing(string Key, string DisplayName, StoreAvailability Availability);

/// <summary>
/// The sources the visitor may see, in the two groups the studio keeps apart (1.2): the ones the picker offers, and the ones whose
/// databases could not be listed.
/// </summary>
/// <param name="Listed">Every source the visitor may see, unavailable ones included and marked.</param>
/// <param name="Unlistable">
/// Sources that built and whose databases could not be listed (an unreachable master database), and that the source policy passes
/// for the source as a whole. Not selectable - there is no database to select - but known, so a page that names the sources can
/// say so rather than let the source vanish.
/// </param>
internal sealed record StoreListings(IReadOnlyList<StoreListing> Listed, IReadOnlyList<StoreListing> Unlistable)
{
    /// <summary>No source at all.</summary>
    public static StoreListings None { get; } = new([], []);
}

/// <summary>The databases of one source the visitor may see, and whether they could not be listed at all.</summary>
/// <param name="Databases">The databases, filtered for the visitor.</param>
/// <param name="Failed">Whether the enumeration failed (or the source will not build), as distinct from a visitor who may see none.</param>
internal sealed record StoreDatabaseListing(IReadOnlyList<DatabaseListing> Databases, bool Failed);

/// <summary>One database as the picker offers it. The key is the database's stable identity, never a connection string.</summary>
internal sealed record DatabaseListing(string Identity, string Name, string Server);

/// <summary>
/// What the header needs to know about one source and database beyond the listings: how many databases
/// there could be, whether a tenant selector means anything, and which tenants it would offer.
/// </summary>
internal sealed record StoreScopeFacts(
    StudioDatabaseCardinality Cardinality,
    bool ShowTenantSelector,
    TenantList Tenants,
    string? UnavailableMessage)
{
    /// <summary>What a source nobody could build looks like.</summary>
    public static StoreScopeFacts Unavailable(string? message) =>
        new(StudioDatabaseCardinality.Single, ShowTenantSelector: false, TenantList.Unavailable, message);

    /// <summary>What no source at all looks like.</summary>
    public static StoreScopeFacts None { get; } =
        new(StudioDatabaseCardinality.Single, ShowTenantSelector: false, TenantList.Unavailable, null);
}

/// <summary>
/// Where <see cref="StudioState" /> gets its authorization-filtered listings.
/// </summary>
/// <remarks>
/// A seam rather than a method on the state object, for two reasons. The listings are IO - they build
/// sources, enumerate databases and may query for tenants - and the state is what a circuit holds;
/// keeping them apart means a component test can say what the header should show without a Postgres
/// anywhere near it.
/// </remarks>
internal interface IStudioScopeCatalog
{
    /// <summary>Every source the visitor may see, unavailable ones included and marked - and, apart, the ones whose databases could not be listed.</summary>
    Task<StoreListings> ListStoresAsync(CancellationToken cancellationToken = default);

    /// <summary>Every database of one source the visitor may see.</summary>
    Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same listing as <see cref="ListDatabasesAsync" />, and whether the source's databases could not be enumerated: an empty list
    /// that means "this visitor may see none" and one that means "could not be read" are different answers (Find's "Search in").
    /// </summary>
    async Task<StoreDatabaseListing> TryListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default) =>
        new(await ListDatabasesAsync(storeKey, cancellationToken).ConfigureAwait(false), false);

    /// <summary>The facts the header renders about one source and database.</summary>
    Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default);

    /// <summary>Forgets whatever was cached, for the refresh button.</summary>
    void Invalidate();
}

/// <summary>
/// The real catalog: the source provider, the scope policy and the sources' tenant discovery, in that order.
/// </summary>
/// <remarks>
/// Every listing is filtered before it is returned, which is what makes it the set
/// <see cref="StudioState.SetScopeAsync" /> validates against. A source whose every database the visitor
/// may not see is not listed at all - not listed as empty, not listed as denied.
/// </remarks>
internal sealed class StudioScopeCatalog : IStudioScopeCatalog
{
    private readonly IStudioSourceProvider sources;
    private readonly StudioAuthorization authorization;
    private readonly ILogger<StudioScopeCatalog> logger;
    private readonly StudioLogThrottle? throttle;
    private readonly StudioProduct product;

    /// <remarks>
    /// <paramref name="throttle" /> is optional so that a test can build a catalog by hand; the container
    /// always supplies it, and without one every failure is simply a Warning
    /// (<see cref="StudioLogThrottle.LevelOrWarning" />). <paramref name="settings" /> likewise: they choose which
    /// studio's words the events are logged in (<see cref="StudioText.Of" />).
    /// </remarks>
    public StudioScopeCatalog(
        IStudioSourceProvider sources,
        StudioAuthorization authorization,
        ILogger<StudioScopeCatalog> logger,
        StudioLogThrottle? throttle = null,
        IStudioSettings? settings = null)
    {
        this.sources = sources;
        this.authorization = authorization;
        this.logger = logger;
        this.throttle = throttle;
        product = StudioText.Of(settings).Product;
    }

    /// <inheritdoc />
    public async Task<StoreListings> ListStoresAsync(CancellationToken cancellationToken = default)
    {
        List<StoreListing> stores = [];
        List<StoreListing> unlistable = [];

        foreach (StudioSourceRegistration registration in sources.Registrations())
        {
            StoreAvailability availability = sources.TryResolve(registration, out IStudioSource? source);
            if (!availability.IsAvailable || source is null)
            {
                // A source the application registered but that will not build is offered and greyed out
                // rather than omitted - but only to someone the policy would let reach it. The resource
                // carries an empty database identity because there is no database to name yet.
                if (await authorization.IsAuthorizedAsync(new StudioScope(registration.Key, string.Empty, null), null, cancellationToken).ConfigureAwait(false))
                {
                    stores.Add(new StoreListing(registration.Key, registration.DisplayName, availability));
                }

                continue;
            }

            (IReadOnlyList<DatabaseListing> databases, bool failed) = await ListDatabasesCoreAsync(registration.Key, source, cancellationToken).ConfigureAwait(false);
            if (databases.Count > 0)
            {
                stores.Add(new StoreListing(registration.Key, registration.DisplayName, availability));
            }
            else if (failed
                && await authorization.IsAuthorizedAsync(new StudioScope(registration.Key, string.Empty, null), null, cancellationToken).ConfigureAwait(false))
            {
                // A source whose databases could not be listed is not selectable, but it is not nothing: it is kept, apart, for
                // the pages that name every source - and only for a visitor the policy passes for the source as a whole.
                unlistable.Add(new StoreListing(registration.Key, registration.DisplayName, availability));
            }
        }

        return new StoreListings(stores, unlistable);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        StudioSourceRegistration? registration = sources.Find(storeKey);
        if (registration is null)
        {
            return [];
        }

        StoreAvailability availability = sources.TryResolve(registration, out IStudioSource? source);
        return availability.IsAvailable && source is not null
            ? await ListDatabasesAsync(registration.Key, source, cancellationToken).ConfigureAwait(false)
            : [];
    }

    /// <inheritdoc />
    public async Task<StoreDatabaseListing> TryListDatabasesAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        StudioSourceRegistration? registration = sources.Find(storeKey);
        if (registration is null)
        {
            return new StoreDatabaseListing([], false);
        }

        StoreAvailability availability = sources.TryResolve(registration, out IStudioSource? source);
        if (!availability.IsAvailable || source is null)
        {
            return new StoreDatabaseListing([], true);
        }

        // A policy handler that throws while the databases are filtered propagates: the caller says it could not search, as for a failure.
        (IReadOnlyList<DatabaseListing> databases, bool failed) = await ListDatabasesCoreAsync(registration.Key, source, cancellationToken).ConfigureAwait(false);
        return new StoreDatabaseListing(databases, failed);
    }

    /// <inheritdoc />
    public async Task<StoreScopeFacts> DescribeAsync(string storeKey, string databaseId, CancellationToken cancellationToken = default)
    {
        StudioSourceRegistration? registration = sources.Find(storeKey);
        if (registration is null)
        {
            return StoreScopeFacts.None;
        }

        StoreAvailability availability = sources.TryResolve(registration, out IStudioSource? source);
        if (!availability.IsAvailable || source is null)
        {
            return StoreScopeFacts.Unavailable(availability.Message);
        }

        StudioDatabaseCardinality cardinality = source.Cardinality;
        bool showTenants = source.ShowsTenantSelector;
        if (!showTenants || string.IsNullOrEmpty(databaseId))
        {
            return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
        }

        TenantList discovered;
        string identity;

        try
        {
            IReadOnlyList<IStudioDatabase> databases = await source.DatabasesAsync(cancellationToken).ConfigureAwait(false);
            IStudioDatabase? database = null;
            foreach (IStudioDatabase candidate in databases)
            {
                if (string.Equals(candidate.Identity, databaseId, StringComparison.OrdinalIgnoreCase))
                {
                    database = candidate;
                    break;
                }
            }

            if (database is null)
            {
                return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
            }

            identity = database.Identity;

            // With the list just read, so that the database's same-identity twins answer for their tenants too.
            discovered = await source.DiscoverTenantsAsync(database, databases, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Warning the first time in a window, Debug after: the header asks this on every scope change
            // in every circuit, so one unreachable database would otherwise be a Warning per click.
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                throttle, "Store.Tenants", storeKey, null, StudioLogThrottle.KindOf(exception));

            if (product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioTenantDiscoveryFailed(level, exception, storeKey);
            }
            else
            {
                logger.TenantDiscoveryFailed(level, exception, storeKey);
            }

            return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
        }

        try
        {
            TenantList tenants = await FilterTenantsAsync(registration.Key, identity, discovered, cancellationToken)
                .ConfigureAwait(false);

            return new StoreScopeFacts(cardinality, showTenants, tenants, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Discovery worked and the host's own policy threw while it was being asked about the tenants
            // it found - which is not "could not discover the tenants", and filing it under 9219 sent
            // whoever read the log to the database instead of to their authorization handler. Its own
            // event, throttled the same way and for the same reason. Nothing is listed: a policy that could
            // not answer has not said yes to anything.
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                throttle, "Store.TenantPolicy", storeKey, identity, StudioLogThrottle.KindOf(exception));

            if (product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioTenantPolicyFailed(level, exception, storeKey, identity);
            }
            else
            {
                logger.TenantPolicyFailed(level, exception, storeKey, identity);
            }

            return new StoreScopeFacts(cardinality, showTenants, TenantList.Unavailable, null);
        }
    }

    /// <inheritdoc />
    public void Invalidate() => sources.Invalidate();

    /// <summary>
    /// The tenants of <paramref name="tenants" /> the visitor may address, in the order discovery found
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant selector is a listing like the source and database pickers, and it is filtered like them
    /// (plan section 4.2: authorization-filtered listings, and the count of tenants is itself withheld).
    /// Discovery answers for the whole database - the host's configured tenants, the source's descriptors,
    /// or the tenant columns themselves - so without this a visitor whose policy allows one tenant was
    /// shown every tenant's name, and could pick one only for the layout to refuse the page. Each id is
    /// asked about exactly as <see cref="StudioSourceResolver" /> would ask when that scope is resolved:
    /// the scope policy, with this source, this database and that tenant, and no capability.
    /// </para>
    /// <para>
    /// <b>A filtered listing is never claimed to be complete</b> (DB-0-fix-2, F7). Under a policy the
    /// answer is always marked <see cref="TenantList.IsTruncated" />, whether or not discovery hit its cap:
    /// "there may be tenants this list does not show" is true for every visitor a policy filters, and
    /// saying it only when discovery found more than two hundred told them that it had. It is also what
    /// lets <c>ScopeSelector</c> offer the filtered list with an "Other…" entry beside it - the way to reach
    /// an allowed tenant past the cap - and have <see cref="StudioState.SetScopeAsync" /> accept what is
    /// typed there, the same way whatever the source holds. What is typed is resolved like any other scope,
    /// so a tenant the policy refuses is refused by the layout and by every data call; the list the
    /// visitor is <em>shown</em> still names only the ones it allows.
    /// </para>
    /// <para>
    /// A handler that throws is not answered here; <see cref="DescribeAsync" /> logs it as event 9221 and
    /// lists nothing.
    /// </para>
    /// </remarks>
    private async Task<TenantList> FilterTenantsAsync(
        string storeKey,
        string databaseId,
        TenantList tenants,
        CancellationToken cancellationToken)
    {
        if (!authorization.IsEnabled || tenants.Ids.Count == 0)
        {
            return tenants;
        }

        List<string> allowed = await authorization
            .FilterAsync(
                tenants.Ids,
                tenantId => new StudioScope(storeKey, databaseId, tenantId),
                take: null,
                cancellationToken)
            .ConfigureAwait(false);

        return tenants with { Ids = allowed, IsTruncated = true };
    }

    /// <summary>The databases of one source the visitor may see, one per identity.</summary>
    /// <remarks>
    /// <b>One per identity, ignoring case</b>, as the Overview's cards are. Two master-table rows with one connection
    /// string are two database instances over one database, and the studio scopes by that identity and resolves it to
    /// the first match - so the picker offered the same database twice, as two options with one value, and the policy
    /// was asked about it twice. The first is kept, which is the one the resolver matches.
    /// </remarks>
    private async Task<IReadOnlyList<DatabaseListing>> ListDatabasesAsync(
        string storeKey,
        IStudioSource source,
        CancellationToken cancellationToken) =>
        (await ListDatabasesCoreAsync(storeKey, source, cancellationToken).ConfigureAwait(false)).Databases;

    /// <summary>The same list, and whether the enumeration failed (as distinct from a source with no database the visitor may see).</summary>
    private async Task<(IReadOnlyList<DatabaseListing> Databases, bool Failed)> ListDatabasesCoreAsync(
        string storeKey,
        IStudioSource source,
        CancellationToken cancellationToken)
    {
        List<DatabaseListing> listings = [];
        try
        {
            HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);

            foreach (IStudioDatabase database in await source.DatabasesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (identities.Add(database.Identity))
                {
                    listings.Add(new DatabaseListing(database.Identity, database.Name, database.Server));
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The same event, and the same throttle key, as the Overview's store card (9213).
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                throttle, "Store.Databases", storeKey, null, StudioLogThrottle.KindOf(exception));

            if (product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioStoreDatabasesUnreadable(level, exception, storeKey);
            }
            else
            {
                logger.StoreDatabasesUnreadable(level, exception, storeKey);
            }

            return ([], true);
        }

        return (await authorization
            .FilterAsync(
                listings,
                x => new StudioScope(storeKey, x.Identity, null),
                take: null,
                cancellationToken)
            .ConfigureAwait(false), false);
    }
}
