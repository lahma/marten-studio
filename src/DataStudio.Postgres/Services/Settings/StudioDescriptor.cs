using DataStudio.Services.Settings;
using DataStudio.Services;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// What makes one studio this studio: its name, its pages, its navigation, how it names a source, and how it builds the
/// resource an authorization handler is asked about.
/// </summary>
/// <remarks>
/// <para>
/// A singleton, registered by a studio's <c>Add*Studio()</c> and read by every shared service and component in place of
/// that studio's own types. Nothing on it is about a visitor, and nothing on it does IO: it is data plus two pure
/// functions (<see cref="CreateResource" />, and each navigation entry's optional <see cref="StudioNavItem.IsShown" />).
/// </para>
/// <para>
/// <b>Text is here so that shared markup says the right noun.</b> A page refusal says "this Marten store" in MartenStudio;
/// the same shared component says what <c>SourceNoun</c> says in any other studio. Every sentence it feeds keeps
/// MartenStudio's wording exactly as it was, so registering the descriptor changed nothing a host or a visitor reads.
/// </para>
/// </remarks>
internal sealed class StudioDescriptor : DiagnosticsDescriptor
{
    /// <summary>
    /// Every assembly whose pages this studio serves, each with its compile-time root: the database module's own
    /// (<see cref="DatabaseStudioModule" />). The shell's route table and the endpoint re-rooting are built from every module's
    /// list (<see cref="StudioShellDescriptor.PageAssemblies" />); a relative route two of them claim is an error.
    /// </summary>
    public required IReadOnlyList<StudioPageAssembly> PageAssemblies { get; init; }

    /// <summary>
    /// The sidebar, section by section, built for the settings the studio is registered with: an entry whose condition needs more than the
    /// master switch (<see cref="StudioNavConditions.RunSqlEnabled" />) closes over them, so the navigation is built once per registration
    /// (by <see cref="DatabaseStudioModule" />) and never held in a static.
    /// </summary>
    public required Func<IStudioSettings, IReadOnlyList<StudioNavSection>> Navigation { get; init; }

    /// <summary>
    /// The body the studio's shared root page (<c>StudioHome</c>) renders - MartenStudio's store overview. A component
    /// with no <c>@page</c> of its own: the root route is the shared page's, so two studios' bodies never claim it twice.
    /// </summary>
    public required Type OverviewComponent { get; init; }

    /// <summary>
    /// The body the studio's shared <c>query</c> page (<c>QueryPage</c>) renders - MartenStudio's three-mode page, or
    /// the shared SQL console panel alone - as <see cref="OverviewComponent" />.
    /// </summary>
    public required Type QueryComponent { get; init; }

    /// <summary>
    /// What a page served to anyone exposes, in the banner that says so: "every document, event stream and schema in
    /// this process".
    /// </summary>
    public required string ExposedContent { get; init; }

    /// <summary>
    /// The resource an authorization handler is shown for a scope and a capability (or
    /// <see langword="null" /> for a read): MartenStudio's public <c>MartenStoreResource</c>. Pure, and called for every
    /// policy question, so it must not allocate more than the resource itself or do any IO.
    /// </summary>
    public required Func<StudioScope, string?, object> CreateResource { get; init; }

    /// <summary>The startup guard's name for the endpoints this studio maps: <c>Marten Studio</c>.</summary>
    public required string Surface { get; init; }

    /// <summary>
    /// The startup guard's lead sentence: what an unauthorized mapping would serve, and that the studio refuses to start.
    /// </summary>
    public required string Exposure { get; init; }

    /// <summary>The startup guard's list of the three ways to say which authorization was meant, one per line.</summary>
    public required string Remedies { get; init; }

    /// <summary>
    /// The keyboard shortcuts only this studio's pages answer to (1.2), listed on the shortcut sheet after
    /// <see cref="StudioShortcuts.Shared" />: MartenStudio's document list and document editor. Empty for a studio with none, so a
    /// Postgres Studio host is never told about a key that does nothing on its pages.
    /// </summary>
    public IReadOnlyList<StudioShortcutGroup> ExtraShortcuts { get; init; } = [];

    /// <summary>The words the shell speaks in, taken from this descriptor: what <see cref="StudioModuleComposition.Compose" /> composes the shell's descriptor from.</summary>
    public StudioShellIdentity ShellIdentity => new(ProductName, ExposedContent, Surface, Exposure, Remedies);
}

/// <summary>The conditions a sidebar entry may carry (<see cref="StudioNavItem.IsShown" />), shared by both studios' descriptors.</summary>
internal static class StudioNavConditions
{
    /// <summary>
    /// Whether the SQL console's capability is effectively on: <c>RunSql</c> set and the master switch off. The entries that
    /// are only a door to a page that runs SQL (Postgres Studio's Query) are absent otherwise, so an
    /// administrator is not offered a page whose one sentence is addressed to the developer who configures the host
    /// (issue #37). The page keeps its notice for anyone who reaches it by address.
    /// </summary>
    public static bool RunSqlEnabled(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.ReadOnly && settings.IsConfigured(StudioCapability.RunSql);
    }
}
