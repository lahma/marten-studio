using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What R03 reads.</summary>
/// <param name="Server">The server's version and platform.</param>
/// <param name="Role">Whether the server answering is a primary or a standby, and a standby's own replay.</param>
/// <param name="Standbys">The standbys streaming from a primary; <see langword="null" /> on a standby.</param>
/// <param name="Grants">What the reading role holds, for the "not checked" sentence.</param>
/// <param name="ApplyDelay">
/// A standby's own <c>recovery_min_apply_delay</c> - replay held back on purpose - or <see langword="null" /> on a primary
/// or when it was not readable (W5-R2-fix 6).
/// </param>
internal sealed record StandbyLagFacts(
    PostgresServerFacts Server,
    ReplicationRole Role,
    StandbySnapshot? Standbys,
    PostgresGrants Grants,
    TimeSpan? ApplyDelay = null);

/// <summary>
/// One replica's lag, as R03 judges it: how many bytes and how long it is behind. The evaluator's whole input, so another
/// source of readings - AUR's <c>aurora_replica_status()</c> - feeds the same thresholds.
/// </summary>
/// <param name="Replica">What it is, for gated evidence: a standby's <c>application_name</c>, "this server", an instance id.</param>
/// <param name="BehindBytes">Bytes behind, or <see langword="null" /> when not measured.</param>
/// <param name="Behind">Time behind, or <see langword="null" /> when not measured - or not judged (a standby whose replay holds nothing back).</param>
/// <param name="Detail">More about it for the same evidence line: client, state, the lag times; or <see langword="null" />.</param>
internal sealed record StandbyLagReading(string Replica, long? BehindBytes, TimeSpan? Behind, string? Detail = null);

/// <summary>
/// R03: a standby is behind (issue #8 G8: Warning at five minutes or 1 GiB, Critical at an hour).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two places to measure from</b> (B0 §4). <b>On a primary</b> - <c>primary-view</c> - <c>pg_stat_replication</c> has
/// each standby's replay position and <c>replay_lag</c>, but only for a role with <c>pg_read_all_stats</c> (or
/// <c>pg_monitor</c>): any other role sees a sender's process id, role and application name and nothing else, and the check
/// is "not checked", naming the grant. A physical slot's <c>restart_lsn</c> is no substitute - it tracks flush, and read 0
/// while replay was 13 MB behind. <b>On a standby</b> - <c>standby-view</c>, when the data source answered in recovery (a
/// multi-host data source that reaches one) - received less replayed WAL is exact and needs no grant, and the age of the last
/// replayed commit is the time; but an idle primary sends nothing, so that age grows with no lag at all, and an instant's
/// in-flight record is no lag either. So the time is judged only while the standby has held WAL it has not replayed on two
/// consecutive samples (<c>replication.standby-replay-behind</c>), or holds <see cref="StandbyTimeFloorBytes" /> or more
/// now (W5-R2-fix 7). A paused replay (<c>pg_wal_replay_pause()</c>, 14+) is <c>paused</c>, a Warning of its own: somebody
/// stopped it, and it falls further behind until somebody resumes it.
/// </para>
/// <para>
/// <b>A delayed standby</b> (<c>recovery_min_apply_delay</c>) holds back replay on purpose. On the standby the delay is read,
/// and only the time beyond it is judged - its bytes are the delay's by design, and are shown, not judged. From the primary
/// the delay cannot be seen (it is the standby's setting), so its <c>replay_lag</c> includes it, which the caveat says
/// (W5-R2-fix 6).
/// </para>
/// <para>
/// <b>Physical senders only</b> (W5-R2-fix B1): a logical sender - a subscription, a change data capture connector - is
/// not a standby, and one of another database is never named or judged; both are counted in the status row.
/// </para>
/// <para>
/// <b>One finding per variant</b>, about every standby it judges, with the worst lag as its numbers: a subject would be a
/// standby's <c>application_name</c>, and a subject is part of the mute key every visitor of the finding is handed. Names -
/// a standby's application name and client address - are evidence behind <see cref="HealthGate.Activity" />.
/// </para>
/// <para>
/// <b>Not applicable on Aurora</b>, whose readers do not stream: AUR reads their lag from <c>aurora_replica_status()</c> and
/// feeds <see cref="Finding" /> with readings of its own.
/// </para>
/// </remarks>
[HealthCheck("R03", HealthArea.Replication, HealthCostTier.Catalog, "Standby lag", Variants = ["primary-view", "standby-view", "paused"])]
internal sealed class StandbyLagCheck : HealthCheck<StandbyLagFacts>
{
    /// <summary>This many bytes behind is a Warning: 1 GiB.</summary>
    public const long WarningBytes = 1L << 30;

    /// <summary>This long behind is a Warning.</summary>
    public static readonly TimeSpan WarningTime = TimeSpan.FromMinutes(5);

    /// <summary>This long behind is Critical.</summary>
    public static readonly TimeSpan CriticalTime = TimeSpan.FromHours(1);

    /// <summary>How many standbys a finding names in its gated evidence; the count says how many there are.</summary>
    public const int ReplicasNamed = 5;

    /// <summary>What "this server" is called in a standby's own reading.</summary>
    public const string ThisServer = "this server";

    /// <summary>
    /// On a standby, holding this much WAL unreplayed - a segment's worth - is lag at once; below it, only on two consecutive
    /// samples: 16 MiB.
    /// </summary>
    public const long StandbyTimeFloorBytes = 16L << 20;

    /// <summary>A standby's own unreplayed WAL, sampled so the next sweep knows whether it held some before.</summary>
    public const string ReplayBehindSeries = "replication.standby-replay-behind";

    /// <inheritdoc />
    public override async Task<StandbyLagFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (ReplicationPlatform.StandbyLagNotApplicable(server.Platform) is { } why)
        {
            throw new HealthCheckNotApplicableException(why);
        }

        ReplicationRole role = await ReplicationRole.SharedAsync(context).ConfigureAwait(false);
        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        StandbySnapshot? standbys = role.InRecovery ? null : await StandbySnapshot.SharedAsync(context).ConfigureAwait(false);
        TimeSpan? delay = role.InRecovery ? (await ReplicationSettings.SharedAsync(context).ConfigureAwait(false)).ApplyDelay : null;

        if (standbys?.Physical is { Count: > 0 } physical && physical.All(static x => !x.Readable))
        {
            throw new HealthCheckUnavailableException(NotChecked(server, grants, physical.Count));
        }

        return new StandbyLagFacts(server, role, standbys, grants, delay);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(StandbyLagFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Role is { InRecovery: true, ReplayBehindBytes: { } bytes })
        {
            yield return new HealthSamplePart(ReplayBehindSeries, bytes);
        }
    }

    /// <summary>The "not checked" sentence for standbys the reading role may not read, with the standby-side alternative.</summary>
    internal static string NotChecked(PostgresServerFacts server, PostgresGrants grants, long standbys) =>
        PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, server.Platform, grants.ReadsAs)
        + " " + SessionText.Count(standbys, "standby") + (standbys == 1 ? " streams" : " stream") + " from this server, and without it "
        + "pg_stat_replication shows none of their positions or lag. A data source that reaches a standby itself - a multi-host "
        + "data source - is measured there instead, with no grant: what the standby has received less what it has replayed.";

    /// <inheritdoc />
    public override HealthVerdict Evaluate(StandbyLagFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        return facts.Role.InRecovery ? StandbyView(facts, evaluation) : PrimaryView(facts);
    }

    /// <summary>The severity of one reading, or <see langword="null" /> when it is not behind enough to say anything.</summary>
    public static HealthSeverity? Judge(StandbyLagReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.Behind >= CriticalTime)
        {
            return HealthSeverity.Critical;
        }

        return reading.Behind >= WarningTime || reading.BehindBytes >= WarningBytes ? HealthSeverity.Warning : null;
    }

    /// <summary>
    /// R03's finding of <paramref name="variant" /> about the readings that are behind, worst first - or <see langword="null" />
    /// when none is. The evaluator's one input is the list, so any source of readings is judged alike.
    /// </summary>
    /// <param name="variant">The variant: <c>primary-view</c>, <c>standby-view</c> (and AUR's own).</param>
    /// <param name="readings">Every replica read, whether behind or not.</param>
    /// <param name="judged">How many replicas were judged, for the count.</param>
    /// <param name="whyItMatters">The finding's explanation for this source of readings.</param>
    /// <param name="caveat">How the readings were taken.</param>
    /// <param name="context">Database-wide evidence to add after the numbers (where it was measured, the platform).</param>
    public static HealthFinding? Finding(
        string variant,
        IReadOnlyList<StandbyLagReading> readings,
        int judged,
        string whyItMatters,
        string caveat,
        IReadOnlyList<HealthEvidence> context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variant);
        ArgumentNullException.ThrowIfNull(readings);
        ArgumentNullException.ThrowIfNull(context);

        List<(StandbyLagReading Reading, HealthSeverity Severity)> behind =
        [
            .. readings
                .Select(static x => (Reading: x, Severity: Judge(x)))
                .Where(static x => x.Severity is not null)
                .Select(static x => (x.Reading, Severity: x.Severity!.Value))
                .OrderBy(static x => x.Severity)
                .ThenByDescending(static x => x.Reading.Behind ?? TimeSpan.Zero)
                .ThenByDescending(static x => x.Reading.BehindBytes ?? 0),
        ];

        if (behind.Count == 0)
        {
            return null;
        }

        HealthSeverity severity = behind.Min(static x => x.Severity);
        TimeSpan? mostTime = behind.Max(static x => x.Reading.Behind);
        long? mostBytes = behind.Max(static x => x.Reading.BehindBytes);

        List<HealthEvidence> evidence =
        [
            new("Behind", SessionText.Number(behind.Count) + " of " + SessionText.Count(judged, "replica") + " judged"),
            new("Furthest behind in time", mostTime is { } time ? SessionText.Duration(time) : "not measured"),
            new("Furthest behind in WAL", mostBytes is { } bytes ? WalText.Bytes(bytes) : "not measured"),
            new("Thresholds", "Warning at " + SessionText.Duration(WarningTime) + " or " + WalText.Bytes(WarningBytes) + "; Critical at " + SessionText.Duration(CriticalTime)),
        ];

        evidence.AddRange(context);

        int index = 0;
        foreach ((StandbyLagReading reading, HealthSeverity _) in behind.Take(ReplicasNamed))
        {
            evidence.Add(new HealthEvidence(SessionEvidence.Label("Replica", index, "furthest behind"), Describe(reading), HealthGate.Activity));
            index++;
        }

        return new HealthFinding
        {
            Code = "R03",
            Variant = variant,
            Severity = severity,
            Title = (behind.Count == 1 ? "A standby is " : SessionText.Number(behind.Count) + " standbys are ")
                + (mostTime is { } furthest && furthest >= WarningTime ? SessionText.Duration(furthest) : WalText.Bytes(mostBytes ?? 0))
                + " behind" + (behind.Count == 1 ? string.Empty : " at most"),
            WhyItMatters = whyItMatters,
            Evidence = evidence,
            Caveat = caveat,
        };
    }

    /// <summary>One reading, for its gated evidence line.</summary>
    internal static string Describe(StandbyLagReading reading) =>
        reading.Replica
        + (reading.BehindBytes is { } bytes ? ", " + WalText.Bytes(bytes) + " behind" : string.Empty)
        + (reading.Behind is { } time ? ", " + SessionText.Duration(time) + " behind" : string.Empty)
        + (reading.Detail is { Length: > 0 } detail ? ", " + detail : string.Empty);

    private static HealthVerdict PrimaryView(StandbyLagFacts facts)
    {
        StandbySnapshot standbys = facts.Standbys!;

        IReadOnlyList<StandbyRow> physical = standbys.Physical;
        int logical = standbys.Rows.Count - physical.Count;

        string? others = logical > 0 || standbys.OtherDatabases > 0
            ? "Not standbys, and not judged: " + string.Join(" and ", new[]
            {
                logical > 0 ? SessionText.Count(logical, "logical sender") + " of this database" : null,
                standbys.OtherDatabases > 0 ? SessionText.Count(standbys.OtherDatabases, "logical sender") + " of other databases on this server, never named" : null,
            }.Where(static x => x is not null)) + "."
            : null;

        if (physical.Count == 0)
        {
            return HealthVerdict.Quiet(Join("No standby streams from this server.", others));
        }

        List<StandbyRow> readable = [.. physical.Where(static x => x.Readable)];
        List<StandbyLagReading> readings = [.. readable.Select(Reading)];

        string? unreadable = standbys.Unreadable > 0
            ? SessionText.Count(standbys.Unreadable, "other standby") + ": " + NotChecked(facts.Server, facts.Grants, standbys.Unreadable)
            : null;

        string? capped = standbys.Total > standbys.Rows.Count
            ? "Only " + SessionText.Number(standbys.Rows.Count) + " of " + SessionText.Number(standbys.Total) + " senders were read, the standbys and the furthest behind first."
            : null;

        HealthFinding? finding = Finding(
            "primary-view",
            readings,
            readable.Count,
            "A standby that is behind is behind for whatever reads it - a reporting replica serving stale numbers, a read model "
            + "a little out of date - and, if it is the failover target, everything it has not replayed is what a failover loses "
            + "or has to wait for. It also holds WAL on this server through its slot until it catches up. The usual causes are "
            + "a standby whose disk or CPU cannot keep up, a network link, a long query on the standby that replay waits for "
            + "(max_standby_streaming_delay), or replay paused there.",
            "Read in pg_stat_replication at " + standbys.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + ": the bytes are this server's WAL position less the standby's replay position, and the time is the standby's own "
            + "replay_lag, which is empty once it has caught up and nothing new arrives - so an idle primary never makes it grow. "
            + "A standby that delays replay on purpose (recovery_min_apply_delay) shows that delay here as lag: the delay is the "
            + "standby's own setting, which cannot be read from the primary. Standbys are named only with ViewDatabaseActivity.",
            [new HealthEvidence("Measured on", "the primary, in pg_stat_replication"), new HealthEvidence("Platform", facts.Server.Platform.Name)]);

        string? note = Join(Join(unreadable, capped), others);

        if (finding is not null)
        {
            return new HealthVerdict([finding], note);
        }

        long? bytes = readings.Max(static x => x.BehindBytes);
        TimeSpan? time = readings.Max(static x => x.Behind);

        return HealthVerdict.Quiet(Join(
            readable.Count == 0
                ? null
                : SessionText.Count(readable.Count, "standby") + (readable.Count == 1 ? " streams" : " stream") + " from this server, none of them behind: the furthest is "
                  + (bytes is { } b ? WalText.Bytes(b) : "not measured") + " and "
                  + (time is { } t ? SessionText.Duration(t) : "no replay lag") + " behind.",
            note));
    }

    private static HealthVerdict StandbyView(StandbyLagFacts facts, HealthEvaluation evaluation)
    {
        ReplicationRole role = facts.Role;
        long? bytes = role.ReplayBehindBytes;
        TimeSpan? age = role.ReplayAgeAtRead is { } read ? SnapshotAge.At(read, role.ReadAt, evaluation) : null;
        TimeSpan delay = facts.ApplyDelay is { } applyDelay && applyDelay > TimeSpan.Zero ? applyDelay : TimeSpan.Zero;
        bool heldBefore = HeldBefore(evaluation.History, role.ReadAt);
        bool timeJudged = bytes > 0 && (heldBefore || bytes >= StandbyTimeFloorBytes);
        TimeSpan? beyondDelay = age is { } a && delay > TimeSpan.Zero ? (a > delay ? a - delay : TimeSpan.Zero) : age;

        List<HealthEvidence> context =
        [
            new("Measured on", "this standby: what it has received less what it has replayed"),
            new("Received up to", role.ReceiveLsn ?? "nothing by streaming"),
            new("Replayed up to", role.ReplayLsn ?? "not readable"),
            new("Last replayed commit", role.LastReplayAt is { } at
                ? at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + (age is { } ago ? ", " + SessionText.Duration(ago) + " ago" : string.Empty)
                : "none yet"),
            new("Replay", role.PauseState ?? (PostgresFeatures.Has(PostgresFeature.WalReplayPauseState, facts.Server.VersionNumber) ? "not read" : PostgresFeatures.Needs(PostgresFeature.WalReplayPauseState))),
            new("recovery_min_apply_delay", facts.ApplyDelay is null ? "not readable" : delay == TimeSpan.Zero ? "0 (replay is not delayed)" : SessionText.Duration(delay) + ": only the time beyond it is judged, and the WAL it holds is not"),
            new("Platform", facts.Server.Platform.Name),
        ];

        string caveat =
            "Read at " + role.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + " on the standby the data source "
            + "reached. The time is the age of the last replayed commit, which grows on an idle primary with no lag at all, so it "
            + "is judged only while the standby has held WAL it has not replayed on two consecutive samples, or holds "
            + WalText.Bytes(StandbyTimeFloorBytes) + " or more now - an instant's record in flight is not lag - or while replay is paused.";

        if (role.IsPaused)
        {
            List<HealthEvidence> evidence =
            [
                new("Behind in WAL", bytes is { } b ? WalText.Bytes(b) : "not measured"),
                new("Behind in time", age is { } pausedFor ? SessionText.Duration(pausedFor) + " since the last replayed commit" : "not measured"),
                .. context,
            ];

            return HealthVerdict.Of(new HealthFinding
            {
                Code = "R03",
                Variant = "paused",
                Severity = HealthSeverity.Warning,
                Title = "Replay is paused on this standby" + (bytes is > 0 ? ", " + WalText.Bytes(bytes.Value) + " behind" : string.Empty),
                WhyItMatters =
                    "Somebody paused WAL replay here (pg_wal_replay_pause()), so the standby keeps receiving WAL and applies none of "
                    + "it: everything read from it is as of the pause, it falls further behind with every write on the primary, "
                    + "and a failover to it has all of that to replay first. A pause for a consistent backup or an investigation is "
                    + "deliberate; one that is forgotten is a standby quietly going stale. pg_wal_replay_resume() resumes it.",
                Evidence = evidence,
                Caveat = caveat,
            });
        }

        StandbyLagReading reading = new(
            ThisServer,
            delay > TimeSpan.Zero ? null : bytes,
            timeJudged ? beyondDelay : null,
            delay > TimeSpan.Zero && bytes is { } held ? WalText.Bytes(held) + " received and held by recovery_min_apply_delay" : null);

        HealthFinding? finding = Finding(
            "standby-view",
            [reading],
            1,
            "This standby has received WAL it has not replayed, so what is read from it is behind the primary - by the bytes "
            + "it holds and, while it holds them, by the age of the last commit it replayed. If it is the failover target, "
            + "that is what a failover has to replay first. The usual causes are a disk or CPU that cannot keep up, or a long "
            + "query here that replay waits for (max_standby_streaming_delay).",
            caveat,
            context);

        return finding is not null
            ? HealthVerdict.Of(finding)
            : HealthVerdict.Quiet(bytes is null
                ? "This server is a standby that receives nothing by streaming, so its replay lag is not measured."
                : "This server is a standby, " + WalText.Bytes(bytes.Value) + " behind what it has received"
                  + (timeJudged && beyondDelay is { } behindFor ? " and " + SessionText.Duration(behindFor) + " behind in time" : string.Empty)
                  + (delay > TimeSpan.Zero ? ", beyond its " + SessionText.Duration(delay) + " recovery_min_apply_delay." : ".")
                  + (bytes > 0 && !timeJudged ? " It holds WAL it has not replayed on this sample only; the time is judged once it has on two." : string.Empty));
    }

    /// <summary>Whether the sample before <paramref name="readAt" /> found the standby holding WAL it had not replayed.</summary>
    internal static bool HeldBefore(HealthHistoryView history, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(history);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = history.Trend(ReplayBehindSeries).Points;

        for (int i = points.Count - 1; i >= 0; i--)
        {
            if (points[i].At < readAt)
            {
                return points[i].Value > 0;
            }
        }

        return false;
    }

    private static StandbyLagReading Reading(StandbyRow row) => new(
        "'" + (row.ApplicationName ?? string.Empty) + "'",
        row.BehindBytes,
        row.ReplayLag,
        string.Join(", ", new[]
        {
            row.ClientAddress is { } client ? "from " + client : "from a local socket",
            row.User is { } user ? "role " + user : null,
            row.State,
            row.SyncState,
            row.WriteLag is { } write ? "write lag " + SessionText.Duration(write) : null,
            row.FlushLag is { } flush ? "flush lag " + SessionText.Duration(flush) : null,
        }.Where(static x => x is not null)));

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => a + " " + b,
        };
}
