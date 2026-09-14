using JasperFx.Descriptors;

using Marten;
using Marten.Schema;
using Marten.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>One database of a store, as the Overview page draws it.</summary>
/// <param name="Identity">Marten's <c>DatabaseId.Identity</c> - the key everything else is scoped by.</param>
/// <param name="Name">The database name.</param>
/// <param name="Server">The server it lives on. Never a connection string.</param>
/// <param name="SchemaNames">Every schema this store owns objects in.</param>
/// <param name="Error">
/// What went wrong reading this one, or <see langword="null" />. A database that cannot be reached breaks
/// its own region of the page and nothing else (plan section 4.8).
/// </param>
internal sealed record DatabaseOverview(
    string Identity,
    string Name,
    string Server,
    IReadOnlyList<string> SchemaNames,
    string? Error);

/// <summary>The event store's configuration, as three facts a person can act on.</summary>
internal sealed record EventStoreFacts(
    string StreamIdentity,
    string AppendMode,
    string TenancyStyle,
    string SchemaName,
    int KnownEventTypeCount);

/// <summary>One store on the Overview page.</summary>
internal sealed record StoreOverview(
    string Key,
    string DisplayName,
    bool IsAvailable,
    string? UnavailableMessage,
    DatabaseCardinality Cardinality,
    int DocumentTypeCount,
    string? PostgresVersion,
    EventStoreFacts? Events,
    IReadOnlyList<DatabaseOverview> Databases);

/// <summary>Everything the Overview page renders.</summary>
internal sealed record StudioOverview(IReadOnlyList<StoreOverview> Stores);

/// <summary>
/// What the Overview page reads. An interface so a component test can hand the page facts without a
/// Postgres.
/// </summary>
internal interface IStoreInfoService
{
    /// <summary>
    /// Every store the visitor may see, with the databases of each they may see.
    /// </summary>
    Task<StudioOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads what a store says about itself: its databases, its schemas, its document types and its event
/// store configuration.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through <see cref="StudioScopeResolver" />, so a store or database the visitor may not
/// see is absent from the page rather than rendered with a refusal - the Overview is a listing, and a
/// listing that named what it was hiding would be the leak the per-store policy exists to prevent.
/// </para>
/// <para>
/// The Postgres version is <c>IDiagnostics.GetPostgresVersion()</c>, which is synchronous and opens a
/// connection. It is called off the render path and cached for the lifetime of this scope, which is one
/// circuit: a tile that opened a connection on every render would be a connection per render per visitor.
/// </para>
/// </remarks>
internal sealed class StoreInfoService : IStoreInfoService
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly MartenStoreRegistry registry;
    private readonly StudioScopeResolver resolver;
    private readonly ILogger<StoreInfoService> logger;
    private readonly Dictionary<string, string?> postgresVersions = new(StringComparer.OrdinalIgnoreCase);

    public StoreInfoService(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        StudioScopeResolver resolver,
        ILogger<StoreInfoService> logger)
    {
        this.options = options;
        this.registry = registry;
        this.resolver = resolver;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<StudioOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        List<StoreOverview> stores = [];

        foreach (MartenStoreRegistration registration in registry.Registrations(options))
        {
            StoreOverview? overview = await DescribeStoreAsync(registration, cancellationToken).ConfigureAwait(false);
            if (overview is not null)
            {
                stores.Add(overview);
            }
        }

        return new StudioOverview(stores);
    }

    private async Task<StoreOverview?> DescribeStoreAsync(
        MartenStoreRegistration registration,
        CancellationToken cancellationToken)
    {
        ResolvedScope resolved;
        try
        {
            // An empty database identity asks for the store's default database, which is the only one a
            // single-database store has and the right place to start for every other shape.
            resolved = await resolver
                .ResolveAsync(new StudioScope(registration.Key, string.Empty, null), null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
        catch (StudioStoreUnavailableException unavailable)
        {
            return new StoreOverview(
                registration.Key,
                registration.DisplayName,
                IsAvailable: false,
                unavailable.Reason,
                DatabaseCardinality.Single,
                DocumentTypeCount: 0,
                PostgresVersion: null,
                Events: null,
                Databases: []);
        }

        IDocumentStore store = resolved.Store;
        IReadOnlyStoreOptions storeOptions = store.Options;

        List<DatabaseOverview> databases = [];
        try
        {
            string[] schemaNames = store.Storage.AllSchemaNames();
            foreach (IMartenDatabase database in await store.Storage.AllDatabases().ConfigureAwait(false))
            {
                DatabaseOverview? described = await DescribeDatabaseAsync(registration.Key, database, schemaNames, cancellationToken)
                    .ConfigureAwait(false);
                if (described is not null)
                {
                    databases.Add(described);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not list the databases of store {StoreKey}", registration.Key);
            databases.Add(new DatabaseOverview(
                resolved.Database.Id.Identity,
                resolved.Database.Id.Name,
                resolved.Database.Id.Server,
                [],
                exception.Message));
        }

        return new StoreOverview(
            registration.Key,
            registration.DisplayName,
            IsAvailable: true,
            UnavailableMessage: null,
            storeOptions.Tenancy.Cardinality,
            CountVisibleDocumentTypes(storeOptions),
            await PostgresVersionAsync(registration.Key, store).ConfigureAwait(false),
            DescribeEvents(storeOptions),
            databases);
    }

    private async Task<DatabaseOverview?> DescribeDatabaseAsync(
        string storeKey,
        IMartenDatabase database,
        string[] schemaNames,
        CancellationToken cancellationToken)
    {
        try
        {
            // Resolving again per database is the per-database authorization check, and it is the reason a
            // visitor who may see one database of a store does not thereby see the others.
            _ = await resolver
                .ResolveAsync(new StudioScope(storeKey, database.Id.Identity, null), null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        return new DatabaseOverview(database.Id.Identity, database.Id.Name, database.Id.Server, schemaNames, null);
    }

    private int CountVisibleDocumentTypes(IReadOnlyStoreOptions storeOptions)
    {
        Func<Type, bool>? visible = options.Value.IsDocumentTypeVisible;
        if (visible is null)
        {
            return storeOptions.AllKnownDocumentTypes().Count;
        }

        int count = 0;
        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            if (visible(documentType.DocumentType))
            {
                count++;
            }
        }

        return count;
    }

    private static EventStoreFacts DescribeEvents(IReadOnlyStoreOptions storeOptions)
    {
        Marten.Events.IReadOnlyEventStoreOptions events = storeOptions.Events;
        return new EventStoreFacts(
            events.StreamIdentity.ToString(),
            events.AppendMode.ToString(),
            events.TenancyStyle.ToString(),
            events.DatabaseSchemaName,
            events.AllKnownEventTypes().Count);
    }

    /// <summary>
    /// The server version, read once per store per circuit.
    /// </summary>
    /// <remarks>
    /// <c>GetPostgresVersion()</c> is synchronous and opens a connection, so it is pushed onto the thread
    /// pool rather than run on the circuit's renderer. It answers for the store's default connection, so
    /// it is a fact about the store rather than about each of its databases.
    /// </remarks>
    private async Task<string?> PostgresVersionAsync(string storeKey, IDocumentStore store)
    {
        if (postgresVersions.TryGetValue(storeKey, out string? cached))
        {
            return cached;
        }

        string? version;
        try
        {
            Version resolved = await Task.Run(store.Diagnostics.GetPostgresVersion).ConfigureAwait(false);
            version = resolved.ToString();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Marten Studio could not read the Postgres version of store {StoreKey}", storeKey);
            version = null;
        }

        postgresVersions[storeKey] = version;
        return version;
    }
}
