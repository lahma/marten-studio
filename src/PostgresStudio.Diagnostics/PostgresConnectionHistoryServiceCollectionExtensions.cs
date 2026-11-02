using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.ConnectionHistory;

namespace PostgresStudio;

/// <summary>Registers the opt-in connection history (issue #43).</summary>
public static class PostgresConnectionHistoryServiceCollectionExtensions
{
    /// <summary>
    /// Samples every server the studio reads - its client connections, by application - every
    /// <see cref="PostgresConnectionHistoryOptions.Interval" />, over one connection per server kept open from the application's own
    /// data source, into the <see cref="IPostgresConnectionHistoryStore" /> the container holds: the host's own, registered before
    /// or after this call, or this process's memory. Database › Sessions › Holders then shows the peaks beside what holds the server's
    /// connections now. Needs <c>AddPostgresDiagnostics()</c>, <c>AddPostgresStudio()</c> or <c>AddMartenStudio()</c>, whose sources it
    /// samples; it adds one hosted service, and nothing else changes.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">The interval, retention, the applications kept, and whether this process samples.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddPostgresConnectionHistory(
        this IServiceCollection services,
        Action<PostgresConnectionHistoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        RefuseTheOther(services, readerOnly: false);

        OptionsBuilder<PostgresConnectionHistoryOptions> options = services.AddOptions<PostgresConnectionHistoryOptions>()
            .Validate(static x => x.Interval >= TimeSpan.FromSeconds(15) && x.Interval <= TimeSpan.FromHours(1),
                "PostgresConnectionHistoryOptions.Interval must be between fifteen seconds and one hour")
            .Validate(static x => x.Retention >= TimeSpan.FromHours(1) && x.Retention <= TimeSpan.FromDays(90),
                "PostgresConnectionHistoryOptions.Retention must be between one hour and ninety days")
            .Validate(static x => x.MaxApplications is >= 1 and <= 100,
                "PostgresConnectionHistoryOptions.MaxApplications must be between 1 and 100");

        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();

        Mark(services, readerOnly: false);
        services.TryAddSingleton<IPostgresConnectionHistoryStore, InMemoryConnectionHistoryStore>();
        services.TryAddSingleton<ConnectionHistoryReader>();
        services.TryAddSingleton(static provider =>
            provider.GetService<PostgresStudio.Services.Settings.IDiagnosticsSettings>() is null
                ? throw new InvalidOperationException(
                    "AddPostgresConnectionHistory() samples the servers of the data sources a studio or the diagnostics engine reads: "
                    + "register AddPostgresStudio(), AddMartenStudio() or AddPostgresDiagnostics() as well.")
                : ActivatorUtilities.CreateInstance<ConnectionHistorySampler>(provider));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConnectionHistorySampler>(static provider => provider.GetRequiredService<ConnectionHistorySampler>()));

        return services;
    }

    /// <summary>
    /// Reads the connection history of the servers a studio or the diagnostics engine reads, from an
    /// <see cref="IPostgresConnectionHistoryStore" /> another process fills: a worker that samples with
    /// <see cref="AddPostgresConnectionHistory" />, or a process with a pool reserved for it. Database › Sessions › Holders shows the
    /// peaks, and the diagnostics snapshot its connection history; this process samples nothing, opens no connection of its own and
    /// runs no background service, and only the <see cref="PostgresConnectionHistoryOptions.Retention" /> window is read from the
    /// options (the interval, the applications kept and <see cref="PostgresConnectionHistoryOptions.ShouldSample" /> are the sampler's).
    /// The host must register that store - before or after this call - and refuses to start without it, as without a studio or the
    /// engine. It cannot be combined with <see cref="AddPostgresConnectionHistory" /> in one container.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">The retention: how far back Holders reads.</param>
    /// <returns>The same collection.</returns>
    /// <exception cref="InvalidOperationException"><see cref="AddPostgresConnectionHistory" /> is registered already.</exception>
    public static IServiceCollection AddPostgresConnectionHistoryReader(
        this IServiceCollection services,
        Action<PostgresConnectionHistoryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        RefuseTheOther(services, readerOnly: true);

        OptionsBuilder<PostgresConnectionHistoryOptions> options = services.AddOptions<PostgresConnectionHistoryOptions>()
            .Validate(static x => x.Retention >= TimeSpan.FromHours(1) && x.Retention <= TimeSpan.FromDays(90),
                "PostgresConnectionHistoryOptions.Retention must be between one hour and ninety days");

        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();

        Mark(services, readerOnly: true);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PostgresConnectionHistoryOptions>, ConnectionHistoryReaderStartCheck>());
        services.TryAddSingleton<ConnectionHistoryReader>();

        return services;
    }

    private static void Mark(IServiceCollection services, bool readerOnly)
    {
        if (!Holds(services, readerOnly))
        {
            services.AddSingleton(new ConnectionHistoryRegistration(readerOnly));
        }
    }

    private static bool Holds(IServiceCollection services, bool readerOnly) =>
        services.Any(x => x.ServiceType == typeof(ConnectionHistoryRegistration)
            && x.ImplementationInstance is ConnectionHistoryRegistration registration
            && registration.ReaderOnly == readerOnly);

    private static void RefuseTheOther(IServiceCollection services, bool readerOnly)
    {
        if (Holds(services, !readerOnly))
        {
            throw new InvalidOperationException(
                "AddPostgresConnectionHistory() and AddPostgresConnectionHistoryReader() are exclusive: a process either samples (and reads) or only reads.");
        }
    }
}
