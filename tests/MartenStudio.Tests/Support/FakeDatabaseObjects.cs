using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Support;

/// <summary>
/// A sample database for the browser's page tests: the demo store's own schemas, a Quartz.NET schema and a
/// legacy schema holding one of every case the browser draws differently.
/// </summary>
/// <remarks>
/// <para>
/// Built from the DTOs the service really returns, so a page test exercises the same shapes a live page
/// gets. Every object is here once, in <see cref="Objects" />, and the list, the overview and the detail
/// are derived from that one set so they cannot disagree with each other.
/// </para>
/// <para>
/// What is in it: a visible Marten document table and the event store's table (rows never read), a
/// Marten-managed projection table (rows readable), Quartz's <c>qrtz_job_details</c> and
/// <c>qrtz_triggers</c> with a composite key and a composite foreign key, a partitioned table with two
/// partitions, a foreign table, a table whose name cannot be quoted, a table the role cannot read, a view,
/// an unpopulated materialized view, a view over a hidden type's table, a function, a procedure, an
/// aggregate, a SECURITY DEFINER function with no <c>search_path</c>, an enabled and a disabled trigger,
/// the event sequence with two per-tenant sequences rolled into it, an owned sequence, and an enum, a
/// domain, a composite and a range type.
/// </para>
/// </remarks>
internal static class FakeDatabaseObjects
{
    /// <summary>The demo store's document schema.</summary>
    public const string StoreSchema = "studio_sample";

    /// <summary>The demo store's event schema.</summary>
    public const string EventSchema = "studio_sample_events";

    /// <summary>The Quartz.NET schema.</summary>
    public const string Quartz = "quartz";

    /// <summary>The legacy schema.</summary>
    public const string Legacy = "legacy";

    private static readonly DatabaseObjectOwnership OtherOwner = new(DatabaseObjectOwner.Other);
    private static readonly DatabaseObjectOwnership QuartzOwner = new(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET");
    private static readonly DatabaseObjectOwnership InfrastructureOwner = new(DatabaseObjectOwner.MartenInfrastructure, "default");

    /// <summary>The gate, open or shut.</summary>
    /// <param name="refusal">
    /// <see cref="DatabaseRefusal.None" /> for an open gate with <c>quartz</c> and <c>legacy</c> browsable, or
    /// the gate that is shut.
    /// </param>
    public static DatabaseAccessState Access(DatabaseRefusal refusal = DatabaseRefusal.None)
    {
        bool open = refusal == DatabaseRefusal.None;

        return new DatabaseAccessState(
            CapabilityEnabled: refusal is not (DatabaseRefusal.CapabilityOff or DatabaseRefusal.ReadOnly),
            ReadOnly: refusal == DatabaseRefusal.ReadOnly,
            Authorized: refusal switch
            {
                DatabaseRefusal.CapabilityOff or DatabaseRefusal.ReadOnly => null,
                DatabaseRefusal.WritePolicy => false,
                _ => true,
            },
            BrowsableSchemasConfigured: refusal != DatabaseRefusal.SchemaNotBrowsable,
            StoreSchemas: [StoreSchema, EventSchema],
            BrowsableSchemas: open ? [Legacy, Quartz] : [],
            WithheldSchemaCount: open ? 1 : 3,
            Refusal: refusal,
            Denial: refusal switch
            {
                DatabaseRefusal.None => null,
                DatabaseRefusal.ReadOnly => DatabaseGate.ReadOnlyDenial,
                DatabaseRefusal.CapabilityOff => DatabaseGate.CapabilityDenial,
                DatabaseRefusal.WritePolicy => DatabaseGate.WritePolicyDenial,
                DatabaseRefusal.SchemaNotBrowsable => DatabaseGate.EmptyListDenial,
                _ => "The database browser is unavailable.",
            });
    }

    /// <summary>The overview: the store's two schemas, and the two browsable ones when the gate is open.</summary>
    public static DatabaseBrowserOverview Overview(DatabaseRefusal refusal = DatabaseRefusal.None)
    {
        DatabaseAccessState access = Access(refusal);
        IReadOnlyList<DatabaseObjectSummary> visible = Visible(access);

        List<DatabaseSchemaSummary> schemas = [];

        foreach (string schema in access.StoreSchemas.Concat(access.BrowsableSchemas))
        {
            int Count(DatabaseObjectCategory category) =>
                visible.Count(x => x.Schema == schema && x.Category == category);

            schemas.Add(new DatabaseSchemaSummary(
                schema,
                access.StoreSchemas.Contains(schema),
                access.BrowsableSchemas.Contains(schema),
                Count(DatabaseObjectCategory.Tables),
                Count(DatabaseObjectCategory.Views),
                Count(DatabaseObjectCategory.Functions),
                Count(DatabaseObjectCategory.Triggers),
                Count(DatabaseObjectCategory.Sequences),
                Count(DatabaseObjectCategory.Types)));
        }

        return new DatabaseBrowserOverview(schemas, access, false, DatabaseRefusal.None, null);
    }

    /// <summary>One tab's list, honouring the query's schema, owner, name filter and limit.</summary>
    public static DatabaseObjectList List(DatabaseObjectQuery query, DatabaseRefusal refusal = DatabaseRefusal.None)
    {
        ArgumentNullException.ThrowIfNull(query);

        DatabaseAccessState access = Access(refusal);
        int limit = query.Limit ?? 500;

        List<DatabaseObjectSummary> matching = [.. Visible(access).Where(x =>
            x.Category == query.Category
            && (query.Schema is null || x.Schema == query.Schema)
            && (query.NameFilter is null || x.Name.Contains(query.NameFilter, StringComparison.OrdinalIgnoreCase)))];

        int marten = matching.Count(static x => x.Ownership.IsMarten);

        List<DatabaseObjectSummary> shown = [.. matching.Where(x => query.Owner switch
        {
            DatabaseOwnerFilter.Marten => x.Ownership.IsMarten,
            DatabaseOwnerFilter.Other => !x.Ownership.IsMarten,
            _ => true,
        })];

        return new DatabaseObjectList(
            query with { Limit = limit },
            [.. shown.Take(limit)],
            new DatabaseOwnerCounts(matching.Count, marten, matching.Count - marten),
            shown.Count > limit,
            limit,
            access,
            DatabaseRefusal.None,
            null);
    }

    /// <summary>
    /// One relation's detail: Quartz's <c>qrtz_triggers</c> in full, a plain column set for anything else
    /// in <see cref="Objects" />, and not-found for the rest.
    /// </summary>
    public static DatabaseObjectDetail Detail(string schema, string name)
    {
        DatabaseAccessState access = Access();

        if (Visible(access).OfType<DatabaseRelationSummary>().FirstOrDefault(x => x.Schema == schema && x.Name == name)
            is not { } relation)
        {
            return DatabaseObjectDetail.Unavailable(
                DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(schema, name), access);
        }

        if (schema == Quartz && name == "qrtz_triggers")
        {
            return new DatabaseObjectDetail(
                relation,
                [
                    Column("sched_name", 1, "character varying(120)"),
                    Column("trigger_name", 2, "character varying(200)"),
                    Column("trigger_group", 3, "character varying(200)"),
                    Column("job_name", 4, "character varying(200)"),
                    Column("job_group", 5, "character varying(200)"),
                    Column("next_fire_time", 6, "bigint", nullable: true),
                    Column("trigger_state", 7, "character varying(16)"),
                    Column("job_data", 8, "bytea", nullable: true, sortable: false),
                ],
                new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "qrtz_triggers_pkey", ["sched_name", "trigger_name", "trigger_group"]),
                [
                    new DatabaseConstraintInfo("qrtz_triggers_pkey", DatabaseConstraintKind.PrimaryKey, "PRIMARY KEY (sched_name, trigger_name, trigger_group)"),
                ],
                [
                    new DatabaseIndexInfo("qrtz_triggers_pkey", "CREATE UNIQUE INDEX qrtz_triggers_pkey ON quartz.qrtz_triggers USING btree (sched_name, trigger_name, trigger_group)", true, true, true, false, ["sched_name", "trigger_name", "trigger_group"]),
                    new DatabaseIndexInfo("idx_qrtz_t_next_fire_time", "CREATE INDEX idx_qrtz_t_next_fire_time ON quartz.qrtz_triggers USING btree (next_fire_time)", false, false, true, false, ["next_fire_time"]),
                ],
                [],
                [
                    new DatabaseForeignKeyInfo(
                        "qrtz_triggers_sched_name_job_name_job_group_fkey",
                        Quartz, "qrtz_triggers", ["sched_name", "job_name", "job_group"],
                        Quartz, "qrtz_job_details", ["sched_name", "job_name", "job_group"],
                        true, QuartzOwner, true, "no action", "no action"),
                ],
                [
                    new DatabaseForeignKeyInfo(
                        "qrtz_simprop_triggers_sched_name_trigger_name_trigger_group_fkey",
                        Quartz, "qrtz_simprop_triggers", ["sched_name", "trigger_name", "trigger_group"],
                        Quartz, "qrtz_triggers", ["sched_name", "trigger_name", "trigger_group"],
                        true, QuartzOwner, true, "cascade", "no action"),
                ],
                [],
                access,
                DatabaseRefusal.None,
                null);
        }

        return new DatabaseObjectDetail(
            relation,
            [Column("id", 1, "bigint"), Column("name", 2, "text", nullable: true)],
            relation.HasPrimaryKey ? new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, name + "_pkey", ["id"]) : null,
            [],
            [],
            [],
            [],
            [],
            name == "secret_view"
                ? [new DatabaseViewDependency(null, null, DatabaseObjectKind.Table, 1, false, null)]
                : [],
            access,
            DatabaseRefusal.None,
            null);
    }

    /// <summary>A definition for whatever is asked: SQL for a view, a routine or a trigger, a description for a type.</summary>
    public static DatabaseObjectDefinition Definition(DatabaseObjectRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        return reference.Kind switch
        {
            DatabaseObjectKind.Aggregate => DatabaseObjectDefinition.Unavailable(
                reference, DatabaseRefusal.NotApplicable, "An aggregate has no body Postgres will print."),
            DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView => new DatabaseObjectDefinition(
                reference, " SELECT o.customer_id,\n    sum(o.amount) AS total\n   FROM legacy.orders o\n  GROUP BY o.customer_id;", null, DatabaseRefusal.None, null),
            DatabaseObjectKind.Function or DatabaseObjectKind.Procedure or DatabaseObjectKind.WindowFunction => new DatabaseObjectDefinition(
                reference, "CREATE OR REPLACE FUNCTION legacy.recalculate(order_id bigint)\n RETURNS numeric\n LANGUAGE plpgsql\nAS $function$\nbegin\n  return 0;\nend;\n$function$\n", null, DatabaseRefusal.None, null),
            DatabaseObjectKind.Trigger => new DatabaseObjectDefinition(
                reference, "CREATE TRIGGER orders_audit AFTER INSERT OR UPDATE ON legacy.orders FOR EACH ROW EXECUTE FUNCTION legacy.audit()", null, DatabaseRefusal.None, null),
            _ when Objects().OfType<DatabaseTypeSummary>().FirstOrDefault(x => x.Schema == reference.Schema && x.Name == reference.Name) is { } type =>
                new DatabaseObjectDefinition(reference, null, type, DatabaseRefusal.None, null),
            _ => DatabaseObjectDefinition.Unavailable(
                reference, DatabaseRefusal.NotFound, DatabaseObjectAssembler.NotFound(reference.Schema, reference.Name)),
        };
    }

    /// <summary>Every object in the sample, whatever the gate says.</summary>
    public static IReadOnlyList<DatabaseObjectSummary> Objects() =>
    [
        Table(StoreSchema, "mt_doc_customer", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "customer"),
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.MartenOwned, "A Marten document table's rows are browsed in Documents.")),
        Table(StoreSchema, "host_settings", OtherOwner),
        Table(EventSchema, "mt_events", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "default"),
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.MartenOwned, "The event store's tables are browsed in Streams and the Feed.")),
        Table(StoreSchema, "flat_orders", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, "default")),
        Table(Quartz, "qrtz_job_details", QuartzOwner, estimatedRows: 12, foreignKeysIn: 1),
        Table(Quartz, "qrtz_triggers", QuartzOwner, estimatedRows: 1204, foreignKeysOut: 1, foreignKeysIn: 1),
        Table(Quartz, "qrtz_simprop_triggers", QuartzOwner, estimatedRows: 0, foreignKeysOut: 1),
        Table(Legacy, "orders", OtherOwner, estimatedRows: 50_000),
        Table(Legacy, "measurements", OtherOwner, kind: DatabaseObjectKind.PartitionedTable, partitions: 2),
        Table(Legacy, "remote_orders", OtherOwner, kind: DatabaseObjectKind.ForeignTable, estimatedRows: null, foreignServer: "archive",
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.ForeignTable, "A foreign table is listed, never read.")),
        Table(Legacy, "bad\"name", OtherOwner, quotable: false,
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.Unquotable, "Its name cannot be put into SQL safely.")),
        Table(Legacy, "payroll", OtherOwner, readable: false,
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.NoPrivilege, "The store's Postgres role has no SELECT privilege on it.")),
        Table(Legacy, "keyless_log", OtherOwner, hasPrimaryKey: false),
        Table(Legacy, "order_totals", OtherOwner, kind: DatabaseObjectKind.View, estimatedRows: null, hasPrimaryKey: false),
        Table(Legacy, "order_stats", OtherOwner, kind: DatabaseObjectKind.MaterializedView, populated: false, hasPrimaryKey: false),
        Table(Legacy, "secret_view", OtherOwner, kind: DatabaseObjectKind.View, estimatedRows: null, hasPrimaryKey: false,
            rows: DatabaseRowAccess.Refused(DatabaseRefusal.HiddenDependency, "This view reads a table whose document type the host hides.")),

        Routine(StoreSchema, "mt_jsonb_patch", InfrastructureOwner, "doc jsonb, patch jsonb"),
        Routine(Legacy, "recalculate", OtherOwner, "order_id bigint"),
        Routine(Legacy, "recalculate", OtherOwner, "order_id bigint, force boolean"),
        Routine(Legacy, "archive_orders", OtherOwner, "before date", kind: DatabaseObjectKind.Procedure),
        Routine(Legacy, "sum_amounts", OtherOwner, "numeric", kind: DatabaseObjectKind.Aggregate, definition: false),
        Routine(Legacy, "unsafe_definer", OtherOwner, string.Empty, securityDefiner: true),

        Trigger(Legacy, "orders", "orders_audit", enabled: true),
        Trigger(Legacy, "orders", "orders_legacy_sync", enabled: false),

        new DatabaseSequenceSummary(DatabaseObjectKind.Sequence, EventSchema, "mt_events_sequence", InfrastructureOwner, null, true,
            "bigint", 1, 1, 1, long.MaxValue, false, true, 4210, null, null, null, false, 2),
        new DatabaseSequenceSummary(DatabaseObjectKind.Sequence, Legacy, "orders_id_seq", OtherOwner, null, true,
            "bigint", 1, 1, 1, long.MaxValue, false, true, 50_000, Legacy, "orders", "id", false, 0),

        Type(DatabaseObjectKind.EnumType, "order_status", labels: ["pending", "shipped", "cancelled"]),
        Type(DatabaseObjectKind.DomainType, "positive_amount", baseType: "numeric(12,2)", checks: ["CHECK (VALUE > 0::numeric)"]),
        Type(DatabaseObjectKind.CompositeType, "address", attributes: [new("street", "text"), new("city", "text")]),
        Type(DatabaseObjectKind.RangeType, "price_range", subtype: "numeric"),
    ];

    private static List<DatabaseObjectSummary> Visible(DatabaseAccessState access) =>
        [.. Objects().Where(x => access.StoreSchemas.Contains(x.Schema) || access.BrowsableSchemas.Contains(x.Schema))];

    private static DatabaseRelationSummary Table(
        string schema,
        string name,
        DatabaseObjectOwnership owner,
        DatabaseObjectKind kind = DatabaseObjectKind.Table,
        long? estimatedRows = 100,
        int foreignKeysOut = 0,
        int foreignKeysIn = 0,
        int partitions = 0,
        string? foreignServer = null,
        bool quotable = true,
        bool readable = true,
        bool populated = true,
        bool hasPrimaryKey = true,
        DatabaseRowAccess? rows = null) =>
        new(
            kind,
            schema,
            name,
            owner,
            null,
            quotable,
            estimatedRows,
            8192 * 4,
            hasPrimaryKey,
            foreignKeysOut,
            foreignKeysIn,
            partitions,
            false,
            populated,
            false,
            foreignServer,
            readable,
            rows ?? DatabaseRowAccess.Granted,
            kind is DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView);

    private static DatabaseRoutineSummary Routine(
        string schema,
        string name,
        DatabaseObjectOwnership owner,
        string arguments,
        DatabaseObjectKind kind = DatabaseObjectKind.Function,
        bool securityDefiner = false,
        bool definition = true) =>
        new(
            kind,
            schema,
            name,
            owner,
            null,
            true,
            arguments,
            kind == DatabaseObjectKind.Procedure ? null : "numeric",
            kind == DatabaseObjectKind.Aggregate ? "internal" : "plpgsql",
            "volatile",
            securityDefiner,
            false,
            [],
            definition);

    private static DatabaseTriggerSummary Trigger(string schema, string table, string name, bool enabled) =>
        new(
            DatabaseObjectKind.Trigger,
            schema,
            name,
            OtherOwner,
            null,
            true,
            table,
            OtherOwner,
            "AFTER",
            ["INSERT", "UPDATE"],
            true,
            enabled,
            enabled ? "O" : "D",
            schema,
            "audit",
            false,
            true);

    private static DatabaseTypeSummary Type(
        DatabaseObjectKind kind,
        string name,
        IReadOnlyList<string>? labels = null,
        string? baseType = null,
        IReadOnlyList<string>? checks = null,
        IReadOnlyList<DatabaseTypeAttribute>? attributes = null,
        string? subtype = null) =>
        new(
            kind,
            Legacy,
            name,
            OtherOwner,
            null,
            true,
            baseType,
            false,
            null,
            labels ?? [],
            checks ?? [],
            attributes ?? [],
            subtype,
            1,
            true);

    private static DatabaseColumnInfo Column(string name, int position, string type, bool nullable = false, bool sortable = true) =>
        new(name, position, type, nullable, null, DatabaseIdentityKind.None, false, sortable, null, true);
}
