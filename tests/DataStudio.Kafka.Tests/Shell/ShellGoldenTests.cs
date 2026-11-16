using Confluent.Kafka;

using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using DataStudio.Postgres;

namespace DataStudio.Kafka.Tests.Shell;

/// <summary>
/// The golden files of the chrome a Postgres Studio host with the Kafka module prerenders around a page: the anonymous banner, the rail (every
/// section and entry, the module's among them) and the header (scope selector, capability chip, preferences). They hold the shell's markup
/// still while it is restructured: no rendered word, attribute or class may move, so a later packet never re-accepts these baselines.
/// </summary>
/// <remarks>
/// The data source and the broker are ones nothing listens on: prerendering reads nothing, because a page's body starts only once its circuit
/// is interactive.
/// </remarks>
public class ShellGoldenTests
{
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(bool allCapabilities = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/DataStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });
        builder.Services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
        builder.Services.AddDataStudio().AddPostgres(allCapabilities ? static options => options.Capabilities = PostgresStudioCapabilities.All() : null);
        builder.Services.AddDataStudio().AddKafka(options =>
        {
            options.Capabilities = KafkaStudioCapabilities.All();
            options.ClusterAuthorizationPolicy = "kafka";   // the startup guard wants a policy named; prerendering evaluates none
            options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary"));
        });

        WebApplication app = builder.Build();
        app.MapDataStudio().AllowAnonymous();
        return app;
    }

    [Theory]
    [InlineData("/postgres")]
    [InlineData("/postgres/kafka")]
    [InlineData("/postgres/kafka/search")]
    public async Task The_prerendered_chrome_of_a_page_is_stable(string path)
    {
        await using WebApplication app = CreateApp();

        string golden = await ShellGolden.GoldenAsync(app, path, Token);

        await Verify(golden).UseParameters(path);
    }

    [Fact]
    public async Task The_capability_chip_of_the_front_page_lists_every_capability()
    {
        await using WebApplication app = CreateApp(allCapabilities: true);

        string golden = await ShellGolden.GoldenAsync(app, "/postgres", Token);

        await Verify(golden);
    }
}
