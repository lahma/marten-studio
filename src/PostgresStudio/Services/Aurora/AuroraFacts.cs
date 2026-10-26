using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Aurora;

/// <summary>
/// What the Server page's Aurora section shows of one database, read once per <c>HealthInterval</c> for every visitor -
/// gate-free and keyed by source and database identity (W5-index §2.3), for <c>AuroraService</c> to show each visitor through
/// their gates, and for the API packet (D31) to expose.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Server">The server facts: the platform, the Aurora version and the instance that answered (A1).</param>
/// <param name="Capacity">What the host's capacity source said this interval (A8).</param>
/// <param name="Replicas">The cluster's instances (A3), or <see langword="null" /> off Aurora.</param>
/// <param name="Waits">The instance's cumulative waits (A4), or <see langword="null" /> off Aurora.</param>
/// <param name="Database">This database's commit latency and DML (A4), or <see langword="null" /> off Aurora.</param>
/// <param name="Statements">Every statement the read ran, for "Show SQL".</param>
internal sealed record AuroraFacts(
    DateTimeOffset ReadAt,
    PostgresServerFacts Server,
    CapacityReading Capacity,
    ServerRead<AuroraReplicaStatus>? Replicas,
    ServerRead<AuroraWaitProfile>? Waits,
    ServerRead<AuroraDatabaseActivity>? Database,
    IReadOnlyList<HealthStatement> Statements)
{
    /// <summary>Whether the server is Aurora - and so whether the section draws anything but the capacity.</summary>
    public bool IsAurora => Server.Platform.Kind == PostgresPlatformKind.AmazonAurora;

    /// <summary>The sections that failed, by name, for a test: empty when every one read.</summary>
    public IReadOnlyList<string> Failures =>
    [
        .. new (string Name, bool Failed, string? Note)[]
            {
                ("replicas", Replicas is { Read: false }, Replicas?.Note),
                ("waits", Waits is { Read: false }, Waits?.Note),
                ("commit latency", Database is { Read: false }, Database?.Note),
            }
            .Where(static x => x.Failed)
            .Select(static x => x.Name + ": " + x.Note),
    ];
}

/// <summary>Reads <see cref="AuroraFacts" /> for one database.</summary>
/// <remarks>An interface so a component or service test can hand the section facts without Postgres (D12).</remarks>
internal interface IAuroraFactsReader
{
    /// <summary>Reads every section. Throws only when the server's version and platform cannot be read.</summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AuroraFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken);

    /// <summary>
    /// This database's sessions and their waits (<see cref="AuroraSessionWaits" />): the activity gate's, so read only for a
    /// visitor who passes it. Throws what Postgres throws.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<AuroraSessionWaits> ReadSessionsAsync(ResolvedSource resolved, CancellationToken cancellationToken);
}

/// <summary>
/// The real reader: every section through <see cref="HealthSqlRunner" /> - the advisor's read-only transaction under
/// <c>statement_timeout</c> and <c>lock_timeout</c>, as <c>SqlConsoleRole</c> when one is set - on one connection, with a memo of
/// its own so each shared read runs once, and with the sweep's own records (<see cref="AuroraReplicaStatus" />,
/// <see cref="AuroraWaitProfile" />, <see cref="AuroraDatabaseActivity" />), so the page and the advisor read the same
/// statements.
/// </summary>
/// <remarks>
/// <b>Aurora's reads only on Aurora.</b> On any other server the facts are the platform and the capacity alone: no Aurora
/// function exists there to call. A section that fails is that section's sentence and SQLSTATE; Debug for a state the host
/// chose (<c>42501</c>, a timeout), event 9501 at the throttle's level for anything else.
/// </remarks>
internal sealed class AuroraFactsReader : IAuroraFactsReader
{
    private readonly IStudioSettings settings;
    private readonly IServiceProvider services;
    private readonly CapacityReader capacity;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<AuroraFactsReader> logger;
    private readonly TimeProvider timeProvider;
    private readonly IStudioRuntime runtime;

    public AuroraFactsReader(
        IStudioSettings settings,
        IServiceProvider services,
        CapacityReader capacity,
        StudioLogThrottle throttle,
        ILogger<AuroraFactsReader> logger,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null)
    {
        this.settings = settings;
        this.services = services;
        this.capacity = capacity;
        this.throttle = throttle;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
    }

    /// <inheritdoc />
    public async Task<AuroraFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        CapacityReading reading = await capacity.GetAsync(resolved, server, cancellationToken).ConfigureAwait(false);

        if (server.Platform.Kind != PostgresPlatformKind.AmazonAurora)
        {
            return new AuroraFacts(context.Now, server, reading, null, null, null, context.Sql.Statements);
        }

        ServerRead<AuroraReplicaStatus> replicas = await SectionAsync(context, "replica status", AuroraReplicaStatus.SharedAsync, cancellationToken).ConfigureAwait(false);
        ServerRead<AuroraWaitProfile> waits = await SectionAsync(context, "waits", AuroraWaitProfile.SharedAsync, cancellationToken).ConfigureAwait(false);
        ServerRead<AuroraDatabaseActivity> database = await SectionAsync(context, "commit latency", AuroraDatabaseActivity.SharedAsync, cancellationToken).ConfigureAwait(false);

        return new AuroraFacts(context.Now, server, reading, replicas, waits, database, context.Sql.Statements);
    }

    /// <inheritdoc />
    public async Task<AuroraSessionWaits> ReadSessionsAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (server.Platform.Kind != PostgresPlatformKind.AmazonAurora)
        {
            return new AuroraSessionWaits(context.Now, []);
        }

        var names = AuroraQueries.WaitNames();
        IReadOnlyList<(string? Events, string? Types)> named = await context.Sql
            .QueryAsync(names, static reader => (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)), cancellationToken)
            .ConfigureAwait(false);

        WaitNames waitNames = named.Count == 0 ? WaitNames.FromJson(null, null) : WaitNames.FromJson(named[0].Events, named[0].Types);

        var sessions = AuroraQueries.BackendWaits();
        IReadOnlyList<AuroraSession> rows = await context.Sql
            .QueryAsync(
                sessions,
                reader => AuroraSessionWaits.Session(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    waitNames),
                cancellationToken)
            .ConfigureAwait(false);

        return new AuroraSessionWaits(context.Now, rows);
    }

    private HealthCheckContext Context(ResolvedSource resolved, HealthConnection connection, CancellationToken cancellationToken)
    {
        TimeSpan timeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = timeout, Role = settings.SqlConsoleRole }, observer: null);

        return new HealthCheckContext(resolved, settings, runtime, services, sql, timeProvider.GetUtcNow(), new HealthSweepMemo(), cancellationToken);
    }

    private async Task<ServerRead<T>> SectionAsync<T>(
        HealthCheckContext context,
        string section,
        Func<HealthCheckContext, Task<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return ServerRead<T>.Of(await read(context).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ServerRead<T>(null, Failure(exception, context, section));
        }
    }

    /// <summary>A section's sentence for a failure, and the log line: Debug for an expected state, 9501 at the throttle's level otherwise.</summary>
    private string Failure(Exception exception, HealthCheckContext context, string section)
    {
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        string? expected = postgres?.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => "The role the studio reads as may not read this (SQLSTATE 42501).",
            PostgresErrorCodes.QueryCanceled => "The read ran out of its statement_timeout; it is tried again at the next interval.",
            PostgresErrorCodes.LockNotAvailable => "The read waited too long for a lock; it is tried again at the next interval.",
            _ => PostgresFailure.IsTimeout(exception) ? "The read timed out; it is tried again at the next interval." : null,
        };

        if (expected is not null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read Aurora's {Section} of {DatabaseId}: {Reason}", settings.Text.ProductName, section, context.DatabaseId, expected);
            }

            return expected;
        }

        LogLevel level = throttle.WarningOrDebug("Aurora.Read", context.StoreKey, context.DatabaseId, StudioLogThrottle.KindOf(exception));

        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioAuroraReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }
        else
        {
            logger.AuroraReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "This could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ").");
    }
}

/// <summary>
/// One <see cref="AuroraFacts" /> per database per <c>HealthInterval</c>, however many visitors or clicks ask (D10), and one
/// <see cref="AuroraSessionWaits" /> likewise for the visitors the activity gate admits - single-flight caches keyed by source and
/// database identity.
/// </summary>
/// <remarks>
/// <b>Authorization comes before this, never inside it</b>, as for the sweep: the caller resolves the scope - the store policy
/// for the database as a whole - first, and what a visitor is shown is decided after.
/// </remarks>
internal sealed class AuroraFactsCache
{
    private readonly IAuroraFactsReader reader;
    private readonly StudioSnapshotCache cache;
    private int reads;
    private int sessionReads;

    public AuroraFactsCache(IAuroraFactsReader reader, IStudioSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(settings);

        this.reader = reader;
        cache = new StudioSnapshotCache(timeProvider ?? TimeProvider.System, settings.EffectiveHealthInterval);
    }

    /// <summary>How many times the facts were actually read, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>How many times the sessions were actually read, for a test.</summary>
    internal int SessionReadCount => Volatile.Read(ref sessionReads);

    /// <summary>The database's facts: a recent read, or a new one shared with everybody who asks while it runs.</summary>
    public Task<AuroraFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync("facts|" + resolved.Source.Key + "|" + resolved.Target.Identity, token =>
        {
            Interlocked.Increment(ref reads);
            return reader.ReadAsync(resolved, token);
        }, cancellationToken);
    }

    /// <summary>The database's sessions and their waits - for a caller that has asked the activity gate first.</summary>
    public Task<AuroraSessionWaits> GetSessionsAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync("sessions|" + resolved.Source.Key + "|" + resolved.Target.Identity, token =>
        {
            Interlocked.Increment(ref sessionReads);
            return reader.ReadSessionsAsync(resolved, token);
        }, cancellationToken);
    }
}
