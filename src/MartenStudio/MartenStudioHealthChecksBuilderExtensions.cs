using MartenStudio.Services.Health.Export;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MartenStudio;

/// <summary>
/// Adds Marten Studio's health advisor to the host's own ASP.NET Core health checks.
/// </summary>
public static class MartenStudioHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers one health check that runs the advisor's configuration and catalog checks over every
    /// Marten store and database the studio would list, and reports what they found as codes and counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and nothing else registers it.</b> <c>AddMartenStudio()</c> does not call this, and a host
    /// that never does is exactly the application it was: no health check, no hosted service, no timer.
    /// Once registered, the check runs only when the host's health endpoint is probed - there is still no
    /// background work of the studio's own. <c>services.AddMartenStudio()</c> must be called as well; without
    /// it the check reports <see cref="HealthStatus.Degraded" /> and says so.
    /// </para>
    /// <para>
    /// <b>Status.</b> A Critical finding reports <paramref name="failureStatus" />
    /// (<see cref="HealthStatus.Unhealthy" /> when it is <see langword="null" />). A Warning, a check that
    /// could not run or ran out of time, or a store that could not be reached reports
    /// <see cref="HealthStatus.Degraded" />, never worse than <paramref name="failureStatus" />. Info findings,
    /// and checks that cannot run here for a reason that is the host's choice (no privilege, no extension, an old
    /// Postgres), leave it <see cref="HealthStatus.Healthy" />.
    /// </para>
    /// <para>
    /// <b>Data is codes and counts, never names.</b> The report's data lists the codes found at each
    /// severity, the codes that could not run, and how many databases were checked. It never carries a
    /// tenant, a stream, a session, a role, a table, a statement or anything Postgres said, because there is
    /// no visitor to authorize: whoever can reach the endpoint can read it. A document type
    /// <c>IsDocumentTypeVisible</c> hides contributes nothing, not even to a count.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Concurrent probes share one run, and once a run has completed every probe is answered with
    /// the latest result at once; a probe that finds it older than <c>MartenStudioOptions.HealthInterval</c> (a
    /// minute by default) starts the next run for the probes after it, and a result two intervals old is not
    /// served - that probe waits for a fresh run, as the first one does. So an orchestrator probing every ten
    /// seconds costs one run per interval, not six. A run checks at most 25 databases, in the order the studio
    /// lists them.
    /// </para>
    /// <para>
    /// <b>Enumeration.</b> The export's own listing never enumerates a tenancy that creates or migrates databases
    /// when it is enumerated - master-table, sharded, single-server: only the databases this process already holds
    /// are checked, each read from the tenancy's own cache. With a projection coordinator hosted in this process,
    /// the daemon checks ask that coordinator for its daemons, and Marten's coordinator answers by enumerating the
    /// store - as its own leadership loop does on every cycle - so a probe can be the first enumerator only in the
    /// moment before that loop's first cycle. Under <c>MartenDaemonModeIsSolo()</c>, a testing coordinator with no
    /// loop, a probe is the enumerator.
    /// </para>
    /// </remarks>
    /// <param name="builder">The host's health checks, from <c>services.AddHealthChecks()</c>.</param>
    /// <param name="name">The health check's name, unique among the host's checks.</param>
    /// <param name="failureStatus">
    /// What a Critical finding reports, or <see langword="null" /> for <see cref="HealthStatus.Unhealthy" />.
    /// </param>
    /// <param name="tags">Tags a health endpoint can filter on - to put the check on a readiness probe only, say.</param>
    /// <param name="timeout">
    /// How long a probe waits for the check before ASP.NET Core reports it as having timed out, or
    /// <see langword="null" /> for no limit of the check's own. Only the process's first probe can wait for a run;
    /// giving up cancels that probe's wait and never the run.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="name" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    public static IHealthChecksBuilder AddMartenStudio(
        this IHealthChecksBuilder builder,
        string name = "marten-studio",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // One runner per process however many registrations name it, so a liveness and a readiness check over
        // the same studio still cost one run per interval between them.
        builder.Services.TryAddSingleton<HealthExportRunner>();

        return builder.Add(new HealthCheckRegistration(
            name,
            static services => new MartenStudioHealthCheck(services.GetRequiredService<HealthExportRunner>()),
            failureStatus,
            tags,
            timeout));
    }
}
