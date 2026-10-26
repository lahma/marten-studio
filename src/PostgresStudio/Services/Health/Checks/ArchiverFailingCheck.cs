using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What R02 reads.</summary>
/// <param name="Archiver">The archiver's counters, the settings it is judged with and the WAL file being written.</param>
/// <param name="Directory"><c>pg_wal</c>'s <c>.ready</c> count and size, or <see langword="null" /> without <c>pg_monitor</c>.</param>
/// <param name="Grants">What the reading role holds, for the "not checked" sentence of the directory evidence.</param>
/// <param name="Server">The server's version and platform.</param>
internal sealed record ArchiverFailingFacts(ArchiverFacts Archiver, WalDirectoryFacts? Directory, PostgresGrants Grants, PostgresServerFacts Server);

/// <summary>
/// R02: WAL archiving is failing, or has fallen behind (issue #8 G8 and the health-gap analysis's G02).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> With <c>archive_mode</c> on, Postgres keeps every WAL file until the archive command has copied it,
/// so an archiver that fails - or hangs, or is slower than the writes - fills <c>pg_wal</c> until the disk is full and the
/// server stops; and every point-in-time restore from the archive ends at the last file it did copy.
/// </para>
/// <para>
/// <b>Two variants.</b> <c>failing</c>, Critical (G8): <c>failed_count</c> rose within the advisor's window, the last
/// failure is later than the last success, or there are failures and no success at all. <c>backlog</c>: a hung or slow
/// command, or <c>archive_mode = on</c> with an empty command - which, per the manual, "continues to accumulate WAL segment
/// files" - never raises <c>failed_count</c>, so the distance from the last archived segment to the one being written
/// (<see cref="ArchiveBacklog" />) is judged: <see cref="BacklogSegments" /> segments and <see cref="BacklogWarningBytes" />,
/// grown across <see cref="BacklogWindow" /> of the advisor's samples, is a Warning, and Critical at
/// <see cref="BacklogCriticalBytes" />, or with no successful archive for <see cref="StalledFor" /> while it grows. A
/// <c>failing</c> archiver has a backlog too, and is said once, as <c>failing</c>. A standby is not judged for a backlog:
/// <c>pg_walfile_name</c> cannot run in recovery.
/// </para>
/// <para>
/// <b>Nothing archived yet</b> - the last archived file is null, or a <c>.history</c>, <c>.partial</c> or <c>.backup</c>
/// file - leaves no segment to measure from, so the backlog is the segments the advisor has seen written in its window: a
/// lower bound, and never sized by the whole WAL sequence the current file's number counts.
/// </para>
/// <para>
/// <b>Not applicable</b> (never run, listed) on Aurora, whose storage layer does the archiving, and on Amazon RDS, Azure
/// and Cloud SQL, which archive WAL themselves for their own backups (<see cref="ReplicationPlatform" />). No remedy line:
/// the command is the host's own, and what it said is in the server log.
/// </para>
/// <para>
/// <b>Gate.</b> <see cref="HealthGate.Database" />: counters, file names and settings are the server's. <c>archive_command</c>'s
/// text is evidence behind <see cref="HealthGate.Activity" />, because a command line may carry anything - a host, a bucket,
/// a key - and Postgres prints it only to a role that may read it.
/// </para>
/// </remarks>
[HealthCheck("R02", HealthArea.Replication, HealthCostTier.Catalog, "WAL archiving failing", Variants = ["failing", "backlog"])]
internal sealed class ArchiverFailingCheck : HealthCheck<ArchiverFailingFacts>
{
    /// <summary>The fewest segments behind that is a backlog.</summary>
    public const long BacklogSegments = 64;

    /// <summary>The fewest bytes behind that is a backlog: 1 GiB.</summary>
    public const long BacklogWarningBytes = 1L << 30;

    /// <summary>A backlog of this much is Critical: 10 GiB.</summary>
    public const long BacklogCriticalBytes = 10L << 30;

    /// <summary>How long a backlog must have grown across the advisor's samples to be one.</summary>
    public static readonly TimeSpan BacklogWindow = TimeSpan.FromMinutes(10);

    /// <summary>No successful archive for this long while the backlog grows is Critical.</summary>
    public static readonly TimeSpan StalledFor = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public override async Task<ArchiverFailingFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (ReplicationPlatform.ArchiverNotApplicable(server.Platform) is { } why)
        {
            throw new HealthCheckNotApplicableException(why);
        }

        ArchiverFacts archiver = await ArchiverFacts.SharedAsync(context).ConfigureAwait(false);
        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        WalDirectoryFacts? directory = archiver.Archives ? await WalDirectoryFacts.SharedAsync(context).ConfigureAwait(false) : null;

        return new ArchiverFailingFacts(archiver, directory, grants, server);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(ArchiverFailingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.Archiver.Archives)
        {
            yield break;
        }

        yield return new HealthSamplePart(ReplicationHistory.ArchiverFailed, facts.Archiver.FailedCount);
        yield return new HealthSamplePart(ReplicationHistory.ArchiverArchived, facts.Archiver.ArchivedCount);

        if (facts.Archiver.Backlog is { } backlog)
        {
            yield return new HealthSamplePart(ReplicationHistory.ArchiverBacklog, backlog.Segments);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ArchiverFailingFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ArchiverFacts archiver = facts.Archiver;

        if (!archiver.Archives)
        {
            return HealthVerdict.Quiet(archiver.Settings.ArchiveMode switch
            {
                "on" => "archive_mode is on, but this server is in recovery, where only archive_mode = always archives.",
                null => "archive_mode was not readable, so nothing is judged.",
                _ => "archive_mode is " + archiver.Settings.ArchiveMode + ": this server archives no WAL, so there is nothing to fail.",
            });
        }

        CounterWindow? failures = ReplicationHistory.Window(evaluation.History, [(ReplicationHistory.ArchiverFailed, archiver.FailedCount)], evaluation.Now);
        long rose = failures is null ? 0 : (long) failures.Gains[0];

        if (rose > 0 || archiver.LastAttemptFailed)
        {
            return HealthVerdict.Of(Failing(facts, failures, rose, evaluation));
        }

        if (archiver.Backlog is not { } backlog)
        {
            return HealthVerdict.Quiet(Healthy(archiver, evaluation)
                + (archiver.InRecovery ? " A standby is not judged for a backlog: pg_walfile_name cannot run in recovery." : string.Empty));
        }

        (long segments, bool grew) = Measure(backlog, evaluation, BacklogWindow);

        if (segments >= BacklogSegments && segments * backlog.SegmentBytes >= BacklogWarningBytes && grew)
        {
            return HealthVerdict.Of(Backlog(facts, backlog, segments, evaluation));
        }

        string watching = segments >= BacklogSegments && segments * backlog.SegmentBytes >= BacklogWarningBytes
            ? " It is " + SessionText.Count(segments, "segment") + " behind; the advisor raises it once that has grown across "
              + SessionText.Duration(BacklogWindow) + " of its samples."
            : string.Empty;

        return HealthVerdict.Quiet(Healthy(archiver, evaluation) + " " + BacklogText(backlog, segments) + "." + watching);
    }

    /// <summary>
    /// The backlog the checks judge, and whether it grew across <paramref name="span" /> of samples: the distance itself, or -
    /// with nothing archived yet - the segments written since the window began, a lower bound.
    /// </summary>
    internal static (long Segments, bool Grew) Measure(ArchiveBacklog backlog, HealthEvaluation evaluation, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(evaluation);

        bool grew = ReplicationHistory.GrewOver(evaluation.History, ReplicationHistory.ArchiverBacklog, backlog.Segments, evaluation.Now, span);

        if (!backlog.NothingArchived)
        {
            return (backlog.Segments, grew);
        }

        CounterWindow? window = ReplicationHistory.Window(evaluation.History, [(ReplicationHistory.ArchiverBacklog, backlog.Segments)], evaluation.Now);
        return (window is null ? 0 : (long) window.Gains[0], grew);
    }

    private static HealthFinding Failing(ArchiverFailingFacts facts, CounterWindow? failures, long rose, HealthEvaluation evaluation)
    {
        ArchiverFacts archiver = facts.Archiver;
        bool recovered = !archiver.LastAttemptFailed;

        List<HealthEvidence> evidence =
        [
            new("Failed attempts", SessionText.Number(archiver.FailedCount) + " since the archiver's statistics began"
                + (failures is not null && rose > 0 ? ", " + SessionText.Number(rose) + " of them in the last " + SessionText.Duration(failures.Span) : string.Empty)),
            new("Last failed WAL file", archiver.LastFailedWal ?? "not recorded"),
            new("Last failure", When(archiver.LastFailedAt, archiver.LastFailedAgeAtRead, archiver.ReadAt, evaluation)),
            new("Last successful archive", archiver.LastArchivedAt is null
                ? "none since the archiver's statistics began"
                : (archiver.LastArchivedWal ?? "a file") + ", " + When(archiver.LastArchivedAt, archiver.LastArchivedAgeAtRead, archiver.ReadAt, evaluation)),
        ];

        if (archiver.Backlog is { } backlog)
        {
            evidence.Add(new HealthEvidence("Archiver behind", BacklogText(backlog, Measure(backlog, evaluation, BacklogWindow).Segments)));
        }

        evidence.AddRange(Settings(facts, evaluation.Text));

        return new HealthFinding
        {
            Code = "R02",
            Variant = "failing",
            Severity = HealthSeverity.Critical,
            Title = recovered
                ? "WAL archiving failed " + SessionText.Count(rose, "time") + " in the last " + SessionText.Duration(failures!.Span)
                : "WAL archiving is failing",
            WhyItMatters =
                "The archive command is failing; WAL accumulates in pg_wal until it succeeds. With archive_mode on, Postgres keeps "
                + "every WAL file until the command has copied it, so a command that keeps failing fills the WAL volume until the "
                + "server stops accepting writes, and every point-in-time restore from the archive ends at the last file it copied. "
                + "Postgres retries a failed file three times, then waits a minute and tries again, logging each failure with what "
                + "the command said - the server log is where to look first.",
            Evidence = evidence,
            Caveat = Caveat(archiver)
                + (recovered ? " The last attempt succeeded, after the failures counted here: the archiver may have recovered." : string.Empty),
        };
    }

    private static HealthFinding Backlog(ArchiverFailingFacts facts, ArchiveBacklog backlog, long segments, HealthEvaluation evaluation)
    {
        ArchiverFacts archiver = facts.Archiver;
        long bytes = segments * backlog.SegmentBytes;
        TimeSpan? sinceSuccess = archiver.LastArchivedAgeAtRead is { } age ? SnapshotAge.At(age, archiver.ReadAt, evaluation) : null;
        bool stalled = backlog.NothingArchived
            ? ReplicationHistory.GrewOver(evaluation.History, ReplicationHistory.ArchiverBacklog, backlog.Segments, evaluation.Now, StalledFor)
            : sinceSuccess >= StalledFor;
        bool critical = (!backlog.NothingArchived && bytes >= BacklogCriticalBytes) || stalled;

        List<HealthEvidence> evidence =
        [
            new("Archiver behind", BacklogText(backlog, segments)),
            new("WAL file being written", archiver.CurrentWal ?? "not readable"),
            new("Last archived WAL file", archiver.LastArchivedWal ?? "none since the archiver's statistics began"),
            new("Last successful archive", When(archiver.LastArchivedAt, archiver.LastArchivedAgeAtRead, archiver.ReadAt, evaluation)),
            new("Archived files", SessionText.Number(archiver.ArchivedCount) + " since the archiver's statistics began"),
            new("Failed attempts", SessionText.Number(archiver.FailedCount) + " - a hung or slow command, or an empty one, never counts as one"),
        ];

        evidence.AddRange(Settings(facts, evaluation.Text));

        return new HealthFinding
        {
            Code = "R02",
            Variant = "backlog",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = backlog.NothingArchived
                ? "Nothing has been archived while " + WalText.Bytes(bytes) + " of WAL was written"
                : "WAL archiving is " + WalText.Bytes(bytes) + " behind, and falling further behind",
            WhyItMatters =
                "The archiver is behind the WAL the server writes, and the distance has grown across "
                + SessionText.Duration(BacklogWindow) + " of the advisor's samples. A hung or slow archive command - an upload that "
                + "stalls - and archive_mode = on with an empty archive_command, which keeps every WAL segment, never count as "
                + "failures, so nothing else says so: pg_wal holds every file not yet archived, and grows until the WAL volume is "
                + "full and the server stops accepting writes. A restore from the archive ends at the last file it copied.",
            Evidence = evidence,
            Caveat = Caveat(archiver)
                + (backlog.NothingArchived
                    ? " Nothing has been archived since the archiver's statistics began, so the distance is the WAL written while the advisor watched - a lower bound."
                    : " The distance is in whole segments, from the last segment archived to the one being written."),
        };
    }

    /// <summary>The settings and the directory, for every R02 finding.</summary>
    private static IEnumerable<HealthEvidence> Settings(ArchiverFailingFacts facts, StudioText text)
    {
        ReplicationSettings settings = facts.Archiver.Settings;

        yield return new HealthEvidence("archive_mode", settings.ArchiveMode ?? "not readable");

        if (settings.ArchiveLibrary is { } library)
        {
            yield return new HealthEvidence("archive_library", library + " (archive_command is not used while it is set)");
        }
        else
        {
            yield return new HealthEvidence("archive_command", settings.ArchiveCommand switch
            {
                ArchiveCommandState.Set => "set - its text is shown with " + text.OptionName("Capabilities.ViewDatabaseActivity"),
                ArchiveCommandState.Empty => "empty: Postgres keeps every WAL segment and archives none",
                ArchiveCommandState.Disabled => "disabled",
                _ => "not shown to role " + facts.Grants.ReadsAs,
            });

            if (settings.ArchiveCommandText is { } command)
            {
                yield return new HealthEvidence("archive_command's text", command, HealthGate.Activity);
            }
        }

        yield return facts.Directory is { } directory
            ? new HealthEvidence(
                "Waiting in pg_wal",
                SessionText.Count(directory.ReadyFiles, "file") + " ready for the archiver; pg_wal holds "
                + SessionText.Count(directory.Files, "file") + ", " + WalText.Bytes(directory.Bytes))
            : new HealthEvidence(
                "Waiting in pg_wal",
                PostgresGrants.NotChecked(PostgresGrant.Monitor, facts.Server.Platform, facts.Grants.ReadsAs));

        yield return new HealthEvidence("Platform", facts.Server.Platform.Name);
    }

    private static string Healthy(ArchiverFacts archiver, HealthEvaluation evaluation) =>
        "archive_mode is " + archiver.Settings.ArchiveMode + "; "
        + (archiver.LastArchivedAt is null
            ? "nothing has been archived since the archiver's statistics began"
            : "the archiver last archived " + (archiver.LastArchivedWal ?? "a file") + " " + When(archiver.LastArchivedAt, archiver.LastArchivedAgeAtRead, archiver.ReadAt, evaluation))
        + ", and has counted " + SessionText.Count(archiver.FailedCount, "failure") + ", none of them new.";

    private static string BacklogText(ArchiveBacklog backlog, long segments) =>
        backlog.NothingArchived
            ? "nothing archived yet; " + SessionText.Count(segments, "segment") + " (" + WalText.Bytes(segments * backlog.SegmentBytes) + ") written while the advisor watched"
            : SessionText.Count(segments, "segment") + " (" + WalText.Bytes(segments * backlog.SegmentBytes) + ") from the last archived segment to the one being written";

    /// <summary>"2026-10-26 09:00:00Z, 4 min ago", aged to the evaluation's time; "never" with no time.</summary>
    private static string When(DateTimeOffset? at, TimeSpan? ageAtRead, DateTimeOffset readAt, HealthEvaluation evaluation) =>
        at is not { } time
            ? "never"
            : time.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
              + (ageAtRead is { } age ? ", " + SessionText.Duration(SnapshotAge.At(age, readAt, evaluation)) + " ago" : string.Empty);

    private static string Caveat(ArchiverFacts archiver) =>
        "Read in pg_stat_archiver at " + archiver.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
        + (archiver.StatsReset is { } reset ? "; its counters count since " + reset.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : string.Empty)
        + ". The advisor's window is its own samples, which exist only while somebody watches the Health page or the Overview.";
}
