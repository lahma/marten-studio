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
    private readonly IReadOnlyList<StudioNavSection> navigation = Rail(options);

    /// <inheritdoc />
    public string Key => KafkaStudioText.Key;

    /// <inheritdoc />
    public string ProductName => KafkaStudioText.ProductName;

    /// <inheritdoc />
    public StudioPageAssembly Pages => pages;

    /// <inheritdoc />
    public IReadOnlyList<StudioNavSection> Navigation => navigation;

    /// <summary>
    /// The rail's one section (D-UI decision 6): Topics, Search, Live tail and Cluster settings. Search is drawn only while the capability is effectively on -
    /// read live from the options on every render, and failing closed under the host studio's <c>ReadOnly</c> - so an entry never promises a page that
    /// would only say it is off. Live tail is drawn whatever the capability (D-M5 decision 3.7): its page says which option turns it on, and why a
    /// <c>Production</c> cluster has none. The topic overview and the record page have no entry: links reach them.
    /// </summary>
    private static IReadOnlyList<StudioNavSection> Rail(IOptions<KafkaStudioOptions> options) =>
    [
        new StudioNavSection(
            "Kafka",
            [
                new StudioNavItem("Topics", "kafka", "topics"),
                new StudioNavItem("Search", "kafka/search", "search")
                {
                    IsShown = settings => !settings.ReadOnly && options.Value.Capabilities.SearchRecords,
                },
                new StudioNavItem("Live tail", "kafka/tail", "pulse")
                {
                    Title = "Follow topics as records arrive",
                },
                new StudioNavItem("Cluster settings", "kafka/settings", "cog"),
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
