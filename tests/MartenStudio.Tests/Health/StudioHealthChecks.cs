using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// MartenStudio's health-check registry exactly as a host's container builds it: every check in the descriptor's
/// assemblies (the shared engine's and MartenStudio's own), with the source kinds each runs against, and every
/// <c>RequiresFeature</c> resolved against the Marten this process runs.
/// </summary>
/// <remarks>
/// The one registry a test that wants "the studio's checks" reads. The parameterless constructor it replaces resolved
/// features against no runtime - so it would have thrown the day a check required one - and after the move would have
/// found the shared assembly's checks alone.
/// </remarks>
internal static class StudioHealthChecks
{
    /// <summary>The registry.</summary>
    public static HealthCheckRegistry Registry { get; } = new(MartenStudioDescriptor.Instance, MartenStudioRuntime.Current);
}
