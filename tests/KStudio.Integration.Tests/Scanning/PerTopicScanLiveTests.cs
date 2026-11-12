using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// One scan whose topics have their own predicates and grants (D42, D-CORR decision 2) on real brokers: each topic matches only its own
/// text and is read under its own grant, in one scan with one consumer whose group is never joined (hard rule 17 holds, as it does for a
/// scan with one predicate).
/// </summary>
public abstract class PerTopicScanLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_topics_with_their_own_texts_and_grants_are_one_scan_with_one_consumer_group_that_is_never_joined()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string orders = admin.NewTopic("per-topic-orders");
        string audit = admin.NewTopic("per-topic-audit");
        await admin.CreateTopicAsync(orders, 2);
        await admin.CreateTopicAsync(audit, 1);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(orders, partition, [.. Enumerable.Range(0, 40).Select(i => LiveRecords.At(i, $"o-{partition}-{i % 10}", """{"order":{"id":"o-7"}}""", ("id", $"m-{partition}-{i}")))]);
        }

        await admin.ProduceAsync(audit, 0, [.. Enumerable.Range(0, 40).Select(i => LiveRecords.At(i, $"a-{i % 10}", """{"order":{"id":"o-7"}}""", ("id", $"x-{i}")))]);

        string[] topicsBefore = TopicNames();

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaScanRequest request = LiveScanEngine.Request([orders, audit], grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload) with
        {
            PerTopic =
            [
                new KafkaTopicQuery(new KafkaTopicName(orders), "key = 'o-1-3' or key = 'o-0-3'", KafkaReadGrant.Envelope),
                new KafkaTopicQuery(new KafkaTopicName(audit), "$.order.id = 'o-7' and header.id = 'x-5'", KafkaReadGrant.Envelope | KafkaReadGrant.Payload),
            ],
        };

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(request, Token);

        plan.IsValid.Should().BeTrue();
        plan.ReadsValues.Should().BeTrue();
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Partitions.Select(static x => x.Topic).Distinct(StringComparer.Ordinal).Should().BeEquivalentTo([orders, audit], "one scan reads both topics");

        matches.Where(x => x.Topic == orders).Select(static x => x.Key.Text).Should().OnlyContain(static x => x == "o-1-3" || x == "o-0-3");
        matches.Count(x => x.Topic == orders).Should().Be(8, "keys o-0-3 and o-1-3 recur every ten records in 40");
        matches.Where(x => x.Topic == orders).Should().OnlyContain(static x => x.ValueText == null && !x.ReadWithPayload, "this topic was searched under the envelope alone");

        KafkaMatch fromAudit = matches.Single(x => x.Topic == audit);
        fromAudit.Headers.Single(static h => h.Name == "id").Value.Text.Should().Be("x-5");
        fromAudit.ValueText.Should().Contain("o-7");
        fromAudit.ReadWithPayload.Should().BeTrue();

        TopicNames().Should().BeEquivalentTo(topicsBefore, "a scan creates no topic");

        string group = $"kstudio-{result.ScanId:N}";
        using IAdminClient client = new AdminClientBuilder(fixture.Client("admin")).Build();
        ListConsumerGroupsResult groups = await client.ListConsumerGroupsAsync();
        IReadOnlyList<string> listed = [.. (groups.Valid ?? []).Select(static x => x.GroupId)];
        listed.Should().NotContain(group, "the scan's one group id is never joined");
        listed.Should().NotContain(static x => x.StartsWith("kstudio-", StringComparison.Ordinal) && x.Length == "kstudio-".Length + 32, "a per-topic scan leaves no group of any scan");

        LiveGroupOffsets.Committed(fixture, group, [new TopicPartition(orders, 0), new TopicPartition(orders, 1), new TopicPartition(audit, 0)])
            .Should().BeEmpty("the scan committed nothing");
    }

    [Fact]
    public async Task A_value_clause_on_a_topic_searched_under_the_envelope_is_refused_on_that_topic_on_a_real_broker()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string envelope = admin.NewTopic("per-topic-envelope");
        await admin.CreateTopicAsync(envelope, 1);
        await admin.ProduceAsync(envelope, 0, [LiveRecords.At(0, "k", """{"a":1}""")]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaScanRequest request = LiveScanEngine.Request([envelope], grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload) with
        {
            PerTopic = [new KafkaTopicQuery(new KafkaTopicName(envelope), "$.a = 1", KafkaReadGrant.Envelope)],
        };

        KafkaScanPlan plan = await engine.PlanAsync(request, Token);

        plan.IsValid.Should().BeFalse();
        plan.Errors.Should().ContainSingle().Which.Message.Should().StartWith($"On {envelope}: ");
    }

    private string[] TopicNames()
    {
        using IAdminClient client = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build();

        // Internal topics are the broker's own (kstudio.md D37, "The group coordinator on a pristine cluster").
        return [.. client.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic)
            .Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }
}

/// <summary><see cref="PerTopicScanLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class PerTopicScanLiveTests39(Kafka39Fixture fixture) : PerTopicScanLiveTests(fixture);

/// <summary><see cref="PerTopicScanLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class PerTopicScanLiveTests4(Kafka4Fixture fixture) : PerTopicScanLiveTests(fixture);
