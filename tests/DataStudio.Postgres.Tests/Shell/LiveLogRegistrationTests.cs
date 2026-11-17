using DataStudio.Postgres.Services;
using DataStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Tests.Shell;

/// <summary>
/// v2 step 4, decision 6: the shell registers a live-update log, and the database module replaces it with its own whichever of the two registered
/// last. The two say the same sentences, so what tells them apart is the throttle of event 9220: the shell's own is private to it, and the database
/// module's is the container's <see cref="StudioLogThrottle" />, so that a failure the container's throttle has already warned of is Debug.
/// </summary>
public class LiveLogRegistrationTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    private static string Said(Action<IServiceCollection> register)
    {
        ServiceCollection services = new();
        register(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        RecordingLogger logger = new();

        provider.GetRequiredService<StudioLiveLog>().LiveUpdateFailed(logger, new InvalidOperationException("boom"));

        return logger.Messages.Single();
    }

    [Fact]
    public void The_shell_alone_says_it_in_its_own_words()
    {
        Said(static services => services.AddDataStudio()).Should().Be("A Data Studio live update failed");
    }

    [Fact]
    public void The_database_module_says_it_in_its_own_words()
    {
        Said(static services => services.AddDataStudio().AddPostgres()).Should().Be("A Data Studio live update failed");
    }

    [Fact]
    public void The_shell_registered_again_after_the_module_does_not_take_the_words_back()
    {
        Said(static services =>
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio();
        }).Should().Be("A Data Studio live update failed");
    }

    /// <summary>
    /// The one thing that tells the database module's log from the shell's: event 9220's level comes from the container's throttle. A warning the
    /// container's throttle has already spent makes the failure Debug here; the shell's own throttle, fresh, would write it as a Warning. Deleting the
    /// <c>Replace</c> in the module's registration, in either order, fails this.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_database_modules_log_replaces_the_shells_in_either_order_and_takes_its_level_from_the_containers_throttle(bool shellFirst)
    {
        ServiceCollection services = new();

        if (shellFirst)
        {
            services.AddDataStudio();
            services.AddDataStudio().AddPostgres();
        }
        else
        {
            services.AddDataStudio().AddPostgres();
            services.AddDataStudio();
        }

        using ServiceProvider provider = services.BuildServiceProvider();
        InvalidOperationException failure = new("boom");

        StudioLogThrottle throttle = provider.GetRequiredService<StudioLogThrottle>();
        StudioLogThrottle.LevelOrWarning(throttle, "LiveUpdates.FailedHandler", null, null, StudioLogThrottle.KindOf(failure))
            .Should().Be(LogLevel.Warning, "the container's throttle has not warned of this kind of failure yet, and now it has");

        RecordingLogger logger = new();
        provider.GetRequiredService<StudioLiveLog>().LiveUpdateHandlerFailed(logger, failure);

        logger.Levels.Should().Equal([LogLevel.Debug], "the module's log asks the container's throttle, which has just warned; the shell's own would warn");
    }

    [Fact]
    public void A_module_added_twice_leaves_one_log_registration()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();
        services.AddDataStudio().AddPostgres();

        services.Count(static x => x.ServiceType == typeof(StudioLiveLog)).Should().Be(1);
    }
}
