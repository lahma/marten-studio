using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Endpoints;

/// <summary>
/// The startup guard: an application whose Marten Studio mapping answers nothing about authorization
/// refuses to start, and each of four ways of answering satisfies it.
/// </summary>
/// <remarks>
/// "I forgot to add <c>RequireAuthorization</c>" must not be a silent state (plan D5). The studio's pages
/// can read and edit every document in every Marten store in the process, so an open one is a serious
/// exposure rather than an information leak - and the mistake that opens it is a mapping call that says
/// nothing at all.
/// </remarks>
public class StartupGuardTest
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp(
        Action<MartenStudioOptions>? configureStudio = null,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMartenStudio(configureStudio);
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
    public async Task A_mapping_that_answers_nothing_refuses_to_start()
    {
        await using var app = CreateApp();
        app.MapMartenStudio();

        var failure = await StartAndStopAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain("Marten Studio is mapped with no authorization");
        failure.Message.Should().Contain("app.MapMartenStudio().RequireAuthorization()");
        failure.Message.Should().Contain("options.AuthorizationPolicy");
        failure.Message.Should().Contain("app.MapMartenStudio().AllowAnonymous()");
        failure.Message.Should().Contain("FallbackPolicy");
        failure.Message.Should().Contain("/marten", "the message names the routes nothing authorizes");
    }

    [Fact]
    public async Task RequireAuthorization_at_the_map_site_satisfies_the_guard()
    {
        await using var app = CreateApp(configureBuilder: static builder =>
            builder.Services.AddAuthorization(static options =>
                options.AddPolicy("studio-policy", static policy => policy.RequireAssertion(static _ => true))));

        app.MapMartenStudio().RequireAuthorization("studio-policy");

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task AllowAnonymous_at_the_map_site_satisfies_the_guard()
    {
        await using var app = CreateApp();
        app.MapMartenStudio().AllowAnonymous();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task A_configured_AuthorizationPolicy_satisfies_the_guard()
    {
        await using var app = CreateApp(
            options => options.AuthorizationPolicy = "studio-policy",
            static builder => builder.Services.AddAuthorization(static options =>
                options.AddPolicy("studio-policy", static policy => policy.RequireAssertion(static _ => true))));

        app.MapMartenStudio();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task A_host_FallbackPolicy_satisfies_the_guard()
    {
        await using var app = CreateApp(configureBuilder: static builder =>
            builder.Services.AddAuthorization(static options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(static _ => true).Build()));

        app.MapMartenStudio();

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    /// <summary>
    /// A group's own data sources answer with the endpoints as they were mapped - before the group's
    /// prefix and before the group's conventions - so reading them in <c>StartingAsync</c> would refuse a
    /// mapping the application had authorized. The guard skips the group and catches it in
    /// <c>StartedAsync</c> from the container's own endpoint data source instead.
    /// </summary>
    [Fact]
    public async Task A_group_that_authorizes_the_studio_satisfies_the_guard()
    {
        await using var app = CreateApp(configureBuilder: static builder =>
            builder.Services.AddAuthorization(static options =>
                options.AddPolicy("studio-policy", static policy => policy.RequireAssertion(static _ => true))));

        var group = app.MapGroup("/admin");
        group.MapMartenStudio();
        group.RequireAuthorization("studio-policy");

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    [Fact]
    public async Task A_group_that_authorizes_nothing_is_still_caught()
    {
        await using var app = CreateApp();

        var group = app.MapGroup("/admin");
        group.MapMartenStudio();

        var failure = await StartAndStopAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain("Marten Studio is mapped with no authorization");
    }

    /// <summary>
    /// A <c>Startup.Configure</c> class, or anything else that maps inside <c>UseEndpoints</c>, builds its
    /// routes while the web host is starting - after <c>StartingAsync</c> has already run and found
    /// nothing. Later than ideal, since the listener is bound by then, but the host stops on the
    /// exception and the alternative is passing it in silence.
    /// </summary>
    [Fact]
    public async Task A_mapping_inside_UseEndpoints_is_still_caught()
    {
        await using var app = CreateApp();

        app.UseRouting();
#pragma warning disable ASP0014 // Suggest using top level route registrations - the shape under test is exactly this one.
        app.UseEndpoints(endpoints => endpoints.MapMartenStudio());
#pragma warning restore ASP0014

        var failure = await StartAndStopAsync(app);

        failure.Should().BeOfType<InvalidOperationException>();
        failure!.Message.Should().Contain("Marten Studio is mapped with no authorization");
    }

    [Fact]
    public async Task A_mapping_inside_UseEndpoints_that_authorizes_itself_passes()
    {
        await using var app = CreateApp(configureBuilder: static builder =>
            builder.Services.AddAuthorization(static options =>
                options.AddPolicy("studio-policy", static policy => policy.RequireAssertion(static _ => true))));

        app.UseRouting();
#pragma warning disable ASP0014
        app.UseEndpoints(static endpoints => endpoints.MapMartenStudio().RequireAuthorization("studio-policy"));
#pragma warning restore ASP0014

        (await StartAndStopAsync(app)).Should().BeNull();
    }

    /// <summary>
    /// Registering the services and never mapping the endpoints serves nothing, so there is nothing to
    /// refuse. The guard must not turn "I added the package" into a startup failure.
    /// </summary>
    [Fact]
    public async Task Registering_without_mapping_starts_normally()
    {
        await using var app = CreateApp();

        (await StartAndStopAsync(app)).Should().BeNull();
    }
}
