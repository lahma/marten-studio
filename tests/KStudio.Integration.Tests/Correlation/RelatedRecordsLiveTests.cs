using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Correlation;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Services;

namespace KStudio.Integration.Tests.Correlation;

/// <summary>
/// <b>The M4 exit on real brokers (D42):</b> related records across an in topic and an out topic, found by the correlation fields of each, as the
/// restricted principal <c>reader</c>. The services are KStudio's real ones composed by the real registrations (<see cref="LiveSearchHost" />), the
/// scan is one scan with one consumer whose group is never joined and commits nothing (hard rule 17), and a topic the principal may not describe is a
/// lane with the engine's sentence while the others are read.
/// </summary>
public abstract class RelatedRecordsLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private LiveSearchHost Host(params string[] declared) =>
        LiveSearchHost.Create(fixture, "reader", options =>
        {
            foreach (string topic in declared)
            {
                options.Topic(topic, _ => { });
            }
        });

    private static KafkaRelatedInput Input(LiveSearchHost host, string[] topics, CorrelationKeySet keys, string anchorTopic) =>
        new(host.Entry, KafkaRelatedSubject.Anchor(anchorTopic, 0, 0), keys, topics, LiveScanEngine.T0, DateTimeOffset.UtcNow.AddDays(1));

    private static async Task<(KafkaScanResult Result, List<KafkaMatch> Matches)> RunAsync(LiveSearchHost host, KafkaRelatedPlanned planned)
    {
        await using KafkaSearchRun run = ((KafkaRelatedStartResult.Running) await host.Correlation.StartAsync(planned, confirmed: true, Token)).Run;

        List<KafkaMatch> matches = [];
        KafkaScanResult? result = null;

        await foreach (KafkaScanBatch batch in run.ReadBatchesAsync(Token))
        {
            matches.AddRange(batch.NewMatches);

            if (batch.Final is { } final)
            {
                result = final;
                run.Complete(final);
            }
        }

        result.Should().NotBeNull("the last batch carries the result");
        return (result, matches);
    }

    [KafkaFact]
    public async Task A_wolverine_shaped_in_and_out_topic_give_the_children_and_only_them_in_one_scan_that_joins_no_group()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string incoming = admin.NewTopic("related-in");
        string outgoing = admin.NewTopic("related-out");
        string unrelated = admin.NewTopic("related-other");

        foreach (string topic in new[] { incoming, outgoing, unrelated })
        {
            await admin.CreateTopicAsync(topic, 1);
        }

        await admin.ProduceAsync(incoming, 0, [LiveRecords.At(10, "order-7", "placed", ("correlation-id", "flow-7"), ("id", "env-1")), LiveRecords.At(11, "order-8", "placed", ("correlation-id", "flow-8"), ("id", "env-2"))]);
        await admin.ProduceAsync(
            outgoing,
            0,
            [
                LiveRecords.At(12, "order-7", "billed", ("correlation-id", "flow-7"), ("conversation-id", "env-1"), ("id", "env-3")),
                LiveRecords.At(13, "order-8", "billed", ("correlation-id", "flow-8"), ("conversation-id", "env-2"), ("id", "env-4")),
            ]);
        await admin.ProduceAsync(unrelated, 0, [LiveRecords.At(14, "x", "noise", ("correlation-id", "flow-9"))]);
        string[] topicsBefore = TopicNames();

        await using LiveSearchHost host = Host(incoming, outgoing, unrelated);
        KafkaRelatedAnchorResult.Opened anchor = (KafkaRelatedAnchorResult.Opened) await host.Correlation.OpenAnchorAsync(host.Entry, incoming, 0, 0, Token);
        anchor.Keys.Of("correlation").Select(static x => x.Value).Should().Equal("flow-7");

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await host.Correlation.PlanAsync(Input(host, [incoming, outgoing, unrelated], anchor.Keys, incoming), Token)).Value;
        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(host, planned);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Select(static x => (x.Topic, x.Offset)).Should().BeEquivalentTo([(incoming, 0L), (outgoing, 0L)], "the anchor and its child, and nothing else");
        result.Partitions.Select(static x => x.Topic).Distinct(StringComparer.Ordinal).Should().BeEquivalentTo([incoming, outgoing, unrelated], "one scan reads all three topics");

        host.Ring.Where(static x => x.Action == "SearchRecords").Should().ContainSingle().Which.Succeeded.Should().BeTrue();
        host.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9600);

        TopicNames().Should().BeEquivalentTo(topicsBefore, "a scan creates no topic");

        string group = $"kstudio-{result.ScanId:N}";
        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        IReadOnlyList<string> listed = [.. (groups.Valid ?? []).Select(static x => x.GroupId)];
        listed.Should().NotContain(group, "the scan's one group id is never joined");

        LiveGroupOffsets.Committed(fixture, group, [new TopicPartition(incoming, 0), new TopicPartition(outgoing, 0), new TopicPartition(unrelated, 0)])
            .Should().BeEmpty("the scan committed nothing");
    }

    [KafkaFact]
    public async Task A_topic_the_principal_may_not_describe_is_a_lane_with_the_engines_sentence_and_the_others_are_read()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string incoming = admin.NewTopic("related-in");

        // Outside the ACL's prefix (ks.): the principal cannot even describe it.
        string hidden = $"other.related-hidden.{fixture.RunId}";
        await admin.CreateTopicAsync(incoming, 1);
        await admin.CreateTopicAsync(hidden, 1);
        await admin.ProduceAsync(incoming, 0, [LiveRecords.At(10, "order-7", "placed", ("correlation-id", "flow-7"))]);
        await admin.ProduceAsync(hidden, 0, [LiveRecords.At(11, "order-7", "billed", ("correlation-id", "flow-7"))]);

        await using LiveSearchHost host = Host(incoming, hidden);
        KafkaRelatedAnchorResult.Opened anchor = (KafkaRelatedAnchorResult.Opened) await host.Correlation.OpenAnchorAsync(host.Entry, incoming, 0, 0, Token);

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await host.Correlation.PlanAsync(Input(host, [incoming, hidden], anchor.Keys, incoming), Token)).Value;
        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(host, planned);

        matches.Should().ContainSingle().Which.Topic.Should().Be(incoming);
        result.TopicRefusals.Should().ContainSingle(x => x.Topic == hidden).Which.Text.Should().NotBeNullOrWhiteSpace();

        IReadOnlyList<KafkaTimelineLane> lanes = KafkaRelatedView.Lanes(planned.Lanes, matches, result, running: false, started: true, anchorTopic: incoming);
        lanes.Single(x => x.Topic == hidden).State.Should().StartWith("not readable: ");
        lanes.Single(x => x.Topic == incoming).Matches.Should().Be(1);
    }

    private string[] TopicNames()
    {
        using IAdminClient client = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();

        // Internal topics are the broker's own (kstudio.md D37, "The group coordinator on a pristine cluster").
        return [.. client.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic)
            .Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }
}

/// <summary><see cref="RelatedRecordsLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class RelatedRecordsLiveTests39(Kafka39Fixture fixture) : RelatedRecordsLiveTests(fixture);

/// <summary><see cref="RelatedRecordsLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class RelatedRecordsLiveTests4(Kafka4Fixture fixture) : RelatedRecordsLiveTests(fixture);
