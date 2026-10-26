using MartenStudio.Internal.Sql;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D09 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Reading">The high-water reading: the sequence, the mark and the highest committed event, in one snapshot.</param>
/// <param name="Shards">The registered async shards' progression rows.</param>
internal sealed record ProgressionAheadFacts(
    DaemonCheckConfiguration Configuration,
    HighWaterReading Reading,
    IReadOnlyList<DaemonShardRow> Shards);

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
/// <c>HighWaterStatisticsDetector</c> at V9.39.0 and v9.51.0): <c>last_value</c> once called, <c>last_value - 1</c> before.
/// The projections screen's <c>highest_sequence</c> reads a never-called sequence as 0, which after
/// <c>RESTART WITH n</c> would call every mark above zero ahead; this one reads <c>n - 1</c>. The sequence is not
/// transactional, so an append that drew a number at the instant of the read counts as handed out - which only ever
/// makes the sequence look further ahead, never behind: in a healthy store the mark, every shard and every committed event
/// are at or below it, and nothing fires.
/// </para>
/// <para>
/// <b>The remedies are Marten's own.</b> <c>store.Advanced.TryCorrectProgressInDatabaseAsync</c> - the projections screen's
/// Correct progression - sets every progression row but the allocation fence and the stuck-gap clock to the sequence's
/// <c>last_value</c>, and only while the <c>HighWaterMark</c> row is above it (<c>HighWaterDetector</c> and
/// <c>AdvancedOperations</c>, identical at V9.39.0 and v9.51.0): so it is the remedy for a mark ahead, and does nothing for a
/// shard ahead of a mark that is not, which a rebuild resets instead. A sequence behind the events is a DBA's
/// <c>setval</c>, said in words.
/// </para>
/// <para>
/// <b>Not for a store that partitions its events by tenant</b>, as D01-D04: every tenant draws from a sequence of its own
/// there, and the store-global rows are not the ones that count.
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

        return new ProgressionAheadFacts(configuration, reading, shards);
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

        if (reading.HighestCommitted is { } highest && highest >= next)
        {
            findings.Add(Behind(configuration, reading, sequence, next, highest));
        }

        // Only the registered shards a visitor may be told of: a projection of a hidden document type is never named.
        List<DaemonShardRow> ahead = [.. facts.Shards
            .Where(x => x.Sequence > allocated
                && configuration.AsyncShards.Contains(x.Name, StringComparer.Ordinal)
                && !configuration.IsHidden(x.Name))
            .OrderBy(static x => x.Name, StringComparer.Ordinal)];

        if (reading.Mark is { } mark && mark > allocated)
        {
            findings.Add(MarkIsAhead(configuration, reading, sequence, next, mark, ahead));
        }
        else
        {
            findings.AddRange(ahead.Select(x => ShardIsAhead(configuration, reading, sequence, next, x)));
        }

        return findings.Count > 0 ? new HealthVerdict(findings) : HealthVerdict.Quiet();
    }

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

    private static HealthFinding Behind(DaemonCheckConfiguration configuration, HighWaterReading reading, string sequence, long next, long highest)
    {
        string events = SchemaKey.For(configuration.EventSchema, EventTableInfo.EventsTable);

        return new HealthFinding
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
            Evidence = Common(reading, sequence, next),
            Caveat = SnapshotCaveat,
            Remedy = new HealthRemedy(
                "// A DBA's repair, not a StoreOptions line, with appends stopped:\n"
                + "// move " + sequence + " past the highest seq_id in " + events + " - setval it to that maximum.",
                "The next append then takes the number after the highest event. Stop appends first: one that commits between "
                + "reading the maximum and moving the sequence would be overtaken. If D09 also reports progression ahead of the "
                + "sequence, this comes first: the correction moves progression to the sequence's position, and that has to be "
                + "past the events."),
        };
    }

    private static HealthFinding MarkIsAhead(
        DaemonCheckConfiguration configuration,
        HighWaterReading reading,
        string sequence,
        long next,
        long mark,
        List<DaemonShardRow> ahead)
    {
        List<HealthEvidence> evidence = Common(reading, sequence, next);

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
            DocumentTypes = [.. ahead.SelectMany(x => configuration.PublishedTypesOf(x.Name)).Distinct()],
            Title = "The high-water mark (" + DaemonCheckConfiguration.Number(mark) + ") is ahead of the event sequence, which hands out "
                + DaemonCheckConfiguration.Number(next) + " next: new events take numbers the daemon has already passed",
            WhyItMatters =
                "The daemon processes only events above the high-water mark and above each shard's own position, so every event "
                + "appended now - until the sequence passes " + DaemonCheckConfiguration.Number(mark) + " again - is one no async "
                + "projection or subscription in this database will ever see, and nothing reports it as lost." + Causes,
            Evidence = evidence,
            Caveat = SnapshotCaveat,
            Link = new HealthLink("Open the projections screen", "projections"),
            Remedy = Correction,
        };
    }

    private static HealthFinding ShardIsAhead(
        DaemonCheckConfiguration configuration,
        HighWaterReading reading,
        string sequence,
        long next,
        DaemonShardRow shard)
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
                + "// or rewind the subscription to the number the sequence hands out next.",
                "Correct progression does nothing here: Marten corrects progression only while the high-water mark itself is "
                + "ahead of the sequence (TryCorrectProgressInDatabaseAsync), and the mark is not. A rebuild replays the "
                + "projection from the first event to the mark, which leaves the shard where the events are."),
        };
    }

    /// <summary>Marten's correction, as the projections screen runs it.</summary>
    private static HealthRemedy Correction => new(
        "// The projections screen's Correct progression (Capabilities.CorrectProgression) runs exactly this:\n"
        + "await store.Advanced.TryCorrectProgressInDatabaseAsync(cancellationToken);",
        "Marten sets the high-water mark and every shard's progression back to the sequence's last_value - its own "
        + "bookkeeping rows aside - and does so only while the mark is above it. Every event appended since the reset at a "
        + "number at or below that value is then treated as processed: rebuild the async projections that should hold "
        + "them. Right after a RESTART, before anything is appended, last_value is the number the sequence hands out next, "
        + "so the mark ends one past what it has handed out and the first event appended after it is treated as processed "
        + "too. If D09 also reports the sequence behind the events, fix that first, or the correction moves progression back "
        + "to a number the events have long passed and the daemon processes them all again. On a store with a database per "
        + "tenant, TryCorrectProgressInDatabaseAsync(tenantId, cancellationToken) reaches only that tenant's database.");
}
