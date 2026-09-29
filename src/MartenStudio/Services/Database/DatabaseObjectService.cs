using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services.Database;

/// <summary>
/// The database browser's reads: the gate, the classification and the catalog, put together per visitor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order, for anything past the store's own structure.</b> The store the scope names is found first
/// (<see cref="DatabaseAccess.FindStoreAsync(StudioScope, string?, string?, CancellationToken)" />) - the store
/// policy for the visitor's own scope, audited when it refuses, then the
/// registration and its declarations, which is how the service knows whether the schema asked about is one
/// of the store's own - and nothing about the database or the tenant is asked yet. A request that needs
/// <c>BrowseDatabase</c> then goes through <see cref="DatabaseAccess.RequireBrowseAsync" /> - the capability,
/// then the two policies for the database as a whole, then the scope resolved <em>with no tenant</em> - and
/// the schema against <see cref="MartenStudioOptions.BrowsableSchemas" />, settled from the options alone
/// wherever it can be, all before the database is asked anything about that schema. Every one of those
/// refusals is audited. The visitor's own tenant-bearing scope is resolved only on the path that needs no
/// capability - the store's own structure - so a request the capability refuses never runs tenant
/// discovery, which can query the database.
/// </para>
/// <para>
/// <b>Structure of the store's own schemas needs nothing</b>: it is what the Schema screen has always
/// shown. Neither does a definition of Marten's own objects there, nor the value of Marten's own
/// sequences. Everything else in <see cref="GetDefinitionAsync" /> and <see cref="GetSequenceValueAsync" /> is
/// a <c>BrowseDatabase</c> read.
/// </para>
/// <para>
/// <b>Nothing is thrown at a page</b> but cancellation: refusals, unreadable configurations and failed
/// catalog reads are results with a <see cref="DatabaseRefusal" /> and a sentence.
/// </para>
/// </remarks>
internal sealed class DatabaseObjectService : IDatabaseObjectService
{
    /// <summary>How many objects a list shows when the caller does not say.</summary>
    internal const int DefaultListLimit = 500;

    /// <summary>What the audit ring calls reading a sequence's value.</summary>
    internal const string SequenceValueAction = "Read database sequence value";

    private readonly StudioScopeResolver resolver;
    private readonly DatabaseAccess access;
    private readonly DatabaseCatalog catalog;
    private readonly StudioActionLog audit;
    private readonly IOptions<MartenStudioOptions> options;

    public DatabaseObjectService(
        StudioScopeResolver resolver,
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioActionLog audit,
        IOptions<MartenStudioOptions> options)
    {
        this.resolver = resolver;
        this.access = access;
        this.catalog = catalog;
        this.audit = audit;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (ResolvedScope? resolved, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return DatabaseBrowserOverview.Unavailable(refused!);
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseBrowserOverview.Unavailable(gateRead.Reason ?? "The database browser is unavailable.", gateRead.Refusal);
            }

            DatabaseGate gate = gateRead.Gate;

            // Counted by a query of its own, grouped by schema - exact, whatever a list's cap is.
            IReadOnlyList<CatalogObjectCount> counts = await catalog
                .CountsAsync(resolved.Database, gate.VisibleSchemas, gate.Classifier.HiddenTablesAndRoutines(), cancellationToken)
                .ConfigureAwait(false);

            return DatabaseObjectAssembler.Overview(gate, counts);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseBrowserOverview.Unavailable(DatabaseAccess.CatalogFailure(exception));
        }
    }

    /// <inheritdoc />
    public async Task<DatabaseObjectList> ListAsync(
        StudioScope scope,
        DatabaseObjectQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        int limit = Math.Clamp(query.Limit ?? DefaultListLimit, 1, DatabaseCatalog.ListCap);
        DatabaseObjectQuery clamped = query with
        {
            Limit = limit,
            Schema = string.IsNullOrEmpty(query.Schema) ? null : query.Schema,
            NameFilter = string.IsNullOrWhiteSpace(query.NameFilter) ? null : query.NameFilter.Trim(),
        };

        Route route = await RouteAsync(
                scope,
                clamped.Schema is { } asked
                    ? declared => IsStoreSchema(declared, asked) ? RouteKind.Structure : RouteKind.Browse
                    : static _ => RouteKind.Structure,
                clamped.Schema,
                null,
                DatabaseAccess.ListAction,
                clamped.Schema ?? string.Empty,
                cancellationToken)
            .ConfigureAwait(false);

        if (!route.Allowed)
        {
            return DatabaseObjectList.Refused(clamped, route.Refusal, route.Reason!);
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectList.Refused(clamped, gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = gateRead.Gate;

            if (clamped.Schema is { } schema && !gate.CanSeeStructure(schema))
            {
                string reason = "There is no schema '" + schema + "' among the schemas this studio shows you.";
                audit.Record(DatabaseAccess.ListAction, schema, succeeded: false, reason, StudioCapability.BrowseDatabase, scope with { TenantId = null });

                return DatabaseObjectList.Refused(clamped, DatabaseRefusal.NotFound, reason, gate.State);
            }

            IReadOnlyList<string> schemas = clamped.Schema is { } one ? [one] : gate.VisibleSchemas;

            CatalogSnapshot snapshot = await catalog
                .SnapshotAsync(route.Resolved.Database, schemas, PartsFor(clamped.Category), clamped.NameFilter, cancellationToken)
                .ConfigureAwait(false);

            return DatabaseObjectAssembler.List(gate, snapshot, clamped, limit);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectList.Refused(clamped, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
        }
    }

    /// <inheritdoc />
    public async Task<DatabaseObjectDetail> GetObjectAsync(
        StudioScope scope,
        string schema,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
        {
            return DatabaseObjectDetail.Unavailable(
                DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema ?? string.Empty, name ?? string.Empty));
        }

        string target = DatabaseAccess.Target(schema, name);

        Route route = await RouteAsync(
                scope,
                declared => IsStoreSchema(declared, schema) ? RouteKind.Structure : RouteKind.Browse,
                schema,
                name,
                DatabaseAccess.ObjectAction,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        if (!route.Allowed)
        {
            return DatabaseObjectDetail.Unavailable(route.Refusal, route.Reason!);
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectDetail.Unavailable(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                string reason = DatabaseObjectAssembler.NotFound(schema, name);
                audit.Record(DatabaseAccess.ObjectAction, target, succeeded: false, reason, StudioCapability.BrowseDatabase, scope with { TenantId = null });

                return DatabaseObjectDetail.Unavailable(DatabaseRefusal.NotFound, reason, gate.State);
            }

            CatalogRelationDetail? detail = await catalog
                .RelationAsync(route.Resolved.Database, schema, name, cancellationToken)
                .ConfigureAwait(false);

            return detail is null
                ? DatabaseObjectDetail.Unavailable(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), gate.State)
                : DatabaseObjectAssembler.Detail(gate, detail);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectDetail.Unavailable(DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
        }
    }

    /// <inheritdoc />
    public async Task<DatabaseObjectDefinition> GetDefinitionAsync(
        StudioScope scope,
        DatabaseObjectRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reference);

        string schema = reference.Schema ?? string.Empty;
        string name = reference.Name ?? string.Empty;
        string target = DatabaseAccess.Target(schema, name);

        if (schema.Length == 0 || name.Length == 0
            || (reference.Kind == DatabaseObjectKind.Trigger && string.IsNullOrEmpty(reference.Table)))
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name));
        }

        if (NotApplicable(reference.Kind) is { } notApplicable)
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotApplicable, notApplicable);
        }

        DatabaseObjectOwnership? ownership = null;

        Route route = await RouteAsync(
                scope,
                declared =>
                {
                    // Whose it is, from the name alone - which is all that is needed to know whether this read
                    // is the Schema screen's (Marten's own, in the store's own schemas) or a BrowseDatabase read.
                    ownership = ClassifyByName(declared.Classifier!, reference);

                    return ownership switch
                    {
                        // A hidden type's table, or a trigger on one: not there, as far as this visitor is
                        // concerned - said from the name alone, before anything is asked.
                        null => RouteKind.NotThere,
                        { IsMarten: true } when IsStoreSchema(declared, schema) => RouteKind.Structure,
                        _ => RouteKind.Browse,
                    };
                },
                schema,
                name,
                DatabaseAccess.DefinitionAction,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        if (!route.Allowed || ownership is null)
        {
            return DatabaseObjectDefinition.Unavailable(reference, route.Refusal, route.Reason!);
        }

        StudioScope tenantless = scope with { TenantId = null };

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectDefinition.Unavailable(reference, gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                string reason = DatabaseObjectAssembler.NotFound(schema, name);
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, reason, StudioCapability.BrowseDatabase, tenantless);

                return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, reason);
            }

            DatabaseRowAccess allowed = gate.DefinitionAccess(schema, ownership);
            if (!allowed.Allowed)
            {
                // The store's own schema, somebody else's object, and a BrowsableSchemas that does not admit
                // the schema: the capability and the policy said yes, the list did not.
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, allowed.Reason, StudioCapability.BrowseDatabase, tenantless);

                return DatabaseObjectDefinition.Unavailable(reference, allowed.Refusal, allowed.Reason ?? "Refused.");
            }

            DatabaseObjectDefinition definition = await ReadDefinitionAsync(route.Resolved, gate, reference, cancellationToken)
                .ConfigureAwait(false);

            if (definition.Refusal is DatabaseRefusal.HiddenDependency or DatabaseRefusal.WithheldDependency)
            {
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, definition.Reason, StudioCapability.BrowseDatabase, tenantless);
            }

            return definition;
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
        }
    }

    /// <inheritdoc />
    public async Task<DatabaseSequenceValue> GetSequenceValueAsync(
        StudioScope scope,
        string schema,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var reference = new DatabaseObjectRef(DatabaseObjectKind.Sequence, schema ?? string.Empty, name ?? string.Empty);
        string target = DatabaseAccess.Target(schema, name);

        if (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(name))
        {
            return DatabaseSequenceValue.Unavailable(
                reference, DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema ?? string.Empty, name ?? string.Empty));
        }

        DatabaseObjectOwnership? byName = null;

        Route route = await RouteAsync(
                scope,
                declared =>
                {
                    // By name, as the list's CanReadValue decides it: Marten's own sequences in the store's own
                    // schemas are the Schema screen's; anybody else's value is a BrowseDatabase read; and a
                    // per-tenant event sequence - its name is a tenant id - is never listed or read.
                    byName = declared.Classifier!.ClassifySequence(schema, name, null, null);

                    return byName switch
                    {
                        null => RouteKind.NotThere,
                        { IsMarten: true } when IsStoreSchema(declared, schema) => RouteKind.Structure,
                        _ => RouteKind.Browse,
                    };
                },
                schema,
                name,
                SequenceValueAction,
                target,
                cancellationToken)
            .ConfigureAwait(false);

        if (!route.Allowed || byName is null)
        {
            return DatabaseSequenceValue.Unavailable(reference, route.Refusal, route.Reason!);
        }

        StudioScope tenantless = scope with { TenantId = null };

        DatabaseSequenceValue Refuse(DatabaseRefusal refusal, string reason, bool audited)
        {
            if (audited)
            {
                audit.Record(SequenceValueAction, target, succeeded: false, reason, StudioCapability.BrowseDatabase, tenantless);
            }

            return DatabaseSequenceValue.Unavailable(reference, refusal, reason);
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return Refuse(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.", audited: false);
            }

            DatabaseGate gate = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: true);
            }

            // The list row first: lock-free, and what says whose it is - a sequence one of a hidden type's
            // columns owns is not there for this visitor.
            CatalogSequence? sequence = await catalog
                .SequenceAsync(route.Resolved.Database, schema, name, cancellationToken)
                .ConfigureAwait(false);

            if (sequence is null
                || gate.Classifier.ClassifySequence(sequence.Schema, sequence.Name, sequence.OwnerSchema, sequence.OwnerTable) is null)
            {
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: false);
            }

            DatabaseRowAccess allowed = gate.DefinitionAccess(sequence.Schema, DatabaseObjectAssembler.SequenceValueOwnership(gate, sequence));
            if (!allowed.Allowed)
            {
                return Refuse(allowed.Refusal, allowed.Reason ?? "Refused.", audited: true);
            }

            if (!sequence.CanReadValue)
            {
                return Refuse(
                    DatabaseRefusal.NoPrivilege,
                    options.Value.SqlConsoleRole is { } role
                        ? "The role '" + role + "' (MartenStudioOptions.SqlConsoleRole) has neither SELECT nor USAGE on it."
                        : "The store's Postgres role has neither SELECT nor USAGE on it.",
                    audited: false);
            }

            // Only now anything that locks: one sequence, briefly, in a transaction of its own.
            CatalogSequenceValue? value = await catalog
                .SequenceValueAsync(route.Resolved.Database, sequence.Schema, sequence.Name, cancellationToken)
                .ConfigureAwait(false);

            return value is { CanReadValue: true }
                ? new DatabaseSequenceValue(reference, value.LastValue, DatabaseRefusal.None, null)
                : Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: false);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return Refuse(DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception), audited: false);
        }
    }

    private async Task<DatabaseObjectDefinition> ReadDefinitionAsync(
        ResolvedScope resolved,
        DatabaseGate gate,
        DatabaseObjectRef reference,
        CancellationToken cancellationToken)
    {
        string schema = reference.Schema;
        string name = reference.Name;
        string notFound = DatabaseObjectAssembler.NotFound(schema, name);

        switch (reference.Kind)
        {
            case DatabaseObjectKind.View:
            case DatabaseObjectKind.MaterializedView:
            {
                // The view's own detail first, and what it reads and calls judged directly - whatever it is
                // called and whoever owns it. A view named mt_ over a hidden type's table is Marten's by name
                // and needs no capability to ask about, and its query names that table all the same.
                CatalogRelationDetail? detail = await catalog
                    .RelationAsync(resolved.Database, schema, name, cancellationToken)
                    .ConfigureAwait(false);

                if (detail is null || detail.Relation.Kind is not ("v" or "m"))
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);

                if (shown.Relation is null)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, shown.Refusal, shown.Reason ?? notFound);
                }

                if (DatabaseObjectAssembler.DefinitionRefusal(gate, detail) is { } refused)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, refused.Refusal, refused.Reason ?? notFound);
                }

                CatalogDefinition? view = await catalog
                    .ViewDefinitionAsync(resolved.Database, schema, name, cancellationToken)
                    .ConfigureAwait(false);

                return view?.Sql is { } sql
                    ? new DatabaseObjectDefinition(reference, gate.Redact(sql), null, DatabaseRefusal.None, null)
                    : DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
            }

            case DatabaseObjectKind.Function:
            case DatabaseObjectKind.Procedure:
            case DatabaseObjectKind.WindowFunction:
            case DatabaseObjectKind.Aggregate:
            {
                if (await IdentityArgumentsAsync(resolved, gate, reference, cancellationToken).ConfigureAwait(false) is not { } arguments)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                CatalogDefinition? routine = await catalog
                    .RoutineDefinitionAsync(resolved.Database, schema, name, arguments, cancellationToken)
                    .ConfigureAwait(false);

                if (routine is null)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                if (routine.RoutineKind == "a" || routine.Sql is null)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotApplicable, AggregateHasNoBody);
                }

                return gate.Classifier.MentionsHiddenTable(routine.Sql)
                    ? DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.HiddenDependency, HiddenTableNamedDenial)
                    : new DatabaseObjectDefinition(reference, gate.Redact(routine.Sql), null, DatabaseRefusal.None, null);
            }

            case DatabaseObjectKind.Trigger:
            {
                CatalogDefinition? trigger = await catalog
                    .TriggerDefinitionAsync(resolved.Database, schema, reference.Table!, name, cancellationToken)
                    .ConfigureAwait(false);

                if (trigger?.Sql is not { } sql)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                // The definition ends EXECUTE FUNCTION schema.name(): a function in a withheld schema is
                // refused, not masked, because the list already blanks that function's name.
                if (trigger.FunctionSchema is { } functionSchema && gate.IsWithheld(functionSchema))
                {
                    return DatabaseObjectDefinition.Unavailable(
                        reference,
                        DatabaseRefusal.WithheldDependency,
                        "Its function is in a schema you cannot see, and the definition names it.");
                }

                // A WHEN clause or an argument can name a hidden type's table as well as a body can.
                return gate.Classifier.MentionsHiddenTable(sql)
                    ? DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.HiddenDependency, HiddenTableNamedDenial)
                    : new DatabaseObjectDefinition(reference, gate.Redact(sql), null, DatabaseRefusal.None, null);
            }

            default:
            {
                // Through the same read the Types list makes, so a type's "used by N columns" is counted
                // over the same schemas here as there - the visitor's, and nothing withheld.
                CatalogSnapshot snapshot = await catalog
                    .SnapshotAsync(resolved.Database, gate.VisibleSchemas, CatalogParts.Types, name, cancellationToken)
                    .ConfigureAwait(false);

                CatalogType? type = snapshot.Types.Items.FirstOrDefault(x =>
                    string.Equals(x.Schema, schema, StringComparison.Ordinal)
                    && string.Equals(x.Name, name, StringComparison.Ordinal));

                return type is null
                    ? DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound)
                    : new DatabaseObjectDefinition(reference, null, DatabaseObjectAssembler.Type(gate, type), DatabaseRefusal.None, null);
            }
        }
    }

    /// <summary>
    /// The real identity arguments of the overload <paramref name="reference" /> names, or
    /// <see langword="null" /> when there is none this visitor could have been shown.
    /// </summary>
    /// <remarks>
    /// A list shows a signature with every withheld schema masked, and a page asks for the definition with
    /// what it was shown. So a masked signature is matched back to the one overload whose own signature
    /// masks to it (two that mask alike are ambiguous, and neither is returned); and a signature that
    /// itself names a withheld schema is one no list showed - somebody typed it to find out whether the
    /// type exists - and is answered as not found.
    /// </remarks>
    private async Task<string?> IdentityArgumentsAsync(
        ResolvedScope resolved,
        DatabaseGate gate,
        DatabaseObjectRef reference,
        CancellationToken cancellationToken)
    {
        string asked = reference.Arguments ?? string.Empty;

        if (!asked.Contains(WithheldNames.Token, StringComparison.Ordinal))
        {
            return string.Equals(gate.Redact(asked), asked, StringComparison.Ordinal) ? asked : null;
        }

        CatalogSnapshot overloads = await catalog
            .SnapshotAsync(resolved.Database, [reference.Schema], CatalogParts.Routines, reference.Name, cancellationToken)
            .ConfigureAwait(false);

        string[] matching =
        [
            .. overloads.Routines.Items
                .Where(x => string.Equals(x.Schema, reference.Schema, StringComparison.Ordinal)
                    && string.Equals(x.Name, reference.Name, StringComparison.Ordinal)
                    && string.Equals(gate.Redact(x.IdentityArguments), asked, StringComparison.Ordinal))
                .Select(static x => x.IdentityArguments),
        ];

        return matching.Length == 1 ? matching[0] : null;
    }

    /// <summary>What a routine's or trigger's definition says when its text names a hidden type's table.</summary>
    internal const string HiddenTableNamedDenial =
        "Its definition names a table whose document type the host hides from the studio " +
        "(MartenStudioOptions.IsDocumentTypeVisible), so it is not shown.";

    /// <summary>What an aggregate's definition says.</summary>
    internal const string AggregateHasNoBody =
        "An aggregate has no body Postgres will print: pg_get_functiondef refuses aggregates (42809).";

    /// <summary>Why a kind has no definition here, or <see langword="null" /> when it has one.</summary>
    internal static string? NotApplicable(DatabaseObjectKind kind) => kind switch
    {
        DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable =>
            "A table has no definition beyond its columns, keys and indexes, which its detail shows.",
        DatabaseObjectKind.Sequence => "A sequence has no definition beyond the settings its row shows.",
        DatabaseObjectKind.Aggregate => AggregateHasNoBody,
        _ => null,
    };

    /// <summary>
    /// Which way a read goes, decided before the database is asked anything: the store's own structure,
    /// resolved with the visitor's own scope, or a <c>BrowseDatabase</c> read, refused or resolved with no
    /// tenant.
    /// </summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="decide">
    /// Given the declarations, which way this read goes. Called once, after the store is found and before
    /// anything else.
    /// </param>
    /// <param name="schema">The schema asked about, to settle against the entry list; <see langword="null" /> for none.</param>
    /// <param name="name">The object asked about, for a "not there" sentence; <see langword="null" /> for none.</param>
    /// <param name="action">What the audit ring calls the read.</param>
    /// <param name="target">What it was aimed at.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    private async Task<Route> RouteAsync(
        StudioScope scope,
        Func<DatabaseDeclarations, RouteKind> decide,
        string? schema,
        string? name,
        string action,
        string target,
        CancellationToken cancellationToken)
    {
        // 1. The store, and its declarations - the store policy for the visitor's own scope, and no database.
        //    A store-policy refusal is audited there, under this read's action and target.
        DatabaseStoreLookup store = await access.FindStoreAsync(scope, action, target, cancellationToken).ConfigureAwait(false);

        if (!store.Found)
        {
            return Route.Refused(DatabaseRefusal.Unavailable, store.Refused ?? "The store could not be found.");
        }

        if (!store.Declarations.Succeeded)
        {
            return Route.Refused(DatabaseRefusal.Unavailable, store.Declarations.Failure!);
        }

        RouteKind kind = decide(store.Declarations);

        if (kind == RouteKind.NotThere)
        {
            // Settled by the name alone - a hidden type's table or function, a per-tenant sequence - and
            // answered as a missing object is. In a schema the store does not declare, a missing object is
            // a BrowseDatabase read that the capability, the policies and the schema list answer first: so
            // this one is asked the same questions, in the same order, before it is "not there" - otherwise
            // "not found" where every other name gets "capability off" says the name is a hidden type's.
            if (schema is not null && !IsStoreSchema(store.Declarations, schema))
            {
                Route browse = await BrowseAsync(scope, schema, action, target, cancellationToken).ConfigureAwait(false);

                if (!browse.Allowed)
                {
                    return browse;
                }
            }

            return Route.Refused(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema ?? string.Empty, name ?? string.Empty));
        }

        if (kind == RouteKind.Structure)
        {
            // 2a. The store's own structure: the visitor's own scope, tenant and all, as every other screen
            //     resolves it. No capability is needed, so nothing is refused here that a capability would.
            (ResolvedScope? resolved, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

            return resolved is null
                ? Route.Refused(DatabaseRefusal.Unavailable, refused!)
                : new Route(resolved, DatabaseRefusal.None, null);
        }

        // 2b. A BrowseDatabase read.
        return await BrowseAsync(scope, schema, action, target, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A <c>BrowseDatabase</c> read's route: the capability, the two policies and the tenant-less scope, then
    /// the schema against the entry list - before the database is asked anything, and never with the
    /// visitor's tenant. Every refusal is audited.
    /// </summary>
    private async Task<Route> BrowseAsync(
        StudioScope scope,
        string? schema,
        string action,
        string target,
        CancellationToken cancellationToken)
    {
        DatabaseBrowseGrant grant = await access.RequireBrowseAsync(scope, action, target, cancellationToken)
            .ConfigureAwait(false);

        if (!grant.Allowed)
        {
            return Route.Refused(grant.Refusal, grant.Reason ?? "Refused.");
        }

        if (schema is not null)
        {
            IReadOnlyList<string> entries = [.. options.Value.BrowsableSchemas];

            if (!BrowsableSchemaMatcher.MightMatch(entries, schema))
            {
                string reason = DatabaseGate.SchemaDenial(schema, entries);
                audit.Record(action, target, succeeded: false, reason, StudioCapability.BrowseDatabase, scope with { TenantId = null });

                return Route.Refused(DatabaseRefusal.SchemaNotBrowsable, reason);
            }
        }

        return new Route(grant.Resolved, DatabaseRefusal.None, null);
    }

    private static DatabaseObjectOwnership? ClassifyByName(DatabaseObjectClassifier classifier, DatabaseObjectRef reference) =>
        reference.Kind switch
        {
            DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView or DatabaseObjectKind.Table
                or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable =>
                classifier.ClassifyRelation(reference.Schema, reference.Name),
            DatabaseObjectKind.Function or DatabaseObjectKind.Procedure or DatabaseObjectKind.Aggregate
                or DatabaseObjectKind.WindowFunction => classifier.ClassifyRoutine(reference.Schema, reference.Name),
            DatabaseObjectKind.Trigger => classifier.ClassifyTrigger(reference.Schema, reference.Table ?? string.Empty, reference.Name),
            DatabaseObjectKind.Sequence => classifier.ClassifySequence(reference.Schema, reference.Name, null, null),
            _ => DatabaseObjectClassifier.ClassifyType(reference.Schema, reference.Name),
        };

    private static bool IsStoreSchema(DatabaseDeclarations declared, string schema) =>
        declared.StoreSchemas.Contains(schema, StringComparer.Ordinal);

    private static CatalogParts PartsFor(DatabaseObjectCategory category) => category switch
    {
        DatabaseObjectCategory.Tables => CatalogParts.Relations | CatalogParts.ForeignKeys,
        DatabaseObjectCategory.Views => CatalogParts.Relations | CatalogParts.ViewDependencies,
        DatabaseObjectCategory.Functions => CatalogParts.Routines,
        DatabaseObjectCategory.Triggers => CatalogParts.Triggers,
        DatabaseObjectCategory.Sequences => CatalogParts.Sequences,
        _ => CatalogParts.Types,
    };

    /// <summary>
    /// The visitor's scope, resolved as a read - or the resolver's own sentence, which says the same thing
    /// for a scope that is refused and one that does not exist.
    /// </summary>
    private async Task<(ResolvedScope? Resolved, string? Refused)> ResolveAsync(
        StudioScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is StudioNotAuthorizedException or KeyNotFoundException
            or StudioStoreUnavailableException)
        {
            return (null, exception.Message);
        }
    }

    /// <summary>Which way a read goes, decided from the declarations alone.</summary>
    private enum RouteKind
    {
        /// <summary>The store's own structure, or Marten's own objects in it: no capability needed.</summary>
        Structure,

        /// <summary>A <c>BrowseDatabase</c> read.</summary>
        Browse,

        /// <summary>Not there, as far as this visitor is concerned, from its name alone.</summary>
        NotThere,
    }

    /// <summary>Where a read goes: a resolved scope, or the refusal.</summary>
    /// <param name="Resolved">The scope - the visitor's own, or the tenant-less one a grant carries.</param>
    /// <param name="Refusal">Which gate refused.</param>
    /// <param name="Reason">The sentence.</param>
    private sealed record Route(ResolvedScope? Resolved, DatabaseRefusal Refusal, string? Reason)
    {
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Resolved))]
        public bool Allowed => Resolved is not null;

        public static Route Refused(DatabaseRefusal refusal, string reason) => new(null, refusal, reason);
    }
}
