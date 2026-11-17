using Confluent.Kafka;

using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace DataStudio.Kafka.Tests.Shell;

/// <summary>
/// The golden files of the chrome a Kafka-only host (<c>AddDataStudio().AddKafka()</c>) prerenders around a page: the anonymous banner, the rail
/// (the module's section and the shell's own) and the header (scope selector, capability chip with every capability on, preferences). The front address of a host with one
/// module is a redirect to that module's home, so it has no chrome of its own here; the front page of a host with two modules is the shell suite's
/// (<c>FrontPage</c> tiles in <c>ShellFrontEndpointTests</c>), and the chrome around a database module's pages is the Postgres and Marten suites'.
/// </summary>
/// <remarks>
/// The broker is one nothing listens on: prerendering reads nothing, because a page's body starts only once its circuit is interactive.
/// </remarks>
public class ShellGoldenTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/DataStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });
        builder.Services.AddDataStudio().AddKafka(options =>
        {
            options.Capabilities = KafkaCapabilities.All();
            options.ClusterAuthorizationPolicy = "kafka";   // the startup guard wants a policy named; prerendering evaluates none
            options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary"));
        });

        WebApplication app = builder.Build();
        app.MapDataStudio().AllowAnonymous();
        return app;
    }

    [Theory]
    [InlineData("/studio/kafka")]
    [InlineData("/studio/kafka/search")]
    public async Task The_prerendered_chrome_of_a_page_is_stable(string path)
    {
        await using WebApplication app = CreateApp();

        string golden = await ShellGolden.GoldenAsync(app, path, Token);

        await Verify(golden).UseParameters(path);
    }
}
