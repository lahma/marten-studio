using Bunit;

using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Components.Pages;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Tests.Support;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>The Overview's server line says when the server is below the supported floor (14), and only then.</summary>
public class PostgresOverviewFloorTests
{
    [Theory]
    [InlineData(130023, "13.23", "PostgreSQL 13.23, below the supported floor (14)")]
    [InlineData(140024, "14.24", "PostgreSQL 14.24")]
    [InlineData(180006, "18.6", "PostgreSQL 18.6")]
    public async Task The_version_line_names_the_floor_below_it(int versionNumber, string version, string line)
    {
        FakeServerReader reader = new() { Facts = new PostgresServerFacts(DateTimeOffset.UnixEpoch, versionNumber, version, PostgresPlatform.SelfHosted) };

        await using PostgresComponentContext context = await new PostgresComponentContext(
            services => services.AddSingleton<IPostgresServerReader>(reader)).ReadyAsync();

        var page = context.Render<StudioHome>();

        page.WaitForAssertion(() => page.FindAll(".ms-pg-overview-facts").Should().ContainSingle());
        page.FindAll(".ms-pg-overview-facts .ms-kv-row")
            .Single(static x => x.QuerySelector(".ms-kv-key")!.TextContent == "Version")
            .QuerySelector(".ms-kv-value")!.TextContent.Should().Be(line);
    }
}
