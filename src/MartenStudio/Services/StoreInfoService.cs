using JasperFx.Descriptors;

using Marten;
using Marten.Schema;
using Marten.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>One database of a store, as the Overview page draws it.</summary>
/// <param name="Identity">
/// Marten's <c>DatabaseId.Identity</c> - the key everything else is scoped by. Empty on the one card that
/// says the store's databases could not be listed, which has no database to name.
/// </param>
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
/// Every store and database is asked about exactly as <see cref="StudioScopeResolver" /> would ask - the
/// store policy, for the store before anything is built, then for each database with no tenant - so one the
/// visitor may not see is absent from the page rather than rendered with a refusal. The Overview is a
/// listing, and a listing that named what it was hiding would be the leak the per-store policy exists to
/// prevent. The questions go to <see cref="StudioAuthorization" /> directly rather than through the
/// resolver, because every resolution is an enumeration of the store's databases of its own
/// (<see cref="ListDatabasesAsync" />).
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
    private readonly StudioAuthorization authorization;
    private readonly IServiceProvider provider;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<StoreInfoService> logger;
    private readonly Dictionary<string, string?> postgresVersions = new(StringComparer.OrdinalIgnoreCase);

    public StoreInfoService(
        IOptions<MartenStudioOptions> options,
        MartenStoreRegistry registry,
        StudioAuthorization authorization,
        IServiceProvider provider,
        StudioLogThrottle throttle,
        ILogger<StoreInfoService> logger)
    {
        this.options = options;
        this.registry = registry;
        this.authorization = authorization;
        this.provider = provider;
        this.throttle = throttle;
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
        string storeKey = registration.Key;

        // 1. The store policy about the store as a whole, before anything is built: the question the resolver
        //    asks first for an empty database identity, and the reason a store the visitor may not see is
        //    absent rather than drawn with a refusal.
        if (!await authorization
                .IsAuthorizedAsync(new StudioScope(storeKey, string.Empty, null), capability: null, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        // 2. The store itself, which may refuse to build: its own region, not a broken page (plan section 4.8).
        StoreAvailability availability = registry.TryResolve(registration, provider, out IDocumentStore? store);
        if (!availability.IsAvailable || store is null)
        {
            return new StoreOverview(
                storeKey,
                registration.DisplayName,
                IsAvailable: false,
                availability.Message ?? "unknown reason",
                DatabaseCardinality.Single,
                DocumentTypeCount: 0,
                PostgresVersion: null,
                Events: null,
                Databases: []);
        }

        // 3. Its databases, from one enumeration.
        IReadOnlyList<DatabaseOverview>? databases = await ListDatabasesAsync(storeKey, store, cancellationToken)
            .ConfigureAwait(false);

        if (databases is null)
        {
            return null;
        }

        IReadOnlyStoreOptions storeOptions = store.Options;

        return new StoreOverview(
            storeKey,
            registration.DisplayName,
            IsAvailable: true,
            UnavailableMessage: null,
            storeOptions.Tenancy.Cardinality,
            CountVisibleDocumentTypes(storeOptions),
            await PostgresVersionAsync(storeKey, store, cancellationToken).ConfigureAwait(false),
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

    /// <summary>
    /// The databases of <paramref name="store" /> the visitor may see, from one <c>AllDatabases()</c> - or
    /// <see langword="null" /> when the store has none, which the resolver always answered "not found" for an
    /// empty identity, and which is therefore not drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One enumeration per store per load.</b> This used to resolve the store's empty scope, enumerate, and
    /// then resolve every database again for its authorization - and every resolution is an enumeration of its
    /// own, so a load cost N + 2 of them per store. On a master-table or sharded tenancy each one is a read of
    /// the lookup table (AGENTS.md D27), and the per-database resolution added nothing the policy does not
    /// ask: the store was already built and the database already in hand. So the one list is filtered by the
    /// question the resolver would have asked - the store policy, with this store, that database and no
    /// tenant - through <see cref="StudioAuthorization.FilterAsync{T}" />, which fetches the visitor once for
    /// the whole sweep.
    /// </para>
    /// <para>
    /// <b>One card per identity.</b> Two databases with one <c>Id.Identity</c> - two master-table rows with the
    /// same connection string are two <c>MartenDatabase</c> objects over one database - are one database to
    /// the studio, which scopes by that identity and resolves it to the first match. The page keys its cards
    /// by it, and two siblings under one Blazor <c>@key</c> end the circuit rather than the render (AGENTS.md
    /// hard rule 10), so the first is drawn and its twin is not.
    /// </para>
    /// <para>
    /// A store whose databases cannot be listed - a master database that is unreachable - breaks its own
    /// region and nothing else: one card carrying the failure, with no identity, because none could be read.
    /// It used to fail the whole page, since the resolver's enumeration came first and threw.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<DatabaseOverview>?> ListDatabasesAsync(
        string storeKey,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<IMartenDatabase> all = await store.Storage.AllDatabases().ConfigureAwait(false);
            if (all.Count == 0)
            {
                return null;
            }

            List<IMartenDatabase> distinct = [];
            HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);
            foreach (IMartenDatabase database in all)
            {
                if (identities.Add(database.Id.Identity))
                {
                    distinct.Add(database);
                }
            }

            List<IMartenDatabase> visible = await authorization
                .FilterAsync(distinct, x => new StudioScope(storeKey, x.Id.Identity, null), take: null, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<string> schemaNames = SchemaNames(store.Options);

            List<DatabaseOverview> databases = new(visible.Count);
            foreach (IMartenDatabase database in visible)
            {
                databases.Add(new DatabaseOverview(database.Id.Identity, database.Id.Name, database.Id.Server, schemaNames, null));
            }

            return databases;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Every Overview load in every circuit, and the scope selector asks the same question: one
            // Warning per store and kind of failure per window, Debug in between (event 9213).
            LogLevel level = throttle.WarningOrDebug("Store.Databases", storeKey, null, StudioLogThrottle.KindOf(exception));
            logger.StoreDatabasesUnreadable(level, exception, storeKey);

            return [new DatabaseOverview(string.Empty, string.Empty, string.Empty, [], exception.Message)];
        }
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
    /// <para>
    /// <c>GetPostgresVersion()</c> is synchronous and opens a connection, so it is pushed onto the thread
    /// pool rather than run on the circuit's renderer. It answers for the store's default connection, so
    /// it is a fact about the store rather than about each of its databases.
    /// </para>
    /// <para>
    /// <b>A store with no default database has no store-wide version, and that is a value</b>
    /// (<see cref="HasDefaultDatabase" />). Marten's <c>GetPostgresVersion</c> opens
    /// <c>Tenancy.Default.Database</c>, which a master-table or sharded tenancy refuses with
    /// <see cref="NotSupportedException" /> - every operation there needs a tenant - and a static multi-tenancy with no
    /// database marked <c>AsDefault()</c> does not have; its databases may sit on servers of different versions. So the
    /// version is "unknown" without a read, at Debug: it used to be event 9214 at Warning, once a window for every such
    /// host, for a state the host configured. A store that has a default database and cannot be read is still 9214,
    /// throttled - that is an anomaly.
    /// </para>
    /// </remarks>
    private async Task<string?> PostgresVersionAsync(string storeKey, IDocumentStore store, CancellationToken cancellationToken)
    {
        if (postgresVersions.TryGetValue(storeKey, out string? cached))
        {
            return cached;
        }

        bool noDefaultDatabase = !HasDefaultDatabase(store.Options.Tenancy);

        string? version = noDefaultDatabase
            ? null
            : await ReadPostgresVersionAsync(
                store.Diagnostics.GetPostgresVersion,
                options.Value.QueryTimeout,
                cancellationToken).ConfigureAwait(false);

        if (version is null)
        {
            // Once per circuit per store, and every circuit asks: event 9214, throttled across them - and Debug for a
            // tenancy that has no default database to read, which is the host's configuration rather than a fault.
            LogLevel level = noDefaultDatabase
                ? LogLevel.Debug
                : throttle.WarningOrDebug("Store.PostgresVersion", storeKey, null, kind: null);
            logger.PostgresVersionUnreadable(level, null, storeKey);
        }

        postgresVersions[storeKey] = version;
        return version;
    }

    /// <summary>
    /// Whether <paramref name="tenancy" /> has a default database for <c>GetPostgresVersion</c> to open - asked of the
    /// tenancy's memory, never of a database.
    /// </summary>
    /// <remarks>
    /// <c>Tenancy.Default</c> is a field or an in-memory lookup on every Marten tenancy (read at <c>V9.39.0</c> and
    /// <c>V9.50.2</c>): <c>DefaultTenancy</c> and its tenant-partitioned subclass hold one; <c>StaticMultiTenancy</c> holds
    /// the database marked <c>AsDefault()</c>, or none; <c>SingleServerMultiTenancy</c> takes the first tenant it has
    /// built; <c>MasterTableTenancy</c> and <c>ShardedTenancy</c> throw <see cref="NotSupportedException" />. Nothing
    /// here enumerates, builds or connects.
    /// <para>
    /// Anything else a host's own tenancy throws from <c>Default</c> is not an answer, so it is read as "has one": the
    /// version read is attempted, fails the way it always did, and is event 9214 at the throttled Warning. It must never
    /// leave here - this runs outside the version read's own guard, and a throw would take the Overview's stores region
    /// with it.
    /// </para>
    /// </remarks>
    internal static bool HasDefaultDatabase(ITenancy tenancy)
    {
        ArgumentNullException.ThrowIfNull(tenancy);

        try
        {
            return tenancy.Default?.Database is not null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return true;
        }
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
