using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

using Npgsql;

namespace PostgresStudio.Services.Database;

/// <summary>The declarations of every registered source, or why they could not all be read.</summary>
/// <remarks>
/// Read through the studio's <see cref="IDatabaseDeclarationsSource" />: MartenStudio's reads every registered
/// store's options; a source with no declarations of its own answers an empty classifier.
/// </remarks>
/// <param name="Classifier">The classifier over every source, when every source could be read.</param>
/// <param name="StoreSchemas">The resolved source's own schemas.</param>
/// <param name="Failure">
/// Why the classification is unavailable - fail closed - in words any visitor of the resolved store may read:
/// the resolved store is named, because the visitor's own scope names it, and another store is "a registered
/// Marten store". <see cref="DatabaseAccess.DescribeFailureAsync" /> names that one to a visitor its store
/// policy passes.
/// </param>
internal sealed record DatabaseDeclarations(
    DatabaseObjectClassifier? Classifier,
    IReadOnlyList<string> StoreSchemas,
    string? Failure)
{
    /// <summary>Whether every store's declarations were read.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Classifier))]
    public bool Succeeded => Classifier is not null && Failure is null;

    /// <summary>
    /// The registration key of the other store <see cref="Failure" /> is about, when it is not the resolved
    /// store - shown only to a visitor that store's store policy passes (AGENTS.md D27).
    /// </summary>
    public string? FailedStoreKey { get; init; }

    /// <summary>
    /// <see cref="Failure" /> with <see cref="FailedStoreKey" /> named and the reason it gave, for a visitor
    /// that store's store policy passes; <see langword="null" /> when <see cref="Failure" /> names no other store.
    /// </summary>
    public string? NamedFailure { get; init; }
}

/// <summary>
/// The source a visitor's scope names, found without resolving the scope - so without asking which
/// database or tenant - or the resolver's own sentence for why not.
/// </summary>
/// <param name="Registration">The registration.</param>
/// <param name="Source">The built source.</param>
/// <param name="Declarations">Every registered source's declarations, read with this one as the resolved source.</param>
/// <param name="Refused">
/// Why there is none: the same sentence <see cref="StudioSourceResolver.ResolveAsync" /> would have thrown,
/// which says the same thing for a source that is refused and one that does not exist.
/// </param>
internal sealed record DatabaseStoreLookup(
    StudioSourceRegistration? Registration,
    IStudioSource? Source,
    DatabaseDeclarations? Declarations,
    string? Refused)
{
    /// <summary>Whether the source was found.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Registration), nameof(Source), nameof(Declarations))]
    public bool Found => Refused is null && Registration is not null && Source is not null && Declarations is not null;
}

/// <summary>What the page's question about the policies answers.</summary>
/// <param name="CapabilityEnabled">Whether <c>BrowseDatabase</c> is on and <c>ReadOnly</c> is off.</param>
/// <param name="Authorized">Both policies' yes, or <see langword="null" /> when nobody was asked.</param>
/// <param name="Refusal">
/// When <paramref name="Authorized" /> is <see langword="false" />, the one that said no:
/// <see cref="DatabaseRefusal.StorePolicy" /> or <see cref="DatabaseRefusal.WritePolicy" />.
/// </param>
internal sealed record DatabasePolicyAnswer(bool CapabilityEnabled, bool? Authorized, DatabaseRefusal Refusal);

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
internal sealed record DatabaseBrowseGrant(ResolvedSource? Resolved, DatabaseRefusal Refusal, string? Reason)
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
/// <param name="Gate">
/// The visitor's gate the grant was decided by - what judges the far ends of the relation's keys, and whose
/// <see cref="DatabaseGate.WithheldSchemas" /> masks whatever Postgres says about a read of it
/// (<see cref="PostgresErrorText" />), so nothing a page shows names a schema the visitor may not see.
/// </param>
internal sealed record DatabaseRowGrant(
    ResolvedSource Resolved,
    CatalogRelationDetail Relation,
    DatabaseObjectOwnership Ownership,
    IReadOnlyList<DatabaseColumnInfo> Columns,
    DatabaseRowKey? RowKey,
    DatabaseGate Gate);

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
/// <c>RunSql</c> (AGENTS.md hard rule 5): <see cref="StudioCapabilityGuard.Require" /> first, then the store
/// policy and the write policy - asked one at a time, so the refusal and event 9203 name the one that said
/// no - then <see cref="StudioSourceResolver.ResolveAsync" /> with no tenant and the capability named, and
/// only then anything about the database. Every refusal is audited.
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

    private readonly IStudioSettings settings;
    private readonly StudioDescriptor descriptor;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly StudioActionLog audit;
    private readonly IStudioSourceProvider sources;
    private readonly DatabaseCatalog catalog;
    private readonly IDatabaseDeclarationsSource declarations;

    public DatabaseAccess(
        IStudioSettings settings,
        StudioDescriptor descriptor,
        StudioCapabilityGuard capabilities,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        StudioActionLog audit,
        IStudioSourceProvider sources,
        DatabaseCatalog catalog,
        IDatabaseDeclarationsSource declarations)
    {
        this.settings = settings;
        this.descriptor = descriptor;
        this.capabilities = capabilities;
        this.authorization = authorization;
        this.resolver = resolver;
        this.audit = audit;
        this.sources = sources;
        this.catalog = catalog;
        this.declarations = declarations;
    }

    /// <summary>
    /// Every registered source's declarations, and the resolved source's own schemas - or the reason the
    /// studio cannot tell Marten's objects from anybody else's - from the studio's
    /// <see cref="IDatabaseDeclarationsSource" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Fail closed.</b> A store that will not build, or whose declarations throw, is a store whose
    /// tables would all read as "Other" - rows included. So one failure is a failure of the whole
    /// classification rather than a smaller map - named after the store only for a visitor that store's store
    /// policy passes (<see cref="DescribeFailureAsync" />), because another store's key is not everybody's.
    /// </para>
    /// <para>
    /// Every registration, ancillary stores included whatever the studio shows: a store the studio does not
    /// show is still a store whose tables are Marten's. Only options are read; no store's databases are
    /// enumerated. A failure is asked again next time.
    /// </para>
    /// <para>
    /// <b>Remembered only while it is still true.</b> The options do not change, but what Marten knows about
    /// them does: <c>AllKnownDocumentTypes()</c> grows the first time a session touches a type that was never
    /// registered with <c>Schema.For&lt;T&gt;()</c>. A classification read before that moment would go on
    /// listing a hidden type's table as <c>mt_</c> infrastructure - by name - and passing views over it,
    /// for the rest of the circuit. So each store's count of known document types is the fingerprint the
    /// remembered read is checked against on every call, and a store that has learned a type is read again.
    /// </para>
    /// </remarks>
    public DatabaseDeclarations ReadDeclarations(ResolvedSource resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return declarations.Read(resolved);
    }

    /// <summary>
    /// Why <paramref name="declared" /> failed, as this visitor may read it: another store that could not be
    /// built or read is named, with the reason it gave, only when that store's store policy passes the visitor
    /// for <c>(that store, <paramref name="databaseId" />, no tenant)</c> - the rule D27 sets for another store's
    /// key everywhere else on the browser. Anybody else reads "a registered Marten store".
    /// </summary>
    /// <param name="declared">A failed read.</param>
    /// <param name="databaseId">
    /// The database the other store's policy is asked about. A store that cannot be built - or whose
    /// configuration cannot be read - cannot say which of its databases this is, and is not asked to enumerate
    /// them to find out: <see cref="GateAsync" /> passes this database's own identity, and a caller that has not
    /// resolved a database yet the visitor's own <see cref="StudioScope.DatabaseId" />, as the Schema screen's
    /// classification does.
    /// </param>
    /// <param name="cancellationToken">Cancels the policy's evaluation.</param>
    internal async Task<string> DescribeFailureAsync(
        DatabaseDeclarations declared,
        string databaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(declared);

        string unnamed = declared.Failure ?? "The database browser is unavailable.";

        if (declared.FailedStoreKey is not { } key || declared.NamedFailure is not { } named)
        {
            return unnamed;
        }

        return await authorization.IsAuthorizedAsync(new StudioScope(key, databaseId, null), capability: null, cancellationToken)
            .ConfigureAwait(false)
            ? named
            : unnamed;
    }

    /// <summary>
    /// The source <paramref name="scope" /> names, and every source's declarations - found the way the
    /// resolver finds it, but without resolving the database or the tenant, so nothing is asked of the
    /// database before the caller knows whether the read is one the capability must allow first.
    /// </summary>
    /// <remarks>
    /// The store policy is asked about the visitor's own scope before anything is looked up, exactly as
    /// <see cref="StudioSourceResolver.ResolveAsync" /> asks it, so a store the visitor may not see and one
    /// that does not exist answer with the same sentence. That is an in-process question; tenant discovery,
    /// which can query the database, is never run here.
    /// </remarks>
    public Task<DatabaseStoreLookup> FindStoreAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        FindStoreAsync(scope, null, null, cancellationToken);

    /// <summary>
    /// <see cref="FindStoreAsync(StudioScope, CancellationToken)" />, with the store policy's refusal written
    /// to the audit ring (and event 9203) under <paramref name="action" /> and <paramref name="target" /> -
    /// the refusal is the same sentence a missing store gets, and the trail is where the difference is kept.
    /// </summary>
    /// <remarks>
    /// The entry is recorded under <c>BrowseDatabase</c>, as every other refusal of a browser read is: its
    /// target is the <c>schema.relation</c> somebody asked the browser for, and the Activity screen and the
    /// Overview show such a name only to a visitor who may browse the database themselves
    /// (<see cref="StudioActionLog.GetVisibleAsync" />). Recorded with no capability it was shown to every
    /// reader the store policy passes - the one audience the refusal says was not allowed to see it.
    /// </remarks>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="action">What the audit ring calls the read; <see langword="null" /> to audit nothing.</param>
    /// <param name="target">What it was aimed at.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public async Task<DatabaseStoreLookup> FindStoreAsync(
        StudioScope scope,
        string? action,
        string? target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!await authorization.IsAuthorizedAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
        {
            if (action is not null)
            {
                audit.RecordScopeDenied(scope, StorePolicyName(settings), action, target ?? string.Empty, StudioCapability.BrowseDatabase);
            }

            return new DatabaseStoreLookup(null, null, null, new StudioNotAuthorizedException(scope).Message);
        }

        StudioSourceRegistration? registration = sources.Find(scope.StoreKey);

        if (registration is null)
        {
            return new DatabaseStoreLookup(null, null, null, $"No {descriptor.SourceNoun} is registered under '{scope.StoreKey}'.");
        }

        StoreAvailability availability = sources.TryResolve(registration, out IStudioSource? source);

        if (!availability.IsAvailable || source is null)
        {
            return new DatabaseStoreLookup(
                null, null, null, new StudioStoreUnavailableException(registration.Key, availability.Message ?? "unknown reason", descriptor.SourceNoun).Message);
        }

        // The declarations of the source as found, not of a database of it: nothing about its databases or tenants
        // is asked before the caller knows whether the capability must allow the read first.
        return new DatabaseStoreLookup(registration, source, declarations.Read(source), null);
    }

    /// <summary>
    /// Whether this visitor passes the <c>BrowseDatabase</c> gate - capability, then policy - and the
    /// refusal when not. Not audited: it is the page's question, asked on every render.
    /// </summary>
    /// <remarks>
    /// The policy is asked twice, exactly as <see cref="StudioSourceResolver.ResolveAsync" /> will ask it:
    /// the store policy for the tenant-less scope, then the write policy with the capability named. Asking
    /// only the second would draw an enabled control for a visitor whose store policy refuses a tenant-less
    /// resource, and the service would then refuse them.
    /// </remarks>
    public async Task<(bool CapabilityEnabled, bool? Authorized)> EvaluateAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        DatabasePolicyAnswer answer = await EvaluatePoliciesAsync(scope, cancellationToken).ConfigureAwait(false);

        return (answer.CapabilityEnabled, answer.Authorized);
    }

    /// <summary>
    /// <see cref="EvaluateAsync" />, and which policy said no when one did - so the page names the same
    /// setting the enforcement's audit entry names.
    /// </summary>
    public async Task<DatabasePolicyAnswer> EvaluatePoliciesAsync(
        StudioScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!capabilities.IsEnabled(StudioCapability.BrowseDatabase))
        {
            // Nobody is asked about a capability the process does not have.
            return new DatabasePolicyAnswer(false, null, DatabaseRefusal.None);
        }

        StudioScope tenantless = scope with { TenantId = null };

        if (!await authorization.IsAuthorizedAsync(tenantless, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return new DatabasePolicyAnswer(true, false, DatabaseRefusal.StorePolicy);
        }

        return await authorization.IsAuthorizedAsync(tenantless, StudioCapability.BrowseDatabase, cancellationToken).ConfigureAwait(false)
            ? new DatabasePolicyAnswer(true, true, DatabaseRefusal.None)
            : new DatabasePolicyAnswer(true, false, DatabaseRefusal.WritePolicy);
    }

    /// <summary>
    /// The whole gate for a resolved scope: declarations (fail closed), capability, policy, every other
    /// registered source's policies (<see cref="IDatabaseDeclarationsSource.OtherSourcesAsync" />), and the
    /// database's live schema list, which <c>"*"</c> and the withheld count need.
    /// </summary>
    public async Task<DatabaseGateRead> GateAsync(
        StudioScope scope,
        ResolvedSource resolved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(resolved);

        DatabaseDeclarations declared = ReadDeclarations(resolved);

        if (!declared.Succeeded)
        {
            return new DatabaseGateRead(
                null,
                DatabaseRefusal.Unavailable,
                await DescribeFailureAsync(declared, resolved.Target.Identity, cancellationToken).ConfigureAwait(false));
        }

        DatabasePolicyAnswer answer = await EvaluatePoliciesAsync(scope, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, DatabaseStoreAccess> others = await declarations
            .OtherSourcesAsync(resolved, declared.Classifier, answer.CapabilityEnabled, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<CatalogSchema> live = await catalog.SchemasAsync(resolved.Target, cancellationToken)
            .ConfigureAwait(false);

        return new DatabaseGateRead(
            new DatabaseGate(
                declared.StoreSchemas,
                live,
                [.. settings.BrowsableSchemas],
                answer.CapabilityEnabled,
                capabilities.ReadOnly,
                answer.Authorized,
                declared.Classifier,
                settings.SqlConsoleRole,
                answer.Refusal == DatabaseRefusal.StorePolicy ? DatabaseRefusal.StorePolicy : DatabaseRefusal.WritePolicy,
                resolved.Source.Key,
                others,
                settings.Text),
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
                denied.Reason == CapabilityDenialReason.ReadOnly ? settings.Text.BrowseReadOnlyDenial : settings.Text.BrowseCapabilityDenial);
        }

        // 2. The two policies, of the database as a whole - nothing here is tenant-scoped - and asked one at a
        //    time, as the page's EvaluatePoliciesAsync asks them, so that a refusal names the policy that
        //    actually said no: the store policy, or the write policy with the capability named.
        StudioScope tenantless = scope with { TenantId = null };

        if (!await authorization.IsAuthorizedAsync(tenantless, capability: null, cancellationToken).ConfigureAwait(false))
        {
            // Named BrowseDatabase in the ring, as the write policy's refusal below is: the store policy saying
            // no is no reason to show the name somebody asked the browser for to everybody it says yes to.
            audit.RecordScopeDenied(tenantless, StorePolicyName(settings), action, target, StudioCapability.BrowseDatabase);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.StorePolicy, settings.Text.BrowseStorePolicyDenial);
        }

        if (!await authorization.IsAuthorizedAsync(tenantless, StudioCapability.BrowseDatabase, cancellationToken).ConfigureAwait(false))
        {
            // Named BrowseDatabase in the ring, so the Activity screen shows the refusal - and the name it was
            // aimed at - only to a visitor who may browse too (StudioActionLog.GetVisibleAsync).
            audit.RecordScopeDenied(tenantless, WritePolicyName(settings), action, target, StudioCapability.BrowseDatabase);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.WritePolicy, settings.Text.BrowseWritePolicyDenial);
        }

        // 3. The scope, resolved with no tenant - so no tenant is discovered - and the capability named. The
        //    resolver asks both policies again (it is the one place a scope is looked up, and it never skips
        //    them); a policy that changes its mind between the two asks is refused as the write policy.
        try
        {
            ResolvedSource resolved = await resolver
                .ResolveAsync(tenantless, nameof(StudioCapability.BrowseDatabase), cancellationToken)
                .ConfigureAwait(false);

            return new DatabaseBrowseGrant(resolved, DatabaseRefusal.None, null);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(tenantless, WritePolicyName(settings), action, target, StudioCapability.BrowseDatabase);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.WritePolicy, settings.Text.BrowseWritePolicyDenial);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            audit.Record(action, target, succeeded: false, exception.Message, StudioCapability.BrowseDatabase, tenantless);
            return new DatabaseBrowseGrant(null, DatabaseRefusal.Unavailable, exception.Message);
        }
    }

    /// <summary>
    /// The row gate, for a reader of one relation's rows: the capability, the tenant-less scope with the
    /// capability named, the schema against the studio's <c>BrowsableSchemas</c> option, then the
    /// relation looked up in the catalog and judged - whose it is, what it is, what it reads, whether the
    /// role may select from it. Every refusal is audited under <paramref name="action" /> unless
    /// <paramref name="record" /> says otherwise; a grant is not, because the caller knows what it then read
    /// and records that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="record" /> is <see langword="false" /> only for a read nobody asked for: a count a page
    /// makes on its own while it draws something else, such as how many rows of a table point at the
    /// document on screen. "The reading role has no <c>SELECT</c> on it" is then an answer the page shows
    /// beside the row, not a refusal of anything the visitor did, and recording it would put an entry in the
    /// ring and a line in the log every time somebody opened a document.
    /// </para>
    /// <para>
    /// It covers the relation-level answers only - not found, not browsable, whose it is, what it reads,
    /// the role's privilege. The capability and the two policies are still recorded by
    /// <see cref="RequireBrowseAsync" /> whatever it says, because they are security events: a caller passing
    /// <see langword="false" /> asks the page's own gate first, unaudited, so those can only refuse here when
    /// the answer changed in between.
    /// </para>
    /// </remarks>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="schema">The schema, as the URL had it. Only compared, never quoted.</param>
    /// <param name="name">The relation, as the URL had it. Only compared, never quoted.</param>
    /// <param name="action">What the audit ring calls the read.</param>
    /// <param name="record">Whether a refusal of the relation itself is audited; <see langword="true" /> unless the read is passive.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<DatabaseRowAccessResult> RequireRowAccessAsync(
        StudioScope scope,
        string schema,
        string name,
        string action = RowsAction,
        bool record = true,
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
            if (record)
            {
                audit.Record(action, target, succeeded: false, reason, StudioCapability.BrowseDatabase, tenantless);
            }

            return DatabaseRowAccessResult.Refused(refusal, reason);
        }

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
        {
            return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema ?? string.Empty, name ?? string.Empty));
        }

        // 3. The schema, settled from the options alone where it can be: an entry list that cannot admit it
        //    is refused before the database is asked anything.
        IReadOnlyList<string> entries = [.. settings.BrowsableSchemas];

        if (!BrowsableSchemaMatcher.MightMatch(entries, schema))
        {
            return Refuse(DatabaseRefusal.SchemaNotBrowsable, DatabaseGate.SchemaDenial(schema, entries, settings.Text));
        }

        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead gateRead = await GateAsync(tenantless, grant.Resolved, cancellationToken).ConfigureAwait(false);

            if (!gateRead.Succeeded)
            {
                return Refuse(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = known = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                // Said as not-found rather than "not browsable": a "*" that does not admit a schema is, from
                // here, a schema that is not there.
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name));
            }

            // 4. The catalog. From here on the names are the catalog's, not the caller's.
            CatalogRelationDetail? relation = await catalog
                .RelationAsync(grant.Resolved.Target, schema, name, cancellationToken)
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
                new DatabaseRowGrant(grant.Resolved, relation, summary.Ownership, detail.Columns, detail.RowKey, gate),
                DatabaseRefusal.None,
                null);
        }
        catch (Exception exception) when (IsCatalogFailure(exception))
        {
            return Refuse(DatabaseRefusal.Unavailable, CatalogFailure(exception, known));
        }
    }

    /// <summary>What the audit entry names as the target.</summary>
    internal static string Target(string? schema, string? name) => (schema ?? string.Empty) + "." + (name ?? string.Empty);

    /// <summary>Whether an exception is a catalog read going wrong, which a page renders rather than throws.</summary>
    internal static bool IsCatalogFailure(Exception exception) =>
        exception is NpgsqlException or InvalidOperationException or TimeoutException
        && exception is not OperationCanceledException;

    /// <summary>The sentence for a catalog read that failed.</summary>
    /// <remarks>
    /// Postgres' message is the visitor's to read only with every withheld schema masked; a caller that holds
    /// the visitor's gate passes it to <see cref="CatalogFailure(Exception, DatabaseGate?)" />. With none - the
    /// gate itself was being read - what failed was the schema list, whose failures name no schema.
    /// </remarks>
    internal static string CatalogFailure(Exception exception) => CatalogFailure(exception, gate: null);

    /// <summary>
    /// The sentence for a catalog read that failed, with Postgres' own words masked through
    /// <paramref name="gate" />'s withheld schemas (<see cref="PostgresErrorText" />) when there is one.
    /// </summary>
    internal static string CatalogFailure(Exception exception, DatabaseGate? gate)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string message = exception is PostgresException postgres
            ? postgres.SqlState + " " + postgres.MessageText
            : exception.Message;

        return "The catalog could not be read: " +
               (gate is null ? message : PostgresErrorText.Redact(message, gate.WithheldSchemas) ?? message);
    }

    /// <summary>The policy a write-policy refusal names in event 9203.</summary>
    internal static string WritePolicyName(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.WritePolicy ?? settings.ScopePolicy ?? "(none)";
    }

    /// <summary>The policy a store-policy refusal names in event 9203.</summary>
    internal static string StorePolicyName(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ScopePolicy ?? "(none)";
    }
}
