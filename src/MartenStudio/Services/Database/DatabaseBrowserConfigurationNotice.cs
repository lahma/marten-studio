using MartenStudio.Internal;
using MartenStudio.Services.Settings;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Services.Database;

/// <summary>
/// Says out loud, once when the host starts, that <c>BrowsableSchemas</c> contains <c>"*"</c> and no
/// <c>SqlConsoleRole</c> narrows it (event 9230) - for a host that mapped the studio, and for no other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per host start, at startup.</b> A hosted service rather than a first-use check, because the
/// startup log is where an operator looks for configuration warnings, and "the first time somebody opened
/// the database browser" is a moment nobody is watching. It runs in <see cref="StartedAsync" />, logs at
/// most once for the lifetime of this instance, and does nothing else.
/// </para>
/// <para>
/// <b>It must not change what a host does at startup.</b> A host that registers the studio and never maps it
/// is, by the project's first rule, the application it was: so nothing here happens unless a studio
/// endpoint exists. That is checked in <see cref="StartedAsync" /> rather than earlier, because a
/// <c>MapGroup</c> or a <c>Startup.Configure</c> finishes its endpoints only while the web host starts - the
/// same reason the startup guard checks again there. Only then are the settings read - the studio's live
/// adapter over its options, which builds them and runs their validation on first read; and whatever that throws - a validation failure, or a configure callback of the
/// host's own that fails - is swallowed and left to surface exactly where it did before, on first use.
/// Cancellation is the one exception that is not this service's to swallow.
/// </para>
/// </remarks>
internal sealed class DatabaseBrowserConfigurationNotice : IHostedLifecycleService
{
    private readonly IStudioSettings settings;
    private readonly ILogger<DatabaseBrowserConfigurationNotice> logger;
    private readonly MartenStudioMappedEndpoints? mappedEndpoints;
    private readonly EndpointDataSource? containerEndpoints;
    private int logged;

    public DatabaseBrowserConfigurationNotice(
        IStudioSettings settings,
        ILogger<DatabaseBrowserConfigurationNotice> logger,
        MartenStudioMappedEndpoints? mappedEndpoints = null,
        EndpointDataSource? containerEndpoints = null)
    {
        this.settings = settings;
        this.logger = logger;
        this.mappedEndpoints = mappedEndpoints;
        this.containerEndpoints = containerEndpoints;
    }

    /// <summary>Whether the configuration is the one worth warning about.</summary>
    internal static bool ShouldWarn(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return BrowsableSchemaMatcher.IncludesEverything(settings.BrowsableSchemas)
            && string.IsNullOrWhiteSpace(settings.SqlConsoleRole);
    }

    /// <inheritdoc />
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsStudioMapped())
            {
                // Registered and never mapped: the host is exactly the application it was, options unbuilt.
                return Task.CompletedTask;
            }

            if (ShouldWarn(settings) && Interlocked.Exchange(ref logged, 1) == 0)
            {
                logger.DatabaseBrowserOpenToEverySchemaWithoutRole();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Not this service's failure to report, and not at this moment: see the remarks.
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Whether any endpoint carries the studio's marker - on a route builder <c>MapMartenStudio</c> was
    /// called on, or, for a mapping onto a group, among the host's finished endpoints.
    /// </summary>
    private bool IsStudioMapped()
    {
        if (mappedEndpoints is not null && HasStudioEndpoint(mappedEndpoints.Endpoints()))
        {
            return true;
        }

        return containerEndpoints is not null && HasStudioEndpoint(containerEndpoints.Endpoints);
    }

    private static bool HasStudioEndpoint(IEnumerable<Endpoint> endpoints)
    {
        foreach (Endpoint endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<MartenStudioEndpointMarker>() is not null)
            {
                return true;
            }
        }

        return false;
    }
}
