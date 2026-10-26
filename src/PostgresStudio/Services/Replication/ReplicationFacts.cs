using System.Globalization;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Replication;

// ---------------------------------------------------------------------------------------------------------------------
// The facts of the Replication page and R02-R04: gate-free reads about the server, each the same for every visitor.
// Each record reads itself through a HealthCheckContext - the sweep's, so a read two checks need runs once per sweep
// (context.Shared), or the page's own (ReplicationFactsReader), so the page and the checks run the very same statements.
// What a visitor is shown of them is ReplicationService's business, never theirs.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>
/// Whether the server answering is a primary or a standby, and on a standby how far its replay is behind what it has
/// received - exact and readable by any role (B0 §4) - when it last replayed a commit and whether replay is paused.
/// </summary>
/// <param name="InRecovery"><c>pg_is_in_recovery()</c>: a standby, or a server restoring from archive.</param>
/// <param name="ReceiveLsn">The last WAL position received and flushed by streaming; null on a primary, or when nothing streams.</param>
/// <param name="ReplayLsn">The last WAL position replayed; null on a primary.</param>
/// <param name="ReplayBehindBytes">Received less replayed, never below zero; null on a primary or with nothing received by streaming.</param>
/// <param name="LastReplayAt">The commit time of the last transaction replayed; null on a primary or before the first.</param>
/// <param name="ReplayAgeAtRead">How long ago that commit was, by the server's clock, when read.</param>
/// <param name="PauseState">
/// <c>not paused</c>, <c>pause requested</c> or <c>paused</c> (PostgreSQL 14+, in recovery only), or <see langword="null" />.
/// </param>
/// <param name="ReadAt">When, by the studio's clock.</param>
internal sealed record ReplicationRole(
    bool InRecovery,
    string? ReceiveLsn,
    string? ReplayLsn,
    long? ReplayBehindBytes,
    DateTimeOffset? LastReplayAt,
    TimeSpan? ReplayAgeAtRead,
    string? PauseState,
    DateTimeOffset ReadAt)
{
    /// <summary>Whether replay is paused (<c>pg_wal_replay_pause()</c> took effect).</summary>
    public bool IsPaused => string.Equals(PauseState, "paused", StringComparison.Ordinal);

    /// <summary><see cref="ReceiveLsn" /> as a number, for R03's samples of it (W5-R2-fix2 1).</summary>
    public ulong? ReceivePosition => WalPosition.Of(ReceiveLsn);

    /// <summary><see cref="ReplayLsn" /> as a number.</summary>
    public ulong? ReplayPosition => WalPosition.Of(ReplayLsn);

    /// <summary>The sweep's one read of it (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    public static Task<ReplicationRole> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.RoleKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<ReplicationRole> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        var query = ReplicationQueries.Recovery(PostgresFeatures.Has(PostgresFeature.WalReplayPauseState, server.VersionNumber));

        IReadOnlyList<ReplicationRole> rows = await context.Sql
            .QueryAsync(
                query,
                reader => new ReplicationRole(
                    reader.GetBoolean(0),
                    SessionReads.Text(reader, 1),
                    SessionReads.Text(reader, 2),
                    reader.IsDBNull(3) ? null : (long) Math.Max(0d, reader.GetDouble(3)),
                    SessionReads.Value<DateTimeOffset>(reader, 4),
                    SessionReads.Seconds(reader, 5),
                    SessionReads.Text(reader, 6),
                    context.Now),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The recovery read came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
    }
}

/// <summary>One WAL sender of <c>pg_stat_replication</c>: a standby, or a base backup, streaming from this server.</summary>
/// <param name="ApplicationName">Its <c>application_name</c> - a name, behind the activity gate.</param>
/// <param name="ClientAddress">Its client address - behind the activity gate; null for a local socket or when not readable.</param>
/// <param name="User">The role it connected as - behind the activity gate.</param>
/// <param name="State">
/// <c>streaming</c>, <c>catchup</c>, <c>startup</c>, <c>backup</c> or <c>stopping</c>; <see langword="null" /> when the
/// reading role may not read it (no <c>pg_read_all_stats</c>), which is how <see cref="Readable" /> is told.
/// </param>
/// <param name="SyncState"><c>async</c>, <c>potential</c>, <c>sync</c> or <c>quorum</c>.</param>
/// <param name="BehindBytes">This server's WAL position less the standby's replay position, never below zero.</param>
/// <param name="SentBehindBytes">What was sent less what was replayed - the part of the lag the standby holds.</param>
/// <param name="WriteLag">How long the standby took to write recent WAL, as it reported.</param>
/// <param name="FlushLag">How long it took to flush it.</param>
/// <param name="ReplayLag">
/// How long it took to replay it - null once the standby has caught up and nothing new arrives, so an idle primary never
/// grows it (PostgreSQL's documentation of <c>pg_stat_replication</c>).
/// </param>
/// <param name="Physical">
/// Whether it is a physical sender - a standby or a base backup, connected to no database - rather than a logical one of
/// this database (a subscription, a change data capture connector). Only a physical sender is a standby R03 judges. A
/// logical sender of another database is never a row at all: it is counted (<see cref="StandbySnapshot.OtherDatabases" />)
/// and its names never leave the server (W5-R2-fix B1).
/// </param>
internal sealed record StandbyRow(
    string? ApplicationName,
    string? ClientAddress,
    string? User,
    string? State,
    string? SyncState,
    long? BehindBytes,
    long? SentBehindBytes,
    TimeSpan? WriteLag,
    TimeSpan? FlushLag,
    TimeSpan? ReplayLag,
    bool Physical = true)
{
    /// <summary>Whether the reading role may read its state and lag (<c>pg_read_all_stats</c>, or a sender of its own role).</summary>
    public bool Readable => State is not null;
}

/// <summary>The WAL senders of the server answering: physical ones first, most behind first.</summary>
/// <param name="Total">How many physical and this database's logical senders there are, beyond the ones listed.</param>
/// <param name="Rows">At most <see cref="ReplicationQueries.MaxStandbys" />, physical and readable ones first.</param>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="OtherDatabases">
/// Logical senders of other databases on this server: counted, never listed or named, never judged (W5-R2-fix B1).
/// </param>
internal sealed record StandbySnapshot(long Total, IReadOnlyList<StandbyRow> Rows, DateTimeOffset ReadAt, long OtherDatabases = 0)
{
    /// <summary>How many of the listed physical senders the reading role may not read.</summary>
    public int Unreadable => Physical.Count(static x => !x.Readable);

    /// <summary>The physical senders: the standbys and base backups, which R03 judges.</summary>
    public IReadOnlyList<StandbyRow> Physical => [.. Rows.Where(static x => x.Physical)];

    /// <summary>The sweep's one read of it.</summary>
    public static Task<StandbySnapshot> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.StandbysKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<StandbySnapshot> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ReplicationQueries.Standbys();
        IReadOnlyList<(long Total, long Others, StandbyRow? Row)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2)
                        ? null
                        : new StandbyRow(
                            SessionReads.Text(reader, 3),
                            SessionReads.Text(reader, 4),
                            SessionReads.Text(reader, 5),
                            SessionReads.Text(reader, 6),
                            SessionReads.Text(reader, 7),
                            Bytes(reader, 8),
                            Bytes(reader, 9),
                            SessionReads.Seconds(reader, 10),
                            SessionReads.Seconds(reader, 11),
                            SessionReads.Seconds(reader, 12),
                            reader.GetBoolean(2))),
                cancellationToken)
            .ConfigureAwait(false);

        return new StandbySnapshot(
            rows.Count > 0 ? rows[0].Total : 0,
            [.. rows.Where(static x => x.Row is not null).Select(static x => x.Row!)],
            context.Now,
            rows.Count > 0 ? rows[0].Others : 0);
    }

    private static long? Bytes(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (long) Math.Max(0d, reader.GetDouble(ordinal));
}

/// <summary>What a slot has beyond R01's read: 17's failover flags and a logical slot's decoding statistics (14+).</summary>
/// <param name="SlotName">The slot, to join to R01's <see cref="ReplicationSlot" /> by - never shown past that slot's own rule.</param>
/// <param name="Failover">Whether a logical slot is synchronised to standbys for failover (PostgreSQL 17+).</param>
/// <param name="Synced">Whether it is a standby's copy, synchronised from the primary (PostgreSQL 17+).</param>
/// <param name="SpillTxns">Transactions decoding spilled to disk.</param>
/// <param name="SpillBytes">Bytes decoding spilled to disk.</param>
/// <param name="StreamTxns">In-progress transactions streamed to the consumer.</param>
/// <param name="StreamBytes">Bytes streamed.</param>
/// <param name="TotalTxns">Transactions sent to the output plugin.</param>
/// <param name="TotalBytes">Bytes decoded.</param>
internal sealed record SlotStatisticsRow(
    string SlotName,
    bool? Failover,
    bool? Synced,
    long? SpillTxns,
    long? SpillBytes,
    long? StreamTxns,
    long? StreamBytes,
    long? TotalTxns,
    long? TotalBytes);

/// <summary>Every slot's <see cref="SlotStatisticsRow" />, by name.</summary>
/// <param name="Rows">The rows, at most <see cref="ReplicationQueries.MaxSlotStatistics" />.</param>
/// <param name="HasStatistics">Whether the server has <c>pg_stat_replication_slots</c> (PostgreSQL 14+).</param>
internal sealed record SlotStatisticsList(IReadOnlyList<SlotStatisticsRow> Rows, bool HasStatistics)
{
    /// <summary>The row of <paramref name="slotName" />, or <see langword="null" />.</summary>
    public SlotStatisticsRow? For(string slotName) => Rows.FirstOrDefault(x => string.Equals(x.SlotName, slotName, StringComparison.Ordinal));

    /// <summary>Reads it.</summary>
    public static async Task<SlotStatisticsList> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        bool statistics = PostgresFeatures.Has(PostgresFeature.ReplicationSlotStats, server.VersionNumber);
        var query = ReplicationQueries.SlotStatistics(statistics);

        IReadOnlyList<SlotStatisticsRow> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => new SlotStatisticsRow(
                    reader.GetString(0),
                    SessionReads.Value<bool>(reader, 1),
                    SessionReads.Value<bool>(reader, 2),
                    SessionReads.Value<long>(reader, 3),
                    SessionReads.Value<long>(reader, 4),
                    SessionReads.Value<long>(reader, 5),
                    SessionReads.Value<long>(reader, 6),
                    SessionReads.Value<long>(reader, 7),
                    SessionReads.Value<long>(reader, 8)),
                cancellationToken)
            .ConfigureAwait(false);

        return new SlotStatisticsList(rows, statistics);
    }
}

/// <summary>One setting as <c>pg_settings</c> showed it to the reading role.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Value">Its value as Postgres prints it, in <paramref name="Unit" />.</param>
/// <param name="Unit">Its unit (<c>MB</c>, <c>s</c>, <c>B</c>, <c>8kB</c>), or <see langword="null" />.</param>
/// <param name="Context">Its <c>pg_settings.context</c> (<c>postmaster</c>, <c>sighup</c>, ...).</param>
internal sealed record ReplicationSetting(string Name, string? Value, string? Unit, string? Context);

/// <summary>
/// What the reading role may see of <see cref="ReplicationQueries.SettingNames" />: a missing one is a setting this
/// release does not have, or - for <c>archive_command</c>, which every supported release has - one this role may not see.
/// </summary>
/// <param name="Settings">The rows, by name.</param>
internal sealed record ReplicationSettings(IReadOnlyDictionary<string, ReplicationSetting> Settings)
{
    /// <summary>A server that showed none.</summary>
    public static ReplicationSettings None { get; } = new(new Dictionary<string, ReplicationSetting>(StringComparer.Ordinal));

    /// <summary>One setting, or <see langword="null" />.</summary>
    public ReplicationSetting? this[string name] => Settings.TryGetValue(name, out ReplicationSetting? setting) ? setting : null;

    /// <summary><c>archive_mode</c>: <c>off</c>, <c>on</c> or <c>always</c>.</summary>
    public string? ArchiveMode => this["archive_mode"]?.Value;

    /// <summary>Whether <c>archive_mode</c> archives on this server: <c>on</c> on a primary, <c>always</c> anywhere.</summary>
    public bool Archives(bool inRecovery) => ArchiveMode switch
    {
        "always" => true,
        "on" => !inRecovery,
        _ => false,
    };

    /// <summary><c>archive_library</c> (PostgreSQL 15+), or <see langword="null" /> when unset or absent.</summary>
    public string? ArchiveLibrary => this["archive_library"]?.Value is { Length: > 0 } library ? library : null;

    /// <summary>What can be said of <c>archive_command</c> without printing it.</summary>
    public ArchiveCommandState ArchiveCommand => this["archive_command"] switch
    {
        null => ArchiveCommandState.Hidden,
        { Value: null or "" } => ArchiveCommandState.Empty,
        { Value: "(disabled)" } => ArchiveCommandState.Disabled,
        _ => ArchiveCommandState.Set,
    };

    /// <summary><c>archive_command</c>'s text when it is set and visible - behind the activity gate, because a command line may carry anything.</summary>
    public string? ArchiveCommandText => ArchiveCommand == ArchiveCommandState.Set ? this["archive_command"]!.Value : null;

    /// <summary><c>wal_segment_size</c> in bytes (16 MiB unless <c>initdb --wal-segsize</c> said otherwise).</summary>
    public long? WalSegmentBytes => Bytes("wal_segment_size");

    /// <summary><c>checkpoint_timeout</c>.</summary>
    public TimeSpan? CheckpointTimeout => Duration("checkpoint_timeout");

    /// <summary><c>max_wal_size</c> in bytes.</summary>
    public long? MaxWalSizeBytes => Bytes("max_wal_size");

    /// <summary><c>min_wal_size</c> in bytes.</summary>
    public long? MinWalSizeBytes => Bytes("min_wal_size");

    /// <summary><c>archive_timeout</c>; zero when off.</summary>
    public TimeSpan? ArchiveTimeout => Duration("archive_timeout");

    /// <summary><c>idle_replication_slot_timeout</c> (PostgreSQL 18+); zero when off.</summary>
    public TimeSpan? IdleSlotTimeout => Duration("idle_replication_slot_timeout");

    /// <summary>
    /// <c>recovery_min_apply_delay</c>: on a standby, how long replay is held back on purpose; zero when it is not.
    /// </summary>
    public TimeSpan? ApplyDelay => Duration("recovery_min_apply_delay");

    /// <summary>A size setting in bytes, from its value and its unit.</summary>
    public long? Bytes(string name) =>
        this[name] is { Value: { } value } setting
        && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
        && UnitBytes(setting.Unit) is { } unit
            ? number < 0 ? number : number * unit
            : null;

    /// <summary>A time setting, from its value and its unit.</summary>
    public TimeSpan? Duration(string name) =>
        this[name] is { Value: { } value } setting
        && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
        && UnitSeconds(setting.Unit) is { } unit
            ? TimeSpan.FromSeconds(Math.Max(0d, number) * unit)
            : null;

    /// <summary>What one of a size setting's units is in bytes: <c>B</c>, <c>kB</c>, <c>MB</c>, <c>GB</c>, <c>TB</c>, and a block multiple such as <c>8kB</c>.</summary>
    internal static long? UnitBytes(string? unit)
    {
        if (string.IsNullOrEmpty(unit))
        {
            return 1;
        }

        int digits = 0;
        while (digits < unit.Length && char.IsAsciiDigit(unit[digits]))
        {
            digits++;
        }

        long multiple = digits == 0 ? 1 : long.Parse(unit.AsSpan(0, digits), CultureInfo.InvariantCulture);

        long? scale = unit[digits..] switch
        {
            "B" => 1L,
            "kB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => null,
        };

        return scale * multiple;
    }

    /// <summary>What one of a time setting's units is in seconds.</summary>
    internal static double? UnitSeconds(string? unit) => unit switch
    {
        "ms" => 0.001,
        "s" or null or "" => 1,
        "min" => 60,
        "h" => 3600,
        "d" => 86400,
        _ => null,
    };

    /// <summary>The sweep's one read of it.</summary>
    public static Task<ReplicationSettings> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.SettingsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<ReplicationSettings> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ReplicationQueries.Settings();
        IReadOnlyList<ReplicationSetting> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => new ReplicationSetting(reader.GetString(0), SessionReads.Text(reader, 1), SessionReads.Text(reader, 2), SessionReads.Text(reader, 3)),
                cancellationToken)
            .ConfigureAwait(false);

        return new ReplicationSettings(rows.ToDictionary(static x => x.Name, StringComparer.Ordinal));
    }
}

/// <summary>What can be said of <c>archive_command</c> without printing it.</summary>
internal enum ArchiveCommandState
{
    /// <summary>It has a value.</summary>
    Set,

    /// <summary>It is empty: with <c>archive_mode</c> on, Postgres keeps every WAL file and archives none.</summary>
    Empty,

    /// <summary><c>archive_mode</c> is off, and Postgres shows the command as <c>(disabled)</c>.</summary>
    Disabled,

    /// <summary><c>pg_settings</c> did not show it to the reading role.</summary>
    Hidden,
}

/// <summary>
/// The archiver: <c>pg_stat_archiver</c>'s counters, how long ago its last success and failure were, and the WAL file the
/// server is writing now, which <see cref="Backlog" /> is measured to.
/// </summary>
/// <param name="ArchivedCount">Files archived since the statistics were reset.</param>
/// <param name="LastArchivedWal">The last file archived - a segment, or a <c>.history</c>, <c>.partial</c> or <c>.backup</c> file.</param>
/// <param name="LastArchivedAt">When.</param>
/// <param name="LastArchivedAgeAtRead">How long ago, by the server's clock.</param>
/// <param name="FailedCount">Failed attempts since the statistics were reset.</param>
/// <param name="LastFailedWal">The file of the last failed attempt.</param>
/// <param name="LastFailedAt">When.</param>
/// <param name="LastFailedAgeAtRead">How long ago, by the server's clock.</param>
/// <param name="StatsReset">When the archiver's statistics were last reset.</param>
/// <param name="InRecovery">Whether the server is in recovery, where no current WAL file can be named.</param>
/// <param name="CurrentWal">The WAL file being written (<c>pg_walfile_name(pg_current_wal_lsn())</c>); null in recovery.</param>
/// <param name="Settings">The settings it is judged with: <c>archive_mode</c>, the command, the library and the segment size.</param>
/// <param name="ReadAt">When, by the studio's clock.</param>
internal sealed record ArchiverFacts(
    long ArchivedCount,
    string? LastArchivedWal,
    DateTimeOffset? LastArchivedAt,
    TimeSpan? LastArchivedAgeAtRead,
    long FailedCount,
    string? LastFailedWal,
    DateTimeOffset? LastFailedAt,
    TimeSpan? LastFailedAgeAtRead,
    DateTimeOffset? StatsReset,
    bool InRecovery,
    string? CurrentWal,
    ReplicationSettings Settings,
    DateTimeOffset ReadAt)
{
    /// <summary>Whether this server archives at all: <c>archive_mode</c> on a primary, or <c>always</c>.</summary>
    public bool Archives => Settings.Archives(InRecovery);

    /// <summary>How far the archiver is behind the file being written, or <see langword="null" /> when that cannot be measured.</summary>
    public ArchiveBacklog? Backlog => ArchiveBacklog.Measure(LastArchivedWal, CurrentWal, Settings.WalSegmentBytes);

    /// <summary>Whether the last attempt failed: a failure after the last success, or failures and no success at all.</summary>
    public bool LastAttemptFailed =>
        FailedCount > 0 && (LastArchivedAt is null || (LastFailedAt is { } failed && failed > LastArchivedAt));

    /// <summary>The sweep's one read of it.</summary>
    public static Task<ArchiverFacts> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.ArchiverKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<ArchiverFacts> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ReplicationSettings settings = await ReplicationSettings.SharedAsync(context).ConfigureAwait(false);

        var query = ReplicationQueries.Archiver();
        IReadOnlyList<ArchiverFacts> rows = await context.Sql
            .QueryAsync(
                query,
                reader => new ArchiverFacts(
                    reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    SessionReads.Text(reader, 1),
                    SessionReads.Value<DateTimeOffset>(reader, 2),
                    SessionReads.Seconds(reader, 3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    SessionReads.Text(reader, 5),
                    SessionReads.Value<DateTimeOffset>(reader, 6),
                    SessionReads.Seconds(reader, 7),
                    SessionReads.Value<DateTimeOffset>(reader, 8),
                    reader.GetBoolean(9),
                    SessionReads.Text(reader, 10),
                    settings,
                    context.Now),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The archiver read came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
    }
}

/// <summary>
/// Where the archiver stands against the WAL file being written: the segment being written, and - when the last file
/// archived names a segment - how many segments the archiver is behind it.
/// </summary>
/// <remarks>
/// <b>Two numbers, never one series</b> (W5-R2-fix B2). The segment being written is a position, and the distance is a
/// difference; the advisor samples them apart (<c>replication.wal-segment</c> and <c>replication.archiver-backlog</c>), and
/// writes a distance only when there is one, so that a failover, a statistics reset, a crash or a <c>.history</c> file
/// archived last - each of which leaves no last segment for a moment - is never read as the distance jumping to the whole
/// WAL sequence.
/// </remarks>
/// <param name="CurrentSegment">The number of the segment being written.</param>
/// <param name="Distance">
/// The segments from the last one archived to the one being written, or <see langword="null" /> when nothing archived
/// names a segment (<see cref="NothingArchived" />).
/// </param>
/// <param name="SegmentBytes">One segment's size.</param>
internal sealed record ArchiveBacklog(long CurrentSegment, long? Distance, long SegmentBytes)
{
    /// <summary>
    /// Whether the last archived file names no position - none since the statistics began, a <c>.history</c> file or a
    /// <c>.backup</c> label - so "nothing archived yet" (W5-R2 addition 3; W5-R2-fix2 2). A <c>.partial</c> file names the
    /// segment it is of.
    /// </summary>
    public bool NothingArchived => Distance is null;

    /// <summary>The distance in bytes, or <see langword="null" />.</summary>
    public long? Bytes => Distance * SegmentBytes;

    /// <summary>
    /// Where the archiver stands, from the last archived file and the file being written, or <see langword="null" /> when the
    /// file being written is not a segment's or the size is unknown.
    /// </summary>
    public static ArchiveBacklog? Measure(string? lastArchived, string? current, long? segmentBytes)
    {
        if (segmentBytes is not { } size || !WalFileName.TryParse(current, size, out WalSegment now))
        {
            return null;
        }

        return new ArchiveBacklog(
            (long) now.Number,
            WalFileName.TryParseArchived(lastArchived, size, out WalSegment last) ? Math.Max(0L, (long) now.Number - (long) last.Number) : null,
            size);
    }
}

/// <summary>One WAL segment as its file name identifies it: a timeline and a segment number.</summary>
/// <param name="Timeline">The timeline.</param>
/// <param name="Number">The segment number, counted across timelines (a promotion continues the numbering).</param>
internal readonly record struct WalSegment(uint Timeline, ulong Number);

/// <summary>A WAL position (<c>pg_lsn</c>) as Postgres prints it - two hex numbers, <c>16/B374D848</c> - as one number.</summary>
internal static class WalPosition
{
    /// <summary>Parses <paramref name="text" />: the high 32 bits, a slash, the low 32 bits.</summary>
    public static bool TryParse(string? text, out ulong position)
    {
        position = 0;
        int slash = text?.IndexOf('/', StringComparison.Ordinal) ?? -1;

        if (text is null || slash <= 0 || slash == text.Length - 1)
        {
            return false;
        }

        if (!uint.TryParse(text.AsSpan(0, slash), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint high)
            || !uint.TryParse(text.AsSpan(slash + 1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        position = ((ulong) high << 32) | low;
        return true;
    }

    /// <summary>The position, or <see langword="null" /> when <paramref name="text" /> is none.</summary>
    public static ulong? Of(string? text) => TryParse(text, out ulong position) ? position : null;
}

/// <summary>
/// WAL file names, parsed as Postgres builds them (<c>XLogFileName</c>): 24 hex digits - the timeline, then the segment
/// number split at every 4 GiB of WAL into a "log" and a "seg" part, the second counting segments of
/// <c>wal_segment_size</c> within that 4 GiB.
/// </summary>
/// <remarks>
/// Parsed here rather than by <c>pg_split_walfile_name</c>, which only PostgreSQL 16 has and which raises on a name it
/// does not take. What the archiver also archives - a <c>.history</c> file at a promotion, a <c>.partial</c> segment of the
/// old timeline, a base backup's <c>.backup</c> label - is read by <see cref="TryParseArchived" />: a <c>.partial</c> file
/// names the segment the old timeline had reached in its first 24 digits; a history file names none, and neither does a
/// backup label for this purpose - its 24 digits are the backup's <i>start</i> segment, and the archiver sends the label
/// before the stop segment, so it is no position the archiver has reached (W5-R2-fix2 2).
/// </remarks>
internal static class WalFileName
{
    /// <summary>4 GiB: what one "log" part of a file name spans.</summary>
    private const ulong LogSpan = 0x1_0000_0000UL;

    /// <summary>
    /// Parses a file the archiver archived as the segment it has reached: a segment, or a <c>.partial</c> file
    /// (<c>000000010000000000000003.partial</c>) by the segment its first 24 digits name. A <c>.history</c> file and a
    /// <c>.backup</c> label (<c>000000010000000000000002.00000028.backup</c>, whose digits are the backup's start segment,
    /// archived before its stop segment) name no position, and read as "nothing to measure from" - a gap in the distance,
    /// never a stand-in for one.
    /// </summary>
    public static bool TryParseArchived(string? name, long segmentBytes, out WalSegment segment)
    {
        segment = default;

        if (name is null || name.Length < 24)
        {
            return false;
        }

        if (name.Length == 24)
        {
            return TryParse(name, segmentBytes, out segment);
        }

        return string.Equals(name[24..], ".partial", StringComparison.Ordinal) && TryParse(name[..24], segmentBytes, out segment);
    }

    /// <summary>Parses a segment's file name for a server whose segments are <paramref name="segmentBytes" /> long.</summary>
    public static bool TryParse(string? name, long segmentBytes, out WalSegment segment)
    {
        segment = default;

        if (name is not { Length: 24 } || segmentBytes <= 0 || (ulong) segmentBytes > LogSpan || LogSpan % (ulong) segmentBytes != 0)
        {
            return false;
        }

        if (!uint.TryParse(name.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint timeline)
            || !uint.TryParse(name.AsSpan(8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint log)
            || !uint.TryParse(name.AsSpan(16, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint seg))
        {
            return false;
        }

        ulong perLog = LogSpan / (ulong) segmentBytes;

        if (seg >= perLog)
        {
            return false;
        }

        segment = new WalSegment(timeline, log * perLog + seg);
        return true;
    }
}

/// <summary>
/// <c>pg_wal</c> as the archiver leaves it: files waiting in <c>archive_status</c> (<c>.ready</c>) and the directory's size.
/// Read only for a role that is a member of <c>pg_monitor</c>; evidence, never a trigger (W5-R2 addition 3).
/// </summary>
/// <param name="ReadyFiles">WAL files waiting for the archiver.</param>
/// <param name="Files">Files in <c>pg_wal</c>.</param>
/// <param name="Bytes">Their size.</param>
internal sealed record WalDirectoryFacts(long ReadyFiles, long Files, long Bytes)
{
    /// <summary>
    /// The sweep's one read of it: <see langword="null" /> - never read - when the reading role is not a member of
    /// <c>pg_monitor</c>, which <c>pg_ls_waldir()</c> and <c>pg_ls_archive_statusdir()</c> are restricted to.
    /// </summary>
    public static Task<WalDirectoryFacts?> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.WalDirectoryKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it, if the role may.</summary>
    public static async Task<WalDirectoryFacts?> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);

        if (!grants.Holds(PostgresGrant.Monitor))
        {
            return null;
        }

        var query = ReplicationQueries.WalDirectory();
        IReadOnlyList<WalDirectoryFacts> rows = await context.Sql
            .QueryAsync(query, static reader => new WalDirectoryFacts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)), cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1 ? rows[0] : null;
    }
}

/// <summary><c>pg_stat_wal</c> (PostgreSQL 14+): what the server has written to WAL since the statistics were reset.</summary>
/// <param name="Records">WAL records.</param>
/// <param name="FullPageImages">Full-page images among them - the first change to a page after a checkpoint.</param>
/// <param name="Bytes">Bytes of WAL.</param>
/// <param name="BuffersFull">Times WAL was written because the WAL buffers were full.</param>
/// <param name="Writes">WAL writes (14-17; 18 moved them to <c>pg_stat_io</c>).</param>
/// <param name="Syncs">WAL syncs (14-17).</param>
/// <param name="WriteTimeMs">Time spent writing (14-17, with <c>track_wal_io_timing</c>).</param>
/// <param name="SyncTimeMs">Time spent syncing (14-17, with <c>track_wal_io_timing</c>).</param>
/// <param name="StatsReset">When the statistics were last reset.</param>
internal sealed record WalStatistics(
    long Records,
    long FullPageImages,
    double Bytes,
    long BuffersFull,
    long? Writes,
    long? Syncs,
    double? WriteTimeMs,
    double? SyncTimeMs,
    DateTimeOffset? StatsReset)
{
    /// <summary>
    /// The sweep's one read of it, or <see langword="null" /> below PostgreSQL 14, which has no <c>pg_stat_wal</c>.
    /// </summary>
    public static Task<WalStatistics?> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.WalKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<WalStatistics?> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (!PostgresFeatures.Has(PostgresFeature.PgStatWal, server.VersionNumber))
        {
            return null;
        }

        var query = ReplicationQueries.Wal();
        IReadOnlyList<WalStatistics> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => new WalStatistics(
                    reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    SessionReads.Value<long>(reader, 4),
                    SessionReads.Value<long>(reader, 5),
                    SessionReads.Value<double>(reader, 6),
                    SessionReads.Value<double>(reader, 7),
                    SessionReads.Value<DateTimeOffset>(reader, 8)),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1 ? rows[0] : null;
    }
}

/// <summary>How the checkpoint counters are counted on this release - the denominator of R04's requested share (B0 G8).</summary>
internal enum CheckpointCounting
{
    /// <summary>
    /// Up to 16: <c>pg_stat_bgwriter.checkpoints_timed</c> and <c>checkpoints_req</c>; the share is <c>req / (timed + req)</c>.
    /// <c>checkpoints_timed</c> counts the checkpoints an idle server skipped too (B0 G8's table; the code increments it when a
    /// timed checkpoint is due, before it is skipped), so idleness dilutes the share as on 17 (W5-R2-fix 10).
    /// </summary>
    Bgwriter,

    /// <summary>
    /// 17: <c>pg_stat_checkpointer.num_timed</c> and <c>num_requested</c>, where <c>num_timed</c> counts the checkpoints an
    /// idle server skipped too - so the share <c>requested / (timed + requested)</c> is diluted by idleness.
    /// </summary>
    CheckpointerWithSkipped,

    /// <summary>18: <c>num_done</c> is the checkpoints that ran; the share is <c>requested / done</c>.</summary>
    CheckpointerDone,
}

/// <summary>The checkpoint and background-writer counters, as this release keeps them.</summary>
/// <param name="Source">The view the checkpoint counters came from: <c>pg_stat_bgwriter</c> or <c>pg_stat_checkpointer</c>.</param>
/// <param name="Counting">How they are counted, and so the requested share's denominator.</param>
/// <param name="Timed">Checkpoints at <c>checkpoint_timeout</c>, skipped ones included on every release.</param>
/// <param name="Requested">Checkpoints requested - by WAL reaching <c>max_wal_size</c>, a <c>CHECKPOINT</c>, a base backup, ....</param>
/// <param name="Done">Checkpoints that ran (PostgreSQL 18+).</param>
/// <param name="RestartpointsTimed">A standby's timed restartpoints (17+).</param>
/// <param name="RestartpointsRequested">A standby's requested restartpoints (17+).</param>
/// <param name="RestartpointsDone">A standby's restartpoints that ran (17+).</param>
/// <param name="WriteTimeMs">Time checkpoints spent writing.</param>
/// <param name="SyncTimeMs">Time checkpoints spent syncing.</param>
/// <param name="BuffersWritten">Buffers checkpoints wrote.</param>
/// <param name="BuffersClean">Buffers the background writer wrote.</param>
/// <param name="MaxwrittenClean">Times the background writer stopped at <c>bgwriter_lru_maxpages</c>.</param>
/// <param name="BuffersBackend">Buffers backends wrote themselves (up to 16; 16+ has them in <c>pg_stat_io</c>).</param>
/// <param name="BuffersAlloc">Buffers allocated.</param>
/// <param name="StatsReset">When the checkpoint counters were last reset.</param>
internal sealed record CheckpointStatistics(
    string Source,
    CheckpointCounting Counting,
    long Timed,
    long Requested,
    long? Done,
    long? RestartpointsTimed,
    long? RestartpointsRequested,
    long? RestartpointsDone,
    double? WriteTimeMs,
    double? SyncTimeMs,
    long? BuffersWritten,
    long? BuffersClean,
    long? MaxwrittenClean,
    long? BuffersBackend,
    long? BuffersAlloc,
    DateTimeOffset? StatsReset)
{
    /// <summary>The denominator of the requested share on this release: <c>timed + requested</c>, or 18's <c>done</c>.</summary>
    public static double Denominator(CheckpointCounting counting, double timed, double requested, double? done) =>
        counting == CheckpointCounting.CheckpointerDone && done is { } ran ? ran : timed + requested;

    /// <summary>The requested share since the counters were reset, or <see langword="null" /> with no checkpoint yet.</summary>
    public double? RequestedShare
    {
        get
        {
            double denominator = Denominator(Counting, Timed, Requested, Done);
            return denominator <= 0 ? null : Math.Min(1d, Requested / denominator);
        }
    }

    /// <summary>The sweep's one read of it.</summary>
    public static Task<CheckpointStatistics> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ReplicationQueries.CheckpointsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<CheckpointStatistics> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        bool checkpointer = PostgresFeatures.Has(PostgresFeature.PgStatCheckpointer, server.VersionNumber);
        CheckpointCounting counting = !checkpointer
            ? CheckpointCounting.Bgwriter
            : PostgresFeatures.Has(PostgresFeature.CheckpointerNumDone, server.VersionNumber)
                ? CheckpointCounting.CheckpointerDone
                : CheckpointCounting.CheckpointerWithSkipped;

        var query = ReplicationQueries.Checkpoints(checkpointer);
        IReadOnlyList<CheckpointStatistics> rows = await context.Sql
            .QueryAsync(
                query,
                reader => new CheckpointStatistics(
                    reader.GetString(0),
                    counting,
                    reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    SessionReads.Value<long>(reader, 3),
                    SessionReads.Value<long>(reader, 4),
                    SessionReads.Value<long>(reader, 5),
                    SessionReads.Value<long>(reader, 6),
                    SessionReads.Value<double>(reader, 7),
                    SessionReads.Value<double>(reader, 8),
                    SessionReads.Value<long>(reader, 9),
                    SessionReads.Value<long>(reader, 10),
                    SessionReads.Value<long>(reader, 11),
                    SessionReads.Value<long>(reader, 12),
                    SessionReads.Value<long>(reader, 13),
                    SessionReads.Value<DateTimeOffset>(reader, 14)),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The checkpoint read came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
    }
}

/// <summary>One group of <c>pg_stat_io</c>: a backend type and an object, summed over every context.</summary>
/// <param name="BackendType">The backend type: <c>client backend</c>, <c>checkpointer</c>, <c>autovacuum worker</c>, ....</param>
/// <param name="Object"><c>relation</c>, <c>temp relation</c>, or (18) <c>wal</c>.</param>
/// <param name="Reads">Read operations.</param>
/// <param name="Writes">Write operations.</param>
/// <param name="Extends">Extend operations.</param>
/// <param name="Hits">Shared-buffer hits.</param>
/// <param name="Evictions">Buffers evicted for this backend type.</param>
/// <param name="Fsyncs">fsync calls.</param>
/// <param name="ReadBytes">Bytes read: 18's <c>read_bytes</c>, or on 16 and 17 reads times <c>op_bytes</c>.</param>
/// <param name="WriteBytes">Bytes written, the same way.</param>
/// <param name="ExtendBytes">Bytes extended, the same way.</param>
/// <param name="ReadTimeMs">Time reading (with <c>track_io_timing</c>).</param>
/// <param name="WriteTimeMs">Time writing (with <c>track_io_timing</c>).</param>
internal sealed record IoGroup(
    string BackendType,
    string Object,
    double? Reads,
    double? Writes,
    double? Extends,
    double? Hits,
    double? Evictions,
    double? Fsyncs,
    double? ReadBytes,
    double? WriteBytes,
    double? ExtendBytes,
    double? ReadTimeMs,
    double? WriteTimeMs)
{
    /// <summary>Whether it is one of 18's WAL groups.</summary>
    public bool IsWal => string.Equals(Object, "wal", StringComparison.Ordinal);
}

/// <summary><c>pg_stat_io</c> (PostgreSQL 16+), by backend type and object.</summary>
/// <param name="Groups">The groups, at most <see cref="ReplicationQueries.MaxIoGroups" />.</param>
/// <param name="BytesFromOpBytes">Whether the byte columns were counted as operations times <c>op_bytes</c> (16 and 17).</param>
internal sealed record IoStatistics(IReadOnlyList<IoGroup> Groups, bool BytesFromOpBytes)
{
    /// <summary>Reads it, or <see langword="null" /> below PostgreSQL 16, which has no <c>pg_stat_io</c>.</summary>
    public static async Task<IoStatistics?> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (!PostgresFeatures.Has(PostgresFeature.PgStatIo, server.VersionNumber))
        {
            return null;
        }

        var query = ReplicationQueries.Io();
        IReadOnlyList<IoGroup> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => new IoGroup(
                    reader.GetString(0),
                    reader.GetString(1),
                    SessionReads.Value<double>(reader, 2),
                    SessionReads.Value<double>(reader, 3),
                    SessionReads.Value<double>(reader, 4),
                    SessionReads.Value<double>(reader, 5),
                    SessionReads.Value<double>(reader, 6),
                    SessionReads.Value<double>(reader, 7),
                    SessionReads.Value<double>(reader, 8),
                    SessionReads.Value<double>(reader, 9),
                    SessionReads.Value<double>(reader, 10),
                    SessionReads.Value<double>(reader, 11),
                    SessionReads.Value<double>(reader, 12)),
                cancellationToken)
            .ConfigureAwait(false);

        return new IoStatistics(rows, CountsBytesFromOpBytes(server.VersionNumber));
    }

    /// <summary>
    /// Whether a server of <paramref name="versionNumber" /> has no byte columns of its own in <c>pg_stat_io</c>, so the read
    /// counts bytes as operations times <c>op_bytes</c>: 16 and 17 (18 has <c>read_bytes</c>, <c>write_bytes</c> and
    /// <c>extend_bytes</c>, and drops <c>op_bytes</c>).
    /// </summary>
    public static bool CountsBytesFromOpBytes(int versionNumber) => !PostgresFeatures.Has(PostgresFeature.PgStatIoBytes, versionNumber);
}

/// <summary>One tablespace: its name, its location and whether it is this database's default - never its size in a list.</summary>
/// <param name="Name">The tablespace.</param>
/// <param name="Location">Its directory; empty for <c>pg_default</c> and <c>pg_global</c>, which live in the data directory.</param>
/// <param name="ThisDatabase">Whether it is the connected database's default tablespace.</param>
internal sealed record TablespaceRow(string Name, string? Location, bool ThisDatabase);

/// <summary>The server's tablespaces.</summary>
/// <param name="Total">How many there are, beyond the ones listed.</param>
/// <param name="Rows">At most <see cref="ReplicationQueries.MaxTablespaces" />, by name.</param>
internal sealed record TablespaceList(long Total, IReadOnlyList<TablespaceRow> Rows)
{
    /// <summary>Reads it.</summary>
    public static async Task<TablespaceList> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ReplicationQueries.Tablespaces();
        IReadOnlyList<(TablespaceRow Row, long Total)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (new TablespaceRow(reader.GetString(0), SessionReads.Text(reader, 1), !reader.IsDBNull(2) && reader.GetBoolean(2)), reader.GetInt64(3)),
                cancellationToken)
            .ConfigureAwait(false);

        return new TablespaceList(rows.Count > 0 ? rows[0].Total : 0, [.. rows.Select(static x => x.Row)]);
    }
}
