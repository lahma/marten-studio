using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Schema;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="IHealthTableSource" />: the tables the store declares (<see cref="MartenTables" />), each
/// classified for the whole-database contract. PS-1a's pass-through - the table checks still read
/// <see cref="MartenTables" /> directly until the health packet (PS-1c) moves them onto this seam and widens it to every
/// table of the database (issue #8, G1).
/// </summary>
internal sealed class MartenHealthTableSource : IHealthTableSource
{
    /// <inheritdoc />
    public async Task<IStudioTableSet> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet set = await MartenTables.SetAsync(context).ConfigureAwait(false);
        return new MartenStudioTableSet(set);
    }

    /// <summary>One Marten table as the shared checks see it.</summary>
    internal static StudioTable Describe(MartenTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        string? library = table.Kind == MartenTableKind.Document ? null : StudioTableLibraries.LibraryOf(table.Table);

        return new StudioTable(
            table.Schema,
            table.Table,
            table.Kind switch
            {
                MartenTableKind.Document => StudioTableKind.DocumentTable,
                MartenTableKind.EventStore => StudioTableKind.EventTable,
                MartenTableKind.Managed => StudioTableKind.Projection,
                _ => StudioTableKind.Bookkeeping,
            },
            library is null ? StudioTableOwnership.Owned : StudioTableOwnership.Library)
        {
            GatedSchemas = table.GatedSchemas,
            TypeName = table.TypeName,
            Alias = table.Alias,
            SoftDeleted = table.SoftDeleted,
            DocumentTypes = table.DocumentTypes,
            Library = library,
        };
    }

    private sealed class MartenStudioTableSet : IStudioTableSet
    {
        private readonly MartenTableSet set;
        private readonly Dictionary<string, (StudioTable Shared, MartenTable Marten)> byKey;

        public MartenStudioTableSet(MartenTableSet set)
        {
            this.set = set;
            byKey = new Dictionary<string, (StudioTable, MartenTable)>(StringComparer.OrdinalIgnoreCase);

            List<StudioTable> tables = new(set.Tables.Count);
            foreach (MartenTable table in set.Tables)
            {
                StudioTable shared = Describe(table);
                tables.Add(shared);
                byKey[table.Key] = (shared, table);
            }

            Tables = tables;
        }

        public IReadOnlyList<StudioTable> Tables { get; }

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) =>
            byKey.TryGetValue(SchemaKey.For(schema, table), out (StudioTable Shared, MartenTable Marten) found) ? found.Shared : null;

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table)
        {
            ArgumentNullException.ThrowIfNull(index);
            ArgumentNullException.ThrowIfNull(table);

            return byKey.TryGetValue(table.Key, out (StudioTable Shared, MartenTable Marten) found)
                ? set.OriginOf(index, found.Marten)
                : IndexOrigin.Undeclared;
        }
    }
}

/// <summary>
/// MartenStudio's <see cref="IHealthRemedyProvider" />: a <c>StoreOptions</c> line for a document table it can name, and
/// nothing for any other table - today's rule, because no Marten line sets another table's storage and the studio never
/// suggests DDL for a table Marten's next apply would reconcile (D29). PS-1a's pass-through; the health packet (PS-1c)
/// routes the checks' remedies through it and adds the migration-SQL wording for a relational table (issue #8, G12).
/// </summary>
internal sealed class MartenHealthRemedyProvider : IHealthRemedyProvider
{
    /// <inheritdoc />
    public HealthRemedy? StorageParameter(StudioTable table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return table.TypeName is { } type
            ? new HealthRemedy(
                "opts.Schema.For<" + type + ">().StorageParameter(\"" + parameter + "\", " + value + ");",
                explanation,
                RuntimeFeature.DocumentStorageParameter)
            : null;
    }

    /// <inheritdoc />
    /// <remarks>None yet: the checks that would suggest an index word their own remedy until PS-1c moves them here.</remarks>
    public HealthRemedy? AddIndex(StudioTable table, IReadOnlyList<string> columns, string explanation) => null;

    /// <inheritdoc />
    /// <remarks>None yet: the checks that would remove an index word their own remedy until PS-1c moves them here.</remarks>
    public HealthRemedy? RemoveIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, string explanation) => null;
}
