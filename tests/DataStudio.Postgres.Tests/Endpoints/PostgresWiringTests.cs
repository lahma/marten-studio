using System.Reflection;
using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using Npgsql;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Health.Export;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Tests.Endpoints;

/// <summary>
/// A real <c>AddPostgresStudio()</c> host hands every service that takes its words optionally - the settings or the
/// text, defaulting to MartenStudio's only so a test can build the service by hand - PostgresStudio's own (PS-3b-fix).
/// </summary>
public sealed class PostgresWiringTests : IAsyncDisposable
{
    private readonly ServiceProvider services;

    public PostgresWiringTests()
    {
        ServiceCollection collection = new();
        collection.AddLogging();
        collection.AddAuthorization();
        collection.AddSingleton<AuthenticationStateProvider, Anonymous>();
        collection.AddSingleton<IJSRuntime, NoJs>();
        collection.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment, WebHost>();
        collection.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        collection.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=app;Username=app"));
        collection.AddDataStudio().AddPostgres();
        collection.AddHealthChecks().AddPostgresDiagnostics();
        services = collection.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync() => await services.DisposeAsync();

    [Fact]
    public async Task Every_service_with_optional_words_is_given_PostgresStudios()
    {
        await using AsyncServiceScope circuit = services.CreateAsyncScope();
        IServiceProvider scope = circuit.ServiceProvider;

        object[] owners =
        [
            scope.GetRequiredService<StudioActionLog>(),
            scope.GetRequiredService<HealthExportRunner>(),
        ];

        foreach (object owner in owners)
        {
            FieldInfo[] words = [.. owner.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(static x => x.FieldType == typeof(StudioText))];

            words.Should().NotBeEmpty(owner.GetType().Name + " takes the studio's words");

            foreach (FieldInfo field in words)
            {
                ReferenceEquals(field.GetValue(owner), PostgresText.Instance)
                    .Should().BeTrue(owner.GetType().Name + "." + field.Name + " is PostgresStudio's, not MartenStudio's");
            }
        }

        scope.GetRequiredService<HealthExportRunner>().Text.Should().BeSameAs(PostgresText.Instance);
        scope.GetRequiredService<IStudioSettings>().Text.Should().BeSameAs(PostgresText.Instance);
    }

    [Fact]
    public void A_health_check_registered_without_the_studio_names_AddPostgres()
    {
        ServiceCollection collection = new();
        collection.AddLogging();
        collection.AddHealthChecks().AddPostgresDiagnostics();
        using ServiceProvider alone = collection.BuildServiceProvider();

        alone.GetRequiredService<HealthExportRunner>().Text.Should().BeSameAs(PostgresText.Instance,
            "with no settings registered, the words are the registering studio's");
        HealthExportSummary.NotRegistered(DateTimeOffset.UnixEpoch, alone.GetRequiredService<HealthExportRunner>().Text).Problem
            .Should().Be("Data Studio's services are not registered: call services.AddPostgresDiagnostics() or services.AddDataStudio().AddPostgres() as well as AddHealthChecks().AddPostgresDiagnostics()");
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class WebHost : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string ApplicationName { get; set; } = "DataStudio.Postgres.Tests";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
