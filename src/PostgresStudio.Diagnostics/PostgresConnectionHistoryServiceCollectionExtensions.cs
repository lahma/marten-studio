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
}
