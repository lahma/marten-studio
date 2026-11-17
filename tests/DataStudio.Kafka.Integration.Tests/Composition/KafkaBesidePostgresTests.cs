using DataStudio.Kafka.Internal;
using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DataStudio.Kafka.Integration.Tests.Composition;

/// <summary>
/// D34 with a database module in the host: <c>AddKafka()</c> beside <c>AddPostgres()</c> or <c>AddPostgresDiagnostics()</c>, in either order, composes the
/// shell, the rail, the banner and the chip in the order the host wrote them, and a host without Kafka is the host it was. These cases live here, in a
/// project that references both modules, because the fast Kafka suite is a Kafka-only host with no database module in its output (v2 step 7); they are
/// registration checks over the dependency-injection container and need no broker, no Docker and no browser.
/// </summary>
public class KafkaBesidePostgresTests
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
        provider.GetServices<IHostedService>().OfType<KafkaStartupGuard>().Single().StartAsync(Ct);

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
            [.. provider.GetRequiredService<StudioShellDescriptor>().Modules.SelectMany(static x => x.CapabilityGroups.Select(group => x.ProductName + ": " + group.Name))];

        Headings(kafkaFirst).First().Should().Be("Kafka: reads");
        Headings(postgresFirst).First().Should().StartWith("Postgres: ");
        Headings(postgresFirst).Last().Should().Be("Kafka: reads");
        kafkaFirst.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.ProductName).Should().Equal("Kafka", "Postgres");
        postgresFirst.GetRequiredService<StudioShellDescriptor>().Modules.Select(static x => x.ProductName).Should().Equal("Postgres", "Kafka");
    }

    [Fact]
    public void A_host_without_kafka_resolves_the_very_descriptor_it_did_before()
    {
        using ServiceProvider provider = Build(static services => services.AddDataStudio().AddPostgres());

        provider.GetRequiredService<StudioDescriptor>().Should().BeSameAs(DataStudio.Postgres.Services.Postgres.PostgresDescriptor.Instance);
        provider.GetRequiredService<StudioShellDescriptor>().Modules.Should().ContainSingle().Which.HomeRoute.Should().Be("postgres", "the database is the one module");
    }

    [Fact]
    public void A_host_without_kafka_has_no_find_in_kafka_seam()
    {
        using ServiceProvider provider = Build(static services => services.AddDataStudio().AddPostgres());

        provider.GetService<IStudioRecordSearchLinks>().Should().BeNull();
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

        provider.GetServices<IStudioRecordSearchLinks>().Should().ContainSingle().Which.Label.Should().Be("Find in Kafka");
    }

    [Fact]
    public async Task A_valid_external_link_starts_a_host_with_a_database_module_too()
    {
        using ServiceProvider provider = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka(static o => o.ExternalLink("Traces", "https://tempo.example.com/trace/{traceId}"));
        });

        await Start(provider);
    }

    [Fact]
    public void Every_rail_route_of_a_postgres_and_kafka_host_matches_a_page()
    {
        using ServiceProvider host = Build(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio().AddKafka();
        });

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Modules.Select(static x => x.Key).Should().Equal("postgres", "kafka");
        ComposedRailAssertions.NoOneDotXAddressIsAPage(shell);
    }
}
