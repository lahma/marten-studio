using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Replication;

/// <summary>How one section of the facts came out.</summary>
internal enum ReplicationReadOutcome
{
    /// <summary>Read.</summary>
    Read,

    /// <summary>Not applicable on the server's platform (<see cref="ReplicationPlatform" />), so never read.</summary>
    NotApplicable,

    /// <summary>The server's release has no such view, so never read: "needs PostgreSQL 16".</summary>
    NeedsVersion,

    /// <summary>The read failed; <see cref="ReplicationRead{T}.Note" /> is the studio's sentence and the SQLSTATE.</summary>
    Failed,
}

/// <summary>One section of <see cref="ReplicationFacts" />: its value, or why there is none.</summary>
/// <typeparam name="T">What the section reads.</typeparam>
/// <param name="Value">The value, when <see cref="Outcome" /> is <see cref="ReplicationReadOutcome.Read" />.</param>
/// <param name="Outcome">How it came out.</param>
/// <param name="Note">
/// Why there is no value: the platform's sentence, "needs PostgreSQL N", or the studio's own words for a failure with its
/// SQLSTATE - never Postgres' text, which could name what a visitor is not shown.
/// </param>
internal sealed record ReplicationRead<T>(T? Value, ReplicationReadOutcome Outcome, string? Note)
    where T : class
{
    /// <summary>A value.</summary>
    public static ReplicationRead<T> Of(T value) => new(value, ReplicationReadOutcome.Read, null);

    /// <summary>Not applicable here.</summary>
    public static ReplicationRead<T> NotApplicable(string why) => new(null, ReplicationReadOutcome.NotApplicable, why);

    /// <summary>Not on this release.</summary>
    public static ReplicationRead<T> Needs(PostgresFeature feature) =>
        new(null, ReplicationReadOutcome.NeedsVersion, "Not on this server: " + PostgresFeatures.Needs(feature) + ".");
}

/// <summary>
/// Everything the Replication page shows, read once per database per <c>HealthInterval</c> for every visitor - gate-free,
/// keyed by source and database identity, and what <c>ReplicationService</c> then shows each visitor through their gates.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Server">The server's version and platform: what every gate on a section is decided by.</param>
/// <param name="Grants">What the reading role holds.</param>
/// <param name="Role">Primary or standby, and a standby's replay.</param>
/// <param name="Standbys">The WAL senders.</param>
/// <param name="Slots">R01's slot read, reused (<see cref="ReplicationSlotSnapshot" />).</param>
/// <param name="SlotStatistics">What the slots have beyond R01's read.</param>
/// <param name="Archiver">The archiver.</param>
/// <param name="WalDirectory"><c>pg_wal</c>'s <c>.ready</c> count and size; a <see langword="null" /> value is "no <c>pg_monitor</c>".</param>
/// <param name="Wal"><c>pg_stat_wal</c>.</param>
/// <param name="Checkpoints">The checkpoint counters.</param>
/// <param name="Io"><c>pg_stat_io</c>.</param>
/// <param name="Tablespaces">The tablespaces.</param>
/// <param name="Settings">The settings the sections are read with.</param>
/// <param name="Statements">Every statement the read ran, for "Show SQL".</param>
internal sealed record ReplicationFacts(
    DateTimeOffset ReadAt,
    PostgresServerFacts Server,
    ReplicationRead<PostgresGrants> Grants,
    ReplicationRead<ReplicationRole> Role,
    ReplicationRead<StandbySnapshot> Standbys,
    ReplicationRead<ReplicationSlotSnapshot> Slots,
    ReplicationRead<SlotStatisticsList> SlotStatistics,
    ReplicationRead<ArchiverFacts> Archiver,
    ReplicationRead<WalDirectoryFacts> WalDirectory,
    ReplicationRead<WalStatistics> Wal,
    ReplicationRead<CheckpointStatistics> Checkpoints,
    ReplicationRead<IoStatistics> Io,
    ReplicationRead<TablespaceList> Tablespaces,
    ReplicationRead<ReplicationSettings> Settings,
    IReadOnlyList<HealthStatement> Statements)
{
    /// <summary>The sections that failed, by name, for a test and the matrix: empty when every section read or said why not.</summary>
    public IReadOnlyList<string> Failures =>
    [
        .. new (string Name, ReplicationReadOutcome Outcome, string? Note)[]
            {
                ("grants", Grants.Outcome, Grants.Note), ("role", Role.Outcome, Role.Note), ("standbys", Standbys.Outcome, Standbys.Note),
                ("slots", Slots.Outcome, Slots.Note), ("slot statistics", SlotStatistics.Outcome, SlotStatistics.Note),
                ("archiver", Archiver.Outcome, Archiver.Note), ("pg_wal", WalDirectory.Outcome, WalDirectory.Note),
                ("wal", Wal.Outcome, Wal.Note), ("checkpoints", Checkpoints.Outcome, Checkpoints.Note), ("io", Io.Outcome, Io.Note),
                ("tablespaces", Tablespaces.Outcome, Tablespaces.Note), ("settings", Settings.Outcome, Settings.Note),
            }
            .Where(static x => x.Outcome == ReplicationReadOutcome.Failed)
            .Select(static x => x.Name + ": " + x.Note),
    ];
}

/// <summary>Reads <see cref="ReplicationFacts" /> for one database - the page's read, through the same statements as the checks.</summary>
/// <remarks>An interface so a component or service test can hand the page facts without Postgres (D12).</remarks>
internal interface IReplicationFactsReader
{
    /// <summary>Reads every section. Throws only when the server's version and platform cannot be read; a section's failure is its own value.</summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ReplicationFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken);

    /// <summary>One tablespace's size, on demand; throws what Postgres throws (<c>42501</c> for one the role may not size).</summary>
    /// <param name="resolved">The database, resolved with no tenant.</param>
    /// <param name="tablespace">The tablespace, by name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<long?> TablespaceSizeAsync(ResolvedSource resolved, string tablespace, CancellationToken cancellationToken);
}

/// <summary>
/// The real reader: every section through <see cref="HealthSqlRunner" /> - the advisor's read-only transaction under
/// <c>statement_timeout</c> and <c>lock_timeout</c>, <c>search_path</c> pinned, as <c>SqlConsoleRole</c> when one is set -
/// on one connection, with a memo of its own so each shared read (the platform, the grants, the settings) runs once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The checks' own reads.</b> Each section is the record R02-R04 gather through (<see cref="ArchiverFacts" />,
/// <see cref="CheckpointStatistics" />, ...) and R01's <see cref="ReplicationSlotSnapshot" />, so what the page shows and
/// what the advisor judges are the same statements; a section a check does not need (<c>pg_stat_io</c>, the tablespaces,
/// the slot statistics) is read here only.
/// </para>
/// <para>
/// <b>A section never read where it does not apply</b>: Aurora's checkpoint, WAL, archiver, tablespace and standby
/// sections, a managed provider's archiver (<see cref="ReplicationPlatform" />), and a view the server's release does not
/// have (<see cref="PostgresFeatures" />). A section that fails is that section's sentence and SQLSTATE; Debug for a state
/// the host chose (<c>42501</c>, a timeout), event 9430 at the throttle's level for anything else.
/// </para>
/// </remarks>
internal sealed class ReplicationFactsReader : IReplicationFactsReader
{
    private readonly IStudioSettings settings;
    private readonly IServiceProvider services;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<ReplicationFactsReader> logger;
    private readonly TimeProvider timeProvider;
    private readonly IStudioRuntime runtime;

    public ReplicationFactsReader(
        IStudioSettings settings,
        IServiceProvider services,
        StudioLogThrottle throttle,
        ILogger<ReplicationFactsReader> logger,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null)
    {
        this.settings = settings;
        this.services = services;
        this.throttle = throttle;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
    }

    /// <inheritdoc />
    public async Task<ReplicationFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        PostgresPlatform platform = server.Platform;
        int version = server.VersionNumber;

        ReplicationRead<PostgresGrants> grants = await ReadAsync(context, "grants", PostgresGrants.SharedAsync, cancellationToken).ConfigureAwait(false);
        ReplicationRead<ReplicationSettings> replicationSettings = await ReadAsync(context, "settings", ReplicationSettings.SharedAsync, cancellationToken).ConfigureAwait(false);
        ReplicationRead<ReplicationRole> role = await ReadAsync(context, "recovery", ReplicationRole.SharedAsync, cancellationToken).ConfigureAwait(false);

        ReplicationRead<ReplicationSlotSnapshot> slots = await ReadAsync(context, "slots", ReplicationSlotSnapshot.SharedAsync, cancellationToken).ConfigureAwait(false);
        ReplicationRead<SlotStatisticsList> slotStatistics = await ReadAsync(context, "slot statistics", x => SlotStatisticsList.ReadAsync(x, cancellationToken), cancellationToken).ConfigureAwait(false);

        ReplicationRead<StandbySnapshot> standbys = ReplicationPlatform.NotApplicable(platform, ReplicationSection.Standbys) is { } noStandbys
            ? ReplicationRead<StandbySnapshot>.NotApplicable(noStandbys)
            : await ReadAsync(context, "standbys", StandbySnapshot.SharedAsync, cancellationToken).ConfigureAwait(false);

        ReplicationRead<ArchiverFacts> archiver;
        ReplicationRead<WalDirectoryFacts> directory;

        if (ReplicationPlatform.NotApplicable(platform, ReplicationSection.Archiver) is { } noArchiver)
        {
            archiver = ReplicationRead<ArchiverFacts>.NotApplicable(noArchiver);
            directory = ReplicationRead<WalDirectoryFacts>.NotApplicable(noArchiver);
        }
        else
        {
            archiver = await ReadAsync(context, "archiver", ArchiverFacts.SharedAsync, cancellationToken).ConfigureAwait(false);
            directory = await ReadNullableAsync(context, "pg_wal", WalDirectoryFacts.SharedAsync, cancellationToken).ConfigureAwait(false);
        }

        ReplicationRead<WalStatistics> wal = ReplicationPlatform.NotApplicable(platform, ReplicationSection.Wal) is { } noWal
            ? ReplicationRead<WalStatistics>.NotApplicable(noWal)
            : !PostgresFeatures.Has(PostgresFeature.PgStatWal, version)
                ? ReplicationRead<WalStatistics>.Needs(PostgresFeature.PgStatWal)
                : await ReadNullableAsync(context, "wal", WalStatistics.SharedAsync, cancellationToken).ConfigureAwait(false);

        ReplicationRead<CheckpointStatistics> checkpoints = ReplicationPlatform.NotApplicable(platform, ReplicationSection.Checkpoints) is { } noCheckpoints
            ? ReplicationRead<CheckpointStatistics>.NotApplicable(noCheckpoints)
            : await ReadAsync(context, "checkpoints", CheckpointStatistics.SharedAsync, cancellationToken).ConfigureAwait(false);

        ReplicationRead<IoStatistics> io = !PostgresFeatures.Has(PostgresFeature.PgStatIo, version)
            ? ReplicationRead<IoStatistics>.Needs(PostgresFeature.PgStatIo)
            : await ReadNullableAsync(context, "io", x => IoStatistics.ReadAsync(x, cancellationToken), cancellationToken).ConfigureAwait(false);

        ReplicationRead<TablespaceList> tablespaces = ReplicationPlatform.NotApplicable(platform, ReplicationSection.Tablespaces) is { } noTablespaces
            ? ReplicationRead<TablespaceList>.NotApplicable(noTablespaces)
            : await ReadAsync(context, "tablespaces", x => TablespaceList.ReadAsync(x, cancellationToken), cancellationToken).ConfigureAwait(false);

        return new ReplicationFacts(
            context.Now,
            server,
            grants,
            role,
            standbys,
            slots,
            slotStatistics,
            archiver,
            directory,
            wal,
            checkpoints,
            io,
            tablespaces,
            replicationSettings,
            context.Sql.Statements);
    }

    /// <inheritdoc />
    public async Task<long?> TablespaceSizeAsync(ResolvedSource resolved, string tablespace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentException.ThrowIfNullOrEmpty(tablespace);

        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = Context(resolved, connection, cancellationToken);

        var query = ReplicationQueries.TablespaceSize(tablespace);
        IReadOnlyList<long?> rows = await context.Sql
            .QueryAsync(query, static reader => reader.IsDBNull(0) ? (long?) null : reader.GetInt64(0), cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private HealthCheckContext Context(ResolvedSource resolved, HealthConnection connection, CancellationToken cancellationToken)
    {
        TimeSpan timeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = timeout, Role = settings.SqlConsoleRole }, observer: null);

        return new HealthCheckContext(resolved, settings, runtime, services, sql, timeProvider.GetUtcNow(), new HealthSweepMemo(), cancellationToken);
    }

    private async Task<ReplicationRead<T>> ReadAsync<T>(
        HealthCheckContext context,
        string section,
        Func<HealthCheckContext, Task<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return ReplicationRead<T>.Of(await read(context).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ReplicationRead<T>(null, ReplicationReadOutcome.Failed, Failure(exception, context, section));
        }
    }

    /// <summary>A read whose null answer means "not read here" (no <c>pg_monitor</c>, an older release): a value of null, still read.</summary>
    private async Task<ReplicationRead<T>> ReadNullableAsync<T>(
        HealthCheckContext context,
        string section,
        Func<HealthCheckContext, Task<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return new ReplicationRead<T>(await read(context).ConfigureAwait(false), ReplicationReadOutcome.Read, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ReplicationRead<T>(null, ReplicationReadOutcome.Failed, Failure(exception, context, section));
        }
    }

    /// <summary>The section's sentence for a failure, and the log line: Debug for an expected state, 9430 at the throttle's level otherwise.</summary>
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
                logger.LogDebug(exception, "{Product} did not read the replication {Section} of {DatabaseId}: {Reason}", settings.Text.ProductName, section, context.DatabaseId, expected);
            }

            return expected;
        }

        LogLevel level = throttle.WarningOrDebug("Replication.Read", context.StoreKey, context.DatabaseId, StudioLogThrottle.KindOf(exception));

        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioReplicationReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }
        else
        {
            logger.ReplicationReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "This could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ").");
    }
}

/// <summary>
/// One <see cref="ReplicationFacts" /> per database per <c>HealthInterval</c>, however many visitors or clicks ask (D10) -
/// a single-flight <see cref="StudioSnapshotCache" /> keyed by source and database identity.
/// </summary>
/// <remarks>
/// <b>Authorization comes before this, never inside it</b>, as for the sweep and the Sessions snapshot: the key is the
/// database and nothing else, so the caller resolves the scope - the store policy for the database as a whole - first, and
/// what a visitor is shown is decided after.
/// </remarks>
internal sealed class ReplicationFactsCache
{
    private readonly IReplicationFactsReader reader;
    private readonly StudioSnapshotCache cache;
    private int reads;

    public ReplicationFactsCache(IReplicationFactsReader reader, IStudioSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(settings);

        this.reader = reader;
        cache = new StudioSnapshotCache(timeProvider ?? TimeProvider.System, settings.EffectiveHealthInterval);
    }

    /// <summary>The reader, for the on-demand reads that are never cached.</summary>
    public IReplicationFactsReader Reader => reader;

    /// <summary>How many times the facts were actually read, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>The database's facts: a recent read, or a new one shared with everybody who asks while it runs.</summary>
    public Task<ReplicationFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync(resolved.Source.Key + "|" + resolved.Target.Identity, token => ReadAsync(resolved, token), cancellationToken);
    }

    private Task<ReplicationFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);
        return reader.ReadAsync(resolved, cancellationToken);
    }
}
