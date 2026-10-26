using MartenStudio.Internal.Sql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D09 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Reading">The high-water reading: the sequence, the mark and the highest committed event, in one snapshot.</param>
/// <param name="Shards">The registered async shards' progression rows.</param>
/// <param name="Database">
/// The swept database by its studio identity (<see cref="HealthCheckContext.DatabaseId" />), the name the scope selector
/// gives it: where a repair runs. Never a connection string.
/// </param>
internal sealed record ProgressionAheadFacts(
    DaemonCheckConfiguration Configuration,
    HighWaterReading Reading,
    IReadOnlyList<DaemonShardRow> Shards,
    string Database);

/// <summary>Which remedy a mark-ahead finding gets (<see cref="ProgressionAheadOfSequenceCheck.RemedyFor" />).</summary>
internal enum MarkAheadRemedy
{
    /// <summary>Marten's own correction: the mark is above <c>last_value</c>, and the sequence is not behind the events.</summary>
    Correction,

    /// <summary>
    /// The sequence-behind repair alone, which moves the sequence past the events and past the mark: the sequence is behind
    /// the events too, so Marten's correction would move progression back to below events already processed.
    /// </summary>
    OneRepair,

    /// <summary>
    /// The repair after a <c>RESTART</c> the correction can do nothing more about, guarded to run only while nothing has
    /// drawn from the sequence: the mark is at <c>last_value</c>, and the sequence is not behind the events.
    /// </summary>
    GuardedRepair,
}

/// <summary>
/// D09: the event sequence and what counts on it disagree - the daemon's progression is ahead of the numbers the sequence
/// hands out, or the sequence is behind the events already committed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three kinds</b>, its variants (<see cref="HealthFinding.Variant" />), each Critical:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="MarkAhead" />: the <c>HighWaterMark</c> row is above the sequence's highest number handed out. New events
/// take numbers the daemon has already passed, so no async projection or subscription sees them.
/// </description></item>
/// <item><description>
/// <see cref="ShardAhead" />: a registered shard is - and the mark is not. Folded into the first otherwise, which names
/// the shards with it: one cause, one finding.
/// </description></item>
/// <item><description>
/// <see cref="SequenceBehind" />: <c>max(seq_id)</c> is at or above the number the sequence hands out next, so the next
/// append draws a number <c>mt_events</c> already holds and fails on its primary key (23505).
/// </description></item>
/// </list>
/// <para>
/// <b>The sequence is read as Marten reads it.</b> "Handed out" is Marten's own <c>AllocatedSequenceHigh</c> (#5091,
/// <c>HighWaterStatisticsDetector</c> at V9.39.0, v9.51.0 and V9.54.1): <c>last_value</c> once called, <c>last_value - 1</c> before.
/// The projections screen's <c>highest_sequence</c> reads a never-called sequence as 0, which after
/// <c>RESTART WITH n</c> would call every mark above zero ahead; this one reads <c>n - 1</c>. The sequence is not
/// transactional, so an append that drew a number at the instant of the read counts as handed out - which only ever
/// makes the sequence look further ahead, never behind: in a healthy store the mark, every shard and every committed event
/// are at or below it, and nothing fires.
/// </para>
/// <para>
/// <b>A mark ahead gets one of three remedies</b> (<see cref="RemedyFor" />). <c>store.Advanced.TryCorrectProgressInDatabaseAsync</c>
/// - the projections screen's Correct progression - sets every progression row but the allocation fence and the stuck-gap
/// clock to the sequence's <c>last_value</c>, and only while the <c>HighWaterMark</c> row is above it
/// (<c>HighWaterDetector</c> and <c>AdvancedOperations</c>, identical at V9.39.0, v9.51.0 and V9.54.1). It is the remedy
/// only while the sequence is not also behind the events: when it is - the commonest state, a <c>RESTART</c> under a
/// table full of events - the correction would move progression back below events the daemon has already processed, so
/// the remedy is the sequence-behind repair alone, which moves the sequence past the events and past the mark in one
/// statement. Right after a <c>RESTART</c> over an empty table the mark can sit exactly at <c>last_value</c>, the number
/// handed out next: the correction has done all it can, and the remedy is the same statement, guarded to do nothing once
/// anything has drawn from the sequence.
/// </para>
/// <para>
/// <b>The sequence repair is computed when it runs.</b> It is the statement <see cref="HealthDaemonQueries.SequenceRepair" />
/// builds: the greater of the mark and <c>max(seq_id)</c>, both read by the statement itself and only ever forward, so
/// pasting it after more events have committed is as safe as running it at once. It is a one-off repair
/// (<see cref="HealthRemedyKind.Repair" />) for whoever runs the database, which the studio never runs, and it names the
/// database it runs in by the studio's identity for it.
/// </para>
/// <para>
/// <b>A shard ahead</b> is not brought back by either: a rebuild resets it (emptying the projection's tables first), or a
/// rewind to <see cref="Floor" />, the lower of the mark and the highest number handed out - where a repair runs first,
/// handed out as the repair leaves the sequence, which is the mark itself. JasperFx replays from the number after the floor
/// it is given, and a floor below what a subscription has already processed repeats its side effects.
/// </para>
/// <para>
/// <b>Progression only for a store with async work</b>, as D02, D03, D05, D07 and D08 judge it: with no async projection
/// or subscription registered, mark-ahead and shard-ahead are not judged, and sequence-behind - an append that fails - still
/// is. <b>Not for a store that partitions its events by tenant</b>, as D01-D04: every tenant draws from a sequence of its
/// own there, and the store-global rows are not the ones that count.
/// </para>
/// </remarks>
[HealthCheck(
    "D09",
    HealthArea.Daemon,
    HealthCostTier.Catalog,
    "Event sequence and daemon progression disagree",
    Gate = HealthGate.Database,
    Variants = [MarkAhead, ShardAhead, SequenceBehind])]
internal sealed class ProgressionAheadOfSequenceCheck : HealthCheck<ProgressionAheadFacts>
{
    /// <summary>The high-water mark is ahead of the sequence.</summary>
    internal const string MarkAhead = "mark-ahead";

    /// <summary>A registered shard is ahead of the sequence, and the mark is not.</summary>
    internal const string ShardAhead = "shard-ahead";

    /// <summary>The sequence is behind the highest committed event.</summary>
    internal const string SequenceBehind = "sequence-behind";

    /// <summary>What every finding says about where this comes from.</summary>
    internal const string Causes =
        " It is what a reset of the sequence leaves behind - ALTER SEQUENCE ... RESTART, or a TRUNCATE of mt_events with "
        + "RESTART IDENTITY - and so do a restore of mt_events, of its sequence or of mt_event_progression from another point in "
        + "time than the others, and a cut-over to a database logical replication filled: it copies rows, and before "
        + "PostgreSQL 19 not a sequence's position.";

    /// <inheritdoc />
    public override async Task<ProgressionAheadFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);

        if (configuration.TenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(DaemonCheckConfiguration.TenantPartitionedReason);
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        if (!shape.HasHighWater)
        {
            throw new HealthCheckUnavailableException(EventStoreRelations.NoEventStoreReason);
        }

        HighWaterReading reading = await HighWaterReading.ReadAsync(context, configuration.EventSchema, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DaemonShardRow> shards = await DaemonShardRow
            .ReadAsync(context, shape, configuration, configuration.ExtendedProgressionTracking, cancellationToken)
            .ConfigureAwait(false);

        return new ProgressionAheadFacts(configuration, reading, shards, context.DatabaseId);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ProgressionAheadFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        HighWaterReading reading = facts.Reading;
        DaemonCheckConfiguration configuration = facts.Configuration;

        if (reading.AllocatedHigh is not { } allocated)
        {
            return HealthVerdict.Quiet("The sequence's position was not read, so it is not compared with anything.");
        }

        long next = allocated + 1;
        string sequence = SchemaKey.For(configuration.EventSchema, HealthDaemonQueries.EventsSequence);
        List<HealthFinding> findings = [];

        // Any store with events: an append fails whether or not anything projects them.
        bool behind = reading.HighestCommitted is { } committed && committed >= next;

        if (behind)
        {
            findings.Add(Behind(facts, sequence, next, reading.HighestCommitted.GetValueOrDefault()));
        }

        // Progression only counts for a store with async work, as D02, D03, D05, D07 and D08 judge it: with no async
        // projection or subscription registered, a high-water row is nothing a reader waits on.
        if (configuration.AsyncShards.Count == 0)
        {
            return findings.Count > 0
                ? new HealthVerdict(findings)
                : HealthVerdict.Quiet("The store registers no async projection or subscription, so only the sequence and the events are compared.");
        }

        // Only the registered shards a visitor may be told of: a projection of a hidden document type is never named.
        List<DaemonShardRow> ahead = [.. facts.Shards
            .Where(x => x.Sequence > allocated
                && configuration.AsyncShards.Contains(x.Name, StringComparer.Ordinal)
                && !configuration.IsHidden(x.Name))
            .OrderBy(static x => x.Name, StringComparer.Ordinal)];

        if (reading.Mark is { } mark && mark > allocated)
        {
            // Marten's correction acts only while the mark is above the sequence's last_value; right after a RESTART, before
            // anything is appended, the mark can sit exactly at it - one past what was handed out - and then it has done
            // all it can (HighWaterDetector.TryCorrectProgressInDatabaseAsync, V9.39.0, v9.51.0 and V9.54.1).
            bool corrects = reading.SequenceLastValue is not { } lastValue || mark > lastValue;

            // A shard past the mark is brought back by no sequence repair. It is rewound after the repair, which leaves the
            // sequence at the greater of the mark and the highest event - so the floor is the mark itself.
            List<DaemonShardRow> aboveMark = [.. ahead.Where(x => x.Sequence > mark)];
            long floor = Floor(mark, Math.Max(mark, reading.HighestCommitted ?? 0) + 1);

            HealthRemedy remedy = RemedyFor(corrects, behind) switch
            {
                MarkAheadRemedy.Correction => Correction,
                MarkAheadRemedy.OneRepair => OneRepair(facts, mark, corrects, aboveMark, floor),
                _ => GuardedRepair(facts, mark, aboveMark, floor),
            };

            findings.Add(MarkIsAhead(facts, sequence, next, mark, ahead, remedy));
        }
        else
        {
            findings.AddRange(ahead.Select(x => ShardIsAhead(configuration, reading, sequence, next, x, Floor(reading.Mark, next))));
        }

        return findings.Count > 0 ? new HealthVerdict(findings) : HealthVerdict.Quiet();
    }

    /// <summary>
    /// Which remedy a mark ahead gets. The sequence behind the events decides it first: Marten's correction would then move
    /// progression back below events already processed, and the sequence repair puts the sequence past the events and the
    /// mark alike. Otherwise the correction, while it can still act, and the guarded repair once it cannot.
    /// </summary>
    /// <param name="corrects">Whether Marten's correction would act: the mark is above <c>last_value</c>, or that was not read.</param>
    /// <param name="behind">Whether the highest committed event is at or above the number the sequence hands out next.</param>
    internal static MarkAheadRemedy RemedyFor(bool corrects, bool behind) =>
        behind ? MarkAheadRemedy.OneRepair : corrects ? MarkAheadRemedy.Correction : MarkAheadRemedy.GuardedRepair;

    /// <summary>
    /// Where a shard ahead of the sequence is rewound to: the lower of the high-water mark - no shard is rightly past it -
    /// and the highest number the sequence has handed out, or that number alone when there is no mark. JasperFx replays
    /// from the number after the floor it is given.
    /// </summary>
    /// <param name="mark">The high-water mark, or <see langword="null" />.</param>
    /// <param name="next">The number the sequence hands out next - after a repair, as the repair leaves it.</param>
    internal static long Floor(long? mark, long next) => mark is { } m ? Math.Min(m, next - 1) : next - 1;

    /// <summary>
    /// What a rewind to <paramref name="floor" /> leaves behind and what it repeats: it replays from the number after it, so
    /// an event appended since the reset at or below it is replayed only by a rebuild, and one at or below what a
    /// subscription has already processed is handled again.
    /// </summary>
    private static string RewindExplanation(long floor) =>
        "A rebuild empties the projection's tables and then replays it from the first event to the mark, which leaves the shard "
        + "where the events are. A rewind replays from the number after the one it is given - from "
        + DaemonCheckConfiguration.Number(floor + 1) + " - so events appended since the reset at or below "
        + DaemonCheckConfiguration.Number(floor) + " are not replayed by it; a rebuild is the way to those. A floor below what "
        + "a subscription has already processed repeats its side effects: every event it replays is handled a second time.";

    /// <summary>Where a repair runs: the database this finding is about, by the studio's name for it.</summary>
    private static string WhereToRun(string database) =>
        string.IsNullOrWhiteSpace(database)
            ? "Run it in the database this finding is about, and in no other: each database has its own sequence and its own events."
            : "Run it in the database this finding is about - " + database + ", as the studio names it - and in no other: each "
              + "database has its own sequence and its own events.";

    private const string ComputedWhenRun =
        "It reads the mark and max(seq_id) when it runs, so running it later than this sweep is as safe as running it now, and "
        + "it never moves the sequence back.";

    private static List<HealthEvidence> Common(HighWaterReading reading, string sequence, long next) =>
    [
        new("Sequence", sequence),
        new("Next number it hands out", DaemonCheckConfiguration.Number(next)),
        new("Highest committed seq_id", reading.HighestCommitted is { } highest
            ? DaemonCheckConfiguration.Number(highest)
            : "none: mt_events holds no event"),
        new("High-water mark", reading.Mark is { } mark ? DaemonCheckConfiguration.Number(mark) : "not written yet"),
    ];

    private const string SnapshotCaveat =
        "Read in one snapshot: the sequence's last_value and is_called, max(seq_id) of mt_events and the progression rows. A "
        + "sequence is not transactional, so an append that drew a number at that instant counts as handed out - which can only "
        + "make the sequence look further ahead, never behind.";

    private static HealthFinding Behind(ProgressionAheadFacts facts, string sequence, long next, long highest) =>
        new()
        {
            Code = "D09",
            Variant = SequenceBehind,
            Severity = HealthSeverity.Critical,
            Subject = sequence,
            Title = "The event sequence is behind the events: it hands out " + DaemonCheckConfiguration.Number(next)
                + " next, and mt_events already holds events up to " + DaemonCheckConfiguration.Number(highest),
            WhyItMatters =
                "Every append draws its seq_id from the sequence, so the next one draws a number mt_events already holds and fails "
                + "with a unique violation (23505) on its primary key - and rolls back, having burnt the number, so appends go on "
                + "failing one at a time until the sequence has passed every event. Where the events are partitioned by archive "
                + "status, a number only an archived event holds is no conflict at all, and two events then share it." + Causes,
            Evidence = Common(facts.Reading, sequence, next),
            Caveat = SnapshotCaveat,
            Remedy = HealthRemedy.ForRepair(
                "-- With appends stopped: move mt_events_sequence past the highest seq_id in mt_events, and past the\n"
                + "-- high-water mark where that is further on - both read when this runs. It never moves the sequence back.\n"
                + Repair(facts.Configuration, onlyWhileNeverCalled: false),
                "The next append then takes the number after the highest event, or after the mark where that is further on. Stop "
                + "appends first: one that commits between reading the maximum and moving the sequence would be overtaken. "
                + ComputedWhenRun + " Because it moves the sequence past the high-water mark too, a mark-ahead finding beside this "
                + "one needs nothing more for the mark - and not Correct progression, which would set progression back to the "
                + "sequence's position, behind the events; a shard still past the mark keeps the rebuild or rewind D09 gives it. "
                + WhereToRun(facts.Database)),
        };

    /// <summary>
    /// The repair statement (<see cref="HealthDaemonQueries.SequenceRepair" />), or - for a schema whose name it will not
    /// print - the same repair in words.
    /// </summary>
    private static string Repair(DaemonCheckConfiguration configuration, bool onlyWhileNeverCalled) =>
        HealthDaemonQueries.SequenceRepair(configuration.EventSchema, onlyWhileNeverCalled)
        ?? "-- setval the sequence to the greater of the high-water mark and max(seq_id) of mt_events, both read at that moment"
           + (onlyWhileNeverCalled ? ",\n-- and only while it still reads is_called = false." : ".");

    private static HealthFinding MarkIsAhead(
        ProgressionAheadFacts facts,
        string sequence,
        long next,
        long mark,
        List<DaemonShardRow> ahead,
        HealthRemedy remedy)
    {
        List<HealthEvidence> evidence = Common(facts.Reading, sequence, next);

        if (ahead.Count > 0)
        {
            evidence.Add(new HealthEvidence("Shards ahead of the sequence", string.Join(", ", ahead.Select(static x =>
                x.Name + " at " + DaemonCheckConfiguration.Number(x.Sequence)))));
        }

        return new HealthFinding
        {
            Code = "D09",
            Variant = MarkAhead,
            Severity = HealthSeverity.Critical,
            Subject = HealthDaemonQueries.HighWaterMarkRow,

            // The shards it names, whose projections are visible ones: a hidden one is never in the list at all.
            DocumentTypes = [.. ahead.SelectMany(x => facts.Configuration.PublishedTypesOf(x.Name)).Distinct()],
            Title = "The high-water mark (" + DaemonCheckConfiguration.Number(mark) + ") is ahead of the event sequence, which hands out "
                + DaemonCheckConfiguration.Number(next) + " next: new events take numbers the daemon has already passed",
            WhyItMatters =
                "The daemon processes only events above the high-water mark and above each shard's own position, so every event "
                + "appended now - until the sequence passes " + DaemonCheckConfiguration.Number(mark) + " again - is one no async "
                + "projection or subscription in this database will ever see, and nothing reports it as lost." + Causes,
            Evidence = evidence,
            Caveat = SnapshotCaveat,

            // The projections screen is where Correct progression, a rebuild and a rewind are; a repair that needs none of
            // them does not send anybody to the button it says not to press.
            Link = remedy.Kind != HealthRemedyKind.Repair || remedy.Code.Contains("Capabilities.RebuildProjections", StringComparison.Ordinal)
                ? new HealthLink("Open the projections screen", "projections")
                : null,
            Remedy = remedy,
        };
    }

    private static HealthFinding ShardIsAhead(
        DaemonCheckConfiguration configuration,
        HighWaterReading reading,
        string sequence,
        long next,
        DaemonShardRow shard,
        long floor)
    {
        List<HealthEvidence> evidence = Common(reading, sequence, next);
        evidence.Add(new HealthEvidence("Shard position", DaemonCheckConfiguration.Number(shard.Sequence)));

        return new HealthFinding
        {
            Code = "D09",
            Variant = ShardAhead,
            Severity = HealthSeverity.Critical,
            Subject = shard.Name,
            DocumentTypes = configuration.PublishedTypesOf(shard.Name),
            Title = "Shard " + shard.Name + " (" + DaemonCheckConfiguration.Number(shard.Sequence) + ") is ahead of the event sequence, "
                + "which hands out " + DaemonCheckConfiguration.Number(next) + " next: it will not see the next events",
            WhyItMatters =
                "A shard processes only events above its own position, so every event appended now - until the sequence passes "
                + DaemonCheckConfiguration.Number(shard.Sequence) + " again - is one this projection or subscription never sees, "
                + "and nothing reports it as lost. The high-water mark is not ahead, so this one shard's row is." + Causes,
            Evidence = evidence,
            Caveat = SnapshotCaveat,
            Link = new HealthLink("Open the projections screen", "projections"),
            Remedy = new HealthRemedy(
                "// Rebuild this projection on the projections screen (Capabilities.RebuildProjections),\n"
                + "// or rewind the subscription to " + DaemonCheckConfiguration.Number(floor) + ".",
                "Correct progression does nothing here: Marten corrects progression only while the high-water mark itself is "
                + "ahead of the sequence (TryCorrectProgressInDatabaseAsync), and the mark is not. The rewind's floor is the "
                + "high-water mark - no shard is rightly past it - or the highest number the sequence has handed out, where that "
                + "is lower or there is no mark. " + RewindExplanation(floor)),
        };
    }

    /// <summary>Marten's correction, as the projections screen runs it - offered only while the sequence is not behind the events.</summary>
    private static HealthRemedy Correction => new(
        "// The projections screen's Correct progression (Capabilities.CorrectProgression) runs exactly this:\n"
        + "await store.Advanced.TryCorrectProgressInDatabaseAsync(cancellationToken);",
        "Marten sets the high-water mark and every shard's progression back to the sequence's last_value - its own "
        + "bookkeeping rows aside - and does so only while the mark is above it. Every event appended since the reset at a "
        + "number at or below that value is then treated as processed: rebuild the async projections that should hold "
        + "them - a rebuild empties the projection's tables before it replays. Right after a RESTART, before anything is "
        + "appended, last_value is the number the sequence hands out next, so the correction leaves the mark one past what has "
        + "been handed out, and D09 then says what is left to do. Should the sequence fall behind the events as well, D09 "
        + "offers the sequence repair in place of this. On a store with a database per tenant, "
        + "TryCorrectProgressInDatabaseAsync(tenantId, cancellationToken) reaches only that tenant's database.");

    /// <summary>
    /// The sequence behind the events as well as the mark ahead: the sequence-behind repair alone, which moves the sequence
    /// past both - and never Marten's correction, which would move progression back below events already processed.
    /// </summary>
    /// <param name="facts">What D09 read.</param>
    /// <param name="mark">The high-water mark.</param>
    /// <param name="corrects">Whether Marten's correction would still act - which is why this says not to run it.</param>
    /// <param name="aboveMark">The shards ahead of the mark itself, which the sequence repair does not bring back.</param>
    /// <param name="floor">Where such a shard is rewound to, once the repair has run.</param>
    private static HealthRemedy OneRepair(ProgressionAheadFacts facts, long mark, bool corrects, List<DaemonShardRow> aboveMark, long floor)
    {
        long highest = facts.Reading.HighestCommitted ?? mark;

        string code =
            "-- One repair for both of D09's findings, with appends stopped - not Correct progression: the sequence-behind\n"
            + "-- repair moves mt_events_sequence past the highest seq_id and past the high-water mark alike, both read when\n"
            + "-- this runs. It never moves the sequence back.\n"
            + Repair(facts.Configuration, onlyWhileNeverCalled: false);

        string explanation =
            (corrects
                ? "Do not run Correct progression here. Marten's correction would set the high-water mark and every shard back to "
                  + "the sequence's last_value" + (facts.Reading.SequenceLastValue is { } lastValue ? " (" + DaemonCheckConfiguration.Number(lastValue) + ")" : string.Empty)
                  + ", and every event above it up to the mark - events the daemon has already processed - would be processed "
                  + "again. "
                : "Correct progression has done what it can: Marten moves progression only while the mark is above the sequence's "
                  + "last_value, and the mark is at it - the number the sequence hands out next, because nothing has drawn from it "
                  + "since it was reset. ")
            + "The events reach " + DaemonCheckConfiguration.Number(highest) + ", at or past the mark, so moving the sequence past "
            + "them - the repair D09's sequence-behind finding offers - also moves it past the mark: one repair for both findings, "
            + "not two. " + ComputedWhenRun + " " + WhereToRun(facts.Database);

        return HealthRemedy.ForRepair(code + ShardLines(aboveMark, floor), explanation + ShardExplanation(aboveMark, floor));
    }

    /// <summary>
    /// What is left when Marten's correction can do no more and the sequence is not behind the events: the mark sits at the
    /// sequence's <c>last_value</c>, which a sequence that has not been called since a <c>RESTART</c> hands out next. The
    /// repair moves the sequence to the greater of the mark and the highest committed event, both read when it runs, and
    /// only while nothing has drawn from it. Shards above the mark keep their own advice.
    /// </summary>
    /// <param name="facts">What D09 read.</param>
    /// <param name="mark">The high-water mark, equal to the number handed out next.</param>
    /// <param name="aboveMark">The shards ahead of the mark itself, which the sequence repair does not bring back.</param>
    /// <param name="floor">Where such a shard is rewound to, once the repair has run.</param>
    private static HealthRemedy GuardedRepair(ProgressionAheadFacts facts, long mark, List<DaemonShardRow> aboveMark, long floor)
    {
        string markText = DaemonCheckConfiguration.Number(mark);

        string code =
            "-- With appends stopped, and only while mt_events_sequence still reads is_called = false:\n"
            + "-- setval it to the greater of the high-water mark and max(seq_id) of mt_events, both read when this runs.\n"
            + Repair(facts.Configuration, onlyWhileNeverCalled: true);

        string explanation =
            "Correct progression has done what it can: Marten moves progression only while the mark is above the sequence's "
            + "last_value, and the mark is at it - the number the sequence hands out next, because nothing has drawn from it since "
            + "it was reset. Left as it is, the next event appended takes " + markText + ", which the daemon treats as already "
            + "processed: no async projection or subscription sees it, and nothing reports it lost. The statement moves the "
            + "sequence so the next append takes a number above the mark and above every committed event. It does nothing once "
            + "anything has drawn from the sequence: by then the event at " + markText + " has been taken, and the projections "
            + "that should hold it need a rebuild - which empties their tables before it replays. " + ComputedWhenRun + " "
            + WhereToRun(facts.Database);

        return HealthRemedy.ForRepair(code + ShardLines(aboveMark, floor), explanation + ShardExplanation(aboveMark, floor));
    }

    /// <summary>The rebuild-or-rewind lines for shards past the mark, after a repair - or nothing.</summary>
    private static string ShardLines(List<DaemonShardRow> aboveMark, long floor)
    {
        if (aboveMark.Count == 0)
        {
            return string.Empty;
        }

        bool one = aboveMark.Count == 1;

        return "\n-- Then, for the " + (one ? "shard" : "shards") + " past the mark, which no sequence repair brings back: rebuild "
            + (one ? "its projection" : "their projections") + "\n-- on the projections screen (Capabilities.RebuildProjections), or rewind "
            + (one ? "the subscription" : "each subscription") + " to " + DaemonCheckConfiguration.Number(floor) + ".";
    }

    /// <summary>Which shards are past the mark, and what a rewind to <paramref name="floor" /> does - or nothing.</summary>
    private static string ShardExplanation(List<DaemonShardRow> aboveMark, long floor) =>
        aboveMark.Count == 0
            ? string.Empty
            : " Past the mark itself: " + string.Join(", ", aboveMark.Select(static x => x.Name + " at " + DaemonCheckConfiguration.Number(x.Sequence)))
              + ". After the repair the sequence has handed out at least the mark, so the floor is the mark: "
              + DaemonCheckConfiguration.Number(floor) + ". " + RewindExplanation(floor);
}
