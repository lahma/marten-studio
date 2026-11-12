using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Redaction;
using KStudio.Services;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Clusters;

/// <summary>
/// D43, decisions 5.2 and 6: a source's clusters join the registry when it is built, as entries (<c>{base}/{tenant}</c>) the policies see as a
/// cluster and a tenant; a host's declaration of a topic stays the descriptor and a source only fills what the host left open; and one bad
/// discovery never takes down the host's own clusters.
/// </summary>
public class ClusterSourceTests
{
    private static ConsumerConfig Consumer(string servers = "broker:9092") => new() { BootstrapServers = servers };

    private static AdminClientConfig Admin(string servers = "broker:9092") => new() { BootstrapServers = servers };

    private static KafkaContributedCluster Cluster(
        string key = "wolverine",
        string? tenant = null,
        Action<KafkaClusterBuilder>? configure = null,
        params KafkaDiscoveredTopic[] topics)
    {
        KafkaClusterBuilder builder = new();
        builder.DisplayName("Wolverine");
        configure?.Invoke(builder);
        return new KafkaContributedCluster(key, tenant, Consumer(), Admin(), builder, topics);
    }

    private static KafkaDiscoveredTopic Topic(
        string name,
        bool cloudEvents = false,
        KafkaPartitioner? partitioner = null,
        string? description = "Wolverine listener",
        KafkaTopicRole role = KafkaTopicRole.Listener) =>
        new(name, role, ["Orders.PlaceOrder"], "SystemTextJsonSerializer", "orders-app", cloudEvents, partitioner, "Wolverine", description);

    private static KafkaClusterRegistry Registry(
        KafkaEngineOptions options,
        KafkaClusterContribution contribution,
        IServiceProvider? services = null)
    {
        options.ClusterSources.Add(_ => new FakeSource(contribution));
        return new KafkaClusterRegistry(options, services ?? new ServiceCollection().BuildServiceProvider());
    }

    private static KafkaClusterContribution Contribution(params KafkaContributedCluster[] clusters) => new(clusters, []);

    [Fact]
    public void A_source_alone_is_enough_and_a_tenant_is_an_entry_keyed_with_a_slash()
    {
        KafkaClusterRegistry registry = Registry(
            new KafkaEngineOptions(),
            Contribution(Cluster("wolverine"), Cluster("wolverine", "acme"), Cluster("wolverine", "globex")));

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine/acme", "wolverine/globex");

        KafkaClusterDefinition acme = registry.Get("wolverine/acme");
        (acme.BaseKey, acme.TenantId, acme.Source).Should().Be(("wolverine", "acme", "Fake"));

        KafkaClusterDefinition plain = registry.Get("wolverine");
        (plain.BaseKey, plain.TenantId).Should().Be(("wolverine", null));
    }

    [Fact]
    public void The_host_clusters_come_first_and_a_source_never_replaces_one()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("wolverine", new ClientConfig { BootstrapServers = "host:9092" });
        options.AddCluster("other", new ClientConfig { BootstrapServers = "host:9092" });

        KafkaClusterRegistry registry = Registry(options, Contribution(Cluster("wolverine"), Cluster("wolverine", "acme")));

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "other", "wolverine/acme");
        registry.Get("wolverine").Source.Should().BeNull("the host's cluster of that key stays the host's");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("already registered");
    }

    [Fact]
    public void A_cluster_that_breaks_a_rule_is_skipped_and_noted_and_the_rest_are_listed()
    {
        KafkaClusterRegistry registry = Registry(
            new KafkaEngineOptions(),
            Contribution(
                Cluster("Not A Key"),
                Cluster("good"),
                Cluster("tenanted", new string('x', 129)),
                new KafkaContributedCluster("nobootstrap", null, new ConsumerConfig(), new AdminClientConfig(), new KafkaClusterBuilder(), [])));

        registry.Clusters.Select(static x => x.Key).Should().Equal("good");
        registry.Notes.Should().HaveCount(3).And.OnlyContain(static x => x.Source == "Fake");
    }

    [Fact]
    public void A_source_that_throws_costs_a_note_and_nothing_else()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("host", new ClientConfig { BootstrapServers = "host:9092" });
        options.ClusterSources.Add(static _ => new ThrowingSource());

        KafkaClusterRegistry registry = new(options, new ServiceCollection().BuildServiceProvider());

        registry.Clusters.Select(static x => x.Key).Should().Equal("host");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("InvalidOperationException");
    }

    [Fact]
    public void The_one_argument_constructor_refuses_options_that_name_a_source()
    {
        KafkaEngineOptions options = new();
        options.ClusterSources.Add(static _ => new FakeSource(Contribution()));

        Action build = () => _ = new KafkaClusterRegistry(options);

        build.Should().Throw<InvalidOperationException>().WithMessage("*cluster source*");
    }

    [Fact]
    public void Options_with_neither_a_cluster_nor_a_source_are_still_refused()
    {
        Action build = () => _ = new KafkaClusterRegistry(new KafkaEngineOptions());

        build.Should().Throw<InvalidOperationException>().WithMessage("*At least one Kafka cluster*");
    }

    [Fact]
    public void A_discovered_topic_is_declared_with_a_utf8_key_and_the_facts_the_source_found()
    {
        KafkaClusterRegistry registry = Registry(
            new KafkaEngineOptions(),
            Contribution(Cluster(topics: [Topic("checkout.v1", cloudEvents: true, partitioner: KafkaPartitioner.Murmur2)])));

        KafkaTopicDescriptor descriptor = registry.Get("wolverine").Descriptor("checkout.v1");

        descriptor.Declared.Should().BeTrue();
        descriptor.Discovered.Should().NotBeNull();
        descriptor.Description.Should().Be("Wolverine listener");
        descriptor.KeyFormatDeclared.Should().BeTrue();
        descriptor.KeyFormat.Should().Be(KafkaKeyFormat.Utf8String);
        descriptor.Partitioner.Should().BeSameAs(KafkaPartitioner.Murmur2);
        KafkaDecoders.BuiltInKind(descriptor.Settings.DecoderInstance).Should().Be(KafkaBuiltInDecoder.CloudEvents);
    }

    [Fact]
    public void A_discovered_topic_that_is_not_cloud_events_declares_no_decoder()
    {
        KafkaClusterRegistry registry = Registry(new KafkaEngineOptions(), Contribution(Cluster(topics: [Topic("plain.v1")])));

        KafkaTopicDescriptor descriptor = registry.Get("wolverine").Descriptor("plain.v1");

        descriptor.Settings.DecoderSource.Should().BeNull("detection decides, as for any topic");
        descriptor.Partitioner.Should().BeNull();
    }

    [Fact]
    public void A_host_declaration_stays_the_descriptor_and_the_source_fills_only_what_it_left_open()
    {
        KafkaEngineOptions options = new();
        options.Topic("checkout.v1", t => t
            .Description("The host's words")
            .Key(KafkaKeyFormat.Utf8String)
            .Value(KafkaDecoders.Json)
            .Partitioner(KafkaPartitioner.Fnv1a));

        KafkaClusterRegistry registry = Registry(
            options,
            Contribution(Cluster(topics: [Topic("checkout.v1", cloudEvents: true, partitioner: KafkaPartitioner.Murmur2)])));

        KafkaTopicDescriptor descriptor = registry.Get("wolverine").Descriptor("checkout.v1");

        descriptor.Description.Should().Be("The host's words");
        descriptor.Partitioner.Should().BeSameAs(KafkaPartitioner.Fnv1a);
        KafkaDecoders.BuiltInKind(descriptor.Settings.DecoderInstance).Should().Be(KafkaBuiltInDecoder.Json, "a host decoder wins over CloudEvents");
        descriptor.Discovered.Should().NotBeNull("the facts are attached beside the host's declaration");
    }

    [Fact]
    public void A_cluster_level_declaration_wins_over_a_global_one_and_keeps_its_redaction()
    {
        KafkaEngineOptions options = new();
        options.Topic("checkout.v1", t => t.Description("global"));

        KafkaClusterRegistry registry = Registry(
            options,
            Contribution(Cluster(
                configure: b => b.Topic("checkout.v1", t => t.Description("cluster").Redact(r => r.Header("customer-email"))),
                topics: Topic("checkout.v1"))));

        KafkaTopicDescriptor descriptor = registry.Get("wolverine").Descriptor("checkout.v1");

        descriptor.Description.Should().Be("cluster");
        descriptor.Settings.Rules.Should().NotBeNull("discovery adds no rule and removes none");
        descriptor.Settings.Rules!.Declared.Should().ContainSingle(static x => x.Target == RedactionTarget.Header && x.Field == "customer-email");
    }

    [Fact]
    public void Discovery_adds_no_redaction_rule()
    {
        KafkaClusterRegistry registry = Registry(new KafkaEngineOptions(), Contribution(Cluster(topics: [Topic("checkout.v1")])));

        registry.Get("wolverine").Descriptor("checkout.v1").Settings.Rules.Should().BeNull();
    }

    [Fact]
    public void A_topic_with_an_invalid_name_or_a_repeated_one_is_dropped_and_a_long_description_is_cut()
    {
        KafkaClusterRegistry registry = Registry(
            new KafkaEngineOptions(),
            Contribution(Cluster(topics:
            [
                Topic("good.v1", description: new string('d', 600)),
                Topic("bad name"),
                Topic("good.v1"),
            ])));

        KafkaClusterDefinition definition = registry.Get("wolverine");

        definition.Topics.Keys.Should().Equal("good.v1");
        definition.Descriptor("good.v1").Description!.Length.Should().Be(500);
    }

    [Fact]
    public void The_source_receives_the_container_and_runs_once_when_the_registry_is_built()
    {
        int runs = 0;
        IServiceProvider? received = null;
        KafkaEngineOptions options = new();
        options.ClusterSources.Add(provider =>
        {
            received = provider;
            return new CountingSource(() => runs++);
        });
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        KafkaClusterRegistry registry = new(options, services);
        _ = registry.Clusters;
        _ = registry.Clusters;

        runs.Should().Be(1);
        received.Should().BeSameAs(services);
    }

    [Fact]
    public void The_registration_builds_the_registry_with_the_container_and_runs_the_source_after_the_options_delegate()
    {
        KafkaEngineOptions options = new();
        int runs = 0;
        options.ClusterSources.Add(_ => new CountingSource(() => runs++));
        ServiceCollection services = new();
        services.AddLogging();
        KStudioEngineRegistration.Add(services, _ => options, _ => new StubSettings());
        using ServiceProvider provider = services.BuildServiceProvider();

        runs.Should().Be(0, "nothing is read until the registry is first needed");

        KafkaClusterRegistry registry = provider.GetRequiredService<KafkaClusterRegistry>();

        runs.Should().Be(1);
        registry.Clusters.Should().ContainSingle().Which.Key.Should().Be("counted");
        provider.GetRequiredService<KafkaClusterRegistry>().Should().BeSameAs(registry);
        runs.Should().Be(1);
    }

    [Fact]
    public void A_note_is_logged_as_event_9677_with_no_name_in_it_and_at_Debug_the_second_time()
    {
        CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddSingleton<ILoggerFactory>(logs.CreateFactory());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<KStudioLoggers>();
        services.AddSingleton(static provider => new KafkaLogOnce(provider.GetRequiredService<TimeProvider>()));
        using ServiceProvider provider = services.BuildServiceProvider();

        KafkaSourceNote note = new("a tenant cluster", "Wolverine had not connected it yet", "Tenant SECRET-TENANT's cluster is not listed yet.");
        _ = Registry(new KafkaEngineOptions(), new KafkaClusterContribution([Cluster()], [note, note]), provider);

        CapturedLogEntry[] entries = [.. logs.Entries.Where(static x => x.EventId.Id == 9677)];

        entries.Select(static x => x.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
        entries.Should().OnlyContain(static x => x.Message == "KStudio did not list a tenant cluster from Fake: Wolverine had not connected it yet");
        logs.Entries.Should().NotContain(static x => x.Message.Contains("SECRET-TENANT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("wolverine", "wolverine")]
    [InlineData("wolverine/acme", "wolverine")]
    [InlineData("wolverine/a/b", "wolverine")]
    [InlineData("a", "a")]
    public void The_base_of_an_entry_key_is_the_part_before_the_first_slash(string entryKey, string expected)
    {
        KafkaClusterKey.BaseOf(entryKey).Should().Be(expected);
        KafkaClusterKey.Compose("wolverine", null).Should().Be("wolverine");
        KafkaClusterKey.Compose("wolverine", "acme").Should().Be("wolverine/acme");
    }

    [Theory]
    [InlineData("acme", true)]
    [InlineData("a/b", true)]
    [InlineData("Acme Corp", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a\nb", false)]
    [InlineData("a\0b", false)]
    public void A_tenant_id_is_one_to_128_characters_with_no_control_character(string tenant, bool valid)
    {
        KafkaClusterKey.IsValidTenantId(tenant).Should().Be(valid);
        KafkaClusterKey.IsValidTenantId(new string('x', 128)).Should().BeTrue();
        KafkaClusterKey.IsValidTenantId(new string('x', 129)).Should().BeFalse();
        KafkaClusterKey.IsValidTenantId(null).Should().BeFalse();
    }

    [Fact]
    public void A_host_cluster_key_still_cannot_hold_a_slash()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("a/b", new ClientConfig { BootstrapServers = "host:9092" });

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("the key must be");
    }

    [Fact]
    public void The_entry_source_gives_every_entry_its_tenant_and_the_clusters_own_key()
    {
        KafkaClusterRegistry registry = Registry(
            new KafkaEngineOptions(),
            Contribution(Cluster("wolverine"), Cluster("wolverine", "acme")));

        KafkaClusterEntry[] entries = [.. registry.Clusters.Select(static x => new KafkaClusterEntry(x.Key, x.TenantId, x.DisplayName, x.Environment) { Cluster = x.BaseKey })];

        entries.Select(static x => (x.Key, x.Cluster, x.TenantId, x.Label)).Should().Equal(
            ("wolverine", "wolverine", null, "Wolverine"),
            ("wolverine/acme", "wolverine", "acme", "Wolverine · acme"));
    }

    private sealed class FakeSource(KafkaClusterContribution contribution) : IKafkaClusterSource
    {
        public string Name => "Fake";

        public KafkaClusterContribution Contribute() => contribution;
    }

    private sealed class ThrowingSource : IKafkaClusterSource
    {
        public string Name => "Throwing";

        public KafkaClusterContribution Contribute() => throw new InvalidOperationException("boom");
    }

    private sealed class CountingSource(Action count) : IKafkaClusterSource
    {
        public string Name => "Counting";

        public KafkaClusterContribution Contribute()
        {
            count();
            return new KafkaClusterContribution([new KafkaContributedCluster("counted", null, new ConsumerConfig { BootstrapServers = "b:9092" }, new AdminClientConfig { BootstrapServers = "b:9092" }, new KafkaClusterBuilder(), [])], []);
        }
    }

    private sealed class StubSettings : IKafkaEngineSettings
    {
        public TimeSpan CatalogTimeToLive => TimeSpan.FromSeconds(5);
    }
}
