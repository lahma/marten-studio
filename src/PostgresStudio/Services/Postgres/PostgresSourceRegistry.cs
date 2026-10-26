using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// The sources PostgresStudio offers, process-wide: the application's unkeyed <see cref="NpgsqlDataSource" /> found in
/// the service collection, then what <see cref="PostgresStudioOptions.AddDataSource" /> and
/// <see cref="PostgresStudioOptions.AddDatabaseSource" /> named - built on first use, a failure remembered for ten
/// seconds, and a host's database list remembered for a minute.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lazy, like MartenStudio's store registry.</b> Holds the <see cref="IServiceCollection" /> captured at
/// <c>AddPostgresStudio</c> time and scans it on first use, because <c>AddPostgresStudio()</c> may be called before
/// <c>AddNpgsqlDataSource()</c>. Only a descriptor whose service type is exactly <see cref="NpgsqlDataSource" />, unkeyed,
/// counts: <c>AddNpgsqlDataSource</c> also registers <c>DbDataSource</c> and a transient <c>NpgsqlConnection</c>, and
/// neither is a source (PS-0, E7: it registers factories only, so nothing here reads an instance off a descriptor). And
/// the <b>last</b> such descriptor decides, because it is the one the container resolves: when it is a singleton the
/// source is listed, and when it is scoped or transient it is not, whatever an earlier registration said - resolving it
/// would build a pool per circuit, or per resolution (event 9300).
/// </para>
/// <para>
/// <b>Built from the root container, once.</b> A singleton itself, it holds the application's root
/// <see cref="IServiceProvider" />: the default data source is resolved from it, and a factory given to
/// <c>AddDataSource</c> or <c>AddDatabaseSource</c> is called with it exactly once - under a lock per source, so two
/// circuits asking at the same moment do not call it twice - and what it answers is kept for the process. Since that
/// answer outlives every scope, the provider a factory is handed resolves a data source or a database source only when
/// the container registers it as a singleton: a scoped or transient one is refused, and the source is unavailable with a
/// sentence that says why, rather than one visitor's instance being kept for everybody.
/// </para>
/// <para>
/// <b>Nothing connects.</b> Building a source is resolving it from the container or calling the host's factory once;
/// listing a host's databases is the host's own <see cref="IPostgresDatabaseSource.ListAsync" />, which by contract
/// builds and changes nothing. A data source the studio got from the application is never disposed by it.
/// </para>
/// <para>
/// <b>One identity, one database, for the life of the process.</b> Caches that are facts about a database - the
/// catalog, its columns, its sessions - are keyed on <see cref="IStudioDatabase.Identity" /> alone, and here an identity
/// is a name a source chose. So every identity is claimed by the first source that answers it: a single data source
/// claims its own key, and a host-listed name that another source already holds is left out of the later source's list
/// and counted (9301). Two sources can never present one identity for two databases, which would hand one database's
/// cached catalog to a visitor admitted to the other.
/// </para>
/// </remarks>
internal sealed class PostgresSourceRegistry
{
    /// <summary>How long a source that would not build is answered from memory.</summary>
    internal static readonly TimeSpan FailureCacheWindow = TimeSpan.FromSeconds(10);

    /// <summary>How long a host's database list is answered from memory (API design §3.3).</summary>
    internal static readonly TimeSpan ListCacheWindow = TimeSpan.FromSeconds(60);

    /// <summary>The most names one host list is read to (API design §3.3).</summary>
    internal const int MaxListedDatabases = 10_000;

    private readonly IServiceCollection services;
    private readonly IServiceProvider root;
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<string, Lock> builds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StoreAvailability> failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> built = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostList> lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> identities = new(StringComparer.OrdinalIgnoreCase);

    private DefaultScan? scanned;
    private long listGeneration;

    /// <param name="services">The application's service collection, scanned on first use.</param>
    /// <param name="root">The application's root service provider: what the default source and every factory resolve from.</param>
    /// <param name="timeProvider">The clock behind every cache and timeout; the system's when <see langword="null" />.</param>
    public PostgresSourceRegistry(IServiceCollection services, IServiceProvider root, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(root);

        this.services = services;
        this.root = root;
        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The clock behind the caches and the host timeouts.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Every source, in order: <c>default</c> (when the container's last unkeyed data source is a singleton and
    /// <see cref="PostgresStudioOptions.IncludeDefaultDataSource" /> is on), then each <c>AddDataSource</c>, then each
    /// <c>AddDatabaseSource</c>, in call order (API design §3.7).
    /// </summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="logger">Where the first scan says what it skipped, or <see langword="null" />.</param>
    public IReadOnlyList<StudioSourceRegistration> Registrations(PostgresStudioOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<StudioSourceRegistration> registrations = [];

        if (options.IncludeDefaultDataSource && Scan(logger).HasDefault)
        {
            registrations.Add(new StudioSourceRegistration(PostgresSourceNames.Default, "Default", StudioSourceKind.DataSource));
        }

        foreach (PostgresSourceDefinition definition in options.DataSources)
        {
            registrations.Add(new StudioSourceRegistration(definition.Name, definition.Name, StudioSourceKind.DataSource));
        }

        foreach (PostgresSourceDefinition definition in options.DatabaseSources)
        {
            registrations.Add(new StudioSourceRegistration(definition.Name, definition.Name, StudioSourceKind.HostProvider));
        }

        // A single data source's one database is named by the source's key: claimed before any host list is read.
        foreach (StudioSourceRegistration registration in registrations)
        {
            if (registration.Kind == StudioSourceKind.DataSource)
            {
                identities.TryAdd(registration.Key, registration.Key);
            }
        }

        return registrations;
    }

    /// <summary>The registration with this key, ignoring case, or <see langword="null" />.</summary>
    public StudioSourceRegistration? Find(string? key, PostgresStudioOptions options, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (StudioSourceRegistration registration in Registrations(options, logger))
        {
            if (string.Equals(registration.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return registration;
            }
        }

        return null;
    }

    /// <summary>The data source behind a <see cref="StudioSourceKind.DataSource" /> registration, or why there is none.</summary>
    /// <param name="registration">A registration this registry answered.</param>
    /// <param name="options">The studio's options.</param>
    /// <param name="dataSource">The application's data source, when available.</param>
    public StoreAvailability TryResolveDataSource(
        StudioSourceRegistration registration,
        PostgresStudioOptions options,
        [NotNullWhen(true)] out NpgsqlDataSource? dataSource)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        PostgresSourceDefinition? definition = Definition(registration, options.DataSources);
        bool isDefault = string.Equals(registration.Key, PostgresSourceNames.Default, StringComparison.OrdinalIgnoreCase);

        StoreAvailability availability = TryBuild(
            registration.Key,
            typeof(NpgsqlDataSource),
            isDefault
                ? static services => services.GetRequiredService<NpgsqlDataSource>()
                : definition?.DataSource is { } factory
                    ? services => factory(services) ?? throw new InvalidOperationException("The factory returned no data source.")
                    : null,
            out object? instance);

        dataSource = instance as NpgsqlDataSource;
        return availability;
    }

    /// <summary>The host's database source behind a <see cref="StudioSourceKind.HostProvider" /> registration, or why there is none.</summary>
    /// <param name="registration">A registration this registry answered.</param>
    /// <param name="options">The studio's options.</param>
    /// <param name="source">The host's source, when available.</param>
    public StoreAvailability TryResolveDatabaseSource(
        StudioSourceRegistration registration,
        PostgresStudioOptions options,
        [NotNullWhen(true)] out IPostgresDatabaseSource? source)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        PostgresSourceDefinition? definition = Definition(registration, options.DatabaseSources);

        StoreAvailability availability = TryBuild(
            registration.Key,
            typeof(IPostgresDatabaseSource),
            definition?.DatabaseSource is { } factory
                ? services => factory(services) ?? throw new InvalidOperationException("The factory returned no database source.")
                : null,
            out object? instance);

        source = instance as IPostgresDatabaseSource;
        return availability;
    }

    /// <summary>
    /// The names <paramref name="source" /> lists, valid, distinct and claimed for it - from memory for a minute, read
    /// once however many callers ask at the same moment, and given <paramref name="timeout" /> to answer.
    /// </summary>
    /// <param name="key">The source's key.</param>
    /// <param name="source">The host's source.</param>
    /// <param name="timeout">How long the host's listing may take: the studio's <c>QueryTimeout</c>.</param>
    /// <param name="logger">Where names left out are counted (9301).</param>
    /// <param name="throttle">The studio's throttle, or <see langword="null" /> for a Warning every time.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never a listing other callers share.</param>
    /// <exception cref="Exception">Whatever the host's listing threw, or a <see cref="TimeoutException" />; nothing is remembered then.</exception>
    public async ValueTask<IReadOnlyList<string>> ListAsync(
        string key,
        IPostgresDatabaseSource source,
        TimeSpan timeout,
        ILogger logger,
        StudioLogThrottle? throttle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        HostList list;
        lock (gate)
        {
            long generation = listGeneration;
            DateTimeOffset now = TimeProvider.GetUtcNow();

            if (!lists.TryGetValue(key, out HostList? cached)
                || cached.Generation != generation
                || cached.Pending.IsFaulted
                || cached.Pending.IsCanceled
                || (cached.Pending.IsCompletedSuccessfully && now - cached.ReadAt >= ListCacheWindow))
            {
                cached = new HostList(ReadListAsync(key, source, timeout, logger, throttle), now, generation);
                lists[key] = cached;
            }

            list = cached;
        }

        return await list.Pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets every host's list, for the scope selector's refresh: the next listing asks the host again.</summary>
    public void InvalidateLists()
    {
        lock (gate)
        {
            listGeneration++;
            lists.Clear();
        }
    }

    private async Task<IReadOnlyList<string>> ReadListAsync(
        string key,
        IPostgresDatabaseSource source,
        TimeSpan timeout,
        ILogger logger,
        StudioLogThrottle? throttle)
    {
        // Off the caller's context and never on its token: the listing is shared, and one caller going away must not
        // cancel it for the others.
        await Task.Yield();

        using CancellationTokenSource limit = new(timeout, TimeProvider);

        List<string> names = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int invalid = 0;
        int duplicate = 0;
        int another = 0;
        bool capped = false;

        try
        {
            await foreach (string? name in source.ListAsync(limit.Token).WithCancellation(limit.Token).ConfigureAwait(false))
            {
                if (names.Count >= MaxListedDatabases)
                {
                    capped = true;
                    break;
                }

                if (name is null || !PostgresSourceNames.IsValidDatabaseName(name))
                {
                    invalid++;
                    continue;
                }

                if (!seen.Add(name))
                {
                    duplicate++;
                    continue;
                }

                string owner = identities.GetOrAdd(name, key);
                if (!string.Equals(owner, key, StringComparison.OrdinalIgnoreCase))
                {
                    another++;
                    continue;
                }

                names.Add(name);
            }
        }
        catch (OperationCanceledException exception) when (limit.IsCancellationRequested)
        {
            // The host's listing ran out of time: a failure of the source, said as one - never a cancellation, which
            // every caller would read as its own page going away.
            throw new TimeoutException(
                "The database source '" + key + "' did not list its databases within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.",
                exception);
        }

        Report(logger, throttle, key, invalid, "they do not match ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$");
        Report(logger, throttle, key, duplicate, "they repeat another name of the source, ignoring case");
        Report(logger, throttle, key, another, "another source already names a database the same, ignoring case");

        if (capped)
        {
            // The list is not read past the cap - that is the cap's point - so how many more there were is not known: the
            // count is the first name left out.
            Report(logger, throttle, key, 1, "the source listed more than " + MaxListedDatabases.ToString(CultureInfo.InvariantCulture) + " databases, and the list was not read past the cap, so any more are not counted");
        }

        return names;
    }

    private static void Report(ILogger logger, StudioLogThrottle? throttle, string key, int count, string reason)
    {
        if (count == 0)
        {
            return;
        }

        LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, "Postgres.ListedNames", key, null, reason);
        logger.DatabaseNamesSkipped(level, count, key, reason);
    }

    /// <summary>
    /// Builds a source once: under its own lock, so concurrent first callers wait for the one call rather than making a
    /// second, and kept for the process once it succeeded. A failure is remembered for ten seconds.
    /// </summary>
    private StoreAvailability TryBuild(
        string key,
        Type serviceType,
        Func<IServiceProvider, object>? build,
        out object? instance)
    {
        instance = null;

        if (build is null)
        {
            return StoreAvailability.Unavailable("No data source is registered under '" + key + "'.", TimeProvider.GetUtcNow());
        }

        lock (builds.GetOrAdd(key, static _ => new Lock()))
        {
            lock (gate)
            {
                if (built.TryGetValue(key, out object? existing))
                {
                    instance = existing;
                    return StoreAvailability.Available;
                }

                if (failures.TryGetValue(key, out StoreAvailability? failed)
                    && failed.At is { } failedAt
                    && TimeProvider.GetUtcNow() - failedAt < FailureCacheWindow)
                {
                    return failed;
                }
            }

            try
            {
                object created = build(new SingletonServices(root, services));

                lock (gate)
                {
                    failures.Remove(key);
                    built[key] = created;
                }

                instance = created;
                return StoreAvailability.Available;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StoreAvailability unavailable = StoreAvailability.Unavailable(exception.Message, TimeProvider.GetUtcNow());

                lock (gate)
                {
                    failures[key] = unavailable;
                }

                // The failure cache expires every ten seconds and every polling page asks again, so the Warning is
                // throttled across them, as MartenStudio's store registry throttles the same event (9210).
                LogLevel level = StudioLogThrottle.LevelOrWarning(
                    root.GetService<StudioLogThrottle>(),
                    "Store.Resolve",
                    key,
                    null,
                    StudioLogThrottle.KindOf(exception));

                root.GetService<ILoggerFactory>()?
                    .CreateLogger<PostgresSourceRegistry>()
                    .StoreUnavailable(level, key, serviceType.FullName ?? serviceType.Name, exception.Message);

                return unavailable;
            }
        }
    }

    private static PostgresSourceDefinition? Definition(StudioSourceRegistration registration, IReadOnlyList<PostgresSourceDefinition> definitions)
    {
        foreach (PostgresSourceDefinition definition in definitions)
        {
            if (string.Equals(definition.Name, registration.Key, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private DefaultScan Scan(ILogger? logger)
    {
        if (scanned is { } done)
        {
            return done;
        }

        lock (gate)
        {
            if (scanned is { } raced)
            {
                return raced;
            }

            // The last unkeyed registration is the one the container resolves, so it alone decides.
            ServiceDescriptor? last = SingletonServices.Last(services, typeof(NpgsqlDataSource), keyed: false, key: null);
            bool hasDefault = last is { Lifetime: ServiceLifetime.Singleton };

            if (last is { Lifetime: not ServiceLifetime.Singleton } skipped && logger?.IsEnabled(LogLevel.Information) == true)
            {
                logger.DataSourceLifetimeSkipped(skipped.Lifetime.ToString());
            }

            scanned = new DefaultScan(hasDefault);
            return scanned;
        }
    }

    /// <summary>What the scan found: whether the container's last unkeyed data source is a singleton.</summary>
    private sealed record DefaultScan(bool HasDefault);

    /// <summary>One host's list, shared by every caller while it is fresh.</summary>
    private sealed record HostList(Task<IReadOnlyList<string>> Pending, DateTimeOffset ReadAt, long Generation);

    /// <summary>
    /// The root provider as a source's factory sees it: everything resolves as the container would resolve it, except a
    /// data source or a database source the container does not register as a singleton, which is refused - because what
    /// the factory answers is kept for the life of the process.
    /// </summary>
    private sealed class SingletonServices(IServiceProvider root, IServiceCollection services)
        : IServiceProvider, IKeyedServiceProvider, ISupportRequiredService
    {
        public object? GetService(Type serviceType)
        {
            Require(serviceType, keyed: false, key: null);
            return root.GetService(serviceType);
        }

        public object GetRequiredService(Type serviceType)
        {
            Require(serviceType, keyed: false, key: null);
            return root.GetRequiredService(serviceType);
        }

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            Require(serviceType, keyed: true, serviceKey);
            return root is IKeyedServiceProvider keyed
                ? keyed.GetKeyedService(serviceType, serviceKey)
                : throw new InvalidOperationException("The application's service provider does not support keyed services.");
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            Require(serviceType, keyed: true, serviceKey);
            return root.GetRequiredKeyedService(serviceType, serviceKey);
        }

        /// <summary>The last descriptor of <paramref name="type" /> (with <paramref name="key" />), as the container would pick it.</summary>
        public static ServiceDescriptor? Last(IServiceCollection services, Type type, bool keyed, object? key)
        {
            ServiceDescriptor? last = null;

            foreach (ServiceDescriptor descriptor in services)
            {
                if (descriptor.ServiceType == type
                    && descriptor.IsKeyedService == keyed
                    && (!keyed || Equals(descriptor.ServiceKey, key)))
                {
                    last = descriptor;
                }
            }

            return last;
        }

        private void Require(Type type, bool keyed, object? key)
        {
            if (!typeof(DbDataSource).IsAssignableFrom(type) && !typeof(IPostgresDatabaseSource).IsAssignableFrom(type))
            {
                return;
            }

            if (Last(services, type, keyed, key) is { Lifetime: not ServiceLifetime.Singleton } descriptor)
            {
                string registration = keyed ? " under the key '" + key + "'" : string.Empty;
                throw new InvalidOperationException(
                    type.Name + registration + " is registered as " + descriptor.Lifetime + ", and Postgres Studio keeps what a "
                    + "source's factory answers for the life of the process: register it as a singleton, or the studio would hold "
                    + "one scope's instance for every visitor.");
            }
        }
    }
}
