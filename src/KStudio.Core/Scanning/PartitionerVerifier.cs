using System.Collections.Concurrent;
using System.Globalization;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Predicates;
using KStudio.Core.Wire;
using KStudio.Core.Windows;

namespace KStudio.Core.Scanning;

/// <summary>What sampling a window said about a topic's declared partitioner (D-PAYLOAD decision 7.4).</summary>
internal abstract record PartitionerCheck
{
    private PartitionerCheck()
    {
    }

    /// <summary>At least 100 keyed records were checked, at least one from every non-empty range, and none disagreed: pruning is on.</summary>
    /// <param name="Checked">How many keyed records were checked.</param>
    public sealed record Verified(int Checked) : PartitionerCheck;

    /// <summary>A record is in a partition its key does not map to: pruning is off for the topic.</summary>
    /// <param name="Partition">The partition the record is in.</param>
    /// <param name="Offset">Its offset.</param>
    /// <param name="Expected">The partition the partitioner maps its key to.</param>
    public sealed record Mismatch(int Partition, long Offset, int Expected) : PartitionerCheck;

    /// <summary>The window holds fewer than 100 keyed records that could be checked.</summary>
    /// <param name="Checked">How many were checked.</param>
    public sealed record TooFewKeys(int Checked) : PartitionerCheck;

    /// <summary>A non-empty range gave no keyed record to check.</summary>
    /// <param name="Partition">The partition.</param>
    public sealed record RangeWithoutKeys(int Partition) : PartitionerCheck;

    /// <summary>The sample could not be read (the broker refused it, a reader failed, or the time or the bytes ran out before 100 records).</summary>
    public sealed record NotReadable : PartitionerCheck;
}

/// <summary>
/// What a topic's declared partitioner came to for one search: the partitions to read, or why all of them are (D-PAYLOAD decisions 7.4 to
/// 7.6). The sentences are the engine's; the pages show them.
/// </summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partitioner">The declared partitioner's name; <see langword="null" /> when the topic declared none.</param>
/// <param name="PartitionCount">How many partitions the topic had when the window was resolved.</param>
/// <param name="Partitions">The partitions read, ascending; empty when the topic is not pruned.</param>
/// <param name="Check">The verification's outcome, when the window was sampled.</param>
/// <param name="NotPrunedReason">Why every partition is read; <see langword="null" /> when the topic is pruned.</param>
/// <param name="KeyRedacted">Whether a redaction rule masks the topic's key: the audit text then says how many partitions were read, never which.</param>
internal sealed record KafkaTopicPruning(
    string Topic,
    string? Partitioner,
    int PartitionCount,
    IReadOnlyList<int> Partitions,
    PartitionerCheck? Check,
    string? NotPrunedReason,
    bool KeyRedacted = false)
{
    /// <summary>Whether the topic is read in some of its partitions only.</summary>
    public bool IsPruned => NotPrunedReason is null && Partitions.Count > 0;

    /// <summary>How many keyed records the verification checked (0 when it did not pass).</summary>
    public int Checked => Check is PartitionerCheck.Verified verified ? verified.Checked : 0;

    /// <summary>How the key clause runs: one partition, or several.</summary>
    public ClauseVerdictKind VerdictKind => Partitions.Count == 1 ? ClauseVerdictKind.PointLookup : ClauseVerdictKind.PartitionPruned;

    /// <summary>The verdict of the key clause on this topic, once the sample passed.</summary>
    public string? VerdictText => !IsPruned
        ? null
        : Partitions.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"one partition: {Partitioner} maps the key to partition {Partitions[0]} of {PartitionCount} (verified on {Checked:N0} records)")
            : string.Create(CultureInfo.InvariantCulture, $"pruned to partitions {List(Partitions)} of {PartitionCount} ({Partitioner}, verified on {Checked:N0} records)");

    /// <summary>The sentence beside a result's negatives: what the search did not read.</summary>
    public string? Caveat => !IsPruned
        ? null
        : string.Create(
            CultureInfo.InvariantCulture,
            $"This search read only {(Partitions.Count == 1 ? "partition" : "partitions")} {List(Partitions)} of {PartitionCount} of {Topic} ({Partitioner}, verified on {Checked:N0} records).");

    /// <summary>What the audit message adds for this topic: <c>; pruned to partition 3 of orders</c>, or <c>; pruned to 1 of 6 partitions of orders</c> where the key is masked.</summary>
    public string AuditSuffix => !IsPruned
        ? string.Empty
        : KeyRedacted
            ? string.Create(CultureInfo.InvariantCulture, $"; pruned to {Partitions.Count} of {PartitionCount} partitions of {Topic}")
            : string.Create(CultureInfo.InvariantCulture, $"; pruned to {(Partitions.Count == 1 ? "partition" : "partitions")} {List(Partitions)} of {Topic}");

    /// <summary>Writes partitions as <c>3</c>, <c>1 and 3</c> or <c>1, 2 and 3</c>.</summary>
    /// <param name="partitions">The partitions.</param>
    public static string List(IReadOnlyList<int> partitions)
    {
        string[] texts = [.. partitions.Select(static x => x.ToString(CultureInfo.InvariantCulture))];

        return texts.Length switch
        {
            0 => string.Empty,
            1 => texts[0],
            _ => string.Join(", ", texts[..^1]) + " and " + texts[^1],
        };
    }
}

/// <summary>What the verifier is asked about one topic.</summary>
/// <param name="Cluster">The cluster's key.</param>
/// <param name="Topic">The topic.</param>
/// <param name="Descriptor">What the host declared for it.</param>
/// <param name="KeySet">The literals the search fixes the key to; <see langword="null" /> when it does not fix it.</param>
/// <param name="KeyRedacted">Whether a redaction rule masks the topic's key: a mismatch then does not say which partition the record's key maps to.</param>
/// <param name="DisablePruning">Whether the visitor asked to search every partition.</param>
/// <param name="PartitionCount">The topic's partitions when the window was resolved.</param>
/// <param name="Ranges">The topic's ranges of this scan, every partition.</param>
/// <param name="Isolation">The isolation the scan reads under.</param>
/// <param name="Previous">What the scan this one continues proved for the topic, when it proved a pruning: the same claim is kept, not sampled again.</param>
internal sealed record PruneRequest(
    string Cluster,
    string Topic,
    KafkaTopicDescriptor Descriptor,
    IReadOnlyList<string>? KeySet,
    bool KeyRedacted,
    bool DisablePruning,
    int PartitionCount,
    IReadOnlyList<PartitionRange> Ranges,
    WireIsolation Isolation,
    KafkaTopicPruning? Previous = null);

/// <summary>
/// Decides, topic by topic, whether a key search may read fewer partitions, and proves the host's claim before it does (AGENTS.md D39,
/// D-PAYLOAD decisions 7.3 and 7.4): a declared partitioner is sampled over the window, one record that disagrees turns pruning off for the
/// topic and the answer is kept for ten minutes, and a proof is never reused, because partitions added since, or a producer that changed,
/// would make it wrong. The sample is a short consumer with explicit offsets, like <see cref="RecordSizeSampler" />'s; its bytes count
/// against the owner.
/// </summary>
internal sealed class PartitionerVerifier(
    KafkaWires wires,
    KafkaEngineSnapshot settings,
    OwnerByteBudget budget,
    KStudioLoggers loggers,
    TimeProvider time)
{
    /// <summary>The fewest offsets a window may have for pruning to be worth proving.</summary>
    public const long MinWindowOffsets = 400;

    /// <summary>The fewest keyed records a verification must check.</summary>
    public const int MinCheckedRecords = 100;

    /// <summary>The most keyed records one verification asks for.</summary>
    public const int MaxSampleRecords = 1_000;

    /// <summary>The most bytes one verification reads.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>The longest one verification takes.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(5);

    /// <summary>How long a mismatch is kept.</summary>
    public static readonly TimeSpan MismatchTimeToLive = TimeSpan.FromMinutes(10);

    private const int SampleQueueKilobytes = 1_024;
    private const int SampleFetchBytes = 1024 * 1024;
    private const int MaxIdleReads = 30;
    private const int MinPerRange = 4;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PartitionerCheck.Mismatch Mismatch)> mismatches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> perCluster = new(StringComparer.Ordinal);

    /// <summary>Decides one topic.</summary>
    /// <param name="request">The topic and what the search says about its key.</param>
    /// <param name="owner">Whose scan it is: the sample's bytes count against the owner's budget.</param>
    /// <param name="cost">The cluster's own allowance (D47): a verification is refused when either allowance is spent, and its bytes count against both.</param>
    /// <param name="cancellationToken">Abandons the sampling.</param>
    public async Task<KafkaTopicPruning> DecideAsync(PruneRequest request, KafkaScanOwner owner, KafkaClusterCost cost, CancellationToken cancellationToken)
    {
        KafkaPartitioner? partitioner = request.Descriptor.Partitioner;

        if (partitioner is null)
        {
            return NotPruned(request, null, null, "the topic declares no partitioner");
        }

        if (request.KeySet is not { } keySet)
        {
            return NotPruned(request, partitioner, null, "the search does not fix the key with = or in");
        }

        if (request.DisablePruning)
        {
            return NotPruned(request, partitioner, null, "you asked to search every partition");
        }

        KafkaKeyFormat format = request.Descriptor.KeyFormat;

        if (!KeyPruning.FormsAreComplete(format, keySet))
        {
            return NotPruned(
                request,
                partitioner,
                null,
                "a JSON-string key with characters other than letters, digits, space and -_.:@ is escaped differently by different serializers");
        }

        if (KeyPruning.Forms(format, keySet) is not { Count: > 0 } forms)
        {
            // A literal the format cannot write is a compile error long before this point; if it is ever reached, nothing is claimed.
            return NotPruned(request, partitioner, null, "the search does not fix the key with = or in");
        }

        List<byte[]> offenders;

        try
        {
            offenders = [.. forms.Where(x => !partitioner.IsDeterministic(x))];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Host code: a plan never fails for it.
            LogFailure(request, exception.GetType().Name);
            return NotPruned(request, partitioner, new PartitionerCheck.NotReadable(), "the sample could not be read");
        }

        if (offenders.Count > 0)
        {
            return NotPruned(
                request,
                partitioner,
                null,
                offenders.Any(static x => x.Length == 0)
                    ? $"{partitioner.Name} does not decide the partition of an empty key"
                    : $"{partitioner.Name} does not decide the partition of every key the search names");
        }

        // Where the keys map to is known before anything is read: a key set that reaches every partition has nothing to skip.
        if (!TryPartitions(request, partitioner, forms, out List<int> partitions))
        {
            return NotPruned(request, partitioner, new PartitionerCheck.NotReadable(), "the sample could not be read");
        }

        if (partitions.Count >= request.PartitionCount)
        {
            return NotPruned(request, partitioner, null, "the keys the search names map to every partition of the topic, so there is nothing to skip");
        }

        if (request.Previous is { IsPruned: true } previous
            && previous.PartitionCount == request.PartitionCount
            && previous.Partitioner == partitioner.Name
            && previous.Partitions.SequenceEqual(partitions))
        {
            // A continued scan keeps what its first scan proved: same window, same verdict, and no second sample.
            return previous.KeyRedacted == request.KeyRedacted ? previous : previous with { KeyRedacted = request.KeyRedacted };
        }

        long offsets = request.Ranges.Sum(static x => Math.Max(0, x.End - x.Start));

        if (offsets < MinWindowOffsets)
        {
            return NotPruned(request, partitioner, null, "the window has fewer than 400 offsets, so it is read whole");
        }

        PartitionerCheck check = await CheckAsync(request, partitioner, owner, cost, cancellationToken).ConfigureAwait(false);

        switch (check)
        {
            case PartitionerCheck.Verified:
                return new KafkaTopicPruning(request.Topic, partitioner.Name, request.PartitionCount, partitions, check, null, request.KeyRedacted);

            case PartitionerCheck.Mismatch mismatch:
                string expected = request.KeyRedacted ? "another partition" : "partition " + mismatch.Expected.ToString(CultureInfo.InvariantCulture);
                return NotPruned(
                    request,
                    partitioner,
                    check,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"record {mismatch.Partition}/{mismatch.Offset} is in partition {mismatch.Partition}, and {partitioner.Name} maps its key to {expected}"));

            case PartitionerCheck.TooFewKeys few:
                return NotPruned(
                    request,
                    partitioner,
                    check,
                    string.Create(CultureInfo.InvariantCulture, $"only {few.Checked:N0} keyed records could be checked; at least {MinCheckedRecords} are needed"));

            case PartitionerCheck.RangeWithoutKeys without:
                return NotPruned(request, partitioner, check, string.Create(CultureInfo.InvariantCulture, $"partition {without.Partition} gave no keyed record to check"));

            default:
                return NotPruned(request, partitioner, check, "the sample could not be read");
        }
    }

    /// <summary>The partitions the byte forms map to, ascending; false when the host's partitioner threw or answered a partition the topic does not have.</summary>
    private bool TryPartitions(PruneRequest request, KafkaPartitioner partitioner, IReadOnlyList<byte[]> forms, out List<int> partitions)
    {
        partitions = [];

        try
        {
            foreach (byte[] form in forms)
            {
                int partition = partitioner.Partition(form, request.PartitionCount);

                if (partition < 0 || partition >= request.PartitionCount)
                {
                    return false;
                }

                if (!partitions.Contains(partition))
                {
                    partitions.Add(partition);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(request, exception.GetType().Name);
            return false;
        }

        partitions.Sort();
        return true;
    }

    private static KafkaTopicPruning NotPruned(PruneRequest request, KafkaPartitioner? partitioner, PartitionerCheck? check, string reason) =>
        new(request.Topic, partitioner?.Name, request.PartitionCount, [], check, reason, request.KeyRedacted);

    private async Task<PartitionerCheck> CheckAsync(PruneRequest request, KafkaPartitioner partitioner, KafkaScanOwner owner, KafkaClusterCost cost, CancellationToken cancellationToken)
    {
        string key = string.Create(CultureInfo.InvariantCulture, $"{request.Cluster}|{request.Topic}|{partitioner.Name}|{request.PartitionCount}");

        if (Remembered(key) is { } known)
        {
            return known;
        }

        // An owner who has read the hour's bytes reads no more, a verification included.
        if (cost.Remaining(owner.Key, budget) <= 0)
        {
            LogFailure(request, "OwnerBudget");
            return new PartitionerCheck.NotReadable();
        }

        SemaphoreSlim gate = perCluster.GetOrAdd(request.Cluster, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another plan may have found the mismatch while this one waited.
            if (Remembered(key) is { } learned)
            {
                return learned;
            }

            PartitionerCheck check = await Task.Factory
                .StartNew(() => Sample(request, partitioner, owner, cost, cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);

            if (check is PartitionerCheck.Mismatch mismatch)
            {
                mismatches[key] = (time.GetUtcNow(), mismatch);
            }

            return check;
        }
        finally
        {
            gate.Release();
        }
    }

    private PartitionerCheck.Mismatch? Remembered(string key) =>
        mismatches.TryGetValue(key, out (DateTimeOffset At, PartitionerCheck.Mismatch Mismatch) known) && time.GetUtcNow() - known.At < MismatchTimeToLive
            ? known.Mismatch
            : null;

    private PartitionerCheck Sample(PruneRequest request, KafkaPartitioner partitioner, KafkaScanOwner owner, KafkaClusterCost cost, CancellationToken cancellationToken)
    {
        List<PartitionRange> ranges = [.. request.Ranges.Where(static x => !x.IsEmpty)];
        long bytes = 0;
        IWireScanReader? reader = null;

        try
        {
            long byteBound = Math.Min(MaxBytes, Math.Max(0, cost.Remaining(owner.Key, budget)));
            long offsets = ranges.Sum(static x => x.End - x.Start);
            int target = (int) Math.Min(MaxSampleRecords, offsets / 4);
            int quota = Math.Max(MinPerRange, (int) Math.Ceiling(target / (double) ranges.Count));
            int firstHalf = (quota + 1) / 2;
            int lastHalf = quota - firstHalf;

            // Small, like the size sampler's consumer: a verification is outside S and the governor.
            KafkaScanLimits small = settings.Limits.Copy();
            small.QueueKilobytes = SampleQueueKilobytes;
            small.FetchBytes = SampleFetchBytes;
            WireReaderOptions options = new(Guid.NewGuid(), request.Isolation, small);
            reader = wires.For(request.Cluster).OpenReader(options);

            // The start of each range, where records produced before a partition-count change sit, and then its end, where a change made
            // late in the window sits: half the quota is read from each.
            reader.Assign([.. ranges.Select(static x => new WirePartitionOffset(x.Topic, x.Partition, x.Start))]);

            Dictionary<WirePartition, RangeSample> samples = ranges.ToDictionary(static x => x.Key, static x => new RangeSample(x.End));
            HashSet<WirePartition> finished = [];
            DateTimeOffset deadline = time.GetUtcNow() + MaxDuration;
            int idle = 0;
            int checkedCount = 0;
            bool refused = false;

            while (finished.Count < ranges.Count
                && bytes < byteBound
                && idle < MaxIdleReads
                && time.GetUtcNow() < deadline
                && !cancellationToken.IsCancellationRequested)
            {
                switch (reader.Read(TimeSpan.FromMilliseconds(100)))
                {
                    case WireReadOutcome.Record { Value: { } record }:
                        idle = 0;
                        WirePartition place = new(record.Topic, record.Partition);

                        if (!samples.TryGetValue(place, out RangeSample? sample) || finished.Contains(place))
                        {
                            break;
                        }

                        if (record.Offset < sample.MinOffset)
                        {
                            // Fetched before the jump to the end of the range: read and counted, not checked.
                            bytes += record.Size;
                            break;
                        }

                        if (record.Offset >= sample.End)
                        {
                            // Past the range's end: ignored, and the partition is done.
                            Finish(reader, finished, place);
                            break;
                        }

                        bytes += record.Size;
                        sample.Read++;

                        if (record.Key is not { } recordKey)
                        {
                            sample.NullKeys++;
                        }
                        else if (!partitioner.IsDeterministic(recordKey))
                        {
                            sample.Skipped++;
                        }
                        else
                        {
                            int expected = partitioner.Partition(recordKey, request.PartitionCount);

                            if (expected != record.Partition)
                            {
                                return new PartitionerCheck.Mismatch(record.Partition, record.Offset, expected);
                            }

                            sample.Keyed++;
                            checkedCount++;
                        }

                        if (sample.Read >= quota)
                        {
                            Finish(reader, finished, place);
                        }
                        else if (!sample.Jumped && sample.Read >= firstHalf)
                        {
                            sample.Jumped = true;
                            long resume = Math.Max(record.Offset + 1, sample.End - lastHalf);

                            if (resume >= sample.End)
                            {
                                Finish(reader, finished, place);
                            }
                            else if (resume > record.Offset + 1)
                            {
                                reader.Reposition(new WirePartitionOffset(record.Topic, record.Partition, resume));
                                sample.MinOffset = resume;
                            }
                        }

                        break;

                    case WireReadOutcome.PartitionEnd reached:
                        finished.Add(reached.Partition);
                        break;

                    case WireReadOutcome.Failure { Error: WireError.NotAuthorized { ResourceType: KafkaResourceType.Topic } }:
                        refused = true;
                        idle = MaxIdleReads;
                        break;

                    default:
                        idle++;
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            bool complete = finished.Count == ranges.Count;
            PartitionRange? withoutKeys = ranges.FirstOrDefault(x => samples[x.Key].Keyed == 0);

            if (refused)
            {
                LogFailure(request, "NotAuthorized");
                return new PartitionerCheck.NotReadable();
            }

            if (!complete)
            {
                // The time, the bytes or the broker ran out. What was read still counts for a pass; nothing else is claimed from a partial read.
                if (checkedCount >= MinCheckedRecords && withoutKeys is null)
                {
                    return new PartitionerCheck.Verified(checkedCount);
                }

                LogFailure(request, bytes >= byteBound ? "ByteLimit" : idle >= MaxIdleReads ? "Idle" : "Deadline");
                return new PartitionerCheck.NotReadable();
            }

            if (checkedCount < MinCheckedRecords)
            {
                return new PartitionerCheck.TooFewKeys(checkedCount);
            }

            return withoutKeys is not null ? new PartitionerCheck.RangeWithoutKeys(withoutKeys.Partition) : new PartitionerCheck.Verified(checkedCount);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A reader failure, or a host partitioner that threw whatever it threw: nothing is claimed, and a plan never fails for it.
            LogFailure(request, exception.GetType().Name);
            return new PartitionerCheck.NotReadable();
        }
        finally
        {
            budget.Add(owner.Key, bytes);
            cost.OwnerBudget?.Add(owner.Key, bytes);

            try
            {
                reader?.Close();
            }
            catch (WireException)
            {
                // The reader is gone either way.
            }
        }
    }

    private static void Finish(IWireScanReader reader, HashSet<WirePartition> finished, WirePartition place)
    {
        if (finished.Add(place))
        {
            reader.Pause([place]);
        }
    }

    private void LogFailure(PruneRequest request, string kind)
    {
        string topic = KafkaLogValue.Cap(request.Topic)!;
        string cluster = KafkaLogValue.Cap(request.Cluster)!;
        KStudioLog.PartitionerSampleFailed(loggers.Scans, topic, cluster, kind);
    }

    private sealed class RangeSample(long end)
    {
        public long End { get; } = end;

        public int Read { get; set; }

        public bool Jumped { get; set; }

        public long MinOffset { get; set; }

        public int Keyed { get; set; }

        public int NullKeys { get; set; }

        public int Skipped { get; set; }
    }
}
