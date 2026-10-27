using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's <see cref="IHealthTableSource" /> (issue #8, G1): every ordinary and partitioned table of the schemas
/// <c>PostgresStudioOptions.BrowsableSchemas</c> admits, each one the host's own, a recognised library's, or somebody
/// else's.
/// </summary>
/// <remarks>
/// <para>
/// <b>One statement, estimates only.</b> The set is read by <see cref="HealthTableQueries.RelationalTables" /> through the
/// sweep's own runner - read-only, under the console role, once per sweep (the sweep memoises the set) - and it reads
/// <c>pg_class</c>, <c>pg_namespace</c>, <c>pg_depend</c> and <c>pg_inherits</c> and nothing else: no size function, no
/// per-table query, no lock on anything it lists (D27; B0 measured that a size function waits behind a migration's lock).
/// </para>
/// <para>
/// <b>Capped for a large database</b> (design R5). A database of ten thousand tables is judged by its
/// <see cref="TableCap" /> largest, by estimated size - which is where bloat, vacuum and wraparound matter most - and the
/// set's <see cref="IStudioTableSet.Coverage" /> says how many were left out, so a quiet check never reads as a clean
/// bill for the rest.
/// </para>
/// <para>
/// <b>Whose each table is.</b> A table a recognised library names (<see cref="StudioTableLibraries.LibraryOf" />:
/// Quartz.NET's <c>qrtz_</c>, Wolverine's, Marten's own bookkeeping, EF Core's and FluentMigrator's history) is
/// <see cref="StudioTableOwnership.Library" /> - exempt from the index-design rules only, judged on vacuum, statistics,
/// size and wraparound like any other. Otherwise a table in one of the source's own schemas (named exactly) is
/// <see cref="StudioTableOwnership.Owned" />, and one only <c>"*"</c> admits is <see cref="StudioTableOwnership.Other" />.
/// Every table is <see cref="StudioTableKind.Relational" />: the host's migrations, not a library's configuration, decide
/// its schema, so every index on it but the primary key is <see cref="IndexOrigin.Host" /> - never one an apply drops.
/// </para>
/// <para>
/// <b>Gated where the browser withholds.</b> A table outside the source's own schemas carries its schema in
/// <see cref="StudioTable.GatedSchemas" />, so a finding naming it is shown only to a visitor the database browser's gate
/// admits to that schema - the same rule a Marten store's extended tables follow (D29).
/// </para>
/// </remarks>
internal sealed class PostgresHealthTableSource : IHealthTableSource
{
    /// <summary>How many tables a sweep judges at most: the largest, by estimated size.</summary>
    internal const int TableCap = 2000;

    /// <summary>The memo key of the one statement that lists them.</summary>
    internal const string RelationalTablesKey = "postgres.tables";

    /// <inheritdoc />
    public async Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<string> browsable = [.. context.Settings.BrowsableSchemas];
        IReadOnlyList<string> own = PostgresDatabaseDeclarationsSource.OwnSchemas(browsable);
        bool everything = BrowsableSchemaMatcher.IncludesEverything(browsable);

        if (own.Count == 0 && !everything)
        {
            // Nothing is admitted, so nothing is asked: no connection is opened for an empty set.
            return Build([], own, TableCap);
        }

        // Through the sweep's memo, which hands the read the sweep's own budget (the source's own entry point takes none).
        IReadOnlyList<HealthRelationalTableRow> rows = await context
            .Shared(
                RelationalTablesKey,
                token => context.Sql.QueryAsync(
                    HealthTableQueries.RelationalTables(own, everything, TableCap), HealthTableQueries.ReadRelationalTable, token))
            .ConfigureAwait(false);

        return Build(rows, own, TableCap);
    }

    /// <summary>The set the rows describe, for a test as for a sweep.</summary>
    /// <param name="rows">What the statement listed, largest first.</param>
    /// <param name="ownSchemas">The source's own schemas: the ones <c>BrowsableSchemas</c> names exactly.</param>
    /// <param name="cap">The cap the statement was run with.</param>
    internal static IStudioTableSet Build(IReadOnlyList<HealthRelationalTableRow> rows, IReadOnlyList<string> ownSchemas, int cap)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(ownSchemas);

        HashSet<string> own = new(ownSchemas, StringComparer.Ordinal);
        List<StudioTable> tables = new(rows.Count);

        foreach (HealthRelationalTableRow row in rows)
        {
            bool isOwn = own.Contains(row.Schema);

            // A Marten store's table is that store's to manage, whatever it holds: its next apply drops an index nobody
            // declared and keeps the storage parameters it declares, so nothing here is the host's design. Every mt_
            // table - the documents' too - is the Marten library's, exempt from the index-design rules, worded as a
            // Marten table and given no migration SQL (PostgresHealthTableRemedies).
            bool marten = DatabaseObjectClassifier.IsMartenName(row.Table);
            string? library = marten ? "Marten" : StudioTableLibraries.LibraryOf(row.Table);

            StudioTableOwnership ownership = library is not null
                ? StudioTableOwnership.Library
                : isOwn ? StudioTableOwnership.Owned : StudioTableOwnership.Other;

            tables.Add(new StudioTable(row.Schema, row.Table, marten ? MartenKindOf(row.Table) : StudioTableKind.Relational, ownership)
            {
                Library = library,
                GatedSchemas = isOwn ? [] : [row.Schema],
            });
        }

        int total = rows.Count == 0 ? 0 : rows[0].Total;
        int listedOwn = rows.Count(x => own.Contains(x.Schema));

        // A row built by hand says nothing of the source's own schemas: when every row it listed is in one, so is every
        // table it counted.
        int ownTotal = rows.Count == 0 ? 0 : rows[0].OwnTotal ?? (listedOwn == rows.Count ? total : listedOwn);

        string? coverage = total > rows.Count ? CoverageOf(rows.Count, total, cap, ownSchemasOnly: false) : null;

        // What a visitor the database browser keeps out of every schema only "*" admits is told: the same sentence over the
        // source's own schemas, which tells nothing of what the others hold - not even how many tables (G1M F6, H1).
        string? ownCoverage = ownTotal == total && listedOwn == rows.Count
            ? coverage
            : ownTotal > listedOwn ? CoverageOf(listedOwn, ownTotal, cap, ownSchemasOnly: true) : null;

        return new RelationalTableSet(
            [.. tables.OrderBy(static x => x.Schema, StringComparer.Ordinal).ThenBy(static x => x.Table, StringComparer.Ordinal)],
            coverage,
            ownCoverage);
    }

    /// <summary>
    /// "the 2 largest of 3,412 tables by estimated size (a sweep judges at most 2)" over every admitted schema, or "the 1
    /// largest of 10 tables in the data source's own schemas by estimated size" over the own schemas alone.
    /// </summary>
    /// <remarks>
    /// The own reading leaves the cap out (H1b): a capped list holds exactly the cap's number of tables, so the cap less the
    /// own schemas' judged count is how many tables the sweep judged in schemas this reader is not shown - arithmetic that
    /// would give back the count the own reading exists to withhold.
    /// </remarks>
    private static string CoverageOf(int judged, int total, int cap, bool ownSchemasOnly) =>
        "the " + judged.ToString("N0", CultureInfo.InvariantCulture) + " largest of "
        + total.ToString("N0", CultureInfo.InvariantCulture) + " tables"
        + (ownSchemasOnly
            ? " in the data source's own schemas by estimated size"
            : " by estimated size (a sweep judges at most " + cap.ToString("N0", CultureInfo.InvariantCulture) + ")");

    /// <summary>What a Marten table is, by its name alone - no store is registered to say more.</summary>
    /// <param name="table">An <c>mt_</c> table's name.</param>
    internal static StudioTableKind MartenKindOf(string table) =>
        table.StartsWith(DatabaseObjectClassifier.DocumentTablePrefix, StringComparison.OrdinalIgnoreCase)
            ? StudioTableKind.DocumentTable
            : table.StartsWith("mt_event", StringComparison.OrdinalIgnoreCase) || string.Equals(table, "mt_streams", StringComparison.OrdinalIgnoreCase)
                ? StudioTableKind.EventTable
                : StudioTableKind.Bookkeeping;

    /// <summary>
    /// A plain data source's judged tables: every index the host's, but the primary key - and but a Marten table's, which
    /// its store manages (<see cref="IndexOrigin.StoreManaged" />).
    /// </summary>
    private sealed class RelationalTableSet : IStudioTableSet
    {
        private readonly Dictionary<string, StudioTable> byKey;

        public RelationalTableSet(IReadOnlyList<StudioTable> tables, string? coverage, string? ownSchemasCoverage)
        {
            Tables = tables;
            Coverage = coverage;
            OwnSchemasCoverage = ownSchemasCoverage;
            byKey = new Dictionary<string, StudioTable>(StringComparer.Ordinal);

            foreach (StudioTable table in tables)
            {
                byKey.TryAdd(table.Schema + "\u001f" + table.Table, table);
            }
        }

        public IReadOnlyList<StudioTable> Tables { get; }

        public string? Coverage { get; }

        public string? OwnSchemasCoverage { get; }

        public StudioTable? Find(string schema, string table) =>
            byKey.TryGetValue(schema + "\u001f" + table, out StudioTable? found) ? found : null;

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(table);

            return index.IsPrimaryKey
                ? IndexOrigin.PrimaryKey
                : table.Kind == StudioTableKind.Relational ? IndexOrigin.Host : IndexOrigin.StoreManaged;
        }
    }
}
