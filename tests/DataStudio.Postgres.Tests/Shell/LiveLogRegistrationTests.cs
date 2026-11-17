using DataStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataStudio.Postgres.Tests.Shell;

/// <summary>
/// v2 step 4, decision 6: the shell registers a live-update log in its own words, and the database module replaces it with its own whichever of the
/// two registered last, so a database host logs what it has always logged.
/// </summary>
public class LiveLogRegistrationTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
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

    [Fact]
    public void A_module_added_twice_leaves_one_log_registration()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();
        services.AddDataStudio().AddPostgres();

        services.Count(static x => x.ServiceType == typeof(StudioLiveLog)).Should().Be(1);
    }
}
