using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Tests.Contract;
using KStudio.Tests.Support;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Clusters;

/// <summary>Decisions 2.1, 2.3 and 2.5: keys, duplicates, the clone, topic precedence, the option bounds and the container's registration.</summary>
public class ClusterRegistrationTests
{
    private static ClientConfig Config(string servers = "broker:9092") => new() { BootstrapServers = servers };

    [Theory]
    [InlineData("prod")]
    [InlineData("eu-west-1")]
    [InlineData("0")]
    public void Valid_keys_are_accepted(string key)
    {
        KafkaEngineOptions options = new();
        options.AddCluster(key, Config());

        KafkaEngineOptionsValidation.Validate(options).Should().BeEmpty();
        new KafkaClusterRegistry(options).Get(key).Key.Should().Be(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Prod")]
    [InlineData("-prod")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz-abcdefghijklmnopqrstuvwxyz-abcdefghijklmnopqrstuvwxyz")]
    public void An_invalid_key_is_refused_with_a_message_naming_the_cluster(string key)
    {
        KafkaEngineOptions options = new();
        options.AddCluster(key, Config());

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("the key must be");
    }

    [Fact]
    public void No_cluster_is_refused()
    {
        KafkaEngineOptionsValidation.Validate(new KafkaEngineOptions()).Should().ContainSingle().Which.Should().Contain("At least one Kafka cluster");
    }

    [Fact]
    public void A_duplicate_key_is_refused()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config()).AddCluster("prod", Config());

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("used by more than one cluster");
    }

    [Fact]
    public void A_cluster_with_no_bootstrap_servers_is_refused()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new ClientConfig());

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("bootstrap.servers");
    }

    [Fact]
    public void Registry_construction_refuses_options_the_validation_refuses_and_says_why()
    {
        KafkaEngineOptions options = new();

        Action build = () => _ = new KafkaClusterRegistry(options);

        build.Should().Throw<InvalidOperationException>().WithMessage("*At least one Kafka cluster*");
    }

    [Fact]
    public void Each_option_bound_is_checked_and_the_message_names_the_property()
    {
        KafkaEngineOptions options = new()
        {
            GroupIdPrefix = "no spaces allowed",
            RequestTimeout = TimeSpan.FromMilliseconds(10),
            AdminIdleTimeout = TimeSpan.FromDays(1),
        };
        options.Limits.MaxMatches = 0;
        options.Limits.BytesPerSecond = 1;
        options.AddCluster("prod", Config());

        IReadOnlyList<string> problems = KafkaEngineOptionsValidation.Validate(options);

        problems.Should().Contain(static x => x.StartsWith("GroupIdPrefix", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.StartsWith("RequestTimeout", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.StartsWith("AdminIdleTimeout", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.StartsWith("Limits.MaxMatches", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.StartsWith("Limits.BytesPerSecond", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_limit_default_is_inside_its_bounds_and_each_bound_is_refused_just_outside()
    {
        new KafkaScanLimits().Validate().Should().BeEmpty();

        Action<KafkaScanLimits>[] breaks =
        [
            static x => x.MaxConcurrentScans = 9,
            static x => x.MaxConcurrentScansPerOwner = 3,
            static x => x.BytesPerSecond = 1024,
            static x => x.OwnerBytesPerHour = 1,
            static x => x.MaxBytesPerScan = 128L * 1024 * 1024 * 1024,
            static x => x.MaxRecordsPerScan = 2_000_000_000,
            static x => x.MaxScanDuration = TimeSpan.FromSeconds(1),
            static x => x.MaxMatches = 10_001,
            static x => x.MaxMatchBytes = 10,
            static x => x.MaxDecodeBytes = 10,
            static x => x.ConfirmAboveBytes = long.MaxValue,
            static x => x.ConfirmAboveRecords = long.MaxValue,
            static x => x.QueueKilobytes = 10,
            static x => x.FetchBytes = 10,
            static x => x.CreateTimeMargin = TimeSpan.FromHours(2),
            static x => x.PartitionStallTimeout = TimeSpan.Zero,
        ];

        foreach (Action<KafkaScanLimits> broken in breaks)
        {
            KafkaScanLimits limits = new();
            broken(limits);
            limits.Validate().Should().ContainSingle();
        }
    }

    [Fact]
    public void A_topic_name_description_and_group_are_validated()
    {
        KafkaEngineOptions options = new();
        options.Topic("..", static t => t.Description("x"));
        options.AddCluster(
            "prod",
            Config(),
            static c => c
                .DisplayName(new string('n', 101))
                .Environment(new string('e', 41))
                .Topic("bad name", static t => t.Description(string.Empty))
                .ConsumerGroup(string.Empty));

        IReadOnlyList<string> problems = KafkaEngineOptionsValidation.Validate(options);

        problems.Should().Contain(static x => x.Contains("Topic '..'", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.Contains("DisplayName must be 1 to 100", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.Contains("Environment must be 1 to 40", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.Contains("topic 'bad name'", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.Contains("Description must be 1 to 500", StringComparison.Ordinal));
        problems.Should().Contain(static x => x.Contains("consumer group id", StringComparison.Ordinal));
    }

    [Fact]
    public void More_than_fifty_groups_are_refused()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config(), static c =>
        {
            for (int i = 0; i < 51; i++)
            {
                c.ConsumerGroup("g" + i);
            }
        });

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("at most 50 consumer groups");
    }

    [Fact]
    public void A_cluster_topic_replaces_never_merges_with_a_global_one_of_the_same_name()
    {
        KafkaEngineOptions options = new();
        options.Topic("orders", static t => t.Description("global").Key(KafkaKeyFormat.Int64BigEndian));
        options.Topic("audit", static t => t.Description("global audit"));
        options.AddCluster("prod", Config(), static c => c.Topic("orders", static t => t.Description("own")));
        options.AddCluster("dev", Config());

        KafkaClusterRegistry registry = new(options);
        KafkaTopicDescriptor own = registry.Get("prod").Descriptor("orders");

        own.Description.Should().Be("own");
        own.KeyFormatDeclared.Should().BeFalse("the cluster's descriptor replaced the global one, key format included");
        own.KeyFormat.Should().BeSameAs(KafkaKeyFormat.Utf8String);
        registry.Get("prod").Descriptor("audit").Description.Should().Be("global audit");
        registry.Get("dev").Descriptor("orders").KeyFormat.Should().BeSameAs(KafkaKeyFormat.Int64BigEndian);
    }

    [Fact]
    public void An_undeclared_topic_has_a_default_descriptor_that_says_nothing_was_declared()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config());

        KafkaTopicDescriptor descriptor = new KafkaClusterRegistry(options).Get("prod").Descriptor("anything");

        descriptor.Declared.Should().BeFalse();
        descriptor.KeyFormatDeclared.Should().BeFalse("every verdict about its keys says 'assumed'");
        descriptor.KeyFormat.Name.Should().Be("Utf8String");
    }

    [Fact]
    public void A_cluster_added_with_a_consumer_and_an_admin_config_keeps_each_for_its_role()
    {
        ConsumerConfig consumer = new() { BootstrapServers = "c:9092", GroupId = "host" };
        AdminClientConfig admin = new() { BootstrapServers = "a:9092" };
        KafkaEngineOptions options = new();
        options.AddCluster("prod", consumer, admin);

        KafkaClusterDefinition definition = new KafkaClusterRegistry(options).Get("prod");

        definition.ConsumerSettings["bootstrap.servers"].Should().Be("c:9092");
        definition.AdminSettings["bootstrap.servers"].Should().Be("a:9092");
        definition.AdminSettings.Should().NotContainKey("group.id");
    }

    [Fact]
    public void The_display_name_defaults_to_the_key_and_the_environment_and_groups_are_kept()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config(), static c => c.Environment("production").ConsumerGroup("billing"));
        options.AddCluster("dev", Config(), static c => c.DisplayName("Development"));

        KafkaClusterRegistry registry = new(options);

        registry.Get("prod").DisplayName.Should().Be("prod");
        registry.Get("prod").Environment.Should().Be("production");
        registry.Get("prod").ConsumerGroups.Should().Equal("billing");
        registry.Get("dev").DisplayName.Should().Be("Development");
        registry.Clusters.Select(static x => x.Key).Should().Equal("prod", "dev");
        registry.TryGet("nope", out _).Should().BeFalse();
        Action missing = () => registry.Get("nope");
        missing.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void A_derived_options_class_is_an_options_class_for_the_engine()
    {
        DerivedOptions options = new();
        options.AddCluster("prod", Config());

        new KafkaClusterRegistry(options).Clusters.Should().ContainSingle();
    }

    [Fact]
    public async Task Registration_resolves_every_service_connects_to_nothing_and_adds_no_hosted_service()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        CapturingLoggerProvider logs = new();

        ServiceCollection services = new();
        services.AddSingleton(logs.CreateFactory());
        services.AddSingleton<IKafkaWireFactory>(new FakeKafkaWireFactory(cluster));
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config("unreachable.invalid:9092"));
        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(TimeSpan.FromSeconds(5)));

        services.Should().NotContain(static x => x.ServiceType == typeof(IHostedService), "no hosted service (decision 4)");
        services.Should().OnlyContain(static x => x.Lifetime == ServiceLifetime.Singleton, "singletons only");

        await using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        provider.GetRequiredService<KafkaClusterRegistry>();
        provider.GetRequiredService<Core.Catalog.IKafkaCatalog>();
        provider.GetRequiredService<Core.Windows.WindowResolver>();
        provider.GetRequiredService<KafkaAdminPool<IAdminClient>>().ClientCount.Should().Be(0, "no admin client exists until a read needs one");

        cluster.Calls.Should().BeEmpty("nothing was asked of a cluster by registering and resolving");
        logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_registered_engine_puts_a_test_factory_first()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        ServiceCollection services = new();
        services.AddLogging();
        FakeKafkaWireFactory factory = new(cluster);
        services.AddSingleton<IKafkaWireFactory>(factory);
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Config());
        KStudioEngineRegistration.Add(services, _ => options, _ => new TestEngineSettings(TimeSpan.FromSeconds(5)));

        await using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IKafkaWireFactory>().Should().BeSameAs(factory, "TryAdd leaves the factory a test registered first");
        provider.GetRequiredService<KafkaWires>().For("prod").Should().BeOfType<FakeKafkaWire>();
        provider.GetRequiredService<KafkaWires>().For("prod").Should().BeSameAs(provider.GetRequiredService<KafkaWires>().For("prod"), "one wire per cluster");
    }

    [Fact]
    public async Task The_hosts_token_hook_is_run_by_the_engines_admin_client_and_one_that_throws_is_a_failed_refresh_not_a_crash()
    {
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
        KafkaClusterDefinition definition = new KafkaClusterRegistry(options).Get("prod");

        CapturingLoggerProvider logs = new();
        ConfluentClientEvents events = new(new KStudioLoggers(logs.CreateFactory()), new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

        using IAdminClient admin = ConfluentWireFactory.BuildAdminClient(definition, events);

        await called.Task.Bounded();

        // The handler's exception was caught and reported as a failed refresh; only its type name reaches the log.
        SpinWait.SpinUntil(() => logs.Entries.Any(static x => x.EventId.Id == 9657 && x.Message.Contains("InvalidOperationException", StringComparison.Ordinal)), TimeSpan.FromSeconds(10))
            .Should().BeTrue();
        logs.Entries.Should().NotContain(static x => x.Message.Contains("token service", StringComparison.Ordinal));
        logs.Entries.Where(static x => x.Level == LogLevel.Warning && x.EventId.Id == 9657).Should().BeEmpty();
    }

    private sealed class DerivedOptions : KafkaEngineOptions;
}
