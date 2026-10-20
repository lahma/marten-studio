using System.Net;

using Marten;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The export behind a real health endpoint on a <c>TestServer</c>, the way a host maps it: the status code an
/// orchestrator acts on, and a body that - with ASP.NET Core's default writer - is the status word and nothing
/// else.
/// </summary>
public class HealthExportEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <param name="severity">What K90 finds, by <c>HealthSeverity</c> name (an internal enum), or null for nothing.</param>
    /// <param name="code">The status code an orchestrator acts on.</param>
    /// <param name="body">What the default writer prints.</param>
    [Theory]
    [InlineData(null, HttpStatusCode.OK, "Healthy")]
    [InlineData(nameof(HealthSeverity.Info), HttpStatusCode.OK, "Healthy")]
    [InlineData(nameof(HealthSeverity.Warning), HttpStatusCode.OK, "Degraded")]
    [InlineData(nameof(HealthSeverity.Critical), HttpStatusCode.ServiceUnavailable, "Unhealthy")]
    public async Task The_endpoint_answers_with_the_status_and_nothing_more(string? severity, HttpStatusCode code, string body)
    {
        HealthScript script = new();
        if (severity is not null)
        {
            script.Finds("K90", Enum.Parse<HealthSeverity>(severity), "Tenant acme-secret-tenant: session 4242 reads secret_schema.mt_doc_orders");
        }

        await using WebApplication app = CreateApp(script);
        await app.StartAsync(Token);

        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Token);

        response.StatusCode.Should().Be(code);
        (await response.Content.ReadAsStringAsync(Token)).Should().Be(body, "the default writer prints the status word, so the data never leaves unless the host chooses a writer");
    }

    private static WebApplication CreateApp(HealthScript script)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(script);
        builder.Services.AddSingleton(HealthCheckRegistry.ForTypes([typeof(ScriptedConfigurationCheck)]));
        builder.Services.AddMarten(options =>
        {
            options.Connection(HealthExportHarness.Refused(HealthExportHarness.SecretDatabase));
            options.DatabaseSchemaName = "health_export_endpoint";
        });
        builder.Services.AddMartenStudio();
        builder.Services.AddHealthChecks().AddMartenStudio();

        WebApplication app = builder.Build();
        app.MapHealthChecks("/healthz");
        return app;
    }
}
