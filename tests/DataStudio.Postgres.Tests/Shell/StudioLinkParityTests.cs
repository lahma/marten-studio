using DataStudio.Components;
using DataStudio.Postgres.Services;

namespace DataStudio.Postgres.Tests.Shell;

/// <summary>
/// <c>StudioLink.To</c> is the identity (v2 step 5): the shell references no engine and a link is the whole address below the mount, relative to the
/// document's studio-rooted <c>&lt;base href&gt;</c>, so the string a module's route constant gives and the link the shell builds for it are one string - and
/// every Postgres route constant starts with the module's one segment.
/// </summary>
public class StudioLinkParityTests
{
    [Theory]
    [InlineData("")]
    [InlineData("postgres/health")]
    [InlineData("postgres/database/object")]
    [InlineData("kafka/topics")]
    [InlineData("postgres/query?store=main&db=app")]
    [InlineData("a b/c")]
    public void A_shell_link_is_its_address(string route)
    {
        StudioLink.To(route).Should().Be(route);
    }

    [Fact]
    public void Every_route_constant_the_engine_names_is_a_postgres_address_and_is_linked_as_it_is()
    {
        string[] routes =
        [
            StudioPageRoutes.Plan,
            StudioPageRoutes.Connection,
            StudioPageRoutes.DatabaseBrowser,
        ];

        routes.Should().NotBeEmpty().And.OnlyContain(static x => x.StartsWith(PostgresRoutes.Root + "/", StringComparison.Ordinal));

        foreach (string route in routes)
        {
            StudioLink.To(route).Should().Be(route);
        }
    }

    [Fact]
    public void The_postgres_segment_is_spelled_once_and_the_pages_say_it_as_text()
    {
        PostgresRoutes.Root.Should().Be("postgres");
        PostgresRoutes.Page("query").Should().Be("postgres/query");
        DataStudio.Postgres.Services.Postgres.PostgresStudioDescriptor.PageRoot.Should().Be("/" + PostgresRoutes.Root);
        DataStudio.Postgres.Services.Postgres.PostgresStudioDescriptor.Key.Should().Be(PostgresRoutes.Root);
    }
}
