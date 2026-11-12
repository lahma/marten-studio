using System.Globalization;

using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

/// <summary>
/// What this circuit is currently about, and the authorization-filtered listings it may pick from.
/// </summary>
/// <remarks>
/// <para>
/// Scoped: the active store, database and tenant are a browser tab's, not the process's. Every listing
/// here came from <see cref="IStudioScopeCatalog" /> and has already been through
/// <see cref="StudioAuthorization" />, which is what makes it the set <see cref="SetScopeAsync" />
/// validates against - a <c>change</c> event arrives from a browser and Blazor does not check that its
/// value was one of the options the server rendered, so without that validation a client could name any
/// store in the process and every subscribed page would re-read for it.
/// </para>
/// <para>
/// The theme and the time zone are seeded from cookies in the constructor rather than fetched from
/// <c>localStorage</c> after the first render, so a dark studio renders dark on the server and there is
/// no flash of the light theme during prerendering.
/// </para>
/// </remarks>
internal sealed class StudioState
{
    /// <summary>The cookie and <c>localStorage</c> key the theme is remembered under.</summary>
    public const string ThemePreferenceKey = "ms_theme";

    /// <summary>The cookie and <c>localStorage</c> key the time zone is remembered under.</summary>
    public const string TimeZonePreferenceKey = "ms_tz";

    /// <summary>
    /// The most databases, the usual one excluded, that <see cref="SetScopeAsync" /> asks for a tenant when an address names a tenant and no
    /// database. Each one is a tenant listing, which may be a query; past the bound the search stops and the address is applied as if
    /// nothing had been found.
    /// </summary>
    public const int MaxTenantDatabaseSearch = 32;

    /// <summary>9229: the tenant search stopped at <see cref="MaxTenantDatabaseSearch" /> databases, at Debug and naming no tenant.</summary>
    private static readonly EventId TenantDatabaseSearchBounded = new(9229, nameof(TenantDatabaseSearchBounded));

    private readonly IStudioScopeCatalog catalog;
    private readonly ILogger<StudioState> logger;
    private readonly StudioProduct product;
    private readonly TimeProvider clock;

    private string selectedTheme = "system";
    private string selectedTimeZoneId = TimeZoneInfo.Local.Id;
    private long preferenceVersion;
    private bool initialized;
    private StudioScope? applyingScope;
    private long applyingGeneration;

    /// <param name="catalog">The sources, databases and tenants this visitor may see.</param>
    /// <param name="logger">Where a page's failed scope-change handler goes.</param>
    /// <param name="httpContextAccessor">The request that opened the circuit, for the remembered preferences.</param>
    /// <param name="settings">
    /// The studio's settings, whose words that line is logged in - optional so that a test can build the state by hand;
    /// the container always supplies them (<see cref="StudioText.Of" />).
    /// </param>
    /// <param name="timeProvider">
    /// The clock "today" is taken from when a moment is spelt (<see cref="Clock" />): the host's own <see cref="TimeProvider" /> when
    /// it registered one, the system's otherwise.
    /// </param>
    public StudioState(
        IStudioScopeCatalog catalog,
        ILogger<StudioState> logger,
        IHttpContextAccessor httpContextAccessor,
        IStudioSettings? settings = null,
        TimeProvider? timeProvider = null)
    {
        this.catalog = catalog;
        this.logger = logger;
        product = StudioText.Of(settings).Product;
        clock = timeProvider ?? TimeProvider.System;

        HttpContext? httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        string? themeCookie = httpContext.Request.Cookies[ThemePreferenceKey];
        if (!string.IsNullOrWhiteSpace(themeCookie))
        {
            selectedTheme = NormalizeTheme(themeCookie);
        }

        string? timeZoneCookie = httpContext.Request.Cookies[TimeZonePreferenceKey];
        if (!string.IsNullOrWhiteSpace(timeZoneCookie))
        {
            selectedTimeZoneId = NormalizeTimeZoneId(timeZoneCookie);
        }
    }

    /// <summary>
    /// Raised after the active scope changes, so every subscribed page re-reads for the new one.
    /// </summary>
    /// <remarks>
    /// A <see cref="Func{TResult}" /> returning a <see cref="Task" /> rather than an
    /// <see cref="EventHandler" />: a handler that has to re-query cannot be synchronous, and the
    /// <c>async void</c> handler an <c>EventHandler</c> forces is the one shape whose exception kills a
    /// Blazor circuit silently (AGENTS.md hard rule 6).
    /// </remarks>
    public event Func<Task>? ScopeChanged;

    /// <summary>
    /// Raised, synchronously and on the caller's thread, the moment the scope the studio's <em>links</em> should carry
    /// (<see cref="LinkScope" />) moves: when a scope starts to be applied and again when it becomes the active one (1.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A second event beside <see cref="ScopeChanged" />, because that one is awaited one handler after another.</b> The shell's
    /// own handler is first in line and reads the authorization policy before it returns, so the rail's handler ran a policy read
    /// and a round trip after the scope moved, and for that long a rail link a visitor clicked carried the old scope. This event is
    /// raised before anything is awaited and its handlers must not wait for anything: they ask for a render and return. It is an
    /// <see cref="Action" /> because it is synchronous by contract; a handler is a method, never an <c>async</c> lambda (hard rule 6).
    /// </para>
    /// <para>
    /// Raised only when <see cref="LinkScope" /> actually changed, so a page that re-validates the address it was opened with
    /// (<see cref="SetScopeAsync" /> with the scope the studio is already on) draws nothing.
    /// </para>
    /// </remarks>
    public event Action? LinkScopeChanged;

    /// <summary>The store, database and tenant every page on this circuit reads for.</summary>
    public StudioScope? ActiveScope { get; private set; }

    /// <summary>
    /// The scope a link built now should carry: the one <see cref="SetScopeAsync" /> is applying while it reads the catalog, and
    /// otherwise <see cref="ActiveScope" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applying a scope is a few awaited reads (the new store's databases, the database's tenants), and until they finish
    /// <see cref="ActiveScope" /> is still the old one. A link drawn from it in that window - the rail's - sends a visitor who
    /// clicks it to the old store, and the shell then follows the click's address back (<c>StudioLayout</c> applies the scope of an
    /// intercepted link). So a link is drawn from what is being applied: the scope a pasted address names while the first load
    /// reads, the scope a picker was just moved to while the picker's change reads. The values are the request's, filled in from
    /// the active scope where the request leaves a part out and checked against what the last listings carry where that needs no
    /// await; a name the final resolution replaces (a database the new store does not list) is replaced by the same rule when the
    /// scope is applied, as it is when the address is followed.
    /// </para>
    /// <para>
    /// For links only. Authorization, data reads and the address bar's rewrite all read <see cref="ActiveScope" />, which moves
    /// only when the scope really has.
    /// </para>
    /// </remarks>
    public StudioScope? LinkScope => applyingScope ?? ActiveScope;

    /// <summary>Every store the visitor may see, unavailable ones included and marked.</summary>
    public IReadOnlyList<StoreListing> AvailableStores { get; private set; } = [];

    /// <summary>
    /// The sources the visitor may see whose databases could not be listed (1.2): not selectable, so not in <see cref="AvailableStores" />
    /// and not in the header's picker, but known - the Stores page and the Overview's strip name them, with the reason, rather than let
    /// them vanish. Only sources the source policy passes for the source as a whole.
    /// </summary>
    public IReadOnlyList<StoreListing> UnlistableStores { get; private set; } = [];

    /// <summary>
    /// How many sources the visitor may see, the unlistable ones included: what decides whether the sidebar offers the Stores page.
    /// </summary>
    public int KnownStoreCount => AvailableStores.Count + UnlistableStores.Count;

    /// <summary>Every database of the active store the visitor may see.</summary>
    public IReadOnlyList<DatabaseListing> AvailableDatabases { get; private set; } = [];

    /// <summary>The tenants of the active store and database.</summary>
    public TenantList AvailableTenants { get; private set; } = TenantList.Unavailable;

    /// <summary>
    /// How many databases the active store has, which decides whether a database picker is shown at all
    /// and whether it needs a refresh button.
    /// </summary>
    public StudioDatabaseCardinality Cardinality { get; private set; } = StudioDatabaseCardinality.Single;

    /// <summary>
    /// Whether anything in the active store is tenanted per row. A tenant picker over a store with no
    /// conjoined types would be a control that changes nothing.
    /// </summary>
    public bool ShowTenantSelector { get; private set; }

    /// <summary>What the active store said when it would not build, or <see langword="null" />.</summary>
    public string? ActiveStoreUnavailableMessage { get; private set; }

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>.</summary>
    public string SelectedTheme
    {
        get => selectedTheme;
        set => Set(ref selectedTheme, NormalizeTheme(value));
    }

    /// <summary>The time zone every timestamp is rendered in.</summary>
    public string SelectedTimeZoneId
    {
        get => selectedTimeZoneId;
        set => Set(ref selectedTimeZoneId, NormalizeTimeZoneId(value));
    }

    /// <summary>
    /// How many times <see cref="SelectedTheme" /> or <see cref="SelectedTimeZoneId" /> has actually
    /// moved on this circuit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sequence number rather than a timestamp, and it exists for exactly one reason: reading the
    /// browser's remembered preferences is two awaited JS round trips, and on a slow browser the visitor
    /// can pick a theme while those are in flight. Without a stamp taken before the awaits and checked
    /// after them, the answer that comes back - which was read <em>before</em> the click, so it is the
    /// preference the visitor has just moved away from - is written over the one they chose, and the
    /// picker snaps back on its own a moment after they used it.
    /// </para>
    /// <para>
    /// Only a real change counts. Re-selecting the theme that is already on changes nothing and needs no
    /// protecting, and counting it would make a stored time zone lose a race it never entered.
    /// </para>
    /// </remarks>
    public long PreferenceVersion => Volatile.Read(ref preferenceVersion);

    /// <summary>Builds the listings and picks an opening scope, once per circuit.</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-reads the listings, keeping the active scope when it is still in them and falling back to the
    /// first thing the visitor may see when it is not.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        StudioScope? previous = ActiveScope;

        StoreListings listings = await catalog.ListStoresAsync(cancellationToken).ConfigureAwait(false);
        AvailableStores = listings.Listed;
        UnlistableStores = listings.Unlistable;

        StoreListing? store = FindStore(previous?.StoreKey) ?? (AvailableStores.Count > 0 ? AvailableStores[0] : null);
        if (store is null)
        {
            AvailableDatabases = [];
            ApplyFacts(StoreScopeFacts.None);
            await SetActiveScopeAsync(null, previous).ConfigureAwait(false);
            return;
        }

        AvailableDatabases = await catalog.ListDatabasesAsync(store.Key, cancellationToken).ConfigureAwait(false);

        string databaseId = PickDatabase(previous?.DatabaseId);
        ApplyFacts(await catalog.DescribeAsync(store.Key, databaseId, cancellationToken).ConfigureAwait(false));

        string? tenantId = TenantList.IsAcceptable(AvailableTenants, previous?.TenantId) ? NullIfBlank(previous?.TenantId) : null;

        await SetActiveScopeAsync(new StudioScope(store.Key, databaseId, tenantId), previous).ConfigureAwait(false);
    }

    /// <summary>
    /// Points the studio at a store, database and tenant, ignoring anything the last listing did not
    /// carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silently rather than loudly: the values arrive from a <c>change</c> event, and a browser that names
    /// something it was not offered is either a stale tab or someone probing. Neither deserves an error
    /// message that says which of the three was wrong.
    /// </para>
    /// <para>
    /// <b>A database named with a new store is kept</b> when the new store lists it, so a pasted link to another
    /// store's database reopens that database (D9) rather than the store's first. A name the new store does not list
    /// falls back to its first database, as any unknown name does; the scope selector names none when the visitor
    /// picks another store, so picking one always opens its first.
    /// </para>
    /// <para>
    /// <b>A tenant the new listing does not carry</b> falls back to the previous scope's tenant only if the new store and
    /// database accept that one too; otherwise to all tenants.
    /// </para>
    /// <para>
    /// <b>A tenant named with no database</b> (a link from another page or a module knows a store and a tenant, D44) that the database
    /// resolved as above does not list is looked for in the store's other databases, in listing order and at most
    /// <see cref="MaxTenantDatabaseSearch" /> of them: the first whose tenant listing carries it becomes the database. Only the listings
    /// the visitor already sees are asked - <see cref="AvailableDatabases" /> and <see cref="IStudioScopeCatalog.DescribeAsync" />, which
    /// are already the policy's answer per database and per tenant - so nothing is revealed and no tenancy is enumerated. A tenant found
    /// nowhere is dropped, as a tenant the listing does not carry always was. An address that names a database is applied exactly (D9).
    /// </para>
    /// </remarks>
    public async Task SetScopeAsync(
        string? storeKey,
        string? databaseId,
        string? tenantId,
        CancellationToken cancellationToken = default)
    {
        StudioScope? previous = ActiveScope;

        StoreListing? store = FindStore(storeKey ?? previous?.StoreKey);
        if (store is null)
        {
            // Nothing is applied, and nothing is ended here: a pending scope that is standing belongs to whoever began it (the first
            // load, which ends its own in a finally), not to a call that applied nothing.
            return;
        }

        bool storeChanged = !string.Equals(store.Key, previous?.StoreKey, StringComparison.OrdinalIgnoreCase);

        // Before the first await: from here until the scope is the active one, a link is drawn from the scope being applied.
        long generation = SetApplying(PendingScopeFor(store.Key, databaseId, tenantId, previous, storeChanged));

        try
        {
            if (storeChanged)
            {
                AvailableDatabases = await catalog.ListDatabasesAsync(store.Key, cancellationToken).ConfigureAwait(false);
            }

            string resolvedDatabaseId = PickDatabase(databaseId ?? (storeChanged ? null : previous?.DatabaseId));

            if (storeChanged || !string.Equals(resolvedDatabaseId, previous?.DatabaseId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyFacts(await catalog.DescribeAsync(store.Key, resolvedDatabaseId, cancellationToken).ConfigureAwait(false));
            }

            // An address that names a tenant and no database, whose usual database does not list that tenant: in a store that keeps
            // each tenant in a database of its own the tenant is in another one, so look for it among the databases the visitor may see.
            if (NamesTenantWithoutDatabase(databaseId, tenantId) && !ListsTenant(AvailableTenants, tenantId)
                && await FindTenantDatabaseAsync(store.Key, resolvedDatabaseId, tenantId!, cancellationToken).ConfigureAwait(false) is { } found)
            {
                resolvedDatabaseId = found.DatabaseId;
                ApplyFacts(found.Facts);
            }

            // A tenant the listing does not carry falls back to the previous one only while this store and database still
            // accept it: after a store or database change the previous tenant is the old scope's, and is checked against the
            // new listing like any other name - or dropped, as RefreshAsync drops one.
            string? resolvedTenantId = TenantList.IsAcceptable(AvailableTenants, tenantId)
                ? NullIfBlank(tenantId)
                : TenantList.IsAcceptable(AvailableTenants, previous?.TenantId) ? NullIfBlank(previous?.TenantId) : null;

            await SetActiveScopeAsync(new StudioScope(store.Key, resolvedDatabaseId, resolvedTenantId), previous, generation).ConfigureAwait(false);
        }
        finally
        {
            // A read that threw or was cancelled applied nothing: the links go back to the scope that is still the active one.
            // (A no-op after the scope was applied: SetActiveScopeAsync ended it before it notified anyone.) Only this call's own: a
            // later call that began applying another scope meanwhile keeps its own pending scope.
            EndApplying(generation);
        }
    }

    /// <summary>
    /// Says, before anything is read, that the shell is about to apply the scope a pasted address names, so the links drawn while
    /// the listings load carry it instead of the scope the studio is still on (1.2). The first load calls it with the address's
    /// parts; <see cref="SetScopeAsync" /> replaces it with its own, resolved against the listings, and every way out of the first
    /// load ends it (<see cref="EndApplying" />). A part the address does not name is left out of the links, never defaulted.
    /// </summary>
    /// <param name="storeKey">The address's source key, or <see langword="null" />.</param>
    /// <param name="databaseId">The address's database, or <see langword="null" />.</param>
    /// <param name="tenantId">The address's tenant, or <see langword="null" />.</param>
    /// <returns>The generation to hand <see cref="EndApplying" />, or 0 when the address names nothing and nothing was begun.</returns>
    public long BeginApplying(string? storeKey, string? databaseId, string? tenantId)
    {
        if (storeKey is null && databaseId is null && tenantId is null)
        {
            return 0;
        }

        return SetApplying(new StudioScope(storeKey ?? ActiveScope?.StoreKey ?? string.Empty, databaseId ?? string.Empty, NullIfBlank(tenantId)));
    }

    /// <summary>
    /// Ends the pending scope <paramref name="generation" /> began: links are drawn from <see cref="ActiveScope" /> again. A no-op when a
    /// later call has begun another pending scope since (or this one has already ended), so one call's exit never clears another's.
    /// </summary>
    /// <param name="generation">What <see cref="BeginApplying" /> or the call applying a scope was given; 0 ends nothing.</param>
    public void EndApplying(long generation)
    {
        if (generation == 0 || generation != Volatile.Read(ref applyingGeneration) || applyingScope is null)
        {
            return;
        }

        StudioScope? before = LinkScope;
        applyingScope = null;
        RaiseLinkScopeChanged(before);
    }

    /// <summary>
    /// The scope a link should carry while <see cref="SetScopeAsync" /> is still reading, built without an await from the request
    /// and the active scope with the same rules the final resolution uses where the listings already in memory can apply them.
    /// </summary>
    private StudioScope PendingScopeFor(string storeKey, string? databaseId, string? tenantId, StudioScope? previous, bool storeChanged)
    {
        // The new store's databases are not read yet, so a database named with it is the request's word; with the same store
        // the listing is the current one and the name is resolved as PickDatabase will.
        //
        // A tenant named with no database that the database on screen does not list is being looked for in the others (SetScopeAsync): until
        // it is found the database is the request's - empty, as it named none - never the database on screen with a tenant that is not in it.
        string pendingDatabase = storeChanged
            ? NullIfBlank(databaseId) ?? string.Empty
            : NamesTenantWithoutDatabase(databaseId, tenantId) && AvailableDatabases.Count > 1 && !ListsTenant(AvailableTenants, tenantId)
                ? string.Empty
                : PickDatabase(databaseId ?? previous?.DatabaseId);

        bool databaseChanged = storeChanged || !string.Equals(pendingDatabase, previous?.DatabaseId, StringComparison.OrdinalIgnoreCase);

        // The current tenant listing answers only for the scope it was read for. After a store or database change the request's
        // tenant is taken as named: it is checked against the new listing when the scope is applied.
        string? pendingTenant = databaseChanged
            ? NullIfBlank(tenantId)
            : TenantList.IsAcceptable(AvailableTenants, tenantId)
                ? NullIfBlank(tenantId)
                : TenantList.IsAcceptable(AvailableTenants, previous?.TenantId) ? NullIfBlank(previous?.TenantId) : null;

        return new StudioScope(storeKey, pendingDatabase, pendingTenant);
    }

    private static bool NamesTenantWithoutDatabase(string? databaseId, string? tenantId) =>
        string.IsNullOrWhiteSpace(databaseId) && !string.IsNullOrWhiteSpace(tenantId);

    /// <summary>
    /// Whether the listing names <paramref name="tenantId" />. Not <see cref="TenantList.IsAcceptable" />, which also takes any plausible id
    /// from a truncated listing - and a listing a policy filtered is always marked truncated - so it says nothing about where a tenant is.
    /// </summary>
    private static bool ListsTenant(TenantList tenants, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        foreach (string id in tenants.Ids)
        {
            if (string.Equals(id, tenantId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The first database of the store, in listing order and the usual one excluded, whose tenant listing carries
    /// <paramref name="tenantId" />, with the facts that listing came from; <see langword="null" /> when none does.
    /// </summary>
    /// <remarks>
    /// Asks only <see cref="IStudioScopeCatalog.DescribeAsync" />, the scope picker's own path (the databases the visitor may see, and for
    /// each the tenants the policy lets them address). A database whose tenants could not be read answers <see cref="TenantList.Unavailable" />
    /// and is skipped; so is one whose listing throws. At most <see cref="MaxTenantDatabaseSearch" /> databases are asked.
    /// </remarks>
    private async Task<(string DatabaseId, StoreScopeFacts Facts)?> FindTenantDatabaseAsync(
        string storeKey,
        string usualDatabaseId,
        string tenantId,
        CancellationToken cancellationToken)
    {
        int searched = 0;

        foreach (DatabaseListing database in AvailableDatabases.ToArray())
        {
            if (string.Equals(database.Identity, usualDatabaseId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (searched == MaxTenantDatabaseSearch)
            {
                // The tenant is named nowhere in the first databases, and the rest are not asked: the address is applied as if it were found nowhere.
                if (!logger.IsEnabled(LogLevel.Debug))
                {
                    return null;
                }

                if (product == StudioProduct.PostgresStudio)
                {
                    logger.LogDebug(TenantDatabaseSearchBounded, "Postgres Studio looked for a tenant in {Count} databases of data source {StoreKey} and stopped at that bound", searched, storeKey);
                }
                else
                {
                    logger.LogDebug(TenantDatabaseSearchBounded, "Marten Studio looked for a tenant in {Count} databases of store {StoreKey} and stopped at that bound", searched, storeKey);
                }

                return null;
            }

            searched++;

            StoreScopeFacts facts;
            try
            {
                facts = await catalog.DescribeAsync(storeKey, database.Identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The real catalog never throws here (it logs and answers an unavailable listing); a catalog that does is that database's failure,
                // not the address's.
                continue;
            }

            if (ListsTenant(facts.Tenants, tenantId))
            {
                return (database.Identity, facts);
            }
        }

        return null;
    }

    private long SetApplying(StudioScope scope)
    {
        StudioScope? before = LinkScope;
        long generation = Interlocked.Increment(ref applyingGeneration);
        applyingScope = scope;
        RaiseLinkScopeChanged(before);
        return generation;
    }

    /// <summary>
    /// Raises <see cref="LinkScopeChanged" /> when the links' scope moved, containing a handler that throws: the invocation list is
    /// walked by hand, as <see cref="NotifyAsync" /> walks its own, so one subscriber's failure neither stops the next nor unwinds
    /// into the scope change it was told about (which would leave the scope moved without <see cref="ScopeChanged" /> raised, or a
    /// pending scope standing for good).
    /// </summary>
    private void RaiseLinkScopeChanged(StudioScope? before)
    {
        if (LinkScope == before)
        {
            return;
        }

        Delegate[] handlers = LinkScopeChanged?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try
            {
                ((Action) handler)();
            }
            catch (Exception exception)
            {
                if (product == StudioProduct.PostgresStudio)
                {
                    logger.LogError(exception, "A Postgres Studio page failed to handle a change of the scope its links carry");
                }
                else
                {
                    logger.LogError(exception, "A Marten Studio page failed to handle a change of the scope its links carry");
                }
            }
        }
    }

    /// <summary>Re-reads everything from scratch, for the refresh button beside a dynamic database list.</summary>
    public async Task RefreshScopeListingsAsync(CancellationToken cancellationToken = default)
    {
        catalog.Invalidate();
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Tells every subscriber the scope moved.
    /// </summary>
    /// <remarks>
    /// The invocation list is walked by hand so that one page throwing does not stop the next page from
    /// being told - a stale page is a bug, a page that never hears about the switch is a page showing
    /// another tenant's data.
    /// </remarks>
    public async Task NotifyAsync()
    {
        Delegate[] handlers = ScopeChanged?.GetInvocationList() ?? [];
        foreach (Delegate handler in handlers)
        {
            try
            {
                await ((Func<Task>) handler)().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (product == StudioProduct.PostgresStudio)
                {
                    logger.LogError(exception, "A Postgres Studio page failed to handle a scope change");
                }
                else
                {
                    logger.LogError(exception, "A Marten Studio page failed to handle a scope change");
                }
            }
        }
    }

    /// <summary>What the last listing said about this store key, or <see langword="null" />.</summary>
    public StoreListing? FindStore(string? storeKey)
    {
        if (string.IsNullOrWhiteSpace(storeKey))
        {
            return null;
        }

        foreach (StoreListing store in AvailableStores)
        {
            if (string.Equals(store.Key, storeKey, StringComparison.OrdinalIgnoreCase))
            {
                return store;
            }
        }

        return null;
    }

    /// <summary>The clock a page takes "now" and "today" from: the host's <see cref="TimeProvider" /> if one is registered, else the system's.</summary>
    public TimeProvider Clock => clock;

    /// <summary><paramref name="value" /> in the selected time zone.</summary>
    public DateTimeOffset ConvertToSelectedTimeZone(DateTimeOffset value) =>
        TimeZoneInfo.ConvertTime(value, ResolveSelectedTimeZone());

    /// <summary><paramref name="value" /> formatted in the selected time zone.</summary>
    public string FormatInSelectedTimeZone(DateTimeOffset value, string format = "u")
    {
        DateTimeOffset converted = ConvertToSelectedTimeZone(value);
        string outputFormat = string.Equals(format, "u", StringComparison.Ordinal) ? "yyyy-MM-dd HH:mm:ss zzz" : format;
        return converted.ToString(outputFormat, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc cref="FormatInSelectedTimeZone(DateTimeOffset, string)" />
    public string FormatInSelectedTimeZone(DateTimeOffset? value, string format = "u") =>
        value.HasValue ? FormatInSelectedTimeZone(value.Value, format) : "n/a";

    private string PickDatabase(string? preferred)
    {
        foreach (DatabaseListing database in AvailableDatabases)
        {
            if (string.Equals(database.Identity, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return database.Identity;
            }
        }

        return AvailableDatabases.Count > 0 ? AvailableDatabases[0].Identity : string.Empty;
    }

    private void ApplyFacts(StoreScopeFacts facts)
    {
        Cardinality = facts.Cardinality;
        ShowTenantSelector = facts.ShowTenantSelector;
        AvailableTenants = facts.Tenants;
        ActiveStoreUnavailableMessage = facts.UnavailableMessage;
    }

    private async Task SetActiveScopeAsync(StudioScope? scope, StudioScope? previous, long endsGeneration = 0)
    {
        // The links move first and synchronously (LinkScopeChanged); only then does the awaited ScopeChanged walk its handlers. The
        // scope being applied ends here when it is the one that has just become active, and stands when a refresh of the listings
        // sets the opening scope while a pasted address's own is still to be applied.
        StudioScope? linkBefore = LinkScope;
        ActiveScope = scope;
        if (endsGeneration != 0 && endsGeneration == Volatile.Read(ref applyingGeneration))
        {
            applyingScope = null;
        }

        RaiseLinkScopeChanged(linkBefore);

        if (scope != previous)
        {
            await NotifyAsync().ConfigureAwait(false);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Assigns one preference and counts it, so <see cref="PreferenceVersion" /> says whether the visitor
    /// has expressed a preference since some earlier moment.
    /// </summary>
    private void Set(ref string field, string value)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        Interlocked.Increment(ref preferenceVersion);
    }

    private TimeZoneInfo ResolveSelectedTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(selectedTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string NormalizeTimeZoneId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeZoneInfo.Local.Id;
        }

        string candidate = value.Trim();
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(candidate);
            return candidate;
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local.Id;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local.Id;
        }
    }

    private static string NormalizeTheme(string? value)
    {
        if (string.Equals(value, "light", StringComparison.OrdinalIgnoreCase))
        {
            return "light";
        }

        return string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "system";
    }
}
