using DataStudio.Components;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Postgres;

using Microsoft.Extensions.Options;

namespace DataStudio.Postgres.Tests.Shell;

/// <summary>
/// <c>StudioLink.To</c> used to ask the engine's <c>StudioPageRoutes.BaseRelative</c>, whose body is its argument; the shell references no engine, so it
/// returns its argument itself (v2 step 3). The two must stay the same function: a link the shell builds for a module and the address the module's own
/// route constants give are one string.
/// </summary>
public class StudioLinkParityTests
{
    private static readonly PostgresStudioSettings Settings = new(Options.Create(new PostgresStudioOptions()));

    [Theory]
    [InlineData("")]
    [InlineData("health")]
    [InlineData("database/object")]
    [InlineData("kafka/topics")]
    [InlineData("query?store=main&db=app")]
    [InlineData("a b/c")]
    public void A_shell_link_is_what_the_engine_makes_relative(string route)
    {
        StudioLink.To(Settings, route).Should().Be(StudioPageRoutes.BaseRelative(route));
    }

    [Fact]
    public void Every_route_constant_the_engine_names_is_linked_as_it_is()
    {
        string[] routes =
        [
            StudioPageRoutes.Connection,
            StudioPageRoutes.DatabaseBrowser,
        ];

        routes.Should().NotBeEmpty().And.OnlyContain(static x => x.Length > 0);

        foreach (string route in routes)
        {
            StudioLink.To(Settings, route).Should().Be(route);
        }
    }
}
