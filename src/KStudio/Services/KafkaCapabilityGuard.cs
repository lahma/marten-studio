using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace KStudio.Services;

/// <summary>
/// Whether a Kafka capability is on (D35): the host studio's master switch is off and the host switched the flag on. KStudio has no
/// <c>ReadOnly</c> of its own - the host studio's turns every capability off, here and in the chip - so there is one master switch and the
/// chip's "every capability is off" stays true.
/// </summary>
/// <param name="options">KStudio's options.</param>
/// <param name="settings">The host studio's settings, whose <c>ReadOnly</c> is the master switch.</param>
internal sealed class KafkaCapabilityGuard(IOptions<KafkaStudioOptions> options, IStudioSettings settings)
{
    /// <summary>Every capability KStudio offers.</summary>
    public static IReadOnlyList<KafkaCapability> Offered { get; } = Enum.GetValues<KafkaCapability>();

    /// <summary>Whether the host studio's master switch is on, which turns every capability off.</summary>
    public bool ReadOnly => settings.ReadOnly;

    /// <summary>The capabilities the host switched on, without the master switch.</summary>
    public IReadOnlyList<KafkaCapability> Configured => ConfiguredIn(options.Value.Capabilities);

    /// <summary>Whether the host switched <paramref name="capability" /> on, without the master switch.</summary>
    /// <param name="capability">The capability.</param>
    public bool IsConfigured(KafkaCapability capability) => IsConfigured(options.Value.Capabilities, capability);

    /// <summary>Whether <paramref name="capability" /> is effectively on: the master switch off and the flag set.</summary>
    /// <param name="capability">The capability.</param>
    public bool IsEnabled(KafkaCapability capability) => !settings.ReadOnly && IsConfigured(capability);

    /// <summary>The option that switches <paramref name="capability" /> on, as a host writes it.</summary>
    /// <param name="capability">The capability.</param>
    public static string OptionFor(KafkaCapability capability) =>
        nameof(KafkaStudioOptions) + "." + nameof(KafkaStudioOptions.Capabilities) + "." + capability;

    /// <summary>The capabilities <paramref name="capability" /> needs on as well: <c>SearchRecords</c> for each of the other three.</summary>
    /// <param name="capability">The capability.</param>
    public static IReadOnlyList<KafkaCapability> Prerequisites(KafkaCapability capability) => capability switch
    {
        KafkaCapability.SearchRecords => [],
        _ => [KafkaCapability.SearchRecords],
    };

    /// <summary>
    /// Throws unless <paramref name="capability" /> and each of its <see cref="Prerequisites" /> is on, the prerequisites first, so the refusal names the
    /// first missing option; it names the option a host would have to set.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <exception cref="KafkaCapabilityDeniedException">The capability, or one it needs, is off.</exception>
    public void Require(KafkaCapability capability)
    {
        foreach (KafkaCapability prerequisite in Prerequisites(capability))
        {
            RequireOne(prerequisite);
        }

        RequireOne(capability);
    }

    /// <summary>
    /// Throws unless <paramref name="capability" /> alone is on: for a caller that has already passed the gate of its prerequisites (a search that went
    /// through <c>SearchRecords</c> asking whether it may read values), so that the refusal names the capability that is missing.
    /// </summary>
    /// <param name="capability">The capability.</param>
    /// <exception cref="KafkaCapabilityDeniedException">The capability is off.</exception>
    public void RequireOne(KafkaCapability capability)
    {
        if (settings.ReadOnly)
        {
            throw new KafkaCapabilityDeniedException(capability, CapabilityDenialReason.ReadOnly, settings.ReadOnlyOption());
        }

        if (!IsConfigured(capability))
        {
            throw new KafkaCapabilityDeniedException(capability, CapabilityDenialReason.Disabled, settings.ReadOnlyOption());
        }
    }

    /// <summary>The capabilities <paramref name="capabilities" /> switched on.</summary>
    /// <param name="capabilities">The host's capabilities.</param>
    public static IReadOnlyList<KafkaCapability> ConfiguredIn(KafkaStudioCapabilities capabilities) =>
        [.. Offered.Where(capability => IsConfigured(capabilities, capability))];

    private static bool IsConfigured(KafkaStudioCapabilities capabilities, KafkaCapability capability) => capability switch
    {
        KafkaCapability.SearchRecords => capabilities.SearchRecords,
        KafkaCapability.ReadPayloads => capabilities.ReadPayloads,
        KafkaCapability.ExportRecords => capabilities.ExportRecords,
        KafkaCapability.LiveTail => capabilities.LiveTail,
        _ => false,
    };
}

/// <summary>
/// What a Kafka service method throws when the capability it needs is not enabled. The message names the exact option a host would have to
/// set. Thrown by the service, not by the component: hiding a button is a convenience (AGENTS.md hard rule 5).
/// </summary>
internal sealed class KafkaCapabilityDeniedException : InvalidOperationException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="capability">The capability that was refused.</param>
    /// <param name="reason">Whether the master switch or the individual capability refused it.</param>
    /// <param name="readOnlyOption">The host studio's master switch, spelled as its options spell it.</param>
    public KafkaCapabilityDeniedException(KafkaCapability capability, CapabilityDenialReason reason, string readOnlyOption)
        : base(reason == CapabilityDenialReason.ReadOnly
            ? $"KStudio refused '{capability}' because {readOnlyOption} is true, which turns every capability off."
            : $"KStudio refused '{capability}' because {KafkaCapabilityGuard.OptionFor(capability)} is false.")
    {
        Capability = capability;
        Reason = reason;
        CapabilityOption = KafkaCapabilityGuard.OptionFor(capability);
        VisitorText = KafkaStudioText.CapabilityNotAvailable(capability);
    }

    /// <summary>What a page shows a visitor: one neutral sentence with no option name (#74). <see cref="Exception.Message" /> is for the audit and the log.</summary>
    public string VisitorText { get; }

    /// <summary>The capability that was refused.</summary>
    public KafkaCapability Capability { get; }

    /// <summary>Whether the master switch or the individual capability refused it.</summary>
    public CapabilityDenialReason Reason { get; }

    /// <summary>The option a host would have to set, for the log.</summary>
    public string CapabilityOption { get; }
}
