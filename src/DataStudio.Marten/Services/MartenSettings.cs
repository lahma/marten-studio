using DataStudio.Postgres.Services.Settings;
using DataStudio.Services.Settings;

using Microsoft.Extensions.Options;
using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Services;

/// <summary>
/// The Marten module's <see cref="IStudioSettings" />: a live adapter over <see cref="MartenOptions" /> and the shell's settings.
/// </summary>
/// <remarks>
/// Every member reads its options again, so <c>MapDataStudio(pattern)</c>'s write of <c>DataStudioOptions.Path</c> onto the resolved instance after
/// startup is seen by every shared component. What belongs to the shell - the mount, the title, the entry policy, the poll interval - is answered
/// from the shell's settings; <see cref="ReadOnly" /> is either switch's (the shell's turns every module's capabilities off, this module's only its own).
/// </remarks>
internal sealed class MartenSettings : IStudioSettings
{
    private readonly IOptions<MartenOptions> options;
    private readonly IShellSettings shell;

    /// <summary>The settings over <paramref name="options" />, answering the shell's members from <paramref name="shell" />, the container's own.</summary>
    public MartenSettings(IOptions<MartenOptions> options, IShellSettings shell)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(shell);
        this.options = options;
        this.shell = shell;
    }

    private MartenOptions Value => options.Value;

    /// <summary><see cref="MartenOptions.ProjectionThresholds" />, as frozen at startup, for the daemon checks (D03, D04).</summary>
    internal IReadOnlyDictionary<string, ProjectionLagThreshold> ProjectionThresholds => Value.ProjectionLagThresholdsByName;

    /// <inheritdoc />
    public string ProductName => shell.ProductName;

    /// <inheritdoc />
    public string OptionsTypeName => nameof(MartenOptions);

    /// <inheritdoc />
    public string CapabilitiesTypeName => nameof(MartenCapabilities);

    /// <inheritdoc />
    public StudioText Text => MartenText.Instance;

    /// <inheritdoc />
    public string Title => shell.Title;

    /// <inheritdoc />
    public string TrimmedPath => shell.TrimmedPath;

    /// <inheritdoc />
    public string EscapedPath => shell.EscapedPath;

    /// <inheritdoc />
    public bool HasCustomPath => shell.HasCustomPath;

    /// <inheritdoc />
    public string? ApplicationPath => shell.ApplicationPath;

    /// <inheritdoc />
    public string? AuthorizationPolicy => shell.AuthorizationPolicy;

    /// <inheritdoc />
    public string? ScopePolicy => Value.StoreAuthorizationPolicy;

    /// <inheritdoc />
    public string? WritePolicy => Value.WriteAuthorizationPolicy;

    /// <inheritdoc />
    public bool ReadOnly => shell.ReadOnly || Value.ReadOnly;

    /// <inheritdoc />
    /// <remarks>The switch that is on: the shell's when it is, else this module's - which is also the one a host reads to learn which switch exists.</remarks>
    public string ReadOnlyOptionName => shell.ReadOnly ? shell.ReadOnlyOptionName : ((IDiagnosticsSettings) this).OptionName("ReadOnly");

    /// <inheritdoc />
    /// <remarks>Every member: MartenStudio offers all fourteen.</remarks>
    public IReadOnlyList<StudioCapability> Capabilities => StudioCapabilityGuard.All;

    /// <inheritdoc />
    public bool IsConfigured(StudioCapability capability) => IsConfigured(Value.Capabilities, capability);

    /// <inheritdoc />
    public int DefaultPageSize => Value.DefaultPageSize;

    /// <inheritdoc />
    public int MaxPageSize => Value.MaxPageSize;

    /// <inheritdoc />
    public int MaxSqlConsoleRows => Value.MaxSqlConsoleRows;

    /// <inheritdoc />
    public int MaxInlineValueBytes => Value.MaxInlineValueBytes;

    /// <inheritdoc />
    public TimeSpan QueryTimeout => Value.QueryTimeout;

    /// <inheritdoc />
    public int MaxStudioConnections => Value.MaxStudioConnections;

    /// <inheritdoc />
    public string? StudioApplicationName => Value.StudioApplicationName;

    /// <inheritdoc />
    public TimeSpan RefreshInterval => shell.RefreshInterval;

    /// <inheritdoc />
    /// <remarks><c>HealthInterval</c>, or the shell's <c>RefreshInterval</c> when that is longer: the advisor never runs faster than a page polls.</remarks>
    public TimeSpan EffectiveHealthInterval => Value.HealthInterval >= shell.RefreshInterval ? Value.HealthInterval : shell.RefreshInterval;

    /// <inheritdoc />
    public long ExactCountThreshold => Value.ExactCountThreshold;

    /// <inheritdoc />
    public string? SqlConsoleRole => Value.SqlConsoleRole;

    /// <inheritdoc />
    public IReadOnlyList<string> BrowsableSchemas =>
        Value.BrowsableSchemas as IReadOnlyList<string> ?? [.. Value.BrowsableSchemas];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> AcceptedChecks => Value.AcceptedCheckReasons;

    /// <inheritdoc />
    public Func<Type, bool>? IsDocumentTypeVisible => Value.IsDocumentTypeVisible;

    /// <summary>
    /// The <see cref="MartenCapabilities" /> property behind <paramref name="capability" />.
    /// </summary>
    /// <remarks>
    /// The default arm throws rather than answering <see langword="false" />. A <c>_ =&gt; false</c> arm is
    /// how a capability added to the enum and forgotten here becomes a switch a host sets to
    /// <see langword="true" /> and the studio silently goes on refusing: the page names the option, the
    /// host sets it, and nothing changes. <c>StudioCapabilityGuardTests</c> walks every member so the
    /// omission fails a test rather than a support ticket.
    /// </remarks>
    internal static bool IsConfigured(MartenCapabilities capabilities, StudioCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capability switch
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
            StudioCapability.ViewDatabaseActivity => capabilities.ViewDatabaseActivity,
            StudioCapability.CompactStreams => capabilities.CompactStreams,
            StudioCapability.RunLinqQueries => capabilities.RunLinqQueries,
            StudioCapability.ViewDefinitions => capabilities.ViewDefinitions,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "No MartenCapabilities property is mapped to this capability."),
        };
    }

    /// <summary>
    /// <c>MartenOptions.Capabilities.X</c> - the option behind <paramref name="capability" />, for Marten-only code
    /// that has no settings object in hand. Shared code asks <see cref="StudioCapabilityGuard.OptionFor" />.
    /// </summary>
    internal static string CapabilityOption(StudioCapability capability) =>
        nameof(MartenOptions) + ".Capabilities." + capability;
}
