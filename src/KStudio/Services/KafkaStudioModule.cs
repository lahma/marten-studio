using KStudio.Components.Layout;

using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace KStudio.Services;

/// <summary>
/// KStudio as a module of the studio the host registered (D34): its pages, its rail section, its cluster selector, its words and its
/// capabilities, composed into the shell's descriptor when it is first resolved. Everything here is pure and reads only KStudio's own options,
/// read live on every call so a rail entry or the chip follows the options exactly.
/// </summary>
/// <param name="options">KStudio's options.</param>
internal sealed class KafkaStudioModule(IOptions<KafkaStudioOptions> options) : IStudioModule
{
    /// <summary>The compile-time root every Kafka page's <c>@page</c> starts with.</summary>
    internal const string PageRoot = "/kafka";

    private readonly StudioPageAssembly pages = new(typeof(KafkaStudioModule).Assembly, PageRoot) { KeepsRoot = true };

    /// <inheritdoc />
    public string Key => KafkaStudioText.Key;

    /// <inheritdoc />
    public string ProductName => KafkaStudioText.ProductName;

    /// <inheritdoc />
    public StudioPageAssembly Pages => pages;

    /// <inheritdoc />
    public IReadOnlyList<StudioNavSection> Navigation { get; } =
    [
        new StudioNavSection(
            "Kafka",
            [
                new StudioNavItem("Clusters", "kafka", "broker"),
            ]),
    ];

    /// <inheritdoc />
    public Type ScopeSelector => typeof(ClusterSelector);

    /// <inheritdoc />
    public string ExposedSentence => KafkaStudioText.ExposedSentence;

    /// <inheritdoc />
    public string CapabilitiesHeading => KafkaStudioText.CapabilitiesHeading;

    /// <inheritdoc />
    public IReadOnlyList<StudioModuleCapability> Capabilities =>
    [
        .. KafkaCapabilityGuard.Offered.Select(capability => new StudioModuleCapability(
            capability.ToString(),
            KafkaCapabilityGuard.OptionFor(capability),
            KafkaCapabilityGuard.ConfiguredIn(options.Value.Capabilities).Contains(capability))),
    ];

    /// <inheritdoc />
    public IReadOnlyList<StudioShortcutGroup> Shortcuts { get; } = [];
}
