using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Database;

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
/// a capability then goes through <see cref="DatabaseAccess.RequireDefinitionAsync" /> - the capability,
/// then the two policies for the database as a whole, then the scope resolved <em>with no tenant</em> - and
/// the schema against the studio's <c>BrowsableSchemas</c> option, settled from the options alone
/// wherever it can be, all before the database is asked anything about that schema. Every one of those
/// refusals is audited. The visitor's own tenant-bearing scope is resolved only on the path that needs no
/// capability - the store's own structure - so a request the capability refuses never runs tenant
/// discovery, which can query the database.
/// </para>
/// <para>
/// <b>Two capabilities open this half, and the gate says which schemas each does.</b> <c>BrowseDatabase</c> admits every
/// schema <c>BrowsableSchemas</c> admits; <c>ViewDefinitions</c>, which <c>BrowseDatabase</c> implies, admits the ones it
/// names exactly, and no row of any. <see cref="DatabaseAccess.RequireDefinitionAsync" /> lets a read go as far as the
/// catalog when either passes; <see cref="DatabaseGate" />, read through <see cref="DatabaseAccess.ObjectGateAsync" />,
/// draws the line by schema, owner and kind. Rows are another gate - <see cref="DatabaseAccess.RequireRowAccessAsync" />,
/// <c>BrowseDatabase</c>'s alone.
/// </para>
/// <para>
/// <b>Structure of the store's own schemas needs nothing</b>: it is what the Schema screen has always
/// shown. Neither does a definition of Marten's own objects there, nor the value of Marten's own
/// sequences. Everything else in <see cref="GetDefinitionAsync" /> is a <c>BrowseDatabase</c> read, or, for the host's
/// own objects in a schema <c>BrowsableSchemas</c> names exactly, a <c>ViewDefinitions</c> one. A sequence's current
/// value (<see cref="GetSequenceValueAsync" />) is data, not a definition - it says how many rows a table has had - and is
/// <c>BrowseDatabase</c>'s alone.
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

    private readonly StudioSourceResolver resolver;
    private readonly DatabaseAccess access;
    private readonly DatabaseCatalog catalog;
    private readonly StudioActionLog audit;
    private readonly IStudioSettings settings;

    public DatabaseObjectService(
        StudioSourceResolver resolver,
        DatabaseAccess access,
        DatabaseCatalog catalog,
        StudioActionLog audit,
        IStudioSettings settings)
    {
        this.resolver = resolver;
        this.access = access;
        this.catalog = catalog;
        this.audit = audit;
        this.settings = settings;
    }

    /// <inheritdoc />
    public async Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        (ResolvedSource? resolved, DatabaseRefusal refusal, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return DatabaseBrowserOverview.Unavailable(refused!, refusal);
        }

        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead gateRead = await access.ObjectGateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseBrowserOverview.Unavailable(gateRead.Reason ?? "The database browser is unavailable.", gateRead.Refusal);
            }

            DatabaseGate gate = known = gateRead.Gate;

            // Counted by a query of its own, grouped by schema - exact, whatever a list's cap is.
            IReadOnlyList<CatalogObjectCount> counts = await catalog
                .CountsAsync(resolved.Target, gate.VisibleSchemas, gate.Classifier.HiddenTablesAndRoutines(), cancellationToken)
                .ConfigureAwait(false);

            return DatabaseObjectAssembler.Overview(gate, counts);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseBrowserOverview.Unavailable(DatabaseAccess.CatalogFailure(exception, known));
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

        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead gateRead = await access.ObjectGateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectList.Refused(clamped, gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = known = gateRead.Gate;

            if (clamped.Schema is { } schema && !gate.CanSeeStructure(schema))
            {
                string reason = "There is no schema '" + schema + "' among the schemas this studio shows you.";
                audit.Record(DatabaseAccess.ListAction, schema, succeeded: false, reason, access.ReadCapability, scope with { TenantId = null });

                return DatabaseObjectList.Refused(clamped, DatabaseRefusal.NotFound, reason, gate.State);
            }

            IReadOnlyList<string> schemas = clamped.Schema is { } one ? [one] : gate.VisibleSchemas;

            CatalogSnapshot snapshot = await catalog
                .SnapshotAsync(route.Resolved.Target, schemas, PartsFor(clamped.Category), clamped.NameFilter, cancellationToken)
                .ConfigureAwait(false);

            return DatabaseObjectAssembler.List(gate, snapshot, clamped, limit);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectList.Refused(clamped, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception, known));
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

        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead gateRead = await access.ObjectGateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectDetail.Unavailable(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = known = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                string reason = DatabaseObjectAssembler.NotFound(schema, name);
                audit.Record(DatabaseAccess.ObjectAction, target, succeeded: false, reason, access.ReadCapability, scope with { TenantId = null });

                return DatabaseObjectDetail.Unavailable(DatabaseRefusal.NotFound, reason, gate.State);
            }

            CatalogRelationDetail? detail = await catalog
                .RelationAsync(route.Resolved.Target, schema, name, cancellationToken)
                .ConfigureAwait(false);

            return detail is null
                ? DatabaseObjectDetail.Unavailable(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), gate.State)
                : DatabaseObjectAssembler.Detail(gate, detail);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectDetail.Unavailable(DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception, known));
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
                        { IsMarten: true } when IsStoreSchema(declared, schema) && DeclaresAStore(declared) => RouteKind.Structure,
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
        DatabaseGate? known = null;

        try
        {
            DatabaseGateRead gateRead = await access.ObjectGateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return DatabaseObjectDefinition.Unavailable(reference, gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.");
            }

            DatabaseGate gate = known = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                string reason = DatabaseObjectAssembler.NotFound(schema, name);
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, reason, access.ReadCapability, tenantless);

                return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, reason);
            }

            DatabaseRowAccess allowed = gate.DefinitionAccess(schema, ownership);
            if (!allowed.Allowed)
            {
                // The store's own schema, somebody else's object, and a BrowsableSchemas that does not admit
                // the schema - or, for a visitor ViewDefinitions alone admits, one only "*" does, or an object Marten
                // owns: the capability and the policy said yes, the list did not.
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, allowed.Reason, access.ReadCapability, tenantless);

                return DatabaseObjectDefinition.Unavailable(reference, allowed.Refusal, allowed.Reason ?? "Refused.");
            }

            DatabaseObjectDefinition definition = await ReadDefinitionAsync(route.Resolved, gate, reference, cancellationToken)
                .ConfigureAwait(false);

            if (definition.Refusal is DatabaseRefusal.HiddenDependency or DatabaseRefusal.WithheldDependency)
            {
                audit.Record(DatabaseAccess.DefinitionAction, target, succeeded: false, definition.Reason, access.ReadCapability, tenantless);
            }

            return definition;
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            // A definition read Postgres refused - pg_get_functiondef on something it will not print, a lock
            // it gave up on - is said in Postgres' words, masked through this visitor's gate.
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception, known));
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
                    // schemas are the Schema screen's; anybody else's value is a BrowseDatabase read - data, not a
                    // definition, so ViewDefinitions does not open it; and a per-tenant event sequence - its name
                    // is a tenant id - is never listed or read.
                    byName = declared.Classifier!.ClassifySequence(schema, name, null, null);

                    return byName switch
                    {
                        null => RouteKind.NotThere,
                        { IsMarten: true } when IsStoreSchema(declared, schema) && DeclaresAStore(declared) => RouteKind.Structure,
                        _ => RouteKind.Browse,
                    };
                },
                schema,
                name,
                SequenceValueAction,
                target,
                cancellationToken,
                browseOnly: true)
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

        DatabaseGate? known = null;

        try
        {
            // BrowseDatabase's own gate: a value is data, so the exactly named schemas ViewDefinitions admits are not its.
            DatabaseGateRead gateRead = await access.GateAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false);
            if (!gateRead.Succeeded)
            {
                return Refuse(gateRead.Refusal, gateRead.Reason ?? "The database browser is unavailable.", audited: false);
            }

            DatabaseGate gate = known = gateRead.Gate;

            if (!gate.CanSeeStructure(schema))
            {
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: true);
            }

            // The list row first: lock-free, and what says whose it is - a sequence one of a hidden type's
            // columns owns is not there for this visitor.
            CatalogSequence? sequence = await catalog
                .SequenceAsync(route.Resolved.Target, schema, name, cancellationToken)
                .ConfigureAwait(false);

            if (sequence is null
                || gate.Classifier.ClassifySequence(sequence.Schema, sequence.Name, sequence.OwnerSchema, sequence.OwnerTable) is null)
            {
                return Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: false);
            }

            DatabaseRowAccess allowed = gate.SequenceValueAccess(sequence.Schema, DatabaseObjectAssembler.SequenceValueOwnership(gate, sequence));
            if (!allowed.Allowed)
            {
                return Refuse(allowed.Refusal, allowed.Reason ?? "Refused.", audited: true);
            }

            if (!sequence.CanReadValue)
            {
                return Refuse(
                    DatabaseRefusal.NoPrivilege,
                    await SequenceNoPrivilegeAsync(scope, route.Resolved, cancellationToken).ConfigureAwait(false),
                    audited: false);
            }

            // Only now anything that locks: one sequence, briefly, in a transaction of its own.
            CatalogSequenceValue? value = await catalog
                .SequenceValueAsync(route.Resolved.Target, sequence.Schema, sequence.Name, cancellationToken)
                .ConfigureAwait(false);

            return value is { CanReadValue: true }
                ? new DatabaseSequenceValue(reference, value.LastValue, DatabaseRefusal.None, null)
                {
                    // Its range and the columns it feeds, read with it from the catalog (W5-B1): what its headroom is judged by.
                    Headroom = value is { DataType: { } type, ReadAt: { } readAt }
                        ? new SequenceHeadroomFacts(type, value.Start, value.Increment, value.Minimum, value.Maximum, value.Cycles, value.ColumnTypes, value.ColumnMaximum, readAt)
                        : null,
                }
                : Refuse(DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), audited: false);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return Refuse(DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception, known), audited: false);
        }
    }

    /// <summary>What a sequence value refused for the reading role's want of a privilege says, when the role is not named.</summary>
    internal const string SequenceNoPrivilegeUnnamed = "The role the studio reads as has neither SELECT nor USAGE on it.";

    /// <summary>
    /// Why the reading role may not read a sequence's value - naming <c>SqlConsoleRole</c> only to a visitor past the Database
    /// gate (H1's R4, H1b).
    /// </summary>
    /// <remarks>
    /// The name of the role the studio reads as is Database-gate information (D29): the store policy asked about the database
    /// as a whole, with no tenant. The value of Marten's own sequence in the store's own schemas is the Schema screen's
    /// structure, read with the visitor's own scope, tenant and all - so a visitor the store policy passes only for a tenant
    /// reached this sentence, and was told the role. A scope with no tenant has passed that gate already: the resolver asked
    /// it, by the spelling and again by the registered names, and so does every <c>BrowseDatabase</c> read.
    /// </remarks>
    private async Task<string> SequenceNoPrivilegeAsync(StudioScope scope, ResolvedSource resolved, CancellationToken cancellationToken)
    {
        if (settings.SqlConsoleRole is not { } role)
        {
            return "The store's Postgres role has neither SELECT nor USAGE on it.";
        }

        bool named = resolved.Scope.TenantId is null
            || await access.PassesDatabaseGateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);

        return named
            ? "The role '" + role + "' (" + settings.OptionName("SqlConsoleRole") + ") has neither SELECT nor USAGE on it."
            : SequenceNoPrivilegeUnnamed;
    }

    private async Task<DatabaseObjectDefinition> ReadDefinitionAsync(
        ResolvedSource resolved,
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
                    .RelationAsync(resolved.Target, schema, name, cancellationToken)
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
                    .ViewDefinitionAsync(resolved.Target, schema, name, cancellationToken)
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
                    .RoutineDefinitionAsync(resolved.Target, schema, name, arguments, cancellationToken)
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
                    .TriggerDefinitionAsync(resolved.Target, schema, reference.Table!, name, cancellationToken)
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
                    .SnapshotAsync(resolved.Target, gate.VisibleSchemas, CatalogParts.Types, name, cancellationToken)
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
        ResolvedSource resolved,
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
            .SnapshotAsync(resolved.Target, [reference.Schema], CatalogParts.Routines, reference.Name, cancellationToken)
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
    /// <param name="browseOnly">
    /// Whether a read that is not the store's own structure is <c>BrowseDatabase</c>'s alone - a sequence's value, which is
    /// data - rather than either way into a definition (<see cref="DatabaseAccess.RequireDefinitionAsync" />).
    /// </param>
    private async Task<Route> RouteAsync(
        StudioScope scope,
        Func<DatabaseDeclarations, RouteKind> decide,
        string? schema,
        string? name,
        string action,
        string target,
        CancellationToken cancellationToken,
        bool browseOnly = false)
    {
        // 1. The store, and its declarations - the store policy for the visitor's own scope, and no database.
        //    A store-policy refusal is audited there, under this read's action and target.
        DatabaseStoreLookup store = await access.FindStoreAsync(scope, action, target, cancellationToken).ConfigureAwait(false);

        if (!store.Found)
        {
            // Labelled as the lookup answered (H1): the store policy's refusal of this scope, a key nothing is registered
            // under, or a source that will not build - each with the sentence that already says which.
            return Route.Refused(
                store.RefusedAs == DatabaseRefusal.None ? DatabaseRefusal.Unavailable : store.RefusedAs,
                store.Refused ?? "The store could not be found.");
        }

        if (!store.Declarations.Succeeded)
        {
            // Another store that will not build is named only to a visitor its store policy passes (D27) - asked about
            // this database by its own identity, never the visitor's spelling of it (D29, H1). No database is resolved
            // yet, so the resolver is asked for it, with no tenant; a scope that will not resolve names no other store.
            return Route.Refused(
                DatabaseRefusal.Unavailable,
                await DescribeFailureAsync(store, scope, cancellationToken).ConfigureAwait(false));
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
                Route browse = await BrowseAsync(scope, schema, action, target, browseOnly, cancellationToken).ConfigureAwait(false);

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
            (ResolvedSource? resolved, DatabaseRefusal refusal, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);

            return resolved is null
                ? Route.Refused(refusal, refused!)
                : new Route(resolved, DatabaseRefusal.None, null);
        }

        // 2b. A BrowseDatabase read - or, for all but a sequence's value, a ViewDefinitions one.
        return await BrowseAsync(scope, schema, action, target, browseOnly, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A read's route past the store's own structure: the capability, the two policies and the tenant-less scope, then
    /// the schema against the entry list - before the database is asked anything, and never with the
    /// visitor's tenant. Every refusal is audited. <paramref name="browseOnly" /> asks <c>BrowseDatabase</c> alone
    /// (<see cref="DatabaseAccess.RequireBrowseAsync" />); otherwise either way into a definition passes it.
    /// </summary>
    private async Task<Route> BrowseAsync(
        StudioScope scope,
        string? schema,
        string action,
        string target,
        bool browseOnly,
        CancellationToken cancellationToken)
    {
        DatabaseBrowseGrant grant = browseOnly
            ? await access.RequireBrowseAsync(scope, action, target, cancellationToken).ConfigureAwait(false)
            : await access.RequireDefinitionAsync(scope, action, target, cancellationToken).ConfigureAwait(false);

        if (!grant.Allowed)
        {
            return Route.Refused(grant.Refusal, grant.Reason ?? "Refused.");
        }

        if (schema is not null)
        {
            IReadOnlyList<string> entries = [.. settings.BrowsableSchemas];

            if (!BrowsableSchemaMatcher.MightMatch(entries, schema))
            {
                string reason = DatabaseGate.SchemaDenial(schema, entries, settings.Text);
                audit.Record(
                    action, target, succeeded: false, reason, browseOnly ? StudioCapability.BrowseDatabase : access.ReadCapability, scope with { TenantId = null });

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

    /// <summary>
    /// Whether a registered store declares the objects classified here - what makes Marten's own definitions and
    /// sequence values in the store's schemas the Schema screen's structure. A studio over plain data sources has none,
    /// so an object only named like Marten's is a <c>BrowseDatabase</c> read there, like every other definition.
    /// </summary>
    private static bool DeclaresAStore(DatabaseDeclarations declared) => declared.Classifier is { Stores.Count: > 0 };

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
    /// The visitor's scope, resolved as a read - or the resolver's own sentence, and which answer it is (H1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Labelled honestly, enumerating nothing.</b> A scope the store policy refuses is
    /// <see cref="DatabaseRefusal.ScopeRefused" /> - the resolver asks the policy before it looks anything up, so that
    /// label and its sentence are the same whether or not the store, the database or the tenant exist. A scope the policy
    /// passed that names nothing is <see cref="DatabaseRefusal.NotFound" />, and a source that will not build
    /// <see cref="DatabaseRefusal.Unavailable" />: the resolver's sentence for each already says which, so the label tells
    /// the visitor nothing the sentence does not. Before, all three were "unavailable", and a store policy's refusal read
    /// as a fault.
    /// </para>
    /// <para>
    /// <b>Nothing else is thrown at the page either.</b> Resolving can query the database - a dynamic tenancy's list of
    /// databases, tenant discovery - and a failure there carries Postgres' own words, which may name a schema this
    /// visitor's gate withholds; with no gate read yet there is nothing to mask them with, so they are withheld and only
    /// the SQLSTATE is said (D27's rule for the Schema screen).
    /// </para>
    /// </remarks>
    private async Task<(ResolvedSource? Resolved, DatabaseRefusal Refusal, string? Refused)> ResolveAsync(
        StudioScope scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false), DatabaseRefusal.None, null);
        }
        catch (StudioNotAuthorizedException exception)
        {
            return (null, DatabaseRefusal.ScopeRefused, exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return (null, DatabaseRefusal.NotFound, exception.Message);
        }
        catch (StudioStoreUnavailableException exception)
        {
            return (null, DatabaseRefusal.Unavailable, exception.Message);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return (null, DatabaseRefusal.Unavailable, ScopeReadFailure(exception));
        }
    }

    /// <summary>
    /// What a page is told when resolving the scope itself failed in the database: the SQLSTATE, never Postgres' words -
    /// no gate has been read to mask a schema they name.
    /// </summary>
    /// <remarks>
    /// Any other failure's words are said, escaped and bounded (<see cref="StudioLogValue.CapText" />, H1b): an
    /// <see cref="InvalidOperationException" /> or a <see cref="TimeoutException" /> here can be a host's - its
    /// <c>IPostgresDatabaseSource.ListAsync</c> threw it - and so carry anything, a line break or a megabyte included, onto
    /// the page and into whatever records the refusal.
    /// </remarks>
    internal static string ScopeReadFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception is PostgresException postgres
            ? "The scope could not be resolved: Postgres refused a read (" + postgres.SqlState + "). Its own words are withheld: "
              + "the schemas they may name could not be checked for you."
            : exception is NpgsqlException
                ? "The scope could not be resolved: the database could not be reached."
                : "The scope could not be resolved: " + StudioLogValue.CapText(exception.Message);
    }

    /// <summary>
    /// <see cref="DatabaseAccess.DescribeFailureAsync" /> for a store whose declarations could not all be read, before the
    /// page has resolved a database: another store is named only to a visitor that store's policy passes for this database,
    /// asked by this database's own identity (D29) and never the visitor's spelling of it. The identity is the resolver's -
    /// the scope resolved with no tenant, so no tenant is discovered, and through the one place a source's databases are
    /// listed - and a scope that will not resolve so names no other store (fail closed).
    /// </summary>
    private async Task<string> DescribeFailureAsync(DatabaseStoreLookup store, StudioScope scope, CancellationToken cancellationToken)
    {
        DatabaseDeclarations declared = store.Declarations!;
        string unnamed = declared.Failure ?? "The database browser is unavailable.";

        if (declared.FailedStoreKey is null || declared.NamedFailure is null)
        {
            return unnamed;
        }

        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(scope with { TenantId = null }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return unnamed;
        }

        return await access.DescribeFailureAsync(declared, resolved.Target.Identity, cancellationToken).ConfigureAwait(false);
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
    private sealed record Route(ResolvedSource? Resolved, DatabaseRefusal Refusal, string? Reason)
    {
        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Resolved))]
        public bool Allowed => Resolved is not null;

        public static Route Refused(DatabaseRefusal refusal, string reason) => new(null, refusal, reason);
    }
}
