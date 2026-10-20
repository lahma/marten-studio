using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Marten;
using Marten.Storage;

using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IDatabaseDeclarationsSource" />: every registered store's declarations, read from each
/// store's options by <see cref="SchemaDeclarationReader" />, and every other store's policies for the database the
/// visitor's scope resolved to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Fail closed.</b> A store that will not build, or whose declarations throw, is a store whose tables would all
/// read as "Other" - rows included. So one failure is a failure of the whole classification rather than a smaller
/// map - named after the store only for a visitor that store's store policy passes
/// (<see cref="DatabaseAccess.DescribeFailureAsync" />), because another store's key is not everybody's.
/// </para>
/// <para>
/// Every registration, ancillary stores included whatever <see cref="MartenStudioOptions.IncludeAncillaryStores" />
/// says: a store the studio does not show is still a store whose tables are Marten's. Only options are read; no store's
/// databases are enumerated for a declaration. A failure is asked again next time.
/// </para>
/// <para>
/// <b>Remembered only while it is still true.</b> The options do not change, but what Marten knows about them does:
/// <c>AllKnownDocumentTypes()</c> grows the first time a session touches a type that was never registered with
/// <c>Schema.For&lt;T&gt;()</c>. A classification read before that moment would go on listing a hidden type's table as
/// <c>mt_</c> infrastructure - by name - and passing views over it, for the rest of the circuit. So each store's count
/// of known document types is the fingerprint the remembered read is checked against on every call, and a store that
/// has learned a type is read again. Scoped: the memory is this circuit's.
/// </para>
/// </remarks>
internal sealed class MartenDatabaseDeclarationsSource : IDatabaseDeclarationsSource
{
    /// <summary>
    /// What a sentence about another registered store calls it, for a visitor that store's store policy does
    /// not pass: its key is not theirs to learn (AGENTS.md D27).
    /// </summary>
    internal const string UnnamedStore = "A registered Marten store";

    private readonly MartenStoreRegistry registry;
    private readonly IServiceProvider provider;
    private readonly IOptions<MartenStudioOptions> options;

    /// <summary>
    /// Resolved on the first policy question, never by the constructor: the health sweep resolves this source in a scope
    /// that has no circuit only to call <see cref="ReadUncached" />, which asks no policy, and the authorization graph
    /// (the authentication state, the authorization service) is not this read's to build - C06 labels its locks with what
    /// this answers, and a graph that would not build must leave it unlabelled, not unchecked.
    /// </summary>
    private StudioAuthorization? authorization;

    /// <summary>
    /// Successful declaration reads, per store key, for the life of the circuit - each with the fingerprint
    /// it was read at, and used only while the fingerprint still matches. Concurrent: two components of one
    /// page can ask at once, and their continuations do not share a thread.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Fingerprint, DatabaseDeclarations Declarations)> declarations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Another store's own identity for a database, per store and database, for the life of the circuit.</summary>
    private readonly ConcurrentDictionary<string, string> otherDatabaseIds = new(StringComparer.OrdinalIgnoreCase);

    public MartenDatabaseDeclarationsSource(
        MartenStoreRegistry registry,
        IServiceProvider provider,
        IOptions<MartenStudioOptions> options)
    {
        this.registry = registry;
        this.provider = provider;
        this.options = options;
    }

    /// <summary>The circuit's authorization, resolved from this source's own scope on first use.</summary>
    private StudioAuthorization Authorization => authorization ??= provider.GetRequiredService<StudioAuthorization>();

    /// <inheritdoc />
    public DatabaseDeclarations Read(ResolvedSource resolved)
    {
        ResolvedScope scope = Marten(resolved);
        return ReadDeclarations(scope.Registration, scope.Store);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="source" /> is not a Marten store.</exception>
    public DatabaseDeclarations Read(IStudioSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source is MartenStudioSource marten
            ? ReadDeclarations(marten.Marten, marten.Store)
            : throw new ArgumentException("MartenStudio declares the objects of a Marten store only.", nameof(source));
    }

    /// <inheritdoc />
    public DatabaseDeclarations ReadUncached(ResolvedSource resolved)
    {
        ResolvedScope scope = Marten(resolved);
        return DeclareEveryStore(registry, provider, scope.Registration, scope.Store, options.Value.IsDocumentTypeVisible);
    }

    /// <summary>
    /// Every registered store's declarations, with <paramref name="registration" /> as the resolved store -
    /// remembered for the circuit while every store's fingerprint still matches.
    /// </summary>
    /// <param name="registration">The resolved store's registration.</param>
    /// <param name="resolvedStore">The resolved store.</param>
    internal DatabaseDeclarations ReadDeclarations(MartenStoreRegistration registration, IDocumentStore resolvedStore)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(resolvedStore);

        if (CollectStores(registry, provider, registration, resolvedStore, out List<(string Key, IDocumentStore Store)> stores) is { } unbuilt)
        {
            return unbuilt;
        }

        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;

        if (Fingerprint(stores) is not { } fingerprint)
        {
            // A type Marten cannot map is a configuration that cannot be read: fail closed, as below.
            return Declare(stores, registration.Key, resolvedStore, isVisible);
        }

        if (declarations.TryGetValue(registration.Key, out var cached) && cached.Fingerprint == fingerprint)
        {
            return cached.Declarations;
        }

        DatabaseDeclarations result = Declare(stores, registration.Key, resolvedStore, isVisible);

        if (result.Succeeded)
        {
            declarations[registration.Key] = (fingerprint, result);
        }

        return result;
    }

    /// <summary>
    /// Every registered store's declarations, with <paramref name="registration" /> as the resolved store, read
    /// afresh and remembered nowhere - the classification <see cref="Read(ResolvedSource)" /> caches per circuit, for a
    /// caller that has no circuit: a health check labelling the locks it reads (C06). Fails closed in the same way, and
    /// touches no connection.
    /// </summary>
    /// <param name="registry">The store registry.</param>
    /// <param name="provider">What the other stores are resolved from.</param>
    /// <param name="registration">The resolved store's registration.</param>
    /// <param name="resolvedStore">The resolved store.</param>
    /// <param name="isDocumentTypeVisible"><see cref="MartenStudioOptions.IsDocumentTypeVisible" />.</param>
    internal static DatabaseDeclarations DeclareEveryStore(
        MartenStoreRegistry registry,
        IServiceProvider provider,
        MartenStoreRegistration registration,
        IDocumentStore resolvedStore,
        Func<Type, bool>? isDocumentTypeVisible)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(resolvedStore);

        return CollectStores(registry, provider, registration, resolvedStore, out List<(string Key, IDocumentStore Store)> stores) is { } unbuilt
            ? unbuilt
            : Declare(stores, registration.Key, resolvedStore, isDocumentTypeVisible);
    }

    /// <summary>
    /// Every registered store, ancillary ones included and <paramref name="resolvedStore" /> first-class among them -
    /// or the failure that a store which will not build is.
    /// </summary>
    private static DatabaseDeclarations? CollectStores(
        MartenStoreRegistry registry,
        IServiceProvider provider,
        MartenStoreRegistration registration,
        IDocumentStore resolvedStore,
        out List<(string Key, IDocumentStore Store)> stores)
    {
        stores = [];

        foreach (MartenStoreRegistration each in registry.Registrations(includeAncillaryStores: true))
        {
            if (string.Equals(each.Key, registration.Key, StringComparison.OrdinalIgnoreCase))
            {
                stores.Add((each.Key, resolvedStore));
                continue;
            }

            StoreAvailability availability = registry.TryResolve(each, provider, out IDocumentStore? store);

            if (!availability.IsAvailable || store is null)
            {
                const string consequence =
                    ", so the studio cannot tell its tables from anybody else's and shows no classification until it builds.";

                // Another store: its key - and the reason it gave, which can say anything about it - only for a
                // visitor that store's store policy passes (DatabaseAccess.DescribeFailureAsync).
                return new DatabaseDeclarations(null, [], UnnamedStore + " could not be built" + consequence)
                {
                    FailedStoreKey = each.Key,
                    NamedFailure = "Marten store '" + each.Key + "' could not be built (" +
                                   (availability.Message ?? "unknown reason") + ")" + consequence,
                };
            }

            stores.Add((each.Key, store));
        }

        if (!stores.Any(x => string.Equals(x.Key, registration.Key, StringComparison.OrdinalIgnoreCase)))
        {
            // The resolved store is always registered - the resolver found it there - but a registry that
            // stopped listing it would otherwise leave this store's own tables unclassified.
            stores.Insert(0, (registration.Key, resolvedStore));
        }

        return null;
    }

    /// <summary>
    /// What the remembered classification is checked against: every store's key and its count of known
    /// document types, which only grows - or <see langword="null" /> when a store's types cannot be listed.
    /// </summary>
    private static string? Fingerprint(List<(string Key, IDocumentStore Store)> stores)
    {
        StringBuilder fingerprint = new();

        try
        {
            foreach ((string key, IDocumentStore store) in stores)
            {
                fingerprint.Append(key).Append(':')
                    .Append(store.Options.AllKnownDocumentTypes().Count.ToString(CultureInfo.InvariantCulture))
                    .Append(';');
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        return fingerprint.ToString();
    }

    private static DatabaseDeclarations Declare(
        List<(string Key, IDocumentStore Store)> stores,
        string resolvedKey,
        IDocumentStore resolvedStore,
        Func<Type, bool>? isVisible)
    {
        List<StoreDeclarations> declared = [];
        IReadOnlyList<string> storeSchemas = [];

        foreach ((string key, IDocumentStore store) in stores)
        {
            SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(store.Options, isVisible);

            if (!read.Succeeded)
            {
                string named = "The configuration of Marten store '" + key + "' could not be read, so the " +
                               "studio cannot tell its tables from anybody else's: " + read.Failure;

                if (string.Equals(key, resolvedKey, StringComparison.OrdinalIgnoreCase))
                {
                    // The visitor's own store: its key is in their own scope already.
                    return new DatabaseDeclarations(null, [], named);
                }

                return new DatabaseDeclarations(
                    null,
                    [],
                    "The configuration of a registered Marten store could not be read, so the studio cannot tell " +
                    "its tables from anybody else's.")
                {
                    FailedStoreKey = key,
                    NamedFailure = named,
                };
            }

            if (ReferenceEquals(store, resolvedStore) && string.Equals(key, resolvedKey, StringComparison.OrdinalIgnoreCase))
            {
                storeSchemas = read.Declarations.Schemas;
            }

            declared.Add(new StoreDeclarations(key, read.Declarations));
        }

        return new DatabaseDeclarations(
            new DatabaseObjectClassifier(declared, hidesDocumentTypes: isVisible is not null),
            storeSchemas,
            null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// This store's own grant says nothing about another store: a visitor the resolver would refuse store B must not
    /// learn B's key from an ownership badge, nor read B's projection rows because they happen to sit in a schema of
    /// this store's database.
    /// </para>
    /// <para>
    /// <b>Asked about this database, as that store names it.</b> The visitor's own <c>DatabaseId</c> is this store's
    /// spelling - often empty, which means "this store's default" - and handed to store B it would mean B's default,
    /// which need not be this database at all. So B's policy is asked with the identity of B's own database that is
    /// this one (<see cref="OtherStoreDatabaseIdAsync" />).
    /// </para>
    /// <para>
    /// <b>And only when somebody will be asked, and only from memory.</b> Finding B's identity for this database means
    /// <c>AllDatabases()</c> on B. The identity is only ever the resource a policy is shown, so with no store policy -
    /// and, while the capability is on, no write policy either - nothing is enumerated, on this page or on any document
    /// open that reads the gate. And a B whose enumeration is not an in-memory walk is never enumerated at all, because
    /// the walk could only ever change the identity's case (<see cref="OtherStoreDatabaseIdAsync" />).
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
        ResolvedSource resolved,
        DatabaseObjectClassifier classifier,
        bool capabilityEnabled,
        CancellationToken cancellationToken)
    {
        ResolvedScope scope = Marten(resolved);
        ArgumentNullException.ThrowIfNull(classifier);

        Dictionary<string, DatabaseStoreAccess> others = new(StringComparer.OrdinalIgnoreCase);
        string resolvedKey = scope.Registration.Key;

        // The same two questions the loop asks, and the policies that would answer them: the store policy for
        // B's identity, and - only while the capability is on - the write policy for B's rows.
        StudioAuthorization authorization = Authorization;
        bool anyPolicyAsked = !string.IsNullOrWhiteSpace(authorization.PolicyFor(null))
            || (capabilityEnabled && !string.IsNullOrWhiteSpace(authorization.PolicyFor(nameof(StudioCapability.BrowseDatabase))));

        foreach (StoreDeclarations store in classifier.Stores)
        {
            if (string.Equals(store.StoreKey, resolvedKey, StringComparison.OrdinalIgnoreCase) || others.ContainsKey(store.StoreKey))
            {
                continue;
            }

            string databaseId = anyPolicyAsked
                ? await OtherStoreDatabaseIdAsync(store.StoreKey, scope.Database, cancellationToken).ConfigureAwait(false)
                : scope.Database.Id.Identity;

            StudioScope other = new(store.StoreKey, databaseId, null);

            if (!await authorization.IsAuthorizedAsync(other, capability: null, cancellationToken).ConfigureAwait(false))
            {
                others[store.StoreKey] = new DatabaseStoreAccess(
                    false, DatabaseRowAccess.Refused(DatabaseRefusal.StorePolicy, DatabaseGate.OtherStorePolicyDenial));
                continue;
            }

            if (!capabilityEnabled)
            {
                // The rows are refused by the capability anyway, and nobody is asked about a capability the
                // process does not have.
                others[store.StoreKey] = new DatabaseStoreAccess(
                    true, DatabaseRowAccess.Refused(DatabaseRefusal.CapabilityOff, DatabaseGate.CapabilityDenial));
                continue;
            }

            others[store.StoreKey] = await authorization.IsAuthorizedAsync(other, StudioCapability.BrowseDatabase, cancellationToken).ConfigureAwait(false)
                ? DatabaseStoreAccess.Open
                : new DatabaseStoreAccess(
                    true, DatabaseRowAccess.Refused(DatabaseRefusal.WritePolicy, DatabaseGate.OtherStoreWritePolicyDenial));
        }

        return others;
    }

    /// <summary>
    /// Another store's own identity for <paramref name="database" />: the identity of the database of
    /// <paramref name="storeKey" /> that is this one - matched by <c>Id.Identity</c>, which is what that
    /// store's scope selector and its policy see - or, when it has none (or cannot say), this database's own
    /// identity, which at least names this database rather than that store's default. Remembered for the
    /// circuit.
    /// </summary>
    /// <remarks>
    /// The answer is always this database's own identity: the walk below matches by <c>Id.Identity</c>,
    /// ignoring case, and returns the match, so all it can change is the case that store spells it in. That
    /// is worth an in-memory walk and nothing more. A <c>Single</c> or <c>StaticMultiple</c> store's
    /// <c>AllDatabases()</c> answers from memory. A <c>DynamicMultiple</c> one runs
    /// <c>Tenancy.BuildDatabases()</c>, which reads its master or pool table every time - and the first time per
    /// store instance applies that table and upserts its seed rows, or on a single-server tenancy creates every
    /// configured database that does not exist yet (AGENTS.md D27). So a dynamic store, and one whose
    /// cardinality cannot be read, is never enumerated here: the identity is used as given.
    /// </remarks>
    private async Task<string> OtherStoreDatabaseIdAsync(string storeKey, IMartenDatabase database, CancellationToken cancellationToken)
    {
        string identity = database.Id.Identity;
        string key = storeKey + "\u001f" + identity;

        if (otherDatabaseIds.TryGetValue(key, out string? known))
        {
            return known;
        }

        string answer = identity;
        MartenStoreRegistration? registration = registry.Find(storeKey, options);

        if (registration is not null
            && registry.TryResolve(registration, provider, out IDocumentStore? store).IsAvailable
            && store is not null
            && EnumeratesFromMemory(store))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (IMartenDatabase candidate in await store.Storage.AllDatabases().ConfigureAwait(false))
                {
                    if (string.Equals(candidate.Id.Identity, identity, StringComparison.OrdinalIgnoreCase))
                    {
                        answer = candidate.Id.Identity;
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // That store cannot list its databases just now: this database's own identity it is.
            }
        }

        otherDatabaseIds[key] = answer;
        return answer;
    }

    /// <summary>
    /// Whether <paramref name="store" />'s <c>AllDatabases()</c> is a walk of what it already holds: a
    /// <c>Single</c> or <c>StaticMultiple</c> tenancy. A <c>DynamicMultiple</c> one is not, and neither is a
    /// tenancy whose cardinality cannot be read - the stricter answer.
    /// </summary>
    private static bool EnumeratesFromMemory(IDocumentStore store)
    {
        try
        {
            return store.Options.Tenancy.Cardinality
                is JasperFx.Descriptors.DatabaseCardinality.Single
                or JasperFx.Descriptors.DatabaseCardinality.StaticMultiple;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static ResolvedScope Marten(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved as ResolvedScope
            ?? throw new ArgumentException("MartenStudio declares the objects of a Marten store only.", nameof(resolved));
    }
}
