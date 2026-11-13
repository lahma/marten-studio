using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;

using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Services.Links;

/// <summary>
/// MartenStudio's <see cref="IStudioRouteLinks" />: a link from a module's page to a document or a stream of one of this
/// application's Marten stores (D-CORR 7.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure.</b> It reads the stores' <c>StoreOptions</c> and nothing else - never a connection, and never a call that ensures
/// storage (hard rule 14) - and it asks no policy: the page a link opens resolves its own scope and authorizes the visitor for it
/// again, so a link is never a grant. A store that will not build is skipped, as the pickers skip it.
/// </para>
/// <para>
/// <b>The type.</b> A name resolves to <c>(store, alias)</c> by the same visibility gate the Documents page opens a collection
/// through (<see cref="DocumentDataService.VisibleDocumentTypesOf" />): an alias first, then a CLR full name, then a CLR name, each
/// ordinal and ignoring case, over the root types and their subclasses. A hidden type is absent, and so answers
/// <see langword="null" /> exactly as a type nobody declared does (D27). A name two types of one store share at one level is
/// ambiguous, and so is <see langword="null" />.
/// </para>
/// <para>
/// <b>The store.</b> One store declares the name: that store. Several: the scope's store when it is one of them, else the default
/// store when it is, else <see langword="null" /> - the link would be a guess.
/// </para>
/// <para>
/// <b>The tenant.</b> A store with tenants is given the tenant the caller knows; with none the answer is <see langword="null" />,
/// because the page would open the visitor's current tenant and say "not found" falsely (OPEN-8). That holds for conjoined tenants
/// in one database, and for a store that keeps each tenant in a database of its own. A <b>static</b> tenancy always answers: the
/// host declared in configuration which database serves each tenant, and the page a link opens places the tenant from that declaration
/// (<see cref="PostgresStudio.Services.StudioState.SetScopeAsync" />, in memory, with no tenancy enumeration), whether or not rows are
/// tenanted - except where they are tenanted and the host listed <c>KnownTenantIds</c>, which make the database on screen "list" every
/// tenant so that the page never looks for it elsewhere. A master-table, sharded or one-server tenancy answers only when it also keeps its
/// rows per tenant (<see cref="StoreTenancy.KeepsRowsPerTenant" />) and the host listed no <c>KnownTenantIds</c>: the page looks for the
/// tenant in the tenant listings of the databases the visitor may see, and no database lists tenants where documents and events are
/// single-tenanted, so a link would reopen the id of the database on screen - and the only maps from a tenant to its database are a
/// table (reading it is a tenancy enumeration, hard rule 14) and one a one-server tenancy keeps private behind a call that creates
/// databases. A store with no tenants ignores it. A tenancy of the application's own, or one the configuration cannot place, gets no
/// link. The link carries <c>store</c> and <c>tenant</c> and never <c>db</c>.
/// </para>
/// </remarks>
internal sealed class MartenStudioRouteLinks : IStudioRouteLinks
{
    /// <summary>The longest stream key Marten's <c>stream_key</c> column holds.</summary>
    private const int MaxStreamKeyLength = 250;

    private readonly IOptions<MartenStudioOptions> options;
    private readonly MartenStoreRegistry registry;
    private readonly IServiceProvider provider;

    public MartenStudioRouteLinks(IOptions<MartenStudioOptions> options, MartenStoreRegistry registry, IServiceProvider provider)
    {
        this.options = options;
        this.registry = registry;
        this.provider = provider;
    }

    /// <inheritdoc />
    public string? ToDocument(StudioLinkScope scope, string documentType, string id)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrWhiteSpace(documentType) || string.IsNullOrEmpty(id))
        {
            return null;
        }

        MartenStudioOptions current = options.Value;

        List<Candidate> candidates = [];
        foreach ((MartenStoreRegistration registration, IDocumentStore store) in Stores(current))
        {
            if (AliasOf(store, documentType, current.IsDocumentTypeVisible) is { Found: true } match)
            {
                candidates.Add(new Candidate(registration.Key, store, match.Alias));
            }
        }

        if (Choose(candidates, scope.StoreKey) is not { Alias: { } alias } chosen
            || !TenantFor(chosen.Store, current, scope.TenantId, out string? tenant))
        {
            return null;
        }

        return DocumentLinks.ToDocument(current, chosen.StoreKey, tenant, alias, id);
    }

    /// <inheritdoc />
    public string? ToStream(StudioLinkScope scope, string streamIdOrKey)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrEmpty(streamIdOrKey))
        {
            return null;
        }

        MartenStudioOptions current = options.Value;

        List<Candidate> candidates = [];
        foreach ((MartenStoreRegistration registration, IDocumentStore store) in Stores(current))
        {
            if (StreamIdOf(store, streamIdOrKey) is { } streamId)
            {
                candidates.Add(new Candidate(registration.Key, store, streamId));
            }
        }

        if (Choose(candidates, scope.StoreKey) is not { Alias: { } canonicalId } chosen
            || !TenantFor(chosen.Store, current, scope.TenantId, out string? tenant))
        {
            return null;
        }

        return EventLinks.ToStream(current, chosen.StoreKey, tenant, canonicalId);
    }

    /// <summary>The stores the studio lists, the default first, those that build.</summary>
    private IEnumerable<(MartenStoreRegistration Registration, IDocumentStore Store)> Stores(MartenStudioOptions current)
    {
        foreach (MartenStoreRegistration registration in registry.Registrations(current.IncludeAncillaryStores))
        {
            if (registry.TryResolve(registration, provider, out IDocumentStore? store).IsAvailable && store is not null)
            {
                yield return (registration, store);
            }
        }
    }

    /// <summary>
    /// The one candidate the link is for: the only one, else the scope's store, else the default store; <see langword="null" />
    /// when none applies. A candidate whose name was ambiguous carries no alias, and so produces no link.
    /// </summary>
    private static Candidate? Choose(List<Candidate> candidates, string? scopeStoreKey)
    {
        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : null;
        }

        foreach (string? key in (string?[])[scopeStoreKey, MartenStoreRegistry.DefaultStoreKey])
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            foreach (Candidate candidate in candidates)
            {
                if (string.Equals(candidate.StoreKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The tenant the link carries, or whether there is a link at all. A single-database store with no tenants carries none and always
    /// answers. A store with tenants - conjoined in one database, or each in a database of its own (master table, sharded, one-server,
    /// static) - answers only when the caller knows one. A static tenancy then answers whether or not its rows are tenanted, because its
    /// configuration places the tenant in its database (ZW38), unless rows are tenanted and the host listed <c>KnownTenantIds</c>. A master-table, sharded or one-server tenancy answers only when it also keeps its rows per tenant
    /// (<see cref="StoreTenancy.KeepsRowsPerTenant" />) and the host listed no <c>KnownTenantIds</c>: the page a link opens takes a tenant
    /// with no database to the first database the visitor may see whose tenant listing carries it, and no database lists tenants
    /// otherwise. A tenancy of the application's own, or one the configuration would not classify, never answers.
    /// </summary>
    private static bool TenantFor(IDocumentStore store, MartenStudioOptions current, string? tenantId, out string? tenant)
    {
        if (!PlacesTenant(store, current, tenantId, out tenant))
        {
            return false;
        }

        // The id as Marten stores it (ForceLowerCase and its siblings), which is what a static tenancy's declaration and every tenant listing
        // hold: a record's header may spell it otherwise. Pure, as the rest of the builder is.
        if (tenant is not null)
        {
            tenant = store.Options.TenantIdStyle.MaybeCorrectTenantId(tenant);
        }

        return true;
    }

    private static bool PlacesTenant(IDocumentStore store, MartenStudioOptions current, string? tenantId, out string? tenant)
    {
        tenant = null;

        // Read from the configuration alone: the tenancy's type and its in-memory cardinality, never its databases (hard rule 14).
        switch (StoreTenancy.Classify(store.Options, current.IsDocumentTypeVisible).Kind)
        {
            case StoreTenancyKind.Single:
                return true;

            case StoreTenancyKind.Conjoined or StoreTenancyKind.ConjoinedPartitioned:
                if (string.IsNullOrWhiteSpace(tenantId))
                {
                    return false;
                }

                tenant = tenantId;
                return true;

            case StoreTenancyKind.Static:
                // The declaration is the source: MultiTenantedDatabases(...) says in memory which database serves each tenant, and the page
                // a link opens reads it (StudioState.SetScopeAsync's tenant search, IStudioSource.DatabasesDeclaringAsync) with no connection and
                // no enumeration, whether or not rows are tenanted. The link still carries no database. One case the search is never
                // started for: rows tenanted and the host's KnownTenantIds listed, when the database on screen "lists" every tenant and so
                // already seems to hold this one - there the page would keep it and a link would reopen the wrong database.
                if (string.IsNullOrWhiteSpace(tenantId)
                    || (current.KnownTenantIds.Count > 0 && StoreTenancy.KeepsRowsPerTenant(store.Options, current.IsDocumentTypeVisible)))
                {
                    return false;
                }

                tenant = tenantId;
                return true;

            case StoreTenancyKind.MasterTable or StoreTenancyKind.Sharded or StoreTenancyKind.OneServer:
                // The page finds a tenant's database only through the tenant listings of the databases, and a database lists tenants only
                // where rows are tenanted: with single-tenanted documents and events the tenant selector is off, every listing is
                // unavailable, and the link would reopen the same id in the database on screen - another tenant's record. A host's
                // KnownTenantIds make every database "list" every tenant, so they say nothing about where one is.
                if (string.IsNullOrWhiteSpace(tenantId)
                    || current.KnownTenantIds.Count > 0
                    || !StoreTenancy.KeepsRowsPerTenant(store.Options, current.IsDocumentTypeVisible))
                {
                    return false;
                }

                tenant = tenantId;
                return true;

            default:
                // Custom, which includes a tenancy that would not say: fail closed.
                return false;
        }
    }

    /// <summary>The alias a name resolves to in one store, by alias, then CLR full name, then CLR name.</summary>
    private static AliasMatch AliasOf(IDocumentStore store, string name, Func<Type, bool>? isVisible)
    {
        List<(string Alias, Type Clr)> types = [];

        foreach (IDocumentType documentType in DocumentDataService.VisibleDocumentTypesOf(store, isVisible))
        {
            types.Add((documentType.Alias, documentType.DocumentType));

            foreach (SubClassMapping subclass in documentType.SubClasses)
            {
                types.Add((subclass.Alias, subclass.DocumentType));
            }
        }

        foreach (Func<(string Alias, Type Clr), string?> level in (Func<(string Alias, Type Clr), string?>[])
        [
            static x => x.Alias,
            static x => x.Clr.FullName,
            static x => x.Clr.Name,
        ])
        {
            string[] matches =
            [
                .. types
                    .Where(x => string.Equals(level(x), name, StringComparison.OrdinalIgnoreCase))
                    .Select(static x => x.Alias)
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ];

            if (matches.Length == 1)
            {
                return new AliasMatch(true, matches[0]);
            }

            if (matches.Length > 1)
            {
                // Two types share the name at this level: not a guess a link may make.
                return new AliasMatch(true, null);
            }
        }

        return new AliasMatch(false, null);
    }

    /// <summary>The stream id as the store's identity writes it, or <see langword="null" /> when the store keeps no events or the value is not one.</summary>
    private static string? StreamIdOf(IDocumentStore store, string value)
    {
        var events = store.Options.Events;

        if (!events.AllKnownEventTypes().Any(static x => x.EventType != typeof(Archived)))
        {
            return null;
        }

        if (events.StreamIdentity == StreamIdentity.AsGuid)
        {
            return Guid.TryParse(value, out Guid guid) ? guid.ToString("D") : null;
        }

        return value.Length is >= 1 and <= MaxStreamKeyLength ? value : null;
    }

    private sealed record Candidate(string StoreKey, IDocumentStore Store, string? Alias);

    private readonly record struct AliasMatch(bool Found, string? Alias);
}
