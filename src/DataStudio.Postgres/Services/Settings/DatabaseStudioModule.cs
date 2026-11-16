using DataStudio.Services.Settings;
using DataStudio.Services;
using DataStudio.Postgres.Components.Layout;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// The database as a module of the shell (D34): Postgres Studio's pages or Marten Studio's, built from the studio's own
/// <see cref="StudioDescriptor" />, <see cref="IStudioSettings" /> and <see cref="StudioCapabilityGuard" />, and registered by
/// <see cref="StudioCoreRegistration" />. It is the root: its pages sit directly under the mount, and it always exists.
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
    }

    /// <inheritdoc />
    public string Key => descriptor.Key;

    /// <inheritdoc />
    public string ProductName => descriptor.ProductName;

    /// <inheritdoc />
    public bool IsRoot => true;

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
    public string HomeRoute => string.Empty;

    /// <inheritdoc />
    public string? ExposedSentence => null;

    /// <inheritdoc />
    public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups
    {
        get
        {
            List<StudioModuleCapabilityGroup> groups = [new("Reads authorized like writes", Rows(guard.OfferedReads))];

            IReadOnlyList<StudioCapability> mutating = guard.OfferedMutating;
            if (mutating.Count > 0)
            {
                groups.Add(new("Mutating operations", Rows(mutating)));
            }

            return groups;
        }
    }

    /// <inheritdoc />
    public string? AllCapabilitiesOption => settings.AllCapabilities();

    /// <inheritdoc />
    public IReadOnlyList<StudioShortcutGroup> Shortcuts => descriptor.ExtraShortcuts;

    /// <inheritdoc />
    /// <remarks>Empty: the Activity page's footer keeps naming the studio's own range, 9200-9211, as literal text in phase A.</remarks>
    public string AuditEventIds => string.Empty;

    private List<StudioModuleCapability> Rows(IReadOnlyList<StudioCapability> capabilities) =>
        [.. capabilities.Select(capability => new StudioModuleCapability(capability.ToString(), guard.OptionFor(capability), guard.IsEnabled(capability)))];
}
