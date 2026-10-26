using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What R04 reads.</summary>
/// <param name="Server">The server's version and platform.</param>
/// <param name="Checkpoints">The checkpoint counters, as this release keeps them.</param>
/// <param name="Wal"><c>pg_stat_wal</c>, sampled for the Replication page's WAL rate; <see langword="null" /> below 14.</param>
/// <param name="Settings"><c>checkpoint_timeout</c> and <c>max_wal_size</c>, and the latter's context.</param>
internal sealed record RequestedCheckpointsFacts(
    PostgresServerFacts Server,
    CheckpointStatistics Checkpoints,
    WalStatistics? Wal,
    ReplicationSettings Settings);

/// <summary>
/// R04: too many checkpoints are requested rather than timed - WAL reaches <c>max_wal_size</c> before
/// <c>checkpoint_timeout</c> does (issue #8 G8: a requested share of 0.2 or more over 10 checkpoints or more).
/// </summary>
/// <remarks>
/// <para>
/// <b>Over the advisor's window</b>, never since a reset nobody remembers: the counters are sampled each sweep, and the
/// window is the gain from the oldest sample after the last reset (<see cref="ReplicationHistory.Window" />). Once CAP's
/// <c>StatisticsWindow</c> lands, its <c>Since</c> is the other window to judge over; until then, the window is the samples.
/// </para>
/// <para>
/// <b>Two triggers, either fires.</b> The share (G8): requested over the denominator this release counts
/// (<see cref="CheckpointCounting" />) - up to 17 timed plus requested, though the timed count includes the checkpoints an
/// idle server skipped (<c>checkpoints_timed</c> up to 16, <c>num_timed</c> on 17), so idleness dilutes it, and 18
/// <c>num_done</c> - at least
/// <see cref="ShareThreshold" /> over at least <see cref="MinimumCheckpoints" />. And, on every release (the health-gap
/// analysis, addition 5): at least <see cref="MinimumCheckpoints" /> requested in the window, and more than the window
/// over <c>checkpoint_timeout</c> - more requested checkpoints than timed ones could have run - which no skipped
/// checkpoint dilutes.
/// </para>
/// <para>
/// <b>Warning</b>, with <c>max_wal_size</c> doubled as the remedy, worded for the platform (<see cref="ServerSettingRemedy" />).
/// Not applicable on Aurora, whose storage layer checkpoints. It also samples <c>pg_stat_wal</c>, for the Replication page's
/// WAL rate and full-page-image share - every counter of that view the page reads over a window, written by this one check.
/// </para>
/// </remarks>
[HealthCheck("R04", HealthArea.Replication, HealthCostTier.Catalog, "Requested checkpoints")]
internal sealed class RequestedCheckpointsCheck : HealthCheck<RequestedCheckpointsFacts>
{
    /// <summary>The requested share that is too much.</summary>
    public const double ShareThreshold = 0.2;

    /// <summary>The fewest checkpoints - or requested ones, for the second trigger - in the window that are judged.</summary>
    public const int MinimumCheckpoints = 10;

    /// <inheritdoc />
    public override async Task<RequestedCheckpointsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (ReplicationPlatform.CheckpointsNotApplicable(server.Platform) is { } why)
        {
            throw new HealthCheckNotApplicableException(why);
        }

        CheckpointStatistics checkpoints = await CheckpointStatistics.SharedAsync(context).ConfigureAwait(false);
        WalStatistics? wal = await WalStatistics.SharedAsync(context).ConfigureAwait(false);
        ReplicationSettings settings = await ReplicationSettings.SharedAsync(context).ConfigureAwait(false);

        return new RequestedCheckpointsFacts(server, checkpoints, wal, settings);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(RequestedCheckpointsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        yield return new HealthSamplePart(ReplicationHistory.CheckpointsTimed, facts.Checkpoints.Timed);
        yield return new HealthSamplePart(ReplicationHistory.CheckpointsRequested, facts.Checkpoints.Requested);

        if (facts.Checkpoints.Done is { } done)
        {
            yield return new HealthSamplePart(ReplicationHistory.CheckpointsDone, done);
        }

        if (facts.Wal is { } wal)
        {
            yield return new HealthSamplePart(ReplicationHistory.WalRecords, wal.Records);
            yield return new HealthSamplePart(ReplicationHistory.WalFullPageImages, wal.FullPageImages);
            yield return new HealthSamplePart(ReplicationHistory.WalBytes, wal.Bytes);
        }
    }

    /// <summary>The counters R04 judges, with their values now: timed, requested, and 18's done.</summary>
    internal static IReadOnlyList<(string Series, double Current)> Counters(CheckpointStatistics checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);

        return checkpoints.Done is { } done
            ? [(ReplicationHistory.CheckpointsTimed, checkpoints.Timed), (ReplicationHistory.CheckpointsRequested, checkpoints.Requested), (ReplicationHistory.CheckpointsDone, done)]
            : [(ReplicationHistory.CheckpointsTimed, checkpoints.Timed), (ReplicationHistory.CheckpointsRequested, checkpoints.Requested)];
    }

    /// <summary>What the window says: the requested checkpoints, the denominator and the share - or why it says nothing yet.</summary>
    /// <param name="Requested">Requested checkpoints in the window.</param>
    /// <param name="Denominator">The checkpoints the share is of, as this release counts them.</param>
    /// <param name="Share">The requested share, or <see langword="null" /> with no checkpoint in the window.</param>
    /// <param name="Span">How long the window is.</param>
    /// <param name="ByShare">Whether the share trigger fires.</param>
    /// <param name="ByTimeout">Whether the "more than checkpoint_timeout allows" trigger fires.</param>
    internal sealed record Judgement(double Requested, double Denominator, double? Share, TimeSpan Span, bool ByShare, bool ByTimeout)
    {
        /// <summary>Whether either trigger fires.</summary>
        public bool Fires => ByShare || ByTimeout;
    }

    /// <summary>Judges one window of counters - pure, for the unit tests at every edge.</summary>
    /// <param name="counting">How this release counts checkpoints.</param>
    /// <param name="timed">Timed checkpoints in the window.</param>
    /// <param name="requested">Requested checkpoints in the window.</param>
    /// <param name="done">Checkpoints done in the window (18), or <see langword="null" />.</param>
    /// <param name="span">How long the window is.</param>
    /// <param name="checkpointTimeout"><c>checkpoint_timeout</c>, or <see langword="null" /> when not read.</param>
    public static Judgement Judge(CheckpointCounting counting, double timed, double requested, double? done, TimeSpan span, TimeSpan? checkpointTimeout)
    {
        double denominator = CheckpointStatistics.Denominator(counting, timed, requested, done);
        double? share = denominator > 0 ? Math.Min(1d, requested / denominator) : null;

        bool byShare = denominator >= MinimumCheckpoints && share >= ShareThreshold;
        bool byTimeout = requested >= MinimumCheckpoints
            && checkpointTimeout is { TotalSeconds: > 0 } timeout
            && requested > span.TotalSeconds / timeout.TotalSeconds;

        return new Judgement(requested, denominator, share, span, byShare, byTimeout);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(RequestedCheckpointsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        CheckpointStatistics checkpoints = facts.Checkpoints;
        CounterWindow? window = ReplicationHistory.Window(evaluation.History, Counters(checkpoints), evaluation.Now);

        if (window is null)
        {
            return HealthVerdict.Quiet(
                "Watching: the requested share is judged over the advisor's own samples, and there is no earlier sample to measure "
                + "from yet. Since its counters were reset, " + Totals(checkpoints) + ".");
        }

        Judgement judgement = Judge(
            checkpoints.Counting,
            window.Gains[0],
            window.Gains[1],
            window.Gains.Count > 2 ? window.Gains[2] : null,
            window.Span,
            facts.Settings.CheckpointTimeout);

        if (!judgement.Fires)
        {
            return HealthVerdict.Quiet(
                "In the last " + SessionText.Duration(window.Span) + ", " + SessionText.Count((long) judgement.Requested, "requested checkpoint")
                + " of " + SessionText.Number((long) judgement.Denominator)
                + (judgement.Denominator < MinimumCheckpoints
                    ? " - fewer than " + MinimumCheckpoints.ToString(CultureInfo.InvariantCulture) + " to judge a share by."
                    : " (" + SessionText.Percent(judgement.Share ?? 0) + ")."));
        }

        return HealthVerdict.Of(Finding(facts, window, judgement, evaluation));
    }

    private static HealthFinding Finding(RequestedCheckpointsFacts facts, CounterWindow window, Judgement judgement, HealthEvaluation evaluation)
    {
        CheckpointStatistics checkpoints = facts.Checkpoints;
        ReplicationSettings settings = facts.Settings;

        List<HealthEvidence> evidence =
        [
            new("Window", "the last " + SessionText.Duration(window.Span) + " of the advisor's samples"),
            new("Requested checkpoints", SessionText.Number((long) judgement.Requested)),
            new(Denominator(checkpoints.Counting), SessionText.Number((long) judgement.Denominator)),
            new("Requested share", judgement.Share is { } share ? SessionText.Percent(share) : "no checkpoint ran"),
            new("checkpoint_timeout", settings.CheckpointTimeout is { } timeout
                ? SessionText.Duration(timeout) + ": at most " + SessionText.Number((long) Math.Floor(window.Span.TotalSeconds / timeout.TotalSeconds))
                  + " timed checkpoints fit in the window"
                : "not readable"),
            new("max_wal_size", settings.MaxWalSizeBytes is { } max ? WalText.Bytes(max) : "not readable"),
            new("Counted in", checkpoints.Source),
            new("Since the counters were reset", Totals(checkpoints)),
            new("Platform", facts.Server.Platform.Name),
        ];

        if (WalInWindow(evaluation, facts.Wal, window) is { } written)
        {
            evidence.Insert(6, new HealthEvidence("WAL written in the window", WalText.Bytes((long) written)));
        }

        string? doubled = settings.MaxWalSizeBytes is > 0 ? Size(settings.MaxWalSizeBytes.Value * 2) : null;

        return new HealthFinding
        {
            Code = "R04",
            Severity = HealthSeverity.Warning,
            Title = SessionText.Count((long) judgement.Requested, "checkpoint") + " in " + SessionText.Duration(window.Span)
                + " were requested rather than timed" + (judgement.Share is { } s && judgement.ByShare ? " (" + SessionText.Percent(s) + ")" : string.Empty),
            WhyItMatters =
                "A checkpoint is requested before checkpoint_timeout comes round when WAL reaches max_wal_size, so checkpoints come "
                + "closer together than configured. Each one writes every dirty buffer, and the first change to each page after it "
                + "writes the whole page to WAL again (a full-page image) - so frequent checkpoints raise both write I/O and WAL "
                + "volume, which every standby and the archive then carry too. A server whose checkpoints are almost all timed has "
                + "max_wal_size room for its peak writes.",
            Evidence = evidence,
            Caveat = Caveat(checkpoints),
            Remedy = doubled is null
                ? null
                : ServerSettingRemedy.For(
                    facts.Server.Platform,
                    "max_wal_size",
                    doubled,
                    "Double it (from " + Size(settings.MaxWalSizeBytes!.Value) + ") so that WAL between checkpoints has room before "
                    + "checkpoint_timeout comes round; if the share stays high, double it again. It costs disk - pg_wal may grow to "
                    + "about this size between checkpoints - and a longer crash recovery, which replays the WAL since the last "
                    + "checkpoint.",
                    evaluation.Text,
                    settings["max_wal_size"]?.Context),
        };
    }

    /// <summary>The denominator's label on this release.</summary>
    private static string Denominator(CheckpointCounting counting) => counting switch
    {
        CheckpointCounting.CheckpointerDone => "Checkpoints done (num_done)",
        CheckpointCounting.CheckpointerWithSkipped => "Checkpoints counted (num_timed + num_requested, skipped ones included)",
        _ => "Checkpoints (checkpoints_timed + checkpoints_req)",
    };

    /// <summary>The totals since the counters were reset, as one phrase.</summary>
    private static string Totals(CheckpointStatistics checkpoints) =>
        SessionText.Number(checkpoints.Timed) + " timed, " + SessionText.Number(checkpoints.Requested) + " requested"
        + (checkpoints.Done is { } done ? ", " + SessionText.Number(done) + " done" : string.Empty)
        + (checkpoints.StatsReset is { } reset ? " (since " + reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + ")" : string.Empty);

    /// <summary>WAL written over the same window, when the WAL counter was sampled across it.</summary>
    private static double? WalInWindow(HealthEvaluation evaluation, WalStatistics? wal, CounterWindow window)
    {
        if (wal is null)
        {
            return null;
        }

        CounterWindow? bytes = ReplicationHistory.Window(evaluation.History, [(ReplicationHistory.WalBytes, wal.Bytes)], evaluation.Now);
        return bytes is not null && bytes.Since == window.Since ? bytes.Gains[0] : null;
    }

    private static string Caveat(CheckpointStatistics checkpoints) =>
        "Counted from " + checkpoints.Source + " over the advisor's own samples, which exist only while somebody watches the Health "
        + "page or the Overview. A requested checkpoint is also one a CHECKPOINT command, a base backup or CREATE DATABASE asked "
        + "for, so a window with a backup in it counts those too."
        + checkpoints.Counting switch
        {
            CheckpointCounting.Bgwriter =>
                " Up to PostgreSQL 16, checkpoints_timed counts the checkpoints an idle server skipped too, so idle time dilutes the "
                + "share; the second trigger - more requested checkpoints than checkpoint_timeout would have run - does not depend on it.",
            CheckpointCounting.CheckpointerWithSkipped =>
                " On PostgreSQL 17, num_timed counts the checkpoints an idle server skipped too, so idle time dilutes the share; "
                + "the second trigger - more requested checkpoints than checkpoint_timeout would have run - does not depend on it.",
            CheckpointCounting.CheckpointerDone => " On PostgreSQL 18 the share is of num_done, the checkpoints that ran.",
            _ => string.Empty,
        };

    /// <summary>A size as a server setting's value: "2GB", or "1536MB" for a size that is not whole gigabytes.</summary>
    internal static string Size(long bytes)
    {
        long megabytes = Math.Max(1, bytes / (1024 * 1024));
        return megabytes % 1024 == 0
            ? (megabytes / 1024).ToString(CultureInfo.InvariantCulture) + "GB"
            : megabytes.ToString(CultureInfo.InvariantCulture) + "MB";
    }
}
