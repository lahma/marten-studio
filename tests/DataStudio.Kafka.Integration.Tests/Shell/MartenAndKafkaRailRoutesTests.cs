using DataStudio.Marten.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Integration.Tests.Shell;

/// <summary>
/// v2 step 5 in a Marten host that also has the Kafka module, the one composition the fast suites cannot hold (no fast suite references both modules, by
/// the fences): every rail route is a page of the composed table, the modules compose in the order the host added them, in either order, and the shell's own
/// section closes the rail. Needs no Docker.
/// </summary>
public class MartenAndKafkaRailRoutesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Every_rail_route_of_a_marten_and_kafka_host_matches_a_page_and_the_modules_are_in_registration_order(bool martenFirst)
    {
        ServiceCollection services = new();
        services.AddLogging();
        if (martenFirst)
        {
            services.AddDataStudio().AddMarten().AddKafka();
        }
        else
        {
            services.AddDataStudio().AddKafka().AddMarten();
        }

        using ServiceProvider host = services.BuildServiceProvider();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Modules.Select(static x => x.Key).Should().Equal(martenFirst ? ["marten", "kafka"] : ["kafka", "marten"]);
        shell.Navigation.Select(static x => x.Title).First().Should().Be(martenFirst ? "Data" : "Kafka");
        shell.Navigation.Select(static x => x.Title).Last().Should().Be("Studio", "the shell's own section is last, and always Studio");
        shell.Navigation.SelectMany(static x => x.Items).Select(static x => x.Route).Should().OnlyContain(
            static x => x == "activity" || x!.StartsWith("kafka", StringComparison.Ordinal) || x.StartsWith("marten", StringComparison.Ordinal) || x.StartsWith("postgres/", StringComparison.Ordinal));
        ComposedRailAssertions.NoOneDotXAddressIsAPage(shell);
    }
}
