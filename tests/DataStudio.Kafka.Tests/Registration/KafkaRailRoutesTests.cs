using DataStudio.Marten.Tests.Support;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Registration;

/// <summary>
/// v2 step 5 with the Kafka module: its rail routes and its route constants are pages below <c>kafka</c>, in a host that has only Kafka (which needs no
/// Postgres page to resolve one). The same check in a host with the Postgres module beside it is the live project's (<c>KafkaBesidePostgresTests</c>).
/// </summary>
public class KafkaRailRoutesTests
{
    private static ServiceProvider KafkaOnly() => new ServiceCollection().AddLogging().AddDataStudio().AddKafka().Services.BuildServiceProvider();

    [Fact]
    public void Every_rail_route_of_a_kafka_only_host_matches_a_page()
    {
        using ServiceProvider host = KafkaOnly();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Navigation.SelectMany(static x => x.Items).Select(static x => x.Route).Should().OnlyContain(static x => x == "activity" || x!.StartsWith("kafka", StringComparison.Ordinal));
        ComposedRailAssertions.NoOneDotXAddressIsAPage(shell);
    }

    [Fact]
    public void Every_route_constant_of_the_kafka_module_is_a_page()
    {
        using ServiceProvider host = KafkaOnly();
        StudioShellDescriptor shell = host.GetRequiredService<StudioShellDescriptor>();

        ComposedRailAssertions.EveryRouteConstantIsAPage(shell, typeof(DataStudio.Kafka.Services.KafkaAddress).Assembly);
    }
}
