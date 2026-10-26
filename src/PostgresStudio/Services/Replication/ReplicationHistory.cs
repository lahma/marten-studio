using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Replication;

/// <summary>
/// The advisor's series R02 and R04 write - cumulative counters, sampled once per regular sweep - and the window arithmetic
/// the checks and the Replication page both read them with.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counters, not rates.</b> Each series is a counter as Postgres keeps it, so a rate or a share is a difference between
/// two samples: what happened in the window, not since a reset nobody remembers. A counter that went down was reset
/// (<c>pg_stat_reset_shared</c>, or a crash - B0 G4), and the window starts again at the sample after it, never across it.
/// </para>
/// <para>
/// <b>Written at <see cref="HealthGate.Database" /></b>, R02's and R04's gate, so the page reads them at that gate and no
/// lower (<see cref="HealthHistoryView.VisibleAt" />). The names are internal: a series is never shown, exported or logged.
/// </para>
/// </remarks>
internal static class ReplicationHistory
{
    /// <summary><c>pg_stat_archiver.failed_count</c> (R02).</summary>
    public const string ArchiverFailed = "replication.archiver-failed";

    /// <summary><c>pg_stat_archiver.archived_count</c> (R02).</summary>
    public const string ArchiverArchived = "replication.archiver-archived";

    /// <summary>
    /// R02's backlog in segments: the distance from the last archived segment to the one being written - written only when
    /// the last archived file names a segment, never a stand-in (W5-R2-fix B2).
    /// </summary>
    public const string ArchiverBacklog = "replication.archiver-backlog";

    /// <summary>The number of the segment being written (R02): what "nothing archived yet" is measured by.</summary>
    public const string WalSegment = "replication.wal-segment";

    /// <summary>Timed checkpoints (R04).</summary>
    public const string CheckpointsTimed = "replication.checkpoints-timed";

    /// <summary>Requested checkpoints (R04).</summary>
    public const string CheckpointsRequested = "replication.checkpoints-requested";

    /// <summary>Checkpoints done (R04, PostgreSQL 18+).</summary>
    public const string CheckpointsDone = "replication.checkpoints-done";

    /// <summary><c>pg_stat_wal.wal_records</c> (R04, for the page's full-page-image share).</summary>
    public const string WalRecords = "replication.wal-records";

    /// <summary><c>pg_stat_wal.wal_fpi</c> (R04, for the page).</summary>
    public const string WalFullPageImages = "replication.wal-fpi";

    /// <summary><c>pg_stat_wal.wal_bytes</c> (R04, for the page's WAL rate).</summary>
    public const string WalBytes = "replication.wal-bytes";

    /// <summary>
    /// What several counters gained over the window: from the oldest sample that has every one of them, after the last
    /// sample at which any of them went down, to <paramref name="current" /> - each counter's value now.
    /// </summary>
    /// <param name="history">The history, as the reader's gate may read it.</param>
    /// <param name="current">Each counter's series and its value now, read with the facts.</param>
    /// <param name="now">The evaluation's time: a sample after it is not read.</param>
    /// <returns>
    /// The gain of each counter, in the order asked, and when the window began - or <see langword="null" /> when no
    /// earlier sample has them all, or one of them went down since the last sample (a reset just now): no window yet.
    /// </returns>
    public static CounterWindow? Window(HealthHistoryView history, IReadOnlyList<(string Series, double Current)> current, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(current);

        double[]? baseline = null;
        double[]? previous = null;
        DateTimeOffset since = default;

        foreach (HealthSample sample in history.Samples)
        {
            if (sample.At > now)
            {
                continue;
            }

            double[] values = new double[current.Count];
            bool complete = true;

            for (int i = 0; i < current.Count; i++)
            {
                if (!sample.Values.TryGetValue(current[i].Series, out values[i]))
                {
                    complete = false;
                    break;
                }
            }

            if (!complete)
            {
                continue;
            }

            if (baseline is null || WentDown(values, previous!))
            {
                baseline = values;
                since = sample.At;
            }

            previous = values;
        }

        if (baseline is null)
        {
            return null;
        }

        double[] now2 = [.. current.Select(static x => x.Current)];

        if (WentDown(now2, previous!) || since >= now)
        {
            return null;
        }

        return new CounterWindow([.. now2.Select((value, i) => value - baseline[i])], since, now - since);
    }

    /// <summary>
    /// Whether <paramref name="series" /> has grown across at least <paramref name="span" /> of samples: its value now is
    /// above the latest sample at least <paramref name="span" /> old, and no sample since then fell below that one.
    /// </summary>
    public static bool GrewOver(HealthHistoryView history, string series, double current, DateTimeOffset now, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(history);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = history.Trend(series).Points;
        int start = -1;

        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].At <= now - span)
            {
                start = i;
            }
        }

        if (start < 0 || current <= points[start].Value)
        {
            return false;
        }

        for (int i = start + 1; i < points.Count && points[i].At <= now; i++)
        {
            if (points[i].Value < points[start].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static bool WentDown(double[] values, double[] previous)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] < previous[i])
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>What some counters gained over a window of the advisor's samples.</summary>
/// <param name="Gains">Each counter's gain, in the order asked.</param>
/// <param name="Since">When the window began: the sample the gains are measured from.</param>
/// <param name="Span">How long the window is.</param>
internal sealed record CounterWindow(IReadOnlyList<double> Gains, DateTimeOffset Since, TimeSpan Span);
