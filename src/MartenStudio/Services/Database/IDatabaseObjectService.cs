namespace MartenStudio.Services.Database;

/// <summary>
/// What the database browser reads: the schemas, the objects in them, one relation's structure, and a
/// definition - never a row (that is the row service's), and never anything that changes the database.
/// </summary>
/// <remarks>
/// <para>
/// An interface so a component test can drive the pages with <c>FakeDatabaseObjectService</c> and so the
/// gate has one implementation to live in (AGENTS.md hard rule 5). Every method takes a
/// <see cref="StudioScope" /> and resolves it; none takes an <c>IDocumentStore</c> or an
/// <c>IMartenDatabase</c>.
/// </para>
/// <para>
/// <b>Every failure is a value.</b> A refused gate, a scope the visitor may not have, a store whose
/// configuration will not read, a catalog read that timed out - each comes back as a result carrying a
/// <see cref="DatabaseRefusal" /> and a sentence, never as an exception to the page. Only cancellation is
/// thrown.
/// </para>
/// <para>
/// <b>Reads only, and no migration.</b> Every catalog read is <c>pg_catalog</c> inside the read-only
/// session (<c>DatabaseCatalog</c>); nothing reaches <c>AllSchemaNames()</c>, <c>AllObjects()</c> or a
/// Weasel migration (hard rule 14). The names avoid every verb <c>CapabilityGatingMatrixTests</c> treats as
/// mutating.
/// </para>
/// </remarks>
internal interface IDatabaseObjectService
{
    /// <summary>
    /// The schemas this visitor may see, with per-kind counts, the store's own flagged, the count of the
    /// others withheld (never their names), and the gate.
    /// </summary>
    /// <remarks>Needs no capability: without one it shows the store's own schemas, as the Schema screen does.</remarks>
    Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>One kind of object, optionally in one schema, by owner and name; bounded, with <c>Truncated</c>.</summary>
    /// <remarks>
    /// A schema outside the gate is refused - audited - with the sentence naming the gate, and nothing is
    /// read from the database for it.
    /// </remarks>
    Task<DatabaseObjectList> ListAsync(
        StudioScope scope,
        DatabaseObjectQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One table, view, materialized view or foreign table: columns, row key, constraints, indexes,
    /// triggers, foreign keys both ways, what a view reads, and whether its rows may be read.
    /// </summary>
    /// <remarks>
    /// A relation this visitor may not see and one that does not exist get the same answer once the gate
    /// has let the schema through; a schema the gate refuses is refused - audited - before the database is
    /// asked, with the sentence naming the gate.
    /// </remarks>
    Task<DatabaseObjectDetail> GetObjectAsync(
        StudioScope scope,
        string schema,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A definition: a view's query, a routine's body (never an aggregate's), a trigger's definition, a
    /// type's description.
    /// </summary>
    /// <remarks>
    /// Marten's own objects in the store's own schemas need no capability, as on the Schema screen.
    /// Anything else is a <c>BrowseDatabase</c> read: the capability, the write policy for the database
    /// with no tenant, and a schema <c>BrowsableSchemas</c> admits - refused and audited in that order.
    /// </remarks>
    Task<DatabaseObjectDefinition> GetDefinitionAsync(
        StudioScope scope,
        DatabaseObjectRef reference,
        CancellationToken cancellationToken = default);
}
