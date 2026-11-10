using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;

namespace KStudio.Tests.Wire;

/// <summary>
/// Decision 2.4's table, key by key (AGENTS.md D37): the host's configuration cloned with the engine's overrides written last, the host's
/// instance untouched, and the admin config free of keys another role owns.
/// </summary>
public class ConfluentOverridesTests
{
    private static readonly Guid ScanId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A host that set the opposite of every overridden key.</summary>
    private static ConsumerConfig HostConsumerConfig() => new()
    {
        BootstrapServers = "broker:9092",
        SaslUsername = "app",
        SaslPassword = "secret",
        EnableAutoCommit = true,
        EnableAutoOffsetStore = true,
        GroupId = "host-group",
        GroupInstanceId = "x",
        GroupProtocol = GroupProtocol.Consumer,
        StatisticsIntervalMs = 5000,
        Debug = "all",
        ClientId = "my app/1",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        IsolationLevel = IsolationLevel.ReadUncommitted,
        EnableMetricsPush = true,
        QueuedMaxMessagesKbytes = 65536,
        FetchMaxBytes = 52428800,
        AllowAutoCreateTopics = true,
        EnablePartitionEof = false,
        ConsumeResultFields = "none",
        MaxPartitionFetchBytes = 9999,
    };

    private static KafkaClusterDefinition Definition(ClientConfig consumer, ClientConfig? admin = null)
    {
        KafkaEngineOptions options = new();

        if (admin is null)
        {
            options.AddCluster("main", consumer);
        }
        else
        {
            options.AddCluster("main", (ConsumerConfig) consumer, (AdminClientConfig) admin);
        }

        return new KafkaClusterRegistry(options).Get("main");
    }

    [Theory]
    [InlineData("allow.auto.create.topics", "false")]
    [InlineData("enable.auto.commit", "false")]
    [InlineData("enable.auto.offset.store", "false")]
    [InlineData("group.id", "kstudio-11111111222233334444555555555555")]
    [InlineData("group.protocol", "classic")]
    [InlineData("client.id", "my-app-1.kstudio")]
    [InlineData("isolation.level", "read_committed")]
    [InlineData("enable.partition.eof", "true")]
    [InlineData("auto.offset.reset", "error")]
    [InlineData("statistics.interval.ms", "0")]
    [InlineData("enable.metrics.push", "false")]
    [InlineData("queued.max.messages.kbytes", "8192")]
    [InlineData("fetch.max.bytes", "8388608")]
    [InlineData("max.partition.fetch.bytes", "1048576")]
    public void The_scan_consumer_gets_the_engines_value_whatever_the_host_set(string key, string expected)
    {
        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-");

        config.Get(key).Should().Be(expected);
    }

    [Fact]
    public void The_scan_consumer_loses_the_keys_the_engine_removes_and_keeps_the_rest()
    {
        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-");

        config.Get("group.instance.id").Should().BeNull("no static membership");
        config.Get("debug").Should().BeNull("the host's debug contexts are not the studio's");
        config.Get("dotnet.consumer.consume.result.fields").Should().BeNull("every field is needed");
        config.Get("bootstrap.servers").Should().Be("broker:9092");
        config.Get("sasl.username").Should().Be("app");
        config.Get("sasl.password").Should().Be("secret");
    }

    [Fact]
    public void An_isolation_that_includes_uncommitted_records_is_read_uncommitted()
    {
        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadUncommitted, new KafkaScanLimits(), "kstudio-");

        config.Get("isolation.level").Should().Be("read_uncommitted");
    }

    [Fact]
    public void The_group_id_uses_the_hosts_prefix_and_the_scans_id()
    {
        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "acme.");

        config.GroupId.Should().Be("acme.11111111222233334444555555555555");
    }

    [Fact]
    public void The_fetch_size_never_exceeds_what_receive_message_max_bytes_allows_and_message_max_bytes_never_exceeds_the_fetch_size()
    {
        ConsumerConfig host = HostConsumerConfig();
        host.ReceiveMessageMaxBytes = 2_000_000;
        host.MessageMaxBytes = 20_000_000;

        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(host), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-");

        config.Get("fetch.max.bytes").Should().Be("1999488", "receive.message.max.bytes - 512, which librdkafka requires");
        config.Get("max.partition.fetch.bytes").Should().Be("1048576");
        config.Get("message.max.bytes").Should().Be("1999488", "librdkafka refuses a consumer whose fetch.max.bytes is below message.max.bytes (SPIKE S3d)");
    }

    [Fact]
    public void The_limits_decide_the_queue_and_the_fetch_size()
    {
        KafkaScanLimits limits = new() { QueueKilobytes = 2048, FetchBytes = 2 * 1024 * 1024 };

        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadCommitted, limits, "kstudio-");

        config.Get("queued.max.messages.kbytes").Should().Be("2048");
        config.Get("fetch.max.bytes").Should().Be("2097152");
    }

    [Fact]
    public void Each_overridden_host_key_is_reported_by_name_and_never_by_value()
    {
        List<string> reported = [];

        ConfluentClientSettings.BuildConsumerConfig(
            Definition(HostConsumerConfig()), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-", reported.Add);

        reported.Should().Contain(
            ["enable.auto.commit", "group.id", "group.instance.id", "statistics.interval.ms", "debug", "client.id", "isolation.level", "enable.metrics.push"]);
        reported.Should().NotContain("bootstrap.servers", "a key the host set and the engine left alone is not an override");
        reported.Should().NotContain(static x => x.Contains("secret", StringComparison.Ordinal) || x.Contains("host-group", StringComparison.Ordinal));
    }

    [Fact]
    public void The_admin_client_never_auto_creates_a_topic_and_has_no_statistics_metrics_push_or_debug()
    {
        AdminClientConfig admin = new(HostConsumerConfig())
        {
            StatisticsIntervalMs = 5000,
            Debug = "all",
            ClientId = "my app/1",
            EnableMetricsPush = true,
            AllowAutoCreateTopics = true,
        };

        AdminClientConfig config = ConfluentClientSettings.BuildAdminConfig(Definition(HostConsumerConfig(), admin));

        config.Get("allow.auto.create.topics").Should().Be("false", "a default admin client creates a topic on a single-topic metadata request (SPIKE S1.6)");
        config.Get("statistics.interval.ms").Should().Be("0");
        config.Get("enable.metrics.push").Should().Be("false");
        config.Get("client.id").Should().Be("my-app-1.kstudio");
        config.Get("debug").Should().BeNull();
        config.Get("bootstrap.servers").Should().Be("broker:9092");
    }

    [Fact]
    public void The_admin_config_holds_no_key_that_only_a_consumer_or_a_producer_understands()
    {
        ConsumerConfig host = HostConsumerConfig();
        host.Set("transactional.id", "tx");
        host.Set("enable.idempotence", "true");

        AdminClientConfig config = ConfluentClientSettings.BuildAdminConfig(Definition(host));
        string[] keys = [.. config.Select(static x => x.Key)];

        keys.Should().NotContain(ConfluentPropertyScopes.ConsumerOnly);
        keys.Should().NotContain(ConfluentPropertyScopes.ProducerOnly);
    }

    [Fact]
    public void The_scan_consumer_config_holds_no_producer_only_key()
    {
        ConsumerConfig host = HostConsumerConfig();
        host.Set("transactional.id", "tx");
        host.Set("compression.type", "lz4");

        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            Definition(host), ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-");

        config.Select(static x => x.Key).Should().NotContain(ConfluentPropertyScopes.ProducerOnly);
    }

    [Fact]
    public void The_hosts_instance_is_byte_for_byte_unchanged_and_a_later_change_to_it_is_not_seen()
    {
        ConsumerConfig host = HostConsumerConfig();
        KeyValuePair<string, string>[] before = [.. host];

        KafkaEngineOptions options = new();
        options.AddCluster("main", host);
        host.Set("bootstrap.servers", "changed:9092");
        KafkaClusterDefinition definition = new KafkaClusterRegistry(options).Get("main");

        ConsumerConfig config = ConfluentClientSettings.BuildConsumerConfig(
            definition, ScanId, WireIsolation.ReadCommitted, new KafkaScanLimits(), "kstudio-");
        ConfluentClientSettings.BuildAdminConfig(definition);

        host.Where(static x => x.Key != "bootstrap.servers").Should().BeEquivalentTo(before.Where(static x => x.Key != "bootstrap.servers"));
        host.Get("bootstrap.servers").Should().Be("changed:9092");
        config.Get("bootstrap.servers").Should().Be("broker:9092", "the clone was taken when the cluster was added");
    }

    [Theory]
    [InlineData("rdkafka", "rdkafka.kstudio")]
    [InlineData("my app/1", "my-app-1.kstudio")]
    [InlineData("a_b.c-d", "a_b.c-d.kstudio")]
    public void The_client_id_is_the_hosts_with_a_suffix_and_only_safe_characters(string host, string expected)
    {
        ConfluentClientSettings.ClientId(new Dictionary<string, string> { ["client.id"] = host }).Should().Be(expected);
    }

    [Fact]
    public void A_missing_client_id_is_rdkafka_and_a_long_one_is_cut_to_255_characters()
    {
        ConfluentClientSettings.ClientId(new Dictionary<string, string>()).Should().Be("rdkafka.kstudio");
        ConfluentClientSettings.ClientId(new Dictionary<string, string> { ["client.id"] = new string('x', 400) }).Should().HaveLength(255).And.EndWith(".kstudio");
    }
}
