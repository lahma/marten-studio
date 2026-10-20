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
        /// callers: none left (PS-1d moved <c>Query.razor</c> onto the settings). (<c>DocumentLinks</c> is MartenStudio's:
        /// its shared half, <c>WithScope</c>, is <see cref="StudioScopeQuery.Append(string, StudioScope?, string)" />.)
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
        /// MartenStudio's options. No shared-bound caller is left (PS-1b moved <c>SafeReturnLink</c> onto the settings).
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
        /// No shared-bound caller is left (PS-1b moved <c>DatabaseLinks</c> onto the descriptor's parameter).
        /// </summary>
        internal static string StoreKeyParameter => MartenStudioDescriptor.SourceQueryParameter;

        /// <summary><see cref="StudioScopeQuery.Read(string, string)" /> with MartenStudio's source parameter.</summary>
        /// <param name="uri">The URI to read.</param>
        internal static (string? StoreKey, string? DatabaseId, string? TenantId) Read(string uri) =>
            StudioScopeQuery.Read(uri, MartenStudioDescriptor.SourceQueryParameter);
    }
}
