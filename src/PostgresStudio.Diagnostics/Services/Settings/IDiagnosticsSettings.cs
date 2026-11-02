namespace PostgresStudio.Services.Settings;

/// <summary>
/// Everything the diagnostics engine reads from a host's options, behind one interface - so that no engine type names an
/// options type, and so that <c>AddPostgresDiagnostics()</c> and a studio's <c>Add*Studio()</c> answer it from their own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live, never a copy,</b> as <see cref="IDiagnosticsSettings" />'s members are: an implementation reads its options object
/// on every access. It never connects, never resolves a source and never throws for a value the host set - options
/// validation has already refused an invalid configuration - and it is a singleton: nothing on it is about a visitor.
/// </para>
/// <para>
/// A studio's <c>IDiagnosticsSettings</c> extends this with everything only a UI reads - the path, the policies, the
/// capabilities, the page sizes - and one object answers both in a host that registers a studio.
/// </para>
/// </remarks>
internal interface IDiagnosticsSettings
{
    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    string ProductName { get; }

    /// <summary>The public options type's name, as a host's registration spells it: <c>MartenStudioOptions</c>.</summary>
    string OptionsTypeName { get; }

    /// <summary>
    /// Every sentence the shared studio says in this studio's own words (<see cref="StudioText" />): MartenStudio's is
    /// <see cref="StudioText.Marten" />, which is 0.4's wording to the byte. This member has no default implementation, so
    /// a studio's settings must say. A few shared services take the settings optionally, only so that a test can build
    /// them by hand, and fall back to MartenStudio's words when given none (<see cref="StudioText.Of" />); every studio's
    /// registration supplies its settings, which <c>PostgresStudioWiringTests</c> holds a real <c>AddPostgresStudio()</c>
    /// host to.
    /// </summary>
    StudioText Text { get; }

    /// <summary>The <c>statement_timeout</c> every studio statement runs under.</summary>
    TimeSpan QueryTimeout { get; }

    /// <summary>The health sweep interval, never shorter than <c>RefreshInterval</c>.</summary>
    TimeSpan EffectiveHealthInterval { get; }

    /// <summary>The role the console, the browser's reads and plans run as (<c>SET LOCAL ROLE</c>), or <see langword="null" />.</summary>
    string? SqlConsoleRole { get; }

    /// <summary>Issue #44: the most connections the studio holds of each pool at a time (<c>MaxStudioConnections</c>).</summary>
    int MaxStudioConnections => Internal.Sql.StudioConnectionPools.DefaultMax;

    /// <summary>
    /// Issue #44: the <c>application_name</c> the studio's sessions carry: the host's, <c>""</c> for the application's own, or
    /// <see langword="null" /> for the derived one (<c>StudioApplicationName</c>).
    /// </summary>
    string? StudioApplicationName => null;

    /// <summary>
    /// Issue #44: the name the derived session name ends with - the product's, or the engine's on a host with no studio.
    /// </summary>
    string SessionProduct => OptionsTypeName == "PostgresDiagnosticsOptions" ? "Postgres Studio Diagnostics" : Text.ProductName;

    /// <summary>The schemas the database browser may show beyond a source's own: exact names, or <c>"*"</c> (D27).</summary>
    IReadOnlyList<string> BrowsableSchemas { get; }

    /// <summary>
    /// The checks the host accepted, by code with its reason (<c>AcceptedChecks</c>), keys compared ignoring case. Host
    /// configuration, validated at startup. Empty by default, so a settings object built without one accepts nothing.
    /// </summary>
    IReadOnlyDictionary<string, string> AcceptedChecks => EmptyAcceptedChecks.Instance;

    /// <summary>
    /// The host's document-type filter, or <see langword="null" /> when every type is visible - which is always the
    /// answer of a studio that has no document types. Every rule that exists only to protect a hidden type is off
    /// while this is <see langword="null" />, which under D27 is correct when no hidden type can exist.
    /// </summary>
    Func<Type, bool>? IsDocumentTypeVisible { get; }

    /// <summary>
    /// The source picker's label for this configuration, or <see langword="null" /> for the descriptor's own
    /// (<see cref="DiagnosticsDescriptor.SourceLabel" />). PostgresStudio says "Source" once a host or <c>AddDataSource</c>
    /// source exists, so the source picker and the database picker are never both called "Database".
    /// </summary>
    string? SourceLabel => null;

    /// <summary>
    /// <paramref name="member" /> spelled the way a host's registration spells it: <c>MartenStudioOptions.ReadOnly</c>,
    /// <c>MartenStudioOptions.Capabilities.RunSql</c>.
    /// </summary>
    /// <param name="member">The member path below the options type, such as <c>ReadOnly</c> or <c>Capabilities.RunSql</c>.</param>
    string OptionName(string member) => OptionsTypeName + "." + member;
}

/// <summary>The accepted checks of a settings object that has none.</summary>
internal static class EmptyAcceptedChecks
{
    /// <summary>No check accepted, keys compared ignoring case.</summary>
    public static IReadOnlyDictionary<string, string> Instance { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The sentences built from <see cref="IDiagnosticsSettings" /> that more than one place writes.</summary>
internal static class DiagnosticsSettingsText
{
    /// <summary>
    /// The source picker's label: what <paramref name="settings" /> says for this configuration, or the descriptor's.
    /// </summary>
    public static string SourceLabelOf(this IDiagnosticsSettings settings, DiagnosticsDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        return settings.SourceLabel ?? descriptor.SourceLabel;
    }
}
