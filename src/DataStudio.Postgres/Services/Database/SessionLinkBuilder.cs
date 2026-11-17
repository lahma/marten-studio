using DataStudio.Components;
using DataStudio.Postgres.Components;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services.Database;

/// <summary>
/// Database › Sessions' address as a visitor's link: the route and the tab names are the engine's
/// (<see cref="SessionLinks" />), the link that carries the scope is the UI's.
/// </summary>
internal static class SessionLinkBuilder
{
    extension(SessionLinks)
    {
        /// <summary>The page, with the scope carried (D9) under the studio's own source parameter.</summary>
        /// <param name="descriptor">The studio's descriptor: its source parameter.</param>
        /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
        /// <param name="view">The tab - <see cref="SessionLinks.BlockingView" /> or <see cref="SessionLinks.HoldersView" /> - or <see langword="null" /> for the sessions themselves.</param>
        public static string ToSessions(StudioDescriptor descriptor, StudioScope? scope, string? view = null)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            string route = view is { Length: > 0 }
                ? SessionLinks.Route + "?" + SessionLinks.ViewParameter + "=" + Uri.EscapeDataString(view)
                : SessionLinks.Route;

            return StudioScopeQuery.Append(StudioLink.To(route), scope, descriptor.SourceQueryParameter);
        }
    }
}
