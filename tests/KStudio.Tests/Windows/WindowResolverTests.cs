using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Windows;

/// <summary>
/// Window resolution on the fake, beyond what <c>WindowContract</c> proves on both: the calls it makes, and the states only a fake can be put
/// in (a refused config, a refused topic, a compacted or tiered topic). Fast tests rely on a Kafka behaviour only if the contract proves it.
/// </summary>
public class WindowResolverTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int seconds) => DateTimeOffset.FromUnixTimeMilliseconds(T0 + (seconds * 1000L));

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(TimeSpan? catalogTimeToLive = null)
        {
            Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
            Cluster = new FakeKafkaCluster(Clock);
            Engine = EngineHarness.ForWire(new FakeKafkaWire(Cluster, EngineHarness.ClusterKey), Clock, catalogTimeToLive);
        }

        public ManualTimeProvider Clock { get; }

        public FakeKafkaCluster Cluster { get; }

        public EngineHarness Engine { get; }

        public void Seed(string topic, int partition, params int[] seconds)
        {
            for (int i = 0; i < seconds.Length; i++)
            {
                Cluster.Produce(topic, partition, Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"), null, T0 + (seconds[i] * 1000L));
            }
        }

        public Task<ResolvedWindow> Resolve(int from, int to, TimeSpan? margin, KafkaIsolation isolation = KafkaIsolation.Committed, params string[] topics) =>
            Engine.Resolver.ResolveAsync(
                EngineHarness.ClusterKey,
                [.. topics.Select(static x => new KafkaTopicName(x))],
                new KafkaWindow(At(from), At(to)),
                isolation,
                margin,
                Token);

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    [Fact]
    public async Task One_list_offsets_call_per_spec_batches_every_partition_of_every_topic_and_none_sends_a_partition_twice()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 3);
        rig.Cluster.CreateTopic("b", 2, new Dictionary<string, string> { ["message.timestamp.type"] = "LogAppendTime" });

        await rig.Resolve(0, 100, null, KafkaIsolation.Committed, "a", "b");

        FakeCall[] calls = [.. rig.Cluster.Calls.Where(static x => x.Call == WireCall.ListOffsets)];
        calls.Select(static x => (x.Spec!.Value.Kind, x.Isolation)).Should().BeEquivalentTo(
        [
            (WireOffsetSpecKind.Earliest, (WireIsolation?) WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.Latest, WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.Latest, WireIsolation.ReadCommitted),
            (WireOffsetSpecKind.ForTimestamp, WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.ForTimestamp, WireIsolation.ReadUncommitted),
            (WireOffsetSpecKind.ForTimestamp, WireIsolation.ReadUncommitted),
        ]);

        foreach (FakeCall call in calls)
        {
            call.Partitions.Distinct().Should().HaveCount(call.Partitions.Count, "the engine never sends a partition twice (F6)");
        }

        calls.Where(static x => x.Spec!.Value.Kind != WireOffsetSpecKind.ForTimestamp).Should().OnlyContain(
            static x => x.Partitions.Count == 5, "log start and both Latest calls carry all five partitions");
        calls.Where(static x => x.Spec!.Value.Kind == WireOffsetSpecKind.ForTimestamp).Select(static x => x.Partitions.Count).Order().Should().Equal(
            [2, 3, 5],
            "the start carries all five; the end is one call for the CreateTime topic's three and one for the LogAppendTime topic's two");
    }

    [Fact]
    public async Task Including_uncommitted_records_does_not_ask_for_the_last_stable_offset()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);

        await rig.Resolve(0, 100, null, KafkaIsolation.IncludeUncommitted, "a");

        rig.Cluster.Calls.Where(static x => x.Call == WireCall.ListOffsets).Should().NotContain(static x => x.Isolation == WireIsolation.ReadCommitted);
    }

    [Fact]
    public async Task A_refused_config_means_an_unknown_timestamp_type_a_margin_anyway_and_a_note_with_what_to_grant()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20, 30, 40);
        rig.Cluster.Deny(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, "a");

        ResolvedWindow resolved = await rig.Resolve(10, 20, TimeSpan.FromSeconds(15), KafkaIsolation.Committed, "a");

        resolved.Topics.Single().TimestampKind.Should().Be(KafkaTimestampKind.Unknown, "treated as CreateTime, so never fewer records are read");
        resolved.Topics.Single().Notes.OfType<WindowNote.ConfigNotReadable>().Single().Text.Should().Be(
            "Not available: needs `DESCRIBE_CONFIGS` on topic `a`. The topic's timestamp type is assumed to be CreateTime, so a margin is read past the window's end");
        resolved.Ranges.Single().End.Should().Be(4, "the margin of 15 s reaches the record stamped 30 s: ForTimestamp(35 s) is offset 4");
        resolved.Ranges.Single().Notes.OfType<WindowNote.CreateTimeMargin>().Should().ContainSingle();
    }

    [Fact]
    public async Task A_compacted_topic_and_tiered_storage_are_noted_with_their_wording()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("table", 1, new Dictionary<string, string>
        {
            ["cleanup.policy"] = "compact",
            ["remote.storage.enable"] = "true",
            ["local.retention.ms"] = "60000",
        });
        rig.Seed("table", 0, 0, 10);

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "table");

        resolved.Topics.Single().Notes.Select(static x => x.Text).Should().BeEquivalentTo(
            "Compacted topic: a time-window search can miss superseded keys and sees tombstones",
            "Older records are in tiered storage: reading them is slower and may cost more");
    }

    [Fact]
    public async Task Tiered_storage_is_not_noted_when_the_window_is_inside_the_local_retention()
    {
        await using Rig rig = new();
        long nowMs = rig.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        rig.Cluster.CreateTopic("hot", 1, new Dictionary<string, string> { ["remote.storage.enable"] = "true", ["local.retention.ms"] = "999999999999" });

        ResolvedWindow resolved = await rig.Engine.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName("hot")],
            new KafkaWindow(DateTimeOffset.FromUnixTimeMilliseconds(nowMs - 1000), DateTimeOffset.FromUnixTimeMilliseconds(nowMs)),
            KafkaIsolation.Committed,
            TimeSpan.Zero,
            Token);

        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.TieredStorage);
    }

    [Fact]
    public async Task A_topic_metadata_does_not_list_is_refused_not_found_and_gets_no_ranges()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("here", 1);
        rig.Seed("here", 0, 0);

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "here", "gone");

        resolved.Topics.Single(static x => x.Topic == "gone").Refusal.Should().Be(new WireError.NotFound(KafkaResourceType.Topic, "gone"));
        resolved.Topics.Single(static x => x.Topic == "here").Refusal.Should().BeNull();
        resolved.Ranges.Select(static x => x.Topic).Distinct().Should().Equal("here");
    }

    [Fact]
    public async Task A_list_offsets_call_the_cluster_refuses_whole_refuses_every_topic_it_carried()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("open", 1);
        rig.Cluster.CreateTopic("closed", 1);
        rig.Seed("open", 0, 0, 10);
        rig.Seed("closed", 0, 0, 10);
        rig.Cluster.FailNext(WireCall.ListOffsets, new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Topic, "closed"));

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "open", "closed");

        resolved.Topics.Should().OnlyContain(static x => x.Refusal != null, "a whole call that was refused refuses every topic it carried");
        resolved.Ranges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_metadata_failure_refuses_every_topic_with_that_error()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        WireError error = new WireError.Unavailable("Local_AllBrokersDown", true);
        rig.Cluster.FailNext(WireCall.Metadata, error);

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        resolved.Topics.Single().Refusal.Should().Be(error);
        resolved.Ranges.Should().BeEmpty();
    }

    [Fact]
    public async Task An_invalid_window_a_margin_out_of_range_and_a_non_utc_time_are_refused()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);

        Func<Task> reversed = () => rig.Resolve(10, 10, null, KafkaIsolation.Committed, "a");
        Func<Task> margin = () => rig.Resolve(0, 10, TimeSpan.FromHours(2), KafkaIsolation.Committed, "a");
        Func<Task> negative = () => rig.Resolve(0, 10, TimeSpan.FromSeconds(-1), KafkaIsolation.Committed, "a");
        Func<Task> local = () => rig.Engine.Resolver.ResolveAsync(
            EngineHarness.ClusterKey,
            [new KafkaTopicName("a")],
            new KafkaWindow(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(2)), At(100)),
            KafkaIsolation.Committed,
            null,
            Token);

        await reversed.Should().ThrowAsync<ArgumentException>().WithMessage("*start before it ends*");
        await margin.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await local.Should().ThrowAsync<ArgumentException>().WithMessage("*UTC*");
    }

    [Fact]
    public async Task A_retention_gap_is_logged_once_at_debug_with_the_partition_and_log_start()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20, 30);
        rig.Cluster.DeleteRecords("a", 0, 2);

        await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        CapturedLogEntry entry = rig.Engine.Logs.Entries.Single(static x => x.EventId.Id == 9660);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Be($"Partition 0 of a on {EngineHarness.ClusterKey} starts at offset 2: earlier records are gone");
    }

    [Fact]
    public async Task The_end_is_fixed_when_the_window_is_resolved_and_later_records_do_not_move_it()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20);

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");
        rig.Seed("a", 0, 30, 40);

        resolved.Ranges.Single().End.Should().Be(3);
    }

    [Theory]
    [InlineData(30, "30 s")]
    [InlineData(120, "2 min")]
    [InlineData(3600, "1 h")]
    [InlineData(90, "90 s")]
    [InlineData(1.5, "1.5 s")]
    public void A_margin_is_written_as_seconds_minutes_or_hours(double seconds, string expected)
    {
        WindowNote.Span(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public async Task A_create_time_window_with_no_margin_still_says_that_a_record_appended_after_the_end_may_be_missed()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20, 30);

        ResolvedWindow resolved = await rig.Resolve(0, 15, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        WindowNote.CreateTimeMargin note = resolved.Ranges.Single().Notes.OfType<WindowNote.CreateTimeMargin>().Should().ContainSingle().Subject;
        note.Margin.Should().Be(TimeSpan.Zero);
        note.Text.Should().Be("No margin is read past the window's end: a record stamped earlier than one before offset 2 but appended after it is not found");
    }

    [Fact]
    public async Task A_log_append_time_window_has_no_margin_note_at_margin_zero()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1, new Dictionary<string, string> { ["message.timestamp.type"] = "LogAppendTime" });
        rig.Seed("a", 0, 0, 10, 20, 30);

        ResolvedWindow resolved = await rig.Resolve(0, 15, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.CreateTimeMargin);
    }

    [Fact]
    public async Task A_retention_gap_is_reported_even_when_the_broker_returns_no_timestamp_for_the_first_record()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20, 30);
        rig.Cluster.DeleteRecords("a", 0, 2);
        rig.Cluster.OmitLookupTimestamps = true;

        ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        WindowNote.RetentionGap gap = resolved.AllNotes.OfType<WindowNote.RetentionGap>().Should().ContainSingle().Subject;
        gap.EarliestTimestamp.Should().BeNull();
        gap.Text.Should().Be("Records before offset 2 are gone (retention): the earliest retained record is offset 2");
    }

    [Fact]
    public async Task A_retention_gap_is_not_reported_when_the_first_retained_record_is_inside_the_window()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20, 30);
        rig.Cluster.DeleteRecords("a", 0, 2);

        ResolvedWindow resolved = await rig.Resolve(20, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");

        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.RetentionGap, "the first retained record (20 s) is at the window's start");
    }

    [Fact]
    public async Task A_last_stable_offset_a_broker_refuses_for_one_partition_refuses_the_topic_instead_of_reading_to_the_high_watermark()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10);
        WireError error = new WireError.Unavailable("OffsetNotAvailable", Retriable: true);
        rig.Cluster.OffsetFault = (_, spec, isolation) => spec.Kind == WireOffsetSpecKind.Latest && isolation == WireIsolation.ReadCommitted ? error : null;

        ResolvedWindow committed = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");
        ResolvedWindow uncommitted = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.IncludeUncommitted, "a");

        committed.Topics.Single().Refusal.Should().Be(error);
        committed.Ranges.Should().BeEmpty();
        uncommitted.Topics.Single().Refusal.Should().BeNull("without read_committed the last stable offset is not asked for");
        uncommitted.Ranges.Should().ContainSingle();
    }

    [Fact]
    public async Task A_window_reads_fresh_metadata_so_a_partition_added_since_the_catalog_last_read_is_included_and_it_says_when_the_partitions_were_fixed()
    {
        Rig rig = new(catalogTimeToLive: TimeSpan.FromMinutes(5));
        await using (rig)
        {
            rig.Cluster.CreateTopic("a", 1);
            (await rig.Engine.Catalog.ListTopicsAsync(EngineHarness.ClusterKey, Token)).Should().NotBeNull();
            rig.Cluster.CreateTopic("a", 3);

            ResolvedWindow resolved = await rig.Resolve(0, 100, TimeSpan.Zero, KafkaIsolation.Committed, "a");

            resolved.Ranges.Select(static x => x.Partition).Order().Should().Equal(0, 1, 2);
            resolved.Notes.OfType<WindowNote.PartitionsFixed>().Should().ContainSingle().Which.Text.Should().Be(
                "A partition added after 2026-11-09 12:00:00Z is not read: a window reads the partitions its topics had when it was resolved");
        }
    }

    [Fact]
    public async Task A_margin_reaching_past_the_last_record_has_no_end_offset_to_warn_about()
    {
        await using Rig rig = new();
        rig.Cluster.CreateTopic("a", 1);
        rig.Seed("a", 0, 0, 10, 20);

        ResolvedWindow resolved = await rig.Resolve(0, 10, TimeSpan.FromMinutes(2), KafkaIsolation.Committed, "a");

        resolved.Ranges.Single().End.Should().Be(3, "the margin reaches past the last record, so the end is the end of the log");
        resolved.AllNotes.Should().NotContain(static x => x is WindowNote.CreateTimeMargin);
    }
}
