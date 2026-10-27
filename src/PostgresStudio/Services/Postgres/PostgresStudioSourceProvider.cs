using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IStudioSourceProvider" />: the application's own data sources and the database sets it
/// lists itself (<see cref="PostgresSourceRegistry" />), each one database or many, none with a tenant.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, as the interface asks: its refresh generation is the circuit's. Everything process-wide - which sources
/// exist, what each built, a host's list - is the registry's.
/// </para>
/// <para>
/// <b>Cardinality.</b> A data source is one database, <see cref="StudioDatabaseCardinality.Single" />. A host's database
/// source is <see cref="StudioDatabaseCardinality.DynamicMultiple" />: its list may change while the process runs, so the
/// picker draws a refresh beside it. Neither has a tenant selector; a scope naming a tenant is refused as an unknown one
/// is.
/// </para>
/// </remarks>
internal sealed class PostgresStudioSourceProvider : IStudioSourceProvider
{
    private readonly IOptions<PostgresStudioOptions> options;
    private readonly PostgresSourceRegistry registry;
    private readonly ILogger<PostgresStudioSourceProvider> logger;
    private readonly StudioLogThrottle? throttle;
    private long generation;

    /// <remarks>
    /// <paramref name="throttle" /> is optional so that a test can build a provider by hand; the container always
    /// supplies it. Nothing is resolved from the circuit's own scope: the registry builds every source from the root
    /// container, once.
    /// </remarks>
    public PostgresStudioSourceProvider(
        IOptions<PostgresStudioOptions> options,
        PostgresSourceRegistry registry,
        ILogger<PostgresStudioSourceProvider> logger,
        StudioLogThrottle? throttle = null)
    {
        this.options = options;
        this.registry = registry;
        this.logger = logger;
        this.throttle = throttle;
    }

    /// <inheritdoc />
    public IReadOnlyList<StudioSourceRegistration> Registrations() => registry.Registrations(options.Value, logger);

    /// <inheritdoc />
    public StudioSourceRegistration? Find(string? key) => registry.Find(key, options.Value, logger);

    /// <inheritdoc />
    public StoreAvailability TryResolve(StudioSourceRegistration registration, [NotNullWhen(true)] out IStudioSource? source)
    {
        ArgumentNullException.ThrowIfNull(registration);

        source = null;

        if (registry.Find(registration.Key, options.Value, logger) is not { } known)
        {
            return StoreAvailability.Unavailable("No data source is registered under '" + registration.Key + "'.", DateTimeOffset.UtcNow);
        }

        if (known.Kind == StudioSourceKind.HostProvider)
        {
            StoreAvailability listed = registry.TryResolveDatabaseSource(known, options.Value, out IPostgresDatabaseSource? host);
            if (listed.IsAvailable && host is not null)
            {
                source = new PostgresHostSource(known, host, this);
            }

            return listed;
        }

        StoreAvailability availability = registry.TryResolveDataSource(known, options.Value, out NpgsqlDataSource? dataSource);
        if (availability.IsAvailable && dataSource is not null)
        {
            source = new PostgresDataSourceSource(known, new DataSourceDatabase(known.Key, dataSource));
        }

        return availability;
    }

    /// <inheritdoc />
    /// <remarks>Also forgets every host's database list, so the refresh beside the picker asks the host again.</remarks>
    public void Invalidate()
    {
        Interlocked.Increment(ref generation);
        registry.InvalidateLists();
    }

    /// <inheritdoc />
    public long Generation => Interlocked.Read(ref generation);

    /// <inheritdoc />
    /// <remarks>
    /// Every source in order. A data source is its one database, named by its key - and is not built to say so: a source
    /// that will not build is found out by whoever resolves it. A host's source is every name it lists, through the
    /// registry's minute-long cache, and nothing it names is opened; a source that will not build, or whose list cannot be
    /// read, is one <see cref="StudioSweepTargetOutcome.SourceNotReached" />.
    /// </remarks>
    public async IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (StudioSourceRegistration registration in Registrations())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (registration.Kind == StudioSourceKind.DataSource)
            {
                // Its one database's identity is its key (PostgresDatabases.cs): nothing needs building to name it.
                yield return new StudioListedDatabase(registration, StudioSweepTargetOutcome.Database, registration.Key) { SingleDatabaseSource = true };
                continue;
            }

            if (!TryResolve(registration, out IStudioSource? source).IsAvailable || source is not PostgresHostSource host)
            {
                // TryResolve has logged why, throttled (9210).
                yield return new StudioListedDatabase(registration, StudioSweepTargetOutcome.SourceNotReached);
                continue;
            }

            IReadOnlyList<string>? names = await host.TryListAsync(cancellationToken).ConfigureAwait(false);

            if (names is null)
            {
                yield return new StudioListedDatabase(registration, StudioSweepTargetOutcome.SourceNotReached);
                continue;
            }

            if (names.Count == 0)
            {
                yield return new StudioListedDatabase(registration, StudioSweepTargetOutcome.SourceNotEnumerated, Count: 0, Detail: "the source listed no databases");
                continue;
            }

            foreach (string name in names)
            {
                yield return new StudioListedDatabase(registration, StudioSweepTargetOutcome.Database, name);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A data source answers for its key, or for an empty id. A host's source answers for a name it lists - from the same
    /// cache - and the host is asked for that one database's data source for this operation alone (API design §3.3): null
    /// is "not found", and a throw or a timeout is "not reached", logged at Debug, because the caller counts it.
    /// </remarks>
    public async ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        if (Find(sourceKey) is not { } registration)
        {
            return StudioUnattendedResolution.NotFound("No data source is registered under '" + sourceKey + "'.");
        }

        if (!TryResolve(registration, out IStudioSource? source).IsAvailable || source is null)
        {
            // TryResolve has logged why, throttled (9210).
            return StudioUnattendedResolution.NotReached(
                "Data source '" + registration.Key + "' could not be built; the server log says why (event 9210).");
        }

        if (source is PostgresDataSourceSource single)
        {
            return databaseId.Length > 0 && !string.Equals(databaseId, single.Database.Identity, StringComparison.OrdinalIgnoreCase)
                ? StudioUnattendedResolution.NotFound(
                    "Data source '" + registration.Key + "' is one database, named '" + single.Database.Identity + "', not '" + databaseId + "'.")
                : StudioUnattendedResolution.Of(
                    new ResolvedSource(new StudioScope(registration.Key, single.Database.Identity, null), registration, single.Database));
        }

        PostgresHostSource host = (PostgresHostSource) source;
        IReadOnlyList<string>? names = await host.TryListAsync(cancellationToken).ConfigureAwait(false);

        if (names is null)
        {
            return StudioUnattendedResolution.NotReached(
                "The databases of data source '" + registration.Key + "' could not be listed; the server log says why (event 9213).");
        }

        string? name = databaseId.Length == 0
            ? (names.Count > 0 ? names[0] : null)
            : names.FirstOrDefault(x => string.Equals(x, databaseId, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return StudioUnattendedResolution.NotFound("Data source '" + registration.Key + "' lists no database named '" + databaseId + "'.");
        }

        (DataSourceDatabase? opened, bool failed) = await host.TryOpenAsync(name, cancellationToken).ConfigureAwait(false);

        return opened is not null
            ? StudioUnattendedResolution.Of(new ResolvedSource(new StudioScope(registration.Key, name, null), registration, opened))
            : failed
                ? StudioUnattendedResolution.NotReached(
                    "Data source '" + registration.Key + "' could not hand over database '" + name + "': the host's database source failed or took too long.")
                : StudioUnattendedResolution.NotFound("Data source '" + registration.Key + "' no longer has a database named '" + name + "'.");
    }

    /// <summary>The host's list, through the registry's minute-long cache.</summary>
    internal ValueTask<IReadOnlyList<string>> ListAsync(string key, IPostgresDatabaseSource host, CancellationToken cancellationToken) =>
        registry.ListAsync(key, host, options.Value.QueryTimeout, logger, throttle, cancellationToken);

    /// <summary>
    /// The application's data source for <paramref name="name" />, asked of the host for this operation and never
    /// remembered (API design §3.3): <see langword="null" /> when the host no longer has it.
    /// </summary>
    /// <exception cref="TimeoutException">The host did not answer within <c>QueryTimeout</c>.</exception>
    internal async ValueTask<NpgsqlDataSource?> OpenAsync(IPostgresDatabaseSource host, string key, string name, CancellationToken cancellationToken)
    {
        TimeSpan timeout = options.Value.QueryTimeout;
        using CancellationTokenSource timer = new(timeout, registry.TimeProvider);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        try
        {
            return await host.GetDataSourceAsync(name, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && timer.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The database source '" + key + "' did not hand over database '" + StudioLogValue.Cap(name) + "' within "
                + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.",
                exception);
        }
    }

    /// <summary>Logs a host list that could not be read, as the scope selector's own listing does (9213, throttled).</summary>
    internal void ListFailed(string key, Exception exception)
    {
        LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, "Store.Databases", key, null, StudioLogThrottle.KindOf(exception));
        logger.PostgresStudioStoreDatabasesUnreadable(level, exception, key);
    }

    /// <summary>Logs a host-listed database that could not be opened during a sweep, at Debug: the sweep counts it.</summary>
    internal void OpenFailed(string key, Exception exception)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(exception, "Postgres Studio could not open a database of source {SourceKey} for a sweep", key);
        }
    }
}

/// <summary>One of the application's data sources: one database, named by the source's key.</summary>
/// <param name="registration">The registration.</param>
/// <param name="database">Its one database.</param>
internal sealed class PostgresDataSourceSource(StudioSourceRegistration registration, DataSourceDatabase database) : IStudioSource
{
    /// <inheritdoc />
    public StudioSourceRegistration Registration { get; } = registration;

    /// <summary>Its one database.</summary>
    public DataSourceDatabase Database { get; } = database;

    /// <inheritdoc />
    public StudioDatabaseCardinality Cardinality => StudioDatabaseCardinality.Single;

    /// <inheritdoc />
    public bool ShowsTenantSelector => false;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<IStudioDatabase>>([Database]);

    /// <inheritdoc />
    public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TenantList.Unavailable);

    /// <inheritdoc />
    public ValueTask<ResolvedSource> CompleteAsync(
        StudioScope scope,
        IStudioDatabase database,
        IReadOnlyList<IStudioDatabase> all,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(database);

        PostgresTenancy.RefuseTenant(scope, Registration.Key);
        return ValueTask.FromResult(new ResolvedSource(scope, Registration, database));
    }
}

/// <summary>
/// A host's database source (issue #8, G11): the names it lists, each opened through the host only when a scope or a
/// sweep reaches it.
/// </summary>
internal sealed class PostgresHostSource : IStudioSource
{
    private readonly IPostgresDatabaseSource host;
    private readonly PostgresStudioSourceProvider provider;

    public PostgresHostSource(StudioSourceRegistration registration, IPostgresDatabaseSource host, PostgresStudioSourceProvider provider)
    {
        Registration = registration;
        this.host = host;
        this.provider = provider;
    }

    /// <inheritdoc />
    public StudioSourceRegistration Registration { get; }

    /// <inheritdoc />
    public StudioDatabaseCardinality Cardinality => StudioDatabaseCardinality.DynamicMultiple;

    /// <inheritdoc />
    public bool ShowsTenantSelector => false;

    /// <inheritdoc />
    /// <remarks>Names only: nothing is opened, so listing five hundred tenants builds no pool.</remarks>
    public async ValueTask<IReadOnlyList<IStudioDatabase>> DatabasesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> names = await provider.ListAsync(Registration.Key, host, cancellationToken).ConfigureAwait(false);
        return [.. names.Select(name => (IStudioDatabase) new HostListedDatabase(Registration.Key, name))];
    }

    /// <inheritdoc />
    public ValueTask<TenantList> DiscoverTenantsAsync(IStudioDatabase database, IReadOnlyList<IStudioDatabase> all, CancellationToken cancellationToken) =>
        ValueTask.FromResult(TenantList.Unavailable);

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">
    /// The scope names a tenant, names no database - never "the first one", whatever a policy said about an empty id - or
    /// the host no longer has the database.
    /// </exception>
    /// <exception cref="StudioStoreUnavailableException">The host could not hand the database over.</exception>
    public async ValueTask<ResolvedSource> CompleteAsync(
        StudioScope scope,
        IStudioDatabase database,
        IReadOnlyList<IStudioDatabase> all,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(database);

        PostgresTenancy.RefuseTenant(scope, Registration.Key);

        NpgsqlDataSource? dataSource;
        try
        {
            dataSource = await provider.OpenAsync(host, Registration.Key, database.Identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The name is the host's list's and the reason the host's exception's: neither is the studio's text, and the
            // message is logged and rendered whole, so both go in escaped and bounded (H1).
            throw new StudioStoreUnavailableException(
                Registration.Key,
                "its database '" + StudioLogValue.Cap(database.Identity) + "' could not be opened: " + StudioLogValue.CapText(exception.Message),
                "data source");
        }

        if (dataSource is null)
        {
            throw new KeyNotFoundException(
                "data source '" + Registration.Key + "' no longer has a database named '" + StudioLogValue.Cap(database.Identity) + "'.");
        }

        return new ResolvedSource(scope, Registration, new DataSourceDatabase(database.Identity, dataSource));
    }

    /// <summary>The host's list, or <see langword="null" /> when it could not be read (logged as 9213).</summary>
    internal async ValueTask<IReadOnlyList<string>?> TryListAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ListAsync(Registration.Key, host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            provider.ListFailed(Registration.Key, exception);
            return null;
        }
    }

    /// <summary>One listed database opened for a sweep: the database, or nothing because it left the list, or a failure.</summary>
    internal async ValueTask<(DataSourceDatabase? Database, bool Failed)> TryOpenAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            NpgsqlDataSource? dataSource = await provider.OpenAsync(host, Registration.Key, name, cancellationToken).ConfigureAwait(false);
            return (dataSource is null ? null : new DataSourceDatabase(name, dataSource), false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            provider.OpenFailed(Registration.Key, exception);
            return (null, true);
        }
    }
}

/// <summary>What every PostgresStudio scope must say before it is completed.</summary>
internal static class PostgresTenancy
{
    /// <summary>
    /// Refuses a scope that names a tenant, and one that names no database, with the answer an unknown one gets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No tenant:</b> a plain source has none.
    /// </para>
    /// <para>
    /// <b>A database, always.</b> The shared resolver matches an empty database id to a source's first database, and the
    /// policy it asked first was asked about the empty id - so a handler that allows <c>""</c> (which the studio asks
    /// only to list a source that would not build) would be answered with whichever database the source lists first,
    /// one it refuses by name included. Every scope the studio builds names its database; one that does not is refused
    /// here, for a host's database source and for a single data source alike (PostgresStudioResource's remarks).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// The tenant is the visitor's own text, from <c>?tenant=</c> or a circuit, and the message is logged and audited
    /// whole (H1): it goes in escaped and bounded (<see cref="StudioLogValue.Cap" />), as the resolver's own refusals do.
    /// </remarks>
    /// <exception cref="KeyNotFoundException"><paramref name="scope" /> names a tenant, or no database.</exception>
    public static void RefuseTenant(StudioScope scope, string sourceKey)
    {
        if (scope.TenantId is { } tenant)
        {
            throw new KeyNotFoundException(
                "'" + StudioLogValue.Cap(tenant) + "' is not a tenant of data source '" + sourceKey
                + "': Postgres Studio's sources have no tenants.");
        }

        if (string.IsNullOrEmpty(scope.DatabaseId))
        {
            throw new KeyNotFoundException(
                "The scope names no database of data source '" + sourceKey + "': Postgres Studio resolves a database only by its name.");
        }
    }
}
