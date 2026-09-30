using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// The capabilities - the mutating operations and the two reads beyond the store - as an enum, so a
/// service method can name the one it is about and the guard can answer for it. Mirrors
/// <see cref="MartenStudioCapabilities" /> property for property.
/// </summary>
/// <remarks>
/// New members go at the end: the numeric values are not persisted anywhere, but the declaration order is
/// the order the capability chip lists them in.
/// </remarks>
internal enum StudioCapability
{
    /// <inheritdoc cref="MartenStudioCapabilities.EditDocuments" />
    EditDocuments,

    /// <inheritdoc cref="MartenStudioCapabilities.DeleteDocuments" />
    DeleteDocuments,

    /// <inheritdoc cref="MartenStudioCapabilities.ArchiveStreams" />
    ArchiveStreams,

    /// <inheritdoc cref="MartenStudioCapabilities.ManageDeadLetters" />
    ManageDeadLetters,

    /// <inheritdoc cref="MartenStudioCapabilities.ControlDaemon" />
    ControlDaemon,

    /// <inheritdoc cref="MartenStudioCapabilities.RebuildProjections" />
    RebuildProjections,

    /// <inheritdoc cref="MartenStudioCapabilities.CorrectProgression" />
    CorrectProgression,

    /// <inheritdoc cref="MartenStudioCapabilities.ApplySchemaChanges" />
    ApplySchemaChanges,

    /// <inheritdoc cref="MartenStudioCapabilities.RunSql" />
    RunSql,

    /// <inheritdoc cref="MartenStudioCapabilities.BrowseDatabase" />
    BrowseDatabase
}

/// <summary>Why a capability was refused.</summary>
internal enum CapabilityDenialReason
{
    /// <summary><see cref="MartenStudioOptions.ReadOnly" /> is on, so every capability is off.</summary>
    ReadOnly,

    /// <summary>The individual capability is off.</summary>
    Disabled
}

/// <summary>
/// What a mutating service method throws when the capability it needs is not enabled.
/// </summary>
/// <remarks>
/// The message names the exact option a host would have to set, because "not permitted" with nothing to
/// act on is the failure mode of every feature-flagged admin UI. Thrown by the service, not by the
/// component: hiding a button is a convenience, and a Blazor circuit is a long-lived object a client can
/// drive (AGENTS.md hard rule 5).
/// </remarks>
internal sealed class StudioCapabilityDeniedException : InvalidOperationException
{
    public StudioCapabilityDeniedException(StudioCapability capability, CapabilityDenialReason reason)
        : base(BuildMessage(capability, reason))
    {
        Capability = capability;
        Reason = reason;
    }

    /// <summary>The capability that was refused.</summary>
    public StudioCapability Capability { get; }

    /// <summary>Whether the master switch or the individual capability refused it.</summary>
    public CapabilityDenialReason Reason { get; }

    private static string BuildMessage(StudioCapability capability, CapabilityDenialReason reason)
    {
        return reason == CapabilityDenialReason.ReadOnly
            ? $"Marten Studio refused '{capability}' because MartenStudioOptions.ReadOnly is true, which turns every capability off."
            : $"Marten Studio refused '{capability}' because {StudioCapabilityGuard.OptionName(capability)} is false.";
    }
}

/// <summary>
/// Answers whether one mutating operation is enabled at all in this process.
/// </summary>
/// <remarks>
/// Singleton, and deliberately the simplest thing in the codebase: <c>!ReadOnly &amp;&amp; Capabilities.X</c>.
/// It is process-wide and knows nothing about the visitor - who may do what is
/// <see cref="StudioAuthorization" />'s question, and the two are asked one after the other so that
/// neither can be mistaken for the other.
/// </remarks>
internal sealed class StudioCapabilityGuard
{
    private readonly IOptions<MartenStudioOptions> options;

    public StudioCapabilityGuard(IOptions<MartenStudioOptions> options)
    {
        this.options = options;
    }

    /// <summary>Every capability, in declaration order - what the capability chip counts and lists.</summary>
    public static IReadOnlyList<StudioCapability> All { get; } = Enum.GetValues<StudioCapability>();

    /// <summary>
    /// The capabilities that change nothing but read beyond the store's own documents and events - what
    /// the capability chip lists under "Reads beyond the store" rather than under "Mutating operations".
    /// </summary>
    /// <remarks>
    /// Both are reads treated as writes for authorization (D13): the service asks
    /// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> with the capability named, and
    /// <see cref="MartenStudioOptions.ReadOnly" /> turns them off with everything else. A page that told a
    /// host "every mutating operation is off" when the console and the database browser were off too would
    /// be describing a narrower switch than the one they set.
    /// </remarks>
    public static IReadOnlyList<StudioCapability> ReadsBeyondTheStore { get; } =
        [StudioCapability.RunSql, StudioCapability.BrowseDatabase];

    /// <summary>
    /// Every capability that changes something - <see cref="All" /> without
    /// <see cref="ReadsBeyondTheStore" />, in declaration order.
    /// </summary>
    public static IReadOnlyList<StudioCapability> Mutating { get; } =
        [.. All.Where(static x => !ReadsBeyondTheStore.Contains(x))];

    /// <summary>Whether the master switch is on, which turns every capability off.</summary>
    public bool ReadOnly => options.Value.ReadOnly;

    /// <summary>How many capabilities are effectively enabled.</summary>
    public int EnabledCount
    {
        get
        {
            int count = 0;
            foreach (StudioCapability capability in All)
            {
                if (IsEnabled(capability))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether <paramref name="capability" /> is enabled, master switch included.</summary>
    public bool IsEnabled(StudioCapability capability)
    {
        MartenStudioOptions value = options.Value;
        return !value.ReadOnly && IsConfigured(value.Capabilities, capability);
    }

    /// <summary>
    /// Throws unless <paramref name="capability" /> is enabled. The first line of every mutating service
    /// method.
    /// </summary>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    public void Require(StudioCapability capability)
    {
        MartenStudioOptions value = options.Value;
        if (value.ReadOnly)
        {
            throw new StudioCapabilityDeniedException(capability, CapabilityDenialReason.ReadOnly);
        }

        if (!IsConfigured(value.Capabilities, capability))
        {
            throw new StudioCapabilityDeniedException(capability, CapabilityDenialReason.Disabled);
        }
    }

    /// <summary>
    /// The property a host would set to enable <paramref name="capability" />, spelled the way it is
    /// written in the host's own registration - which is what a disabled control says out loud.
    /// </summary>
    public static string OptionName(StudioCapability capability) =>
        "MartenStudioOptions.Capabilities." + capability;

    /// <summary>
    /// The property behind <paramref name="capability" />.
    /// </summary>
    /// <remarks>
    /// The default arm throws rather than answering <see langword="false" />. A <c>_ =&gt; false</c> arm is
    /// how a capability added to the enum and forgotten here becomes a switch a host sets to
    /// <see langword="true" /> and the studio silently goes on refusing: the page names the option, the
    /// host sets it, and nothing changes. <c>StudioCapabilityGuardTests</c> walks <see cref="All" /> so the
    /// omission fails a test rather than a support ticket.
    /// </remarks>
    internal static bool IsConfigured(MartenStudioCapabilities capabilities, StudioCapability capability) =>
        capability switch
        {
            StudioCapability.EditDocuments => capabilities.EditDocuments,
            StudioCapability.DeleteDocuments => capabilities.DeleteDocuments,
            StudioCapability.ArchiveStreams => capabilities.ArchiveStreams,
            StudioCapability.ManageDeadLetters => capabilities.ManageDeadLetters,
            StudioCapability.ControlDaemon => capabilities.ControlDaemon,
            StudioCapability.RebuildProjections => capabilities.RebuildProjections,
            StudioCapability.CorrectProgression => capabilities.CorrectProgression,
            StudioCapability.ApplySchemaChanges => capabilities.ApplySchemaChanges,
            StudioCapability.RunSql => capabilities.RunSql,
            StudioCapability.BrowseDatabase => capabilities.BrowseDatabase,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "No MartenStudioCapabilities property is mapped to this capability."),
        };
}
