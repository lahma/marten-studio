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
            IReadOnlyList<string> schemaNames = SchemaNames(storeOptions);
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
            await PostgresVersionAsync(registration.Key, store, cancellationToken).ConfigureAwait(false),
            DescribeEvents(storeOptions),
            databases);
    }

    /// <summary>
    /// Every schema this store owns objects in, read from its configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> <c>store.Storage.AllSchemaNames()</c>, which is not a read. It goes
    /// <c>AllObjects</c> → <c>BuildFeatureSchemas</c> → <c>MartenDatabase.Sequences</c> →
    /// <c>resetSequences</c> → <c>executeMigration</c>: opening the Overview page ran a migration against
    /// the host's database, which is precisely what a browser must never do by being looked at, and on a
    /// store whose tenancy had not been resolved it opened that connection against the default host and
    /// port rather than the configured one and failed outright.
    /// </para>
    /// <para>
    /// The names are configuration and the configuration is already in hand: the store's own schema, the
    /// event store's, and whatever each document type was mapped to. Ordered and de-duplicated so the
    /// page does not reorder itself between loads.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> SchemaNames(IReadOnlyStoreOptions storeOptions)
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        Add(storeOptions.DatabaseSchemaName);
        Add(storeOptions.Events.DatabaseSchemaName);

        foreach (IDocumentType documentType in storeOptions.AllKnownDocumentTypes())
        {
            Add(documentType.DatabaseSchemaName);
        }

        return [.. names];

        void Add(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }
    }

    private async Task<DatabaseOverview?> DescribeDatabaseAsync(
        string storeKey,
        IMartenDatabase database,
        IReadOnlyList<string> schemaNames,
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
    private async Task<string?> PostgresVersionAsync(string storeKey, IDocumentStore store, CancellationToken cancellationToken)
    {
        if (postgresVersions.TryGetValue(storeKey, out string? cached))
        {
            return cached;
        }

        string? version = await ReadPostgresVersionAsync(
            store.Diagnostics.GetPostgresVersion,
            options.Value.QueryTimeout,
            cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            logger.LogWarning("Marten Studio could not read the Postgres version of store {StoreKey}", storeKey);
        }

        postgresVersions[storeKey] = version;
        return version;
    }

    /// <summary>
    /// Runs a blocking version read off the renderer, bounded by <paramref name="timeout" />, and answers
    /// <see langword="null" /> when it does not come back in time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is the point. A <c>Task.Run</c> over a synchronous Npgsql call cannot be cancelled - the
    /// thread stays blocked until the connection attempt gives up on its own, and a host whose Postgres is
    /// behind a firewall that drops packets rather than refusing them is a page that never finishes
    /// loading. <c>WaitAsync</c> stops the waiting, not the work; the orphaned task is left to finish and
    /// its exception is observed rather than raised on the finalizer thread.
    /// </para>
    /// <para>
    /// "Unknown" is a value, not an error (plan section 4.8): a version tile that cannot report draws
    /// differently from one that reports zero, and the rest of the Overview is unaffected.
    /// </para>
    /// </remarks>
    internal static async Task<string?> ReadPostgresVersionAsync(
        Func<Version> getPostgresVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<Version> read = Task.Run(getPostgresVersion, CancellationToken.None);

        // Nothing here re-raises what the orphaned read eventually throws, but something has to look at
        // it or an unobserved connection failure becomes an unhandled exception when it is collected.
        _ = read.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            Version resolved = await read.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return resolved.ToString();
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
