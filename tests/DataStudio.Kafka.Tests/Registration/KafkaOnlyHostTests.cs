using System.Net;

using Confluent.Kafka;

using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Registration;

/// <summary>
/// v2 step 7: a host with <c>AddDataStudio().AddKafka()</c> and nothing else needs no database in its output. This suite is that host: its output folder
/// holds no <c>Npgsql.dll</c>, no <c>DataStudio.Postgres*.dll</c> and no Marten, JasperFx or Weasel, and a host built here starts, redirects the mount to
/// the module, serves the module's pages behind the shell's own policy, and serves the shell's Activity page. (The restore-graph and built-reference
/// fences are <c>KafkaFenceTests</c>.)
/// </summary>
public class KafkaOnlyHostTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>What no assembly of a Kafka-only host may be: each as a file-name prefix.</summary>
    private static readonly string[] Forbidden = ["Npgsql", "DataStudio.Postgres", "Marten", "JasperFx", "Weasel"];

    [Fact]
    public void The_output_folder_holds_no_Npgsql_no_database_module_and_no_Marten_family()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileName(x))];

        files.Should().Contain("DataStudio.dll").And.Contain("DataStudio.Kafka.dll").And.Contain("DataStudio.Kafka.Core.dll", "the folder read is the real one");
        files.Where(static x => Forbidden.Any(prefix => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Should().BeEmpty(
            "Npgsql.dll, DataStudio.Postgres*.dll, Marten*.dll, JasperFx*.dll and Weasel*.dll belong to a database module, and a Kafka-only host has none");
    }

    private static WebApplication CreateApp(Action<DataStudioOptions>? configureShell = null, bool authenticate = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Environment.WebRootFileProvider = new TestFileProvider(new Dictionary<string, byte[]>
        {
            ["_framework/blazor.web.js"] = "// blazor.web.js"u8.ToArray(),
            ["_content/DataStudio/css/studio.css"] = ":root { }"u8.ToArray(),
        });

        builder.Services.AddDataStudio(configureShell).AddKafka(static options =>
            options.AddCluster("primary", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Primary")));

        if (authenticate)
        {
            builder.Services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, null);
            builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAuthenticatedUser()));
        }

        return builder.Build();
    }

    [Fact]
    public async Task A_kafka_only_host_starts_redirects_the_mount_to_the_module_and_serves_its_pages_and_the_Activity_page()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage front = await client.GetAsync(new Uri("/studio", UriKind.Relative), Token);
            front.StatusCode.Should().Be(HttpStatusCode.Found);
            front.Headers.Location!.OriginalString.Should().Be("/studio/kafka", "one module is composed, so the mount leads to it");

            using HttpResponseMessage kafka = await client.GetAsync(new Uri("/studio/kafka", UriKind.Relative), Token);
            kafka.StatusCode.Should().Be(HttpStatusCode.OK);
            (await kafka.Content.ReadAsStringAsync(Token)).Should().Contain("ms-k-trademark");

            using HttpResponseMessage activity = await client.GetAsync(new Uri("/studio/activity", UriKind.Relative), Token);
            activity.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task The_module_pages_and_Activity_are_behind_the_shells_policy()
    {
        await using WebApplication app = CreateApp(static shell => shell.AuthorizationPolicy = "ops", authenticate: true);
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDataStudio();

        RouteEndpoint[] pages =
        [
            .. ((IEndpointRouteBuilder) app).DataSources
                .SelectMany(static x => x.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(static x => x.RoutePattern.RawText is "/studio" or "/studio/activity" || (x.RoutePattern.RawText?.StartsWith("/studio/kafka", StringComparison.Ordinal) ?? false)),
        ];
        pages.Select(static x => x.RoutePattern.RawText).Should().Contain(["/studio", "/studio/activity", "/studio/kafka", "/studio/kafka/search"]);
        pages.Should().OnlyContain(static x => x.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(static y => y.Policy == "ops"));

        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage anonymous = await client.GetAsync(new Uri("/studio/kafka", UriKind.Relative), Token);
            anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/studio/kafka", UriKind.Relative));
            request.Headers.Add(TestAuthenticationHandler.UserHeader, "ops");
            using HttpResponseMessage visitor = await client.SendAsync(request, Token);
            visitor.StatusCode.Should().Be(HttpStatusCode.OK, "the policy admits an authenticated visitor");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }
}
