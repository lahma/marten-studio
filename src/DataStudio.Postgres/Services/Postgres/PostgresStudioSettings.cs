using Microsoft.Extensions.Options;

using DataStudio.Postgres.Services.Settings;
using DataStudio.Services.Settings;

namespace DataStudio.Postgres.Services.Postgres;

/// <summary>
/// The Postgres module's <see cref="IStudioSettings" />: a live adapter over <see cref="PostgresOptions" /> and the shell's settings.
/// </summary>
/// <remarks>
/// Every member reads its options again, so <c>MapDataStudio(pattern)</c>'s write of <c>DataStudioOptions.Path</c> onto the resolved instance after
/// startup is seen by every shared component. What belongs to the shell - the mount, the title, the entry policy, the poll interval - is answered
/// from the shell's settings; <see cref="ReadOnly" /> is either switch's (the shell's turns every module's capabilities off, this module's only its own).
/// </remarks>
internal sealed class PostgresStudioSettings : IStudioSettings
{
    /// <summary>The four capabilities PostgresStudio offers, in the chip's order.</summary>
    internal static IReadOnlyList<StudioCapability> Offered { get; } =
    [
        StudioCapability.RunSql,
        StudioCapability.BrowseDatabase,
        StudioCapability.ViewDefinitions,
        StudioCapability.ViewDatabaseActivity,
    ];

    private readonly IOptions<PostgresOptions> options;
    private readonly IShellSettings shell;

    /// <summary>The settings over <paramref name="options" />, answering the shell's members from <paramref name="shell" />, the container's own.</summary>
    public PostgresStudioSettings(IOptions<PostgresOptions> options, IShellSettings shell)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(shell);
        this.options = options;
        this.shell = shell;
    }

    private PostgresOptions Value => options.Value;

    /// <inheritdoc />
    public string ProductName => shell.ProductName;

    /// <inheritdoc />
    public string OptionsTypeName => nameof(PostgresOptions);

    /// <inheritdoc />
    public string CapabilitiesTypeName => nameof(PostgresCapabilities);

    /// <inheritdoc />
    public StudioText Text => PostgresStudioText.Instance;

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
    public string? ScopePolicy => Value.SourceAuthorizationPolicy;

    /// <inheritdoc />
    public string? WritePolicy => Value.WriteAuthorizationPolicy;

    /// <inheritdoc />
    public bool ReadOnly => shell.ReadOnly || Value.ReadOnly;

    /// <inheritdoc />
    /// <remarks>The switch that is on: the shell's when it is, else this module's - which is also the one a host reads to learn which switch exists.</remarks>
    public string ReadOnlyOptionName => shell.ReadOnly ? shell.ReadOnlyOptionName : ((IDiagnosticsSettings) this).OptionName("ReadOnly");

    /// <inheritdoc />
    /// <remarks>The four reads authorized like writes; PostgresStudio has no mutating capability.</remarks>
    public IReadOnlyList<StudioCapability> Capabilities => Offered;

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
    /// <remarks>
    /// "Source" once <c>AddDataSource</c> or <c>AddDatabaseSource</c> named one - the only configurations with a source
    /// picker, and the ones where a host source's database picker, "Database", sits beside it - and otherwise the
    /// descriptor's "Database", which names the one data source in the unavailable and refusal sentences.
    /// </remarks>
    public string? SourceLabel => Value.DataSources.Count + Value.DatabaseSources.Count > 0 ? "Source" : null;

    /// <inheritdoc />
    /// <remarks>Always <see langword="null" />: PostgresStudio has no document types, so none can be hidden.</remarks>
    public Func<Type, bool>? IsDocumentTypeVisible => null;

    /// <summary>
    /// The <see cref="PostgresCapabilities" /> property behind <paramref name="capability" />. The default arm throws
    /// rather than answering <see langword="false" />, as MartenStudio's does: a capability this studio does not have is
    /// never asked about by shared code, and one asked by mistake must fail a test rather than be silently off.
    /// </summary>
    internal static bool IsConfigured(PostgresCapabilities capabilities, StudioCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capability switch
        {
            StudioCapability.RunSql => capabilities.RunSql,
            StudioCapability.BrowseDatabase => capabilities.BrowseDatabase,
            StudioCapability.ViewDefinitions => capabilities.ViewDefinitions,
            StudioCapability.ViewDatabaseActivity => capabilities.ViewDatabaseActivity,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "PostgresCapabilities has no property for this capability: the Postgres module does not offer it."),
        };
    }
}
