using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>Catalog rows for the row reader's unit tests, built the way the catalog reader builds them.</summary>
internal static class TableRowTestData
{
    /// <summary>One column, as the gate hands it to a row reader.</summary>
    public static DatabaseColumnInfo Column(
        string name,
        string type = "text",
        int position = 1,
        bool sortable = true,
        bool nullable = true,
        bool quotable = true) =>
        new(name, position, type, nullable, null, DatabaseIdentityKind.None, false, sortable, null, quotable);

    /// <summary>A relation's own catalog row.</summary>
    public static CatalogRelation Relation(
        string kind = "r",
        long? estimatedRows = 100,
        long sizeBytes = 8192,
        bool rowSecurity = false,
        string schema = "legacy",
        string name = "things") =>
        new(1, schema, name, kind, estimatedRows, sizeBytes, rowSecurity, true, false, true, 0, null, null, kind == "r");

    /// <summary>A relation's detail, with the indexes the verdict reads.</summary>
    public static CatalogRelationDetail Detail(CatalogRelation relation, params CatalogIndex[] indexes) =>
        new(relation, [], [], indexes, [], CatalogList<CatalogForeignKey>.Empty, CatalogList<CatalogViewDependency>.Empty);

    /// <summary>A valid, unconditional index over <paramref name="columns" />.</summary>
    public static CatalogIndex Index(string name, params string[] columns) =>
        new(name, "create index " + name, false, false, true, false, false, columns);

    /// <summary>The Quartz trigger table's key.</summary>
    public static DatabaseRowKey QuartzKey { get; } =
        new(DatabaseRowKeySource.PrimaryKey, "qrtz_triggers_pkey", ["sched_name", "trigger_name", "trigger_group"]);

    /// <summary>The Quartz trigger table's columns.</summary>
    public static IReadOnlyList<DatabaseColumnInfo> QuartzColumns { get; } =
    [
        Column("sched_name", position: 1, nullable: false),
        Column("trigger_name", position: 2, nullable: false),
        Column("trigger_group", position: 3, nullable: false),
        Column("next_fire_time", "bigint", position: 4),
        Column("job_data", "bytea", position: 5, sortable: true),
        Column("job_json", "json", position: 6, sortable: false),
    ];
}
