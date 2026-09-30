using JasperFx.Descriptors;

using Marten;
using Marten.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// Reads the store's own configuration back out, for the Configuration page.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, and it resolves the scope before it reads anything - so a store the visitor may not see
/// refuses here exactly as it does everywhere else, rather than being a page that renders a different
/// store's settings because it never asked (plan D5).
/// </para>
/// <para>
/// The mapping itself lives in <see cref="ConfigurationDescriber" />, which is pure and knows nothing
/// about scopes or Postgres; this class is the part that resolves, fetches and catches. That split is
/// what lets the describer be tested against a real <c>DocumentStore</c> built on an unreachable host.
/// </para>
/// </remarks>
internal sealed class ConfigurationService : IConfigurationService
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioScopeResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly IStoreInfoService storeInfo;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<ConfigurationService> logger;

    public ConfigurationService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        StudioAuthorization authorization,
        IStoreInfoService storeInfo,
        StudioLogThrottle throttle,
        ILogger<ConfigurationService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.authorization = authorization;
        this.storeInfo = storeInfo;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<StudioConfiguration> DescribeAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;

        (IReadOnlyList<ConfiguredDatabase> databases, string? notice) =
            await DescribeDatabasesAsync(resolved, cancellationToken).ConfigureAwait(false);

        string? postgresVersion = await PostgresVersionAsync(resolved.Scope.StoreKey, cancellationToken).ConfigureAwait(false);

        StoreConfiguration store = ConfigurationDescriber.DescribeStore(
            resolved.Registration.Key,
            resolved.Registration.DisplayName,
            storeOptions,
            resolved.Database.AutoCreate,
            postgresVersion,
            databases,
            notice);

        return new StudioConfiguration(
            store,
            ConfigurationDescriber.DescribeEvents(storeOptions.Events),
            ConfigurationDescriber.DescribeDocumentTypes(storeOptions, options.Value.IsDocumentTypeVisible),
            postgresVersion);
    }

    /// <summary>
    /// The databases, reduced to the fields that cannot carry a credential, and to the ones - and the
    /// tenants - this visitor may address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A failure here breaks this card and nothing else: a <c>DynamicMultiple</c> tenancy has to reach a
    /// master table to answer, and a page that went blank because the tenant database list was briefly
    /// unreachable would be hiding the store settings that are right there in memory (plan §4.8).
    /// </para>
    /// <para>
    /// <b>Filtered like every other listing</b> (plan section 4.2): <c>ITenancy.DescribeDatabasesAsync</c>
    /// answers for the whole store, and this card used to print every database's server and name and
    /// every tenant's id to any visitor the scope admitted - the same thing the scope selector stopped
    /// doing in DB-0-fix. See <see cref="FilterAsync" />.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<ConfiguredDatabase> Databases, string? Notice)> DescribeDatabasesAsync(
        ResolvedScope resolved,
        CancellationToken cancellationToken)
    {
        try
        {
            DatabaseUsage usage = await resolved.Store.Options.Tenancy
                .DescribeDatabasesAsync(cancellationToken)
                .ConfigureAwait(false);

            List<DatabaseDescriptor> descriptors = [.. usage.Databases];
            if (descriptors.Count == 0 && usage.MainDatabase is { } main)
            {
                descriptors.Add(main);
            }

            IReadOnlyList<ConfiguredDatabase> databases = authorization.IsEnabled
                ? await FilterAsync(resolved, descriptors, cancellationToken).ConfigureAwait(false)
                : [.. descriptors.Select(ConfigurationDescriber.DescribeDatabase)];

            return (databases, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Marten Studio could not describe the databases of store {StoreKey}",
                resolved.Registration.Key);

            return ([], exception.Message);
        }
    }

    /// <summary>
    /// The databases of <paramref name="descriptors" /> the visitor may address, each with only the
    /// tenants the visitor may address, in the order the tenancy described them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked exactly as <see cref="StudioScopeCatalog" /> asks for the scope selector, which is exactly as
    /// <see cref="StudioScopeResolver" /> would ask when that scope is resolved: the store policy, no
    /// capability, <c>(store, database, null)</c> for a database and <c>(store, database, tenant)</c> for
    /// each of its tenants. A database the policy refuses is not listed - not greyed out, not counted -
    /// and a tenant it refuses is not named; nothing says how many were left out.
    /// </para>
    /// <para>
    /// The database a policy is asked about is keyed as every studio URL keys it, by
    /// <c>DatabaseId.Identity</c>. A descriptor does not carry that - its <c>Identifier</c> is the
    /// tenancy's own name for the database - but Weasel builds <c>Id</c> from the descriptor's own server
    /// and database name, so the database with the same two is the one it describes. A descriptor that
    /// matches none of <c>Storage.AllDatabases()</c> cannot be asked about, and is withheld.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ConfiguredDatabase>> FilterAsync(
        ResolvedScope resolved,
        IReadOnlyList<DatabaseDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        string storeKey = resolved.Registration.Key;
        IReadOnlyList<IMartenDatabase> known = await resolved.Store.Storage.AllDatabases().ConfigureAwait(false);

        List<(DatabaseDescriptor Descriptor, string Identity)> keyed = [];
        foreach (DatabaseDescriptor descriptor in descriptors)
        {
            IMartenDatabase? database = known.FirstOrDefault(x =>
                string.Equals(x.Id.Server, descriptor.ServerName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Id.Name, descriptor.DatabaseName, StringComparison.OrdinalIgnoreCase));

            if (database is not null)
            {
                keyed.Add((descriptor, database.Id.Identity));
            }
        }

        List<(DatabaseDescriptor Descriptor, string Identity)> visible = await authorization
            .FilterAsync(keyed, x => new StudioScope(storeKey, x.Identity, null), take: null, cancellationToken)
            .ConfigureAwait(false);

        List<ConfiguredDatabase> databases = [];
        foreach ((DatabaseDescriptor descriptor, string identity) in visible)
        {
            List<string> tenants = await authorization
                .FilterAsync(descriptor.TenantIds.ToArray(), tenantId => new StudioScope(storeKey, identity, tenantId), take: null, cancellationToken)
                .ConfigureAwait(false);

            databases.Add(ConfigurationDescriber.DescribeDatabase(descriptor) with { TenantIds = tenants });
        }

        return databases;
    }

    /// <summary>
    /// The server version, from the same place the Overview tile gets it.
    /// </summary>
    /// <remarks>
    /// <c>IDiagnostics.GetPostgresVersion()</c> is synchronous and opens a connection, so it is never
    /// called from here directly - <see cref="StoreInfoService" /> already runs it off the render path
    /// and caches it for the circuit.
    /// </remarks>
    private async Task<string?> PostgresVersionAsync(string storeKey, CancellationToken cancellationToken)
    {
        try
        {
            StudioOverview overview = await storeInfo.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            foreach (StoreOverview store in overview.Stores)
            {
                if (string.Equals(store.Key, storeKey, StringComparison.OrdinalIgnoreCase))
                {
                    return store.PostgresVersion;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The same event the Overview's store card logs (9214), under the same throttle key.
            LogLevel level = throttle.WarningOrDebug("Store.PostgresVersion", storeKey, null, StudioLogThrottle.KindOf(exception));
            logger.PostgresVersionUnreadable(level, exception, storeKey);

            return null;
        }
    }
}
