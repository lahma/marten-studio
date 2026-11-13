using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The host's record index (AGENTS.md D48) on real brokers: a narrowed scan assigns explicit offsets and reads only the ranges the hints name inside the window
/// (clipped to the partition's real end), judges every record there, and a wrong hint finds nothing and never something false.
/// </summary>
public abstract class RecordIndexLiveTests(KafkaFixture fixture)
{
    private const int PerPartition = 40;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Two partitions of forty records; the key <c>needle</c> is at partition 0 offset 5 and partition 1 offsets 12 and 33.</summary>
    private static async Task<string> SeedAsync(LiveContractCluster admin, string label)
    {
        string topic = admin.NewTopic(label);
        await admin.CreateTopicAsync(topic, 2);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(
                topic,
                partition,
                [.. Enumerable.Range(0, PerPartition).Select(i => LiveRecords.At(i, (partition, i) is (0, 5) or (1, 12) or (1, 33) ? "needle" : $"k-{partition}-{i}", "v"))]);
        }

        return topic;
    }

    [Fact]
    public async Task A_right_hint_reads_only_its_range_on_a_real_broker_and_the_match_is_the_scans()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "index-right");
        ScriptedIndex index = ScriptedIndex.Answering(new KafkaIndexHint(topic, 1, 10, 15));

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", options => options.UseRecordIndex(_ => index, nameof(ScriptedIndex)));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'needle'"), Token);

        plan.Index!.IsNarrowed.Should().BeTrue();
        plan.Estimate.Offsets.Should().Be(5);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Records.Should().Be(5, "the scan assigned offsets 10 to 14 of partition 1 and read nothing else");

        // The needle at 1/33 is outside the range and was not looked for.
        matches.Select(static x => (x.Partition, x.Offset)).Should().Equal((1, 12L));
        result.Index!.NoMatch.Should().Contain("The rest of the window was not read.");
    }

    [Fact]
    public async Task A_wrong_hint_and_a_hint_past_the_end_of_the_log_find_nothing_false_on_a_real_broker()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "index-wrong");
        ScriptedIndex index = ScriptedIndex.Answering(new KafkaIndexHint(topic, 0, 20, 25), new KafkaIndexHint(topic, 1, 38, 10_000));

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", options => options.UseRecordIndex(_ => index, nameof(ScriptedIndex)));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'needle'"), Token);

        matches.Should().BeEmpty("neither range holds a needle, and the hint is where to look, not what was found");
        plan.Index!.Ranges.Single(static x => x.Partition == 1).To.Should().Be(PerPartition, "a hint is clipped to the real end of the partition");
        result.Records.Should().Be(7);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
    }

    [Fact]
    public async Task Search_the_whole_window_reads_what_the_hint_missed_on_a_real_broker()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "index-whole");
        ScriptedIndex index = ScriptedIndex.Answering(new KafkaIndexHint(topic, 0, 20, 25));

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", options => options.UseRecordIndex(_ => index, nameof(ScriptedIndex)));

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'needle'") with { IgnoreIndex = true }, Token);

        index.Queries.Should().BeEmpty();
        matches.Select(static x => (x.Partition, x.Offset)).Order().Should().Equal((0, 5L), (1, 12L), (1, 33L));
        result.Records.Should().Be(PerPartition * 2);
        result.Index.Should().BeNull();
    }

    /// <summary>A hand-written host index (the suite uses no mocking library) that answers the hints it was given and records every question.</summary>
    private sealed class ScriptedIndex : IKafkaRecordIndex
    {
        private readonly KafkaIndexHint[] hints;
        private readonly List<KafkaIndexQuery> queries = [];

        private ScriptedIndex(KafkaIndexHint[] hints) => this.hints = hints;

        public IReadOnlyList<KafkaIndexQuery> Queries
        {
            get
            {
                lock (queries)
                {
                    return [.. queries];
                }
            }
        }

        public static ScriptedIndex Answering(params KafkaIndexHint[] hints) => new(hints);

        public ValueTask<KafkaIndexAnswer> FindAsync(KafkaIndexQuery query, CancellationToken cancellationToken)
        {
            lock (queries)
            {
                queries.Add(query);
            }

            return ValueTask.FromResult(KafkaIndexAnswer.Hints(hints));
        }
    }
}

/// <summary><see cref="RecordIndexLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class RecordIndexLiveTests39(Kafka39Fixture fixture) : RecordIndexLiveTests(fixture);

/// <summary><see cref="RecordIndexLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class RecordIndexLiveTests4(Kafka4Fixture fixture) : RecordIndexLiveTests(fixture);
