using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services.Database;

/// <summary>
/// Says out loud, once when the host starts, that <c>BrowsableSchemas</c> contains <c>"*"</c> and no
/// <c>SqlConsoleRole</c> narrows it (event 9230).
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per host start, at startup.</b> A hosted service rather than a first-use check, because the
/// startup log is where an operator looks for configuration warnings, and "the first time somebody opened
/// the database browser" is a moment nobody is watching. It runs in <see cref="StartAsync" />, logs at most
/// once for the lifetime of this instance, and does nothing else.
/// </para>
/// <para>
/// <b>It must not change what a host does at startup.</b> Reading <see cref="IOptions{TOptions}.Value" /> runs
/// the options validation, and a host whose studio options are invalid has always started and failed on
/// first use - so a validation failure here is swallowed and left to surface exactly where it did before.
/// </para>
/// </remarks>
internal sealed class DatabaseBrowserConfigurationNotice : IHostedService
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<DatabaseBrowserConfigurationNotice> logger;
    private int logged;

    public DatabaseBrowserConfigurationNotice(
        IOptions<MartenStudioOptions> options,
        ILogger<DatabaseBrowserConfigurationNotice> logger)
    {
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Whether the configuration is the one worth warning about.</summary>
    internal static bool ShouldWarn(MartenStudioOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return BrowsableSchemaMatcher.IncludesEverything(value.BrowsableSchemas)
            && string.IsNullOrWhiteSpace(value.SqlConsoleRole);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        MartenStudioOptions value;

        try
        {
            value = options.Value;
        }
        catch (OptionsValidationException)
        {
            // Not this service's failure to report, and not at this moment: see the remarks.
            return Task.CompletedTask;
        }

        if (ShouldWarn(value) && Interlocked.Exchange(ref logged, 1) == 0)
        {
            logger.DatabaseBrowserOpenToEverySchemaWithoutRole();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
