using JasperFx.Descriptors;

using Marten;

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
    private readonly IStoreInfoService storeInfo;
    private readonly ILogger<ConfigurationService> logger;

    public ConfigurationService(
        IOptions<MartenStudioOptions> options,
        StudioScopeResolver resolver,
        IStoreInfoService storeInfo,
        ILogger<ConfigurationService> logger)
    {
        this.options = options;
        this.resolver = resolver;
        this.storeInfo = storeInfo;
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
    /// The databases, reduced to the fields that cannot carry a credential.
    /// </summary>
    /// <remarks>
    /// A failure here breaks this card and nothing else: a <c>DynamicMultiple</c> tenancy has to reach a
    /// master table to answer, and a page that went blank because the tenant database list was briefly
    /// unreachable would be hiding the store settings that are right there in memory (plan §4.8).
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

            List<ConfiguredDatabase> databases = [];
            foreach (DatabaseDescriptor descriptor in usage.Databases)
            {
                databases.Add(ConfigurationDescriber.DescribeDatabase(descriptor));
            }

            if (databases.Count == 0 && usage.MainDatabase is { } main)
            {
                databases.Add(ConfigurationDescriber.DescribeDatabase(main));
            }

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
            logger.LogWarning(exception, "Marten Studio could not read the Postgres version of store {StoreKey}", storeKey);
            return null;
        }
    }
}
