using DataStudio.Services.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Npgsql;

using DataStudio.Postgres.Services.Health.Export;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Tests.Endpoints;

/// <summary>
/// The two registration guards (design §4): a <c>MapDataStudio()</c> that answered no authorization question
/// refuses to start (D5), and a second database module in one container is refused with the reason.
/// </summary>
public class PostgresRegistrationGuardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
        builder.Services.AddDataStudio().AddPostgres();
        configureBuilder?.Invoke(builder);
        return builder.Build();
    }

    private static async Task<Exception?> StartAndStopAsync(WebApplication app)
    {
        try
        {
            await app.StartAsync(Token);
        }
        catch (Exception exception)
        {
            return exception;
        }

        await app.StopAsync(Token);
        return null;
    }

    [Fact]
    public async Task A_mapping_that_answers_nothing_refuses_to_start_and_names_the_shells_remedies_and_the_modules_words()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio();

        Exception? failure = await StartAndStopAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain("Data Studio is mapped with no authorization. Its pages carry no authorization, so the structure of every database this application registered");
        failure.Message.Should().Contain("app.MapDataStudio().RequireAuthorization()");
        failure.Message.Should().Contain("services.AddDataStudio(options => options.AuthorizationPolicy");
        failure.Message.Should().Contain("app.MapDataStudio().AllowAnonymous()");
        failure.Message.Should().Contain("/studio", "the message names the routes nothing authorizes");
        failure.Message.Should().NotContain("Marten");
    }

    [Fact]
    public async Task RequireAuthorization_at_the_map_site_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp(static builder =>
            builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        app.MapDataStudio().RequireAuthorization("ops");

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task AllowAnonymous_at_the_map_site_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp();
        app.MapDataStudio().AllowAnonymous();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task A_fallback_policy_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp(static builder =>
            builder.Services.AddAuthorization(static options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(static _ => true).Build()));
        app.MapDataStudio();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public void Calling_AddPostgres_twice_is_one_module()
    {
        ServiceCollection services = new();

        services.AddDataStudio().AddPostgres();
        services.AddDataStudio().AddPostgres();

        services.Count(static x => x.ServiceType == typeof(StudioRegistrationMarker)).Should().Be(1);
        services.Count(static x => x.ServiceType == typeof(StudioDescriptor)).Should().Be(1);
    }

    /// <summary>
    /// Another database module first, then this one: refused, naming both and why. DataStudio.Marten.Tests holds the same refusal with
    /// the real <c>AddMarten()</c> in both orders; this project cannot reference it.
    /// </summary>
    [Fact]
    public void A_second_database_module_is_refused_with_the_reason()
    {
        ServiceCollection services = new();
        StudioRegistrationMarker.Claim(services, new StudioRegistrationMarker("marten", "the Marten module", "AddMarten()"));

        Action add = () => services.AddDataStudio().AddPostgres();

        add.Should().Throw<InvalidOperationException>()
            .WithMessage("AddPostgres() was called in an application that already registered the Marten module with AddMarten().*One database module per application*");

        services.Should().NotContain(static x => x.ServiceType == typeof(StudioDescriptor), "nothing of the refused module is registered");
    }

    [Fact]
    public void The_health_check_export_is_registered_once_under_its_name()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();
        services.AddHealthChecks()
            .AddPostgresDiagnostics(tags: ["ready"])
            .AddPostgresDiagnostics("postgres-studio-live", failureStatus: HealthStatus.Degraded);

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckRegistration[] registrations = [.. provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations];

        registrations.Select(static x => x.Name).Should().Equal("postgres-studio", "postgres-studio-live");
        registrations[0].Tags.Should().Equal("ready");
        registrations[0].FailureStatus.Should().Be(HealthStatus.Unhealthy);
        registrations[1].FailureStatus.Should().Be(HealthStatus.Degraded);
        services.Count(static x => x.ServiceType == typeof(HealthExportRunner)).Should().Be(1, "two checks share one run per interval");

        HealthCheck(registrations[0], provider).Should().BeOfType<StudioHealthCheck>();
    }

    private static IHealthCheck HealthCheck(HealthCheckRegistration registration, IServiceProvider provider) => registration.Factory(provider);

    [Fact]
    public void The_marker_is_PostgresStudios()
    {
        PostgresDescriptor.Marker.Should().Be(new StudioRegistrationMarker("postgres", "the Postgres module", "AddPostgres()"));
    }
}
