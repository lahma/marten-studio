using PostgresStudio.Components;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Maintenance;

/// <summary>Operations › Maintenance's address (W5-V1), for the pages that point at it - Storage's largest tables (W5-CAP).</summary>
internal static class MaintenanceLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "maintenance";

    /// <summary>
    /// The page, with the scope carried (D9) under the studio's own source parameter - or <see langword="null" /> when the
    /// studio's sidebar draws no Maintenance entry for these settings, so a link never leads to a page the studio does not offer.
    /// </summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio's descriptor: its navigation and its source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    public static string? ToMaintenance(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);

        bool offered = descriptor.Navigation
            .SelectMany(static x => x.Items)
            .Any(x => string.Equals(x.Route, Route, StringComparison.Ordinal) && (x.IsShown?.Invoke(settings) ?? true));

        return offered ? StudioScopeQuery.Append(StudioLink.To(settings, Route), scope, descriptor.SourceQueryParameter) : null;
    }
}
