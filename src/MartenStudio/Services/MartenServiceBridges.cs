namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's spellings of shared members whose shared form no longer names a MartenStudio type - kept as C# 14
/// static extension members so that Marten-only code compiles unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bridge, not an API.</b> Each member forwards to the shared one with MartenStudio's own names. Marten-only code may
/// keep calling these. A file bound for the shared assembly must not: after the move it cannot see this class, and a
/// text fence cannot see an extension call either - it reads like a call on the shared type. So each member lists its
/// callers, every one of them MartenStudio's own since PS-1e, which deleted every bridge member no code called any more
/// and moved the tests that were a member's last callers onto the member it forwards to. PS-2's compile is the proof
/// that no shared-bound caller is left: the shared assembly cannot see this class at all.
/// </para>
/// </remarks>
internal static class MartenServiceBridges
{
    extension(StudioCapabilityGuard)
    {
        /// <summary>
        /// <c>MartenStudioOptions.Capabilities.X</c>. Shared code asks <see cref="StudioCapabilityGuard.OptionFor" />,
        /// which reads the studio's settings. Callers, all MartenStudio's own: <c>DocumentWriteActions.razor</c>,
        /// <c>Projections.razor</c>, <c>ProjectionDataService.Health</c> and <c>LinqQueryService</c>.
        /// </summary>
        /// <param name="capability">The capability.</param>
        public static string OptionName(StudioCapability capability) => MartenStudioSettings.CapabilityOption(capability);
    }
}
