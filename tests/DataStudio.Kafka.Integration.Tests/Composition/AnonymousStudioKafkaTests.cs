using DataStudio.Postgres.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Integration.Tests.Composition;

/// <summary>
/// K07 counts every composed module's capabilities (REVIEW-V2-B finding 1), read through the shell's descriptor off a real container: another module's
/// capabilities that are on, unless the shell's own <c>ReadOnly</c> is, and never the database module's own (its facts carry those).
/// </summary>
public class AnonymousStudioKafkaTests
{
    private static ServiceProvider Host(Action<DataStudioOptions>? shell = null, Action<KafkaOptions>? kafka = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataStudio(shell).AddPostgres();
        services.AddDataStudio().AddKafka(kafka);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void A_kafka_capability_that_is_on_is_found_by_module_and_name()
    {
        using ServiceProvider host = Host(kafka: static options => options.Capabilities.SearchRecords = true);

        List<StudioModuleCapabilitiesOn> others = AnonymousStudioCheck.OtherModulesOn(host);

        StudioModuleCapabilitiesOn kafka = others.Should().ContainSingle().Subject;
        kafka.Module.Should().Be("Kafka");
        kafka.Enabled.Should().Equal("SearchRecords");
    }

    [Fact]
    public void No_capability_on_in_any_other_module_is_none_found()
    {
        using ServiceProvider host = Host();

        AnonymousStudioCheck.OtherModulesOn(host).Should().BeEmpty();
    }

    [Fact]
    public void The_shells_ReadOnly_turns_every_module_off_so_none_is_found()
    {
        using ServiceProvider host = Host(static shell => shell.ReadOnly = true, static options => options.Capabilities = KafkaCapabilities.All());

        AnonymousStudioCheck.OtherModulesOn(host).Should().BeEmpty();
    }

    [Fact]
    public void The_database_modules_own_ReadOnly_does_not_reach_kafka()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataStudio().AddPostgres(static options => options.ReadOnly = true);
        services.AddDataStudio().AddKafka(static options => options.Capabilities = KafkaCapabilities.All());
        using ServiceProvider host = services.BuildServiceProvider();

        AnonymousStudioCheck.OtherModulesOn(host).Should().ContainSingle().Which.Enabled.Should().Contain("SearchRecords").And.Contain("LiveTail");
    }
}
