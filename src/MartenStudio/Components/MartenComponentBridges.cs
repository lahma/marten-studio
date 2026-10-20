using MartenStudio.Services;

namespace MartenStudio.Components;

/// <summary>
/// MartenStudio's spellings of the shared link and query-string helpers, which now take the studio's settings and its
/// descriptor's source parameter - C# 14 static extension members, so that Marten-only code, and the shared-bound files
/// whose own packet has not moved them yet, compile unchanged. The rule for using them is
/// <see cref="MartenServiceBridges" />'s: Marten-only code may; a shared-bound file must move to the shared member.
/// </summary>
internal static class MartenComponentBridges
{
    extension(StudioLink)
    {
        /// <summary>
        /// <see cref="StudioLink.To(Services.Settings.IStudioSettings, string)" /> with MartenStudio's options. Shared-bound
        /// callers left at PS-1a: <c>DatabaseLinks</c>, <c>SessionActivityModels</c> and <c>DocumentLinks.WithScope</c>'s
        /// half (PS-1b), <c>Query.razor</c> (PS-1d).
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="subPath">The path below the studio root, with no leading slash.</param>
        internal static string To(MartenStudioOptions options, string subPath) =>
            StudioLink.To(MartenStudioSettings.Of(options), subPath);

        /// <summary>
        /// <see cref="StudioLink.ToDashboardRelativePath(string, string, Services.Settings.IStudioSettings)" /> with
        /// MartenStudio's options.
        /// </summary>
        /// <param name="uri">The absolute URI.</param>
        /// <param name="baseUri">The document's base URI.</param>
        /// <param name="options">MartenStudio's options.</param>
        internal static string? ToDashboardRelativePath(string uri, string baseUri, MartenStudioOptions options) =>
            StudioLink.ToDashboardRelativePath(uri, baseUri, MartenStudioSettings.Of(options));

        /// <summary>
        /// <see cref="StudioLink.TryStripDashboardPrefix(string, Services.Settings.IStudioSettings, out string)" /> with
        /// MartenStudio's options. Shared-bound caller left at PS-1a: <c>SafeReturnLink</c> (PS-1b).
        /// </summary>
        /// <param name="normalizedPath">An already-normalized base-relative path.</param>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="stripped">The leaf below the studio root.</param>
        internal static bool TryStripDashboardPrefix(string normalizedPath, MartenStudioOptions options, out string stripped) =>
            StudioLink.TryStripDashboardPrefix(normalizedPath, MartenStudioSettings.Of(options), out stripped);
    }

    extension(StudioScopeQuery)
    {
        /// <summary>
        /// MartenStudio's source parameter, <c>store</c> (<see cref="MartenStudioDescriptor.SourceQueryParameter" />).
        /// Shared-bound caller left at PS-1a: <c>DatabaseLinks</c> (PS-1b).
        /// </summary>
        internal static string StoreKeyParameter => MartenStudioDescriptor.SourceQueryParameter;

        /// <summary><see cref="StudioScopeQuery.Read(string, string)" /> with MartenStudio's source parameter.</summary>
        /// <param name="uri">The URI to read.</param>
        internal static (string? StoreKey, string? DatabaseId, string? TenantId) Read(string uri) =>
            StudioScopeQuery.Read(uri, MartenStudioDescriptor.SourceQueryParameter);
    }
}
