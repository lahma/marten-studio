using DataStudio.Marten.Services;
using DataStudio.Marten.Services.Configuration;
using DataStudio.Postgres.Services.Health;

namespace DataStudio.Marten.Integration.Tests.Health;

/// <summary>
/// The Marten module's health-check registry exactly as a host's container builds it: every check in the descriptor's
/// assemblies, with the source kinds each runs against, and every <c>RequiresFeature</c> resolved against the Marten this
/// process runs - the fast suite's <c>StudioHealthChecks</c>, which this project cannot reference.
/// </summary>
internal static class StudioHealthChecks
{
    /// <summary>The registry.</summary>
    public static HealthCheckRegistry Registry { get; } = new(MartenDescriptor.Instance, MartenHealthRuntime.Current);
}
