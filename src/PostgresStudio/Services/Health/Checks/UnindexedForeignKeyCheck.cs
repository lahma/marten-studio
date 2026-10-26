using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T15 reads: the judged tables, their foreign keys, and the indexes on them.</summary>
/// <param name="Tables">The judged tables that exist, with the catalog's row for each - for their size.</param>
/// <param name="Judged">Every table of the judged set, to resolve a referenced table that is one of them.</param>
/// <param name="Keys">The foreign keys of the judged tables, never a partition's clone.</param>
/// <param name="Indexes">The indexes on the judged tables but the locked ones.</param>
internal sealed record UnindexedForeignKeyFacts(
    IReadOnlyList<TableFacts> Tables,
    IReadOnlyList<StudioTable> Judged,
    IReadOnlyList<HealthForeignKeyRow> Keys,
    IReadOnlyList<IndexFacts> Indexes)
{
    /// <summary>The tables locked for a schema change, whose keys are not judged this sweep (<see cref="HealthIndexRead.LockedTables" />).</summary>
    public IReadOnlySet<string> LockedTables { get; init; } = HealthIndexRead.Empty.LockedTables;
}

/// <summary>
/// T15: a foreign key on a table of 8 MiB or more that no index covers, so every delete from the table it references - and
/// every update of that table's key - reads the whole referencing table (issue #8 G4).
/// </summary>
/// <remarks>
/// <para>
/// <b>B0 §9's rule</b>, shared with the database browser's Keys badge as one pure function
/// (<see cref="ForeignKeyCoverage.IsCovered" />): a valid, non-partial b-tree (or one-column hash) whose leading columns are
/// exactly the key's, in any order; on a partitioned table, the columns the partition key prunes on are left to pruning.
/// A partitioned table's foreign key is judged on the table, against its partitioned indexes - never on a partition's clone.
/// </para>
/// <para>
/// <b>Catalog only, and never a locked table read as unindexed.</b> The keys come from <c>pg_constraint</c> as attribute
/// numbers (<see cref="IndexLintQueries.ForeignKeysSql" />), the indexes from the sweep's one index read
/// (<see cref="HealthTables.IndexReadAsync(HealthCheckContext)" />). A table locked for a schema change is left out for the
/// sweep and counted (<see cref="HealthTables.LockedNote" />), as every index check does.
/// </para>
/// <para>
/// <b>Index design, so a library's table is exempt</b> (issue #8 G1): Quartz.NET's foreign keys are Quartz.NET's to index.
/// A Marten table the store declares is judged - its keys are the host's configuration - and where Marten creates the table
/// itself, the studio says what Marten does about it rather than offer an index its next apply would drop.
/// </para>
/// </remarks>
[HealthCheck("T15", HealthArea.Tables, HealthCostTier.Catalog, "Foreign key without a covering index")]
internal sealed class UnindexedForeignKeyCheck : HealthCheck<UnindexedForeignKeyFacts>
{
    /// <summary>The smallest referencing table worth a finding: its heap, from <c>relpages</c> (issue #8 G4).</summary>
    public const long MinTableBytes = 8L * 1024 * 1024;

    /// <summary>What names a referenced table the finding may not name: a document table no store shows the studio.</summary>
    internal const string UnshownDocumentTable = "a document table not shown here";

    /// <inheritdoc />
    public override async Task<UnindexedForeignKeyFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);

        IReadOnlyList<HealthForeignKeyRow> keys = set.Tables.Count == 0
            ? []
            : await context.Sql.QueryAsync(IndexLintQueries.ForeignKeys(HealthTables.Targets(set)), IndexLintQueries.ReadForeignKey, cancellationToken)
                .ConfigureAwait(false);

        return new UnindexedForeignKeyFacts(tables, set.Tables, keys, read.Indexes) { LockedTables = read.LockedTables };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(UnindexedForeignKeyFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        // Ordinal, as the set keys its tables: "Orders" and orders are two tables.
        Dictionary<string, TableFacts> tables = new(StringComparer.Ordinal);
        foreach (TableFacts table in facts.Tables)
        {
            tables.TryAdd(table.Key, table);
        }

        Dictionary<string, StudioTable> judged = new(StringComparer.Ordinal);
        foreach (StudioTable table in facts.Judged)
        {
            judged.TryAdd(table.Key, table);
        }

        ILookup<string, IndexFacts> indexes = facts.Indexes.ToLookup(static x => x.Table.Key, StringComparer.Ordinal);
        HashSet<string> ownSchemas = new(facts.Judged.Where(static x => x.GatedSchemas.Count == 0).Select(static x => x.Schema), StringComparer.Ordinal);

        List<HealthFinding> findings = [];

        foreach (HealthForeignKeyRow key in facts.Keys)
        {
            if (facts.LockedTables.Contains(key.TableKey)
                || !tables.TryGetValue(key.TableKey, out TableFacts? table)
                || table.Table.ExemptFromIndexDesign
                || table.Row.HeapBytes < MinTableBytes)
            {
                continue;
            }

            IEnumerable<CoveringIndexCandidate> candidates = indexes[key.TableKey]
                .Select(static x => new CoveringIndexCandidate(x.Row.KeyAttributes, x.Row.IsValid, x.Row.IsPartial, x.Row.AccessMethod));

            if (ForeignKeyCoverage.IsCovered(key.Attributes, key.PartitionKey, candidates))
            {
                continue;
            }

            findings.Add(Finding(key, table, Referenced(key, table.Table, judged, ownSchemas), evaluation));
        }

        string? note = HealthTables.LockedNote(facts.LockedTables);
        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    /// <summary>
    /// How the finding may name the referenced table, the schemas that naming gates it on, and the document types it adds:
    /// a judged table as the set describes it; an unjudged one in a schema of the set's own by name; one named like a
    /// document table that the set does not hold not at all - it may be a hidden type's - and any other by name, gated on
    /// its schema (D29).
    /// </summary>
    internal static (string Name, IReadOnlyList<string> GatedSchemas, IReadOnlyList<Type> DocumentTypes) Referenced(
        HealthForeignKeyRow key,
        StudioTable referencing,
        IReadOnlyDictionary<string, StudioTable> judged,
        IReadOnlySet<string> ownSchemas)
    {
        string referencedKey = key.ReferencedSchema + "." + key.ReferencedTable;

        if (judged.TryGetValue(referencedKey, out StudioTable? known))
        {
            return (known.Key, known.GatedSchemas, known.DocumentTypes);
        }

        if (key.ReferencedTable.StartsWith(HealthTableQueries.DocumentTablePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (UnshownDocumentTable, [], []);
        }

        return ownSchemas.Contains(key.ReferencedSchema) || string.Equals(key.ReferencedSchema, referencing.Schema, StringComparison.Ordinal)
            ? (referencedKey, referencing.GatedSchemas, [])
            : (referencedKey, [key.ReferencedSchema], []);
    }

    private static HealthFinding Finding(
        HealthForeignKeyRow key,
        TableFacts table,
        (string Name, IReadOnlyList<string> GatedSchemas, IReadOnlyList<Type> DocumentTypes) referenced,
        HealthEvaluation evaluation)
    {
        StudioTable referencing = table.Table;
        string columns = string.Join(", ", key.Columns);
        bool named = !string.Equals(referenced.Name, UnshownDocumentTable, StringComparison.Ordinal);
        string target = named
            ? referenced.Name + " (" + string.Join(", ", key.ReferencedColumns) + ")"
            : referenced.Name;

        HealthRemedy? remedy = evaluation.TableRemedies?.IndexTheForeignKey(referencing, key.Columns, table.Row.IsPartitioned);
        string? note = remedy is null ? evaluation.TableRemedies?.ForeignKeyIndexNote(referencing, key.Columns) : null;

        return new HealthFinding
        {
            Code = "T15",
            Severity = HealthSeverity.Warning,
            Subject = key.Key,
            DocumentTypes = [.. referencing.DocumentTypes.Concat(referenced.DocumentTypes).Distinct()],
            GatedSchemas = [.. referencing.GatedSchemas.Concat(referenced.GatedSchemas).Distinct(StringComparer.Ordinal)],
            Title = "Foreign key " + key.Name + " on " + referencing.Key + " (" + columns + ") has no index that covers it",
            WhyItMatters =
                "Postgres indexes a primary key and a unique constraint by itself, never a foreign key. So every delete from "
                + (named ? referenced.Name : "the table it references") + ", and every update of its key, looks up the rows of "
                + referencing.Key + " that point at the row - and with no index leading with (" + columns + ") that lookup reads "
                + "the whole table, " + TableText.Bytes(table.Row.HeapBytes) + " of it, once per row deleted. A join from the "
                + "referenced table on the key reads it whole too."
                + (note is { Length: > 0 } words ? " " + words : string.Empty),
            Evidence =
            [
                new HealthEvidence("Foreign key", key.Name + (key.Validated ? string.Empty : " (NOT VALID: still checked on every delete)")),
                new HealthEvidence("Columns", columns),
                new HealthEvidence("References", target),
                new HealthEvidence("Table", referencing.Describe()),
                new HealthEvidence("Table size", TableText.Bytes(table.Row.HeapBytes) + " (heap, from relpages)"),
                new HealthEvidence(
                    "Deletes from the referenced table",
                    key.ReferencedDeletes is { } deletes
                        ? TableText.Count(deletes) + " since the counters began - each one ran this lookup"
                        : "not counted: the statistics collector has nothing for it"),
            ],
            Caveat =
                "Covered means an index - valid, not partial, a b-tree or a one-column hash - whose leading columns are exactly the "
                + "key's, in any order; on a partitioned table the columns its partition key prunes on are left to pruning. An "
                + "index that leads with only some of them still makes the lookup read every row that matches those. The size "
                + "is relpages', as of the last VACUUM or ANALYZE.",
            Remedy = remedy,
        };
    }
}
