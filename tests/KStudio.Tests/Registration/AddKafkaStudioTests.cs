using KStudio.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using PostgresStudio;
using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Registration;

/// <summary>
/// D34: <c>AddKafkaStudio()</c> adds a module to the registered studio, in either order and twice, and refuses to start without a studio and with a
/// capability no policy answers - word for word.
/// </summary>
public class AddKafkaStudioTests
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
            services.AddPostgresStudio();
            services.AddKafkaStudio(static options => options.AddCluster("primary", new Confluent.Kafka.ClientConfig { BootstrapServers = "127.0.0.1:1" }));
        });
        using IServiceScope scope = provider.CreateScope();

        foreach (Type service in new[]
        {
            typeof(KStudio.Services.KafkaState),
            typeof(KStudio.Services.KafkaAuthorization),
            typeof(KStudio.Services.KafkaActionLog),
            typeof(KStudio.Services.KafkaCatalogService),
            typeof(KStudio.Services.KafkaOwnerKey),
            typeof(KStudio.Services.KafkaSearchService),
            typeof(KStudio.Services.KafkaRecordService),
        })
        {
            scope.ServiceProvider.GetRequiredService(service).Should().NotBeNull(service.Name);
        }
    }

    [Fact]
    public async Task Kafka_without_a_studio_refuses_to_start()
    {
        using ServiceProvider provider = Build(static services => services.AddKafkaStudio());

        Func<Task> start = () => Start(provider);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(
            "AddKafkaStudio() was called in an application that registered no studio. KStudio is a module of Marten Studio or Postgres Studio: "
            + "call services.AddMartenStudio() or services.AddPostgresStudio() as well, and map that studio with app.MapMartenStudio() or "
            + "app.MapPostgresStudio(); the Kafka pages are served under its path, at /kafka.");
    }

    [Fact]
    public async Task Kafka_beside_diagnostics_alone_refuses_to_start()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddPostgresDiagnostics();
            services.AddKafkaStudio();
        });

        Func<Task> start = () => Start(provider);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().StartWith("AddKafkaStudio() was called in an application that registered no studio.");
    }

    [Fact]
    public async Task Kafka_beside_a_studio_starts()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddPostgresStudio();
            services.AddKafkaStudio();
        });

        await Start(provider);
    }

    [Fact]
    public void Kafka_before_and_after_AddPostgresStudio_compose_the_same_rail()
    {
        using ServiceProvider before = Build(static services =>
        {
            services.AddKafkaStudio();
            services.AddPostgresStudio();
        });
        using ServiceProvider after = Build(static services =>
        {
            services.AddPostgresStudio();
            services.AddKafkaStudio();
        });

        StudioDescriptor first = before.GetRequiredService<StudioDescriptor>();
        StudioDescriptor second = after.GetRequiredService<StudioDescriptor>();

        first.Navigation.Select(static x => x.Title).Should().Equal(second.Navigation.Select(static x => x.Title));
        first.Navigation.Select(static x => x.Title).Should().Equal("Data", "Database", "Operations", "Kafka", "System");
        first.PageAssemblies.Should().HaveCount(2);
        first.Modules.Should().ContainSingle().Which.Key.Should().Be("kafka");
        first.Navigation.Single(static x => x.Title == "Kafka").ModuleKey.Should().Be("kafka");
    }

    [Fact]
    public void AddKafkaStudio_twice_registers_one_module()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();
        services.AddKafkaStudio();
        services.AddKafkaStudio();

        services.Count(static x => x.ServiceType == typeof(IStudioModule)).Should().Be(1);
        services.Count(static x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(KafkaStudioStartupGuard)).Should().Be(1);
    }

    [Fact]
    public void A_host_without_kafka_resolves_the_very_descriptor_it_did_before()
    {
        using ServiceProvider provider = Build(static services => services.AddPostgresStudio());

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(PostgresStudio.Services.Postgres.PostgresStudioDescriptor.Instance);
    }

    [Fact]
    public void The_module_is_pure_data_over_the_options()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddPostgresStudio();
            services.AddKafkaStudio(static o => o.Capabilities.SearchRecords = true);
        });

        IStudioModule module = provider.GetRequiredService<StudioDescriptor>().Modules.Single();

        module.Pages.KeepsRoot.Should().BeTrue();
        module.Pages.CompileTimeRoot.Should().Be("/kafka");
        module.ProductName.Should().Be("KStudio for Apache Kafka®");
        module.Capabilities.Should().Equal(new StudioModuleCapability("SearchRecords", "KafkaStudioOptions.Capabilities.SearchRecords", Configured: true));
        module.Navigation.Should().ContainSingle().Which.Items.Select(static x => x.Label).Should().Equal("Topics", "Search", "Cluster settings");
    }
}
