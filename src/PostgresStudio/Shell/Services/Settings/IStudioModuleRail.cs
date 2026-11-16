namespace DataStudio.Services.Settings;

/// <summary>
/// What a module's rail entries need of the circuit (D34), for a module whose entries depend on it: the database's links carry its scope, some
/// of its entries are drawn only for some scope states, and its badges are read from its own services. A module with none has its entries
/// drawn with undecorated links and every entry whose <see cref="StudioNavItem.IsShown" /> passes, and none of them wears a badge.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, registered with <c>TryAddEnumerable</c>, at most one per module (<see cref="ModuleKey" />). The rail (<c>NavMenu</c>) asks for the
/// rail of each section by the section's <see cref="StudioNavSection.ModuleKey" />, and knows nothing of what a module's links carry or what its
/// badges count.
/// </para>
/// <para>
/// A host (one <c>NavMenu</c>) subscribes to <see cref="Changed" /> once, when it is created and before it reads anything, and the rail's
/// per-host state starts afresh at that subscription: whether <see cref="StartAsync" /> has been called, and which read is the host's first.
/// A layout that is re-created is a new host.
/// </para>
/// </remarks>
internal interface IStudioModuleRail
{
    /// <summary>The key of the module whose entries this rail serves: <see cref="IStudioModule.Key" />.</summary>
    string ModuleKey { get; }

    /// <summary>The href of one of the module's entries, with whatever the module's links carry (the database: its scope).</summary>
    /// <param name="href">The entry's href below the mount, as the rail builds it.</param>
    string Decorate(string href);

    /// <summary>Whether an entry of the module is drawn for the circuit's state; asked on every render, in memory, no IO.</summary>
    /// <param name="item">The entry, whose own <see cref="StudioNavItem.IsShown" /> has already passed.</param>
    bool IsShown(StudioNavItem item);

    /// <summary>Raised synchronously the moment <see cref="Decorate" />'s answer changes. Handlers ask for a render and return.</summary>
    event Action? LinksChanged;

    /// <summary>Raised when the entries, the links or the badges may have changed: the rail redraws, reads again, redraws.</summary>
    event Func<Task>? Changed;

    /// <summary>Once per host, after its first interactive render and before its polling starts.</summary>
    /// <param name="js">The circuit's JS runtime, for whatever the module keeps in the browser.</param>
    /// <param name="cancellationToken">Cancelled when the host is disposed.</param>
    ValueTask StartAsync(Microsoft.JSInterop.IJSRuntime js, CancellationToken cancellationToken);

    /// <summary>The module's badges by key, or <see langword="null" /> when the read was cancelled or outrun and must not be drawn.</summary>
    /// <param name="cancellationToken">Cancelled when the host is disposed.</param>
    ValueTask<IReadOnlyDictionary<string, StudioNavBadge>?> ReadBadgesAsync(CancellationToken cancellationToken);
}
