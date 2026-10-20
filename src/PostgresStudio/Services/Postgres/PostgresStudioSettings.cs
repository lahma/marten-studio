using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="IStudioSettings" />: a live adapter over <see cref="PostgresStudioOptions" />.
/// </summary>
/// <remarks>
/// Every member reads <see cref="IOptions{TOptions}.Value" /> again, so <c>MapPostgresStudio(pattern)</c>'s write of
/// <see cref="PostgresStudioOptions.Path" /> onto the resolved instance after startup is seen by every shared component.
/// </remarks>
internal sealed class PostgresStudioSettings : IStudioSettings
{
    /// <summary>The three capabilities PostgresStudio offers, in the chip's order.</summary>
    internal static IReadOnlyList<StudioCapability> Offered { get; } =
    [
        StudioCapability.RunSql,
        StudioCapability.BrowseDatabase,
        StudioCapability.ViewDatabaseActivity,
    ];

    private readonly IOptions<PostgresStudioOptions> options;

    public PostgresStudioSettings(IOptions<PostgresStudioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>The adapter over <paramref name="options" />, for a caller that builds the studio's services by hand.</summary>
    public static PostgresStudioSettings Of(PostgresStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new PostgresStudioSettings(Options.Create(options));
    }

    private PostgresStudioOptions Value => options.Value;

    /// <inheritdoc />
    public string ProductName => "Postgres Studio";

    /// <inheritdoc />
    public string OptionsTypeName => nameof(PostgresStudioOptions);

    /// <inheritdoc />
    public string CapabilitiesTypeName => nameof(PostgresStudioCapabilities);

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
    public string? ScopePolicy => Value.SourceAuthorizationPolicy;

    /// <inheritdoc />
    public string? WritePolicy => Value.WriteAuthorizationPolicy;

    /// <inheritdoc />
    public bool ReadOnly => Value.ReadOnly;

    /// <inheritdoc />
    /// <remarks>The three reads authorized like writes; PostgresStudio has no mutating capability.</remarks>
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
    public string MaxInlineValueBytesMember => nameof(PostgresStudioOptions.MaxInlineValueBytes);

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
    /// <remarks>Always <see langword="null" />: PostgresStudio has no document types, so none can be hidden.</remarks>
    public Func<Type, bool>? IsDocumentTypeVisible => null;

    /// <summary>
    /// The <see cref="PostgresStudioCapabilities" /> property behind <paramref name="capability" />. The default arm throws
    /// rather than answering <see langword="false" />, as MartenStudio's does: a capability this studio does not have is
    /// never asked about by shared code, and one asked by mistake must fail a test rather than be silently off.
    /// </summary>
    internal static bool IsConfigured(PostgresStudioCapabilities capabilities, StudioCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return capability switch
        {
            StudioCapability.RunSql => capabilities.RunSql,
            StudioCapability.BrowseDatabase => capabilities.BrowseDatabase,
            StudioCapability.ViewDatabaseActivity => capabilities.ViewDatabaseActivity,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capability),
                capability,
                "PostgresStudioCapabilities has no property for this capability: Postgres Studio does not offer it."),
        };
    }
}
