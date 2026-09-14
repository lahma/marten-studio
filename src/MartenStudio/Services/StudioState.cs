using System.Globalization;

using JasperFx.Descriptors;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

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

    private readonly IStudioScopeCatalog catalog;
    private readonly ILogger<StudioState> logger;

    private string selectedTheme = "system";
    private string selectedTimeZoneId = TimeZoneInfo.Local.Id;
    private bool initialized;

    public StudioState(
        IStudioScopeCatalog catalog,
        ILogger<StudioState> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        this.catalog = catalog;
        this.logger = logger;

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

    /// <summary>The store, database and tenant every page on this circuit reads for.</summary>
    public StudioScope? ActiveScope { get; private set; }

    /// <summary>Every store the visitor may see, unavailable ones included and marked.</summary>
    public IReadOnlyList<StoreListing> AvailableStores { get; private set; } = [];

    /// <summary>Every database of the active store the visitor may see.</summary>
    public IReadOnlyList<DatabaseListing> AvailableDatabases { get; private set; } = [];

    /// <summary>The tenants of the active store and database.</summary>
    public TenantList AvailableTenants { get; private set; } = TenantList.Unavailable;

    /// <summary>
    /// How many databases the active store has, which decides whether a database picker is shown at all
    /// and whether it needs a refresh button.
    /// </summary>
    public DatabaseCardinality Cardinality { get; private set; } = DatabaseCardinality.Single;

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
        set => selectedTheme = NormalizeTheme(value);
    }

    /// <summary>The time zone every timestamp is rendered in.</summary>
    public string SelectedTimeZoneId
    {
        get => selectedTimeZoneId;
        set => selectedTimeZoneId = NormalizeTimeZoneId(value);
    }

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

        AvailableStores = await catalog.ListStoresAsync(cancellationToken).ConfigureAwait(false);

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

        string? tenantId = TenantDiscovery.IsAcceptable(AvailableTenants, previous?.TenantId) ? NullIfBlank(previous?.TenantId) : null;

        await SetActiveScopeAsync(new StudioScope(store.Key, databaseId, tenantId), previous).ConfigureAwait(false);
    }

    /// <summary>
    /// Points the studio at a store, database and tenant, ignoring anything the last listing did not
    /// carry.
    /// </summary>
    /// <remarks>
    /// Silently rather than loudly: the values arrive from a <c>change</c> event, and a browser that names
    /// something it was not offered is either a stale tab or someone probing. Neither deserves an error
    /// message that says which of the three was wrong.
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
            return;
        }

        bool storeChanged = !string.Equals(store.Key, previous?.StoreKey, StringComparison.OrdinalIgnoreCase);
        if (storeChanged)
        {
            AvailableDatabases = await catalog.ListDatabasesAsync(store.Key, cancellationToken).ConfigureAwait(false);
            databaseId = null;
        }

        string resolvedDatabaseId = PickDatabase(databaseId ?? (storeChanged ? null : previous?.DatabaseId));

        if (storeChanged || !string.Equals(resolvedDatabaseId, previous?.DatabaseId, StringComparison.OrdinalIgnoreCase))
        {
            ApplyFacts(await catalog.DescribeAsync(store.Key, resolvedDatabaseId, cancellationToken).ConfigureAwait(false));
        }

        string? resolvedTenantId = TenantDiscovery.IsAcceptable(AvailableTenants, tenantId)
            ? NullIfBlank(tenantId)
            : NullIfBlank(previous?.TenantId);

        await SetActiveScopeAsync(new StudioScope(store.Key, resolvedDatabaseId, resolvedTenantId), previous).ConfigureAwait(false);
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
                logger.LogError(exception, "A Marten Studio page failed to handle a scope change");
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

    private async Task SetActiveScopeAsync(StudioScope? scope, StudioScope? previous)
    {
        ActiveScope = scope;
        if (scope != previous)
        {
            await NotifyAsync().ConfigureAwait(false);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
