using MartenStudio.Services.Settings;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IStudioSettings" />: a live adapter over <see cref="MartenStudioOptions" />.
/// </summary>
/// <remarks>
/// Every member reads <see cref="IOptions{TOptions}.Value" /> again, so <c>MapMartenStudio(pattern)</c>'s write of
/// <see cref="MartenStudioOptions.Path" /> onto the resolved instance after startup is seen by every shared component,
/// exactly as it was when they read the options themselves.
/// </remarks>
internal sealed class MartenStudioSettings : IStudioSettings
{
    private readonly IOptions<MartenStudioOptions> options;

    public MartenStudioSettings(IOptions<MartenStudioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>The adapter over <paramref name="options" />, for a caller that builds the studio's services by hand.</summary>
    public static MartenStudioSettings Of(MartenStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new MartenStudioSettings(Options.Create(options));
    }

    /// <summary>The adapter over <paramref name="options" />.</summary>
    public static MartenStudioSettings Of(IOptions<MartenStudioOptions> options) => new(options);

    private MartenStudioOptions Value => options.Value;

    /// <inheritdoc />
    public string ProductName => "Marten Studio";

    /// <inheritdoc />
    public string OptionsTypeName => nameof(MartenStudioOptions);

    /// <inheritdoc />
    public string CapabilitiesTypeName => nameof(MartenStudioCapabilities);

    /// <inheritdoc />
    public string Title => Value.Title;

    /// <inheritdoc />
    public string TrimmedPath => Value.TrimmedPath;

    /// <inheritdoc />
    public string EscapedPath => Value.EscapedPath;

    /// <inheritdoc />
    public bool HasCustomPath => Value.HasCustomPath;

    /// <inheritdoc />
    public string? AuthorizationPolicy => Value.AuthorizationPolicy;

    /// <inheritdoc />
    public string? ScopePolicy => Value.StoreAuthorizationPolicy;

    /// <inheritdoc />
    public string? WritePolicy => Value.WriteAuthorizationPolicy;

    /// <inheritdoc />
    public bool ReadOnly => Value.ReadOnly;

    /// <inheritdoc />
    /// <remarks>Every member: MartenStudio offers all thirteen.</remarks>
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
    public int MaxInlineValueBytes => Value.MaxInlineDocumentBytes;

    /// <inheritdoc />
    public TimeSpan QueryTimeout => Value.QueryTimeout;

    /// <inheritdoc />
    public TimeSpan RefreshInterval => Value.RefreshInterval;

    /// <inheritdoc />
    public TimeSpan EffectiveHealthInterval => Value.EffectiveHealthInterval;

    /// <inheritdoc />
    public long ExactCountThreshold => Value.ExactCountThreshold;

    /// <inheritdoc />
    public string? SqlConsoleRole => Value.SqlConsoleRole;

    /// <inheritdoc />
    public IReadOnlyList<string> BrowsableSchemas =>
        Value.BrowsableSchemas as IReadOnlyList<string> ?? [.. Value.BrowsableSchemas];

    /// <inheritdoc />
    public Func<Type, bool>? IsDocumentTypeVisible => Value.IsDocumentTypeVisible;

    /// <summary>
    /// The <see cref="MartenStudioCapabilities" /> property behind <paramref name="capability" />.
    /// </summary>
    /// <remarks>
    /// The default arm throws rather than answering <see langword="false" />. A <c>_ =&gt; false</c> arm is
    /// how a capability added to the enum and forgotten here becomes a switch a host sets to
    /// <see langword="true" /> and the studio silently goes on refusing: the page names the option, the
    /// host sets it, and nothing changes. <c>StudioCapabilityGuardTests</c> walks every member so the
    /// omission fails a test rather than a support ticket.
    /// </remarks>
    internal static bool IsConfigured(MartenStudioCapabilities capabilities, StudioCapability capability)
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
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "No MartenStudioCapabilities property is mapped to this capability."),
        };
    }

    /// <summary>
    /// <c>MartenStudioOptions.Capabilities.X</c> - the option behind <paramref name="capability" />, for Marten-only code
    /// that has no settings object in hand. Shared code asks <see cref="StudioCapabilityGuard.OptionFor" />.
    /// </summary>
    internal static string CapabilityOption(StudioCapability capability) =>
        nameof(MartenStudioOptions) + ".Capabilities." + capability;
}
