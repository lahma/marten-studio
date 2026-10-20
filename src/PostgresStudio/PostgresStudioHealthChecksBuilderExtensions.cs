using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using PostgresStudio.Services.Health.Export;

namespace PostgresStudio;

/// <summary>
/// Adds Postgres Studio's health advisor to the host's own ASP.NET Core health checks.
/// </summary>
public static class PostgresStudioHealthChecksBuilderExtensions
{
    /// <summary>
    /// Registers one health check that runs the advisor's configuration and catalog checks over every database the
    /// studio would list, and reports what they found as codes and counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and nothing else registers it.</b> <c>AddPostgresStudio()</c> does not call this, and a host that
    /// never does is exactly the application it was: no health check, no hosted service, no timer. Once registered, the
    /// check runs only when the host's health endpoint is probed. <c>services.AddPostgresStudio()</c> must be called as
    /// well; without it the check reports <see cref="HealthStatus.Degraded" /> and says so.
    /// </para>
    /// <para>
    /// <b>Status.</b> A Critical finding reports <paramref name="failureStatus" /> (<see cref="HealthStatus.Unhealthy" />
    /// when it is <see langword="null" />). A Warning, a check that could not run or ran out of time, or a source that
    /// could not be reached reports <see cref="HealthStatus.Degraded" />, never worse than
    /// <paramref name="failureStatus" />. Info findings, and checks that cannot run here for a reason that is the host's
    /// choice (no privilege, no extension, an old Postgres), leave it <see cref="HealthStatus.Healthy" />.
    /// </para>
    /// <para>
    /// <b>Data is codes and counts, never names.</b> The report's data lists the codes found at each severity, the codes
    /// that could not run, and how many databases were checked. It never carries a database, a session, a role, a table,
    /// a statement or anything Postgres said, because there is no visitor to authorize: whoever can reach the endpoint
    /// can read it.
    /// </para>
    /// <para>
    /// <b>Cost.</b> Concurrent probes share one run, and once a run has completed every probe is answered with the
    /// latest result at once; a probe that finds it older than <c>PostgresStudioOptions.HealthInterval</c> starts the
    /// next run for the probes after it, and a result two intervals old is not served. A run checks at most 25
    /// databases, in the order the studio lists them: the data sources first, then each host's database source's
    /// databases as it lists them.
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
    /// <see langword="null" /> for no limit of the check's own. Giving up cancels that probe's wait and never the run.
    /// </param>
    /// <returns>The same builder, so calls can be chained.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="name" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty or whitespace.</exception>
    public static IHealthChecksBuilder AddPostgresStudio(
        this IHealthChecksBuilder builder,
        string name = "postgres-studio",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // One runner per process however many registrations name it, so a liveness and a readiness check over the same
        // studio still cost one run per interval between them.
        builder.Services.TryAddSingleton<HealthExportRunner>();

        return builder.Add(new HealthCheckRegistration(
            name,
            static services => new StudioHealthCheck(services.GetRequiredService<HealthExportRunner>()),
            failureStatus,
            tags,
            timeout));
    }
}
