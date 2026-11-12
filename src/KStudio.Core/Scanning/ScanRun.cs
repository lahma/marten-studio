using System.Diagnostics;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

namespace KStudio.Core.Scanning;

/// <summary>
/// A seam for tests, <see langword="null" /> in production (decision 12.3): told, on the scan's thread, after each record has been
/// accounted for. A test blocks the thread here until it has deleted records, which is a wait on a condition and not a delay.
/// </summary>
internal interface IScanObserver
{
    /// <summary>A record of <paramref name="partition" /> at <paramref name="offset" /> was read and accounted for.</summary>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    void AfterRecord(WirePartition partition, long offset);
}

/// <summary>What a scan run is given.</summary>
/// <param name="ScanId">The scan's id; its consumer's group id derives from it.</param>
/// <param name="Plan">The plan.</param>
/// <param name="Owner">Whose scan it is.</param>
/// <param name="Wire">The cluster's wire.</param>
/// <param name="Settings">The engine options as validated.</param>
/// <param name="Governor">The byte rate every scan shares.</param>
/// <param name="OwnerBudget">The owners' hourly bytes.</param>
/// <param name="Time">The clock.</param>
/// <param name="Loggers">The engine's loggers.</param>
/// <param name="Once">Logs a repeatable state once per window.</param>
/// <param name="Observer">The test seam.</param>
/// <param name="MatchEveryRecordInRange">
/// Set only by a record read (D-UI decision 1): every record in the range is a match, whatever its timestamp, because a permalink names an
/// offset and not a time. The window filter and the predicate are skipped.
/// </param>
/// <param name="FocusOffset">For a record read: the offset of the record asked for, whose value part may hold <paramref name="FocusValueBytes" />.</param>
/// <param name="FocusValueBytes">For a record read: the bytes of value the record asked for may keep, whatever is left of the match's budget; 0 for none.</param>
/// <param name="ClusterGovernor">The byte rate of this cluster's own, when its profile lowers the host's (D47): the scan is paced by the slower of the two.</param>
/// <param name="ClusterOwnerBudget">The hourly allowance of this cluster's own, when its profile lowers the host's: the scan stops when either is spent.</param>
/// <param name="Tail">
/// Set only by <c>StartTail</c> (D-M5 decision 3.3): the run has no window and no end, keeps its matches in a ring, ends a segment after
/// <c>TailPauseAfter</c> instead of <c>MaxScanDuration</c>, never finishes a partition at its end and never stops for <c>MatchLimit</c>. It must equal the plan's <c>IsTail</c>.
/// </param>
internal sealed record ScanRunInputs(
    Guid ScanId,
    KafkaScanPlan Plan,
    KafkaScanOwner Owner,
    IKafkaWire Wire,
    KafkaEngineSnapshot Settings,
    KafkaByteRateGovernor Governor,
    OwnerByteBudget OwnerBudget,
    TimeProvider Time,
    KStudioLoggers Loggers,
    KafkaLogOnce Once,
    IScanObserver? Observer,
    bool MatchEveryRecordInRange = false,
    long FocusOffset = -1,
    int FocusValueBytes = 0,
    KafkaByteRateGovernor? ClusterGovernor = null,
    OwnerByteBudget? ClusterOwnerBudget = null,
    bool Tail = false);

/// <summary>
/// One scan's reader (AGENTS.md D36 and D38, decisions 4.2 and 9.2): a synchronous method run on a thread of its own that builds, assigns,
/// reads and closes one consumer, and never awaits. It reads each partition's range, judges every record whose own timestamp is inside the
/// window, keeps the matches (capped, redacted, copied) and counts everything else, so that "no match" is said only of what was read.
/// </summary>
/// <remarks>
/// The thread writes to the match store and to the counters under a lock and never to a channel, so a slow reader of batches never paces the
/// reader. A scan stops for exactly one <see cref="KafkaScanStopReason" />; a record is accounted for completely before any limit is looked
/// at, so the cursor (the next offset of every partition) is exact.
/// </remarks>
internal sealed class ScanRun : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StallCheckEvery = TimeSpan.FromMilliseconds(250);
    private const int MaxRepositionsWithoutProgress = 3;
    private const long SlowCloseMilliseconds = 2_000;

    private readonly ScanRunInputs inputs;
    private readonly KafkaScanPlan plan;
    private readonly KafkaScanLimits limits;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly Dictionary<WirePartition, PartitionState> partitions = [];
    private readonly List<PartitionState> ordered = [];
    private readonly CancellationTokenSource stopRequested = new();
    private readonly CancellationTokenSource deadline;
    private readonly CancellationTokenSource either;
    private readonly DecodeCounters counters = new();
    private readonly DecodeCounters matchCounters = new();
    private readonly Dictionary<string, TopicHooks> hooks = new(StringComparer.Ordinal);
    private readonly Dictionary<KafkaUnknownReason, long> unknownByReason = [];
    private readonly List<KafkaScanGap> gaps = [];
    private readonly long fromMs;
    private readonly long toMs;
    private readonly string cluster;
    private readonly string ownerKey;
    private readonly bool tail;
    private IWireScanReader? reader;
    private DateTimeOffset startedAt;
    private DateTimeOffset lastAnyProgress;
    private DateTimeOffset lastStallCheck;
    private long records;
    private long bytes;
    private long undecidable;
    private long outsideWindow;
    private long noTimestamp;
    private int groupRefusals;
    private long version;
    private int requestedReason;
    private KafkaScanStopReason? stopReason;
    private KafkaSpentBudget? spentBudget;
    private bool groupRefusalLogged;

    /// <summary>Creates the run; it reads nothing until <see cref="Execute" />.</summary>
    /// <param name="inputs">What the run needs.</param>
    public ScanRun(ScanRunInputs inputs)
    {
        this.inputs = inputs;
        plan = inputs.Plan;

        if (inputs.Tail != plan.IsTail)
        {
            throw new ArgumentException("A tail run takes a tail plan and a scan run takes a scan plan.", nameof(inputs));
        }

        tail = inputs.Tail;
        limits = plan.Limits;
        time = inputs.Time;
        cluster = plan.Request.Cluster;
        ownerKey = inputs.Owner.Key;
        fromMs = plan.Request.Window.From.ToUnixTimeMilliseconds();
        toMs = plan.Request.Window.To.ToUnixTimeMilliseconds();

        // A tail keeps the newest TailCapacity matches in a ring and reads for TailPauseAfter; a scan keeps the first MaxMatches and reads for MaxScanDuration.
        Store = tail ? new KafkaMatchStore(limits.TailCapacity, ring: true) : new KafkaMatchStore(limits.MaxMatches);
        deadline = new CancellationTokenSource(tail ? limits.TailPauseAfter : limits.MaxScanDuration, time);
        either = CancellationTokenSource.CreateLinkedTokenSource(stopRequested.Token, deadline.Token);
        gaps.AddRange(plan.Gaps);

        foreach (PartitionRange range in plan.Ranges)
        {
            PartitionState state = new(range);
            partitions[range.Key] = state;
            ordered.Add(state);
        }
    }

    /// <summary>The scan's id.</summary>
    public Guid ScanId => inputs.ScanId;

    /// <summary>The matches kept so far.</summary>
    public KafkaMatchStore Store { get; }

    /// <summary>Whether no range has anything to read, so that no consumer is needed.</summary>
    public bool NothingToRead => ordered.All(static x => x.Range.IsEmpty);

    /// <summary>Asks the run to stop; the first reason wins. Safe from any thread.</summary>
    /// <param name="reason">Why.</param>
    public void RequestStop(KafkaScanStopReason reason)
    {
        Interlocked.CompareExchange(ref requestedReason, (int) reason + 1, 0);

        try
        {
            stopRequested.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run has ended and released its tokens: there is nothing left to stop.
        }
    }

    /// <summary>A copy of the progress, taken under the run's lock.</summary>
    public KafkaScanProgress Snapshot()
    {
        lock (gate)
        {
            return BuildProgress();
        }
    }

    /// <summary>
    /// Runs the scan to its end on the calling thread. Everything the consumer is asked is asked from here, and the consumer is closed here.
    /// </summary>
    public KafkaScanResult Execute()
    {
        startedAt = time.GetUtcNow();
        lastAnyProgress = startedAt;
        lastStallCheck = startedAt;

        foreach (PartitionState state in ordered)
        {
            state.LastProgress = startedAt;

            if (state.Range.IsEmpty)
            {
                state.State = PartitionEndState.Empty;
            }
        }

        try
        {
            if (ordered.Any(static x => x.State is null))
            {
                WireIsolation isolation = plan.Request.Isolation == KafkaIsolation.Committed ? WireIsolation.ReadCommitted : WireIsolation.ReadUncommitted;
                reader = inputs.Wire.OpenReader(new WireReaderOptions(ScanId, isolation, limits));
                reader.Assign([.. ordered.Where(static x => x.State is null).Select(static x => new WirePartitionOffset(x.Range.Topic, x.Range.Partition, x.Range.Start))]);
                Loop();
            }
            else
            {
                // Nothing to read. When that is because every topic was refused (a topic the principal may not describe is reported as
                // not found, and one it may not read as refused), saying "completed" would read as "nothing found".
                IReadOnlyList<TopicWindow> windowTopics = plan.Window?.Topics ?? [];
                bool everyTopicRefused = windowTopics.Count > 0 && windowTopics.All(static x => x.Refusal is not null);

                // A tail never completes: with nothing to follow, the cluster listed no partition to follow.
                stopReason = everyTopicRefused ? KafkaScanStopReason.NotAuthorized : tail ? KafkaScanStopReason.Unavailable : KafkaScanStopReason.Completed;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            stopReason = KafkaScanStopReason.Failed;
            string clusterText = KafkaLogValue.Cap(cluster)!;
            string kind = exception.GetType().Name;
            KStudioLog.ScanFailed(inputs.Loggers.Scans, ScanId, clusterText, kind);
        }
        finally
        {
            CloseReader();
        }

        return Finish();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        either.Dispose();
        deadline.Dispose();
        stopRequested.Dispose();
    }

    private void Loop()
    {
        while (true)
        {
            if (CheckStop())
            {
                return;
            }

            // In debt, the thread does not call Consume: librdkafka's bounded queue stops fetching, and the broker sends little more than
            // was read (decision 9.3).
            // The slower of the process's bucket and the cluster's own (D47): the wait is on the one in the deeper debt.
            TimeSpan debt = inputs.Governor.DebtWait();
            KafkaByteRateGovernor waitOn = inputs.Governor;

            if (inputs.ClusterGovernor is { } clusterGovernor && clusterGovernor.DebtWait() is var clusterDebt && clusterDebt > debt)
            {
                debt = clusterDebt;
                waitOn = clusterGovernor;
            }

            if (debt > TimeSpan.Zero)
            {
                DateTimeOffset before = time.GetUtcNow();
                waitOn.Wait(debt, either.Token);
                ExtendStallClocks(time.GetUtcNow() - before);
                continue;
            }

            WireReadOutcome outcome = reader!.Read(ReadTimeout);
            bool stop = outcome switch
            {
                WireReadOutcome.Record record => OnRecord(record.Value),
                WireReadOutcome.PartitionEnd end => OnPartitionEnd(end),
                WireReadOutcome.Failure failure => OnFailure(failure),
                _ => false,
            };

            if (stop)
            {
                return;
            }

            if (ordered.All(static x => x.State is not null))
            {
                bool refused = ordered.Any(static x => x.State is PartitionEndState.NotAuthorized)
                    && ordered.All(static x => x.State is PartitionEndState.NotAuthorized or PartitionEndState.Empty);

                // A tail has no end to reach, so every partition of it finishing means each stalled, was refused or could not go on past deleted offsets.
                stopReason = refused
                    ? KafkaScanStopReason.NotAuthorized
                    : !tail
                        ? KafkaScanStopReason.Completed
                        : ordered.Any(static x => x.State is PartitionEndState.Stalled) ? KafkaScanStopReason.Unavailable : KafkaScanStopReason.Failed;
                return;
            }

            CheckStalls();
        }
    }

    private bool CheckStop()
    {
        if (stopRequested.IsCancellationRequested)
        {
            int requested = Volatile.Read(ref requestedReason);
            stopReason = requested > 0 ? (KafkaScanStopReason) (requested - 1) : KafkaScanStopReason.Cancelled;
            return true;
        }

        if (deadline.IsCancellationRequested)
        {
            stopReason = KafkaScanStopReason.Deadline;
            return true;
        }

        return false;
    }

    private bool OnRecord(WireRecord record)
    {
        // Everything read is paid for, even what is not counted below.
        inputs.Governor.Take(record.Size);
        inputs.ClusterGovernor?.Take(record.Size);

        if (!partitions.TryGetValue(new WirePartition(record.Topic, record.Partition), out PartitionState? p) || p.State is not null)
        {
            // Prefetched before the partition was paused: not counted.
            return false;
        }

        if (record.Offset >= p.Range.End)
        {
            Finish(p, PartitionEndState.EndOfWindow);
            PausePartition(p);
            return false;
        }

        TopicScanContext topic = plan.Topics[record.Topic];
        KafkaMatch? match = null;
        KafkaUnknownReason? unknown = null;
        bool outside = false;
        bool withoutTimestamp = false;

        if (inputs.MatchEveryRecordInRange)
        {
            int focus = inputs.FocusValueBytes > 0 && record.Offset == inputs.FocusOffset ? inputs.FocusValueBytes : 0;
            TopicHooks hook = HooksOf(topic);
            match = KafkaMatchFactory.Create(
                record, topic, topic.Grant, limits, matchCounters, hook.DecoderFailed, focusValueBytes: focus, onRedactionFailed: hook.RedactionFailed);
        }
        else if (!tail && record.TimestampType == WireTimestampType.NotAvailable)
        {
            withoutTimestamp = true;
        }
        else if (!tail && (record.TimestampMs < fromMs || record.TimestampMs >= toMs))
        {
            outside = true;
        }
        else
        {
            // A tail has no window (D-M5 decision 3.3): every record in range is evaluated, whatever its timestamp, and one with none is still
            // evaluated and counted.
            withoutTimestamp = record.TimestampType == WireTimestampType.NotAvailable;
            TopicHooks hook = HooksOf(topic);
            RecordEvaluationContext context = new(
                record,
                topic.Descriptor.KeyFormat,
                counters,
                limits.MaxDecodeBytes,
                topic.DeclaresCloudEvents,
                topic.Grant,
                topic.Decoder,
                topic.Redactor,
                hook.DecoderFailed,
                hook.RedactionFailed);

            switch (topic.Predicate.Evaluate(context))
            {
                case TriState.True:
                    match = KafkaMatchFactory.Create(
                        record, topic, topic.Grant, limits, matchCounters, hook.DecoderFailed, context, onRedactionFailed: hook.RedactionFailed);
                    break;
                case TriState.Unknown:
                    unknown = context.UnknownReason ?? KafkaUnknownReason.InvalidJson;
                    break;
            }
        }

        DateTimeOffset now = time.GetUtcNow();

        lock (gate)
        {
            records++;
            bytes += record.Size;
            p.Records++;
            p.Bytes += record.Size;
            p.Next = record.Offset + 1;
            p.Repositions = 0;
            p.LastProgress = now;
            p.CaughtUp = false;
            lastAnyProgress = now;

            if (withoutTimestamp)
            {
                noTimestamp++;
            }

            if (outside)
            {
                outsideWindow++;
                p.OutsideWindow++;
            }

            if (unknown is { } reason)
            {
                undecidable++;
                p.Undecidable++;
                unknownByReason[reason] = unknownByReason.GetValueOrDefault(reason) + 1;
            }

            if (match is not null && Store.TryAdd(match))
            {
                p.Matches++;
            }

            version++;
        }

        inputs.OwnerBudget.Add(ownerKey, record.Size);
        inputs.ClusterOwnerBudget?.Add(ownerKey, record.Size);
        inputs.Observer?.AfterRecord(p.Range.Key, record.Offset);

        if (p.Next >= p.Range.End)
        {
            Finish(p, p.StopsAtLso ? PartitionEndState.StoppedAtLso : PartitionEndState.EndOfWindow);
            PausePartition(p);
        }

        // A limit is looked at only after the record has been accounted for completely, so the cursor is exact. A tail's store is a ring: it
        // is never full and never stops the run.
        if (!tail && Store.Count >= limits.MaxMatches)
        {
            stopReason = KafkaScanStopReason.MatchLimit;
        }
        else if (bytes >= limits.MaxBytesPerScan)
        {
            stopReason = KafkaScanStopReason.ByteBudget;
        }
        else if (records >= limits.MaxRecordsPerScan)
        {
            stopReason = KafkaScanStopReason.RecordBudget;
        }
        else if (inputs.OwnerBudget.Remaining(ownerKey) <= 0 || inputs.ClusterOwnerBudget?.Remaining(ownerKey) <= 0)
        {
            stopReason = KafkaScanStopReason.OwnerBudget;

            // The process's allowance when it is spent (it stops every cluster), otherwise the cluster's own: the sentence names that one.
            spentBudget = inputs.OwnerBudget.Remaining(ownerKey) <= 0
                ? new KafkaSpentBudget(KafkaBudgetScope.Process, inputs.OwnerBudget.BytesPerHour)
                : new KafkaSpentBudget(KafkaBudgetScope.Cluster, inputs.ClusterOwnerBudget!.BytesPerHour);
        }

        return stopReason is not null;
    }

    private sealed class TopicHooks(Action<Exception> decoderFailed, Action<RedactedValue> redactionFailed)
    {
        public Action<Exception> DecoderFailed { get; } = decoderFailed;

        public Action<RedactedValue> RedactionFailed { get; } = redactionFailed;
    }

    /// <summary>The two callbacks of a topic, made once: a record must not allocate them.</summary>
    private TopicHooks HooksOf(TopicScanContext topic)
    {
        if (!hooks.TryGetValue(topic.Topic, out TopicHooks? known))
        {
            known = new TopicHooks(exception => OnDecoderFailed(topic, exception), redacted => OnPayloadRuleFailed(topic, redacted));
            hooks[topic.Topic] = known;
        }

        return known;
    }

    /// <summary>A payload rule could not be applied: the rule's kind and the exception's type name, once per cluster, topic and rule per window.</summary>
    private void OnPayloadRuleFailed(TopicScanContext topic, RedactedValue redacted)
    {
        string rule = redacted.WithheldBy ?? string.Empty;
        string topicText = KafkaLogValue.Cap(topic.Topic)!;
        string clusterText = KafkaLogValue.Cap(cluster)!;

        if (inputs.Once.ShouldLog($"payload|{clusterText}|{topicText}|{rule}"))
        {
            string kind = rule.StartsWith('$') ? "JsonPath" : "Pattern";
            string failure = redacted.Failure.ToString();
            string exceptionKind = redacted.ExceptionKind ?? "none";
            KStudioLog.PayloadRuleNotApplied(inputs.Loggers.Scans, kind, topicText, clusterText, failure, exceptionKind);
        }
    }

    private void OnDecoderFailed(TopicScanContext topic, Exception exception)
    {
        string decoder = KafkaLogValue.Cap(topic.Decoder?.Name ?? "decoder")!;
        string topicText = KafkaLogValue.Cap(topic.Topic)!;
        string clusterText = KafkaLogValue.Cap(cluster)!;

        if (inputs.Once.ShouldLog($"decoder|{clusterText}|{topicText}|{decoder}"))
        {
            string kind = exception.GetType().Name;
            KStudioLog.DecoderFailed(inputs.Loggers.Scans, decoder, topicText, clusterText, kind);
        }
    }

    private bool OnPartitionEnd(WireReadOutcome.PartitionEnd end)
    {
        if (!partitions.TryGetValue(end.Partition, out PartitionState? p) || p.State is not null)
        {
            return false;
        }

        if (tail)
        {
            // A tail's partition never finishes at its end (D-M5 decision 3.3): it is caught up, "live", until the next record arrives.
            // librdkafka raises the end again only after new records were read to it. The offsets between the last record and the end are
            // control records and aborted batches, so the cursor goes to the end, as a scan's does. Reaching the end is contact with the
            // broker, which is progress.
            DateTimeOffset now = time.GetUtcNow();

            lock (gate)
            {
                p.CaughtUp = true;
                p.Next = Math.Max(p.Next, end.Offset);
                p.LastProgress = now;
                lastAnyProgress = now;
                version++;
            }

            return false;
        }

        PartitionEndState state;

        if (end.Offset >= p.Range.End)
        {
            // Everything the range asked for was read, or lies under control records and aborted batches: nothing is left, so the
            // cursor does not offer this partition again.
            state = p.StopsAtLso ? PartitionEndState.StoppedAtLso : PartitionEndState.EndOfWindow;

            lock (gate)
            {
                if (p.State is null)
                {
                    p.Next = p.Range.End;
                }
            }
        }
        else if (p.Range.Lso is { } lso && lso == end.Offset && lso < p.Range.HighWatermark)
        {
            state = PartitionEndState.StoppedAtLso;
        }
        else
        {
            state = PartitionEndState.EndOfLog;
        }

        Finish(p, state, end.Offset);
        return false;
    }

    private bool OnFailure(WireReadOutcome.Failure failure)
    {
        switch (failure.Error)
        {
            case WireError.NotAuthorized { ResourceType: KafkaResourceType.Group }:
                // One per consumer, and not a stop: a principal with no group ACL still reads (SPIKE S1).
                lock (gate)
                {
                    groupRefusals++;
                    version++;
                }

                if (!groupRefusalLogged)
                {
                    groupRefusalLogged = true;
                    string clusterText = KafkaLogValue.Cap(cluster)!;
                    KStudioLog.GroupCoordinatorRefused(inputs.Loggers.Scans, ScanId, clusterText);
                }

                return false;

            case WireError.NotAuthorized { ResourceType: KafkaResourceType.Topic } refused:
                foreach (PartitionState p in FailedPartitions(failure, refused.ResourceName))
                {
                    Finish(p, PartitionEndState.NotAuthorized);
                }

                return false;

            case WireError.OffsetOutOfRange outOfRange:
                if (partitions.TryGetValue(outOfRange.Partition, out PartitionState? deleted) && deleted.State is null)
                {
                    return OnOffsetsDeleted(deleted);
                }

                return false;

            case WireError.NotFound { ResourceType: KafkaResourceType.Topic } missing:
                foreach (PartitionState p in FailedPartitions(failure, missing.Name))
                {
                    LogStalled(p);
                    Finish(p, PartitionEndState.Stalled);
                }

                return false;

            case WireError.Unavailable:
                if (time.GetUtcNow() - lastAnyProgress >= limits.PartitionStallTimeout)
                {
                    stopReason = KafkaScanStopReason.Unavailable;
                    return true;
                }

                return false;

            case WireError.Failed { IsFatal: true } or WireError.AuthenticationFailed or WireError.NotAuthorized:
                stopReason = KafkaScanStopReason.Failed;
                return true;

            default:
                // Non-fatal, and the client's own error handler has logged it.
                return false;
        }
    }

    private IEnumerable<PartitionState> FailedPartitions(WireReadOutcome.Failure failure, string topic)
    {
        if (failure.Partition is { } known)
        {
            return partitions.TryGetValue(known, out PartitionState? one) && one.State is null ? [one] : [];
        }

        return [.. ordered.Where(x => x.State is null && string.Equals(x.Range.Topic, topic, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Records were deleted below the position (decision 6.6). The log start is read again, the loss is counted, and the partition goes on from
    /// the log start: an unassign and an assign, never a seek (SPIKE S3d). When it cannot go on, it ends with <c>OffsetGap</c> and says so.
    /// </summary>
    private bool OnOffsetsDeleted(PartitionState p)
    {
        long logStart;

        try
        {
            TimeSpan wait = inputs.Settings.RequestTimeout * 2;
            WireResult<IReadOnlyList<WireOffset>> listed = inputs.Wire
                .ListOffsetsAsync([p.Range.Key], WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, either.Token)
                .WaitAsync(wait, time, either.Token)
                .GetAwaiter()
                .GetResult();

            if (!listed.IsOk || listed.Value.Count == 0 || listed.Value[0] is not { Found: true } first)
            {
                Finish(p, PartitionEndState.OffsetGap);
                PausePartition(p);
                return false;
            }

            logStart = first.Offset;
        }
        catch (OperationCanceledException) when (either.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            Finish(p, PartitionEndState.OffsetGap);
            return false;
        }

        long resume = Math.Max(logStart, p.Next);

        if (resume > p.Next)
        {
            long lost = Math.Min(resume, p.Range.End) - p.Next;
            lock (gate)
            {
                gaps.Add(new KafkaScanGap(p.Range.Topic, p.Range.Partition, p.Next, Math.Min(resume, p.Range.End) - 1, lost, logStart));
                version++;
            }

            string topicText = KafkaLogValue.Cap(p.Range.Topic)!;
            KStudioLog.OffsetsDeletedDuringScan(inputs.Loggers.Scans, ScanId, lost, topicText, p.Range.Partition);
        }

        if (resume >= p.Range.End)
        {
            lock (gate)
            {
                p.Next = p.Range.End;
            }

            Finish(p, PartitionEndState.OffsetGap);
            PausePartition(p);
            return false;
        }

        p.Repositions = resume > p.Next || p.Repositions == 0 ? 1 : p.Repositions + 1;

        if (p.Repositions > MaxRepositionsWithoutProgress)
        {
            Finish(p, PartitionEndState.OffsetGap);
            PausePartition(p);
            return false;
        }

        lock (gate)
        {
            p.Next = resume;
            p.LastProgress = time.GetUtcNow();
        }

        try
        {
            reader!.Reposition(new WirePartitionOffset(p.Range.Topic, p.Range.Partition, resume));
        }
        catch (WireException)
        {
            // The consumer could not take it back to the log start: the partition ends here, and the result says so.
            Finish(p, PartitionEndState.OffsetGap);
        }

        return false;
    }

    private void CheckStalls()
    {
        DateTimeOffset now = time.GetUtcNow();

        if (now - lastStallCheck < StallCheckEvery)
        {
            return;
        }

        lastStallCheck = now;

        foreach (PartitionState p in ordered)
        {
            // A caught-up tail partition (always false for a scan's) is idle, not stalled: only one that is behind can make no progress.
            if (p.State is null && !p.CaughtUp && now - p.LastProgress >= limits.PartitionStallTimeout)
            {
                LogStalled(p);
                Finish(p, PartitionEndState.Stalled);
                PausePartition(p);
            }
        }
    }

    private void LogStalled(PartitionState p)
    {
        string topicText = KafkaLogValue.Cap(p.Range.Topic)!;
        KStudioLog.PartitionStalled(inputs.Loggers.Scans, ScanId, topicText, p.Range.Partition, p.Next);
    }

    private void ExtendStallClocks(TimeSpan waited)
    {
        if (waited <= TimeSpan.Zero)
        {
            return;
        }

        // Waiting out a debt is not a partition making no progress.
        lock (gate)
        {
            foreach (PartitionState p in ordered)
            {
                p.LastProgress += waited;
            }

            lastAnyProgress += waited;
        }
    }

    /// <summary>
    /// Ends a partition. A partition that ended at the end of its range has nothing left to read, so its cursor position is the range's end
    /// (the offsets between its last record and the end are control records and aborted batches); one that ended at an offset below the end
    /// (the end of the log, an LSO inside the range) keeps at least that offset.
    /// </summary>
    private void Finish(PartitionState p, PartitionEndState state, long? reached = null)
    {
        lock (gate)
        {
            if (p.State is not null)
            {
                return;
            }

            p.State = state;

            if (state == PartitionEndState.EndOfWindow)
            {
                p.Next = p.Range.End;
            }
            else if (reached is { } offset)
            {
                p.Next = Math.Min(p.Range.End, Math.Max(p.Next, offset));
            }

            version++;
        }
    }

    private void PausePartition(PartitionState p) => reader?.Pause([p.Range.Key]);

    private void CloseReader()
    {
        if (reader is null)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();

        try
        {
            // Commits nothing: auto-commit is off, nothing is stored and nothing was subscribed (D37).
            reader.Close();
        }
        catch (Exception exception) when (exception is WireException or ObjectDisposedException or InvalidOperationException)
        {
            reader.Dispose();
        }

        long elapsed = (long) Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        if (elapsed > SlowCloseMilliseconds)
        {
            KStudioLog.ConsumerCloseSlow(inputs.Loggers.Scans, ScanId, elapsed);
        }
    }

    private KafkaScanResult Finish()
    {
        KafkaScanStopReason reason = stopReason ?? KafkaScanStopReason.Failed;
        KafkaScanResult result;

        lock (gate)
        {
            foreach (PartitionState p in ordered.Where(static x => x.State is null))
            {
                p.State = PartitionEndState.NotReached;
            }

            stopReason = reason;
            version++;

            List<KafkaPartitionResult> results =
            [
                .. ordered.Select(static x => new KafkaPartitionResult(
                    x.Range.Topic, x.Range.Partition, x.Range.Start, x.Range.End, x.Next, x.State!.Value, x.Records, x.Bytes, x.Matches, x.Undecidable, x.OutsideWindow)),
            ];

            KafkaScanCursor cursor = new(
            [
                .. ordered.Where(static x => x.Next < x.Range.End).Select(static x => new KafkaCursorPosition(x.Range.Topic, x.Range.Partition, x.Next, x.Range.End)),
            ])
            {
                Pruning = [.. plan.Pruning.Where(static x => x.IsPruned)],
            };

            List<KafkaTopicRefusal> refusals =
            [
                .. (plan.Window?.Topics ?? []).Where(static x => x.Refusal is not null).Select(static x => new KafkaTopicRefusal(x.Topic, KafkaAccessText.Describe(x.Refusal!))),
            ];

            result = new KafkaScanResult(
                ScanId,
                reason,
                time.GetUtcNow() - startedAt,
                results,
                records,
                bytes,
                Store.Added,
                undecidable,
                outsideWindow,
                noTimestamp,
                groupRefusals,
                new Dictionary<KafkaUnknownReason, long>(unknownByReason),
                [.. gaps],
                [.. plan.Notes],
                refusals,
                cursor,
                counters.Copy(),
                matchCounters.Copy())
            {
                Pruning = plan.Pruning,
                SpentBudget = reason == KafkaScanStopReason.OwnerBudget ? spentBudget : null,
            };
        }

        string clusterText = KafkaLogValue.Cap(cluster)!;
        string reasonText = reason.ToString();
        KStudioLog.ScanStopped(
            inputs.Loggers.Scans, ScanId, clusterText, reasonText, result.Records, result.Bytes, result.Matches, result.Undecidable, (long) result.Elapsed.TotalMilliseconds);

        return result;
    }

    private KafkaScanProgress BuildProgress()
    {
        TimeSpan elapsed = startedAt == default ? TimeSpan.Zero : time.GetUtcNow() - startedAt;

        List<KafkaPartitionProgress> list =
        [
            .. ordered.Select(x =>
            {
                long span = x.Range.End - x.Range.Start;

                // A tail has no end to be a percentage of: it is 100 % while it is caught up, "live", and 0 % while it is behind.
                double percent = tail ? (x.CaughtUp ? 100 : 0) : span <= 0 ? 100 : Math.Clamp((x.Next - x.Range.Start) * 100.0 / span, 0, 100);
                return new KafkaPartitionProgress(
                    x.Range.Topic, x.Range.Partition, x.Range.Start, x.Range.End, x.Next, percent, x.Records, x.Bytes, x.Matches, x.Undecidable, x.OutsideWindow, x.State, x.CaughtUp);
            }),
        ];

        double rate = elapsed.TotalSeconds > 0 ? bytes / elapsed.TotalSeconds : 0;

        // The matches found, not the matches kept: a tail's ring evicts, and the page says how many it never showed.
        return new KafkaScanProgress(list, records, bytes, Store.Added, undecidable, outsideWindow, elapsed, rate, stopReason, version);
    }

    private sealed class PartitionState
    {
        public PartitionState(PartitionRange range)
        {
            Range = range;
            Next = range.Start;
            StopsAtLso = range.Notes.OfType<WindowNote.StoppedAtLso>().Any();
        }

        public PartitionRange Range { get; }

        public bool StopsAtLso { get; }

        public long Next { get; set; }

        public long Records { get; set; }

        public long Bytes { get; set; }

        public long Matches { get; set; }

        public long Undecidable { get; set; }

        public long OutsideWindow { get; set; }

        /// <summary>A tail only: the partition reported its end and no record has arrived since.</summary>
        public bool CaughtUp { get; set; }

        public int Repositions { get; set; }

        public DateTimeOffset LastProgress { get; set; }

        public PartitionEndState? State { get; set; }
    }
}
