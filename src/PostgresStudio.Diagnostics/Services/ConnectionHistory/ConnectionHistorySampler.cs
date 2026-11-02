using System.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #43: samples each server the studio reads - its client connections, by application - every
/// <see cref="PostgresConnectionHistoryOptions.Interval" />, into the <see cref="IPostgresConnectionHistoryStore" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>One kept-open connection per server.</b> A sampler that opened a connection to sample would record nothing at the one moment
/// that matters - when the server refuses new ones. So each server gets one connection, from the application's own data source,
/// opened once and kept: lent out by the pool, it is never pruned, and the sample every interval keeps it alive. It is outside
/// <c>MaxStudioConnections</c>, and its session is named like the studio's, with "history" after the product, for as long as it is
/// kept - between samples too - and given its own name back before it is closed. Its time is not the studio's time on the Health
/// page (a meter of its own): a session open for a week is not a week of the studio's work. A connection that broke is opened again
/// at the next round.
/// </para>
/// <para>
/// <b>Server, not database.</b> <c>pg_stat_activity</c> is server-wide, so the studio's databases are grouped by the host and port
/// their connection string names, and each server is sampled once - through its first database - whatever databases it holds.
/// The list is read again every <see cref="ServersFor" />, not every round.
/// </para>
/// <para>
/// <b>Never a dynamic source.</b> A source whose databases are <see cref="StudioDatabaseCardinality.DynamicMultiple" /> - a
/// host-provided source of tenant databases, a Marten tenancy that builds its databases - is not enumerated, as Holders' own
/// names are not (<c>SessionHolderNames</c>): enumerating a tenancy that builds its databases is not read-only (D27), and a
/// background loop must never be the thing that applies a lookup table or creates a database. Its servers are sampled only
/// where another source names them.
/// </para>
/// <para>
/// <b>Read like Holders.</b> The read is <see cref="HealthConnectionQueries.ServerHolders" />, in a read-only transaction under the
/// studio's statement timeout and <c>SqlConsoleRole</c>: client sessions only, the sampler's own left out, a role without
/// <c>pg_read_all_stats</c> counting what it cannot read as state unknown.
/// </para>
/// <para>
/// <b>Leadership is the host's.</b> <see cref="PostgresConnectionHistoryOptions.ShouldSample" /> is asked before every round; while
/// it answers no, every kept connection is closed.
/// </para>
/// </remarks>
internal sealed class ConnectionHistorySampler : BackgroundService
{
    /// <summary>The most servers sampled; past it the rest are left out.</summary>
    internal const int MaxServers = 50;

    /// <summary>How long the list of servers is kept before the sources are asked again.</summary>
    internal static readonly TimeSpan ServersFor = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory scopes;
    private readonly IOptions<PostgresConnectionHistoryOptions> options;
    private readonly IPostgresConnectionHistoryStore store;
    private readonly IDiagnosticsSettings settings;
    private readonly ILogger<ConnectionHistorySampler> logger;
    private readonly StudioLogThrottle throttle;
    private readonly TimeProvider time;
    private readonly Dictionary<string, NpgsqlConnection> kept = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim round = new(1, 1);

    /// <summary>
    /// The kept connections' own meter: the Health page's "studio cost" is the studio's work, and a session kept open for days would
    /// be booked there as days of it when it closes. Metered all the same, so each sample's transaction is named and counted nowhere.
    /// </summary>
    private readonly StudioDatabaseTime keptTime = new(TimeProvider.System);
    private IReadOnlyList<(string Server, NpgsqlDataSource DataSource)>? servers;
    private DateTimeOffset serversAt;

    public ConnectionHistorySampler(
        IServiceScopeFactory scopes,
        IOptions<PostgresConnectionHistoryOptions> options,
        IPostgresConnectionHistoryStore store,
        IDiagnosticsSettings settings,
        ILogger<ConnectionHistorySampler> logger,
        StudioLogThrottle throttle,
        TimeProvider? time = null)
    {
        this.scopes = scopes;
        this.options = options;
        this.store = store;
        this.settings = settings;
        this.logger = logger;
        this.throttle = throttle;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.Interval, time);

        do
        {
            try
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogRoundFailed(exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // After a round still in flight - which the stop has cancelled - so the kept connections are not closed under it; unless the
        // host's own shutdown budget runs out first, when they are closed regardless.
        bool entered = false;

        try
        {
            await round.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
        }
        catch (OperationCanceledException)
        {
            // The host stops waiting; so does this.
        }

        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            {
                round.Release();
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        round.Dispose();
    }

    /// <summary>One round: every server sampled once, unless the host says another process samples. How many samples were kept.</summary>
    internal async Task<int> SampleOnceAsync(CancellationToken cancellationToken)
    {
        await round.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (options.Value.ShouldSample is { } shouldSample && !await shouldSample(cancellationToken).ConfigureAwait(false))
            {
                await ReleaseAsync().ConfigureAwait(false);
                return 0;
            }

            int kept = 0;

            foreach ((string server, NpgsqlDataSource dataSource) in await ServersAsync(cancellationToken).ConfigureAwait(false))
            {
                PostgresConnectionSample sample;

                try
                {
                    sample = await SampleAsync(server, dataSource, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogLevel level = throttle.WarningOrDebug("ConnectionHistory.Sample", server, null, StudioLogThrottle.KindOf(exception));

                    if (settings.Text.Product == StudioProduct.PostgresStudio)
                    {
                        logger.PostgresStudioConnectionHistorySampleFailed(level, exception, server);
                    }
                    else
                    {
                        logger.ConnectionHistorySampleFailed(level, exception, server);
                    }

                    await ForgetAsync(server).ConfigureAwait(false);
                    continue;
                }

                // The store may be the host's own; it gets the studio's QueryTimeout, and its failure is the round's, not the server's:
                // the kept connection stays.
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(settings.QueryTimeout);

                try
                {
                    await store.AppendAsync(sample, limit.Token).ConfigureAwait(false);
                    kept++;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    LogRoundFailed(exception);
                }
            }

            return kept;
        }
        finally
        {
            round.Release();
        }
    }

    /// <summary>
    /// The servers the studio reads, each once, through the first database of it that has a data source - read again every
    /// <see cref="ServersFor" />, and never from a <see cref="StudioDatabaseCardinality.DynamicMultiple" /> source (see the remarks).
    /// </summary>
    private async Task<IReadOnlyList<(string Server, NpgsqlDataSource DataSource)>> ServersAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();

        if (servers is { } known && now - serversAt < ServersFor)
        {
            return known;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IStudioSourceProvider provider = scope.ServiceProvider.GetRequiredService<IStudioSourceProvider>();
        Dictionary<string, NpgsqlDataSource> found = new(StringComparer.OrdinalIgnoreCase);

        foreach (StudioSourceRegistration registration in provider.Registrations())
        {
            IReadOnlyList<IStudioDatabase> databases;

            try
            {
                if (!provider.TryResolve(registration, out IStudioSource? source).IsAvailable
                    || source is null
                    || source.Cardinality == StudioDatabaseCardinality.DynamicMultiple)
                {
                    continue;
                }

                databases = await source.DatabasesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (settings.Text.Product == StudioProduct.PostgresStudio)
                {
                    logger.PostgresStudioConnectionHistorySourceUnreadable(exception, registration.Key);
                }
                else
                {
                    logger.ConnectionHistorySourceUnreadable(exception, registration.Key);
                }

                continue;
            }

            foreach (IStudioDatabase database in databases)
            {
                if (database.DataSource is { } dataSource && ServerOf(dataSource.ConnectionString) is { } server && found.Count < MaxServers)
                {
                    found.TryAdd(server, dataSource);
                }
            }
        }

        servers = [.. found.Select(static x => (x.Key, x.Value))];
        serversAt = now;
        return servers;
    }

    /// <summary>One sample of <paramref name="server" />, over its kept connection.</summary>
    private async Task<PostgresConnectionSample> SampleAsync(string server, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = await KeptAsync(server, dataSource, cancellationToken).ConfigureAwait(false);
        TimeSpan timeout = settings.QueryTimeout < options.Value.Interval ? settings.QueryTimeout : options.Value.Interval;
        ReadOnlySqlSession session = new(new ReadOnlySqlOptions { StatementTimeout = timeout, Role = settings.SqlConsoleRole });
        DateTimeOffset at = time.GetUtcNow();

        (string? instance, List<SessionActivityReader.HolderRow> rows) = await session.InTransactionAsync(
                connection,
                (transaction, token) => ConnectionHistoryQueries.ReadSampleAsync(connection, transaction, timeout, SessionActivityReader.MapHolder, token),
                cancellationToken)
            .ConfigureAwait(false);

        SessionServerHolders holders = SessionActivityReader.Holders(rows) ?? SessionServerHolders.None;
        return Sample(server, instance, at, holders, options.Value.MaxApplications);
    }

    /// <summary>A sample from one holders reading: the server's client sessions less the sampler's own, and the applications, most first.</summary>
    internal static PostgresConnectionSample Sample(string server, string? instance, DateTimeOffset at, SessionServerHolders holders, int maxApplications)
    {
        ArgumentNullException.ThrowIfNull(holders);

        return new PostgresConnectionSample
        {
            At = at,
            Server = server,
            Instance = instance,
            Connections = Math.Max(0, holders.ServerConnections - 1),
            ClientLimit = holders.MaxConnections == 0 ? 0 : holders.Limits.Available,
            ByApplication =
            [
                .. holders.Groups
                    .GroupBy(static x => x.Application, StringComparer.Ordinal)
                    .Select(static x => new PostgresConnectionCounts
                    {
                        ApplicationName = x.Key.Length == 0 ? null : x.Key,
                        Total = (int) x.Sum(static g => g.Total),
                        Active = (int) x.Sum(static g => g.Active),
                        Idle = (int) x.Sum(static g => g.Idle),
                        IdleInTransaction = (int) x.Sum(static g => g.IdleInTransaction),
                        StateUnknown = (int) x.Sum(static g => g.StateUnknown),
                    })
                    .OrderByDescending(static x => x.Total)
                    .ThenBy(static x => x.ApplicationName, StringComparer.Ordinal)
                    .Take(Math.Max(1, maxApplications)),
            ],
        };
    }

    /// <summary>The host and port a connection string names, lowercased - never its database, user or password.</summary>
    internal static string? ServerOf(string? connectionString)
    {
        try
        {
            NpgsqlConnectionStringBuilder builder = new(connectionString ?? string.Empty);
            return string.IsNullOrWhiteSpace(builder.Host) ? null : builder.Host.ToLowerInvariant() + ":" + builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<NpgsqlConnection> KeptAsync(string server, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        if (kept.TryGetValue(server, out NpgsqlConnection? open) && open.State == ConnectionState.Open)
        {
            return open;
        }

        await ForgetAsync(server).ConfigureAwait(false);

        // Named like the studio's own sessions, with "history" after the product; held to no cap - it is the one connection that
        // must be there when the server refuses new ones (see the remarks).
        string? name = HistoryName(settings.StudioApplicationName, dataSource.ConnectionString, settings.SessionProduct);
        NpgsqlConnection connection = StudioConnectionMeter.Attach(
            dataSource.CreateConnection(),
            meter: keptTime,
            pool: new StudioConnectionPool(1, name, settings.QueryTimeout));

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (name is not null)
            {
                await ConnectionHistoryQueries.NameSessionAsync(connection, name, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        kept[server] = connection;
        return connection;
    }

    private void LogRoundFailed(Exception exception)
    {
        LogLevel level = throttle.WarningOrDebug("ConnectionHistory.Round", null, null, StudioLogThrottle.KindOf(exception));

        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioConnectionHistoryRoundFailed(level, exception);
        }
        else
        {
            logger.ConnectionHistoryRoundFailed(level, exception);
        }
    }

    /// <summary>
    /// The kept session's name: the studio's own name with "history" after the product (<c>orders-api (Postgres Studio history)</c>),
    /// the host's <c>StudioApplicationName</c> with " (history)" after it, or none when the host chose to keep the application's.
    /// </summary>
    internal static string? HistoryName(string? configured, string? connectionString, string product)
    {
        const string Tail = " (history)";

        return configured switch
        {
            null => StudioConnectionPools.ApplicationNameFor(null, connectionString, product + " history"),
            { Length: 0 } => null,
            _ => (configured.Length + Tail.Length > StudioConnectionPools.MaxApplicationNameBytes
                ? configured[..(StudioConnectionPools.MaxApplicationNameBytes - Tail.Length)]
                : configured) + Tail,
        };
    }

    private async Task ForgetAsync(string server)
    {
        if (kept.Remove(server, out NpgsqlConnection? connection))
        {
            if (connection.State == ConnectionState.Open)
            {
                try
                {
                    await ConnectionHistoryQueries.ForgetSessionNameAsync(connection, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
                {
                    // A broken connection is not returned to the pool as it is; there is no name left to give back.
                }
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync()
    {
        foreach (string server in kept.Keys.ToList())
        {
            await ForgetAsync(server).ConfigureAwait(false);
        }
    }
}
