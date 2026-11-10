using KStudio.Core.Clusters;
using KStudio.Core.Windows;

namespace KStudio.Tests.Contract;

/// <summary>
/// What window resolution must do (AGENTS.md D38, decision 6), on any cluster: the resolver is the engine's own, run over the fake in the
/// fast suite and over <c>apache/kafka</c> 3.9 and 4.x in the live suite. The start is exact, the end gets a margin under
/// <c>CreateTime</c> and none under <c>LogAppendTime</c>, and everything that could make "no match" untrue is a note with its wording.
/// </summary>
public abstract class WindowContract
{
    private const long Second = 1000;

    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal abstract Task<IContractCluster> OpenAsync();

    [Fact]
    public async Task Out_of_order_CreateTime_stamps_are_read_up_to_a_margin_past_the_end_and_the_result_says_so()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("margin");
        await cluster.CreateTopicAsync(topic, 1);

        // Offset 6 is stamped 25 s: a late arrival that belongs to the window [20 s, 40 s) but sits after offsets stamped 50 s.
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20, 30, 40, 50, 25, 60, 70, 80));

        ResolvedWindow narrow = await Resolve(cluster, topic, 20, 40, TimeSpan.FromSeconds(5));
        PartitionRange range = narrow.Ranges.Single();

        (range.Start, range.End).Should().Be((2L, 5L), "the start is the earliest offset stamped at or after 20 s, and the end the first stamped at or after 45 s");
        narrow.Topics.Single().TimestampKind.Should().Be(KafkaTimestampKind.CreateTime);
        range.Notes.OfType<WindowNote.CreateTimeMargin>().Should().ContainSingle()
            .Which.Text.Should().Be("A record stamped more than 5 s before a later one is not found after offset 5");

        ResolvedWindow wide = await Resolve(cluster, topic, 20, 40, TimeSpan.FromSeconds(25));

        wide.Ranges.Single().End.Should().Be(8, "a margin of 25 s reaches the late record at offset 6");
    }

    [Fact]
    public async Task A_LogAppendTime_window_has_no_margin_and_reads_exactly_the_records_stamped_inside_it()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("append");
        await cluster.CreateTopicAsync(topic, 1, new Dictionary<string, string> { ["message.timestamp.type"] = "LogAppendTime" });

        AppendBatch first = await cluster.ProduceAppendTimeAsync(topic, 0, 4, 0);
        AppendBatch second = await cluster.ProduceAppendTimeAsync(topic, 0, 3, first.MaxTimestampMs);

        ResolvedWindow resolved = await Resolve(
            cluster,
            topic,
            DateTimeOffset.FromUnixTimeMilliseconds(first.MaxTimestampMs + 1),
            DateTimeOffset.FromUnixTimeMilliseconds(second.MaxTimestampMs + 1),
            null);

        PartitionRange range = resolved.Ranges.Single();
        (range.Start, range.End).Should().Be((second.FirstOffset, second.EndOffset), "exactly the second batch: no margin under LogAppendTime");
        resolved.Topics.Single().TimestampKind.Should().Be(KafkaTimestampKind.LogAppendTime);
        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.CreateTimeMargin);
    }

    [Fact]
    public async Task A_retention_gap_is_a_note_with_the_earliest_retained_offset()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("gap");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20, 30, 40, 50, 60, 70, 80, 90));
        await cluster.DeleteRecordsAsync(topic, 0, 3);

        ResolvedWindow resolved = await Resolve(cluster, topic, 0, 100, TimeSpan.Zero);

        PartitionRange range = resolved.Ranges.Single();
        (range.Start, range.End, range.LogStart).Should().Be((3L, 10L, 3L));
        WindowNote.RetentionGap gap = range.Notes.OfType<WindowNote.RetentionGap>().Single();
        gap.Text.Should().Be(
            $"Records before {WindowNote.Time(DateTimeOffset.FromUnixTimeMilliseconds(T0 + (30 * Second)))} are gone (retention): the earliest retained record is offset 3");
    }

    [Fact]
    public async Task A_window_between_two_records_is_empty_and_is_not_a_retention_gap()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("between");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20, 30));

        ResolvedWindow resolved = await Resolve(cluster, topic, 15, 18, TimeSpan.Zero);

        PartitionRange range = resolved.Ranges.Single();
        (range.Start, range.End).Should().Be((2L, 2L));
        range.IsEmpty.Should().BeTrue();
        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.RetentionGap);
    }

    [Fact]
    public async Task A_window_before_any_record_is_empty()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("before");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20, 30));

        ResolvedWindow resolved = await Resolve(cluster, topic, -200, -100, TimeSpan.Zero);

        PartitionRange range = resolved.Ranges.Single();
        range.IsEmpty.Should().BeTrue();
        range.Start.Should().Be(0);
        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.RetentionGap, "nothing was lost: the log starts at offset 0");
    }

    [Fact]
    public async Task A_window_after_the_last_record_is_empty_at_the_end_of_the_log()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("after");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20, 30));

        ResolvedWindow resolved = await Resolve(cluster, topic, 1000, 2000, TimeSpan.Zero);

        PartitionRange range = resolved.Ranges.Single();
        (range.Start, range.End).Should().Be((4L, 4L));
    }

    [Fact]
    public async Task Under_read_committed_a_partition_stops_at_the_last_stable_offset_and_says_so()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("lso");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped(0, 10, 20));
        await using IContractTransaction transaction = await cluster.BeginTransactionAsync(topic, 0, Stamped(30, 40));

        ResolvedWindow committed = await Resolve(cluster, topic, -10, 100, TimeSpan.Zero);

        PartitionRange stopped = committed.Ranges.Single();
        (stopped.Start, stopped.End, stopped.Lso, stopped.HighWatermark).Should().Be((0L, 3L, 3L, 5L));
        stopped.Notes.OfType<WindowNote.StoppedAtLso>().Single().Text.Should().Be(
            "Stopped at offset 3: an open transaction holds offsets up to 5. Include uncommitted records to read past it.");
        committed.Notes.Should().NotContain(static x => x is WindowNote.IncludesUncommitted);

        ResolvedWindow uncommitted = await cluster.Engine.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName(topic)],
            new KafkaWindow(DateTimeOffset.FromUnixTimeMilliseconds(T0 - (10 * Second)), DateTimeOffset.FromUnixTimeMilliseconds(T0 + (100 * Second))),
            KafkaIsolation.IncludeUncommitted,
            TimeSpan.Zero,
            Token);

        uncommitted.Ranges.Single().End.Should().Be(5);
        uncommitted.Ranges.Single().Lso.Should().BeNull();
        uncommitted.Notes.OfType<WindowNote.IncludesUncommitted>().Single().Text.Should().Be(
            "Includes uncommitted records: a record may belong to a transaction that is later aborted");
        uncommitted.AllNotes.Should().NotContain(static x => x is WindowNote.StoppedAtLso);

        await transaction.AbortAsync();
    }

    private static List<ContractRecord> Stamped(params int[] seconds) =>
        [.. seconds.Select((s, i) => new ContractRecord("k" + i, "v" + i, T0 + (s * Second)))];

    private static Task<ResolvedWindow> Resolve(IContractCluster cluster, string topic, int fromSeconds, int toSeconds, TimeSpan? margin) =>
        Resolve(
            cluster,
            topic,
            DateTimeOffset.FromUnixTimeMilliseconds(T0 + (fromSeconds * Second)),
            DateTimeOffset.FromUnixTimeMilliseconds(T0 + (toSeconds * Second)),
            margin);

    private static Task<ResolvedWindow> Resolve(IContractCluster cluster, string topic, DateTimeOffset from, DateTimeOffset to, TimeSpan? margin) =>
        cluster.Engine.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName(topic)],
            new KafkaWindow(from, to),
            KafkaIsolation.Committed,
            margin,
            Token);
}
