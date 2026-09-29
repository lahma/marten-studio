using MartenStudio.Internal;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 1: the two public members, their validation, the capability plumbing behind them, and the
/// startup warning.
/// </summary>
public class BrowseDatabaseOptionsTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static MartenStudioOptions Resolve(Action<MartenStudioOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddMartenStudio(configure);
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;
    }

    [Fact]
    public void BrowseDatabase_is_off_by_default_and_on_in_All()
    {
        new MartenStudioCapabilities().BrowseDatabase.Should().BeFalse();
        MartenStudioCapabilities.All().BrowseDatabase.Should().BeTrue();

        Resolve(static _ => { }).BrowsableSchemas.Should().BeEmpty();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("quartz")]
    [InlineData("Legacy")]
    [InlineData("with space")]
    public void An_entry_that_is_star_or_a_quotable_name_is_accepted(string entry)
    {
        MartenStudioOptions options = Resolve(options => options.BrowsableSchemas.Add(entry));

        options.BrowsableSchemas.Should().Equal(entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\"schema")]
    [InlineData("nul\0schema")]
    public void An_entry_that_is_not_a_schema_name_fails_startup_naming_the_option(string entry)
    {
        Action act = () => Resolve(options => options.BrowsableSchemas.Add(entry));

        act.Should().Throw<OptionsValidationException>().WithMessage("*MartenStudioOptions.BrowsableSchemas*");
    }

    [Fact]
    public void An_entry_longer_than_sixty_three_bytes_fails_startup()
    {
        Action act = () => Resolve(static options => options.BrowsableSchemas.Add(new string('s', 64)));

        act.Should().Throw<OptionsValidationException>().WithMessage("*63 bytes*");
    }

    /// <summary>
    /// The packet's own requirement: every member of <c>StudioCapabilityGuard.All</c> maps to a
    /// <c>MartenStudioCapabilities</c> property of the same name, and setting that property - and only
    /// that one - turns it on. The switch's default arm throws, so a forgotten arm fails here.
    /// </summary>
    [Fact]
    public void Every_capability_maps_to_its_own_property_and_the_default_arm_is_never_hit()
    {
        foreach (StudioCapability capability in StudioCapabilityGuard.All)
        {
            var configured = new MartenStudioCapabilities();

            System.Reflection.PropertyInfo property = typeof(MartenStudioCapabilities).GetProperty(capability.ToString())
                ?? throw new InvalidOperationException(capability + " has no MartenStudioCapabilities property.");

            StudioCapabilityGuard.IsConfigured(configured, capability).Should().BeFalse();

            property.SetValue(configured, true);

            StudioCapabilityGuard.IsConfigured(configured, capability).Should().BeTrue(
                capability + " must read " + property.Name);

            StudioCapabilityGuard.All.Where(x => x != capability)
                .Should().OnlyContain(x => !StudioCapabilityGuard.IsConfigured(configured, x));
        }

        Action unknown = () => StudioCapabilityGuard.IsConfigured(new MartenStudioCapabilities(), (StudioCapability) 999);
        unknown.Should().Throw<ArgumentOutOfRangeException>("a capability with no property must not quietly read as off");
    }

    [Fact]
    public void The_reads_beyond_the_store_and_the_mutating_operations_partition_every_capability()
    {
        StudioCapabilityGuard.ReadsBeyondTheStore.Should().Equal(StudioCapability.RunSql, StudioCapability.BrowseDatabase);
        StudioCapabilityGuard.Mutating.Should().NotContain(StudioCapabilityGuard.ReadsBeyondTheStore);
        StudioCapabilityGuard.Mutating.Concat(StudioCapabilityGuard.ReadsBeyondTheStore)
            .Should().BeEquivalentTo(StudioCapabilityGuard.All);
        StudioCapabilityGuard.All.Should().HaveCount(10);
    }

    [Fact]
    public void ReadOnly_turns_BrowseDatabase_off_naming_the_master_switch()
    {
        var guard = new StudioCapabilityGuard(Options.Create(new MartenStudioOptions
        {
            Capabilities = MartenStudioCapabilities.All(),
            ReadOnly = true,
        }));

        guard.IsEnabled(StudioCapability.BrowseDatabase).Should().BeFalse();

        guard.Invoking(static x => x.Require(StudioCapability.BrowseDatabase))
            .Should().Throw<StudioCapabilityDeniedException>()
            .Which.Message.Should().Contain("MartenStudioOptions.ReadOnly");
    }

    // ---------------------------------------------------------------------------------------------
    // Event 9230
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Star_without_a_role_is_a_warning_once_at_startup()
    {
        var logs = new CapturingLoggerProvider();
        var options = new MartenStudioOptions();
        options.BrowsableSchemas.Add("*");

        var notice = new DatabaseBrowserConfigurationNotice(Options.Create(options), Logger(logs), containerEndpoints: MappedStudio());

        await notice.StartedAsync(Token);
        await notice.StartedAsync(Token);

        CapturedLogEntry entry = logs.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.EventId.Id.Should().Be(9230);
        entry.Message.Should().Contain("SqlConsoleRole").And.Contain("BrowsableSchemas");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "studio_reader")]
    public async Task Named_schemas_or_a_role_say_nothing(bool star, string? role)
    {
        var logs = new CapturingLoggerProvider();
        var options = new MartenStudioOptions { SqlConsoleRole = role };
        options.BrowsableSchemas.Add(star ? "*" : "quartz");

        await new DatabaseBrowserConfigurationNotice(Options.Create(options), Logger(logs), containerEndpoints: MappedStudio())
            .StartedAsync(Token);

        logs.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// A host whose options are invalid has always started and failed on first use; the notice reads the
    /// options at startup and must not change that.
    /// </summary>
    [Fact]
    public async Task Invalid_options_do_not_fail_startup_through_the_notice()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMartenStudio(static options => options.BrowsableSchemas.Add("bad\"one"));
        services.AddSingleton<EndpointDataSource>(MappedStudio());

        await using ServiceProvider provider = services.BuildServiceProvider();

        DatabaseBrowserConfigurationNotice notice = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<DatabaseBrowserConfigurationNotice>()
            .Should().ContainSingle().Which;

        Func<Task> start = async () =>
        {
            await notice.StartingAsync(Token);
            await notice.StartAsync(Token);
            await notice.StartedAsync(Token);
        };

        await start.Should().NotThrowAsync();
    }

    /// <summary>
    /// F8: registering the studio and never mapping it changes nothing about how the host starts - the
    /// notice builds no options (the configure callback is never run) and writes no line.
    /// </summary>
    [Fact]
    public async Task A_host_that_registers_the_studio_and_never_maps_it_builds_no_options_and_logs_nothing()
    {
        var logs = new CapturingLoggerProvider();
        int configured = 0;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddMartenStudio(options =>
        {
            Interlocked.Increment(ref configured);
            options.BrowsableSchemas.Add("*");
        });

        await using WebApplication app = builder.Build();
        app.MapGet("/", static () => "the host's own endpoint");

        await app.StartAsync(Token);
        await app.StopAsync(Token);

        configured.Should().Be(0, "nothing built MartenStudioOptions for a host that never mapped the studio");
        logs.Entries.Should().NotContain(static x => x.EventId.Id == 9230);
    }

    /// <summary>
    /// F8: a configure callback of the host's own that throws is not the notice's to surface - on a host
    /// that never mapped the studio it is never run, and on one that did the notice swallows it and the
    /// host starts exactly as it would have.
    /// </summary>
    [Fact]
    public async Task A_throwing_configure_callback_does_not_change_how_the_host_starts()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMartenStudio(static _ => throw new InvalidOperationException("the host's own mistake"));

        await using (WebApplication app = builder.Build())
        {
            Func<Task> unmapped = async () =>
            {
                await app.StartAsync(Token);
                await app.StopAsync(Token);
            };

            await unmapped.Should().NotThrowAsync();
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMartenStudio(static _ => throw new InvalidOperationException("the host's own mistake"));
        services.AddSingleton<EndpointDataSource>(MappedStudio());

        await using ServiceProvider provider = services.BuildServiceProvider();

        DatabaseBrowserConfigurationNotice notice = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<DatabaseBrowserConfigurationNotice>()
            .Should().ContainSingle().Which;

        Func<Task> mapped = () => notice.StartedAsync(Token);

        await mapped.Should().NotThrowAsync("the notice reads the options only to decide whether to warn");
    }

    /// <summary>
    /// The other half of F8: a host that maps the studio still hears about <c>"*"</c> without a role, once,
    /// through the real mapping - which is what the notice looks for.
    /// </summary>
    [Fact]
    public async Task A_host_that_maps_the_studio_with_star_and_no_role_hears_about_it_once()
    {
        var logs = new CapturingLoggerProvider();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddMartenStudio(static options => options.BrowsableSchemas.Add("*"));

        await using WebApplication app = builder.Build();
        app.MapMartenStudio().AllowAnonymous();

        await app.StartAsync(Token);
        await app.StopAsync(Token);

        logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9230);
    }

    private static ILogger<DatabaseBrowserConfigurationNotice> Logger(CapturingLoggerProvider logs) =>
        logs.CreateFactory().CreateLogger<DatabaseBrowserConfigurationNotice>();

    /// <summary>A host's endpoints with one studio endpoint among them, as a mapping leaves them.</summary>
    private static DefaultEndpointDataSource MappedStudio() =>
        new DefaultEndpointDataSource(
            new Endpoint(
                static _ => Task.CompletedTask,
                new EndpointMetadataCollection(new MartenStudioEndpointMarker("the studio", "(test)", isPage: true)),
                "studio page"));
}
