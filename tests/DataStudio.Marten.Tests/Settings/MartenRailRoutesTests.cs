using DataStudio.Marten.Services;
using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Settings;

/// <summary>
/// v2 step 5 in a Marten host: every rail entry and every route constant of the Marten module's link classes (and of the Postgres pages it serves
/// over its databases) is a page, the Marten pages below <c>marten</c> and the Postgres pages below <c>postgres</c>; a 1.x address is not one.
/// </summary>
public class MartenRailRoutesTests
{
    private static ServiceProvider Host() => new ServiceCollection().AddLogging().AddDataStudio().AddMarten().Services.BuildServiceProvider();

    [Fact]
    public void Every_rail_route_of_a_marten_host_matches_a_page_of_the_composed_route_table()
    {
        using ServiceProvider host = Host();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Navigation.SelectMany(static x => x.Items).Select(static x => x.Route).Should().OnlyContain(
            static x => x == "activity" || x!.StartsWith("marten", StringComparison.Ordinal) || x.StartsWith("postgres/", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_route_constant_of_the_marten_module_the_postgres_pages_and_the_engine_is_a_page()
    {
        using ServiceProvider host = Host();
        StudioShellDescriptor shell = host.GetRequiredService<StudioShellDescriptor>();

        ComposedRailAssertions.EveryRouteConstantIsAPage(
            shell,
            typeof(MartenRoutes).Assembly,
            typeof(PostgresRoutes).Assembly,
            typeof(DataStudio.Postgres.Components.Pages.StudioHome).Assembly);
    }

    [Fact]
    public void A_1x_address_is_not_a_page()
    {
        using ServiceProvider host = Host();

        ComposedRailAssertions.NoOneDotXAddressIsAPage(host.GetRequiredService<StudioShellDescriptor>());
    }

    [Fact]
    public void The_marten_segment_is_spelled_once_and_the_pages_say_it_as_text()
    {
        MartenRoutes.Root.Should().Be("marten");
        MartenDescriptor.PageRoot.Should().Be("/" + MartenRoutes.Root);
        MartenDescriptor.Key.Should().Be(MartenRoutes.Root);
    }
}
