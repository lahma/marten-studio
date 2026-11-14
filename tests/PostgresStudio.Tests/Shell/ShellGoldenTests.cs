using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace PostgresStudio.Tests.Shell;

/// <summary>
/// The golden files of the chrome Postgres Studio prerenders around a page: the anonymous banner, the rail (every section and entry) and the
/// header (scope selector, capability chip, preferences). They hold the shell's markup still while it is restructured: no rendered word,
/// attribute or class may move, so a later packet never re-accepts these baselines.
/// </summary>
/// <remarks>
/// The data source points at a port nothing listens on: prerendering reads nothing, because a page's body starts only once its circuit is
/// interactive, and a request that did connect would fail rather than pass.
/// </remarks>
public class ShellGoldenTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(Action<PostgresStudioOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
        builder.Services.AddPostgresStudio(configure);

        WebApplication app = builder.Build();
        app.MapPostgresStudio().AllowAnonymous();
        return app;
    }

    [Theory]
    [InlineData("/postgres")]
    [InlineData("/postgres/health")]
    [InlineData("/postgres/activity")]
    public async Task The_prerendered_chrome_of_a_page_is_stable(string path)
    {
        await using WebApplication app = CreateApp();

        string golden = await ShellGolden.GoldenAsync(app, path, Token);

        await Verify(golden).UseParameters(path);
    }

    [Fact]
    public async Task The_capability_chip_of_the_front_page_lists_every_capability()
    {
        await using WebApplication app = CreateApp(static options => options.Capabilities = PostgresStudioCapabilities.All());

        string golden = await ShellGolden.GoldenAsync(app, "/postgres", Token);

        await Verify(golden);
    }
}
