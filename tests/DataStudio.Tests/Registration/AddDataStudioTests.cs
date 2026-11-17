using DataStudio.Internal;
using DataStudio.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataStudio.Tests.Registration;

/// <summary>
/// <c>AddDataStudio()</c> alone (v2 step 4): the shell registers itself, with no module and nothing a host did not ask for, once however often it is
/// called; its settings are the shell's own; and the live-update log it brings is in its own words and throttled.
/// </summary>
public class AddDataStudioTests
{
    [Fact]
    public void It_returns_a_builder_over_the_same_collection()
    {
        ServiceCollection services = new();

        DataStudioBuilder builder = services.AddDataStudio();

        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void It_registers_the_shell_and_no_module()
    {
        ServiceCollection services = new();
        services.AddDataStudio(static options => options.Title = "Ops");
        using ServiceProvider provider = services.BuildServiceProvider();

        IShellSettings settings = provider.GetRequiredService<IShellSettings>();
        settings.Should().BeOfType<ShellSettings>();
        settings.ProductName.Should().Be("Data Studio");
        settings.Title.Should().Be("Ops");
        settings.TrimmedPath.Should().Be("/studio");
        settings.ReadOnlyOptionName.Should().Be("DataStudioOptions.ReadOnly");

        provider.GetServices<IStudioModule>().Should().BeEmpty("a module adds itself with its own Add... call");
        provider.GetRequiredService<StudioShellDescriptor>().Modules.Should().BeEmpty();
        provider.GetRequiredService<StudioMappedEndpoints>().Should().NotBeNull();
        provider.GetRequiredService<StudioActionLogService>().Should().NotBeNull();
        provider.GetServices<IHostedService>().OfType<StudioEndpointAuthorizationGuard>().Should().ContainSingle();
    }

    [Fact]
    public void The_shell_reads_its_options_live()
    {
        ServiceCollection services = new();
        services.AddDataStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        IShellSettings settings = provider.GetRequiredService<IShellSettings>();
        settings.ReadOnly.Should().BeFalse();

        provider.GetRequiredService<IOptions<DataStudioOptions>>().Value.ReadOnly = true;
        provider.GetRequiredService<IOptions<DataStudioOptions>>().Value.Path = "/ops/data";

        settings.ReadOnly.Should().BeTrue();
        settings.TrimmedPath.Should().Be("/ops/data");
        settings.HasCustomPath.Should().BeTrue();
    }

    [Fact]
    public void Calling_it_twice_registers_one_shell_and_configures_the_options_twice()
    {
        ServiceCollection services = new();
        DataStudioBuilder first = services.AddDataStudio(static options => options.Title = "first");
        DataStudioBuilder second = services.AddDataStudio(static options => options.AuthorizationPolicy = "ops");
        using ServiceProvider provider = services.BuildServiceProvider();

        second.Services.Should().BeSameAs(first.Services);
        services.Count(static x => x.ServiceType == typeof(IShellSettings)).Should().Be(1);
        services.Count(static x => x.ServiceType == typeof(StudioShellDescriptor)).Should().Be(1);
        provider.GetServices<IHostedService>().OfType<StudioEndpointAuthorizationGuard>().Should().ContainSingle();

        DataStudioOptions options = provider.GetRequiredService<IOptions<DataStudioOptions>>().Value;
        options.Title.Should().Be("first");
        options.AuthorizationPolicy.Should().Be("ops");
    }

    [Fact]
    public void A_registration_of_the_hosts_own_comes_first_and_wins()
    {
        ServiceCollection services = new();
        ShellSettings own = new(Options.Create(new DataStudioOptions { Title = "Own" }));
        services.AddSingleton<IShellSettings>(own);
        services.AddDataStudio();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShellSettings>().Should().BeSameAs(own, "every service is a TryAdd");
    }

    [Fact]
    public void A_null_services_collection_is_refused()
    {
        Action add = static () => DataStudioServiceCollectionExtensions.AddDataStudio(null!);

        add.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------------------------------
    // The shell's live-update log: its own words, and event 9220 throttled by its own clock
    // -------------------------------------------------------------------------------------------

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, EventId Id, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }

    [Fact]
    public void A_failed_live_update_is_logged_at_Debug_in_the_shells_words()
    {
        RecordingLogger logger = new();
        StudioLiveLog log = ShellLiveLog.Create(new ManualClock());

        log.LiveUpdateFailed(logger, new InvalidOperationException("boom"));

        logger.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Debug, default(EventId), "A Data Studio live update failed"));
    }

    [Fact]
    public void A_failing_handler_is_event_9220_a_Warning_once_per_exception_type_per_ten_minutes_and_Debug_between()
    {
        ManualClock clock = new();
        RecordingLogger logger = new();
        StudioLiveLog log = ShellLiveLog.Create(clock);
        const string message = "A Data Studio page failed to handle a live update failure";

        log.LiveUpdateHandlerFailed(logger, new InvalidOperationException("one"));
        log.LiveUpdateHandlerFailed(logger, new InvalidOperationException("two"));
        log.LiveUpdateHandlerFailed(logger, new TimeoutException("another kind"));
        clock.Advance(TimeSpan.FromMinutes(9));
        log.LiveUpdateHandlerFailed(logger, new InvalidOperationException("three"));
        clock.Advance(TimeSpan.FromMinutes(2));
        log.LiveUpdateHandlerFailed(logger, new InvalidOperationException("four"));

        logger.Entries.Select(static x => x.Level).Should().Equal(
            [LogLevel.Warning, LogLevel.Debug, LogLevel.Warning, LogLevel.Debug, LogLevel.Warning]);
        logger.Entries.Should().OnlyContain(static x => x.Id.Id == 9220 && x.Message == message);
    }
}
