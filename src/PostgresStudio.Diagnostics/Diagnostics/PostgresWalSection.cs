namespace PostgresStudio;

/// <summary>
/// What the server has written to its write-ahead log since the statistics were last reset (<c>pg_stat_wal</c>, and the WAL
/// writes and syncs, which PostgreSQL 18 moved to <c>pg_stat_io</c>): numbers about the server as a whole, the measure a
/// point-in-time-recovery window is sized by.
/// </summary>
/// <remarks>
/// <para>
/// The counters are cumulative and the server's, not this database's, so a baseline reads two of them and the time between:
/// <c>Bytes</c> over the span between two snapshots' <see cref="MeasuredAt" /> is a rate, valid while <see cref="StatsReset" />
/// is the same in both. PostgreSQL 14 added the view; the studio's floor has it.
/// </para>
/// <para>
/// <see cref="Writes" /> and <see cref="Syncs" /> are the server's WAL writes and <c>fsync</c>s since the reset, and mean the
/// same on every release: PostgreSQL 14 to 17 read them from <c>pg_stat_wal</c> (<c>wal_write</c>, <c>wal_sync</c>); from 18,
/// where they moved, from <c>pg_stat_io</c>'s rows with <c>object = 'wal'</c> (<c>writes</c>, <c>fsyncs</c>), summed over the
/// backend types that write WAL and over the <c>normal</c> context only - <c>pg_stat_io</c> also counts the creation of a WAL
/// segment (context <c>init</c>), which <c>pg_stat_wal</c> never did, so it is left out to keep one meaning across an upgrade.
/// A write is one write call and a sync one <c>fsync</c> of the log, so on a server committing synchronously both follow its
/// commits. <see cref="WriteTimeMs" /> and <see cref="SyncTimeMs" />, in milliseconds, are null unless
/// <c>track_wal_io_timing</c> is on - the one setting that times WAL I/O on every release, 18 and later included, where
/// <c>track_io_timing</c> times relations' I/O and no WAL row (measured on 18.6 and 19rc1); while it is off the server counts
/// a zero, which is no time, and the section says null. On 18 and later <see cref="StatsReset" /> is the later of
/// <c>pg_stat_wal</c>'s reset and <c>pg_stat_io</c>'s, which is reset on its own, so a counter is comparable between two
/// snapshots exactly while it is the same.
/// </para>
/// <para>
/// Not Activity-grade, and so never withheld: nothing in it is about another program's work. It is not read on a managed
/// platform whose storage layer writes no WAL of the host's (Amazon Aurora), where <see cref="Outcome" /> is
/// <see cref="PostgresReadOutcome.NotApplicable" /> and <see cref="Reason" /> says why.
/// </para>
/// </remarks>
public sealed record PostgresWalSection
{
    /// <summary>Whether it was read; when not, the other properties keep their defaults.</summary>
    public PostgresReadOutcome Outcome { get; init; }

    /// <summary>Why not.</summary>
    public string? Reason { get; init; }

    /// <summary>When it was read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>WAL records written (<c>wal_records</c>).</summary>
    public long? Records { get; init; }

    /// <summary>
    /// Full-page images among them (<c>wal_fpi</c>): the first change to a page after a checkpoint writes the whole page, which
    /// is most of a busy server's WAL volume.
    /// </summary>
    public long? FullPageImages { get; init; }

    /// <summary>Bytes of WAL written (<c>wal_bytes</c>).</summary>
    public long? Bytes { get; init; }

    /// <summary>
    /// WAL writes the server has made since the reset: <c>pg_stat_wal.wal_write</c> on PostgreSQL 14 to 17, <c>pg_stat_io</c>'s
    /// WAL rows from 18. Null where the section was not read.
    /// </summary>
    public long? Writes { get; init; }

    /// <summary>
    /// WAL syncs (<c>fsync</c>s of the log) since the reset: <c>pg_stat_wal.wal_sync</c> on PostgreSQL 14 to 17,
    /// <c>pg_stat_io</c>'s WAL <c>fsyncs</c> from 18. Null where the section was not read.
    /// </summary>
    public long? Syncs { get; init; }

    /// <summary>
    /// Milliseconds spent in those writes. Null unless <c>track_wal_io_timing</c> is on, the one setting that times WAL I/O on
    /// every release (<c>track_io_timing</c> does not, on 18 either).
    /// </summary>
    public double? WriteTimeMs { get; init; }

    /// <summary>
    /// Milliseconds spent in those syncs. Null unless <c>track_wal_io_timing</c> is on, the one setting that times WAL I/O on
    /// every release (<c>track_io_timing</c> does not, on 18 either).
    /// </summary>
    public double? SyncTimeMs { get; init; }

    /// <summary>
    /// When the counters last began from nothing (<c>stats_reset</c>): a server's start that kept no statistics, or a reset.
    /// From 18, where the writes and syncs come from <c>pg_stat_io</c>, the later of its reset and <c>pg_stat_wal</c>'s.
    /// </summary>
    public DateTimeOffset? StatsReset { get; init; }
}
