using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Replication;

/// <summary>Facts as R02-R04 and the page read them, typed by hand: every threshold is tested against stated numbers.</summary>
internal static class ReplicationTestFacts
{
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public const long Segment16 = 16L * 1024 * 1024;

    public static PostgresServerFacts ServerOf(int version = 170011, PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted) =>
        new(Now, version, (version / 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".11",
            platform == PostgresPlatformKind.SelfHosted ? PostgresPlatform.SelfHosted : new PostgresPlatform(platform, "a marker"));

    public static PostgresGrants Grants(params string[] memberOf) =>
        new("app", false, [.. ServerRoles().Select(name => new PostgresRoleMembership(name, true, memberOf.Contains(name)))]);

    private static string[] ServerRoles() =>
        ["pg_read_all_stats", "pg_monitor", "pg_stat_scan_tables", "pg_read_all_settings", "pg_signal_backend", "pg_maintain"];

    public static ReplicationSettings Settings(params (string Name, string? Value, string? Unit)[] settings) =>
        new(settings.ToDictionary(static x => x.Name, static x => new ReplicationSetting(x.Name, x.Value, x.Unit, "sighup"), StringComparer.Ordinal));

    public static ReplicationSettings Archiving(string mode = "on", string? command = "/usr/local/bin/archive %p", string? library = null) =>
        Settings(
            ("archive_mode", mode, null),
            ("archive_command", command, null),
            ("archive_library", library ?? string.Empty, null),
            ("wal_segment_size", Segment16.ToString(System.Globalization.CultureInfo.InvariantCulture), "B"),
            ("checkpoint_timeout", "300", "s"),
            ("max_wal_size", "1024", "MB"));

    public static ArchiverFacts Archiver(
        long archived = 10,
        string? lastArchived = "000000010000000000000010",
        DateTimeOffset? lastArchivedAt = null,
        TimeSpan? lastArchivedAge = null,
        long failed = 0,
        string? lastFailed = null,
        DateTimeOffset? lastFailedAt = null,
        string? current = "000000010000000000000012",
        bool inRecovery = false,
        ReplicationSettings? settings = null) =>
        new(
            archived,
            lastArchived,
            lastArchived is null ? null : lastArchivedAt ?? Now.AddSeconds(-30),
            lastArchived is null ? null : lastArchivedAge ?? TimeSpan.FromSeconds(30),
            failed,
            lastFailed,
            lastFailedAt,
            lastFailedAt is null ? null : Now - lastFailedAt,
            Now.AddDays(-1),
            inRecovery,
            inRecovery ? null : current,
            settings ?? Archiving(),
            Now);

    /// <summary>A history of samples, each a time and its series' values.</summary>
    public static HealthHistoryView History(params (DateTimeOffset At, (string Series, double Value)[] Values)[] samples) =>
        new([.. samples.Select(static x => new HealthSample(x.At, x.Values.ToDictionary(static v => v.Series, static v => v.Value, StringComparer.Ordinal)))]);

    public static HealthEvaluation At(HealthHistoryView? history = null) => HealthEvaluation.At(Now, history: history);

    /// <summary>A WAL file name: timeline, then the segment number split for 16 MiB segments.</summary>
    public static string Wal(ulong segment, uint timeline = 1, long segmentBytes = Segment16)
    {
        ulong perLog = 0x1_0000_0000UL / (ulong) segmentBytes;
        return timeline.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + (segment / perLog).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
            + (segment % perLog).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
