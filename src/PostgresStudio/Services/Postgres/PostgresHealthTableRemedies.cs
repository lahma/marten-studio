using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's remedy wording (<see cref="IHealthTableRemedies" />, issue #8 G12): the SQL a DBA would put in a
/// migration - <c>ALTER TABLE … SET (autovacuum_…)</c>, <c>CREATE INDEX CONCURRENTLY …</c>,
/// <c>DROP INDEX CONCURRENTLY …</c>, <c>REINDEX INDEX CONCURRENTLY …</c> - as text to hand to whoever owns the schema.
/// The studio never runs any of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only for a table the host's migrations own.</b> Every remedy here is <see cref="HealthRemedy.ForMigration" />, and
/// only for a <see cref="StudioTableKind.Relational" /> table: a migration statement on a table some library's
/// configuration reconciles is a statement that library undoes. So a table the <c>mt_</c> rule calls Marten's gets none
/// - Marten's next <c>CreateOrUpdate</c> drops an index nobody declares, and its rows and its schema are Marten Studio's
/// business - and neither does a library's, whose index design is the library's (they are exempt from those rules
/// anyway), except where a storage parameter is the only lever and it is the host's database.
/// </para>
/// <para>
/// <b>Built under <c>Internal/Sql</c>, quoted there</b> (<see cref="MigrationSqlText" />). A table, schema or index
/// whose name the studio will not quote - a double quote, a NUL, a control or format character in it - gets no remedy:
/// the finding still says what is wrong, and nobody is handed a statement that does not say what it means.
/// </para>
/// <para>
/// Pure: no connection, no IO, the same answer for the same arguments - a singleton the sweep hands every evaluator.
/// </para>
/// </remarks>
internal sealed class PostgresHealthTableRemedies : IHealthTableRemedies
{
    /// <summary>The one instance: it holds nothing.</summary>
    public static PostgresHealthTableRemedies Instance { get; } = new();

    /// <inheritdoc />
    public HealthRemedy? IndexTheFilteredMember(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return OffersIndexes(table) && MigrationSqlText.CreateIndexConcurrently(table.Schema, table.Table) is { } sql
            ? HealthRemedy.ForMigration(
                "-- The column the slow query filters, sorts or joins on, in place of " + MigrationSqlText.ColumnPlaceholder + ":\n" + sql,
                "The statement's plan (EXPLAIN, from the SQL console) shows the sequential scan's filter and so the column. Every "
                + "write maintains every index, so add the one the slow query needs and no other. CREATE INDEX CONCURRENTLY builds "
                + "without blocking writes, but cannot run inside a transaction block.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? IndexTheUnnamedMember(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return OffersIndexes(table) && MigrationSqlText.CreateIndexConcurrently(table.Schema, table.Table) is { } sql
            ? HealthRemedy.ForMigration(
                "-- The column this statement filters on, which pg_stat_statements does not name -\n"
                + "-- if no index on it exists already:\n" + sql,
                "pg_stat_statements records the statement's constants as placeholders, so neither its plan nor the advisor can say "
                + "which column it filters on, nor whether an index on it exists. Read the application's query for the column, and "
                + "add the index only if none serves it. CREATE INDEX CONCURRENTLY cannot run inside a transaction block.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? RebuildInvalidIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        // Any relational table's, a library's included: an invalid index is broken rather than badly designed, and
        // rebuilding it restores exactly the design the library chose.
        return IsRelational(table) && MigrationSqlText.ReindexIndexConcurrently(index.Schema, index.Name) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Builds the index again without blocking writes (PostgreSQL 12+; before that, DROP INDEX CONCURRENTLY and create it "
                + "again). A unique index whose build failed on duplicate values fails again until the duplicates are resolved, and "
                + "a REINDEX CONCURRENTLY that fails leaves an invalid copy (_ccnew) behind to drop.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? RemoveRedundantIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin, HealthIndexRow coveredBy)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(coveredBy);

        return Droppable(table, index, origin) && MigrationSqlText.DropRedundantIndexConcurrently(index.Schema, index.Name, coveredBy.Name) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Every write maintains every index, so one another index covers is cost without benefit. Check first that nothing "
                + "names it - a query hint, a CLUSTER, a migration that alters it. DROP INDEX CONCURRENTLY cannot run inside a "
                + "transaction block.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? RemoveUnusedIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        return Droppable(table, index, origin) && MigrationSqlText.DropIndexConcurrently(index.Schema, index.Name) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Nothing has scanned it since the statistics were last reset, on this server: a query that runs only on a replica, "
                + "or only at month end, does not show here, so check those first. DROP INDEX CONCURRENTLY cannot run inside a "
                + "transaction block.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? LeaveRoomForHotUpdates(StudioTable table, int fillFactor)
    {
        ArgumentNullException.ThrowIfNull(table);

        return IsHosts(table) && MigrationSqlText.SetStorageParameter(table.Schema, table.Table, "fillfactor", fillFactor) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Leaves part of each page free for an update to stay on (a HOT update). It costs more heap, and affects only pages "
                + "written after the change: existing rows are repacked only when the table is rewritten. On a partitioned table, "
                + "set it on each partition.")
            : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// None: which expression index to replace, and with which column, is the application's own design - there is no
    /// statement that says it without knowing the schema's intent.
    /// </remarks>
    public HealthRemedy? IndexAColumnInPlaceOfAnExpression(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return null;
    }

    /// <inheritdoc />
    public HealthRemedy? ReenableAutovacuum(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return IsRelational(table) && MigrationSqlText.ResetStorageParameter(table.Schema, table.Table, "autovacuum_enabled") is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Switches autovacuum back on for this table: RESET returns autovacuum_enabled to the server's setting. On a "
                + "partitioned table, reset it on each partition.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? AutovacuumSetting(StudioTable table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);

        return IsRelational(table) && MigrationSqlText.SetStorageParameter(table.Schema, table.Table, parameter, value) is { } sql
            ? HealthRemedy.ForMigration(sql, explanation + " On a partitioned table, set it on each partition.")
            : null;
    }

    // ---- W5-V1 ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public HealthRemedy? ReenableToastAutovacuum(StudioTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return IsRelational(table) && MigrationSqlText.ResetToastStorageParameter(table.Schema, table.Table, "autovacuum_enabled") is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Switches autovacuum back on for the table's TOAST table, which holds its large values: RESET returns "
                + "toast.autovacuum_enabled to the server's setting. On a partitioned table, reset it on each partition.")
            : null;
    }

    // ---- W5-B1 ---------------------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public HealthRemedy? IndexTheForeignKey(StudioTable table, IReadOnlyList<string> columns, bool partitioned)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        if (!OffersIndexes(table))
        {
            return null;
        }

        if (partitioned)
        {
            return MigrationSqlText.CreateIndexOnPartitionedTable(table.Schema, table.Table, columns) is { } plain
                ? HealthRemedy.ForMigration(
                    plain,
                    "An index whose leading columns are the key's lets Postgres find the rows that point at a deleted or updated "
                    + "row without reading every partition whole. CONCURRENTLY is not allowed on a partitioned table, so this one "
                    + "blocks writes to every partition while it builds; to avoid that, create it ON ONLY the parent, build each "
                    + "partition's with CREATE INDEX CONCURRENTLY, and ALTER INDEX ... ATTACH PARTITION each one.")
                : null;
        }

        return MigrationSqlText.CreateIndexConcurrently(table.Schema, table.Table, columns) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "An index whose leading columns are the key's lets Postgres find the rows that point at a deleted or updated row "
                + "without reading the whole table. CREATE INDEX CONCURRENTLY builds without blocking writes, but cannot run "
                + "inside a transaction block.")
            : null;
    }

    /// <inheritdoc />
    /// <remarks>Nothing to add: a plain source's table is the host's, and <see cref="IndexTheForeignKey" /> words it.</remarks>
    public string? ForeignKeyIndexNote(StudioTable table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        return null;
    }

    /// <inheritdoc />
    public HealthRemedy? RebuildBloatedIndex(StudioTable table, HealthIndexRow index, IndexOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(index);

        // Any relational table's, a library's included: a rebuild keeps exactly the design it had (issue #8 G1: size rules apply).
        return IsRelational(table) && MigrationSqlText.ReindexIndexConcurrently(index.Schema, index.Name) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Builds the index again, packed, under the same name and definition, without blocking writes (PostgreSQL 12+; on a "
                + "partitioned index, 14+). It needs room for a second copy while it runs, and a REINDEX CONCURRENTLY that fails "
                + "leaves an invalid copy (_ccnew) behind to drop. Measure it first with pgstatindex - the Maintenance page's "
                + "bloat measure - since the size it is judged by is an estimate.")
            : null;
    }

    /// <inheritdoc />
    public HealthRemedy? WidenKey(StudioTable table, HealthIntegerKeyRow key)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(key);

        if (!IsHosts(table))
        {
            return null;
        }

        bool narrowColumn = key.ColumnType is "smallint" or "integer";
        bool narrowSequence = key.SequenceType is "smallint" or "integer";
        bool explicitMaximum = IntegerKeyHeadroomCheck.ExplicitMaximum(key);
        bool identity = key.FedBy == "identity";
        List<string> statements = [];

        if (narrowColumn)
        {
            if (MigrationSqlText.WidenColumnToBigint(table.Schema, table.Table, key.Column) is not { } column)
            {
                return null;
            }

            statements.Add(column);
        }

        // An identity column's sequence follows the column's type when the column is altered (measured on 14 and 18,
        // LintMatrixLiveTests); a serial's or a default's does not, and is altered on its own. An explicit MAXVALUE survives
        // both, and is lifted with NO MAXVALUE: through the column for an identity, on the sequence otherwise (W5-B1 review F8).
        string? sequence = (identity && narrowColumn, narrowSequence, explicitMaximum) switch
        {
            (true, _, true) => MigrationSqlText.IdentityNoMaxValue(table.Schema, table.Table, key.Column),
            (true, _, false) => string.Empty,
            (false, true, false) => MigrationSqlText.WidenSequenceToBigint(key.SequenceSchema, key.Sequence),
            (false, _, true) when identity => MigrationSqlText.IdentityNoMaxValue(table.Schema, table.Table, key.Column) is { } lift
                ? (narrowSequence ? MigrationSqlText.WidenSequenceToBigint(key.SequenceSchema, key.Sequence) + "\n" : string.Empty) + lift
                : null,
            (false, _, true) => MigrationSqlText.SequenceNoMaxValue(key.SequenceSchema, key.Sequence, widen: narrowSequence),
            _ => string.Empty,
        };

        if (sequence is null)
        {
            return null;
        }

        if (sequence.Length > 0)
        {
            statements.Add(sequence);
        }

        if (statements.Count == 0)
        {
            return null;
        }

        string explanation =
            (narrowColumn
                ? "ALTER COLUMN ... TYPE bigint rewrites the whole table, and rebuilds every index on it, under an ACCESS "
                  + "EXCLUSIVE lock that every read and write waits for: plan a maintenance window, or add a bigint column, "
                  + "backfill it in batches and swap it in. Widen every column that references this one too. "
                : string.Empty)
            + (narrowSequence && !(identity && narrowColumn) ? "ALTER SEQUENCE ... AS bigint raises the sequence's maximum to bigint's with its type. " : string.Empty)
            + (explicitMaximum
                ? "The sequence's MAXVALUE was set below its type's, and a change of type keeps it: NO MAXVALUE lifts it to the "
                  + "type's own."
                : string.Empty);

        return HealthRemedy.ForMigration(string.Join("\n", statements), explanation.TrimEnd());
    }

    /// <inheritdoc />
    public HealthRemedy? ExtendRangePartitions(StudioTable table, RangePartitionNext next)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(next);

        // Upkeep, not design: a library's partitioned table needs its next period as much as the host's does.
        return IsRelational(table)
               && MigrationSqlText.CreateRangePartition(table.Schema, table.Table, table.Table + "_" + next.Suffix, next.KeyType, next.From, next.To) is { } sql
            ? HealthRemedy.ForMigration(
                sql,
                "Creates the next period's partition, named as the studio would name it - rename it to match yours. "
                + (next.CatchUp is { } catchUp ? catchUp + " " : string.Empty)
                + (next.HasDefault
                    ? "Creating it scans the DEFAULT partition under an ACCESS EXCLUSIVE lock for rows of the new range, and fails "
                      + "if there are any: move those out of the default first. "
                    : string.Empty)
                + "Create periods ahead on a schedule rather than by hand - pg_partman, or a job of the application's own.")
            : null;
    }

    /// <summary>
    /// Whether a migration statement fits <paramref name="table" /> at all: a relational table that is not named like
    /// Marten's, whose configuration would undo it.
    /// </summary>
    private static bool IsRelational(StudioTable table) =>
        table.Kind == StudioTableKind.Relational && !DatabaseObjectClassifier.IsMartenName(table.Table);

    /// <summary>Whether its design is the host's: relational, and no recognised library's.</summary>
    private static bool IsHosts(StudioTable table) => IsRelational(table) && table.Ownership != StudioTableOwnership.Library;

    /// <summary>Whether an index may be suggested for it: the host's table, and the index-design rules apply to it.</summary>
    private static bool OffersIndexes(StudioTable table) => IsHosts(table) && !table.ExemptFromIndexDesign;

    /// <summary>
    /// Whether <paramref name="index" /> can be dropped with <c>DROP INDEX</c>: the host's, not the primary key, and not
    /// one that backs a unique or exclusion constraint - which only <c>ALTER TABLE … DROP CONSTRAINT</c> removes, and whose
    /// removal changes what the table allows.
    /// </summary>
    private static bool Droppable(StudioTable table, HealthIndexRow index, IndexOrigin origin) =>
        OffersIndexes(table)
        && origin == IndexOrigin.Host
        && !index.IsPrimaryKey
        && !index.IsUnique
        && !index.IsExclusion;
}
