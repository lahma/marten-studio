using System.Globalization;

using DataStudio.Services.Settings;
using DataStudio.Services;
using Microsoft.JSInterop;

using DataStudio.Postgres.Components;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services.Live;

/// <summary>The conditions the database's rail answers for an entry (<see cref="StudioNavItem.ShownWhen" />).</summary>
internal static class DatabaseRailConditions
{
    /// <summary>Drawn while the visitor may see two or more stores or sources: the header's picker's own test (the Marten module's Stores entry).</summary>
    public const string SeveralSources = "several-sources";

    /// <summary>Drawn where the header draws a tenant picker (the Marten module's Tenants entry).</summary>
    public const string TenantSelector = "tenant-selector";

    /// <summary>
    /// What a condition key says for a scope state: <see langword="true" /> for no key, the key's test otherwise, and <see langword="false" /> for a key
    /// nothing here knows (fail closed). Pure and in-memory: it reads what <see cref="StudioState" /> already holds.
    /// </summary>
    /// <param name="key">The entry's <see cref="StudioNavItem.ShownWhen" />.</param>
    /// <param name="state">The circuit's scope state.</param>
    public static bool IsShown(string? key, StudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return key switch
        {
            null => true,
            SeveralSources => state.KnownStoreCount > 1,
            TenantSelector => state.ShowTenantSelector,
            _ => false,
        };
    }
}

/// <summary>
/// What the shell's rail needs of the database as a module (D34): its links carry the studio's scope, some of its entries are drawn only for some
/// scope states, and its badges - the health glance and what <see cref="INavIndicatorService" />'s contributors read - are read for the active scope.
/// </summary>
/// <remarks>
/// <para>
/// Moved from <c>NavMenu</c>, not rewritten. Scoped, because the scope it reads for is the circuit's. The rail's per-host state starts afresh
/// whenever a host subscribes to <see cref="Changed" />: what <c>NavMenu</c> kept in its own fields - whether it had started, and the last read -
/// is a host's, and a layout that is re-created is a new host.
/// </para>
/// <para>
/// A host's <see cref="Changed" /> handler is subscribed to <see cref="StudioState.ScopeChanged" /> itself, at the moment the host subscribes - the
/// slot <c>NavMenu</c> had when it subscribed in its own <c>OnInitializedAsync</c>, after the circuit host's, which is what keeps 1.2's N5b window
/// (the address follows the scope before anything else reacts) as narrow as it was. It is subscribed to <see cref="HealthMutes.Changed" /> through
/// a relay that says the read it causes is a mutes change's.
/// </para>
/// </remarks>
internal sealed class DatabaseModuleRail : IStudioModuleRail
{
    private readonly StudioState state;
    private readonly StudioDescriptor descriptor;
    private readonly INavIndicatorService indicators;
    private readonly HealthMutes healthMutes;
    private readonly HealthAlerts healthAlerts;
    private readonly StudioPreferences preferences;

    private readonly Lock gate = new();
    private readonly List<(Func<Task> Handler, Func<Task> OnMutes)> hosts = [];

    private int started;
    private int firstReadPending = 1;
    private int readForMutes;
    private HealthGlance? lastGlance;

    /// <summary>Creates the rail for the circuit.</summary>
    /// <param name="state">The circuit's scope.</param>
    /// <param name="descriptor">The studio's descriptor: the name of the source parameter the links carry.</param>
    /// <param name="indicators">The badges' reader.</param>
    /// <param name="healthMutes">The browser's muted findings.</param>
    /// <param name="healthAlerts">The toast about a finding that is newly Critical.</param>
    /// <param name="preferences">The circuit's time zone, for the health badge's tooltip.</param>
    public DatabaseModuleRail(
        StudioState state,
        StudioDescriptor descriptor,
        INavIndicatorService indicators,
        HealthMutes healthMutes,
        HealthAlerts healthAlerts,
        StudioPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(healthMutes);
        ArgumentNullException.ThrowIfNull(healthAlerts);
        ArgumentNullException.ThrowIfNull(preferences);

        this.state = state;
        this.descriptor = descriptor;
        this.indicators = indicators;
        this.healthMutes = healthMutes;
        this.healthAlerts = healthAlerts;
        this.preferences = preferences;
    }

    /// <inheritdoc />
    public string ModuleKey => descriptor.Key;

    /// <summary>
    /// The rail's href for a page: the page, carrying the scope the studio is about (1.2). A rail click is a client-side navigation, and
    /// an address without <c>store=&amp;db=</c> would show the previous scope's page under no scope until the shell rewrote it. Read at
    /// render, and the rail renders again on every location change and, synchronously, whenever the scope its links carry moves
    /// (<see cref="StudioState.LinkScopeChanged" />), so it is never older than the address. The scope is
    /// <see cref="StudioState.LinkScope" />: the one being applied while the catalog is read (a pasted address, a picker just
    /// moved), else the active one.
    /// </summary>
    /// <param name="href">The entry's href below the mount.</param>
    public string Decorate(string href) => StudioScopeQuery.Append(href, state.LinkScope, descriptor.SourceQueryParameter);

    /// <summary>
    /// Whether an entry is drawn for the visitor's scope state: every one with no condition, and otherwise what its key says (1.2). Pure and
    /// in-memory: the sidebar asks it on every render and redraws on every scope change, so it reads what <see cref="StudioState" /> already
    /// holds and does no I/O. A key this rail does not know is not shown, failing closed.
    /// </summary>
    /// <param name="item">The entry.</param>
    public bool IsShown(StudioNavItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return DatabaseRailConditions.IsShown(item.ShownWhen, state);
    }

    /// <inheritdoc />
    /// <remarks>The scope's own synchronous event, forwarded: the host's handler is the one <see cref="StudioState" /> raises.</remarks>
    public event Action? LinksChanged
    {
        add => state.LinkScopeChanged += value;
        remove => state.LinkScopeChanged -= value;
    }

    /// <inheritdoc />
    public event Func<Task>? Changed
    {
        add
        {
            if (value is null)
            {
                return;
            }

            // The read a mutes change causes is not a poll: it redraws the badge and takes note of nothing (ReadBadgesAsync). The note is made
            // only while this relay's host is still attached: HealthMutes walks a snapshot of its handlers, so a relay whose host has gone can still
            // be called, and it must do what a gone host's handler does - nothing - or its flag would be consumed by the next host's first read.
            Func<Task>? onMutes = null;
            onMutes = () =>
            {
                lock (gate)
                {
                    if (!hosts.Exists(x => ReferenceEquals(x.OnMutes, onMutes)))
                    {
                        return Task.CompletedTask;
                    }

                    Interlocked.Exchange(ref readForMutes, 1);
                }

                return value();
            };

            lock (gate)
            {
                hosts.Add((value, onMutes));

                // A new host: nothing has started, its first read is its own, and it has read nothing.
                Volatile.Write(ref started, 0);
                Volatile.Write(ref firstReadPending, 1);
                Volatile.Write(ref readForMutes, 0);
                Volatile.Write(ref lastGlance, null);
            }

            state.ScopeChanged += value;
            healthMutes.Changed += onMutes;
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            Func<Task>? onMutes = null;

            lock (gate)
            {
                int index = hosts.FindLastIndex(x => x.Handler == value);
                if (index >= 0)
                {
                    onMutes = hosts[index].OnMutes;
                    hosts.RemoveAt(index);
                }
            }

            state.ScopeChanged -= value;

            if (onMutes is not null)
            {
                healthMutes.Changed -= onMutes;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(IJSRuntime js, CancellationToken cancellationToken)
    {
        Volatile.Write(ref started, 1);

        // The browser's muted findings, once per circuit - a JS call, not a query - so the health badge counts
        // only what this browser has not muted. Its Changed event re-reads the badge.
        await healthMutes.LoadAsync(js, cancellationToken);

        // The first interactive look at the health glance only takes note of what is already Critical: those
        // are on screen already, and a toast saying they just appeared would be false.
        await healthAlerts.ObserveAsync(state.ActiveScope, Volatile.Read(ref lastGlance));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The poll runs on the thread pool and a scope change on the renderer, and both write the last read. So each hands the toast the glance of
    /// the read it made itself, never the shared field, together with the scope the visitor is on now; <see cref="HealthAlerts" /> ignores a
    /// glance stamped with any other scope, so a read that the scope outran is not a first look. The host's first read, and one a mutes change
    /// causes, take note of nothing; the poll and a scope change after the start do.
    /// </remarks>
    public async ValueTask<IReadOnlyDictionary<string, StudioNavBadge>?> ReadBadgesAsync(CancellationToken cancellationToken)
    {
        // Said before the read starts: which read this is, never which one landed first.
        bool first = Interlocked.Exchange(ref firstReadPending, 0) == 1;
        bool forMutes = Interlocked.Exchange(ref readForMutes, 0) == 1;

        if (await RefreshIndicatorsAsync(cancellationToken) is not { } read)
        {
            return null;
        }

        if (!first && !forMutes && Volatile.Read(ref started) == 1)
        {
            await healthAlerts.ObserveAsync(state.ActiveScope, read.Health);
        }

        return Badges(read);
    }

    /// <summary>
    /// Reads the indicators for the active scope, treats every failure as "no badge", and answers what it read - or
    /// <see langword="null" /> when the read was cancelled or the scope changed while it was in flight.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The service already answers <c>NavIndicators.None</c> rather than throwing; this is the second
    /// belt, because the one thing the navigation must never do is stop rendering.
    /// </para>
    /// <para>
    /// A read the scope outran is not drawn: the scope change that outran it starts a read of its own for the new
    /// scope, and a badge of the old database's counts under the new one would be wrong until the next poll.
    /// </para>
    /// </remarks>
    private async Task<NavIndicators?> RefreshIndicatorsAsync(CancellationToken cancellationToken)
    {
        StudioScope? readFor = state.ActiveScope;
        NavIndicators read;

        try
        {
            read = await indicators.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            read = NavIndicators.None;
        }

        if (state.ActiveScope != readFor)
        {
            return null;
        }

        Volatile.Write(ref lastGlance, read.Health);
        return read;
    }

    /// <summary>
    /// What the rail draws of a read, by the key its entries name: the health glance, drawn from the glance, and the badges the indicator
    /// service read for the other keys.
    /// </summary>
    private IReadOnlyDictionary<string, StudioNavBadge> Badges(NavIndicators read)
    {
        // A count of what this visitor may see, coloured by the worst of it. Findings they are not shown are said in
        // the tooltip and never counted or drawn on their own: their severity is the gate's to withhold, and a badge
        // that cannot say how bad would sit on every page of a host whose visitors are short of a gate. An absent badge
        // is no claim that all is well; the Health page and the Overview say what is withheld.
        if (read.Health is not { Worst: not null } health)
        {
            return read.Badges.ContainsKey(StudioNavBadgeKeys.Health) ? Without(read.Badges) : read.Badges;
        }

        Dictionary<string, StudioNavBadge> badges = new(read.Badges, StringComparer.Ordinal)
        {
            [StudioNavBadgeKeys.Health] = new StudioNavBadge(
                (health.Critical + health.Warning).ToString("N0", CultureInfo.InvariantCulture),
                health.Critical > 0 ? "ms-nav-badge-danger" : "ms-nav-badge-warning",
                HealthBadgeTitle(health))
            {
                // The title holds a time ("as of 14:05"): spelt again whenever the rail draws, so the visitor's zone follows with no read.
                TitleOf = () => HealthBadgeTitle(health),
            },
        };

        return badges;
    }

    private static Dictionary<string, StudioNavBadge> Without(IReadOnlyDictionary<string, StudioNavBadge> badges)
    {
        Dictionary<string, StudioNavBadge> copy = new(badges, StringComparer.Ordinal);
        copy.Remove(StudioNavBadgeKeys.Health);
        return copy;
    }

    /// <summary>The health badge's tooltip and accessible name: what it counts, what it leaves out, and as of when.</summary>
    private string HealthBadgeTitle(HealthGlance health)
    {
        List<string> parts = [];

        if (health.Critical > 0)
        {
            parts.Add(health.Critical == 1 ? "1 critical finding" : string.Create(CultureInfo.InvariantCulture, $"{health.Critical:N0} critical findings"));
        }

        if (health.Warning > 0)
        {
            parts.Add(health.Warning == 1 ? "1 warning" : string.Create(CultureInfo.InvariantCulture, $"{health.Warning:N0} warnings"));
        }

        string withheld = health.Withheld switch
        {
            0 => string.Empty,
            1 => ", and 1 finding not shown to you",
            _ => string.Create(CultureInfo.InvariantCulture, $", and {health.Withheld:N0} findings not shown to you"),
        };

        return string.Join(" and ", parts) + withheld + ", as of " + preferences.FormatInSelectedTimeZone(health.MeasuredAt, "HH:mm");
    }
}
