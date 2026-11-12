using System.Globalization;

using DotNet.Testcontainers.Containers;

using KStudio.Integration.Tests.Support;
using KStudio.Sample.Kafka;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// The sample's seed, judge, banner and refusal, without Docker (D-UI decision 18): the plan is deterministic and entirely in the past, the
/// needle the README looks for exists, the marker is written last, and the judge refuses a cluster whose <c>orders.v1</c> the sample did not seed.
/// </summary>
/// <remarks>
/// These live in the live suite's project, not the fast one: the sample hosts Marten Studio, and the fast suite is fenced from Marten
/// (<c>KStudioFenceTests</c>). They start no container and read no broker.
/// </remarks>
public class SampleSeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_seed_is_deterministic()
    {
        KafkaSeedPlan first = KafkaSeedPlan.Create(Now);
        KafkaSeedPlan second = KafkaSeedPlan.Create(Now);

        first.Records.Select(static x => x.Describe()).Should().Equal(second.Records.Select(static x => x.Describe()));
        first.Topics.Should().BeEquivalentTo(second.Topics);
        first.Commits.Should().Equal(second.Commits);
    }

    [Fact]
    public void The_seed_has_the_records_the_design_names()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        plan.Records.GroupBy(static x => x.Topic).ToDictionary(static x => x.Key, static x => x.Count()).Should().BeEquivalentTo(
            new Dictionary<string, int>
            {
                [KafkaSeedPlan.OrdersTopic] = 6000,
                [KafkaSeedPlan.PaymentsTopic] = 1500,
                [KafkaSeedPlan.CustomersTopic] = 550,
                [KafkaSeedPlan.DeadLetterTopic] = 20,
                [KafkaSeedPlan.SecretsTopic] = 10,
                [KafkaSeedPlan.MarkerTopic] = 1,
            });

        plan.Topics.ToDictionary(static x => x.Name, static x => x.Partitions).Should().BeEquivalentTo(
            new Dictionary<string, int>
            {
                [KafkaSeedPlan.OrdersTopic] = 6,
                [KafkaSeedPlan.PaymentsTopic] = 3,
                [KafkaSeedPlan.CustomersTopic] = 1,
                [KafkaSeedPlan.DeadLetterTopic] = 1,
                [KafkaSeedPlan.SecretsTopic] = 1,
                [KafkaSeedPlan.MarkerTopic] = 1,
            });

        plan.Topics.Single(static x => x.Name == KafkaSeedPlan.CustomersTopic).Config.Should().Contain("cleanup.policy", "compact");
        plan.Records.Where(static x => x.Topic == KafkaSeedPlan.CustomersTopic && x.Value is null).Should().HaveCount(50, "the compacted topic carries 50 tombstones");

        // Six partitions of about 1,000 orders each, where murmur2 puts the keys (the host's consumer has committed 800 of each, so none is shorter), three of 500 payments each.
        int[] perPartition = [.. plan.Records.Where(static x => x.Topic == KafkaSeedPlan.OrdersTopic).GroupBy(static x => x.Partition).OrderBy(static x => x.Key).Select(static x => x.Count())];
        perPartition.Should().HaveCount(6).And.OnlyContain(static x => x >= 900, "the lag panel's committed offset of 800 must lie inside every partition");
        perPartition.Sum().Should().Be(6000);
        plan.Records.Where(static x => x.Topic == KafkaSeedPlan.PaymentsTopic).GroupBy(static x => x.Partition).Select(static x => x.Count()).Should().AllBeEquivalentTo(500);
    }

    [Fact]
    public void Orders_are_where_murmur2_puts_them()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        foreach (SeedRecord order in plan.Records.Where(static x => x.Topic == KafkaSeedPlan.OrdersTopic))
        {
            order.Partition.Should().Be(KafkaPartitioner.Murmur2.Partition(System.Text.Encoding.UTF8.GetBytes(order.Key!), 6), order.Key);
        }

        // The payments are round-robin on purpose: the topic declares crc32 against them so that the page can show a verification that fails.
        plan.Records.Where(static x => x.Topic == KafkaSeedPlan.PaymentsTopic).Select(static x => x.Partition).Distinct().Should().BeEquivalentTo([0, 1, 2]);
    }

    [Fact]
    public void The_payments_crc32_declaration_fails_verification_on_the_seed()
    {
        // The verifier reads the start and the end of each partition's range and fails on the first record whose partition is not the one the declared
        // partitioner maps its key to. The seed is round-robin: in every partition both ends hold records crc32 puts elsewhere, so any sample catches it.
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        foreach (IGrouping<int, SeedRecord> partition in plan.Records.Where(static x => x.Topic == KafkaSeedPlan.PaymentsTopic).GroupBy(static x => x.Partition))
        {
            SeedRecord[] records = [.. partition];
            static bool Mismatch(SeedRecord record) => KafkaPartitioner.Crc32.Partition(System.Text.Encoding.UTF8.GetBytes(record.Key!), 3) != record.Partition;

            records.Take(20).Any(Mismatch).Should().BeTrue($"payments.v1/{partition.Key}, start of the range");
            records.TakeLast(20).Any(Mismatch).Should().BeTrue($"payments.v1/{partition.Key}, end of the range");
        }
    }

    [Fact]
    public void Every_order_has_a_customer_email_in_its_data()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        foreach (SeedRecord order in plan.Records.Where(static x => x.Topic == KafkaSeedPlan.OrdersTopic))
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(order.ValueText!);
            System.Text.Json.JsonElement customer = document.RootElement.GetProperty("data").GetProperty("customer");
            int number = int.Parse(order.Key!["order-".Length..], CultureInfo.InvariantCulture);

            customer.GetProperty("email").GetString().Should().Be(KafkaSeedPlan.CustomerEmail(number));
            customer.GetProperty("id").GetString().Should().Be("customer-" + number.ToString("000", CultureInfo.InvariantCulture));
            document.RootElement.GetProperty("data").GetProperty("customerId").GetString().Should().NotBeNull("customerId stays");
        }
    }

    [Fact]
    public void Customers_carry_a_phone_the_pattern_matches()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);
        System.Text.RegularExpressions.Regex phone = new(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", System.Text.RegularExpressions.RegexOptions.NonBacktracking, TimeSpan.FromSeconds(5));

        foreach (SeedRecord customer in plan.Records.Where(static x => x.Topic == KafkaSeedPlan.CustomersTopic && x.Value is not null))
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(customer.ValueText!);
            string number = document.RootElement.GetProperty("phone").GetString()!;
            string note = document.RootElement.GetProperty("note").GetString()!;

            phone.IsMatch(number).Should().BeTrue(number);
            phone.Matches(note).Should().ContainSingle("the note contains the phone once").Which.Value.Should().Be(number);
        }
    }

    [Fact]
    public void Every_timestamp_is_in_the_past()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        plan.Records.Should().OnlyContain(x => x.Timestamp < Now, "a record from the future would be outside every window the demo searches (F7)");
        plan.Records.Should().OnlyContain(x => x.Timestamp > Now - TimeSpan.FromHours(24), "a search over the last 24 h, the walk-through's window, must cover the whole seed");
    }

    [Fact]
    public void Timestamps_never_go_backwards_within_a_partition()
    {
        // A partition's time index is only meaningful when its timestamps are monotonic; offsets-for-times would otherwise answer from the wrong place.
        foreach (IGrouping<(string Topic, int Partition), SeedRecord> partition in KafkaSeedPlan.Create(Now).Records.GroupBy(static x => (x.Topic, x.Partition)))
        {
            partition.Select(static x => x.Timestamp).Should().BeInAscendingOrder($"{partition.Key.Topic}/{partition.Key.Partition}");
        }
    }

    [Fact]
    public void The_needle_exists()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        SeedRecord order = plan.Records.Single(static x => x.Topic == KafkaSeedPlan.OrdersTopic && x.Key == "order-0042");
        order.Header("correlation-id").Should().Be("c-0042");

        SeedRecord payment = plan.Records.Single(static x => x.Topic == KafkaSeedPlan.PaymentsTopic && x.Header("ce_subject") == "order-0042");
        payment.Header("correlation-id").Should().Be("c-0042");
        payment.Header("ce_type").Should().NotBeNull("a payment is a binary-mode CloudEvent");

        plan.Records.Count(static x => x.Header("customer-email") == "customer42@example.com").Should().Be(1, "the redacted-but-searchable header finds exactly one order");
    }

    [Fact]
    public void An_order_is_a_structured_cloud_event_with_wolverines_headers()
    {
        SeedRecord order = KafkaSeedPlan.Create(Now).Records.First(static x => x.Topic == KafkaSeedPlan.OrdersTopic);
        string json = order.ValueText!;

        json.Should().Contain("\"specversion\":\"1.0\"").And.Contain("\"source\":\"/sample/orders\"").And.Contain("\"subject\":\"order-0001\"");
        order.Headers.Select(static x => x.Name).Should().Equal("id", "message-type", "correlation-id", "conversation-id", "tenant-id", "traceparent", "customer-email");
    }

    [Fact]
    public void The_deny_listed_headers_are_on_the_secrets_topic_and_the_dead_letters_carry_wolverines_retry_headers()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        plan.Records.Where(static x => x.Topic == KafkaSeedPlan.SecretsTopic).Should().OnlyContain(static x => x.Header("x-api-key") != null && x.Header("authorization") != null);
        plan.Records.Where(static x => x.Topic == KafkaSeedPlan.DeadLetterTopic).Should().OnlyContain(
            static x => x.Header("wolverine-kafka-retry-exception-type") != null && x.Header("wolverine-kafka-retry-exception-message") != null && x.Header("correlation-id") != null);
    }

    [Fact]
    public async Task The_seeder_creates_the_topics_first_and_writes_the_marker_last()
    {
        RecordingTarget target = new();
        KafkaSeedPlan plan = KafkaSeedPlan.Create(Now);

        await KafkaSeeder.SeedAsync(target, plan, TestContext.Current.CancellationToken);

        target.Calls.Should().Equal("topics:6", "produce:" + (plan.Records.Count - 1).ToString(CultureInfo.InvariantCulture), "commit:6", "produce:1");
        target.Produced.Last().Topic.Should().Be(KafkaSeedPlan.MarkerTopic);
        target.Produced.Count(static x => x.Topic == KafkaSeedPlan.MarkerTopic).Should().Be(1);
        target.Committed.Should().OnlyContain(static x => x.Group == "sample-orders-handler" && x.Topic == "orders.v1");
    }

    [Fact]
    public void The_judge_refuses_a_foreign_orders_topic()
    {
        SampleKafkaJudgement foreign = SampleKafkaJudge.Judge(["__consumer_offsets", "orders.v1", "billing.v2"]);

        foreign.Verdict.Should().Be(SampleKafkaVerdict.Refuse);
        foreign.Message.Should().Be(
            "KStudio sample: this Kafka already has an orders.v1 topic that the sample did not seed. Point ConnectionStrings:Kafka at an empty cluster, or delete the topic.");
    }

    [Fact]
    public void The_judge_leaves_an_empty_cluster_to_be_seeded_and_a_seeded_one_alone()
    {
        SampleKafkaJudge.Judge(["__consumer_offsets", "billing.v2"]).Verdict.Should().Be(SampleKafkaVerdict.Empty);
        SampleKafkaJudge.Judge([]).Verdict.Should().Be(SampleKafkaVerdict.Empty);
        SampleKafkaJudge.Judge(["orders.v1", "payments.v1", "kstudio-sample.seed-v2"]).Verdict.Should().Be(SampleKafkaVerdict.AlreadySeeded);
    }

    [Fact]
    public void The_judge_names_an_earlier_seed()
    {
        SampleKafkaJudgement earlier = SampleKafkaJudge.Judge(["orders.v1", "payments.v1", "kstudio-sample.seed-v1"]);

        earlier.Verdict.Should().Be(SampleKafkaVerdict.Refuse);
        earlier.Message.Should().Be(
            "KStudio sample: this Kafka holds an earlier sample's topics. Delete the container (or unset KSTUDIO_SAMPLE_REUSE) so that the sample can seed it again.");
        SampleKafkaJudge.Judge(["orders.v1", "kstudio-sample.seed-v1", "kstudio-sample.seed-v2"]).Verdict.Should().Be(SampleKafkaVerdict.AlreadySeeded, "a cluster with the new marker is the sample's own");
    }

    [Fact]
    public void The_refusal_for_a_missing_docker_is_word_for_word()
    {
        SampleKafka.NoDockerMessage.Should().Be(
            "KStudio sample: no ConnectionStrings:Kafka is configured and Docker is not available to start a throwaway Kafka. Start Docker, or set ConnectionStrings:Kafka to a cluster you own (the sample creates and seeds topics there only with --allow-data-generation).");
    }

    [Fact]
    public void The_banner_names_the_containers_that_were_started_word_for_word()
    {
        SampleKafka.Banner("localhost:32770", "localhost:32771").Should().Be(
            "KStudio sample: started a throwaway Kafka at localhost:32770 and a throwaway Postgres at localhost:32771; both are deleted when this process stops. Set ConnectionStrings:Kafka and ConnectionStrings:Postgres to use your own.");

        SampleKafka.Banner("localhost:32770", null).Should().Contain("a throwaway Kafka at localhost:32770").And.NotContain("Postgres at");
        SampleKafka.Banner(null, "localhost:32771").Should().Contain("a throwaway Postgres at localhost:32771").And.NotContain("Kafka at");
        SampleKafka.Banner(null, null).Should().BeNull("nothing was started, so there is nothing to announce");
    }

    [Fact]
    public void The_banner_never_promises_a_reused_container_is_deleted()
    {
        SampleKafka.Banner("localhost:32770", "localhost:32771", reused: true).Should().Be(
            "KStudio sample: started a throwaway Kafka at localhost:32770 and a throwaway Postgres at localhost:32771; both are kept for the next run (KSTUDIO_SAMPLE_REUSE=true); docker rm -f removes a container when you are done. Set ConnectionStrings:Kafka and ConnectionStrings:Postgres to use your own.");
    }

    [Fact]
    public void Reuse_is_asked_for_only_by_a_literal_true()
    {
        SampleKafka.IsReuseRequested("true").Should().BeTrue();
        SampleKafka.IsReuseRequested(" TRUE ").Should().BeTrue();
        SampleKafka.IsReuseRequested(null).Should().BeFalse();
        SampleKafka.IsReuseRequested("1").Should().BeFalse();
        SampleKafka.IsReuseRequested("").Should().BeFalse();
    }

    [Fact]
    public void The_throwaway_broker_is_labelled_and_reused_only_under_its_own_identity()
    {
        var fresh = SampleKafka.CreateContainer(reuse: false, packet: "SAMPLE");
        var reused = SampleKafka.CreateContainer(reuse: true, packet: null);

        KafkaSampleContainers.Labels(fresh).Should().Contain("kstudio.sample", "true").And.Contain("kstudio.packet", "SAMPLE").And.NotContainKey("kstudio.sample.reuse");
        KafkaSampleContainers.Labels(reused).Should().Contain("kstudio.sample.reuse", "kstudio-sample-kafka");
        KafkaSampleContainers.Labels(reused).Should().NotContainKey("marten-studio.tests", "the live suite's reuse identity is another container's");
    }

    private sealed class RecordingTarget : IKafkaSeedTarget
    {
        public List<string> Calls { get; } = [];

        public List<SeedRecord> Produced { get; } = [];

        public List<SeedCommit> Committed { get; } = [];

        public Task CreateTopicsAsync(IReadOnlyList<SeedTopic> topics, CancellationToken cancellationToken)
        {
            Calls.Add("topics:" + topics.Count.ToString(CultureInfo.InvariantCulture));
            return Task.CompletedTask;
        }

        public Task ProduceAsync(IReadOnlyList<SeedRecord> records, CancellationToken cancellationToken)
        {
            Calls.Add("produce:" + records.Count.ToString(CultureInfo.InvariantCulture));
            Produced.AddRange(records);
            return Task.CompletedTask;
        }

        public Task CommitAsync(IReadOnlyList<SeedCommit> commits, CancellationToken cancellationToken)
        {
            Calls.Add("commit:" + commits.Count.ToString(CultureInfo.InvariantCulture));
            Committed.AddRange(commits);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A built container's labels, as Testcontainers holds them.</summary>
internal static class KafkaSampleContainers
{
    /// <summary>The labels of a container that has been built and not started.</summary>
    /// <param name="container">The container.</param>
    public static IReadOnlyDictionary<string, string> Labels(IContainer container) => ContainerReuse.ConfigurationOf(container).Labels;
}
