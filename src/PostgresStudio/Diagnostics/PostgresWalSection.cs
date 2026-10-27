namespace PostgresStudio;

/// <summary>
/// What the server has written to its write-ahead log since the statistics were last reset (<c>pg_stat_wal</c>): numbers
/// about the server as a whole, the measure a point-in-time-recovery window is sized by.
/// </summary>
/// <remarks>
/// <para>
/// The counters are cumulative and the server's, not this database's, so a baseline reads two of them and the time between:
/// <c>Bytes</c> over the span between two snapshots' <see cref="MeasuredAt" /> is a rate, valid while <see cref="StatsReset" />
/// is the same in both. PostgreSQL 14 added the view; the studio's floor has it. PostgreSQL 14 to 17 count WAL writes and
/// syncs in <c>pg_stat_wal</c> too, and 18 moved them to <c>pg_stat_io</c>, so this section carries none of them, to read the
/// same on every release.
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

    /// <summary>When the counters last began from nothing (<c>stats_reset</c>): a server's start that kept no statistics, or a reset.</summary>
    public DateTimeOffset? StatsReset { get; init; }
}
