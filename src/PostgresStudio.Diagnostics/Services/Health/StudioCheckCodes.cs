using System.Reflection;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health;

/// <summary>
/// The check codes a studio has, read from the same assemblies its registry discovers them in - without building the
/// registry, which runs at first use and needs a runtime: what options validation checks <c>AcceptedChecks</c> against.
/// </summary>
internal static class StudioCheckCodes
{
    /// <summary>
    /// Every code of <paramref name="descriptor" />'s checks, compared ignoring case. A studio's own registry only: Postgres
    /// Studio's never holds a Marten check's code.
    /// </summary>
    /// <param name="descriptor">The studio.</param>
    public static IReadOnlySet<string> Of(DiagnosticsDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return HealthCheckRegistry.DiscoverAll(descriptor.HealthCheckAssemblies)
            .Select(static x => x.Type.GetCustomAttribute<HealthCheckAttribute>()?.Code)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
