using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire.Confluent;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Wire;

/// <summary>
/// The offsets reader (D36, librdkafka #5040): a consumer that is never subscribed, with a dependent admin client over its handle. Built against
/// no broker here; what needs one (the lag read itself, and that the reader joins no group) is proved by the live suite.
/// </summary>
public class ConfluentOffsetsReaderTests
{
    private static (KafkaClusterDefinition Cluster, ConfluentClientEvents Events, KafkaEngineSnapshot Snapshot) Setup()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("main", new ClientConfig { BootstrapServers = "localhost:1" });
        KafkaClusterDefinition cluster = new KafkaClusterRegistry(options).Get("main");
        CapturingLoggerProvider logs = new();
        ConfluentClientEvents events = new(new KStudioLoggers(logs.CreateFactory()), new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

        return (cluster, events, KafkaEngineSnapshot.Take(options));
    }

    [Fact]
    public void A_reader_has_a_group_id_of_its_own_and_draining_and_disposing_it_are_safe_in_any_number()
    {
        (KafkaClusterDefinition cluster, ConfluentClientEvents events, KafkaEngineSnapshot snapshot) = Setup();
        using ConfluentOffsetsReader reader = new(cluster, snapshot.GroupIdPrefix, events);

        reader.GroupId.Should().StartWith(snapshot.GroupIdPrefix).And.HaveLength(snapshot.GroupIdPrefix.Length + 32);
        reader.Admin.Should().NotBeNull();

        // Nothing is queued on a consumer nobody reads from, or the queue is served at once: neither blocks and neither throws.
        for (int i = 0; i < 20; i++)
        {
            reader.Drain();
        }

        reader.Dispose();
        reader.Dispose();
        reader.Drain();
    }

    [Fact]
    public void Two_readers_never_share_a_group_id()
    {
        (KafkaClusterDefinition cluster, ConfluentClientEvents events, KafkaEngineSnapshot snapshot) = Setup();
        using ConfluentOffsetsReader first = new(cluster, snapshot.GroupIdPrefix, events);
        using ConfluentOffsetsReader second = new(cluster, snapshot.GroupIdPrefix, events);

        first.GroupId.Should().NotBe(second.GroupId);
    }

    [Fact]
    public async Task One_drain_straight_after_construction_runs_the_hosts_token_hook_which_nothing_else_serves()
    {
        // librdkafka queues the token-refresh op on the main queue inside rd_kafka_new; the consumer redirects that queue to its poll queue and
        // the dependent admin client polls only its own result queue. The lag read drains before it asks, so the first read on an OAUTHBEARER
        // cluster does not wait out its timeout (REVIEW-CRASH-FIX).
        TaskCompletionSource called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ClientConfig oauth = new()
        {
            BootstrapServers = "localhost:1",
            SecurityProtocol = SecurityProtocol.SaslPlaintext,
            SaslMechanism = SaslMechanism.OAuthBearer,
        };

        KafkaEngineOptions options = new();
        options.AddCluster("prod", oauth, c => c.OAuthBearerTokenRefresh((_, _) =>
        {
            called.TrySetResult();
            throw new InvalidOperationException("the token service is down");
        }));
        KafkaClusterDefinition cluster = new KafkaClusterRegistry(options).Get("prod");
        CapturingLoggerProvider logs = new();
        ConfluentClientEvents events = new(new KStudioLoggers(logs.CreateFactory()), new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

        using ConfluentOffsetsReader reader = new(cluster, KafkaEngineSnapshot.Take(options).GroupIdPrefix, events);
        called.Task.IsCompleted.Should().BeFalse("the hook runs when the queue is served, on no thread of its own");

        reader.Drain();

        await called.Task.Bounded();
        logs.Entries.Should().NotContain(static x => x.Message.Contains("token service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_engine_registers_the_readers_pool_and_creates_no_reader_until_a_lag_read_needs_one()
    {
        ServiceCollection services = new();
        services.AddLogging();
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new ClientConfig { BootstrapServers = "unreachable.invalid:9092" });
        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(TimeSpan.FromSeconds(5)));

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);

        provider.GetRequiredService<KafkaAdminPool<ConfluentOffsetsReader>>().ClientCount.Should().Be(0, "no consumer is built until the lag is read");
    }
}
