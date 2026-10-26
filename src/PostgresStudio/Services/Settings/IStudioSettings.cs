namespace PostgresStudio.Services.Settings;

/// <summary>
/// Everything the shared studio reads from a host's options, behind one interface - so that no shared service,
/// component or page names a studio's public options type.
/// </summary>
/// <remarks>
/// <para>
/// <b>Live, never a copy.</b> An implementation reads its options object on every access. <c>MapMartenStudio(pattern)</c>
/// writes <c>Path</c> onto the resolved options instance after the container is built (the pattern wins over the
/// option), and every component that builds a link or matches a route has to see that write; a snapshot taken when the
/// first circuit opened would serve pages that navigate somewhere else.
/// </para>
/// <para>
/// <b>Names, not values, are what a sentence needs.</b> A disabled control says which option a host would set, spelled
/// the way the host's own registration spells it (D4). <see cref="OptionName" /> composes that spelling from
/// <see cref="OptionsTypeName" />, so the shared code that writes the sentence never names a studio's type itself:
/// MartenStudio answers <c>MartenStudioOptions.Capabilities.RunSql</c>, and a studio with options of its own answers in
/// its own type's name.
/// </para>
/// <para>
/// <b>What it must not do.</b> It never connects, never resolves a source and never throws for a value the host set:
/// options validation has already refused an invalid configuration at startup, and this is read on render paths. It
/// is a singleton - nothing on it is about a visitor.
/// </para>
/// </remarks>
internal interface IStudioSettings
{
    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    string ProductName { get; }

    /// <summary>The public options type's name, as a host's registration spells it: <c>MartenStudioOptions</c>.</summary>
    string OptionsTypeName { get; }

    /// <summary>The public capabilities type's name: <c>MartenStudioCapabilities</c>, whose <c>All()</c> a remedy names.</summary>
    string CapabilitiesTypeName { get; }

    /// <summary>
    /// Every sentence the shared studio says in this studio's own words (<see cref="StudioText" />): MartenStudio's is
    /// <see cref="StudioText.Marten" />, which is 0.4's wording to the byte. No default: a studio that forgot to say would
    /// otherwise speak another's words.
    /// </summary>
    StudioText Text { get; }

    /// <summary>The browser title.</summary>
    string Title { get; }

    /// <summary>The mount path, rooted and without a trailing slash: <c>/marten</c>, <c>/ops/marten</c>.</summary>
    string TrimmedPath { get; }

    /// <summary><see cref="TrimmedPath" /> percent-encoded, as a browser emits it in a request URI and a <c>&lt;base href&gt;</c>.</summary>
    string EscapedPath { get; }

    /// <summary>Whether <see cref="TrimmedPath" /> differs from the studio's compile-time default. A fact, not a mode.</summary>
    bool HasCustomPath { get; }

    /// <summary>The endpoint policy every studio endpoint requires, or <see langword="null" /> for none of its own.</summary>
    string? AuthorizationPolicy { get; }

    /// <summary>
    /// The policy asked, with the scope as the resource, before anything of a source, database or tenant is listed,
    /// framed or read - MartenStudio's <c>StoreAuthorizationPolicy</c>. <see langword="null" /> asks nothing.
    /// </summary>
    string? ScopePolicy { get; }

    /// <summary>
    /// The policy asked, with the capability named on the resource, for every mutating call and every read authorized
    /// like a write. <see langword="null" /> falls back to <see cref="ScopePolicy" />.
    /// </summary>
    string? WritePolicy { get; }

    /// <summary>The master switch: every capability off, whatever else is set.</summary>
    bool ReadOnly { get; }

    /// <summary>
    /// The capabilities this studio offers at all, in the order the capability chip lists them. A capability not in it is
    /// a switch this studio does not have, and <see cref="IsConfigured" /> is never asked about it by shared code.
    /// </summary>
    IReadOnlyList<StudioCapability> Capabilities { get; }

    /// <summary>
    /// Whether the host switched <paramref name="capability" /> on - the individual flag, without <see cref="ReadOnly" />.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capability" /> has no flag in this studio's options. Thrown rather than answered <see langword="false" />:
    /// a <c>_ =&gt; false</c> arm is how a capability added to the enum and forgotten becomes a switch a host sets to
    /// <see langword="true" /> while the studio silently goes on refusing.
    /// </exception>
    bool IsConfigured(StudioCapability capability);

    /// <summary>The page size a grid opens with.</summary>
    int DefaultPageSize { get; }

    /// <summary>The largest page size a visitor may ask for.</summary>
    int MaxPageSize { get; }

    /// <summary>The SQL console's row cap.</summary>
    int MaxSqlConsoleRows { get; }

    /// <summary>The largest value rendered inline before it is offered on demand (MartenStudio's <c>MaxInlineDocumentBytes</c>).</summary>
    int MaxInlineValueBytes { get; }

    /// <summary>
    /// The options member <see cref="MaxInlineValueBytes" /> is read from, as a sentence names it through
    /// <see cref="OptionName" />: <c>MaxInlineDocumentBytes</c> in MartenStudio, <c>MaxInlineValueBytes</c> in
    /// PostgresStudio - the one option the two studios spell differently.
    /// </summary>
    string MaxInlineValueBytesMember { get; }

    /// <summary>The <c>statement_timeout</c> every studio statement runs under.</summary>
    TimeSpan QueryTimeout { get; }

    /// <summary>How often a live page polls.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>The health sweep interval, never shorter than <see cref="RefreshInterval" />.</summary>
    TimeSpan EffectiveHealthInterval { get; }

    /// <summary>Above this estimate an exact count is offered on demand rather than run.</summary>
    long ExactCountThreshold { get; }

    /// <summary>The role the console, the browser's reads and plans run as (<c>SET LOCAL ROLE</c>), or <see langword="null" />.</summary>
    string? SqlConsoleRole { get; }

    /// <summary>The schemas the database browser may show beyond a source's own: exact names, or <c>"*"</c> (D27).</summary>
    IReadOnlyList<string> BrowsableSchemas { get; }

    /// <summary>
    /// The host's document-type filter, or <see langword="null" /> when every type is visible - which is always the
    /// answer of a studio that has no document types. Every rule that exists only to protect a hidden type is off
    /// while this is <see langword="null" />, which under D27 is correct when no hidden type can exist.
    /// </summary>
    Func<Type, bool>? IsDocumentTypeVisible { get; }

    /// <summary>
    /// The source picker's label for this configuration, or <see langword="null" /> for the descriptor's own
    /// (<see cref="StudioDescriptor.SourceLabel" />). PostgresStudio says "Source" once a host or <c>AddDataSource</c>
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

/// <summary>The sentences built from <see cref="IStudioSettings" /> that more than one place writes.</summary>
internal static class StudioSettingsText
{
    /// <summary>The option a host sets to enable <paramref name="capability" />: <c>MartenStudioOptions.Capabilities.RunSql</c>.</summary>
    public static string CapabilityOption(this IStudioSettings settings, StudioCapability capability)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.OptionName("Capabilities." + capability);
    }

    /// <summary>The master switch's option: <c>MartenStudioOptions.ReadOnly</c>.</summary>
    public static string ReadOnlyOption(this IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.OptionName("ReadOnly");
    }

    /// <summary>
    /// The source picker's label: what <paramref name="settings" /> says for this configuration, or the descriptor's.
    /// </summary>
    public static string SourceLabelOf(this IStudioSettings settings, StudioDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        return settings.SourceLabel ?? descriptor.SourceLabel;
    }

    /// <summary>The one-line opt-in that turns every capability on: <c>MartenStudioCapabilities.All()</c>.</summary>
    public static string AllCapabilities(this IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.CapabilitiesTypeName + ".All()";
    }
}
