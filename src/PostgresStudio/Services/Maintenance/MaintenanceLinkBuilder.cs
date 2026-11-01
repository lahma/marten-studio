using PostgresStudio.Components;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Maintenance;

/// <summary>The Maintenance page's address as a visitor's link: the route is the engine's, the link is the UI's.</summary>
internal static class MaintenanceLinkBuilder
{
    extension(MaintenanceLinks)
    {
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
                .Any(x => string.Equals(x.Route, MaintenanceLinks.Route, StringComparison.Ordinal) && (x.IsShown?.Invoke(settings) ?? true));

            return offered ? StudioScopeQuery.Append(StudioLink.To(settings, MaintenanceLinks.Route), scope, descriptor.SourceQueryParameter) : null;
        }
    }
}
