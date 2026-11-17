using DataStudio.Marten.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Tests.Modules;

/// <summary>v2 step 5 in the host this suite can build, the one with only the Kafka module: its rail and the shell's own section are pages.</summary>
public class KafkaOnlyRailRoutesTests
{
    [Fact]
    public void Every_rail_route_of_a_kafka_only_host_matches_a_page_and_none_of_the_1x_addresses_does()
    {
        using ServiceProvider host = new ServiceCollection().AddLogging().AddDataStudio().AddKafka().Services.BuildServiceProvider();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Navigation.Select(static x => x.Title).Should().Equal("Kafka", "System");
        ComposedRailAssertions.NoOneDotXAddressIsAPage(shell);
    }
}
