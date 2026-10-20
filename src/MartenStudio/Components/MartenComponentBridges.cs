using MartenStudio.Services;

namespace MartenStudio.Components;

/// <summary>
/// MartenStudio's spelling of the shared link helper, which now takes the studio's settings - a C# 14 static extension
/// member, so that Marten-only code compiles unchanged. The rule for using it is <see cref="MartenServiceBridges" />'s:
/// Marten-only code may; a shared-bound file must call the shared member.
/// </summary>
internal static class MartenComponentBridges
{
    extension(StudioLink)
    {
        /// <summary>
        /// <see cref="StudioLink.To(Services.Settings.IStudioSettings, string)" /> with MartenStudio's options. Callers, all
        /// MartenStudio's own (PS-1e): <c>DocumentLinks</c>, <c>EventLinks</c>, <c>LinqQueryPanel.razor</c> and
        /// <c>Schema.razor</c>.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="subPath">The path below the studio root, with no leading slash.</param>
        internal static string To(MartenStudioOptions options, string subPath) =>
            StudioLink.To(MartenStudioSettings.Of(options), subPath);
    }
}
