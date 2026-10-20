using System.Reflection;

using PostgresStudio.Components;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Settings;

/// <summary>One assembly whose routable components are studio pages, and the compile-time root its <c>@page</c> directives use.</summary>
/// <param name="Assembly">The assembly.</param>
/// <param name="CompileTimeRoot">
/// The rooted prefix every page template in <paramref name="Assembly" /> starts with - <c>/marten</c> - and which the
/// endpoint mapping re-roots under the configured mount path.
/// </param>
internal sealed record StudioPageAssembly(Assembly Assembly, string CompileTimeRoot);

/// <summary>
/// One assembly whose health checks a studio runs, and the kinds of source they may run against.
/// </summary>
/// <param name="Assembly">The assembly the registry discovers checks in.</param>
/// <param name="SourceKinds">
/// The kinds of source its checks are offered, or <see langword="null" /> for every kind. A check that reads a source
/// through a studio's own types - MartenStudio's checks read the store, its options and its daemon - names that studio's
/// kind here, and is then never run against any other: no row, not a "could not check" one.
/// </param>
internal sealed record StudioHealthCheckAssembly(Assembly Assembly, IReadOnlySet<StudioSourceKind>? SourceKinds = null);

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
/// the same shared component says what <see cref="SourceNoun" /> says in any other studio. Every sentence it feeds keeps
/// MartenStudio's wording exactly as it was, so registering the descriptor changed nothing a host or a visitor reads.
/// </para>
/// </remarks>
internal sealed class StudioDescriptor
{
    private readonly Lazy<StudioRouteTable> routes;

    /// <summary>Creates a descriptor; the route table is built on first use and validated then.</summary>
    public StudioDescriptor()
    {
        routes = new Lazy<StudioRouteTable>(BuildRoutes);
    }

    private StudioRouteTable BuildRoutes() => new(PageAssemblies);

    /// <summary>The studio's key: <c>marten</c>. Also what <see cref="StudioRegistrationMarker" /> holds.</summary>
    public required string Key { get; init; }

    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>
    /// Every assembly whose pages this studio serves, each with its compile-time root. The route table and the endpoint
    /// re-rooting are built from this list and nothing else; a relative route two of them claim is an error.
    /// </summary>
    public required IReadOnlyList<StudioPageAssembly> PageAssemblies { get; init; }

    /// <summary>
    /// The assemblies the health registry discovers checks in, each with the kinds of source its checks may run against.
    /// A check found in more than one entry runs only against a kind every one of them admits - which is what keeps a
    /// studio's own checks off another studio's sources while both are compiled into one assembly.
    /// </summary>
    public required IReadOnlyList<StudioHealthCheckAssembly> HealthCheckAssemblies { get; init; }

    /// <summary>The sidebar, section by section.</summary>
    public required IReadOnlyList<StudioNavSection> Navigation { get; init; }

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

    /// <summary>The query-string name the source key travels under: <c>store</c>.</summary>
    public required string SourceQueryParameter { get; init; }

    /// <summary>A source in a sentence: <c>Marten store</c>.</summary>
    public required string SourceNoun { get; init; }

    /// <summary>A source as a control's label: <c>Store</c>.</summary>
    public required string SourceLabel { get; init; }

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
    /// The route table built from <see cref="PageAssemblies" />, once. Building it validates every page: a template
    /// outside its assembly's root, or a relative route two pages claim, throws on first use.
    /// </summary>
    public StudioRouteTable Routes => routes.Value;
}

/// <summary>One section of the sidebar.</summary>
/// <param name="Title">The section's heading.</param>
/// <param name="Items">Its entries, in order.</param>
internal sealed record StudioNavSection(string Title, IReadOnlyList<StudioNavItem> Items);

/// <summary>One sidebar entry.</summary>
/// <param name="Label">What the link says, and its accessible name.</param>
/// <param name="Route">The page below the studio root, or <see langword="null" /> for one not built yet (drawn disabled).</param>
/// <param name="Icon">The hand-drawn icon's name (<c>NavMenu.Icon</c>).</param>
/// <param name="BadgeKey">
/// Which badge the entry wears, or <see langword="null" />. <see cref="StudioNavBadgeKeys.Health" /> is drawn by the
/// shared sidebar from the health glance; any other key is looked up in what the indicator service read for it.
/// </param>
/// <param name="Title">The tooltip, when the label alone undersells the page; the label otherwise.</param>
internal sealed record StudioNavItem(
    string Label,
    string? Route,
    string Icon,
    string? BadgeKey = null,
    string? Title = null)
{
    /// <summary>
    /// Whether the entry is drawn, or <see langword="null" /> for always. Pure and cheap: it is asked on every render of
    /// the sidebar, and the answer must fail closed - an entry for something the host hides is absent (D27).
    /// </summary>
    public Func<IStudioSettings, bool>? IsShown { get; init; }
}

/// <summary>What one sidebar badge renders as.</summary>
/// <param name="Text">The count, or empty for a dot.</param>
/// <param name="CssClass">The badge's <c>ms-nav-badge-*</c> classes.</param>
/// <param name="Title">Its tooltip and accessible name.</param>
internal sealed record StudioNavBadge(string Text, string CssClass, string Title);

/// <summary>The badge keys the shared sidebar draws itself.</summary>
internal static class StudioNavBadgeKeys
{
    /// <summary>The health glance: the last sweep's unmuted Critical and Warning findings this visitor may see.</summary>
    public const string Health = "health";
}
