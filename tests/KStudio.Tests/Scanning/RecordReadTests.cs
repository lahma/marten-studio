using System.Text.Json;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The record read (D-UI decision 1): one offset and its neighbours through the scan machinery, each status with its sentence, a scan slot
/// taken and given back, and nothing unredacted.
/// </summary>
public class RecordReadTests
{
    private static Task<KafkaRecordRead> ReadAsync(
        ScanFixture fixture,
        string topic,
        long offset,
        int neighbours = 5,
        KafkaIsolation isolation = KafkaIsolation.Committed,
        int partition = 0) =>
        fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), partition, offset, neighbours, KafkaReadGrant.Envelope, isolation),
            fixture.Owner,
            TestContext.Current.CancellationToken);

    private static void Seed(ScanFixture fixture, string topic, int count)
    {
        for (int i = 0; i < count; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }
    }

    [Fact]
    public async Task Found_returns_the_record_and_five_neighbours_each_side()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("record");
        Seed(fixture, topic, 30);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 10);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.Text.Should().BeEmpty();
        read.Record!.Offset.Should().Be(10);
        read.Record.Key.Text.Should().Be("k10");
        read.Before.Select(static x => x.Offset).Should().Equal(5, 6, 7, 8, 9);
        read.After.Select(static x => x.Offset).Should().Equal(11, 12, 13, 14, 15);
        (read.LogStart, read.End).Should().Be((0L, 30L));
        read.RecordsRead.Should().Be(11);
        read.BytesRead.Should().BePositive();
    }

    [Fact]
    public async Task At_the_log_start_there_is_nothing_before()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("start");
        Seed(fixture, topic, 20);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 0);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.Before.Should().BeEmpty();
        read.After.Select(static x => x.Offset).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task At_the_end_there_is_nothing_after()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("end");
        Seed(fixture, topic, 20);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 19);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.After.Should().BeEmpty();
        read.Before.Select(static x => x.Offset).Should().Equal(14, 15, 16, 17, 18);
    }

    [Fact]
    public async Task Neighbours_of_zero_read_only_the_record()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("zero");
        Seed(fixture, topic, 20);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 7, neighbours: 0);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        (read.Before, read.After).Should().BeEquivalentTo((Array.Empty<KafkaMatch>(), Array.Empty<KafkaMatch>()));
        read.RecordsRead.Should().Be(1);
    }

    [Fact]
    public async Task Below_the_log_start_is_BelowLogStart_and_reads_nothing()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("gone");
        Seed(fixture, topic, 20);
        fixture.Cluster.DeleteRecords(topic, 0, 8);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 3);

        read.Status.Should().Be(KafkaRecordReadStatus.BelowLogStart);
        read.Text.Should().Be($"Offset 3 is gone: the earliest offset {topic}/0 still holds is 8 (retention or DeleteRecords removed the rest).");
        read.Record.Should().BeNull();
        (read.LogStart, read.End).Should().Be((8L, 20L));
        fixture.Readers.Should().BeEmpty("no consumer is opened for an offset that is already known to be gone");
    }

    [Fact]
    public async Task Above_the_end_is_AboveEnd()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("future");
        Seed(fixture, topic, 20);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 20);

        read.Status.Should().Be(KafkaRecordReadStatus.AboveEnd);
        read.Text.Should().Be($"Offset 20 has not been written yet: {topic}/0 ends at offset 20.");
        fixture.Readers.Should().BeEmpty();
    }

    [Fact]
    public async Task Past_the_LSO_under_Committed_names_the_open_transaction()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("open");
        Seed(fixture, topic, 2);
        long transaction = fixture.Cluster.BeginTransaction();
        fixture.Cluster.Produce(topic, 0, [1], [2], null, ScanFixture.T0.ToUnixTimeMilliseconds(), transaction);

        KafkaRecordRead committed = await ReadAsync(fixture, topic, 2);

        committed.Status.Should().Be(KafkaRecordReadStatus.AboveEnd);
        committed.Text.Should().Be("Offset 2 is past the last stable offset 2: an open transaction holds it.");

        KafkaRecordRead uncommitted = await ReadAsync(fixture, topic, 2, isolation: KafkaIsolation.IncludeUncommitted);

        uncommitted.Status.Should().Be(KafkaRecordReadStatus.Found, "the high watermark is the end when uncommitted records are included");
    }

    [Fact]
    public async Task A_compacted_gap_is_NoRecordAtOffset_with_neighbours()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("marker");
        Seed(fixture, topic, 4);
        long transaction = fixture.Cluster.BeginTransaction();
        fixture.Cluster.Produce(topic, 0, [1], [2], null, ScanFixture.T0.ToUnixTimeMilliseconds(), transaction);
        fixture.Cluster.EndTransaction(topic, 0, transaction, commit: true);
        fixture.Produce(topic, 0, 10, "after", "v");

        // Offsets: 0-3 records, 4 the transaction's record, 5 its commit marker, 6 a record.
        KafkaRecordRead read = await ReadAsync(fixture, topic, 5);

        read.Status.Should().Be(KafkaRecordReadStatus.NoRecordAtOffset);
        read.Text.Should().Be("No record at offset 5: the topic is compacted, or the offset holds a transaction marker or an aborted record. The nearest records are shown.");
        read.Record.Should().BeNull();
        read.Before.Select(static x => x.Offset).Should().Equal(0, 1, 2, 3, 4);
        read.After.Select(static x => x.Offset).Should().Equal(6);
    }

    [Fact]
    public async Task A_range_that_stalled_is_Stopped_as_unavailable_never_NoRecordAtOffset()
    {
        ScriptedReader reader = new();
        await using ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, _ => reader));
        string topic = fixture.Topic("stall");
        Seed(fixture, topic, 20);

        // The reader returns nothing: the partition makes no progress until the stall timeout ends it.
        Task<KafkaRecordRead> read = ReadAsync(fixture, topic, 10);
        ScanFixture.Until(() => reader.Reads > 0, "the scan's thread is reading");
        fixture.Clock.Advance(TimeSpan.FromSeconds(16));

        KafkaRecordRead result = await read.Bounded();

        result.Status.Should().Be(KafkaRecordReadStatus.Stopped);
        result.StopReason.Should().Be(KafkaScanStopReason.Unavailable);
        result.Text.Should().Be("Stopped: the cluster stopped answering.");
        result.Record.Should().BeNull();
    }

    [Fact]
    public async Task An_offset_deleted_during_the_read_is_BelowLogStart_with_the_new_log_start()
    {
        ScriptedReader reader = new();
        await using ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, _ => reader));
        string topic = fixture.Topic("deleted");
        Seed(fixture, topic, 60);

        Task<KafkaRecordRead> read = ReadAsync(fixture, topic, 10);
        ScanFixture.Until(() => reader.Reads > 0, "the scan's thread is reading");
        fixture.Cluster.DeleteRecords(topic, 0, 50);
        reader.Enqueue(new WireReadOutcome.Failure(new WireError.OffsetOutOfRange(new WirePartition(topic, 0), null), new WirePartition(topic, 0)));

        KafkaRecordRead result = await read.Bounded();

        result.Status.Should().Be(KafkaRecordReadStatus.BelowLogStart);
        result.LogStart.Should().Be(50);
        result.Text.Should().Be($"Offset 10 is gone: the earliest offset {topic}/0 still holds is 50 (retention or DeleteRecords removed the rest).");
        result.Record.Should().BeNull();
    }

    [Fact]
    public async Task Past_the_high_watermark_under_an_open_transaction_is_not_written_yet_and_names_the_high_watermark()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("beyond");
        Seed(fixture, topic, 2);
        long transaction = fixture.Cluster.BeginTransaction();
        fixture.Cluster.Produce(topic, 0, [1], [2], null, ScanFixture.T0.ToUnixTimeMilliseconds(), transaction);

        // The last stable offset is 2 and the high watermark 3: offset 5 is past both, and no open transaction holds it.
        KafkaRecordRead read = await ReadAsync(fixture, topic, 5);

        read.Status.Should().Be(KafkaRecordReadStatus.AboveEnd);
        read.Text.Should().Be($"Offset 5 has not been written yet: {topic}/0 ends at offset 3.");
    }

    [Fact]
    public async Task A_negative_offset_is_not_an_offset_and_asks_the_broker_nothing()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("negative");
        Seed(fixture, topic, 5);
        int calls = fixture.Cluster.Calls.Count;

        KafkaRecordRead read = await ReadAsync(fixture, topic, -1);

        read.Status.Should().Be(KafkaRecordReadStatus.PartitionNotFound);
        read.Text.Should().Be("Offset -1 is not an offset.");
        fixture.Cluster.Calls.Should().HaveCount(calls);
        fixture.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0);
    }

    [Fact]
    public async Task A_refused_read_asks_the_broker_nothing()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;
        int calls = scan.Fixture.Cluster.Calls.Count;

        KafkaRecordRead busy = await ReadAsync(scan.Fixture, scan.Topic, 10);

        busy.Refusal.Should().Be(KafkaScanRefusal.OwnerBusy);
        scan.Fixture.Cluster.Calls.Should().HaveCount(calls, "the slot is taken before the first metadata or offsets call");

        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("spent");
        Seed(fixture, topic, 5);
        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();
        budget.Add(fixture.Owner.Key, budget.Remaining(fixture.Owner.Key));
        int spentCalls = fixture.Cluster.Calls.Count;

        KafkaRecordRead spent = await ReadAsync(fixture, topic, 2);

        spent.Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);
        fixture.Cluster.Calls.Should().HaveCount(spentCalls);
    }

    [Fact]
    public async Task The_slot_is_held_while_the_offsets_are_read_and_freed_on_every_early_return()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("held");
        Seed(fixture, topic, 20);
        fixture.Cluster.DeleteRecords(topic, 0, 8);
        KafkaScanLimiter limiter = fixture.Provider.GetRequiredService<KafkaScanLimiter>();
        List<int> running = [];
        fixture.Cluster.OffsetFault = (_, _, _) =>
        {
            running.Add(limiter.Running);
            return null;
        };

        (await ReadAsync(fixture, topic, 3)).Status.Should().Be(KafkaRecordReadStatus.BelowLogStart);
        (await ReadAsync(fixture, topic, 99)).Status.Should().Be(KafkaRecordReadStatus.AboveEnd);
        (await ReadAsync(fixture, topic, 0, partition: 7)).Status.Should().Be(KafkaRecordReadStatus.PartitionNotFound);

        running.Should().NotBeEmpty().And.OnlyContain(static x => x == 1, "the owner's slot is held across the offset reads");
        limiter.Running.Should().Be(0, "every early return gives the slot back");
        (await ReadAsync(fixture, topic, 10)).Status.Should().Be(KafkaRecordReadStatus.Found);
    }

    [Fact]
    public async Task A_record_with_no_timestamp_is_returned()
    {
        ScriptedReader reader = new();

        // A record read samples nothing, so the first consumer it opens is the scan's.
        await using ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, _ => reader));
        string topic = fixture.Topic("no-timestamp");
        Seed(fixture, topic, 5);

        reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(topic, 0, 2, 0, type: WireTimestampType.NotAvailable)));
        reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(topic, 0), 5));

        KafkaRecordRead read = await ReadAsync(fixture, topic, 2);

        read.Status.Should().Be(KafkaRecordReadStatus.Found, "a permalink names an offset, not a time: a record without a timestamp is still the record");
        read.Record!.TimestampType.Should().Be(WireTimestampType.NotAvailable);
    }

    [Fact]
    public async Task A_refused_READ_is_NotAuthorized_with_the_ACL_sentence()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("denied");
        Seed(fixture, topic, 10);
        fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 3);

        read.Status.Should().Be(KafkaRecordReadStatus.NotAuthorized);
        read.Text.Should().Be($"Not available: needs `READ` on topic `{topic}`");
        read.Record.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_topic_or_partition_is_PartitionNotFound_without_a_consumer()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("known");
        Seed(fixture, topic, 5);

        KafkaRecordRead missingTopic = await ReadAsync(fixture, "ks.nope", 0);
        KafkaRecordRead missingPartition = await ReadAsync(fixture, topic, 0, partition: 3);

        missingTopic.Status.Should().Be(KafkaRecordReadStatus.PartitionNotFound);
        missingTopic.Text.Should().Be("Partition 0 of topic ks.nope was not found, or this application's credentials cannot describe it.");
        missingPartition.Status.Should().Be(KafkaRecordReadStatus.PartitionNotFound);
        fixture.Readers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_running_scan_of_the_same_owner_refuses_with_OwnerBusy()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;
        KafkaScanLimiter limiter = scan.Fixture.Provider.GetRequiredService<KafkaScanLimiter>();
        int running = limiter.Running;

        KafkaRecordRead read = await ReadAsync(scan.Fixture, scan.Topic, 10);

        read.Status.Should().Be(KafkaRecordReadStatus.Refused);
        read.Refusal.Should().Be(KafkaScanRefusal.OwnerBusy);
        read.Text.Should().Be("Another search of yours is running, in this tab or another. Stop it, or wait for it to finish.");
        limiter.Running.Should().Be(running, "a refused read takes no slot");
    }

    [Fact]
    public async Task A_read_takes_a_scan_slot_and_gives_it_back()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("slot");
        Seed(fixture, topic, 20);
        KafkaScanLimiter limiter = fixture.Provider.GetRequiredService<KafkaScanLimiter>();

        (await ReadAsync(fixture, topic, 4)).Status.Should().Be(KafkaRecordReadStatus.Found);
        limiter.Running.Should().Be(0);

        (await ReadAsync(fixture, topic, 5)).Status.Should().Be(KafkaRecordReadStatus.Found, "the slot is free again for the next read of the same owner");
        fixture.Readers.Should().HaveCount(2).And.OnlyContain(static x => x.CloseCount == 1);
    }

    [Fact]
    public async Task A_read_counts_against_the_owners_hourly_budget_and_a_spent_budget_refuses()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("budget");
        Seed(fixture, topic, 20);
        OwnerByteBudget budget = fixture.Provider.GetRequiredService<OwnerByteBudget>();
        long before = budget.Remaining(fixture.Owner.Key);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 4);

        budget.Remaining(fixture.Owner.Key).Should().Be(before - read.BytesRead, "what a record read reads is paid for like any scan's bytes");

        budget.Add(fixture.Owner.Key, before);
        KafkaRecordRead refused = await ReadAsync(fixture, topic, 4);

        refused.Status.Should().Be(KafkaRecordReadStatus.Refused);
        refused.Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);
        refused.Text.Should().Be("You have read your hourly allowance of 2 GiB. It frees up as the hour rolls on.");
        fixture.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_read_stops_its_scan_throws_and_gives_the_slot_back()
    {
        ScriptedReader reader = new();
        await using ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, _ => reader));
        string topic = fixture.Topic("cancel");
        Seed(fixture, topic, 20);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // The scripted reader returns nothing, so the read runs until it is told to stop.
        Task<KafkaRecordRead> read = fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 4, 5, KafkaReadGrant.Envelope),
            fixture.Owner,
            cancel.Token);

        ScanFixture.Until(() => reader.Reads > 0, "the scan's thread is reading");
        await cancel.CancelAsync();

        Func<Task> wait = () => read.Bounded();
        await wait.Should().ThrowAsync<OperationCanceledException>();
        fixture.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0);
        reader.CloseCount.Should().Be(1);
    }

    [Fact]
    public async Task A_deny_listed_header_of_the_record_is_masked()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("masked");
        fixture.Produce(topic, 0, 0, "k", "v", ("Authorization", "Bearer s3cr3t"), ("x-tenant", "acme"));

        KafkaRecordRead read = await ReadAsync(fixture, topic, 0);

        read.Record!.Headers.Single(static x => x.Name == "Authorization").Value.IsRedacted.Should().BeTrue();
        read.Record.Headers.Single(static x => x.Name == "x-tenant").Value.Text.Should().Be("acme");
        JsonSerializer.Serialize(read).Should().NotContain("s3cr3t");
    }

    [Fact]
    public async Task A_read_asks_the_brokers_offsets_one_spec_per_call_and_never_from_the_cache()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("calls");
        Seed(fixture, topic, 10);

        await ReadAsync(fixture, topic, 3);
        fixture.Produce(topic, 0, 20, "late", "v");
        KafkaRecordRead late = await ReadAsync(fixture, topic, 10);

        late.Status.Should().Be(KafkaRecordReadStatus.Found, "a record written after the first read is found by the second: the end is read now");
        Support.FakeKafka.FakeCall[] listings = [.. fixture.Cluster.Calls.Where(static x => x.Call == WireCall.ListOffsets && x.Partitions.Count == 1)];
        listings.Should().OnlyContain(static x => x.Spec != null);
        listings.Select(static x => x.Spec!.Value.Kind).Should().Contain([WireOffsetSpecKind.Earliest, WireOffsetSpecKind.Latest]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task Neighbours_above_ten_throw(int neighbours)
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("throws");

        Func<Task> read = () => ReadAsync(fixture, topic, 0, neighbours);

        await read.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task A_grant_without_the_envelope_throws()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("grant");

        Func<Task> read = () => fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 0, 5, KafkaReadGrant.None),
            fixture.Owner,
            TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<ArgumentException>();
    }
}
