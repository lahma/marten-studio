using DataStudio.Marten.Tests.Support;
using DataStudio.Postgres.Components.Pages;
using DataStudio.Postgres.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Postgres.Tests.Modules;

/// <summary>
/// v2 step 5 in a Postgres host: every rail entry and every route constant of the engine's and the module's link classes is a page below
/// <c>postgres</c>, the module's segment, and a 1.x address is not one.
/// </summary>
public class PostgresRailRoutesTests
{
    private static ServiceProvider Host() => new ServiceCollection().AddLogging().AddDataStudio().AddPostgres().Services.BuildServiceProvider();

    [Fact]
    public void Every_rail_route_of_a_postgres_host_matches_a_page_of_the_composed_route_table()
    {
        using ServiceProvider host = Host();

        StudioShellDescriptor shell = ComposedRailAssertions.EveryRailRouteMatchesAPage(host);

        shell.Navigation.SelectMany(static x => x.Items).Select(static x => x.Route).Should().OnlyContain(static x => x == "activity" || x!.StartsWith("postgres", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_route_constant_of_the_engine_and_the_postgres_module_is_a_page()
    {
        using ServiceProvider host = Host();
        StudioShellDescriptor shell = host.GetRequiredService<StudioShellDescriptor>();

        ComposedRailAssertions.EveryRouteConstantIsAPage(shell, typeof(PostgresRoutes).Assembly, typeof(StudioHome).Assembly);
    }

    [Fact]
    public void A_1x_address_is_not_a_page()
    {
        using ServiceProvider host = Host();

        ComposedRailAssertions.NoOneDotXAddressIsAPage(host.GetRequiredService<StudioShellDescriptor>());
    }
}
