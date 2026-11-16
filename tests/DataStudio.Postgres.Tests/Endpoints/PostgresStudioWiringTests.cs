using System.Reflection;
using System.Security.Claims;

using DataStudio.Marten.Tests.Support;
using DataStudio.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using Npgsql;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Health.Export;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Tests.Endpoints;

/// <summary>
/// A real <c>AddPostgresStudio()</c> host hands every service that takes its words optionally - the settings or the
/// text, defaulting to MartenStudio's only so a test can build the service by hand - PostgresStudio's own (PS-3b-fix).
/// </summary>
public sealed class PostgresStudioWiringTests : IAsyncDisposable
{
    private readonly ServiceProvider services;

    public PostgresStudioWiringTests()
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
        collection.AddHealthChecks().AddPostgresStudio();
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
            scope.GetRequiredService<IStudioScopeCatalog>(),
            scope.GetRequiredService<StudioState>(),
            scope.GetRequiredService<INavIndicatorService>(),
            scope.GetRequiredService<StudioLiveUpdatesFactory>(),
            scope.GetRequiredService<StudioLiveUpdatesFactory>().Create(),
            scope.GetRequiredService<HealthExportRunner>(),
        ];

        foreach (object owner in owners)
        {
            FieldInfo[] words = [.. owner.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Where(static x => x.FieldType == typeof(StudioProduct) || x.FieldType == typeof(StudioText) || x.FieldType == typeof(StudioLiveLog))];

            words.Should().NotBeEmpty(owner.GetType().Name + " takes the studio's words");

            foreach (FieldInfo field in words)
            {
                object? value = field.GetValue(owner);
                (value is StudioProduct.PostgresStudio || ReferenceEquals(value, PostgresStudioText.Instance) || SaysPostgresStudio(value as StudioLiveLog))
                    .Should().BeTrue(owner.GetType().Name + "." + field.Name + " is PostgresStudio's, not MartenStudio's");
            }
        }

        scope.GetRequiredService<HealthExportRunner>().Text.Should().BeSameAs(PostgresStudioText.Instance);
        scope.GetRequiredService<IStudioSettings>().Text.Should().BeSameAs(PostgresStudioText.Instance);
    }

    /// <summary>
    /// Whether a live-update log speaks in Postgres Studio's words. The log closes over the container's throttle, so it is no longer one shared
    /// instance to compare by reference: its first line, the Debug one a failed tick writes, names the product.
    /// </summary>
    private static bool SaysPostgresStudio(StudioLiveLog? log)
    {
        if (log is null)
        {
            return false;
        }

        CapturingLoggerProvider captured = new();
        log.LiveUpdateFailed(captured.CreateFactory().CreateLogger("probe"), new InvalidOperationException("probe"));

        return captured.Entries.Should().ContainSingle().Which.Message.Contains("Postgres Studio", StringComparison.Ordinal);
    }
    [Fact]
    public void A_health_check_registered_without_the_studio_names_AddPostgresStudio()
    {
        ServiceCollection collection = new();
        collection.AddLogging();
        collection.AddHealthChecks().AddPostgresStudio();
        using ServiceProvider alone = collection.BuildServiceProvider();

        alone.GetRequiredService<HealthExportRunner>().Text.Should().BeSameAs(PostgresStudioText.Instance,
            "with no settings registered, the words are the registering studio's");
        HealthExportSummary.NotRegistered(DateTimeOffset.UnixEpoch, alone.GetRequiredService<HealthExportRunner>().Text).Problem
            .Should().Be("Postgres Studio's services are not registered: call services.AddPostgresDiagnostics() or services.AddPostgresStudio() as well as AddHealthChecks().AddPostgresStudio()");
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
