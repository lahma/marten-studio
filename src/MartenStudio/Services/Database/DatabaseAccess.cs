using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Database;

/// <summary>The declarations of every registered store, or why they could not all be read.</summary>
/// <param name="Classifier">The classifier over every store, when every store could be read.</param>
/// <param name="StoreSchemas">The resolved store's own schemas.</param>
/// <param name="Failure">Why the classification is unavailable - fail closed.</param>
internal sealed record DatabaseDeclarations(
    DatabaseObjectClassifier? Classifier,
    IReadOnlyList<string> StoreSchemas,
    string? Failure)
{
    /// <summary>Whether every store's declarations were read.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Classifier))]
    public bool Succeeded => Classifier is not null && Failure is null;
}

/// <summary>A gate, or why there is none.</summary>
/// <param name="Gate">The gate.</param>
/// <param name="Refusal">Why there is none.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseGateRead(DatabaseGate? Gate, DatabaseRefusal Refusal, string? Reason)
{
    /// <summary>Whether there is a gate.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Gate))]
    public bool Succeeded => Gate is not null;
}

/// <summary>What the enforcement half of the gate answers: a resolved scope, or the refusal.</summary>
/// <param name="Resolved">The scope, resolved with no tenant and the capability named, when allowed.</param>
/// <param name="Refusal">Which gate refused.</param>
/// <param name="Reason">The sentence naming it.</param>
internal sealed record DatabaseBrowseGrant(ResolvedScope? Resolved, DatabaseRefusal Refusal, string? Reason)
{
    /// <summary>Whether the capability and the policy both said yes.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Resolved))]
    public bool Allowed => Resolved is not null;
}

/// <summary>What a row reader is handed when it may read, and nothing when it may not.</summary>
/// <param name="Resolved">The scope, resolved with no tenant and the capability named.</param>
/// <param name="Relation">
/// The relation as the catalog has it. Its schema and name are the ones to quote - never the strings the
/// caller passed in.
/// </param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Columns">Its columns.</param>
/// <param name="RowKey">Its row key, or <see langword="null" /> when it has none.</param>
internal sealed record DatabaseRowGrant(
    ResolvedScope Resolved,
    CatalogRelationDetail Relation,
    DatabaseObjectOwnership Ownership,
    IReadOnlyList<DatabaseColumnInfo> Columns,
    DatabaseRowKey? RowKey);

/// <summary>A row grant, or the refusal naming the gate that is shut.</summary>
/// <param name="Grant">The grant.</param>
/// <param name="Refusal">Which gate refused.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseRowAccessResult(DatabaseRowGrant? Grant, DatabaseRefusal Refusal, string? Reason)
{
    /// <summary>Whether rows may be read.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Grant))]
    public bool Allowed => Grant is not null;

    /// <summary>Refused, and why.</summary>
    public static DatabaseRowAccessResult Refused(DatabaseRefusal refusal, string reason) => new(null, refusal, reason);
}

/// <summary>
/// The database browser's per-visitor gate: what this visitor may see, and the audited enforcement in
/// front of every read that goes past the store's own structure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two halves, deliberately separate.</b> <see cref="GateAsync" /> answers the page's question - which
/// schemas, which rows, which definitions - with <see cref="StudioAuthorization.IsAuthorizedAsync(StudioScope, string?, CancellationToken)" />,
/// asked with the same resource the enforcement asks with, so a button the page draws enabled is a
/// button whose service call passes. It writes nothing to the audit. <see cref="RequireBrowseAsync" /> and
/// <see cref="RequireRowAccessAsync" /> are the enforcement, in <c>QueryService</c>'s order for
/// <c>RunSql</c> (AGENTS.md hard rule 5): <see cref="StudioCapabilityGuard.Require" /> first, then
/// <see cref="StudioScopeResolver.ResolveAsync" /> with the capability named - so the write policy is the one
/// asked - and only then anything about the database. Every refusal is audited.
/// </para>
/// <para>
/// <b>The resource carries no tenant.</b> Non-Marten data is not tenant-scoped: a Quartz table has no
/// <c>tenant_id</c> for the studio to filter on, so a read of it is a read for every tenant at once. The
/// policy is therefore asked against <c>scope with { TenantId = null }</c> - a database-level resource - even
/// when the visitor has a tenant selected, and a handler that restricts visitors to their own tenant
/// refuses it, as it should (see the README's handler example).
/// </para>
/// <para>
/// <b>Only catalog-sourced names reach SQL.</b> <see cref="RequireRowAccessAsync" /> looks the relation up
/// in the catalog and hands back the catalog's row; a row reader quotes <em>that</em> schema and name,
/// never the strings a URL supplied.
/// </para>
/// </remarks>
internal sealed class DatabaseAccess
{
    /// <summary>What the audit ring calls opening one object's detail.</summary>
    internal const string ObjectAction = "Open database object";

    /// <summary>What the audit ring calls listing objects.</summary>
    internal const string ListAction = "List database objects";

    /// <summary>What the audit ring calls reading a definition.</summary>
    internal const string DefinitionAction = "Read database definition";

    /// <summary>What the audit ring calls reading a relation's rows.</summary>
    internal const string RowsAction = "Browse database rows";

    private readonly IOptions<MartenStudioOptions> options;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioScopeResolver resolver;
    private readonly StudioActionLog audit;
    private readonly MartenStoreRegistry registry;
    private readonly DatabaseCatalog catalog;
    private readonly IServiceProvider provider;

    /// <summary>
    /// Successful declaration reads, per store key, for the life of the circuit. Concurrent: two components
    /// of one page can ask at once, and their continuations do not share a thread.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DatabaseDeclarations> declarations =
        new(StringComparer.OrdinalIgnoreCase);

    public DatabaseAccess(
        IOptions<MartenStudioOptions> options,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioScopeResolver resolver,
        StudioActionLog audit,
        MartenStoreRegistry registry,
        DatabaseCatalog catalog,
        IServiceProvider provider)
    {
        this.options = options;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.registry = registry;
        this.catalog = catalog;
        this.provider = provider;
    }

    /// <summary>
    /// Every registered store's declarations, and the resolved store's own schemas - or the reason the
    /// studio cannot tell Marten's objects from anybody else's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fail closed.</b> A store that will not build, or whose declarations throw, is a store whose
    /// tables would all read as "Other" - rows included. So one failure is a failure of the whole
    /// classification, named after the store, rather than a smaller map.
    /// </para>
    /// <para>
    /// Every registration, ancillary stores included whatever
    /// <see cref="MartenStudioOptions.IncludeAncillaryStores" /> says: a store the studio does not show is
    /// still a store whose tables are Marten's. Only options are read; no store's databases are
    /// enumerated. A successful read is remembered for the circuit - the options do not change - and a
    /// failure is asked again next time.
    /// </para>
    /// </remarks>
    public DatabaseDeclarations ReadDeclarations(ResolvedScope resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        if (declarations.TryGetValue(resolved.Registration.Key, out DatabaseDeclarations? cached))
        {
            return cached;
        }

        Func<Type, bool>? isVisible = options.Value.IsDocumentTypeVisible;
        List<StoreDeclarations> stores = [];
        IReadOnlyList<string> storeSchemas = [];

        foreach (MartenStoreRegistration registration in registry.Registrations(includeAncillaryStores: true))
        {
            IDocumentStore? store;

            if (string.Equals(registration.Key, resolved.Registration.Key, StringComparison.OrdinalIgnoreCase))
            {
                store = resolved.Store;
            }
            else
            {
                StoreAvailability availability = registry.TryResolve(registration, provider, out store);

                if (!availability.IsAvailable || store is null)
                {
                    return new DatabaseDeclarations(
                        null,
                        [],
                        "Marten store '" + registration.Key + "' could not be built (" +
                        (availability.Message ?? "unknown reason") + "), so the studio cannot tell its tables " +
                        "from anybody else's and shows no classification until it builds.");
                }
            }

            SchemaDeclarationRead read = SchemaDeclarationReader.ReadForClassification(store.Options, isVisible);

            if (!read.Succeeded)
            {
                return new DatabaseDeclarations(
                    null,
                    [],
                    "The configuration of Marten store '" + registration.Key + "' could not be read, so the " +
                    "studio cannot tell its tables from anybody else's: " + read.Failure);
            }

            if (ReferenceEquals(store, resolved.Store))
            {
                storeSchemas = read.Declarations.Schemas;
            }

            stores.Add(new StoreDeclarations(registration.Key, read.Declarations));
        }

        if (!stores.Any(x => string.Equals(x.StoreKey, resolved.Registration.Key, StringComparison.OrdinalIgnoreCase)))
        {
            // The resolved store is always registered - the resolver found it there - but a registry that
            // stopped listing it would otherwise leave this store's own tables unclassified.
            SchemaDeclarationRead own = SchemaDeclarationReader.ReadForClassification(resolved.Store.Options, isVisible);

            if (!own.Succeeded)
            {
                return new DatabaseDeclarations(null, [], own.Failure);
            }

            storeSchemas = own.Declarations.Schemas;
            stores.Insert(0, new StoreDeclarations(resolved.Registration.Key, own.Declarations));
        }

        DatabaseDeclarations result = new(new DatabaseObjectClassifier(stores), storeSchemas, null);

        return declarations.GetOrAdd(resolved.Registration.Key, result);
    }

    /// <summary>
    /// Whether this visitor passes the <c>BrowseDatabase</c> gate - capability, then policy - and the
    /// refusal when not. Not audited: it is the page's question, asked on every render.
    /// </summary>
    /// <remarks>
    /// The policy is asked twice, exactly as <see cref="StudioScopeResolver.ResolveAsync" /> will ask it:
    /// the store policy for the tenant-less scope, then the write policy with the capability named. Asking
    /// only the second would draw an enabled control for a visitor whose store policy refuses a tenant-less
    /// resource, and the service would then refuse them.
    /// </remarks>
    public async Task<(bool CapabilityEnabled, bool? Authorized)> EvaluateAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!capabilities.IsEnabled(StudioCapability.BrowseDatabase))
        {
            // Nobody is asked about a capability the process does not have.
            return (false, null);
        }

        StudioScope tenantless = scope with { TenantId = null };

        bool authorized =
            await authorization.IsAuthorizedAsync(tenantless, capability: null, cancellationToken).ConfigureAwait(false)
            && await authorization.IsAuthorizedAsync(tenantless, StudioCapability.BrowseDatabase, cancellationToken).ConfigureAwait(false);

        return (true, authorized);
    }

    /// <summary>
    /// The whole gate for a resolved scope: declarations (fail closed), capability, policy, and the
    /// database's live schema list, which <c>"*"</c> and the withheld count need.
    /// </summary>
    public async Task<DatabaseGateRead> GateAsync(
        StudioScope scope,
        ResolvedScope resolved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(resolved);

        DatabaseDeclarations declared = ReadDeclarations(resolved);

        if (!declared.Succeeded)
        {
            return new DatabaseGateRead(null, DatabaseRefusal.Unavailable, declared.Failure);
        }

        (bool capabilityEnabled, bool? authorized) = await EvaluateAsync(scope, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<CatalogSchema> live = await catalog.SchemasAsync(resolved.Database, cancellationToken)
            .ConfigureAwait(false);

        MartenStudioOptions value = options.Value;

        return new DatabaseGateRead(
            new DatabaseGate(
                declared.StoreSchemas,
                live,
                [.. value.BrowsableSchemas],
                capabilityEnabled,
                capabilities.ReadOnly,
                authorized,
                declared.Classifier,
                value.SqlConsoleRole),
            DatabaseRefusal.None,
            null);
    }

    /// <summary>
    /// The enforcement in front of every <c>BrowseDatabase</c> read: the capability, then the scope with no
    /// tenant and the capability named. Audited on refusal; never throws for a refusal.
    /// </summary>
    /// <param name="scope">The visitor's scope. Its tenant is dropped.</param>
    /// <param name="action">What the audit ring calls the read.</param>
    /// <param name="target">What it was aimed at.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    public async Task<DatabaseBrowseGrant> RequireBrowseAsync(
        StudioScope scope,
        string action,
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1. The capability, before anybody is asked who is asking.
        try
        {
            capabilities.Require(StudioCapability.BrowseDatabase);
        }
        catch (StudioCapabilityDeniedException denied)
        {
            audit.RecordCapabilityDenied(denied, action, target);

            return new DatabaseBrowseGrant(
                null,
                denied.Reason == CapabilityDenialReason.ReadOnly ? DatabaseRefusal.ReadOnly : DatabaseRefusal.CapabilityOff,
                denied.Reason == CapabilityDenialReason.ReadOnly ? DatabaseGate.ReadOnlyDenial : DatabaseGate.CapabilityDenial);
        }

        // 2. The scope, with no tenant and the capability named, so the write policy is the one asked - of
        //    the database as a whole, because nothing here is tenant-scoped.
        StudioScope tenantless = scope with { TenantId = null };

        try
        {
            ResolvedScope resolved = await resolver
                .ResolveAsync(tenantless, nameof(StudioCapability.BrowseDatabase), cancellationToken)
                .ConfigureAwait(false);

            return new DatabaseBrowseGrant(resolved, DatabaseRefusal.None, null);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(tenantless, WritePolicyName(options.Value), action, target);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.WritePolicy, DatabaseGate.WritePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            audit.Record(action, target, succeeded: false, exception.Message, StudioCapability.BrowseDatabase, tenantless);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.Unavailable, exception.Message);
        }
    }

    /// <summary>
    /// The row gate, for a reader of one relation's rows: the capability, the tenant-less scope with the
    /// capability named, the schema against <see cref="MartenStudioOptions.BrowsableSchemas" />, then the
    /// relation looked up in the catalog and judged - whose it is, what it is, what it reads, whether the
    /// role may select from it. Every refusal is audited under <paramref name="action" />; a grant is not,
    /// because the caller knows what it then read and records that.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="schema">The schema, as the URL had it. Only compared, never quoted.</param>
    /// <param name="name">The relation, as the URL had it. Only compared, never quoted.</param>
    /// <param name="action">What the audit ring calls the read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<DatabaseRowAccessResult> RequireRowAccessAsync(
        StudioScope scope,
        string schema,
        string name,
        string action = RowsAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string target = Target(schema, name);

        DatabaseBrowseGrant grant = await RequireBrowseAsync(scope, action, target, cancellationToken)
            .ConfigureAwait(false);

        if (!grant.Allowed)
        {
            return DatabaseRowAccessResult.Refused(grant.Refusal, grant.Reason ?? "Refused.");
        }

        StudioScope tenantless = scope with { TenantId = null };

        DatabaseRowAccessResult Refuse(DatabaseRefusal refusal, string reason)
        {
            audit.Record(action, target, succeeded: false, reason, StudioCapability.BrowseDatabase, tenantless);
            return DatabaseRowAccessResult.Refused(refusal, reason);
        }

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
        {
            return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema ?? string.Empty, name ?? string.Empty));
        }

        // 3. The schema, settled from the options alone where it can be: an entry list that cannot admit it
        //    is refused before the database is asked anything.
        IReadOnlyList<string> entries = [.. options.Value.BrowsableSchemas];

        if (!BrowsableSchemaMatcher.MightMatch(entries, schema))
        {
            return Refuse(DatabaseRefusal.SchemaNotBrowsable, DatabaseGate.SchemaDenial(schema, entries));
        }

        try
        {
            DatabaseGateRead gateRead = await GateAsync(tenantless, grant.Resolved, cancellationToken).ConfigureAwait(false);

            if (!gateRead.Succeeded)
            {
                return Refuse(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                // Said as not-found rather than "not browsable": a "*" that does not admit a schema is, from
                // here, a schema that is not there.
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name));
            }

            // 4. The catalog. From here on the names are the catalog's, not the caller's.
            CatalogRelationDetail? relation = await catalog
                .RelationAsync(grant.Resolved.Database, schema, name, cancellationToken)
                .ConfigureAwait(false);

            if (relation is null)
            {
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name));
            }

            DatabaseObjectDetail detail = DatabaseObjectAssembler.Detail(gate, relation);

            if (detail.Relation is not { } summary)
            {
                return Refuse(detail.Refusal, detail.Reason ?? DatabaseObjectAssembler.NotFound(schema, name));
            }

            if (!summary.Rows.Allowed)
            {
                return Refuse(summary.Rows.Refusal, summary.Rows.Reason ?? "Its rows may not be read.");
            }

            return new DatabaseRowAccessResult(
                new DatabaseRowGrant(grant.Resolved, relation, summary.Ownership, detail.Columns, detail.RowKey),
                DatabaseRefusal.None,
                null);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            return Refuse(DatabaseRefusal.Unavailable, CatalogFailure(exception));
        }
    }

    /// <summary>What the audit entry names as the target.</summary>
    internal static string Target(string? schema, string? name) => (schema ?? string.Empty) + "." + (name ?? string.Empty);

    /// <summary>Whether an exception is a catalog read going wrong, which a page renders rather than throws.</summary>
    internal static bool IsCatalogFailure(Exception exception) =>
        exception is NpgsqlException or InvalidOperationException or TimeoutException
        && exception is not OperationCanceledException;

    /// <summary>The sentence for a catalog read that failed.</summary>
    internal static string CatalogFailure(Exception exception) =>
        exception is PostgresException postgres
            ? "The catalog could not be read: " + postgres.SqlState + " " + postgres.MessageText
            : "The catalog could not be read: " + exception.Message;

    /// <summary>The policy a write-policy refusal names in event 9203.</summary>
    internal static string WritePolicyName(MartenStudioOptions value) =>
        value.WriteAuthorizationPolicy ?? value.StoreAuthorizationPolicy ?? "(none)";
}
