using DataStudio.Services.Settings;
using DataStudio.Services;
using DataStudio.Postgres.Components.Layout;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// The database as a module of the shell (D34): the Postgres module's pages or the Marten module's, built from the module's own
/// <see cref="StudioDescriptor" />, <see cref="IStudioSettings" /> and <see cref="StudioCapabilityGuard" />, and registered by
/// <see cref="StudioCoreRegistration" />. Its pages sit below its own segment under the mount (<c>postgres</c> or <c>marten</c>), like every module's.
/// </summary>
/// <remarks>
/// Pure, like every module: the rail is built once from the descriptor's factory (an entry whose condition needs the studio's settings closes
/// over them), and the capability groups are read live from the guard on every call, so the chip follows the host's options exactly.
/// </remarks>
internal sealed class DatabaseStudioModule : IStudioModule
{
    private readonly StudioDescriptor descriptor;
    private readonly IStudioSettings settings;
    private readonly StudioCapabilityGuard guard;
    private readonly IReadOnlyList<StudioNavSection> navigation;
    private readonly IReadOnlyList<StudioShortcutGroup> shortcuts;

    /// <summary>Creates the module for the studio registered in this container.</summary>
    /// <param name="descriptor">The studio's descriptor.</param>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="guard">The studio's capability guard.</param>
    public DatabaseStudioModule(StudioDescriptor descriptor, IStudioSettings settings, StudioCapabilityGuard guard)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(guard);

        this.descriptor = descriptor;
        this.settings = settings;
        this.guard = guard;
        navigation = descriptor.Navigation(settings);
        shortcuts = [.. PostgresShortcuts.Groups, .. descriptor.ExtraShortcuts];
    }

    /// <inheritdoc />
    public string Key => descriptor.Key;

    /// <inheritdoc />
    public string ProductName => descriptor.ProductName;

    /// <inheritdoc />
    public IReadOnlyList<StudioPageAssembly> PageAssemblies => descriptor.PageAssemblies;

    /// <inheritdoc />
    public IReadOnlyList<StudioNavSection> Navigation => navigation;

    /// <inheritdoc />
    public Type? ScopeSelector => typeof(ScopeSelector);

    /// <inheritdoc />
    public Type? HeaderSearch => typeof(FindBox);

    /// <inheritdoc />
    public Type? CircuitHost => typeof(DatabaseScopeHost);

    /// <inheritdoc />
    public Type? ScopeGate => typeof(DatabaseScopeGate);

    /// <inheritdoc />
    public string HomeRoute => descriptor.Key;

    /// <inheritdoc />
    public string Summary => descriptor.Summary;

    /// <inheritdoc />
    public string ExposedSentence => "Its pages carry no authorization, so " + descriptor.ExposedContent + " is readable by anyone who can reach this URL.";

    /// <inheritdoc />
    public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups
    {
        get
        {
            List<StudioModuleCapabilityGroup> groups = [new("reads authorized like writes", Rows(guard.OfferedReads))];

            IReadOnlyList<StudioCapability> mutating = guard.OfferedMutating;
            if (mutating.Count > 0)
            {
                groups.Add(new("mutating operations", Rows(mutating)));
            }

            return groups;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The module's own <c>ReadOnly</c>, while it is on. The chip draws groups only while the shell's switch is off, so a <c>ReadOnly</c> that
    /// is on here is this module's.
    /// </remarks>
    public string? ReadOnlyOption => settings.ReadOnly ? settings.ReadOnlyOptionName : null;

    /// <inheritdoc />
    public string? AllCapabilitiesOption => settings.AllCapabilities();

    /// <inheritdoc />
    /// <remarks>The Postgres module's components' own, then the Marten module's document lists and editor, where the module is the Marten one.</remarks>
    public IReadOnlyList<StudioShortcutGroup> Shortcuts => shortcuts;

    /// <inheritdoc />
    /// <remarks>Empty: the Activity page's footer keeps naming the studio's own range, 9200-9211, as literal text in phase A.</remarks>
    public string AuditEventIds => string.Empty;

    private List<StudioModuleCapability> Rows(IReadOnlyList<StudioCapability> capabilities) =>
        [.. capabilities.Select(capability => new StudioModuleCapability(capability.ToString(), guard.OptionFor(capability), guard.IsEnabled(capability)))];
}
