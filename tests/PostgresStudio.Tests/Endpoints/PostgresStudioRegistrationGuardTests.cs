using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Endpoints;

/// <summary>
/// The two registration guards (design §4): a <c>MapPostgresStudio()</c> that answered no authorization question
/// refuses to start (D5), and a second studio in one container is refused with the reason.
/// </summary>
public class PostgresStudioRegistrationGuardTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
        builder.Services.AddPostgresStudio();
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
    public async Task A_mapping_that_answers_nothing_refuses_to_start_and_names_PostgresStudios_remedies()
    {
        await using WebApplication app = CreateApp();
        app.MapPostgresStudio();

        Exception? failure = await StartAndStopAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain("Postgres Studio is mapped with no authorization");
        failure.Message.Should().Contain("app.MapPostgresStudio().RequireAuthorization()");
        failure.Message.Should().Contain("options.AuthorizationPolicy");
        failure.Message.Should().Contain("app.MapPostgresStudio().AllowAnonymous()");
        failure.Message.Should().Contain("/postgres", "the message names the routes nothing authorizes");
        failure.Message.Should().NotContain("Marten");
    }

    [Fact]
    public async Task RequireAuthorization_at_the_map_site_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp(static builder =>
            builder.Services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireAssertion(static _ => true))));
        app.MapPostgresStudio().RequireAuthorization("ops");

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task AllowAnonymous_at_the_map_site_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp();
        app.MapPostgresStudio().AllowAnonymous();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task A_fallback_policy_satisfies_the_guard()
    {
        await using WebApplication app = CreateApp(static builder =>
            builder.Services.AddAuthorization(static options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(static _ => true).Build()));
        app.MapPostgresStudio();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public void Calling_AddPostgresStudio_twice_is_one_studio()
    {
        ServiceCollection services = new();

        services.AddPostgresStudio();
        services.AddPostgresStudio();

        services.Count(static x => x.ServiceType == typeof(StudioRegistrationMarker)).Should().Be(1);
        services.Count(static x => x.ServiceType == typeof(StudioDescriptor)).Should().Be(1);
    }

    /// <summary>
    /// Another studio first, then this one: refused, naming both and why. MartenStudio.Tests holds the same refusal with
    /// the real <c>AddMartenStudio()</c> in both orders; this project cannot reference it.
    /// </summary>
    [Fact]
    public void A_second_studio_is_refused_with_the_reason()
    {
        ServiceCollection services = new();
        StudioRegistrationMarker.Claim(services, new StudioRegistrationMarker("marten", "Marten Studio", "AddMartenStudio()"));

        Action add = () => services.AddPostgresStudio();

        add.Should().Throw<InvalidOperationException>()
            .WithMessage("AddPostgresStudio() was called in an application that already registered Marten Studio with AddMartenStudio().*One studio per application*");

        services.Should().NotContain(static x => x.ServiceType == typeof(StudioDescriptor), "nothing of the refused studio is registered");
    }

    [Fact]
    public void The_marker_is_PostgresStudios()
    {
        PostgresStudioDescriptor.Marker.Should().Be(new StudioRegistrationMarker("postgres", "Postgres Studio", "AddPostgresStudio()"));
    }
}
