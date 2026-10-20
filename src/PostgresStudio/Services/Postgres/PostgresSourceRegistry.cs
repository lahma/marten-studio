using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

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
/// <b>Lazy, like MartenStudio's store registry.</b> Constructed with the <see cref="IServiceCollection" /> at
/// <c>AddPostgresStudio</c> time and scanned on first use, because <c>AddPostgresStudio()</c> may be called before
/// <c>AddNpgsqlDataSource()</c>. Only a descriptor whose service type is exactly <see cref="NpgsqlDataSource" />, unkeyed,
/// counts: <c>AddNpgsqlDataSource</c> also registers <c>DbDataSource</c> and a transient <c>NpgsqlConnection</c>, and
/// neither is a source; and only a singleton, because resolving a scoped or transient one per circuit would build a pool
/// per visitor (PS-0, E7: it registers factories only, so nothing here reads an instance off a descriptor).
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
    private readonly TimeProvider timeProvider;
    private readonly Lock gate = new();
    private readonly Dictionary<string, StoreAvailability> failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object> built = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HostList> lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> identities = new(StringComparer.OrdinalIgnoreCase);

    private DefaultScan? scanned;
    private long listGeneration;

    public PostgresSourceRegistry(IServiceCollection services) : this(services, TimeProvider.System)
    {
    }

    public PostgresSourceRegistry(IServiceCollection services, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.services = services;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Every source, in order: <c>default</c> (when the container has a singleton unkeyed data source and
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
    /// <param name="provider">The container to resolve from.</param>
    /// <param name="dataSource">The application's data source, when available.</param>
    public StoreAvailability TryResolveDataSource(
        StudioSourceRegistration registration,
        PostgresStudioOptions options,
        IServiceProvider provider,
        [NotNullWhen(true)] out NpgsqlDataSource? dataSource)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        PostgresSourceDefinition? definition = Definition(registration, options.DataSources);
        bool isDefault = string.Equals(registration.Key, PostgresSourceNames.Default, StringComparison.OrdinalIgnoreCase);

        StoreAvailability availability = TryBuild(
            registration.Key,
            typeof(NpgsqlDataSource),
            provider,
            isDefault
                ? static services => services.GetRequiredService<NpgsqlDataSource>()
                : definition?.DataSource is { } factory
                    ? services => factory(services) ?? throw new InvalidOperationException("The factory returned no data source.")
                    : null,
            cache: !isDefault,
            out object? instance);

        dataSource = instance as NpgsqlDataSource;
        return availability;
    }

    /// <summary>The host's database source behind a <see cref="StudioSourceKind.HostProvider" /> registration, or why there is none.</summary>
    /// <param name="registration">A registration this registry answered.</param>
    /// <param name="options">The studio's options.</param>
    /// <param name="provider">The container to resolve from.</param>
    /// <param name="source">The host's source, when available.</param>
    public StoreAvailability TryResolveDatabaseSource(
        StudioSourceRegistration registration,
        PostgresStudioOptions options,
        IServiceProvider provider,
        [NotNullWhen(true)] out IPostgresDatabaseSource? source)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(options);

        PostgresSourceDefinition? definition = Definition(registration, options.DatabaseSources);

        StoreAvailability availability = TryBuild(
            registration.Key,
            typeof(IPostgresDatabaseSource),
            provider,
            definition?.DatabaseSource is { } factory
                ? services => factory(services) ?? throw new InvalidOperationException("The factory returned no database source.")
                : null,
            cache: true,
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
    /// <exception cref="Exception">Whatever the host's listing threw, or a timeout; nothing is remembered then.</exception>
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
            DateTimeOffset now = timeProvider.GetUtcNow();

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

        using CancellationTokenSource limit = new(timeout, timeProvider);

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
                "The database source '" + key + "' did not list its databases within " + timeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " s.",
                exception);
        }

        Report(logger, throttle, key, invalid, "they do not match ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$");
        Report(logger, throttle, key, duplicate, "they repeat another name of the source, ignoring case");
        Report(logger, throttle, key, another, "another source already names a database the same, ignoring case");

        if (capped)
        {
            Report(logger, throttle, key, 1, "the source listed more than " + MaxListedDatabases.ToString(System.Globalization.CultureInfo.InvariantCulture) + " databases, and the rest were not read");
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

    private StoreAvailability TryBuild(
        string key,
        Type serviceType,
        IServiceProvider provider,
        Func<IServiceProvider, object>? build,
        bool cache,
        out object? instance)
    {
        ArgumentNullException.ThrowIfNull(provider);

        instance = null;

        lock (gate)
        {
            if (cache && built.TryGetValue(key, out object? existing))
            {
                instance = existing;
                return StoreAvailability.Available;
            }

            if (failures.TryGetValue(key, out StoreAvailability? failed)
                && failed.At is { } failedAt
                && timeProvider.GetUtcNow() - failedAt < FailureCacheWindow)
            {
                return failed;
            }
        }

        if (build is null)
        {
            return StoreAvailability.Unavailable("No data source is registered under '" + key + "'.", timeProvider.GetUtcNow());
        }

        try
        {
            object created = build(provider);

            lock (gate)
            {
                failures.Remove(key);

                // A factory is called once (the options' promise): the first instance it answered is the one kept.
                if (cache)
                {
                    if (built.TryGetValue(key, out object? raced))
                    {
                        created = raced;
                    }
                    else
                    {
                        built[key] = created;
                    }
                }
            }

            instance = created;
            return StoreAvailability.Available;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StoreAvailability unavailable = StoreAvailability.Unavailable(exception.Message, timeProvider.GetUtcNow());

            lock (gate)
            {
                failures[key] = unavailable;
            }

            // The failure cache expires every ten seconds and every polling page asks again, so the Warning is
            // throttled across them, as MartenStudio's store registry throttles the same event (9210).
            LogLevel level = StudioLogThrottle.LevelOrWarning(
                provider.GetService<StudioLogThrottle>(),
                "Store.Resolve",
                key,
                null,
                StudioLogThrottle.KindOf(exception));

            provider.GetService<ILoggerFactory>()?
                .CreateLogger<PostgresSourceRegistry>()
                .StoreUnavailable(level, key, serviceType.FullName ?? serviceType.Name, exception.Message);

            return unavailable;
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

            bool hasDefault = false;
            ServiceLifetime? skipped = null;

            foreach (ServiceDescriptor descriptor in services)
            {
                if (descriptor.ServiceType != typeof(NpgsqlDataSource) || descriptor.IsKeyedService)
                {
                    continue;
                }

                if (descriptor.Lifetime == ServiceLifetime.Singleton)
                {
                    hasDefault = true;
                }
                else
                {
                    skipped = descriptor.Lifetime;
                }
            }

            if (!hasDefault && skipped is { } lifetime && logger?.IsEnabled(LogLevel.Information) == true)
            {
                logger.DataSourceLifetimeSkipped(lifetime.ToString());
            }

            scanned = new DefaultScan(hasDefault);
            return scanned;
        }
    }

    /// <summary>What the scan found: whether the container holds a singleton unkeyed data source.</summary>
    private sealed record DefaultScan(bool HasDefault);

    /// <summary>One host's list, shared by every caller while it is fresh.</summary>
    private sealed record HostList(Task<IReadOnlyList<string>> Pending, DateTimeOffset ReadAt, long Generation);
}
