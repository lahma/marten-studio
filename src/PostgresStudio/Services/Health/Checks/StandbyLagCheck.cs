using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Aurora;
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
/// <param name="Stall">
/// On a standby, where replay stands against what the advisor has seen it receive (<see cref="StandbyLagCheck.Carry" />),
/// or <see langword="null" /> on a primary or when a position was not readable (W5-R2-fix2 1, W5-R2-fix3 2).
/// </param>
internal sealed record StandbyLagFacts(
    PostgresServerFacts Server,
    ReplicationRole Role,
    StandbySnapshot? Standbys,
    PostgresGrants Grants,
    TimeSpan? ApplyDelay = null,
    StandbyLagCheck.ReplayStall? Stall = null)
{
    /// <summary>
    /// On Aurora, every instance as <c>aurora_replica_status()</c> reports it (issue #8 A3, W5-AUR) - the <c>aurora-reader</c>
    /// view - and <see langword="null" /> on every other platform.
    /// </summary>
    public AuroraReplicaStatus? Aurora { get; init; }
}

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
/// multi-host data source that reaches one) - received less replayed WAL is exact and needs no grant. The <i>time</i> is not
/// the age of the last replayed commit, which grows with no lag at all on an idle primary, behind a long writer that has not
/// committed (a freeze <c>VACUUM</c>), and on a delayed standby, whose last commit is the one before a quiet night: each
/// sweep samples what the standby has received (<c>replication.standby-received</c>), and the time behind is the age of the
/// oldest sample whose position replay has still not reached - the WAL received by then is unreplayed now (W5-R2-fix2 1, 3).
/// It is a lower bound, exact to within a sample. That start is carried from sweep to sweep in series of its own
/// (<see cref="StallSinceSeries" />), so a stall longer than the ring of samples still grows to Critical, and a sample above
/// what the standby has received now - from before a <c>pg_rewind</c> or a restore - is no start at all (W5-R2-fix3 1, 2).
/// With no earlier sample, or while replay has been seen to pass nothing and the bound is still under
/// <see cref="WarningTime" />, the time is "not judged yet". A standby whose WAL receiver has disconnected receives nothing
/// and catches up with what it has, so it is not seen to fall behind from here: R01 and the primary's view see it. A paused
/// replay (<c>pg_wal_replay_pause()</c>, 14+) is <c>paused</c>, a Warning of its own: somebody stopped it, and it falls
/// further behind until somebody resumes it.
/// </para>
/// <para>
/// <b>A delayed standby</b> (<c>recovery_min_apply_delay</c>) holds back replay on purpose. On the standby the delay is read,
/// and the time judged is the time beyond it: a healthy one replays what it received a delay ago, so it fires only when WAL
/// received more than the delay and <see cref="WarningTime" /> ago is still unreplayed. Until replay is first seen to pass a
/// position the advisor sampled - about one delay after it began watching - the time is "not judged yet" unless it already
/// exceeds the delay by <see cref="WarningTime" />; the carried start needs no samples reaching back that far. Its bytes are the delay's by
/// design, and are shown, not judged. From the primary the delay cannot be seen (it is the standby's setting), so its
/// <c>replay_lag</c> and bytes include it: a known limit, which the caveat says (W5-R2-fix 6).
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
/// <b>On Aurora</b> - <c>aurora-reader</c> (issue #8 A3, W5-AUR) - readers do not stream WAL, so neither view applies: each
/// reader's <c>replica_lag_in_msec</c> from <c>aurora_replica_status()</c>, "Time that reader instance lags behind writer
/// instance, in milliseconds" (<i>aurora_replica_status</i>, Aurora User Guide, read 2026-10-26), is judged on G8's time
/// thresholds. There is no byte figure. An instance's identifier is the cluster's own name for one of its instances - not a
/// session's, a role's or a client's - so it is database-wide information (D29) and its line needs no activity gate.
/// </para>
/// </remarks>
[HealthCheck("R03", HealthArea.Replication, HealthCostTier.Catalog, "Standby lag", Variants = ["primary-view", "standby-view", "paused", "aurora-reader"])]
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
    /// What a standby has received (<c>pg_last_wal_receive_lsn()</c>, as a number), sampled so a later sweep can tell how long
    /// WAL it received has stayed unreplayed (W5-R2-fix2 1).
    /// </summary>
    public const string ReceivedSeries = "replication.standby-received";

    /// <summary>
    /// When the oldest sample whose position replay has not reached was taken, in Unix milliseconds - written every sweep
    /// while replay has not passed it, and gone once it has - so the stall's start outlives the ring of samples
    /// (W5-R2-fix3 2).
    /// </summary>
    public const string StallSinceSeries = "replication.standby-stall-since";

    /// <summary>That sample's receive position, which replay must pass for the stall to end.</summary>
    public const string StallPositionSeries = "replication.standby-stall-position";

    /// <summary>
    /// Written (as 1) while the stall began at the first sample the advisor took and replay has been seen to pass nothing
    /// since, so it may be older than the samples: its time is a bound, judged only once it reaches <see cref="WarningTime" />.
    /// </summary>
    public const string StallOpenSeries = "replication.standby-stall-open";

    /// <inheritdoc />
    public override async Task<StandbyLagFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (server.Platform.Kind == PostgresPlatformKind.AmazonAurora)
        {
            // Aurora's readers do not stream: their lag is Aurora's own function's, never pg_stat_replication's (A3).
            AuroraReplicaStatus status = await AuroraReplicaStatus.SharedAsync(context).ConfigureAwait(false);
            PostgresGrants auroraGrants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);

            return new StandbyLagFacts(server, new ReplicationRole(server.InRecovery, null, null, null, null, null, null, context.Now), null, auroraGrants)
            {
                Aurora = status,
            };
        }

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

        // The advisor's earlier samples of this database, at R03's own gate - exactly what the next evaluation would be handed,
        // as the Replication page reads them - so the stall's start can be carried into this sweep's sample. Only R03's own
        // series are read. Nothing here is I/O.
        ReplayStall? stall = role.InRecovery
            ? Carry(context.Services.GetService<HealthHistory>()?.Read(context.StoreKey, HealthHistory.RingOf(context.DatabaseId, HealthSweepKind.Regular)).VisibleAt(HealthGate.Database) ?? HealthHistoryView.Empty, role)
            : null;

        return new StandbyLagFacts(server, role, standbys, grants, delay, stall);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(StandbyLagFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Role is { InRecovery: true, ReceivePosition: { } received })
        {
            yield return new HealthSamplePart(ReceivedSeries, received);
        }

        if (facts.Stall is { Since: { } since, Position: { } position } stall)
        {
            yield return new HealthSamplePart(StallSinceSeries, since.ToUnixTimeMilliseconds());
            yield return new HealthSamplePart(StallPositionSeries, position);

            if (stall.SinceBeforeTheSamples)
            {
                yield return new HealthSamplePart(StallOpenSeries, 1);
            }
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

        if (facts.Aurora is { } aurora)
        {
            return AuroraView(facts, aurora);
        }

        return facts.Role.InRecovery ? StandbyView(facts, evaluation) : PrimaryView(facts);
    }

    /// <summary>The variant of a finding about Aurora's readers (A3).</summary>
    public const string AuroraVariant = "aurora-reader";

    /// <summary>
    /// The <c>aurora-reader</c> view: each reader's <c>replica_lag_in_msec</c> on G8's time thresholds, the writer left out (its
    /// lag is null - it is what the readers are behind).
    /// </summary>
    private static HealthVerdict AuroraView(StandbyLagFacts facts, AuroraReplicaStatus status)
    {
        IReadOnlyList<AuroraReplica> readers = status.Readers;

        if (readers.Count == 0)
        {
            return HealthVerdict.Quiet(
                "This Aurora cluster has no reader instance: aurora_replica_status() lists "
                + (status.WriterInstance is { } writer ? "only its writer, " + writer.Instance : "no instance at all") + ".");
        }

        List<StandbyLagReading> readings = [.. readers.Select(static x => new StandbyLagReading(x.Instance, null, x.Lag, AuroraDetail(x)))];
        int unmeasured = readers.Count(static x => x.Lag is null);

        HealthFinding? finding = Finding(
            AuroraVariant,
            readings,
            readers.Count,
            "An Aurora reader that is behind serves reads that are behind: \"Replica lag is the time in milliseconds that the page cache "
            + "of a reader instance is behind that of the writer instance\" (aurora_replica_status, Aurora User Guide). Whatever "
            + "reads from it - the reader endpoint, a reporting job - sees older data by that much. The usual causes are a reader "
            + "too small for the writer's write rate (a Serverless v2 reader in promotion tier 2 to 15 scales on its own, and a low "
            + "minimum capacity there \"can result in excessive replication lag\" - Performance and scaling for Aurora serverless), or "
            + "long queries on the reader that conflict with recovery (pg_stat_database_conflicts says which).",
            "Read in aurora_replica_status() at " + status.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + ": replica_lag_in_msec for each reader, which CloudWatch keeps as AuroraReplicaLag. Aurora replicates through its "
            + "storage rather than a WAL stream, so there is no byte figure. An instance is named by the cluster's own identifier for "
            + "it, shown to anyone the database-wide gate admits." + (unmeasured > 0
                ? " " + SessionText.Count(unmeasured, "reader") + (unmeasured == 1 ? " reports" : " report") + " no lag, and " + (unmeasured == 1 ? "is" : "are") + " not judged."
                : string.Empty),
            [new HealthEvidence("Measured on", "aurora_replica_status(), Aurora's replica status manager"), new HealthEvidence("Platform", facts.Server.Platform.Name)],
            replicaGate: HealthGate.Database,
            one: "An Aurora reader is ",
            many: " Aurora readers are ",
            measuresBytes: false);

        if (finding is not null)
        {
            return HealthVerdict.Of(finding);
        }

        AuroraReplica? furthest = readers.Where(static x => x.Lag is not null).OrderByDescending(static x => x.Lag).FirstOrDefault();

        return HealthVerdict.Quiet(
            SessionText.Count(readers.Count, "Aurora reader") + ", none behind"
            + (furthest is { Lag: { } lag } ? ": the furthest, " + furthest.Instance + ", is " + Milliseconds(lag) + " behind the writer" : string.Empty)
            + " (aurora_replica_status())."
            + (unmeasured > 0 ? " " + SessionText.Count(unmeasured, "reader") + (unmeasured == 1 ? " reports" : " report") + " no lag." : string.Empty));
    }

    /// <summary>A reader's line beyond its lag: its replay latency, its feedback and its status's age.</summary>
    private static string? AuroraDetail(AuroraReplica reader)
    {
        string detail = string.Join(", ", new[]
        {
            reader.ReplayLatency is { } replay ? "replay expected to take " + Milliseconds(replay) : null,
            reader.FeedbackXmin is { Length: > 0 } xmin ? "feedback xmin " + xmin : null,
            reader.LastUpdate is { } updated ? "status updated " + updated.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : null,
        }.Where(static x => x is not null));

        return detail.Length == 0 ? null : detail;
    }

    /// <summary>A short lag as Aurora reports it: "13 ms", and anything a second or more as a duration.</summary>
    internal static string Milliseconds(TimeSpan lag) =>
        lag < TimeSpan.FromSeconds(1)
            ? lag.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + " ms"
            : SessionText.Duration(lag);

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
    /// <param name="replicaGate">
    /// The gate each replica's own line is shown at: <see cref="HealthGate.Activity" /> for a standby, whose application name
    /// and client address are a session's; <see cref="HealthGate.Database" /> for an Aurora instance, whose identifier is the
    /// cluster's own name for it (D29).
    /// </param>
    /// <param name="one">How the title begins for one replica behind.</param>
    /// <param name="many">How the title goes on after the count for several.</param>
    /// <param name="measuresBytes">Whether the source measures bytes behind at all - Aurora's does not.</param>
    public static HealthFinding? Finding(
        string variant,
        IReadOnlyList<StandbyLagReading> readings,
        int judged,
        string whyItMatters,
        string caveat,
        IReadOnlyList<HealthEvidence> context,
        HealthGate replicaGate = HealthGate.Activity,
        string one = "A standby is ",
        string many = " standbys are ",
        bool measuresBytes = true)
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
            new("Furthest behind in WAL", mostBytes is { } bytes ? WalText.Bytes(bytes) : measuresBytes ? "not measured" : "not measured: this source reports time only"),
            new("Thresholds", measuresBytes
                ? "Warning at " + SessionText.Duration(WarningTime) + " or " + WalText.Bytes(WarningBytes) + "; Critical at " + SessionText.Duration(CriticalTime)
                : "Warning at " + SessionText.Duration(WarningTime) + "; Critical at " + SessionText.Duration(CriticalTime)),
        ];

        evidence.AddRange(context);

        int index = 0;
        foreach ((StandbyLagReading reading, HealthSeverity _) in behind.Take(ReplicasNamed))
        {
            evidence.Add(new HealthEvidence(SessionEvidence.Label("Replica", index, "furthest behind"), Describe(reading), replicaGate));
            index++;
        }

        return new HealthFinding
        {
            Code = "R03",
            Variant = variant,
            Severity = severity,
            Title = (behind.Count == 1 ? one : SessionText.Number(behind.Count) + many)
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
            + "A known limit: a standby that delays replay on purpose (recovery_min_apply_delay) shows that delay here as lag - "
            + "an hour's delay reads as Critical - because the delay is the standby's own setting, which cannot be read from the "
            + "primary; a data source that reaches the standby judges it there, beyond its delay. Standbys are named only with "
            + "ViewDatabaseActivity.",
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
        ReplayStall? stall = facts.Stall;
        TimeSpan? unreplayedFor = bytes > 0 ? stall?.UnreplayedFor(role.ReadAt) : null;
        TimeSpan? beyondDelay = unreplayedFor is { } behindFor ? (behindFor > delay ? behindFor - delay : TimeSpan.Zero) : null;
        bool timeJudged = beyondDelay is { } beyond && (!stall!.SinceBeforeTheSamples || beyond >= WarningTime);

        List<HealthEvidence> context =
        [
            new("Measured on", "this standby: what it has received less what it has replayed, and how long WAL it received has gone unreplayed"),
            new("Received up to", role.ReceiveLsn ?? "nothing by streaming"),
            new("Replayed up to", role.ReplayLsn ?? "not readable"),
            new("Unreplayed since", stall switch
            {
                null => "not measured: the replay position was not readable",
                { Samples: 0 } => "not measured yet: no earlier sample of what it received",
                { Since: { } since } when bytes > 0 && since < role.ReadAt => since.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                    + (stall.SinceBeforeTheSamples ? " or earlier: the advisor's samples begin there" : string.Empty),
                _ => "nothing the advisor's samples saw it receive is unreplayed",
            }),
            new("Last replayed commit", role.LastReplayAt is { } at
                ? at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + (age is { } ago ? ", " + SessionText.Duration(ago) + " ago" : string.Empty) + " - shown, never judged"
                : "none yet"),
            new("Replay", role.PauseState ?? (PostgresFeatures.Has(PostgresFeature.WalReplayPauseState, facts.Server.VersionNumber) ? "not read" : PostgresFeatures.Needs(PostgresFeature.WalReplayPauseState))),
            new("recovery_min_apply_delay", facts.ApplyDelay is null ? "not readable" : delay == TimeSpan.Zero ? "0 (replay is not delayed)" : SessionText.Duration(delay) + ": only the time beyond it is judged, and the WAL it holds is not"),
            new("Platform", facts.Server.Platform.Name),
        ];

        string caveat =
            "Read at " + role.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + " on the standby the data source "
            + "reached. The time is how long WAL this standby received has gone unreplayed: each sweep samples what it has received, "
            + "and the time is the age of the oldest sample whose position replay has still not reached - at least that long, exact "
            + "to within a sample. The age of the last replayed commit is never the time: it grows with no lag at all on an idle "
            + "primary, behind a long transaction that has not committed, and on a delayed standby after a quiet spell."
            + (delay > TimeSpan.Zero
                ? " With recovery_min_apply_delay set, the time judged is the time beyond the delay; until replay is first seen to "
                  + "pass a position the advisor sampled - about one delay after it began watching - only a time already more than "
                  + SessionText.Duration(WarningTime) + " beyond the delay is judged."
                : string.Empty)
            + " A standby whose WAL receiver has disconnected receives nothing and is not seen to fall behind from here; R01 and "
            + "the primary's view see it.";

        if (role.IsPaused)
        {
            List<HealthEvidence> evidence =
            [
                new("Behind in WAL", bytes is { } b ? WalText.Bytes(b) : "not measured"),
                new("Behind in time", unreplayedFor is { } pausedFor && pausedFor > TimeSpan.Zero
                    ? "WAL received " + SessionText.Duration(pausedFor) + " ago" + (stall!.SinceBeforeTheSamples ? " or earlier" : string.Empty) + " is not replayed"
                    : "not measured"),
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

        string? detail = string.Join("; ", new[]
        {
            timeJudged && stall!.SinceBeforeTheSamples ? "at least: WAL received when the advisor's samples begin is not replayed yet" : null,
            delay > TimeSpan.Zero ? "the time beyond its " + SessionText.Duration(delay) + " recovery_min_apply_delay" : null,
            delay > TimeSpan.Zero && bytes is { } held ? WalText.Bytes(held) + " received and held by the delay" : null,
        }.Where(static x => x is not null));

        StandbyLagReading reading = new(
            ThisServer,
            delay > TimeSpan.Zero ? null : bytes,
            timeJudged ? beyondDelay : null,
            detail.Length == 0 ? null : detail);

        HealthFinding? finding = Finding(
            "standby-view",
            [reading],
            1,
            "This standby has received WAL it has not replayed, so what is read from it is behind the primary - by the bytes "
            + "it holds and by how long it has held them. If it is the failover target, that is what a failover has to replay "
            + "first. The usual causes are a disk or CPU that cannot keep up, or a long query here that replay waits for "
            + "(max_standby_streaming_delay).",
            caveat,
            context);

        return finding is not null
            ? HealthVerdict.Of(finding)
            : HealthVerdict.Quiet(bytes is null
                ? "This server is a standby that receives nothing by streaming, so its replay lag is not measured."
                : "This server is a standby, " + WalText.Bytes(bytes.Value) + " behind what it has received"
                  + (bytes > 0 ? TimeNote(stall, unreplayedFor, beyondDelay, timeJudged, delay, role.ReadAt) : ".")
                  + (bytes == 0 && delay > TimeSpan.Zero ? " Its recovery_min_apply_delay is " + SessionText.Duration(delay) + "." : string.Empty));
    }

    /// <summary>What a quiet standby's status row says of the time, so "not judged yet" is never silence.</summary>
    private static string TimeNote(ReplayStall? stall, TimeSpan? unreplayedFor, TimeSpan? beyondDelay, bool timeJudged, TimeSpan delay, DateTimeOffset readAt)
    {
        string delayed = delay > TimeSpan.Zero ? " recovery_min_apply_delay is " + SessionText.Duration(delay) + "." : string.Empty;

        if (stall is null)
        {
            return "; its replay position was not readable, so the time is not judged." + delayed;
        }

        if (stall.Samples == 0)
        {
            return "; the time is not judged yet: it is measured against what the advisor sees it receive, from the next sample on." + delayed;
        }

        if (stall.Since is null || (stall.Since >= readAt && stall.PassedAt is not null))
        {
            return "; replay has reached everything it had received at the advisor's last sample, "
                + SessionText.Duration(readAt - stall.PassedAt!.Value) + " ago, so it is not behind in time." + delayed;
        }

        if (!timeJudged)
        {
            return "; replay has reached nothing the advisor's samples saw it receive over the " + SessionText.Duration(unreplayedFor ?? TimeSpan.Zero)
                + " they cover, so the time is not judged yet"
                + (delay > TimeSpan.Zero
                    ? ": with a recovery_min_apply_delay of " + SessionText.Duration(delay) + ", it is judged once replay is seen to pass "
                      + "a position the advisor sampled - about " + SessionText.Duration(delay) + " after it began watching - or once WAL "
                      + "received more than " + SessionText.Duration(delay + WarningTime) + " ago is still unreplayed."
                    : ".");
        }

        return ", and WAL it received " + SessionText.Duration(unreplayedFor!.Value) + " ago is not replayed yet"
            + (delay > TimeSpan.Zero ? ", " + SessionText.Duration(beyondDelay!.Value) + " beyond its " + SessionText.Duration(delay) + " recovery_min_apply_delay." : ".");
    }

    /// <summary>
    /// Where replay stands against what the advisor has seen the standby receive: the start of the stall - the oldest sample,
    /// this read included, whose position replay has not reached - carried from sweep to sweep.
    /// </summary>
    /// <param name="Since">
    /// When that sample was taken - the WAL received by then is still unreplayed - or <see langword="null" /> when nothing
    /// received is unreplayed. This read's own time when replay has reached every earlier sample.
    /// </param>
    /// <param name="Position">That sample's receive position.</param>
    /// <param name="SinceBeforeTheSamples">
    /// Whether the stall began at the first sample the advisor has and replay has been seen to pass nothing since, so it may
    /// be older: <see cref="UnreplayedFor" /> is then a bound, judged only once it reaches <see cref="WarningTime" />.
    /// </param>
    /// <param name="PassedAt">When the newest earlier sample replay has reached was taken, or <see langword="null" />.</param>
    /// <param name="Samples">How many earlier samples of this history there are.</param>
    internal sealed record ReplayStall(DateTimeOffset? Since, ulong? Position, bool SinceBeforeTheSamples, DateTimeOffset? PassedAt, int Samples)
    {
        /// <summary>How long WAL received has gone unreplayed - at least - or <see langword="null" /> when nothing has.</summary>
        public TimeSpan? UnreplayedFor(DateTimeOffset readAt) => Since is { } since ? (readAt > since ? readAt - since : TimeSpan.Zero) : null;
    }

    /// <summary>
    /// The stall of a standby read as <paramref name="role" />, from the samples before it in <paramref name="earlier" />: the
    /// start the newest of them carried, while replay has still not reached its position; otherwise the oldest sample, in the
    /// run of the newest ones, that replay has not reached; otherwise this read.
    /// </summary>
    /// <remarks>
    /// Only samples of this history count. Within one, what a standby has received only goes up, so a sample above what it
    /// has received now is from before a <c>pg_rewind</c> onto an earlier fork or a restore to an earlier point, and would
    /// otherwise read as WAL received long ago and never replayed (W5-R2-fix3 1). And the start is carried rather than read
    /// from the ring alone, so a stall longer than the ring - 60 samples, half an interval apart at the closest - still grows
    /// to Critical (W5-R2-fix3 2).
    /// </remarks>
    internal static ReplayStall? Carry(HealthHistoryView earlier, ReplicationRole role)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(role);

        if (role.ReplayPosition is not { } replayed || role.ReceivePosition is not { } received)
        {
            return null;
        }

        DateTimeOffset readAt = role.ReadAt;
        List<HealthSample> samples =
        [
            .. earlier.Samples
                .Where(x => x.At < readAt && x.Values.TryGetValue(ReceivedSeries, out double position) && position <= received)
                .OrderBy(static x => x.At),
        ];

        DateTimeOffset? newest = samples.Count == 0 ? null : samples[^1].At;

        if (received <= replayed)
        {
            return new ReplayStall(null, null, false, newest, samples.Count);
        }

        bool passedCarried = false;

        if (samples.Count > 0
            && samples[^1].Values.TryGetValue(StallSinceSeries, out double carriedSince)
            && samples[^1].Values.TryGetValue(StallPositionSeries, out double carriedPosition)
            && carriedPosition <= received)
        {
            if (carriedPosition > replayed)
            {
                return new ReplayStall(
                    DateTimeOffset.FromUnixTimeMilliseconds((long) carriedSince),
                    (ulong) carriedPosition,
                    samples[^1].Values.ContainsKey(StallOpenSeries),
                    null,
                    samples.Count);
            }

            // Replay passed the carried start: it moves, so whatever stall follows is not one older than the samples.
            passedCarried = true;
        }

        DateTimeOffset? since = null;
        ulong? position = null;
        DateTimeOffset? passed = null;

        for (int i = samples.Count - 1; i >= 0; i--)
        {
            double value = samples[i].Values[ReceivedSeries];

            if (value > replayed)
            {
                since = samples[i].At;
                position = (ulong) value;
            }
            else
            {
                passed = samples[i].At;
                break;
            }
        }

        return since is { } start
            ? new ReplayStall(start, position, passed is null && !passedCarried, passed, samples.Count)
            : new ReplayStall(readAt, received, samples.Count == 0, passed, samples.Count);
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
