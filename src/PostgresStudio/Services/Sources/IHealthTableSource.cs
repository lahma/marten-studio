using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;

namespace PostgresStudio.Services.Sources;

/// <summary>What a table the table checks judge is, which decides how a finding about it is worded.</summary>
internal enum StudioTableKind
{
    /// <summary>A visible document type's table - the only kind a <c>Schema.For&lt;T&gt;()</c> remedy fits.</summary>
    DocumentTable,

    /// <summary>One of an event store's own tables.</summary>
    EventTable,

    /// <summary>A projection's table, or a table a source manages for the host (Marten's <c>ExtendedSchemaObjects</c>).</summary>
    Projection,

    /// <summary>A library's own bookkeeping (Marten's <c>mt_hilo</c>).</summary>
    Bookkeeping,

    /// <summary>A table the host's own migrations own: everything a plain data source judges (issue #8, G1).</summary>
    Relational,
}

/// <summary>
/// Whose design a table is, which decides which rules apply to it (issue #8, G1).
/// </summary>
/// <remarks>
/// <see cref="Library" /> tables are exempt from the index-design rules only - a redundant, unused or missing index on
/// Quartz.NET's job store is the library's design, and nothing the host can change - while vacuum, statistics, size and
/// wraparound rules still apply to them: a library's table bloats like anybody's.
/// </remarks>
internal enum StudioTableOwnership
{
    /// <summary>The application's own: its documents, its projections, its relational tables.</summary>
    Owned,

    /// <summary>
    /// A library's schema the application did not design: <c>qrtz_*</c>, <c>wolverine_*</c>, Marten's <c>mt_*</c> other
    /// than <c>mt_doc_*</c>, EF Core's <c>__EFMigrationsHistory</c>, FluentMigrator's <c>VersionInfo</c>.
    /// </summary>
    Library,

    /// <summary>Somebody else's: a table in the database that is neither the application's nor a recognised library's.</summary>
    Other,
}

/// <summary>
/// One table the table checks judge, described by the source that knows whose it is.
/// </summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Kind">What it is, for the wording.</param>
/// <param name="Ownership">Whose design it is, for which rules apply.</param>
internal sealed record StudioTable(string Schema, string Table, StudioTableKind Kind, StudioTableOwnership Ownership)
{
    /// <summary>The key the history and the findings name it by.</summary>
    public string Key => SchemaKey.For(Schema, Table);

    /// <summary>
    /// The schemas a finding naming this table must be gated on (<c>HealthFinding.GatedSchemas</c>): its own when it sits
    /// outside the source's own schemas, which D27 withholds from a visitor short of the database browser's gate.
    /// </summary>
    public IReadOnlyList<string> GatedSchemas { get; init; } = [];

    /// <summary>The .NET type a <c>Schema.For&lt;T&gt;()</c> remedy names, for a document table.</summary>
    public string? TypeName { get; init; }

    /// <summary>The collection's alias, for a document table.</summary>
    public string? Alias { get; init; }

    /// <summary>Whether its rows are soft-deleted rather than removed.</summary>
    public bool SoftDeleted { get; init; }

    /// <summary>The document types a finding about this table names - which the sweep drops a hidden one by.</summary>
    public IReadOnlyList<Type> DocumentTypes { get; init; } = [];

    /// <summary>The library that owns it, for a <see cref="StudioTableOwnership.Library" /> table: <c>Quartz.NET</c>.</summary>
    public string? Library { get; init; }

    /// <summary>Whether the index-design rules (redundant, unused, missing) skip it.</summary>
    public bool ExemptFromIndexDesign => Ownership == StudioTableOwnership.Library;

    /// <summary>The target the table statements bind.</summary>
    public HealthTableTarget Target => new(Schema, Table);
}

/// <summary>The tables one sweep judges, and how each index on them came to be.</summary>
internal interface IStudioTableSet
{
    /// <summary>Every table judged, in schema and table order.</summary>
    IReadOnlyList<StudioTable> Tables { get; }

    /// <summary>
    /// What the set leaves out, said in the evidence - "the 2,000 largest of 3,412 tables by estimated rows" - or
    /// <see langword="null" /> when it is every table the source judges.
    /// </summary>
    string? Coverage { get; }

    /// <summary>The table at <paramref name="schema" />.<paramref name="table" />, or <see langword="null" />.</summary>
    StudioTable? Find(string schema, string table);

    /// <summary>
    /// Where <paramref name="index" /> comes from - declared by the source's configuration, added by the library itself,
    /// or the host's own - which decides whether a remedy can say anything about it and what.
    /// </summary>
    IndexOrigin OriginOf(HealthIndexRow index, StudioTable table);
}

/// <summary>
/// The tables of the swept database the table checks (T01-T08, T12, T13, T23) judge.
/// </summary>
/// <remarks>
/// <para>
/// <b>Whole-database scope is the contract (issue #8, G1).</b> A source answers every table of the database it can
/// classify, each marked <see cref="StudioTableOwnership" />: the source's own, a recognised library's, or somebody
/// else's. Postgres Studio's answers every table of the schemas <c>BrowsableSchemas</c> admits; MartenStudio's lays its
/// store's declarations over the catalog - the declared tables exactly as before, and every other table of the store's
/// own schemas and of the ones the database browser admits beside them, another registered store's left out.
/// </para>
/// <para>
/// <b>What it must not do.</b> Read the set without a statement where it can (a store's configuration, the database
/// browser's catalog cache), and with at most one shared, memoised statement through the sweep's own runner where it
/// cannot - never a lock-taking size function, never a per-table query (D27: a list locks nothing it lists). A large
/// database is capped, and the cap is said in <see cref="IStudioTableSet.Coverage" />. A table in a schema D27 withholds
/// carries that schema in <see cref="StudioTable.GatedSchemas" />, and a hidden document type's table is not in the set.
/// </para>
/// </remarks>
internal interface IHealthTableSource
{
    /// <summary>The tables the sweep in <paramref name="context" /> judges, built once per sweep.</summary>
    /// <param name="context">The sweep's context: the resolved source, its settings and its statement runner.</param>
    Task<IStudioTableSet> TablesAsync(HealthCheckContext context);
}

/// <summary>The recognised libraries' tables (issue #8, G1), by name.</summary>
internal static class StudioTableLibraries
{
    /// <summary>
    /// The library that owns a table named <paramref name="table" />, or <see langword="null" /> - by name alone, because
    /// a library's tables are recognisable by name and nothing in the catalog says more. Marten's <c>mt_doc_*</c> tables
    /// are the application's documents, not Marten's design, and are not a library's.
    /// </summary>
    /// <param name="table">The table's name, as the catalog has it.</param>
    public static string? LibraryOf(string table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.StartsWith("qrtz_", StringComparison.OrdinalIgnoreCase))
        {
            return "Quartz.NET";
        }

        if (table.StartsWith("wolverine_", StringComparison.OrdinalIgnoreCase))
        {
            return "Wolverine";
        }

        if (table.StartsWith("mt_", StringComparison.OrdinalIgnoreCase) && !table.StartsWith("mt_doc_", StringComparison.OrdinalIgnoreCase))
        {
            return "Marten";
        }

        if (string.Equals(table, "__EFMigrationsHistory", StringComparison.Ordinal))
        {
            return "Entity Framework Core";
        }

        return string.Equals(table, "VersionInfo", StringComparison.Ordinal) ? "FluentMigrator" : null;
    }
}
