using JasperFx.Events;

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
/// <b>The tenant.</b> A store that keeps its tenants apart in any way (conjoined tables, a database per tenant, a tenancy the studio
/// cannot classify) is given the tenant the caller knows; with none the answer is <see langword="null" />, because the page would
/// open the visitor's current tenant and say "not found" falsely (OPEN-8). A store with no tenants ignores it. The link carries
/// <c>store</c> and <c>tenant</c> and never <c>db</c>.
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
    /// The tenant the link carries. A store with no tenants carries none and always answers; a store with tenants answers only
    /// when the caller knows one.
    /// </summary>
    private static bool TenantFor(IDocumentStore store, MartenStudioOptions current, string? tenantId, out string? tenant)
    {
        tenant = null;

        if (StoreTenancy.Classify(store.Options, current.IsDocumentTypeVisible).Kind == StoreTenancyKind.Single)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        tenant = tenantId;
        return true;
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
