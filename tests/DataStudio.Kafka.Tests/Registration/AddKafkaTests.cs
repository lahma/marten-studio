using DataStudio.Services.Settings;
using DataStudio.Kafka.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using DataStudio.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Kafka.Tests.Registration;

/// <summary>
/// D34: <c>AddKafka()</c> adds a module to the shell the host registered with <c>AddDataStudio()</c>, in either order beside a database module and
/// twice, and the host is refused at start with a capability no policy answers - word for word.
/// </summary>
public class AddKafkaTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(Action<IServiceCollection> register)
    {
        ServiceCollection services = new();
        services.AddLogging();
        register(services);
        return services.BuildServiceProvider();
    }

    private static Task Start(IServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<KafkaStudioStartupGuard>().Single().StartAsync(Ct);

    [Fact]
    public void The_real_registration_resolves_every_scoped_service_a_page_injects()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none"));
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka(static options => options.AddCluster("primary", new Confluent.Kafka.ClientConfig { BootstrapServers = "127.0.0.1:1" }));
        });
        using IServiceScope scope = provider.CreateScope();

        foreach (Type service in new[]
        {
            typeof(DataStudio.Kafka.Services.KafkaState),
            typeof(DataStudio.Kafka.Services.KafkaAuthorization),
            typeof(DataStudio.Kafka.Services.KafkaActionLog),
            typeof(DataStudio.Kafka.Services.KafkaCatalogService),
            typeof(DataStudio.Kafka.Services.KafkaOwnerKey),
            typeof(DataStudio.Kafka.Services.KafkaSearchService),
            typeof(DataStudio.Kafka.Services.KafkaRecordService),
        })
        {
            scope.ServiceProvider.GetRequiredService(service).Should().NotBeNull(service.Name);
        }
    }

    [Fact]
    public async Task Kafka_alone_in_the_shell_starts_because_the_builder_makes_a_studio_without_one_impossible()
    {
        using ServiceProvider provider = Build(static services => services.AddDataStudio().AddKafka());

        await Start(provider);
    }

    [Fact]
    public async Task Kafka_beside_diagnostics_alone_starts()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddPostgresDiagnostics();
            services.AddDataStudio().AddKafka();
        });

        await Start(provider);
    }

    [Fact]
    public async Task Kafka_beside_a_studio_starts()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka();
        });

        await Start(provider);
    }

    [Fact]
    public void Kafka_before_and_after_AddPostgres_compose_in_the_order_the_host_wrote_them()
    {
        using ServiceProvider before = Build(static services =>
        {
            services.AddDataStudio().AddKafka();
            services.AddDataStudio().AddPostgres();
        });
        using ServiceProvider after = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka();
        });
        using ServiceProvider chained = Build(static services => services.AddDataStudio().AddPostgres().AddKafka());
        using ServiceProvider chainedReversed = Build(static services => services.AddDataStudio().AddKafka().AddPostgres());

        StudioShellDescriptor first = before.GetRequiredService<StudioShellDescriptor>();
        StudioShellDescriptor second = after.GetRequiredService<StudioShellDescriptor>();

        first.Modules.Select(static x => x.Key).Should().Equal("kafka", "postgres");
        first.Navigation.Select(static x => x.Title).Should().Equal("Kafka", "Data", "Database", "Operations", "System", "Studio");
        first.PageAssemblies.Should().HaveCount(3, "the database's, the shell's Activity page and the module's");
        first.Navigation.Single(static x => x.Title == "Kafka").ModuleKey.Should().Be("kafka");

        second.Modules.Select(static x => x.Key).Should().Equal("postgres", "kafka");
        second.Navigation.Select(static x => x.Title).Should().Equal("Data", "Database", "Operations", "System", "Kafka", "Studio");
        second.PageAssemblies.Should().HaveCount(3);

        chained.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.Key).Should().Equal("postgres", "kafka");
        chainedReversed.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.Key).Should().Equal("kafka", "postgres");

        // What is refused does not depend on it: both orders compose, and the same page assemblies are served.
        second.PageAssemblies.Select(static x => x.CompileTimeRoot).Order().Should().Equal(first.PageAssemblies.Select(static x => x.CompileTimeRoot).Order());
    }

    [Fact]
    public void The_order_is_the_banner_the_chip_and_the_front_tiles_order_too()
    {
        using ServiceProvider kafkaFirst = Build(static services => services.AddDataStudio().AddKafka().AddPostgres());
        using ServiceProvider postgresFirst = Build(static services => services.AddDataStudio().AddPostgres().AddKafka());

        string[] Headings(ServiceProvider provider) =>
            [.. provider.GetRequiredService<StudioShellDescriptor>().Modules.SelectMany(static x => x.CapabilityGroups).Select(static x => x.Heading)];

        Headings(kafkaFirst).First().Should().Be("KStudio for Apache Kafka® reads");
        Headings(postgresFirst).First().Should().StartWith("Postgres Studio: ");
        Headings(postgresFirst).Last().Should().Be("KStudio for Apache Kafka® reads");
        kafkaFirst.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.ProductName).Should().Equal("KStudio for Apache Kafka®", "Postgres Studio");
        postgresFirst.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.ProductName).Should().Equal("Postgres Studio", "KStudio for Apache Kafka®");
    }

    [Fact]
    public void AddKafka_twice_registers_one_module()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();
        services.AddDataStudio().AddKafka();
        services.AddDataStudio().AddKafka();

        services.Count(static x => x.ServiceType == typeof(IStudioModule) && x.ImplementationType == typeof(DataStudio.Kafka.Services.KafkaStudioModule)).Should().Be(1);
        services.Count(static x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(KafkaStudioStartupGuard)).Should().Be(1);
    }

    [Fact]
    public void A_host_without_kafka_resolves_the_very_descriptor_it_did_before()
    {
        using ServiceProvider provider = Build(static services => services.AddDataStudio().AddPostgres());

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(DataStudio.Postgres.Services.Postgres.PostgresStudioDescriptor.Instance);
        provider.GetRequiredService<StudioShellDescriptor>().Modules.Should().ContainSingle().Which.HomeRoute.Should().Be("postgres", "the database is the one module");
    }

    [Fact]
    public void The_module_is_pure_data_over_the_options()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka(static o => o.Capabilities.SearchRecords = true);
        });

        IStudioModule module = provider.GetRequiredService<StudioShellDescriptor>().Modules.Single(static x => x.Key == "kafka");

        module.PageAssemblies.Should().ContainSingle().Which.CompileTimeRoot.Should().Be("/kafka");
        module.HomeRoute.Should().Be("kafka");
        module.Summary.Should().Be("Topics, record search and live tail of the application's clusters.");
        module.ProductName.Should().Be("KStudio for Apache Kafka®");
        module.CapabilityGroups.Should().ContainSingle().Which.Heading.Should().Be("KStudio for Apache Kafka® reads");
        module.CapabilityGroups.Single().Capabilities.Should().Equal(
            new StudioModuleCapability("SearchRecords", "KafkaOptions.Capabilities.SearchRecords", Configured: true),
            new StudioModuleCapability("ReadPayloads", "KafkaOptions.Capabilities.ReadPayloads", Configured: false),
            new StudioModuleCapability("ExportRecords", "KafkaOptions.Capabilities.ExportRecords", Configured: false),
            new StudioModuleCapability("LiveTail", "KafkaOptions.Capabilities.LiveTail", Configured: false));
        module.Navigation.Should().ContainSingle().Which.Items.Select(static x => x.Label).Should().Equal("Topics", "Search", "Live tail", "Cluster settings");
        module.Navigation.Single().Items.Single(static x => x.Label == "Live tail").IsShown.Should().NotBeNull("the entry is drawn only while the capability is on (#74), like Search");
        module.AuditEventIds.Should().Be("9600-9609");
    }

    [Fact]
    public void The_module_offers_find_in_kafka_to_the_studios_record_screens_once()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka();
            services.AddDataStudio().AddKafka();
        });

        IStudioRecordSearchLinks links = provider.GetRequiredService<IStudioRecordSearchLinks>();

        links.Label.Should().Be("Find in Kafka");
        provider.GetServices<IStudioRecordSearchLinks>().Should().ContainSingle();
    }

    [Fact]
    public void A_host_without_kafka_has_no_find_in_kafka_seam()
    {
        using ServiceProvider provider = Build(static services => services.AddDataStudio().AddPostgres());

        provider.GetService<IStudioRecordSearchLinks>().Should().BeNull();
    }

    [Fact]
    public async Task A_valid_external_link_starts_the_host()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka(static o => o.ExternalLink("Traces", "https://tempo.example.com/trace/{traceId}").ExternalLink("Logs", "https://logs.example.com/?c={correlationId}"));
        });

        await Start(provider);
    }

    [Theory]
    [InlineData("https://user:S3NT1NEL@tempo.example.com/{traceId}", "user info")]
    [InlineData("https://tempo.example.com/{traceId}?api_key=S3NT1NEL", "names a credential")]
    [InlineData("https://tempo.example.com/{S3NT1NEL}", "does not know")]
    public async Task An_external_link_that_breaks_a_rule_stops_the_host_at_start_and_the_error_never_holds_the_template(string template, string rule)
    {
        using ServiceProvider provider = Build(services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka(o => o.ExternalLink("Traces", template));
        });

        Func<Task> start = () => Start(provider);

        string message = (await start.Should().ThrowAsync<OptionsValidationException>()).Which.Message;

        message.Should().Contain("External link 'Traces'").And.Contain(rule).And.NotContain("S3NT1NEL").And.NotContain("tempo.example.com");
    }
}
