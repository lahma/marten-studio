using System.Globalization;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

namespace KStudio.Core.Scanning;

/// <summary>What to read (decision 1 of D-UI): one offset of one partition, and some neighbours on each side.</summary>
/// <param name="Cluster">The cluster's key.</param>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Offset">The offset.</param>
/// <param name="Neighbours">How many records to read on each side, 0 to 10; KStudio passes 5.</param>
/// <param name="Grant">What the visitor may read; it must include <see cref="KafkaReadGrant.Envelope" />.</param>
/// <param name="Isolation">Whether uncommitted records are included.</param>
/// <param name="FocusValueBytes">
/// Under the payload grant: how many bytes of value the record asked for may keep (KStudio passes the decode limit), where its neighbours keep
/// what is left of the match budget. A record read then holds at most 21 matches plus this.
/// </param>
internal sealed record KafkaRecordRequest(
    string Cluster,
    KafkaTopicName Topic,
    int Partition,
    long Offset,
    int Neighbours,
    KafkaReadGrant Grant,
    KafkaIsolation Isolation = KafkaIsolation.Committed,
    int FocusValueBytes = 0);

/// <summary>How a record read ended.</summary>
internal enum KafkaRecordReadStatus
{
    /// <summary>The record at the offset, with its neighbours.</summary>
    Found,

    /// <summary>The range was read to its end and no record has that offset (compaction, a transaction marker or an aborted record).</summary>
    NoRecordAtOffset,

    /// <summary>The offset is below the log start: retention or <c>DeleteRecords</c> removed it.</summary>
    BelowLogStart,

    /// <summary>The offset is at or past the end of the readable log (the high watermark, or the last stable offset under <c>Committed</c>).</summary>
    AboveEnd,

    /// <summary>Metadata does not list the topic or the partition: it is missing, or this application's credentials cannot describe it.</summary>
    PartitionNotFound,

    /// <summary>The broker refused <c>READ</c> (or the call that was needed first).</summary>
    NotAuthorized,

    /// <summary>The limiter, the owner's budget or shutdown refused the read; <see cref="KafkaRecordRead.Refusal" /> says which.</summary>
    Refused,

    /// <summary>The read stopped for another reason; <see cref="KafkaRecordRead.StopReason" /> says which.</summary>
    Stopped,
}

/// <summary>A record read: the record, its neighbours, and a sentence for a visitor when it is not simply found.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Record">The record at the offset, when <see cref="KafkaRecordReadStatus.Found" />. Redacted like every match.</param>
/// <param name="Before">The records before it, ascending by offset.</param>
/// <param name="After">The records after it, ascending by offset.</param>
/// <param name="LogStart">The log start, when it was read.</param>
/// <param name="End">The end of the readable log, when it was read.</param>
/// <param name="Refusal">Why the read was refused, for <see cref="KafkaRecordReadStatus.Refused" />.</param>
/// <param name="StopReason">Why the read stopped, for <see cref="KafkaRecordReadStatus.Stopped" />.</param>
/// <param name="Text">The sentence for the visitor; empty for <see cref="KafkaRecordReadStatus.Found" />.</param>
/// <param name="RecordsRead">Records read, for the audit.</param>
/// <param name="BytesRead">Bytes read, for the audit.</param>
internal sealed record KafkaRecordRead(
    KafkaRecordReadStatus Status,
    KafkaMatch? Record,
    IReadOnlyList<KafkaMatch> Before,
    IReadOnlyList<KafkaMatch> After,
    long? LogStart,
    long? End,
    KafkaScanRefusal? Refusal,
    KafkaScanStopReason? StopReason,
    string Text,
    long RecordsRead,
    long BytesRead)
{
    /// <summary>The longest a record read runs: it is at most 21 offsets, so a longer wait is a cluster that does not answer.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    /// <summary>The most records read on each side of the one asked for.</summary>
    public const int MaxNeighbours = 10;

    internal static KafkaRecordRead Without(KafkaRecordReadStatus status, string text, long? logStart = null, long? end = null) =>
        new(status, null, [], [], logStart, end, null, null, text, 0, 0);

    internal static string NoRecord(long offset) => string.Create(
        CultureInfo.InvariantCulture,
        $"No record at offset {offset}: the topic is compacted, or the offset holds a transaction marker or an aborted record. The nearest records are shown.");

    internal static string Gone(long offset, string topic, int partition, long logStart) => string.Create(
        CultureInfo.InvariantCulture,
        $"Offset {offset} is gone: the earliest offset {topic}/{partition} still holds is {logStart} (retention or DeleteRecords removed the rest).");

    internal static string NotWritten(long offset, string topic, int partition, long end) => string.Create(
        CultureInfo.InvariantCulture,
        $"Offset {offset} has not been written yet: {topic}/{partition} ends at offset {end}.");

    internal static string OpenTransaction(long offset, long lastStable) => string.Create(
        CultureInfo.InvariantCulture,
        $"Offset {offset} is past the last stable offset {lastStable}: an open transaction holds it.");

    internal static string NotAnOffset(long offset) => string.Create(CultureInfo.InvariantCulture, $"Offset {offset} is not an offset.");

    internal static string NotFound(int partition, string topic) => string.Create(
        CultureInfo.InvariantCulture,
        $"Partition {partition} of topic {topic} was not found, or this application's credentials cannot describe it.");

    internal static string RefusalText(KafkaScanRefusal refusal, long ownerBytesPerHour) => refusal switch
    {
        KafkaScanRefusal.ProcessBusy => "The most searches this application runs at once are already running. Try again when one has finished.",
        KafkaScanRefusal.OwnerBusy => "Another search of yours is running, in this tab or another. Stop it, or wait for it to finish.",
        KafkaScanRefusal.OwnerBudgetSpent => $"You have read your hourly allowance of {Bytes(ownerBytesPerHour)}. It frees up as the hour rolls on.",
        KafkaScanRefusal.NeedsConfirmation => "This search needs your confirmation first.",
        KafkaScanRefusal.PredicateInvalid => "The search text has errors.",
        _ => "The application is shutting down.",
    };

    internal static string StopText(KafkaScanStopReason reason, TimeSpan duration) => reason switch
    {
        KafkaScanStopReason.Deadline => $"Stopped after {WindowNote.Span(duration)}, the longest a record read runs.",
        KafkaScanStopReason.ByteBudget => "Stopped: the read reached the byte budget of a search.",
        KafkaScanStopReason.RecordBudget => "Stopped: the read reached the record budget of a search.",
        KafkaScanStopReason.OwnerBudget => "Stopped: you have read your hourly allowance.",
        KafkaScanStopReason.Cancelled or KafkaScanStopReason.CallerGone => "Stopped by you.",
        KafkaScanStopReason.Shutdown => "Stopped: the application is shutting down.",
        KafkaScanStopReason.Unavailable => "Stopped: the cluster stopped answering.",
        _ => "The read failed. The application's log has the reason.",
    };

    /// <summary>A byte count as the pages write it: <c>1.4 GiB</c>, <c>512 KiB</c>.</summary>
    /// <param name="bytes">The bytes.</param>
    internal static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes:N0} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

internal sealed partial class KafkaScanner
{
    /// <inheritdoc />
    public async Task<KafkaRecordRead> ReadRecordAsync(KafkaRecordRequest request, KafkaScanOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(owner);

        if (!request.Grant.HasFlag(KafkaReadGrant.Envelope))
        {
            throw new ArgumentException("A record read needs at least the Envelope grant (the SearchRecords capability).", nameof(request));
        }

        if (request.Neighbours is < 0 or > KafkaRecordRead.MaxNeighbours)
        {
            throw new ArgumentException($"Neighbours must be 0 to {KafkaRecordRead.MaxNeighbours}.", nameof(request));
        }

        KafkaClusterDefinition definition = registry.Get(request.Cluster);
        string topic = request.Topic.Value;
        WirePartition key = new(topic, request.Partition);

        if (request.Offset < 0)
        {
            // Not a question for the broker: no offset is negative.
            return KafkaRecordRead.Without(KafkaRecordReadStatus.PartitionNotFound, KafkaRecordRead.NotAnOffset(request.Offset));
        }

        KafkaScanLimits limits = settings.Limits;

        // A refusal is decided before the broker is asked anything, and the slot is held from here to the end of the read.
        if (coordinator.IsShuttingDown)
        {
            return Refused(request.Cluster, KafkaScanRefusal.ShuttingDown, limits);
        }

        if (ownerBudget.Remaining(owner.Key) <= 0)
        {
            return Refused(request.Cluster, KafkaScanRefusal.OwnerBudgetSpent, limits);
        }

        if (limiter.TryAcquire(owner.Key, out IDisposable? acquired) is { } refusal)
        {
            return Refused(request.Cluster, refusal, limits);
        }

        IDisposable? lease = acquired;

        try
        {
            return await ReadWithSlotAsync(request, owner, definition, key, limits, lease, handOver: () => lease = null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private async Task<KafkaRecordRead> ReadWithSlotAsync(
        KafkaRecordRequest request,
        KafkaScanOwner owner,
        KafkaClusterDefinition definition,
        WirePartition key,
        KafkaScanLimits limits,
        IDisposable? lease,
        Action handOver,
        CancellationToken cancellationToken)
    {
        string topic = key.Topic;

        // Metadata is the cached read: which topics and partitions exist changes slowly.
        WireResult<WireMetadata> metadata = await reads.MetadataAsync(request.Cluster, cancellationToken).ConfigureAwait(false);

        if (metadata.Error is { } metadataError)
        {
            return FromError(request, metadataError);
        }

        WireTopicInfo? info = metadata.Value.Topics.FirstOrDefault(x => x.Error is null && string.Equals(x.Name, topic, StringComparison.Ordinal));

        if (request.Partition < 0 || info is null || info.Partitions.All(x => x.Partition != request.Partition))
        {
            return KafkaRecordRead.Without(KafkaRecordReadStatus.PartitionNotFound, KafkaRecordRead.NotFound(request.Partition, topic));
        }

        // The offsets are read now and never from the catalog's cache: a record just written must not read as "not written yet".
        IKafkaWire wire = wires.For(request.Cluster);
        bool committed = request.Isolation == KafkaIsolation.Committed;
        WireIsolation isolation = committed ? WireIsolation.ReadCommitted : WireIsolation.ReadUncommitted;

        (long? logStart, WireError? startError) = await OffsetAsync(wire, request.Cluster, key, WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, cancellationToken).ConfigureAwait(false);

        if (startError is not null)
        {
            return FromError(request, startError);
        }

        (long? end, WireError? endError) = await OffsetAsync(wire, request.Cluster, key, WireOffsetSpec.Latest, isolation, cancellationToken).ConfigureAwait(false);

        if (endError is not null)
        {
            return FromError(request, endError);
        }

        if (request.Offset < logStart)
        {
            return KafkaRecordRead.Without(KafkaRecordReadStatus.BelowLogStart, KafkaRecordRead.Gone(request.Offset, topic, request.Partition, logStart.Value), logStart, end);
        }

        if (request.Offset >= end)
        {
            string text = KafkaRecordRead.NotWritten(request.Offset, topic, request.Partition, end.Value);

            if (committed)
            {
                // Under Committed the end is the last stable offset; the high watermark says whether an open transaction is why, and
                // an offset at or past the high watermark is simply not written yet.
                (long? highWatermark, WireError? highError) = await OffsetAsync(wire, request.Cluster, key, WireOffsetSpec.Latest, WireIsolation.ReadUncommitted, cancellationToken).ConfigureAwait(false);

                if (highError is null && highWatermark > end)
                {
                    text = request.Offset < highWatermark
                        ? KafkaRecordRead.OpenTransaction(request.Offset, end.Value)
                        : KafkaRecordRead.NotWritten(request.Offset, topic, request.Partition, highWatermark.Value);
                }
            }

            return KafkaRecordRead.Without(KafkaRecordReadStatus.AboveEnd, text, logStart, end);
        }

        long first = Math.Max(logStart!.Value, request.Offset - request.Neighbours);
        long stop = Math.Min(end!.Value, request.Offset + request.Neighbours + 1);

        return await RunRecordReadAsync(request, owner, definition, key, new PartitionRange(topic, request.Partition, first, stop, logStart.Value, end.Value, null, []), limits, lease, handOver, cancellationToken).ConfigureAwait(false);
    }

    private async Task<KafkaRecordRead> RunRecordReadAsync(
        KafkaRecordRequest request,
        KafkaScanOwner owner,
        KafkaClusterDefinition definition,
        WirePartition key,
        PartitionRange range,
        KafkaScanLimits limits,
        IDisposable? lease,
        Action handOver,
        CancellationToken cancellationToken)
    {
        string topic = key.Topic;

        // The range is all the read is for: it never stops at the match limit with the last record read, and it never runs as long as a search.
        limits.MaxMatches = checked((int) (range.End - range.Start)) + 1;
        limits.MaxScanDuration = TimeSpan.FromTicks(Math.Min(limits.MaxScanDuration.Ticks, KafkaRecordRead.MaxDuration.Ticks));

        KafkaScanHandle? handle = null;

        try
        {
            // The same compile as a search with no text: the topic's redactor, key format and decoder, so that a match is a match.
            KafkaTopicDescriptor descriptor = definition.Descriptor(topic);
            IKafkaRedactor redactor = new KafkaRedactor(descriptor.Settings.Rules);
            IKafkaRecordDecoder? decoder = request.Grant.HasFlag(KafkaReadGrant.Payload) || KafkaDecoders.BuiltInKind(descriptor.Settings.DecoderInstance) is not null
                ? decoders.For(descriptor)
                : null;
            bool structured = KafkaDecoders.BuiltInKind(decoder) == KafkaBuiltInDecoder.CloudEvents;

            PredicateCompilation compiled = PredicateCompiler.Compile(
                null,
                new PredicateCompileContext(topic, descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, request.Grant, structured));

            Dictionary<string, TopicScanContext> contexts = new(StringComparer.Ordinal)
            {
                [topic] = new TopicScanContext(topic, descriptor, redactor, compiled.Predicate!, decoder),
            };

            KafkaScanRequest scanRequest = new(
                request.Cluster,
                [request.Topic],
                new KafkaWindow(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMilliseconds(1)),
                string.Empty,
                request.Grant,
                request.Isolation);

            long offsets = range.End - range.Start;
            KafkaScanPlan plan = new(
                scanRequest,
                null,
                [range],
                contexts,
                [],
                [],
                new KafkaScanEstimate(offsets, null, null, 1, false, "A record read: at most 21 offsets."),
                limits,
                string.Empty);

            Guid scanId = Guid.NewGuid();
            ScanRunInputs inputs = new(
                scanId,
                plan,
                owner,
                wires.For(request.Cluster),
                settings,
                governor,
                ownerBudget,
                time,
                loggers,
                once,
                observer,
                MatchEveryRecordInRange: true,
                FocusOffset: request.Offset,
                FocusValueBytes: request.Grant.HasFlag(KafkaReadGrant.Payload) ? request.FocusValueBytes : 0);
            ScanRun run = new(inputs);
            string clusterText = KafkaLogValue.Cap(request.Cluster)!;
            string isolationText = request.Isolation.ToString();
            KStudioLog.ScanStarted(loggers.Scans, scanId, clusterText, 1, 1, offsets, isolationText);

            Task<KafkaScanResult> task;

            // The scan's thread releases the slots once the task exists; until then the caller's finally does.
            IDisposable? slots = lease;

            try
            {
                task = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            return run.Execute();
                        }
                        finally
                        {
                            run.Dispose();
                            slots?.Dispose();
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
            catch
            {
                run.Dispose();
                throw;
            }

            handOver();
            handle = new KafkaScanHandle(run, task, time);
            coordinator.Register(handle);

            KafkaScanResult result;

            using (cancellationToken.Register(static state => ((KafkaScanHandle) state!).Stop(), handle))
            {
                result = await handle.Result.ConfigureAwait(false);
            }

            IReadOnlyList<KafkaMatch> matches = handle.Matches;
            cancellationToken.ThrowIfCancellationRequested();
            return Outcome(request, range, result, matches, limits);
        }
        finally
        {
            if (handle is not null)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static KafkaRecordRead Outcome(
        KafkaRecordRequest request, PartitionRange range, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches, KafkaScanLimits limits)
    {
        string topic = request.Topic.Value;

        KafkaRecordRead Make(KafkaRecordReadStatus status, string text, KafkaMatch? record, KafkaScanStopReason? stop = null) =>
            new(
                status,
                record,
                [.. matches.Where(x => x.Offset < request.Offset)],
                [.. matches.Where(x => x.Offset > request.Offset)],
                range.LogStart,
                range.HighWatermark,
                null,
                stop,
                text,
                result.Records,
                result.Bytes);

        switch (result.StopReason)
        {
            case KafkaScanStopReason.Completed:
                {
                    KafkaMatch? found = matches.FirstOrDefault(x => x.Offset == request.Offset);

                    if (found is not null)
                    {
                        return Make(KafkaRecordReadStatus.Found, string.Empty, found);
                    }

                    // "No record" is said only of a range that was read to its end: a partition that stopped early, or lost the offset
                    // to retention while it was read, says that instead.
                    KafkaScanGap? gap = result.Gaps.FirstOrDefault(x => request.Offset >= x.FromOffset && request.Offset <= x.ToOffset);

                    if (gap is not null)
                    {
                        long logStart = gap.LogStart ?? gap.ToOffset + 1;

                        return new KafkaRecordRead(
                            KafkaRecordReadStatus.BelowLogStart,
                            null,
                            [.. matches.Where(x => x.Offset < request.Offset)],
                            [.. matches.Where(x => x.Offset > request.Offset)],
                            logStart,
                            range.HighWatermark,
                            null,
                            null,
                            KafkaRecordRead.Gone(request.Offset, topic, request.Partition, logStart),
                            result.Records,
                            result.Bytes);
                    }

                    PartitionEndState? ended = result.Partitions.Count > 0 ? result.Partitions[0].State : null;

                    return ended switch
                    {
                        null or PartitionEndState.EndOfWindow or PartitionEndState.EndOfLog or PartitionEndState.StoppedAtLso or PartitionEndState.Empty =>
                            Make(KafkaRecordReadStatus.NoRecordAtOffset, KafkaRecordRead.NoRecord(request.Offset), null),
                        PartitionEndState.Stalled or PartitionEndState.OffsetGap =>
                            Make(KafkaRecordReadStatus.Stopped, KafkaRecordRead.StopText(KafkaScanStopReason.Unavailable, limits.MaxScanDuration), null, KafkaScanStopReason.Unavailable),
                        _ => Make(KafkaRecordReadStatus.Stopped, KafkaRecordRead.StopText(KafkaScanStopReason.Failed, limits.MaxScanDuration), null, KafkaScanStopReason.Failed),
                    };
                }

            case KafkaScanStopReason.NotAuthorized:
                return Make(
                    KafkaRecordReadStatus.NotAuthorized,
                    KafkaAccessText.Needs(new WireError.NotAuthorized(KafkaAclOperation.Read, KafkaResourceType.Topic, topic)),
                    null);

            default:
                return Make(KafkaRecordReadStatus.Stopped, KafkaRecordRead.StopText(result.StopReason, limits.MaxScanDuration), null, result.StopReason);
        }
    }

    private KafkaRecordRead Refused(string cluster, KafkaScanRefusal refusal, KafkaScanLimits limits)
    {
        string clusterText = KafkaLogValue.Cap(cluster)!;
        string refusalText = refusal.ToString();
        KStudioLog.ScanRefused(loggers.Scans, clusterText, refusalText);

        return new KafkaRecordRead(
            KafkaRecordReadStatus.Refused, null, [], [], null, null, refusal, null, KafkaRecordRead.RefusalText(refusal, limits.OwnerBytesPerHour), 0, 0);
    }

    private static KafkaRecordRead FromError(KafkaRecordRequest request, WireError error) => error switch
    {
        WireError.NotFound => KafkaRecordRead.Without(KafkaRecordReadStatus.PartitionNotFound, KafkaRecordRead.NotFound(request.Partition, request.Topic.Value)),
        WireError.NotAuthorized or WireError.AuthenticationFailed => KafkaRecordRead.Without(KafkaRecordReadStatus.NotAuthorized, KafkaAccessText.Describe(error)),
        WireError.Unavailable => StoppedBy(KafkaScanStopReason.Unavailable),
        _ => StoppedBy(KafkaScanStopReason.Failed),
    };

    private static KafkaRecordRead StoppedBy(KafkaScanStopReason reason) =>
        new(KafkaRecordReadStatus.Stopped, null, [], [], null, null, null, reason, KafkaRecordRead.StopText(reason, TimeSpan.Zero), 0, 0);

    /// <summary>One offset of one partition, read now; the wire's error when it could not be.</summary>
    private async Task<(long? Offset, WireError? Error)> OffsetAsync(
        IKafkaWire wire, string cluster, WirePartition partition, WireOffsetSpec spec, WireIsolation isolation, CancellationToken cancellationToken)
    {
        WireResult<IReadOnlyList<WireOffset>> result = reads.Observe(
            cluster, WireCall.ListOffsets, await wire.ListOffsetsAsync([partition], spec, isolation, cancellationToken).ConfigureAwait(false));

        if (result.Error is { } error)
        {
            return (null, error);
        }

        WireOffset? offset = result.Value.FirstOrDefault(x => x.Partition == partition);

        if (offset is null)
        {
            return (null, new WireError.NotFound(KafkaResourceType.Topic, partition.Topic));
        }

        return offset.Error is { } offsetError ? (null, offsetError) : (Math.Max(0, offset.Offset), null);
    }
}
