using JasperFx.Core.Reflection;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;

namespace MartenStudio.Services.Health.Checks;

/// <summary>An aggregate this store only ever builds live, by replaying its stream.</summary>
/// <param name="Name">Its type's name in code, for the remedy.</param>
/// <param name="Registered">
/// Whether it is registered (<c>LiveStreamAggregation&lt;T&gt;()</c> or a live <c>SingleStreamProjection</c>), rather
/// than only aggregated live by code in this process.
/// </param>
internal sealed record LiveAggregate(string Name, bool Registered);

/// <summary>What E06 read.</summary>
/// <param name="LiveOnly">The visible aggregates nothing snapshots, by name.</param>
/// <param name="HiddenLiveOnly">How many more there are that <c>IsDocumentTypeVisible</c> hides, and so are never named.</param>
/// <param name="Streams">What the planner's statistics say about <c>mt_streams</c>.</param>
internal sealed record LongStreamsFacts(IReadOnlyList<LiveAggregate> LiveOnly, int HiddenLiveOnly, TableStatistics Streams);

/// <summary>
/// E06: an aggregate is built only live, and the store has long streams.
/// </summary>
/// <remarks>
/// <para>
/// A live aggregation - <c>AggregateStreamAsync&lt;T&gt;</c>, or <c>FetchForWriting</c> / <c>FetchLatest</c> of a type
/// no inline or async projection keeps - replays the stream from its first event on every call. Snapshotting it
/// (<c>opts.Projections.Snapshot&lt;T&gt;(SnapshotLifecycle.Inline)</c>, verified at the floor) keeps the aggregate as a
/// document instead, and stream compaction folds an old stream into one event.
/// </para>
/// <para>
/// <b>Tier 1, no scan.</b> The aggregates come from the configuration and from what this process has aggregated live
/// (<c>ProjectionOptions.AllAggregateTypes()</c>); the stream lengths from <c>pg_stats.histogram_bounds</c> of
/// <c>mt_streams.version</c>, shared with E05 and E07. The statistics describe every stream together and cannot say
/// which aggregate a long stream belongs to, so the finding is an Info and says so. The 20 longest streams are a
/// scan of <c>mt_streams</c>, which this check never runs.
/// </para>
/// </remarks>
[HealthCheck("E06", HealthArea.Events, HealthCostTier.Catalog, "Long streams without a snapshot")]
internal sealed class LongStreamsWithoutSnapshotCheck : HealthCheck<LongStreamsFacts>
{
    /// <summary>A stream with more events than this is long for a live aggregation.</summary>
    internal const long LongStream = 1_000;

    /// <inheritdoc />
    public override async Task<LongStreamsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        List<LiveAggregate> visible = [];
        int hidden = 0;

        if (context.Store.Options is StoreOptions concrete)
        {
            foreach ((Type type, bool registered) in LiveOnly(concrete))
            {
                if (context.IsDocumentTypeVisible(type))
                {
                    visible.Add(new LiveAggregate(type.ShortNameInCode(), registered));
                }
                else
                {
                    hidden++;
                }
            }
        }

        if (visible.Count == 0)
        {
            // Nothing to name, so nothing to read: the statement is not run.
            return new LongStreamsFacts([], hidden, TableStatistics.Missing(HealthEventQueries.StreamsTable));
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        if (shape.Streams is null)
        {
            throw new HealthCheckUnavailableException("The event store's tables do not exist in this database yet, so there are no streams to measure.");
        }

        EventStatistics statistics = await EventHealth.StatisticsAsync(context, cancellationToken).ConfigureAwait(false);
        TableStatistics streams = statistics.Streams;

        if (!streams.Readable)
        {
            throw new HealthCheckUnavailableException(
                "The role the studio reads as may not select from mt_streams, so PostgreSQL shows it none of that table's statistics.");
        }

        if (!streams.HasColumn("version"))
        {
            throw new HealthCheckUnavailableException(
                "PostgreSQL holds no statistics for mt_streams yet: it has not been analysed since its streams arrived.");
        }

        return new LongStreamsFacts([.. visible.OrderBy(static x => x.Name, StringComparer.Ordinal)], hidden, streams);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(LongStreamsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.LiveOnly.Count == 0)
        {
            return HealthVerdict.Quiet(facts.HiddenLiveOnly > 0
                ? null
                : "Every aggregate this process knows of is kept by an inline or async projection; none is replayed live.");
        }

        double? longest = facts.Streams.LargestSeen("version");

        if (longest is not { } seen || seen <= LongStream)
        {
            return HealthVerdict.Quiet(
                "The longest stream the statistics of mt_streams saw has " + EventHealth.Count(longest ?? 0) + " events.");
        }

        double over = facts.Streams.EstimatedAbove("version", LongStream);
        string overText = over >= 1 ? "about " + EventHealth.Count(over) : "at least one";

        List<HealthEvidence> evidence =
        [
            new("Streams over " + EventHealth.Count(LongStream) + " events", overText),
            new("Longest stream the statistics saw", EventHealth.Count(seen) + " events"),
            new("Streams", facts.Streams.EstimatedRows is { } rows ? "about " + EventHealth.Count(rows) : "unknown"),
            new("Aggregated only live", string.Join(", ", facts.LiveOnly.Select(static x => x.Name + (x.Registered ? string.Empty : " (aggregated in code, not registered)")))),
        ];

        LiveAggregate first = facts.LiveOnly[0];

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E06",
            Severity = HealthSeverity.Info,
            Title = "Some streams are over " + EventHealth.Count(LongStream) + " events long, and "
                + EventHealth.Count(facts.LiveOnly.Count) + " aggregate" + (facts.LiveOnly.Count == 1 ? " is" : "s are")
                + " rebuilt from every event on each fetch",
            WhyItMatters =
                "A live aggregation replays the stream from its first event every time it is asked - AggregateStreamAsync, and "
                + "FetchForWriting or FetchLatest of a type no inline or async projection keeps. Its cost grows with the stream, "
                + "on every request, and a stream that only ever grows makes it worse over time. A snapshot keeps the aggregate "
                + "as a document, updated as events arrive, so a fetch reads one row and only the events after it; stream "
                + "compaction (on the stream's page, behind CompactStreams) folds an old stream's history into one event.",
            Evidence = evidence,
            Caveat = EventHealth.StatisticsCaveat(facts.Streams)
                + " The statistics describe every stream together and cannot say which aggregate a long stream belongs to - it may "
                + "be one that is already snapshotted. Aggregates this process has never fetched live are not known to it.",
            Remedy = new HealthRemedy(
                "opts.Projections.Snapshot<" + first.Name + ">(SnapshotLifecycle.Inline);",
                "Adds the aggregate's document table and keeps it in the appending transaction; on an existing store, rebuild the "
                + "snapshot projection once to fill it. SnapshotLifecycle.Async keeps it off the append path instead, at the cost "
                + "of a fetch that may lag."),
            Link = new HealthLink("Open Streams", EventLinks.StreamsRoute),
        });
    }

    /// <summary>
    /// The aggregates no inline or async projection keeps: every type <c>AllAggregateTypes()</c> lists - registered
    /// projections, live sources and what this process has aggregated live - whose every aggregate registration is
    /// <c>Live</c>.
    /// </summary>
    private static IEnumerable<(Type Type, bool Registered)> LiveOnly(StoreOptions options)
    {
        List<IAggregateProjection> projections = [.. options.Projections.All.OfType<IAggregateProjection>()];

        foreach (Type type in options.Projections.AllAggregateTypes().Distinct())
        {
            bool registered = false;
            bool kept = false;

            foreach (IAggregateProjection projection in projections.Where(x => x.AggregateType == type))
            {
                registered = true;
                kept |= projection.Lifecycle != ProjectionLifecycle.Live;
            }

            if (!registered && options.Projections.TryFindAggregate(type, out IAggregateProjection? found))
            {
                registered = true;
                kept |= found.Lifecycle != ProjectionLifecycle.Live;
            }

            if (!kept)
            {
                yield return (type, registered);
            }
        }
    }
}
