using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Npgsql;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// <c>AddPostgresDiagnostics()</c> (0.11-C, issue #29): the engine alone resolves in a plain container with no ASP.NET Core
/// registration, registers nothing that runs or maps, validates its options when the host starts, and is refused beside a
/// studio in either order.
/// </summary>
public class AddPostgresDiagnosticsTests
{
    /// <summary>
    /// A data source nothing connects to: the container is built and the engine resolved, and every connection waits for a call.
    /// </summary>
    private const string Unreachable = "Host=postgres-diagnostics-test.invalid;Database=none;Username=none;Password=none";

    private static ServiceCollection Engine(Action<PostgresDiagnosticsOptions>? configure = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(NpgsqlDataSource.Create(Unreachable));
        services.AddPostgresDiagnostics(configure);
        return services;
    }

    [Fact]
    public async Task The_engine_resolves_in_a_plain_container_with_no_ASP_NET_Core_registration()
    {
        ServiceCollection services = Engine();

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });

        IPostgresDiagnostics diagnostics = provider.GetRequiredService<IPostgresDiagnostics>();
        diagnostics.Should().BeSameAs(provider.GetRequiredService<IPostgresDiagnostics>(), "one per process");

        string[] codes = [.. diagnostics.Checks.Select(static x => x.Code)];
        codes.Should().HaveCountGreaterThan(40, "the engine's checks are discovered").And.Contain(["C01", "T01"]);
        codes.Should().NotContain(["K05", "K06", "K07"], "those judge a studio's exposure, and there is none");

        // The engine's own services are the container's: the sweep, its cache and the source provider.
        provider.GetRequiredService<PostgresStudio.Services.Health.HealthSweeper>().Should().NotBeNull();
        provider.GetRequiredService<PostgresStudio.Services.Health.HealthSweepCache>().Should().NotBeNull();
        provider.GetRequiredService<IOptions<PostgresDiagnosticsOptions>>().Value.BrowsableSchemas.Should().Equal("public");

        // The studio's paths are relative, as they always were, and meaningless where no UI is mounted.
        diagnostics.GetStudioPath(new PostgresDatabaseRef("default", "app"), PostgresStudioPage.Health)
            .Should().Be("health?store=default&db=app");
    }

    [Fact]
    public async Task The_engine_lists_the_databases_of_the_sources_it_was_given()
    {
        ServiceCollection services = Engine(static options => options.AddDataSource("reporting", static provider => provider.GetRequiredService<NpgsqlDataSource>()));

        await using ServiceProvider provider = services.BuildServiceProvider();

        List<PostgresDatabaseRef> listed = [];
        await foreach (PostgresDatabaseRef database in provider.GetRequiredService<IPostgresDiagnostics>().ListDatabasesAsync(TestContext.Current.CancellationToken))
        {
            listed.Add(database);
        }

        listed.Should().Equal(new PostgresDatabaseRef("default", "default"), new PostgresDatabaseRef("reporting", "reporting"));
    }

    [Fact]
    public void Nothing_that_runs_or_maps_is_registered_and_nothing_connects_at_build()
    {
        ServiceCollection services = Engine();

        services.Where(static x => x.ServiceType == typeof(IHostedService)).Should().BeEmpty("no hosted service, no timer");
        services.Select(static x => x.ServiceType.Namespace ?? string.Empty).Where(static x => x.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .Should().BeEmpty("no ASP.NET Core service is registered");
        services.Where(static x => x.ServiceType == typeof(IPostgresDiagnostics)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }

    [Fact]
    public void The_default_schema_is_public_only_when_nothing_names_one()
    {
        using ServiceProvider none = Engine().BuildServiceProvider();
        none.GetRequiredService<IOptions<PostgresDiagnosticsOptions>>().Value.BrowsableSchemas.Should().Equal("public");

        using ServiceProvider named = Engine(static options => options.BrowsableSchemas.Add("legacy")).BuildServiceProvider();
        named.GetRequiredService<IOptions<PostgresDiagnosticsOptions>>().Value.BrowsableSchemas.Should().Equal("legacy");
    }

    /// <summary>Each failure the options have, with the sentence a host reads; <c>ValidateOnStart</c> raises it when the host starts.</summary>
    public static TheoryData<string, string> Refused => new()
    {
        { "source name", "PostgresDiagnosticsOptions.AddDataSource and AddDatabaseSource names must each be a letter or digit" },
        { "default source", "cannot name a source 'default'" },
        { "duplicate source", "names must be unique, ignoring case, across both" },
        { "query timeout", "PostgresDiagnosticsOptions.QueryTimeout must be between one second and ten minutes" },
        { "console role", "PostgresDiagnosticsOptions.SqlConsoleRole must be a plain Postgres identifier" },
        { "health interval", "PostgresDiagnosticsOptions.HealthInterval must be between fifteen seconds and one hour" },
        { "schema", "PostgresDiagnosticsOptions.BrowsableSchemas entries must each be" },
        { "studio code", "PostgresDiagnosticsOptions.AcceptedChecks key 'K05' is not a health check code PostgresStudio.Diagnostics runs" },
        { "unknown code", "PostgresDiagnosticsOptions.AcceptedChecks key 'Z99' is not a health check code PostgresStudio.Diagnostics runs" },
        { "blank reason", "PostgresDiagnosticsOptions.AcceptedChecks['C01'] needs a reason" },
    };

    private static Action<PostgresDiagnosticsOptions> Configure(string failure) => failure switch
    {
        "source name" => static options => options.AddDataSource("not a name!", static _ => null!),
        "default source" => static options => options.AddDataSource("Default", static _ => null!),
        "duplicate source" => static options => options.AddDataSource("a", static _ => null!).AddDataSource("A", static _ => null!),
        "query timeout" => static options => options.QueryTimeout = TimeSpan.Zero,
        "console role" => static options => options.SqlConsoleRole = "bad role; drop",
        "health interval" => static options => options.HealthInterval = TimeSpan.FromSeconds(1),
        "schema" => static options => options.BrowsableSchemas.Add(" "),
        "studio code" => static options => options.AcceptedChecks["K05"] = "the console reads as the data source's role",
        "unknown code" => static options => options.AcceptedChecks["Z99"] = "no such check",
        "blank reason" => static options => options.AcceptedChecks["C01"] = " ",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_configuration_the_options_refuse_fails_when_the_host_starts(string failure, string sentence)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton(NpgsqlDataSource.Create(Unreachable));
        builder.Services.AddPostgresDiagnostics(Configure(failure));

        using IHost host = builder.Build();

        OptionsValidationException refused = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        refused.Message.Should().Contain(sentence);
    }

    [Fact]
    public async Task A_valid_configuration_starts_and_stops_and_connects_to_nothing()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddSingleton(NpgsqlDataSource.Create(Unreachable));
        builder.Services.AddPostgresDiagnostics(static options =>
        {
            options.AcceptedChecks["C01"] = "the pool is sized for a burst";
            options.QueryTimeout = TimeSpan.FromSeconds(10);
        });

        using IHost host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Calling_it_twice_behaves_as_calling_AddPostgresStudio_twice_does()
    {
        ServiceCollection services = Engine();
        services.AddPostgresDiagnostics();

        services.Where(static x => x.ServiceType == typeof(IPostgresDiagnostics)).Should().ContainSingle("every service is registered once");

        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<IPostgresDiagnostics>().Should().NotBeNull();
    }

    [Fact]
    public void The_engine_before_a_studio_is_refused_with_the_sentence()
    {
        ServiceCollection services = Engine();

        Action add = () => services.AddPostgresStudio();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
        services.Where(static x => x.ServiceType == typeof(PostgresStudio.Services.Settings.StudioRegistrationMarker)).Should().BeEmpty("refused before anything of the studio is registered");
    }

    [Fact]
    public void A_studio_before_the_engine_is_refused_with_the_sentence()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, Nobody>();
        services.AddSingleton(NpgsqlDataSource.Create(Unreachable));
        services.AddPostgresStudio();

        Action add = () => services.AddPostgresDiagnostics();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
    }

    [Fact]
    public async Task A_studio_registers_the_engine_over_the_options_it_configured()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, Nobody>();
        services.AddSingleton(NpgsqlDataSource.Create(Unreachable));
        services.AddPostgresStudio(static options =>
        {
            options.QueryTimeout = TimeSpan.FromSeconds(12);
            options.AddDataSource("reporting", static provider => provider.GetRequiredService<NpgsqlDataSource>());
        });

        await using ServiceProvider provider = services.BuildServiceProvider();

        // The one options object: what the engine reads is what the studio's options hold, live.
        provider.GetRequiredService<IOptions<PostgresDiagnosticsOptions>>().Value.Should().BeSameAs(provider.GetRequiredService<IOptions<PostgresStudioOptions>>().Value);
        provider.GetRequiredService<PostgresStudio.Services.Settings.IDiagnosticsSettings>().Should().BeSameAs(provider.GetRequiredService<PostgresStudio.Services.Settings.IStudioSettings>());
        provider.GetRequiredService<PostgresStudio.Services.Settings.IDiagnosticsSettings>().QueryTimeout.Should().Be(TimeSpan.FromSeconds(12));
    }

    /// <summary>
    /// A host with no studio has no <c>PostgresStudioOptions</c>: the words its settings carry name the option on
    /// <c>PostgresDiagnosticsOptions</c>, and a studio's host keeps the sentence it always had.
    /// </summary>
    [Fact]
    public async Task A_host_with_no_studio_reads_findings_that_name_PostgresDiagnosticsOptions()
    {
        await using ServiceProvider provider = Engine().BuildServiceProvider();

        PostgresStudio.Services.Settings.StudioText words = provider.GetRequiredService<PostgresStudio.Services.Settings.IDiagnosticsSettings>().Text;

        words.Should().BeSameAs(PostgresStudio.Services.Postgres.PostgresStudioText.DiagnosticsInstance);
        words.OptionsTypeName.Should().Be("PostgresDiagnosticsOptions");
        words.UnjudgedOldest.Should().Contain("a schema PostgresDiagnosticsOptions.BrowsableSchemas does not admit").And.NotContain("PostgresStudioOptions");

        PostgresStudio.Services.Postgres.PostgresStudioText.Instance.UnjudgedOldest
            .Should().Contain("a schema PostgresStudioOptions.BrowsableSchemas does not admit", "a studio host's sentence is unchanged")
            .And.NotContain("PostgresDiagnosticsOptions");

        // Only the option's owner differs: every other word is the studio's.
        foreach (System.Reflection.PropertyInfo property in typeof(PostgresStudio.Services.Settings.StudioText).GetProperties()
            .Where(static x => x.PropertyType == typeof(string) && x.Name is not (nameof(PostgresStudio.Services.Settings.StudioText.OptionsTypeName) or nameof(PostgresStudio.Services.Settings.StudioText.UnjudgedOldest))))
        {
            property.GetValue(words).Should().Be(property.GetValue(PostgresStudio.Services.Postgres.PostgresStudioText.Instance), property.Name);
        }
    }

    private sealed class Nobody : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
