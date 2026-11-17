using DataStudio.Marten.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Integration.Tests.Shell;

/// <summary>
/// v2 step 5 in a Marten host that also has the Kafka module, the one composition the fast suites cannot hold (no fast suite references both modules, by
/// the fences): every rail route is a page of the composed table, the modules compose in key order (Kafka before Marten) and the shell's own section closes
/// the rail. Needs no Docker.
/// </summary>
public class MartenAndKafkaRailRoutesTests
{
    [Fact]
    public void Every_rail_route_of_a_marten_and_kafka_host_matches_a_page_and_the_modules_are_in_key_order()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDataStudio().AddMarten();
        services.AddDataStudio().AddKafka();
        using ServiceProvider host = services.BuildServiceProvider();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Modules.Select(static x => x.Key).Should().Equal("kafka", "marten");
        shell.Navigation.Select(static x => x.Title).First().Should().Be("Kafka");
        shell.Navigation.Select(static x => x.Title).Last().Should().Be("Studio", "Marten's rail already ends with System");
        shell.Navigation.SelectMany(static x => x.Items).Select(static x => x.Route).Should().OnlyContain(
            static x => x == "activity" || x!.StartsWith("kafka", StringComparison.Ordinal) || x.StartsWith("marten", StringComparison.Ordinal) || x.StartsWith("postgres/", StringComparison.Ordinal));
        ComposedRailAssertions.NoOneDotXAddressIsAPage(shell);
    }
}
