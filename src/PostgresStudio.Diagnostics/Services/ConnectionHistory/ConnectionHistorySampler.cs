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
/// it answers no, every kept connection is closed and any samples still held (issue #46) are offered to the store, oldest first -
/// storing one reads nothing from the server, so no second process samples (issue #54) - and those older than the hour the backlog
/// is bounded to are dropped, since nothing is added to push them out.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "The round semaphore is never disposed on purpose (issue #50): its wait handle is never created, so it holds nothing unmanaged, and a Stop or an in-flight round after Dispose must still be able to wait on and release it.")]
internal sealed class ConnectionHistorySampler : BackgroundService, IAsyncDisposable
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

    /// <summary>
    /// One round at a time, and a stop after it. Never disposed (issue #50): a <see cref="SemaphoreSlim" /> whose wait handle is never
    /// asked for holds nothing unmanaged, and disposing it made a <see cref="StopAsync" /> after the dispose, and the release of a round
    /// still in flight, throw <see cref="ObjectDisposedException" />.
    /// </summary>
    private readonly SemaphoreSlim round = new(1, 1);

    private int disposed;

    /// <summary>
    /// Issue #46: the samples whose append failed, per server, oldest first. Mutated only inside <see cref="round" />, and each
    /// mutation - an enqueue, a dequeue, a clear - is made under <see cref="backlogGate" />, which a page's read of
    /// <see cref="PendingSamples" /> takes too, so the copy it gets is never taken mid-change. No await is made under the lock.
    /// </summary>
    private readonly Dictionary<string, Backlog> backlogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock backlogGate = new();

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

            // The backlog is in this process alone; a stopped sampler has no next round to store it in.
            lock (backlogGate)
            {
                backlogs.Clear();
            }
        }
        finally
        {
            if (entered)
            {
                round.Release();
            }
        }
    }

    /// <summary>
    /// Cancels the stopping token, as <see cref="BackgroundService" /> does, and nothing else: a synchronous dispose must not block on
    /// the I/O that closes the kept connections, so it releases none. <see cref="DisposeAsync" /> does - the container's async dispose
    /// reaches it - and <see cref="StopAsync" /> still works afterwards (issue #50).
    /// </summary>
    public override void Dispose()
    {
        base.Dispose();
    }

    /// <summary>
    /// Issue #50: cancels the stopping token, waits for a round in flight for at most the studio's <c>QueryTimeout</c> (and goes on
    /// regardless when it runs out, as <see cref="StopAsync" /> does when the host's budget does), closes the kept connections and
    /// clears the backlog - so they are released even when <see cref="StopAsync" /> never ran. Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        base.Dispose();
        bool entered = false;

        try
        {
            using CancellationTokenSource limit = new(settings.QueryTimeout, time);
            await round.WaitAsync(limit.Token).ConfigureAwait(false);
            entered = true;
        }
        catch (OperationCanceledException)
        {
            // A round that outlives QueryTimeout: the connections are closed under it, as a stop does.
        }

        try
        {
            await ReleaseAsync().ConfigureAwait(false);

            lock (backlogGate)
            {
                backlogs.Clear();
            }
        }
        finally
        {
            if (entered)
            {
                round.Release();
            }
        }
    }

    /// <summary>
    /// How many samples of one server the backlog holds (issue #46): an hour of them at <paramref name="interval" /> - 60 at the
    /// default minute, 240 at 15 seconds, 1 at an hour - and never fewer than one. Past it the oldest is dropped first.
    /// </summary>
    internal static int PendingBound(TimeSpan interval) => Math.Max(1, (int) Math.Ceiling(TimeSpan.FromHours(1) / interval));

    /// <summary>How many samples of <paramref name="server" /> wait to be stored: appended by nobody yet, because the store refused them.</summary>
    internal int Pending(string server)
    {
        lock (backlogGate)
        {
            return backlogs.TryGetValue(server, out Backlog? backlog) ? backlog.Samples.Count : 0;
        }
    }

    /// <summary>
    /// A copy, oldest first, of the samples of <paramref name="server" /> that wait to be stored - taken under the lock the round's
    /// own changes to the backlog take, so a page on another thread never reads it mid-change.
    /// </summary>
    internal IReadOnlyList<PostgresConnectionSample> PendingSamples(string server)
    {
        lock (backlogGate)
        {
            return backlogs.TryGetValue(server, out Backlog? backlog) ? [.. backlog.Samples] : [];
        }
    }

    /// <summary>
    /// One round: every server sampled once, unless the host says another process samples - then none is, and only the samples
    /// still held are offered to the store. How many samples were stored - the ones a store had refused earlier included.
    /// </summary>
    internal async Task<int> SampleOnceAsync(CancellationToken cancellationToken)
    {
        await round.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (options.Value.ShouldSample is { } shouldSample && !await shouldSample(cancellationToken).ConfigureAwait(false))
            {
                await ReleaseAsync().ConfigureAwait(false);

                // Issue #54: this process stopped being the sampler, but it may still hold samples from when it was - the moment the
                // leadership moves is often the moment the store refused them. Writing one reads nothing from the server and needs no
                // kept connection, so offering them does not make a second process sample; no new sample is taken.
                return await StoreHeldAsync([], cancellationToken).ConfigureAwait(false);
            }

            int stored = 0;

            HashSet<string> sampled = new(StringComparer.OrdinalIgnoreCase);

            foreach ((string server, NpgsqlDataSource dataSource) in await ServersAsync(cancellationToken).ConfigureAwait(false))
            {
                sampled.Add(server);
                PostgresConnectionSample? sample = null;

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
                }

                // A sample that could not be read is lost - there is nothing to keep - but the samples a store refused earlier are
                // still offered to it.
                stored += await StoreAsync(server, sample, cancellationToken).ConfigureAwait(false);
            }

            // A server that left the list - a source stayed unreadable, the host's databases changed, the MaxServers cut moved - still
            // has a backlog, and writing it needs no connection to that server: offered to the store all the same, so it is neither
            // stranded nor claimed as waiting until the process stops.
            stored += await StoreHeldAsync(sampled, cancellationToken).ConfigureAwait(false);

            return stored;
        }
        finally
        {
            round.Release();
        }
    }

    /// <summary>
    /// Offers the backlog of every server not in <paramref name="except" /> to the store, oldest first and stopping at the first
    /// failure (<see cref="StoreAsync" />), with no sample and no connection. How many were stored.
    /// </summary>
    /// <remarks>
    /// No sample is added to these backlogs, so <see cref="PendingBound" /> - which drops at the moment one is added - never drops
    /// anything here, and a sample the store refuses for good would block every later one for as long as the process runs. So first
    /// the samples older than the window the bound stands for (<see cref="PendingBound" /> intervals) are dropped, as the bound
    /// would have dropped them, with the same event.
    /// </remarks>
    private async Task<int> StoreHeldAsync(HashSet<string> except, CancellationToken cancellationToken)
    {
        string[] held;

        lock (backlogGate)
        {
            held = [.. backlogs.Keys.Where(x => !except.Contains(x))];
        }

        // Half an interval of slack: the sample taken exactly the window ago would otherwise be just past it by the time this runs
        // (after ShouldSample and the release), and be dropped before it was ever offered - it gets one more offer, as on the
        // sampler path, and goes the round after.
        TimeSpan interval = options.Value.Interval;
        DateTimeOffset oldest = time.GetUtcNow() - (PendingBound(interval) * interval) - (interval / 2);
        int stored = 0;

        foreach (string server in held)
        {
            (int dropped, DateTimeOffset since) = DropOlderThan(server, oldest);
            LogDropped(server, dropped, since);
            stored += await StoreAsync(server, null, cancellationToken).ConfigureAwait(false);
        }

        return stored;
    }

    /// <summary>Drops the samples of the server's backlog taken before <paramref name="oldest" />, from the front; how many, and since when the backlog has been held.</summary>
    private (int Dropped, DateTimeOffset Since) DropOlderThan(string server, DateTimeOffset oldest)
    {
        lock (backlogGate)
        {
            if (!backlogs.TryGetValue(server, out Backlog? backlog))
            {
                return (0, default);
            }

            int dropped = 0;

            while (backlog.Samples.Count > 0 && backlog.Samples.Peek().At < oldest)
            {
                backlog.Samples.Dequeue();
                dropped++;
            }

            if (backlog.Samples.Count == 0 && dropped > 0)
            {
                backlogs.Remove(server);
            }

            return (dropped, backlog.Since);
        }
    }

    private void LogDropped(string server, int dropped, DateTimeOffset since)
    {
        if (dropped == 0)
        {
            return;
        }

        LogLevel level = throttle.WarningOrDebug("ConnectionHistory.Backlog", server, null, null);

        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioConnectionHistoryBacklogDropped(level, dropped, server, since);
        }
        else
        {
            logger.ConnectionHistoryBacklogDropped(level, dropped, server, since);
        }
    }

    /// <summary>
    /// Issue #46: the server's backlog, oldest first, then <paramref name="sample" /> (when it was read), each appended on its own
    /// <c>QueryTimeout</c>. The first failure stops it: that sample and every later one stay in the backlog, or go into it, in order,
    /// so a store that hangs costs one timeout a server a round. Past <see cref="PendingBound" /> the oldest is dropped. How many were
    /// stored.
    /// </summary>
    /// <remarks>
    /// The store may be the host's own, and a table in the sampled database needs a connection from the application's pool - so its
    /// append fails exactly when the pool is exhausted, the moment the history exists for. The backlog is in this process alone: a
    /// process that dies loses it.
    /// </remarks>
    internal async Task<int> StoreAsync(string server, PostgresConnectionSample? sample, CancellationToken cancellationToken)
    {
        bool hadBacklog = Pending(server) > 0;
        int stored = 0;
        int flushed = 0;
        bool refused = false;

        while (!refused && PeekPending(server) is { } head)
        {
            if (await TryAppendAsync(head, cancellationToken).ConfigureAwait(false))
            {
                DequeuePending(server);
                stored++;
                flushed++;
            }
            else
            {
                refused = true;
            }
        }

        int dropped = 0;
        DateTimeOffset since = default;

        if (sample is not null)
        {
            if (!refused && await TryAppendAsync(sample, cancellationToken).ConfigureAwait(false))
            {
                stored++;
            }
            else
            {
                (dropped, since) = EnqueuePending(server, sample);
            }
        }

        Backlog? drained = null;

        lock (backlogGate)
        {
            if (backlogs.TryGetValue(server, out Backlog? backlog))
            {
                backlog.Flushed += flushed;

                if (hadBacklog && backlog.Samples.Count == 0)
                {
                    backlogs.Remove(server);
                    drained = backlog;
                }
            }
        }

        if (drained is not null)
        {
            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioConnectionHistoryBacklogStored(drained.Flushed, server, drained.Since);
            }
            else
            {
                logger.ConnectionHistoryBacklogStored(drained.Flushed, server, drained.Since);
            }
        }

        LogDropped(server, dropped, since);

        return stored;
    }

    /// <summary>One append, on the studio's <c>QueryTimeout</c>; a failure is the round's, not the server's - the kept connection stays.</summary>
    private async Task<bool> TryAppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(settings.QueryTimeout);

        try
        {
            await store.AppendAsync(sample, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogRoundFailed(exception);
            return false;
        }
    }

    private PostgresConnectionSample? PeekPending(string server)
    {
        lock (backlogGate)
        {
            return backlogs.TryGetValue(server, out Backlog? backlog) && backlog.Samples.Count > 0 ? backlog.Samples.Peek() : null;
        }
    }

    private void DequeuePending(string server)
    {
        lock (backlogGate)
        {
            if (backlogs.TryGetValue(server, out Backlog? backlog) && backlog.Samples.Count > 0)
            {
                backlog.Samples.Dequeue();
            }
        }
    }

    /// <summary>Holds <paramref name="sample" /> at the back of the server's backlog, dropping the oldest past the bound; how many were dropped, and since when the backlog has been held.</summary>
    private (int Dropped, DateTimeOffset Since) EnqueuePending(string server, PostgresConnectionSample sample)
    {
        int bound = PendingBound(options.Value.Interval);
        int dropped = 0;

        lock (backlogGate)
        {
            if (!backlogs.TryGetValue(server, out Backlog? backlog))
            {
                backlog = new Backlog(time.GetUtcNow());
                backlogs[server] = backlog;
            }

            while (backlog.Samples.Count >= bound)
            {
                backlog.Samples.Dequeue();
                dropped++;
            }

            backlog.Samples.Enqueue(sample);
            return (dropped, backlog.Since);
        }
    }

    private sealed class Backlog(DateTimeOffset since)
    {
        public Queue<PostgresConnectionSample> Samples { get; } = new();

        /// <summary>When the first sample of this backlog was held.</summary>
        public DateTimeOffset Since { get; } = since;

        /// <summary>How many of its samples were stored since.</summary>
        public int Flushed { get; set; }
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
