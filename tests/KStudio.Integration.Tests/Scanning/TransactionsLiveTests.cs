using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// Transactions on a real broker (decision 6, S3c): a scan under <c>read_committed</c> stops at the last stable offset and says why, one that
/// includes uncommitted records reads past it and says that too, and the records of an aborted transaction are invisible to a committed scan.
/// </summary>
public abstract class TransactionsLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_committed_scan_stops_at_the_last_stable_offset_and_says_an_open_transaction_holds_the_rest()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("lso");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(i, "committed-" + i, "v"))]);

        await using IContractTransaction open = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(5, 5).Select(static i => LiveRecords.At(i, "open-" + i, "v"))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic]), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Select(static x => x.Key.Text).Should().Equal("committed-0", "committed-1", "committed-2", "committed-3", "committed-4");
        result.Partitions.Single().State.Should().Be(PartitionEndState.StoppedAtLso);

        WindowNote.StoppedAtLso note = plan.Notes.OfType<WindowNote.StoppedAtLso>().Should().ContainSingle().Subject;
        note.Lso.Should().Be(5);
        note.Text.Should().Be("Stopped at offset 5: an open transaction holds offsets up to 10. Include uncommitted records to read past it.");

        await open.AbortAsync();
    }

    [Fact]
    public async Task A_scan_that_includes_uncommitted_records_reads_past_the_last_stable_offset_and_says_so()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("uncommitted");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(i, "committed-" + i, "v"))]);

        await using IContractTransaction open = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(5, 5).Select(static i => LiveRecords.At(i, "open-" + i, "v"))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], isolation: KafkaIsolation.IncludeUncommitted), Token);

        matches.Select(static x => x.Key.Text).Should().Equal(
            "committed-0", "committed-1", "committed-2", "committed-3", "committed-4", "open-5", "open-6", "open-7", "open-8", "open-9");
        result.Partitions.Single().State.Should().Be(PartitionEndState.EndOfWindow);
        plan.Notes.OfType<WindowNote.IncludesUncommitted>().Should().ContainSingle()
            .Which.Text.Should().Be("Includes uncommitted records: a record may belong to a transaction that is later aborted");
        plan.Notes.OfType<WindowNote.StoppedAtLso>().Should().BeEmpty();

        await open.AbortAsync();
    }

    [Fact]
    public async Task The_records_of_an_aborted_transaction_are_invisible_to_a_committed_scan_and_the_control_records_are_not_matches()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("aborted");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 3).Select(static i => LiveRecords.At(i, "before-" + i, "v"))]);

        await using (IContractTransaction aborted = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(3, 4).Select(static i => LiveRecords.At(i, "aborted-" + i, "v"))]))
        {
            await aborted.AbortAsync();
        }

        await using (IContractTransaction committed = await admin.BeginTransactionAsync(topic, 0, [.. Enumerable.Range(7, 2).Select(static i => LiveRecords.At(i, "committed-" + i, "v"))]))
        {
            await committed.CommitAsync();
        }

        await admin.ProduceAsync(topic, 0, [LiveRecords.At(9, "after", "v")]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        (_, KafkaScanResult committedResult, IReadOnlyList<KafkaMatch> committedMatches) = await engine.RunAsync(LiveScanEngine.Request([topic]), Token);

        committedMatches.Select(static x => x.Key.Text).Should().Equal(
            ["before-0", "before-1", "before-2", "committed-7", "committed-8", "after"],
            $"the scan ended as {committedResult.StopReason}: {string.Join("; ", committedResult.Partitions)}; offsets {string.Join(",", committedMatches.Select(static x => x.Offset))}");
        committedResult.Partitions.Single().State.Should().Be(PartitionEndState.EndOfWindow);

        // Offsets are not contiguous: the markers occupy some, and the records the scan returned say where.
        committedMatches.Select(static x => x.Offset).Should().BeInAscendingOrder();
        committedMatches[3].Offset.Should().BeGreaterThan(committedMatches[2].Offset + 1, "the aborted records and their marker sit between");

        (_, _, IReadOnlyList<KafkaMatch> uncommittedMatches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], isolation: KafkaIsolation.IncludeUncommitted), Token);

        uncommittedMatches.Select(static x => x.Key.Text).Should().Contain("aborted-3", "an uncommitted scan sees the records of an aborted transaction too");
    }
}

/// <summary><see cref="TransactionsLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class TransactionsLiveTests39(Kafka39Fixture fixture) : TransactionsLiveTests(fixture);

/// <summary><see cref="TransactionsLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class TransactionsLiveTests4(Kafka4Fixture fixture) : TransactionsLiveTests(fixture);
