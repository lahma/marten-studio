using Microsoft.Extensions.Diagnostics.HealthChecks;

using AspNetCoreHealthCheck = Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck;
using AspNetCoreHealthCheckContext = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext;
using AspNetCoreHealthCheckResult = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult;

namespace PostgresStudio.Services.Health.Export;

/// <summary>
/// The one ASP.NET Core health check <c>AddHealthChecks().AddMartenStudio()</c> registers: the export run's
/// summary, as a status, a sentence and codes-and-counts data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Aliased on purpose.</b> The advisor's own <c>IHealthCheck</c>, <c>HealthCheckContext</c> and
/// <c>HealthCheckResult</c> live in the enclosing namespace and win every unqualified lookup here, so this file
/// names ASP.NET Core's three only through aliases. This class is not one of the advisor's checks - the
/// registry finds those by the advisor's interface, which this does not implement.
/// </para>
/// <para>
/// <b>Never throws but for the caller's cancellation.</b> A probe whose own token fires gets
/// <see cref="OperationCanceledException" />, which <c>HealthCheckService</c> reports the way it reports every
/// check that timed out; anything else becomes Degraded with a sentence that names the exception's type and
/// none of its words, because the endpoint has no visitor to show them to.
/// </para>
/// <para>
/// A new instance per probe, made by the registration's factory: the state that matters - the run in flight
/// and the last result - is <see cref="HealthExportRunner" />'s, a singleton.
/// </para>
/// </remarks>
internal sealed class StudioHealthCheck : AspNetCoreHealthCheck
{
    private readonly HealthExportRunner runner;

    /// <param name="runner">The process's one export runner.</param>
    public StudioHealthCheck(HealthExportRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        this.runner = runner;
    }

    /// <inheritdoc />
    public async Task<AspNetCoreHealthCheckResult> CheckHealthAsync(
        AspNetCoreHealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // HealthCheckService always sets the registration; a context built by hand may not.
        HealthStatus failureStatus = context.Registration?.FailureStatus ?? HealthStatus.Unhealthy;

        try
        {
            HealthExportSummary summary = await runner.GetAsync(cancellationToken).ConfigureAwait(false);

            return new AspNetCoreHealthCheckResult(
                StatusOf(summary.Level, failureStatus),
                description: summary.Describe(runner.Interval),
                exception: null,
                data: summary.ToData());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            runner.ReportFailure(exception);

            return new AspNetCoreHealthCheckResult(
                StatusOf(HealthExportLevel.Degraded, failureStatus),
                description: runner.Text.ProductName + " could not check: " + exception.GetType().Name + ", logged as event 9270.");
        }
    }

    /// <summary>
    /// The status a level reports: Critical is the caller's <paramref name="failureStatus" />, Degraded is
    /// <see cref="HealthStatus.Degraded" /> but never worse than <paramref name="failureStatus" />, and Healthy is
    /// <see cref="HealthStatus.Healthy" />.
    /// </summary>
    /// <remarks>
    /// "Never worse" because a caller that registered the check with <c>failureStatus: Degraded</c> - or
    /// <c>Healthy</c>, to watch it without paging anybody - has said how loud the worst case may be, and a
    /// Warning is not louder than a Critical.
    /// </remarks>
    internal static HealthStatus StatusOf(HealthExportLevel level, HealthStatus failureStatus) => level switch
    {
        HealthExportLevel.Critical => failureStatus,
        HealthExportLevel.Degraded => Healthier(HealthStatus.Degraded, failureStatus),
        _ => HealthStatus.Healthy,
    };

    /// <summary>The healthier of two statuses: the enum is ordered Unhealthy, Degraded, Healthy.</summary>
    private static HealthStatus Healthier(HealthStatus left, HealthStatus right) => left >= right ? left : right;
}
