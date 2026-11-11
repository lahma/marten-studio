using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;

namespace KStudio.Core.Windows;

/// <summary>
/// Resolves a time window to offsets per partition before anything is read (AGENTS.md D38, decision 6). It reads only list offsets,
/// metadata and configs.
/// </summary>
/// <remarks>
/// The start is offsets-for-times at the window's start: exact. The end is offsets-for-times at the window's end plus a margin under
/// <c>CreateTime</c>, and at the window's end under <c>LogAppendTime</c>; a record is a match only if its own timestamp is in the window,
/// so the margin widens the scan, never the window. Under <c>read_committed</c> a partition stops at its last stable offset. Everything
/// that could make "no match" untrue is a <see cref="WindowNote" />.
/// </remarks>
internal sealed class WindowResolver(
    KafkaWires wires,
    KafkaWireReads reads,
    KafkaEngineSnapshot options,
    KStudioLoggers loggers,
    TimeProvider timeProvider)
{
    /// <summary>Resolves a window.</summary>
    /// <param name="cluster">The cluster's key.</param>
    /// <param name="topics">The topics, already authorized by the host studio.</param>
    /// <param name="window">The window.</param>
    /// <param name="isolation">Whether uncommitted records are included.</param>
    /// <param name="margin">The margin past the end for <c>CreateTime</c> topics; the engine's default when <see langword="null" />.</param>
    /// <param name="cancellationToken">Abandons the resolution.</param>
    public async Task<ResolvedWindow> ResolveAsync(
        string cluster,
        IReadOnlyList<KafkaTopicName> topics,
        KafkaWindow window,
        KafkaIsolation isolation,
        TimeSpan? margin,
        CancellationToken cancellationToken)
    {
        if (window.Problem is { } problem)
        {
            throw new ArgumentException(problem, nameof(window));
        }

        TimeSpan effectiveMargin = margin ?? options.Limits.CreateTimeMargin;
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveMargin, TimeSpan.Zero, nameof(margin));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(effectiveMargin, TimeSpan.FromHours(1), nameof(margin));

        bool committed = isolation == KafkaIsolation.Committed;
        long fromMs = window.From.ToUnixTimeMilliseconds();
        long toMs = window.To.ToUnixTimeMilliseconds();
        long toWithMarginMs = toMs + (long) effectiveMargin.TotalMilliseconds;

        // The window's partitions are the ones this metadata names, so the time the note gives is read before it: a partition added after it
        // is not read, and a later time would say it was (REVIEW-ENG-A-FIX 6).
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Step 1 (partly): the topics' partitions and configs.
        WireResult<WireMetadata> metadata = await reads.FreshMetadataAsync(cluster, cancellationToken).ConfigureAwait(false);
        Task<WireResult<WireTopicConfig>>[] configTasks = [.. topics.Select(topic => reads.ConfigAsync(cluster, topic, cancellationToken))];
        WireResult<WireTopicConfig>[] configs = await Task.WhenAll(configTasks).ConfigureAwait(false);

        Dictionary<string, TopicState> states = new(StringComparer.Ordinal);

        for (int i = 0; i < topics.Count; i++)
        {
            KafkaTopicName topic = topics[i];
            WireTopicInfo? info = metadata.IsOk ? metadata.Value.Topics.FirstOrDefault(x => x.Name == topic.Value && x.Error is null) : null;
            WireError? refusal = metadata.Error ?? (info is null ? new WireError.NotFound(KafkaResourceType.Topic, topic.Value) : null);
            states[topic.Value] = new TopicState(topic.Value, info, refusal, configs[i]);
        }

        // Partitions to look up, by what the end of each depends on.
        List<WirePartition> all = [];
        List<WirePartition> createTime = [];
        List<WirePartition> appendTime = [];

        foreach (TopicState state in states.Values.Where(static x => x.Refusal is null))
        {
            foreach (WirePartitionInfo partition in state.Info!.Partitions)
            {
                WirePartition key = new(state.Topic, partition.Partition);
                all.Add(key);
                (state.Kind == KafkaTimestampKind.LogAppendTime ? appendTime : createTime).Add(key);
            }
        }

        // Step 2: one list-offsets call per spec, each batching every partition of every topic, all at once.
        IKafkaWire wire = wires.For(cluster);
        Task<WireResult<IReadOnlyList<WireOffset>>> earliestTask = Call(wire, cluster, all, WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> latestTask = Call(wire, cluster, all, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> stableTask = committed
            ? Call(wire, cluster, all, WireOffsetSpec.Latest, WireIsolation.ReadCommitted, cancellationToken)
            : Task.FromResult(WireResult<IReadOnlyList<WireOffset>>.Ok([]));
        Task<WireResult<IReadOnlyList<WireOffset>>> startTask = Call(wire, cluster, all, WireOffsetSpec.ForTimestamp(fromMs), WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> endCreateTask = Call(wire, cluster, createTime, WireOffsetSpec.ForTimestamp(toWithMarginMs), WireIsolation.ReadUncommitted, cancellationToken);
        Task<WireResult<IReadOnlyList<WireOffset>>> endAppendTask = Call(wire, cluster, appendTime, WireOffsetSpec.ForTimestamp(toMs), WireIsolation.ReadUncommitted, cancellationToken);

        await Task.WhenAll(earliestTask, latestTask, stableTask, startTask, endCreateTask, endAppendTask).ConfigureAwait(false);

        Lookup earliest = new(earliestTask.Result);
        Lookup latest = new(latestTask.Result);
        Lookup stable = new(stableTask.Result);
        Lookup start = new(startTask.Result);
        Lookup end = new(endCreateTask.Result, endAppendTask.Result);

        List<PartitionRange> ranges = [];
        List<TopicWindow> topicWindows = [];

        foreach (KafkaTopicName topic in topics)
        {
            TopicState state = states[topic.Value];
            List<WindowNote> topicNotes = [];

            if (state.Config.Error is { } configError)
            {
                topicNotes.Add(new WindowNote.ConfigNotReadable(state.Topic, configError));
            }

            if (state.Refusal is { } refusal)
            {
                topicWindows.Add(new TopicWindow(state.Topic, state.Kind, refusal, topicNotes));
                continue;
            }

            // A refusal of the look-ups themselves (the principal may not describe the topic) refuses the topic.
            WireError? lookupRefusal = FirstTopicError(state, earliest, latest, stable, start, end);

            if (lookupRefusal is not null)
            {
                topicWindows.Add(new TopicWindow(state.Topic, state.Kind, lookupRefusal, topicNotes));
                continue;
            }

            if (state.ConfigView is { IsCompacted: true })
            {
                topicNotes.Add(new WindowNote.CompactedTopic(state.Topic));
            }

            if (state.ConfigView is { RemoteStorageEnabled: true, LocalRetentionMs: >= 0 } tiered
                && window.From < now - TimeSpan.FromMilliseconds(tiered.LocalRetentionMs.Value))
            {
                topicNotes.Add(new WindowNote.TieredStorage(state.Topic));
            }

            foreach (WirePartitionInfo partition in state.Info!.Partitions)
            {
                WirePartition key = new(state.Topic, partition.Partition);
                ranges.Add(RangeOf(cluster, state, key, committed, fromMs, effectiveMargin, earliest, latest, stable, start, end));
            }

            topicWindows.Add(new TopicWindow(state.Topic, state.Kind, null, topicNotes));
        }

        List<WindowNote> windowNotes = [new WindowNote.PartitionsFixed(now)];

        if (!committed)
        {
            windowNotes.Add(new WindowNote.IncludesUncommitted());
        }

        foreach (WireError.Unsupported unsupported in new[] { earliest, latest, stable, start, end }.SelectMany(static x => x.Errors).OfType<WireError.Unsupported>().DistinctBy(static x => x.Api))
        {
            windowNotes.Add(new WindowNote.ApiUnsupported(unsupported.Api));
        }

        return new ResolvedWindow(cluster, window, isolation, effectiveMargin, topicWindows, ranges, windowNotes);
    }

    private PartitionRange RangeOf(
        string cluster,
        TopicState topic,
        WirePartition key,
        bool committed,
        long fromMs,
        TimeSpan margin,
        Lookup earliest,
        Lookup latest,
        Lookup stable,
        Lookup start,
        Lookup end)
    {
        long logStart = Math.Max(0, earliest.Offset(key) ?? 0);
        long highWatermark = Math.Max(0, latest.Offset(key) ?? 0);
        long? lso = committed ? stable.Offset(key) : null;
        long stop = committed && lso is { } stable0 ? Math.Min(stable0, highWatermark) : highWatermark;

        WireOffset? startOffset = start.Of(key);
        WireOffset? endOffset = end.Of(key);

        long startAt = startOffset is { Found: true } ? startOffset.Offset : highWatermark;
        long endBeforeStop = endOffset is { Found: true } ? endOffset.Offset : highWatermark;
        long endAt = Math.Min(endBeforeStop, stop);

        List<WindowNote> notes = [];

        if (committed && lso is { } l && l < endBeforeStop && l < highWatermark)
        {
            notes.Add(new WindowNote.StoppedAtLso(key.Topic, key.Partition, l, highWatermark));
        }

        // The first retained record is later than the window's start: what came before it may have been matches. A broker that returned
        // no timestamp cannot say it is not, so the gap is reported without one (the note's wording accepts that).
        if (logStart > 0 && startOffset is { Found: true } found && found.Offset == logStart && (found.TimestampMs is null || found.TimestampMs > fromMs))
        {
            DateTimeOffset? firstAt = found.TimestampMs is { } stamp ? DateTimeOffset.FromUnixTimeMilliseconds(stamp) : null;
            notes.Add(new WindowNote.RetentionGap(key.Topic, key.Partition, logStart, firstAt));
            string topicText = KafkaLogValue.Cap(key.Topic);
            string clusterText = KafkaLogValue.Cap(cluster);
            KStudioLog.RetentionGap(loggers.Catalog, key.Partition, topicText, clusterText, logStart);
        }

        // With no margin the end is exact, and a record stamped earlier than one before the end but appended after it is missed: the case
        // the note is for, so it is given at margin zero too.
        if (topic.Kind != KafkaTimestampKind.LogAppendTime && endOffset is { Found: true })
        {
            notes.Add(new WindowNote.CreateTimeMargin(key.Topic, key.Partition, margin, endAt));
        }

        return new PartitionRange(key.Topic, key.Partition, Math.Min(startAt, endAt), endAt, logStart, highWatermark, lso, notes);
    }

    private async Task<WireResult<IReadOnlyList<WireOffset>>> Call(
        IKafkaWire wire,
        string cluster,
        List<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation,
        CancellationToken cancellationToken)
    {
        if (partitions.Count == 0)
        {
            return WireResult<IReadOnlyList<WireOffset>>.Ok([]);
        }

        WireResult<IReadOnlyList<WireOffset>> result = await wire.ListOffsetsAsync(partitions, spec, isolation, cancellationToken).ConfigureAwait(false);
        return reads.Observe(cluster, WireCall.ListOffsets, result);
    }

    private static WireError? FirstTopicError(TopicState topic, params Lookup[] lookups)
    {
        foreach (Lookup lookup in lookups)
        {
            if (lookup.WholeError is { } whole)
            {
                return whole;
            }

            foreach (WirePartitionInfo partition in topic.Info!.Partitions)
            {
                if (lookup.Of(new WirePartition(topic.Topic, partition.Partition))?.Error is { } error and not WireError.Unsupported)
                {
                    return error;
                }
            }
        }

        return null;
    }

    /// <summary>A topic's metadata, configuration and the timestamp kind that follows from it.</summary>
    private sealed class TopicState
    {
        public TopicState(string topic, WireTopicInfo? info, WireError? refusal, WireResult<WireTopicConfig> config)
        {
            Topic = topic;
            Info = info;
            Refusal = refusal;
            Config = config;

            if (config.IsOk)
            {
                ConfigView = KafkaTopicConfigView.From(config.Value);
                Kind = ConfigView.TimestampType switch
                {
                    { } text when text.Equals("LogAppendTime", StringComparison.OrdinalIgnoreCase) => KafkaTimestampKind.LogAppendTime,
                    { } text when text.Equals("CreateTime", StringComparison.OrdinalIgnoreCase) => KafkaTimestampKind.CreateTime,
                    _ => KafkaTimestampKind.Unknown,
                };
            }
            else
            {
                Kind = KafkaTimestampKind.Unknown;
            }
        }

        public string Topic { get; }

        public WireTopicInfo? Info { get; }

        public WireError? Refusal { get; }

        public WireResult<WireTopicConfig> Config { get; }

        public KafkaTopicConfigView? ConfigView { get; }

        public KafkaTimestampKind Kind { get; }
    }

    /// <summary>One or two list-offsets results indexed by partition.</summary>
    private sealed class Lookup
    {
        private readonly Dictionary<WirePartition, WireOffset> byPartition = [];
        private readonly List<WireError> errors = [];

        public Lookup(params WireResult<IReadOnlyList<WireOffset>>[] results)
        {
            foreach (WireResult<IReadOnlyList<WireOffset>> result in results)
            {
                if (result.Error is { } error)
                {
                    WholeError ??= error is WireError.Unsupported ? null : error;
                    errors.Add(error);
                    continue;
                }

                foreach (WireOffset offset in result.Value)
                {
                    byPartition[offset.Partition] = offset;

                    if (offset.Error is { } offsetError)
                    {
                        errors.Add(offsetError);
                    }
                }
            }
        }

        /// <summary>An error that failed a whole call, other than "not supported".</summary>
        public WireError? WholeError { get; }

        public IEnumerable<WireError> Errors => errors;

        public WireOffset? Of(WirePartition partition) => byPartition.GetValueOrDefault(partition);

        public long? Offset(WirePartition partition) =>
            byPartition.TryGetValue(partition, out WireOffset? offset) && offset.Error is null && offset.Offset >= 0 ? offset.Offset : null;
    }
}
