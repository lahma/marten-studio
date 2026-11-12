using System.Collections;
using System.Reflection;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using KStudio.Core.Catalog;
using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using JasperFx.CodeGeneration;

using KStudio.Integration.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace KStudio.Integration.Tests.Wolverine;

/// <summary>Kafka 3.9 for the application's own cluster and Kafka 4.x for its tenant's: one collection holding both brokers.</summary>
public static class WolverineCollections
{
    /// <summary>The collection with both brokers.</summary>
    public const string Pair = "kafka-wolverine-pair";
}

/// <summary>Both brokers, one fixture each.</summary>
[CollectionDefinition(WolverineCollections.Pair, DisableParallelization = true)]
public sealed class WolverinePairGroup : ICollectionFixture<Kafka39Fixture>, ICollectionFixture<Kafka4Fixture>;

/// <summary>The one message type of the host under test; nothing handles it, and nothing is ever sent to the listener.</summary>
/// <param name="Id">An id.</param>
public sealed record WolverineLiveFact(string Id);

/// <summary>
/// D43, decisions 5 and 6, against a real Wolverine host: Kafka 3.9 for the application's own cluster, Kafka 4.x for the broker of its tenant
/// <c>acme</c>, a listener with the native dead-letter queue and a sender, <c>AddPostgresStudio()</c> with no data source beside
/// <c>AddKafkaStudio(k =&gt; k.UseWolverineKafka())</c>. The adapter reads the started host's configuration; a scan of the tenant entry reads the tenant
/// broker's record and not the parent's; and on both brokers KStudio created nothing, joined nothing and committed nothing, while Wolverine's own group
/// offsets and every configuration of the host are as they were.
/// </summary>
[Collection(WolverineCollections.Pair)]
public sealed class WolverineDiscoveryLiveTests(Kafka39Fixture parent, Kafka4Fixture tenant)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ClientConfig WithSasl(ClientConfig config, KafkaFixture broker)
    {
        config.BootstrapServers = broker.Bootstrap;
        config.SecurityProtocol = SecurityProtocol.SaslPlaintext;
        config.SaslMechanism = SaslMechanism.Plain;
        config.SaslUsername = "admin";
        config.SaslPassword = KafkaFixture.Password("admin");
        return config;
    }

    [KafkaFact]
    public async Task A_started_wolverine_host_is_listed_and_a_scan_of_the_tenant_reads_the_tenants_broker_and_writes_nothing()
    {
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        string run = parent.RunId;
        string service = "wolv-live-" + run;
        string listen = "wolv.in." + run;
        string send = "wolv.out." + run;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddWolverine(options =>
        {
            options.ServiceName = service;
            options.UseKafka(parent.Bootstrap)
                .ConfigureClient(config => WithSasl(config, parent))
                .AutoProvision()
                .AddTenant("acme", expression => expression.ConfigureClient(config => config.BootstrapServers = tenant.Bootstrap));
            options.ListenToKafkaTopic(listen).EnableNativeDeadLetterQueue();
            options.PublishMessage<WolverineLiveFact>().ToKafkaTopic(send);
            options.Durability.Mode = DurabilityMode.Solo;

            // No handler and no runtime compiler: the host under test only declares transports, which is all the adapter reads, and
            // Wolverine 6.61 refuses to start in its dynamic code mode without the compiler package (a package this test does not need).
            options.Discovery.DisableConventionalDiscovery();
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        });
        builder.Services.AddPostgresStudio();
        builder.Services.AddKafkaStudio(kafka => kafka.UseWolverineKafka());

        await using WebApplication app = builder.Build();
        await app.StartAsync(Token);

        try
        {
            WolverineOptions host = app.Services.GetRequiredService<WolverineOptions>();
            KafkaTransport transport = host.Transports.OfType<KafkaTransport>().Single();

            // Wolverine's tenant clusters are created on the tenant broker by AutoProvision; the topics exist on both brokers when the host is up.
            WaitForTopics(parent, listen, send, "wolverine-dead-letter-queue");
            WaitForTopics(tenant, send);

            using IProducer<string, string> parentProducer = new ProducerBuilder<string, string>(new ProducerConfig(WithSasl(new ClientConfig(), parent))).Build();
            using IProducer<string, string> tenantProducer = new ProducerBuilder<string, string>(new ProducerConfig(WithSasl(new ClientConfig(), tenant))).Build();
            await parentProducer.ProduceAsync(send, new Message<string, string> { Key = "parent-key", Value = "from the parent broker" }, Token);
            await tenantProducer.ProduceAsync(send, new Message<string, string> { Key = "tenant-key", Value = "from the tenant broker" }, Token);

            // Wolverine's listeners join their groups a moment after the host starts, on each broker: wait until they have (a condition), so that
            // "before" is Wolverine at rest and anything that changes afterwards is KStudio's.
            await WaitForGroupAsync(parent, service);
            await WaitForGroupAsync(tenant, service);

            string[] hostBefore = HostConfigurations(transport);
            hostBefore.Should().Contain(static x => x.StartsWith("tenant acme consumer:", StringComparison.Ordinal), "the premise: the tenants' configurations are in the snapshot");
            string[] groupsBefore = await GroupsAsync(parent);
            string[] tenantGroupsBefore = await GroupsAsync(tenant);
            string[] topicsBefore = Topics(parent);
            string[] tenantTopicsBefore = Topics(tenant);

            KafkaClusterRegistry registry = app.Services.GetRequiredService<KafkaClusterRegistry>();

            registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine/acme");
            registry.Notes.Should().BeEmpty("the host had connected its tenant before KStudio read the configuration");

            KafkaClusterDefinition own = registry.Get("wolverine");
            KafkaClusterDefinition acme = registry.Get("wolverine/acme");
            (acme.BaseKey, acme.TenantId, acme.Source).Should().Be(("wolverine", "acme", "Wolverine"));
            acme.ConsumerSettings["bootstrap.servers"].Should().Be(tenant.Bootstrap);
            own.ConsumerSettings["bootstrap.servers"].Should().Be(parent.Bootstrap);

            // The catalog: the listener with its group, the sender, and the native dead-letter topic.
            KafkaDiscoveredTopic listener = own.Descriptor(listen).Discovered!;
            (listener.Role, listener.ConsumerGroup).Should().Be((KafkaTopicRole.Listener, service));
            own.Descriptor(send).Discovered!.Role.Should().Be(KafkaTopicRole.Sender);
            own.Descriptor("wolverine-dead-letter-queue").Discovered!.Role.Should().Be(KafkaTopicRole.DeadLetter);
            own.ConsumerGroups.Should().Contain(service);
            acme.Topics.Keys.Should().Contain([listen, send, "wolverine-dead-letter-queue"], "a tenant shares its parent's topology");

            IKafkaCatalog catalog = app.Services.GetRequiredService<IKafkaCatalog>();
            KafkaField<KafkaTopicList> listed = await catalog.ListTopicsAsync("wolverine", Token);
            listed.IsAvailable.Should().BeTrue();
            listed.Value.Topics.Select(static x => x.Name).Should().Contain([listen, send, "wolverine-dead-letter-queue"]);

            // A scan of the tenant entry reads the tenant broker's record, and the parent's entry reads the parent's.
            IKafkaScanner scanner = app.Services.GetRequiredService<IKafkaScanner>();
            (await ScanKeysAsync(scanner, "wolverine/acme", send)).Should().Equal("tenant-key");
            (await ScanKeysAsync(scanner, "wolverine", send)).Should().Equal("parent-key");

            // Hard rule 17 / D37, on both brokers: no topic, no group, no committed offset of KStudio's, and Wolverine's own are as they were.
            Topics(parent).Should().Equal(topicsBefore);
            Topics(tenant).Should().Equal(tenantTopicsBefore);
            (await GroupsAsync(parent)).Should().Equal(groupsBefore);
            (await GroupsAsync(tenant)).Should().Equal(tenantGroupsBefore);
            (await GroupsAsync(parent)).Concat(await GroupsAsync(tenant)).Should().NotContain(static x => x.StartsWith("kstudio-", StringComparison.Ordinal));

            HostConfigurations(transport).Should().Equal(hostBefore, "the adapter and the scans wrote nothing into the host's configuration");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    private static async Task<string[]> ScanKeysAsync(IKafkaScanner scanner, string cluster, string topic)
    {
        KafkaScanOwner owner = new("wolverine-live-" + cluster);
        KafkaScanRequest request = new(
            cluster,
            [new KafkaTopicName(topic)],
            new KafkaWindow(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1)),
            PredicateText: null,
            KafkaReadGrant.Envelope);

        KafkaScanPlan plan = await scanner.PlanAsync(request, owner, Token);
        plan.Errors.Should().BeEmpty();

        KafkaScanStart started = scanner.Start(plan, owner, confirmed: true);
        started.Refusal.Should().BeNull();

        await using KafkaScanHandle handle = started.Handle!;
        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromMinutes(3), Token);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);

        return [.. handle.Matches.Select(static x => x.Key.Text ?? string.Empty).Order(StringComparer.Ordinal)];
    }

    /// <summary>Every configuration pair of the host's Kafka transport and its tenants, as text, so two readings compare by value.</summary>
    private static string[] HostConfigurations(KafkaTransport transport)
    {
        static IEnumerable<string> Pairs(string label, ClientConfig config) =>
            config.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(x => label + ":" + x.Key + "=" + x.Value);

        static string Own(ClientConfig? config) => config is null ? "none" : string.Join(',', config.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + x.Value));

        // Each tenant's child transport: Wolverine's internal cache, read here by reflection (a test may; the adapter reads the same three members).
        IEnumerable<string> Tenants()
        {
            object cache = typeof(KafkaTransport).GetProperty("Tenants", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;

            foreach (object tenant in ((IEnumerable) cache).Cast<object>().OrderBy(static x => (string) x.GetType().GetProperty("TenantId")!.GetValue(x)!, StringComparer.Ordinal))
            {
                string id = (string) tenant.GetType().GetProperty("TenantId")!.GetValue(tenant)!;
                KafkaTransport child = (KafkaTransport) tenant.GetType().GetProperty("Transport")!.GetValue(tenant)!;

                foreach (string line in Pairs("tenant " + id + " consumer", child.ConsumerConfig)
                    .Concat(Pairs("tenant " + id + " producer", child.ProducerConfig))
                    .Concat(Pairs("tenant " + id + " admin", child.AdminClientConfig)))
                {
                    yield return line;
                }
            }
        }

        List<string> lines =
        [
            .. Pairs("consumer", transport.ConsumerConfig),
            .. Pairs("producer", transport.ProducerConfig),
            .. Pairs("admin", transport.AdminClientConfig),
            .. Tenants(),
            .. transport.Topics.OrderBy(static x => x.TopicName, StringComparer.Ordinal)
                .SelectMany(static topic => new[]
                {
                    topic.TopicName + "|consumer|" + Own(topic.ConsumerConfig),
                    topic.TopicName + "|producer|" + Own(topic.ProducerConfig),
                }),
        ];

        return [.. lines];
    }

    private static void WaitForTopics(KafkaFixture broker, params string[] topics)
    {
        using IAdminClient admin = new AdminClientBuilder(broker.Client("admin")).Build();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            string[] present = [.. admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic)];

            if (topics.All(present.Contains))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The topics " + string.Join(", ", topics.Except(present)) + " were not created on " + broker.Image + ".");
            }

            Thread.Sleep(0);
        }
    }

    private static async Task WaitForGroupAsync(KafkaFixture broker, string group)
    {
        using IAdminClient admin = new AdminClientBuilder(broker.Client("admin")).Build();
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            ListConsumerGroupsResult groups = await admin.ListConsumerGroupsAsync();

            if (groups.Valid.Any(x => x.GroupId == group))
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Wolverine's group " + group + " did not appear on " + broker.Image + ".");
            }

            await Task.Yield();
        }
    }

    private static string[] Topics(KafkaFixture broker)
    {
        using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig(broker.Client("admin")) { AllowAutoCreateTopics = false }).Build();
        return [.. admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic).Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Every group with its committed offsets, as text.</summary>
    private static async Task<string[]> GroupsAsync(KafkaFixture broker)
    {
        using IAdminClient admin = new AdminClientBuilder(broker.Client("admin")).Build();
        ListConsumerGroupsResult groups = await admin.ListConsumerGroupsAsync();
        List<string> lines = [];

        foreach (ConsumerGroupListing group in groups.Valid.OrderBy(static x => x.GroupId, StringComparer.Ordinal))
        {
            List<ListConsumerGroupOffsetsResult> offsets = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group.GroupId, null)]);
            lines.Add(group.GroupId + ": " + string.Join(
                ", ",
                offsets.SelectMany(static x => x.Partitions).OrderBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition.Value)
                    .Select(static x => $"{x.Topic}[{x.Partition.Value}]={x.Offset}")));
        }

        return [.. lines];
    }
}
