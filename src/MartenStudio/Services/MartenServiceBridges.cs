namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's spellings of shared members whose shared form no longer names a MartenStudio type - kept as C# 14
/// static extension members so that Marten-only code, and the shared-bound files whose own packet has not moved them yet,
/// compile unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bridge, not an API.</b> Each member forwards to the shared one with MartenStudio's own names. Marten-only code may
/// keep calling these. A file bound for the shared assembly must not: after the move it cannot see this class, and a
/// text fence cannot see an extension call either - it reads like a call on the shared type. So each member lists the
/// shared-bound callers left when PS-1a landed; the packet that owns each caller moves it onto the shared member, and
/// PS-1e's fence over the whole manifest plus PS-2's compile are what prove none is left.
/// </para>
/// </remarks>
internal static class MartenServiceBridges
{
    extension(StudioCapabilityGuard)
    {
        /// <summary>
        /// <c>MartenStudioOptions.Capabilities.X</c>. Shared code asks <see cref="StudioCapabilityGuard.OptionFor" />,
        /// which reads the studio's settings. No shared-bound caller is left: PS-1c moved <c>HealthService</c> and PS-1d
        /// <c>QueryService</c> to <c>OptionFor</c>.
        /// </summary>
        /// <param name="capability">The capability.</param>
        public static string OptionName(StudioCapability capability) => MartenStudioSettings.CapabilityOption(capability);

        /// <summary>
        /// The <see cref="MartenStudioCapabilities" /> flag behind <paramref name="capability" />
        /// (<see cref="MartenStudioSettings.IsConfigured(MartenStudioCapabilities, StudioCapability)" />).
        /// </summary>
        /// <param name="capabilities">The host's flags.</param>
        /// <param name="capability">The capability.</param>
        internal static bool IsConfigured(MartenStudioCapabilities capabilities, StudioCapability capability) =>
            MartenStudioSettings.IsConfigured(capabilities, capability);
    }
}
