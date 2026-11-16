using Marten;

using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace DataStudio.Marten.Tests.Shell;

/// <summary>
/// The golden files of the chrome Marten Studio prerenders around a page: the anonymous banner, the rail (every section and entry) and the
/// header (scope selector, capability chip, preferences). They hold the shell's markup still while it is restructured: no rendered word,
/// attribute or class may move, so a later packet never re-accepts these baselines.
/// </summary>
/// <remarks>
/// The store points at a host nothing resolves: prerendering reads nothing, because a page's body starts only once its circuit is interactive.
/// </remarks>
public class ShellGoldenTests
{
    private const string ConnectionString = "Host=marten-studio-shell-golden.invalid;Database=none;Username=none;Password=none";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(Action<MartenStudioOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMarten(static options => options.Connection(ConnectionString));
        builder.Services.AddDataStudio().AddMarten(configure);

        WebApplication app = builder.Build();
        app.MapDataStudio().AllowAnonymous();
        return app;
    }

    [Theory]
    [InlineData("/studio")]
    [InlineData("/studio/documents")]
    [InlineData("/studio/health")]
    [InlineData("/studio/activity")]
    public async Task The_prerendered_chrome_of_a_page_is_stable(string path)
    {
        await using WebApplication app = CreateApp();

        string golden = await ShellGolden.GoldenAsync(app, path, Token);

        await Verify(golden).UseParameters(path);
    }

    [Fact]
    public async Task The_capability_chip_of_the_front_page_lists_every_capability()
    {
        await using WebApplication app = CreateApp(static options => options.Capabilities = MartenStudioCapabilities.All());

        string golden = await ShellGolden.GoldenAsync(app, "/studio", Token);

        await Verify(golden);
    }
}
