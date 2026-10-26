using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// A Postgres Studio host over one connection string - any login, any database - for the server-posture live tests: the
/// studio's own services resolved as a visitor's would be, so a sweep and the Server page's facts run exactly the code a page
/// runs. <see cref="CreateAsync" /> builds a container; <see cref="ServeAsync" /> a real Kestrel web host with the studio
/// mapped, for the checks that read the mapping (K07).
/// </summary>
internal sealed class PostureHost : IAsyncDisposable
{
    private readonly IServiceProvider services;
    private readonly IAsyncDisposable owner;

    private PostureHost(IServiceProvider services, IAsyncDisposable owner, ResolvedSource resolved)
    {
        this.services = services;
        this.owner = owner;
        Resolved = resolved;
    }

    /// <summary>The source, resolved with no tenant.</summary>
    public ResolvedSource Resolved { get; }

    /// <summary>The host's container.</summary>
    public IServiceProvider Services => services;

    /// <summary>A container with the studio registered over <paramref name="connectionString" />, its own pool.</summary>
    public static async Task<PostureHost> CreateAsync(
        string connectionString,
        Action<PostgresStudioOptions>? options = null,
        string? environment = null)
    {
        ServiceCollection collection = new();
        Register(collection, connectionString, options, environment);
        ServiceProvider provider = collection.BuildServiceProvider();

        try
        {
            return new PostureHost(provider, provider, await ResolveAsync(provider));
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A real web host on Kestrel (<c>127.0.0.1</c>, a free port), the studio mapped by <paramref name="map" /> and the host
    /// started, so the start-up guard has read the finished endpoints.
    /// </summary>
    public static async Task<PostureHost> ServeAsync(
        string connectionString,
        Action<IEndpointConventionBuilder> map,
        Action<PostgresStudioOptions>? options = null,
        string environment = "Production")
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        Register(builder.Services, connectionString, options, environment: null);

        WebApplication app = builder.Build();
        map(app.MapPostgresStudio());

        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            return new PostureHost(app.Services, new Stopping(app), await ResolveAsync(app.Services));
        }
        catch
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>One sweep of <paramref name="kind" />.</summary>
    public Task<HealthSweep> SweepAsync(HealthSweepKind kind = HealthSweepKind.Regular) =>
        services.GetRequiredService<HealthSweeper>().SweepAsync(Resolved, kind, TestContext.Current.CancellationToken);

    /// <summary>The Server page's facts, read afresh.</summary>
    public Task<ServerSettingsFacts> FactsAsync() =>
        services.GetRequiredService<IServerSettingsFactsReader>().ReadAsync(Resolved, TestContext.Current.CancellationToken);

    /// <summary>One check's result in <paramref name="sweep" />.</summary>
    public static HealthCheckResult Result(HealthSweep sweep, string code) => sweep.Results.Single(x => x.Check.Code == code);

    public async ValueTask DisposeAsync()
    {
        // A data source registered as an instance is the host's to dispose, never the container's.
        foreach (NpgsqlDataSource dataSource in services.GetServices<NpgsqlDataSource>())
        {
            await dataSource.DisposeAsync();
        }

        await owner.DisposeAsync();
    }

    private static void Register(IServiceCollection collection, string connectionString, Action<PostgresStudioOptions>? options, string? environment)
    {
        NpgsqlConnectionStringBuilder own = new(connectionString)
        {
            ApplicationName = "ms-s2-host-" + Guid.NewGuid().ToString("N")[..12],
            Pooling = true,
        };

        collection.AddLogging();
        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();
        collection.AddSingleton(NpgsqlDataSource.Create(own.ConnectionString));

        if (environment is not null)
        {
            collection.AddSingleton<IHostEnvironment>(new Environment(environment));
        }

        collection.AddPostgresStudio(studio =>
        {
            studio.BrowsableSchemas.Clear();
            studio.BrowsableSchemas.Add("public");
            options?.Invoke(studio);
        });
    }

    private static async Task<ResolvedSource> ResolveAsync(IServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<StudioSourceResolver>()
            .ResolveAsync(new StudioScope("default", "default", null), null, TestContext.Current.CancellationToken);
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PostgresStudio.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class Stopping(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }
}
