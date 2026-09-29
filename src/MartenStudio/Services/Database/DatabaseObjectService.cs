using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Options;

namespace MartenStudio.Services.Database;

/// <summary>
/// The database browser's reads: the gate, the classification and the catalog, put together per visitor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order, for anything past the store's own structure.</b> The visitor's scope is resolved as a read
/// first - that is what finds the store and its declarations, which is how the service knows whether the
/// schema asked about is one of the store's own. Past that, a request that needs <c>BrowseDatabase</c>
/// goes through <see cref="DatabaseAccess.RequireBrowseAsync" /> - the capability, then the tenant-less
/// scope with the capability named - and then the schema against
/// <see cref="MartenStudioOptions.BrowsableSchemas" />, settled from the options alone wherever it can be,
/// all before the database is asked anything about that schema. Every one of those refusals is audited.
/// </para>
/// <para>
/// <b>Structure of the store's own schemas needs nothing</b>: it is what the Schema screen has always
/// shown. Neither does a definition of Marten's own objects there. Everything else in
/// <see cref="GetDefinitionAsync" /> is a <c>BrowseDatabase</c> read.
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

            CatalogSnapshot snapshot = await catalog
                .SnapshotAsync(resolved.Database, gate.VisibleSchemas, CatalogParts.Listings, null, cancellationToken)
                .ConfigureAwait(false);

            return DatabaseObjectAssembler.Overview(gate, snapshot);
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

        (ResolvedScope? resolved, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return DatabaseObjectList.Refused(clamped, DatabaseRefusal.Unavailable, refused!);
        }

        DatabaseDeclarations declared = access.ReadDeclarations(resolved);
        if (!declared.Succeeded)
        {
            return DatabaseObjectList.Refused(clamped, DatabaseRefusal.Unavailable, declared.Failure!);
        }

        if (clamped.Schema is { } asked && !IsStoreSchema(declared, asked))
        {
            if (await RefuseOutsideStoreAsync(scope, asked, DatabaseAccess.ListAction, asked, cancellationToken)
                    .ConfigureAwait(false) is { } refusal)
            {
                return DatabaseObjectList.Refused(clamped, refusal.Refusal, refusal.Reason);
            }
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
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
                .SnapshotAsync(resolved.Database, schemas, PartsFor(clamped.Category), clamped.NameFilter, cancellationToken)
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

        (ResolvedScope? resolved, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return DatabaseObjectDetail.Unavailable(DatabaseRefusal.Unavailable, refused!);
        }

        DatabaseDeclarations declared = access.ReadDeclarations(resolved);
        if (!declared.Succeeded)
        {
            return DatabaseObjectDetail.Unavailable(DatabaseRefusal.Unavailable, declared.Failure!);
        }

        string target = DatabaseAccess.Target(schema, name);

        if (!IsStoreSchema(declared, schema)
            && await RefuseOutsideStoreAsync(scope, schema, DatabaseAccess.ObjectAction, target, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return DatabaseObjectDetail.Unavailable(refusal.Refusal, refusal.Reason);
        }

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
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
                .RelationAsync(resolved.Database, schema, name, cancellationToken)
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

        (ResolvedScope? resolved, string? refused) = await ResolveAsync(scope, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.Unavailable, refused!);
        }

        DatabaseDeclarations declared = access.ReadDeclarations(resolved);
        if (!declared.Succeeded)
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.Unavailable, declared.Failure!);
        }

        // Whose it is, from the name alone - which is all that is needed to know whether this read is the
        // Schema screen's (Marten's own, in the store's own schemas) or a BrowseDatabase read.
        DatabaseObjectOwnership? ownership = ClassifyByName(declared.Classifier, reference);

        if (ownership is null)
        {
            // A hidden type's table, or a trigger on one: not there, as far as this visitor is concerned.
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name));
        }

        bool martensOwnInTheStore = IsStoreSchema(declared, schema) && ownership.IsMarten;

        if (!martensOwnInTheStore
            && await RefuseOutsideStoreAsync(scope, schema, DatabaseAccess.DefinitionAction, target, cancellationToken)
                .ConfigureAwait(false) is { } refusal)
        {
            return DatabaseObjectDefinition.Unavailable(reference, refusal.Refusal, refusal.Reason);
        }

        StudioScope tenantless = scope with { TenantId = null };

        try
        {
            DatabaseGateRead gateRead = await access.GateAsync(scope, resolved, cancellationToken).ConfigureAwait(false);
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

            return await ReadDefinitionAsync(resolved, gate, reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (DatabaseAccess.IsCatalogFailure(exception))
        {
            return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.Unavailable, DatabaseAccess.CatalogFailure(exception));
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
                // The view's own detail first: a view over a hidden type's table names that table in its
                // query, so its text is refused for the same reason its rows are.
                CatalogRelationDetail? detail = await catalog
                    .RelationAsync(resolved.Database, schema, name, cancellationToken)
                    .ConfigureAwait(false);

                if (detail is null || detail.Relation.Kind is not ("v" or "m"))
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                DatabaseObjectDetail shown = DatabaseObjectAssembler.Detail(gate, detail);

                if (shown.Relation is not { } relation)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, shown.Refusal, shown.Reason ?? notFound);
                }

                if (relation.Rows.Refusal is DatabaseRefusal.HiddenDependency || detail.Dependencies.Truncated)
                {
                    return DatabaseObjectDefinition.Unavailable(
                        reference,
                        DatabaseRefusal.HiddenDependency,
                        "This view's query reads a table whose document type the host hides from the studio " +
                        "(MartenStudioOptions.IsDocumentTypeVisible), so its definition is not shown.");
                }

                CatalogDefinition? view = await catalog
                    .ViewDefinitionAsync(resolved.Database, schema, name, cancellationToken)
                    .ConfigureAwait(false);

                return view?.Sql is { } sql
                    ? new DatabaseObjectDefinition(reference, sql, null, DatabaseRefusal.None, null)
                    : DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
            }

            case DatabaseObjectKind.Function:
            case DatabaseObjectKind.Procedure:
            case DatabaseObjectKind.WindowFunction:
            case DatabaseObjectKind.Aggregate:
            {
                CatalogDefinition? routine = await catalog
                    .RoutineDefinitionAsync(resolved.Database, schema, name, reference.Arguments ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);

                if (routine is null)
                {
                    return DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
                }

                return routine.RoutineKind == "a" || routine.Sql is null
                    ? DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotApplicable, AggregateHasNoBody)
                    : new DatabaseObjectDefinition(reference, routine.Sql, null, DatabaseRefusal.None, null);
            }

            case DatabaseObjectKind.Trigger:
            {
                CatalogDefinition? trigger = await catalog
                    .TriggerDefinitionAsync(resolved.Database, schema, reference.Table!, name, cancellationToken)
                    .ConfigureAwait(false);

                return trigger?.Sql is { } sql
                    ? new DatabaseObjectDefinition(reference, sql, null, DatabaseRefusal.None, null)
                    : DatabaseObjectDefinition.Unavailable(reference, DatabaseRefusal.NotFound, notFound);
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
    /// The shared front half of every read aimed at a schema that is not the store's own: the
    /// capability, the tenant-less scope with the capability named, and the schema against the entry list
    /// as far as the options alone can settle it. Audited; <see langword="null" /> when all three pass.
    /// </summary>
    private async Task<(DatabaseRefusal Refusal, string Reason)?> RefuseOutsideStoreAsync(
        StudioScope scope,
        string schema,
        string action,
        string target,
        CancellationToken cancellationToken)
    {
        DatabaseBrowseGrant grant = await access.RequireBrowseAsync(scope, action, target, cancellationToken)
            .ConfigureAwait(false);

        if (!grant.Allowed)
        {
            return (grant.Refusal, grant.Reason ?? "Refused.");
        }

        IReadOnlyList<string> entries = [.. options.Value.BrowsableSchemas];

        if (!BrowsableSchemaMatcher.MightMatch(entries, schema))
        {
            string reason = DatabaseGate.SchemaDenial(schema, entries);
            audit.Record(action, target, succeeded: false, reason, StudioCapability.BrowseDatabase, scope with { TenantId = null });

            return (DatabaseRefusal.SchemaNotBrowsable, reason);
        }

        return null;
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
}
