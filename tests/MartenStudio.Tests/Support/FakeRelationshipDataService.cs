using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Relationships;

using GraphModel = MartenStudio.Services.Relationships.RelationshipGraph;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What the Relationships screen and the inbound panel are given, said outright by a test.
/// </summary>
/// <remarks>
/// A hand-written fake rather than a mocking framework (AGENTS.md package budget): the seam is one
/// interface with two methods, and a fake that a person can read the whole of is worth more here than a
/// call-matching DSL.
/// </remarks>
internal sealed class FakeRelationshipDataService : IRelationshipDataService
{
    /// <summary>What <see cref="GetGraphAsync" /> answers.</summary>
    public GraphModel Graph { get; set; } = Sample();

    /// <summary>What <see cref="GetReferencedByAsync" /> answers.</summary>
    public ReferencedBy Referenced { get; set; } = ReferencedBy.None;

    /// <summary>What the two methods throw, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>How many times the page has read, so a refresh can be told from the first load.</summary>
    public int Reads { get; private set; }

    /// <summary>The scope the last read was made with.</summary>
    public StudioScope? LastScope { get; private set; }

    public Task<GraphModel> GetGraphAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Reads++;
        LastScope = scope;

        return Failure is null ? Task.FromResult(Graph) : Task.FromException<GraphModel>(Failure);
    }

    public Task<ReferencedBy> GetReferencedByAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        Reads++;
        LastScope = scope;

        return Failure is null ? Task.FromResult(Referenced) : Task.FromException<ReferencedBy>(Failure);
    }

    /// <summary>
    /// The demo store's own shape plus the two drift cases: an order pointing at a customer that the
    /// database really has, an invoice pointing at the same customer that it does not, and a constraint
    /// on notes that nobody declared.
    /// </summary>
    public static GraphModel Sample() => new(
        [
            Node("customer", "Customer"),
            Node("invoice", "Invoice"),
            Node("note", "Note"),
            Node("order", "Order"),
        ],
        [
            new RelationshipEdge("invoice", "customer", "customer_id", "CustomerId", true, false, "NoAction"),
            new RelationshipEdge("note", "order", "order_id", null, false, true, "Cascade"),
            new RelationshipEdge("order", "customer", "customer_id", "CustomerId", true, true, "NoAction"),
        ],
        [
            new UnmatchedForeignKey(
                "legacy_fkey",
                "order",
                "legacy_id",
                "studio_sample.legacy_customers",
                false,
                true,
                "It points at a table no document type of this store maps."),
        ],
        new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero));

    /// <summary>A graph with document types and no keys between them at all.</summary>
    public static GraphModel WithoutKeys() => new(
        [Node("customer", "Customer")],
        [],
        [],
        new DateTimeOffset(2026, 9, 14, 10, 30, 0, TimeSpan.Zero));

    /// <summary>The key of a table node in <see cref="WithTables" />.</summary>
    public static string TableKey(string schema, string name) => RelationshipTableNode.KeyFor(schema, name);

    /// <summary>
    /// The demo store beside a Quartz-shaped and a legacy-shaped schema: two document types with a key
    /// between them; <c>qrtz_triggers → qrtz_job_details</c> on a composite key led by <c>sched_name</c>; a
    /// detail table keyed by exactly its parent's key; a table pointing into a document table with
    /// <c>ON DELETE CASCADE</c>; a <c>NOT VALID</c> key; an isolated table; and one key into a schema the
    /// visitor is not shown.
    /// </summary>
    public static GraphModel WithTables() => new GraphModel(
        [
            Node("customer", "Customer") with { Schema = "studio_sample", Table = "mt_doc_customer" },
            Node("order", "Order") with { Schema = "studio_sample", Table = "mt_doc_order" },
        ],
        [
            new RelationshipEdge("order", "customer", "customer_id", "CustomerId", true, true, "NoAction", "mt_doc_order_customer_id_fkey")
            {
                Columns = "customer_id",
                LinkedColumns = "id",
            },
            new RelationshipEdge(
                TableKey("legacy", "customer_credit"), "customer", "customer_id", null, false, true, "Cascade", "customer_credit_customer_id_fkey")
            {
                Columns = "customer_id",
                LinkedColumns = "id",
                FromIsTable = true,
            },
            new RelationshipEdge(
                TableKey("legacy", "purchase_order_lines"), TableKey("legacy", "purchase_orders"), "order_id", null, false, true, "NoAction", "purchase_order_lines_order_id_fkey")
            {
                Columns = "order_id",
                LinkedColumns = "order_id",
                Validated = false,
                FromIsTable = true,
                ToIsTable = true,
            },
            new RelationshipEdge(
                TableKey("quartz", "qrtz_simple_triggers"), TableKey("quartz", "qrtz_triggers"), "sched_name, trigger_name, trigger_group", null, false, true, "Cascade", "qrtz_simple_triggers_sched_name_trigger_name_trigger_group_fkey")
            {
                Columns = "sched_name, trigger_name, trigger_group",
                LinkedColumns = "sched_name, trigger_name, trigger_group",
                FromIsTable = true,
                ToIsTable = true,
            },
            new RelationshipEdge(
                TableKey("quartz", "qrtz_triggers"), TableKey("quartz", "qrtz_job_details"), "job_name, job_group", null, false, true, "NoAction", "qrtz_triggers_sched_name_job_name_job_group_fkey")
            {
                Columns = "sched_name, job_name, job_group",
                LinkedColumns = "sched_name, job_name, job_group",
                FromIsTable = true,
                ToIsTable = true,
            },
        ],
        [],
        new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.Zero))
    {
        Tables =
        [
            Table("legacy", "audit_log"),
            Table("legacy", "customer_credit"),
            Table("legacy", "purchase_order_lines"),
            Table("legacy", "purchase_orders"),
            Table("quartz", "qrtz_job_details", "Quartz.NET"),
            Table("quartz", "qrtz_simple_triggers", "Quartz.NET"),
            Table("quartz", "qrtz_triggers", "Quartz.NET"),
        ],
        Withheld = [new WithheldForeignKey("customer", VisibleEndIsTarget: true)],
        VisibleSchemas = ["studio_sample", "studio_sample_events", "legacy", "quartz"],
    };

    /// <summary>
    /// <see cref="WithTables" /> with two document types no key reaches at all - the column of unconnected
    /// boxes the Both view used to draw down the left of the diagram.
    /// </summary>
    public static GraphModel WithTablesAndUnkeyedDocuments()
    {
        GraphModel graph = WithTables();

        return graph with
        {
            Nodes = [.. graph.Nodes, Node("auditnote", "AuditNote"), Node("vehicle", "Vehicle")],
        };
    }

    /// <summary>A table node, coloured by its schema the way the builder colours one.</summary>
    public static RelationshipTableNode Table(string schema, string name, string? recognisedAs = null) =>
        new(
            TableKey(schema, name),
            schema,
            name,
            RelationshipTableNode.SchemaHue(schema),
            DocumentCount.Estimate(42),
            MartenStudio.Services.Database.DatabaseObjectOwner.Other,
            recognisedAs,
            InStoreSchema: false);

    /// <summary>The inbound panel's own sample: one exact count and one that hit the cap.</summary>
    public static ReferencedBy SampleReferencedBy() => new(
        [
            new ReferencedByEntry("invoice", "customer_id", "CustomerId", 100, 1000, true, true, true),
            new ReferencedByEntry("order", "customer_id", "CustomerId", 215, 3, false, true, true),
        ]);

    private static RelationshipNode Node(string alias, string type) =>
        new(alias, type, CollectionColorizer.HueFor(alias), DocumentCount.Estimate(12), false, []);
}
