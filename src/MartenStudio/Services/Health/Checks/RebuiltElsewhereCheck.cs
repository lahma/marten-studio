using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One async shard's progression row, as D08 read it.</summary>
/// <param name="Shard">The shard's identity (<c>ShardName.Identity</c>), which is the row's name.</param>
/// <param name="Projection">The projection or subscription it belongs to (<c>ShardName.Name</c>).</param>
/// <param name="Position"><c>last_seq_id</c>, or <see langword="null" /> when the shard has no row - before it first ran, or after a teardown.</param>
/// <param name="LastUpdated"><c>last_updated</c>.</param>
/// <param name="Mode"><c>mode</c>, where the table has it.</param>
/// <param name="DocumentTypes">The document types the projection writes, so a hidden one takes the finding with it.</param>
internal sealed record ShardProgressReading(
    string Shard,
    string Projection,
    long? Position,
    DateTimeOffset? LastUpdated,
    string? Mode,
    IReadOnlyList<Type> DocumentTypes);

/// <summary>A rebuild this process's studio started, finished or not.</summary>
/// <param name="Projection">The projection.</param>
/// <param name="StartedAt">When it started, by the studio's clock.</param>
/// <param name="FinishedAt">When it stopped, or <see langword="null" /> while it runs.</param>
/// <param name="State">How it ended, or <c>Running</c>.</param>
internal sealed record StudioRebuild(string Projection, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, StudioOperationState State);

/// <summary>A "Rewind subscription" this process's studio ran.</summary>
/// <param name="Projection">The projection or subscription rewound.</param>
/// <param name="At">When, by the studio's clock.</param>
internal sealed record StudioRewind(string Projection, DateTimeOffset At);

/// <summary>What D08 read.</summary>
/// <param name="HasAsyncWork">Whether the store registers an async projection or a subscription.</param>
/// <param name="Shards">Every async shard's row.</param>
/// <param name="HighWaterMark">The store-global high-water mark, or <see langword="null" /> without its row.</param>
/// <param name="ServerNow">The server's clock when the rows were read.</param>
/// <param name="StudioRebuilds">The rebuilds this process's studio remembers, against this database.</param>
/// <param name="StudioRewinds">The rewinds this process's studio ran against this database.</param>
/// <param name="RebuildingHere">
/// The shards this process is rebuilding right now: the tracker's latest state says so, or - Marten 9.50+ -
/// <c>IProjectionDaemon.IsRebuilding</c> does. A daemon that does not track rebuilds answers <see langword="false" />,
/// which proves nothing, so this only ever explains a finding away.
/// </param>
/// <param name="AskedDaemon">Whether <c>IsRebuilding</c> was asked at all - Marten 9.50+, with the daemon in this process.</param>
internal sealed record RebuildFacts(
    bool HasAsyncWork,
    IReadOnlyList<ShardProgressReading> Shards,
    long? HighWaterMark,
    DateTimeOffset ServerNow,
    IReadOnlyList<StudioRebuild> StudioRebuilds,
    IReadOnlyList<StudioRewind> StudioRewinds,
    IReadOnlySet<string> RebuildingHere,
    bool AskedDaemon);

/// <summary>
/// D08: a shard went backwards and nothing in this studio did it - it is being rebuilt somewhere else, or a rebuild
/// stopped half-way.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a database shows of a rebuild</b>, read at <c>V9.39.0</c> and <c>V9.50.2</c>. JasperFx's
/// <c>rebuildProjection</c> tears the projection down first - Marten's <c>TeardownExistingProjectionStateAsync</c>
/// deletes its documents and its shards' progression rows - and then replays from the start, writing progress as it
/// goes. So a shard's position falls (to nothing) and climbs back to the high-water mark. The progression table's
/// <c>mode</c> column would say <c>rebuilding</c>, but <b>no Marten release writes it</b>: it is created with
/// <c>ShardMode.none</c> as its default and only ever read (<c>ShardStateSelector</c>, under
/// <c>UseOptimizedProjectionRebuilds</c>); <c>ShardMode.rebuilding</c> is stamped only on the in-memory states a running
/// agent publishes (jasperfx#681). The check still reads the column, so a host whose tooling writes it is believed.
/// </para>
/// <para>
/// <b>What it watches instead</b> is the position itself, sampled into the advisor's history at every regular sweep:
/// a fall between two samples that no rebuild or rewind of this process's studio explains, and that this process is
/// not rebuilding either, is a rebuild, rewind or reset run somewhere else. While the shard climbs it is being replayed
/// there; once it has stood still below the mark for <see cref="StalledAfter" />, the replay stopped half-way. A studio
/// rebuild that failed or was cancelled below the mark is named as such. Nothing is said once the shard is back at
/// the mark, or back past where the replay began - the mark sampled with the fall, which also covers a replay that was
/// restarted part-way and fell a second time from below where the first began (<see cref="WithRecoveryTarget" />). A
/// healthy shard under steady appends sits a few events behind the current mark, so that mark alone would keep
/// calling a finished replay unfinished. A fall in the same interval as the mark's own (<see cref="MarkSeries" />) is
/// a progression correction and explains itself.
/// </para>
/// <para>
/// <b>Seen only while somebody watches.</b> The history exists only while a Health page or the Overview is open, so a
/// fall that happened while nobody looked is not seen - the finding says "watching" rather than "fine". Tier 1: the
/// shards' progression rows, a few dozen by primary key. A store that partitions its events by tenant keeps a mark per
/// tenant and is not covered, like D01-D04.
/// </para>
/// </remarks>
[HealthCheck("D08", HealthArea.Daemon, HealthCostTier.Catalog, "Rebuilt elsewhere, or stopped half-way")]
internal sealed class RebuiltElsewhereCheck : HealthCheck<RebuildFacts>
{
    /// <summary>A shard below the mark that has not moved for this long since it fell has stopped.</summary>
    internal static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(10);

    /// <summary>How far either side of a fall a studio operation still counts as its cause.</summary>
    internal static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    /// <summary>The prefix of the history series each shard's position is sampled into.</summary>
    internal const string PositionSeries = "daemon.rebuild-watch:";

    /// <summary>
    /// The history series of the store-global high-water mark, sampled beside the shards: a shard that falls in the
    /// same interval as the mark was moved back with it - Marten's <c>TryCorrectProgressInDatabaseAsync</c> (the
    /// studio's Correct progression, Marten's CLI, another process) rewrites every progression row, the mark's
    /// included, when the mark is ahead of the event sequence - which is a correction, not a rebuild.
    /// </summary>
    internal const string MarkSeries = "daemon.rebuild-watch-mark";

    /// <summary>The <c>mode</c> value a rebuilding shard would carry (<c>ShardMode.rebuilding</c>).</summary>
    internal static readonly string RebuildingMode = ShardMode.rebuilding.ToString();

    /// <summary>The history series of one shard's position.</summary>
    internal static string Series(string shard) => PositionSeries + shard;

    /// <inheritdoc />
    public override async Task<RebuildFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RebuildFacts empty = new(false, [], null, context.Now, [], [], new HashSet<string>(StringComparer.Ordinal), false);

        if (!DaemonAccessor.HasAsyncWork(context.Store) || context.Store.Options is not StoreOptions concrete)
        {
            return empty;
        }

        if (context.StoreOptions.Events.UseTenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(
                "This store partitions its events by tenant and keeps a high-water mark per tenant, which this check does not cover yet.");
        }

        IReadOnlyList<AsyncShard<IDocumentOperations, IQuerySession>> shards = concrete.Projections.AllShards();
        if (shards.Count == 0)
        {
            return empty;
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        if (shape.Progression is not { } progression)
        {
            throw new HealthCheckUnavailableException(
                "mt_event_progression does not exist in this database yet, so no shard has a position to watch.");
        }

        IReadOnlyList<ProgressionModeRow> rows = await context.Sql
            .QueryAsync(
                HealthEventQueries.ProgressionModes(shape.Schema, progression.Has("mode"), [.. shards.Select(static x => x.Name.Identity)]),
                HealthEventQueries.ReadProgressionMode,
                cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, ProgressionModeRow> byName = new(StringComparer.Ordinal);
        foreach (ProgressionModeRow row in rows)
        {
            if (row.Name is { } name)
            {
                byName[name] = row;
            }
        }

        long? mark = byName.TryGetValue(HealthEventQueries.HighWaterMarkRow, out ProgressionModeRow? markRow) ? markRow.Sequence : null;

        List<ShardProgressReading> readings = [];
        foreach (AsyncShard<IDocumentOperations, IQuerySession> shard in shards)
        {
            string identity = shard.Name.Identity;
            byName.TryGetValue(identity, out ProgressionModeRow? row);

            readings.Add(new ShardProgressReading(
                identity,
                shard.Name.Name,
                row?.Sequence,
                row?.LastUpdated,
                row?.Mode,
                PublishedTypes(concrete, shard.Name.Name)));
        }

        HashSet<string> rebuilding = RebuildingInTracker(context, readings);
        bool asked = await AskDaemonAsync(context, readings, mark, rebuilding, cancellationToken).ConfigureAwait(false);

        return new RebuildFacts(
            true,
            readings,
            mark,
            rows.Count > 0 ? rows[0].ServerNow : context.Now,
            StudioRebuilds(context),
            StudioRewinds(context),
            rebuilding,
            asked);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(RebuildFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        foreach (ShardProgressReading shard in facts.Shards)
        {
            // A shard with no row stands at nothing: before its first run, and right after a teardown.
            yield return new HealthSamplePart(Series(shard.Shard), shard.Position ?? 0);
        }

        if (facts.HasAsyncWork && facts.HighWaterMark is { } mark)
        {
            yield return new HealthSamplePart(MarkSeries, mark);
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(RebuildFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!facts.HasAsyncWork || facts.Shards.Count == 0)
        {
            return HealthVerdict.Quiet("The store registers no async projection or subscription, so there is no shard to watch.");
        }

        List<HealthFinding> findings = [];
        bool watched = false;

        foreach (ShardProgressReading shard in facts.Shards)
        {
            long position = shard.Position ?? 0;
            bool caughtUp = facts.HighWaterMark is { } mark && position >= mark;
            bool here = facts.RebuildingHere.Contains(shard.Shard);
            bool studioRunning = facts.StudioRebuilds.Any(x => Same(x.Projection, shard.Projection) && x.FinishedAt is null);

            if (caughtUp || here || studioRunning)
            {
                continue;
            }

            if (string.Equals(shard.Mode, RebuildingMode, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Persisted(facts, shard, position));
                continue;
            }

            HealthTrend trend = evaluation.History.Trend(Series(shard.Shard));
            watched |= trend.Points.Count >= 2;

            if (Fall(trend) is not { } fall)
            {
                continue;
            }

            HealthTrend marks = evaluation.History.Trend(MarkSeries);
            fall = WithRecoveryTarget(trend, marks, fall);

            if (position >= fall.RecoverAt)
            {
                // Back where it was before the replay began: whatever replayed it has finished, wherever it ran. A healthy
                // shard under steady appends sits a few events below the mark, so the mark alone would say "still
                // replaying"; and a replay restarted part-way has to get back past where the first one began.
                continue;
            }

            if (MarkFellWith(marks, fall))
            {
                // The mark fell in the same interval: every row was moved back together, which is a progression
                // correction - Marten rewrites the mark's row with the shards' - not a rebuild of this one.
                continue;
            }

            if (facts.StudioRewinds.Any(x => Same(x.Projection, shard.Projection) && x.At >= fall.From - Margin && x.At <= fall.To + Margin))
            {
                // This studio rewound it; climbing back is ordinary catch-up, and lag is D03's and D04's.
                continue;
            }

            StudioRebuild? studio = facts.StudioRebuilds
                .Where(x => Same(x.Projection, shard.Projection)
                    && x.StartedAt <= fall.To + Margin
                    && (x.FinishedAt ?? evaluation.Now) >= fall.From - Margin)
                .MaxBy(static x => x.StartedAt);

            if (studio is { State: StudioOperationState.Succeeded })
            {
                continue;
            }

            if (studio is not null)
            {
                findings.Add(StudioStopped(facts, shard, position, fall, studio));
                continue;
            }

            bool stopped = evaluation.Now - fall.LastChange >= StalledAfter;
            findings.Add(stopped
                ? StoppedHalfWay(facts, shard, position, fall, evaluation)
                : Elsewhere(facts, shard, position, fall));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watched
            ? null
            : "A rebuild elsewhere shows as a shard's position falling between two checks; that needs two samples, one interval apart.");
    }

    /// <summary>The most recent fall in a shard's position, and what it has done since.</summary>
    /// <param name="From">The sample before the fall.</param>
    /// <param name="To">The first sample after it.</param>
    /// <param name="Before">The position before.</param>
    /// <param name="After">The position just after.</param>
    /// <param name="Now">The position now.</param>
    /// <param name="LastChange">When the position last changed, since the fall.</param>
    internal sealed record PositionFall(DateTimeOffset From, DateTimeOffset To, double Before, double After, double Now, DateTimeOffset LastChange)
    {
        /// <summary>Whether it has climbed since it fell.</summary>
        public bool Climbing => Now > After;

        /// <summary>
        /// The position the shard has to reach again before the replay can be said to have finished - see
        /// <see cref="WithRecoveryTarget" />.
        /// </summary>
        public double RecoverAt { get; init; }

        /// <summary>Whether <see cref="RecoverAt" /> is the high-water mark sampled with the fall, rather than the history's highest.</summary>
        public bool RecoverAtMark { get; init; }
    }

    /// <summary>
    /// <paramref name="fall" /> with where the shard has to get back to before whatever replayed it has finished
    /// (<see cref="PositionFall.RecoverAt" />): the high-water mark sampled
    /// with the last sample before the fall - every position the shard had reached by then is at or below it, the one
    /// it stood at before an <em>earlier</em> fall that a restarted replay never got back to included - or, where the
    /// mark has no sample then, the highest position the history holds up to the fall. Never below the position just
    /// before the fall.
    /// </summary>
    /// <remarks>
    /// The latest fall alone is not enough: a rebuild restarted part-way falls a second time from below where the first
    /// began, and judged against that second starting point the shard would read as recovered half-way through the
    /// replay. The mark is taken at the fall rather than now, because under steady appends the mark keeps moving and a
    /// healthy shard sits a few events behind it.
    /// </remarks>
    internal static PositionFall WithRecoveryTarget(HealthTrend positions, HealthTrend marks, PositionFall fall)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(fall);

        foreach ((DateTimeOffset at, double value) in marks.Points)
        {
            if (at == fall.From)
            {
                return fall with { RecoverAt = Math.Max(fall.Before, value), RecoverAtMark = true };
            }
        }

        double highest = fall.Before;
        foreach ((DateTimeOffset at, double value) in positions.Points)
        {
            if (at <= fall.From && value > highest)
            {
                highest = value;
            }
        }

        return fall with { RecoverAt = highest, RecoverAtMark = false };
    }

    /// <summary>Whether the high-water mark fell between the two samples <paramref name="fall" /> fell between.</summary>
    internal static bool MarkFellWith(HealthTrend marks, PositionFall fall)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(fall);

        double? before = null;
        double? after = null;

        foreach ((DateTimeOffset at, double value) in marks.Points)
        {
            if (at == fall.From)
            {
                before = value;
            }
            else if (at == fall.To)
            {
                after = value;
            }
        }

        return before is { } then && after is { } now && now < then;
    }

    /// <summary>The most recent fall in <paramref name="trend" />, or <see langword="null" /> when it never fell.</summary>
    internal static PositionFall? Fall(HealthTrend trend)
    {
        ArgumentNullException.ThrowIfNull(trend);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = trend.Points;

        for (int i = points.Count - 2; i >= 0; i--)
        {
            if (points[i].Value <= points[i + 1].Value)
            {
                continue;
            }

            DateTimeOffset lastChange = points[i + 1].At;
            for (int j = i + 2; j < points.Count; j++)
            {
                if (points[j].Value != points[j - 1].Value)
                {
                    lastChange = points[j].At;
                }
            }

            return new PositionFall(points[i].At, points[i + 1].At, points[i].Value, points[i + 1].Value, points[^1].Value, lastChange);
        }

        return null;
    }

    private static HealthFinding Persisted(RebuildFacts facts, ShardProgressReading shard, long position) => new()
    {
        Code = "D08",
        Severity = HealthSeverity.Info,
        Title = "Shard " + shard.Shard + " is marked as rebuilding, and nothing in this process is rebuilding it",
        WhyItMatters =
            "Its progression row's mode column says rebuilding. No Marten release writes that column - the daemon keeps a "
            + "rebuild's mode in memory - so whatever wrote it is tooling of the host's own, or a later Marten. Until the shard "
            + "is back at the high-water mark, the projection's tables are being refilled and readers see a partial "
            + "projection; starting a rebuild here would race it, because two rebuilds of one projection tear down and refill "
            + "the same tables.",
        Subject = shard.Shard,
        Evidence =
        [
            new("mode", shard.Mode ?? string.Empty),
            .. Position(facts, shard, position),
        ],
        Caveat = InProcessCaveat(facts),
        DocumentTypes = shard.DocumentTypes,
    };

    private static HealthFinding Elsewhere(RebuildFacts facts, ShardProgressReading shard, long position, PositionFall fall) => new()
    {
        Code = "D08",
        Severity = HealthSeverity.Info,
        Title = fall.Climbing
            ? "Shard " + shard.Shard + " went back and is being replayed - by something other than this studio"
            : "Shard " + shard.Shard + " went back, and nothing in this studio did it",
        WhyItMatters =
            "A shard's position only falls when something rebuilds, rewinds or resets it: a rebuild deletes the projection's "
            + "documents and its progression rows, then replays from the first event, so until the shard is back at the "
            + "high-water mark readers see a partial projection. Neither a rebuild nor a rewind started from this studio "
            + "explains this one, and this process is not rebuilding it - so another process is, or somebody ran one by hand. "
            + "Starting a rebuild here now would race it: two rebuilds of one projection tear down and refill the same tables.",
        Subject = shard.Shard,
        Evidence = [.. Fallen(fall), .. Position(facts, shard, position)],
        Caveat = HistoryCaveat(fall) + " " + InProcessCaveat(facts),
        DocumentTypes = shard.DocumentTypes,
    };

    private static HealthFinding StoppedHalfWay(
        RebuildFacts facts,
        ShardProgressReading shard,
        long position,
        PositionFall fall,
        HealthEvaluation evaluation) => new()
    {
        Code = "D08",
        Severity = HealthSeverity.Warning,
        Title = "Shard " + shard.Shard + " went back and stopped part-way, and nothing in this studio did it",
        WhyItMatters =
            "The shard's position fell - a rebuild, rewind or reset run by something other than this studio - and it has not "
            + "moved for " + EventHealth.Duration(evaluation.Now - fall.LastChange) + ", short of the high-water mark. If that "
            + "was a rebuild, the projection's documents were deleted first and only the events up to the shard's position have "
            + "been replayed into them: readers see a partial projection until something replays the rest. Restarting the "
            + "shard continues it from its position; a rebuild here starts it again from the first event.",
        Subject = shard.Shard,
        Evidence = [.. Fallen(fall), .. Position(facts, shard, position)],
        Caveat = HistoryCaveat(fall) + " " + InProcessCaveat(facts),
        DocumentTypes = shard.DocumentTypes,
    };

    private static HealthFinding StudioStopped(
        RebuildFacts facts,
        ShardProgressReading shard,
        long position,
        PositionFall fall,
        StudioRebuild studio) => new()
    {
        Code = "D08",
        Severity = HealthSeverity.Warning,
        Title = "The rebuild of " + shard.Projection + " started from this studio ended " + studio.State + ", short of the high-water mark",
        WhyItMatters =
            "A rebuild deletes the projection's documents and its progression rows first, then replays from the first event. "
            + "This one stopped before the shard reached the high-water mark, so the projection holds only the events up to "
            + "the shard's position, and readers see a partial projection until something replays the rest. Restarting the "
            + "shard continues it from its position; another rebuild starts it again from the first event.",
        Subject = shard.Shard,
        Evidence =
        [
            new("Studio rebuild", "started " + EventHealth.Time(studio.StartedAt)
                + (studio.FinishedAt is { } finished ? ", ended " + EventHealth.Time(finished) : string.Empty) + ", " + studio.State),
            .. Fallen(fall),
            .. Position(facts, shard, position),
        ],
        Caveat = HistoryCaveat(fall),
        DocumentTypes = shard.DocumentTypes,
    };

    private static IEnumerable<HealthEvidence> Fallen(PositionFall fall)
    {
        yield return new HealthEvidence(
            "Position fell",
            "from #" + EventHealth.Count(fall.Before) + " to #" + EventHealth.Count(fall.After)
                + ", between " + EventHealth.Time(fall.From) + " and " + EventHealth.Time(fall.To));

        if (fall.RecoverAt > fall.Before)
        {
            yield return new HealthEvidence(
                "Back once it reaches",
                "#" + EventHealth.Count(fall.RecoverAt)
                    + (fall.RecoverAtMark ? ", the high-water mark when it fell" : ", the highest it had reached before it fell"));
        }

        yield return new HealthEvidence("Last moved", EventHealth.Time(fall.LastChange));
    }

    private static IEnumerable<HealthEvidence> Position(RebuildFacts facts, ShardProgressReading shard, long position)
    {
        yield return new HealthEvidence("Position now", shard.Position is null ? "no progression row" : "#" + EventHealth.Count(position));
        yield return new HealthEvidence("High-water mark", facts.HighWaterMark is { } mark ? "#" + EventHealth.Count(mark) : "no HighWaterMark row");

        if (shard.LastUpdated is { } updated)
        {
            yield return new HealthEvidence("Row last written", EventHealth.Time(updated) + " (server time)");
        }
    }

    private static string HistoryCaveat(PositionFall fall) =>
        "Seen in the advisor's own samples, which exist only while a Health page or the Overview is open: the fall happened "
        + "between " + EventHealth.Time(fall.From) + " and " + EventHealth.Time(fall.To) + ", and a fall while nobody watched is not seen.";

    private static string InProcessCaveat(RebuildFacts facts) =>
        "'This process' is the studio's own: its rebuild and rewind operations, and the in-process daemon's own states"
        + (facts.AskedDaemon ? " and IsRebuilding." : " (IsRebuilding needs Marten 9.50+ and the daemon in this process).")
        + " A daemon in another process publishes nothing here.";

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The shards whose latest in-process state was published while rebuilding, and not as stopped or paused.</summary>
    private static HashSet<string> RebuildingInTracker(HealthCheckContext context, IReadOnlyList<ShardProgressReading> readings)
    {
        HashSet<string> rebuilding = new(StringComparer.Ordinal);
        HashSet<string> wanted = new(readings.Select(static x => x.Shard), StringComparer.Ordinal);

        try
        {
            foreach (ShardState state in context.Database.Tracker.CurrentStates())
            {
                if (state.Mode == ShardMode.rebuilding
                    && wanted.Contains(state.ShardName)
                    && state.AgentStatus is not ("Stopped" or "Paused"))
                {
                    rebuilding.Add(state.ShardName);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The tracker is the daemon's; one that cannot be read tells this check nothing either way.
            rebuilding.Clear();
        }

        return rebuilding;
    }

    /// <summary>
    /// Asks the in-process daemon whether it is rebuilding the shards that are behind - Marten 9.50+ only, and only for
    /// those, so a caught-up store asks nothing.
    /// </summary>
    /// <remarks>
    /// Through <see cref="DaemonLightUp" />, the one place <c>IsRebuilding</c> is reached by reflection, as
    /// <c>DaemonCheckFacts</c> reaches it: a JasperFx that lacks it, or a daemon that throws from it, answers
    /// <see langword="false" />, which here proves nothing either way.
    /// </remarks>
    private static async Task<bool> AskDaemonAsync(
        HealthCheckContext context,
        IReadOnlyList<ShardProgressReading> readings,
        long? mark,
        HashSet<string> rebuilding,
        CancellationToken cancellationToken)
    {
        if (!DaemonLightUp.CanTellRebuilding
            || !RuntimeFeature.DaemonIsRebuilding.IsAvailable(context.Runtime)
            || context.Services.GetService<DaemonAccessor>() is not { } accessor)
        {
            return false;
        }

        List<ShardProgressReading> behind = [.. readings.Where(x => mark is not { } m || (x.Position ?? 0) < m)];
        if (behind.Count == 0)
        {
            return false;
        }

        DaemonHosting hosting = await SweptDaemon.HostingAsync(context, accessor).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return false;
        }

        if (context.Store.Options is not StoreOptions concrete)
        {
            return false;
        }

        Dictionary<string, ShardName> names = concrete.Projections.AllShards()
            .ToDictionary(static x => x.Name.Identity, static x => x.Name, StringComparer.Ordinal);

        foreach (ShardProgressReading shard in behind)
        {
            if (!names.TryGetValue(shard.Shard, out ShardName? name))
            {
                continue;
            }

            // A daemon that cannot answer has said nothing; false proves nothing anyway.
            if (DaemonLightUp.IsRebuilding(daemon, name))
            {
                rebuilding.Add(shard.Shard);
            }
        }

        return true;
    }

    /// <summary>The rebuilds this studio remembers against the scope's store and database.</summary>
    private static IReadOnlyList<StudioRebuild> StudioRebuilds(HealthCheckContext context)
    {
        if (context.Services.GetService<StudioOperationTracker>() is not { } tracker)
        {
            return [];
        }

        return
        [
            .. tracker.All()
                .Where(x => string.Equals(x.Kind, "Rebuild", StringComparison.Ordinal)
                    && Same(x.StoreKey, context.StoreKey)
                    && Same(x.DatabaseId, context.DatabaseId))
                .Select(static x => new StudioRebuild(x.Target, x.StartedAt, x.FinishedAt, x.State)),
        ];
    }

    /// <summary>The rewinds this studio ran against the scope's store and database, from the Activity ring.</summary>
    private static List<StudioRewind> StudioRewinds(HealthCheckContext context)
    {
        if (context.Services.GetService<StudioActionLogService>() is not { } log)
        {
            return [];
        }

        const string Separator = " to #";
        List<StudioRewind> rewinds = [];

        foreach (StudioActionLogEntry entry in log.GetLatest())
        {
            if (entry.Succeeded
                && string.Equals(entry.Action, EventDataService.RewindSubscriptionAction, StringComparison.Ordinal)
                && Same(entry.StoreKey, context.StoreKey)
                && Same(entry.DatabaseId, context.DatabaseId)
                && entry.Target.IndexOf(Separator, StringComparison.Ordinal) is var cut and > 0)
            {
                rewinds.Add(new StudioRewind(entry.Target[..cut], entry.Timestamp));
            }
        }

        return rewinds;
    }

    /// <summary>The document types the projection named <paramref name="projection" /> writes.</summary>
    private static IReadOnlyList<Type> PublishedTypes(StoreOptions options, string projection)
    {
        try
        {
            return
            [
                .. options.Projections.All
                    .Where(x => Same(x.Name, projection))
                    .SelectMany(static x => x.PublishedTypes())
                    .Distinct(),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }
}
