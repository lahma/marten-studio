using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>What one matrix read is handed: an open read-only transaction as the role under test, and the studio's host.</summary>
/// <param name="Connection">A connection as the role, in a read-only transaction with <c>search_path</c> pinned to <c>pg_catalog</c>.</param>
/// <param name="Transaction">That transaction.</param>
/// <param name="Schemas">The schemas a list reads: the fixture's <c>store</c> and <c>public</c>.</param>
/// <param name="Timeout">A command timeout, in seconds.</param>
/// <param name="Services">The PostgresStudio host the read's services come from.</param>
/// <param name="Resolved">The host's source, resolved for the same role and database.</param>
internal sealed record MatrixReadContext(
    NpgsqlConnection Connection,
    NpgsqlTransaction Transaction,
    IReadOnlyList<string> Schemas,
    int Timeout,
    IServiceProvider Services,
    ResolvedSource Resolved);

/// <summary>One read the matrix runs on every version.</summary>
/// <param name="Name">What it is, for a failure's message.</param>
/// <param name="LockFree">
/// Whether it promises to take no lock on what it reads, and so must return under a 300 ms <c>lock_timeout</c> while somebody
/// holds <c>ACCESS EXCLUSIVE</c> on a table and a partition (D27: a list locks nothing it lists). A read of one named object
/// - a table's detail, whose <c>pg_get_indexdef</c> briefly locks the table, or a sequence's value - does not.
/// </param>
/// <param name="RunAsync">The read; it throws what Postgres said.</param>
internal sealed record MatrixRead(string Name, bool LockFree, Func<MatrixReadContext, CancellationToken, Task> RunAsync);

/// <summary>
/// Every core read the version matrix proves on PostgreSQL 14 to 19 beside the health sweep: the database browser's lists,
/// details and definitions, the Overview's server read, the Sessions read and the relationship read. Append-only, and
/// shared: a W5 packet that adds a facts read appends a <see cref="MatrixRead" /> here (W5-index §2.11), and
/// <c>VersionMatrixLiveTests</c> runs it on every leg, as <c>app</c> and as <c>probe</c>, and - when it is
/// <see cref="MatrixRead.LockFree" /> - behind a held lock.
/// </summary>
/// <remarks>
/// A read gets the fixture's objects by name (<c>store.child</c>, <c>store.l</c>, <c>store.v</c>, <c>store.touch</c>,
/// <c>store.s</c> - <see cref="PostgresVersionMatrix.SetupSql" />), so a read that ran over nothing cannot pass for one
/// that ran.
/// </remarks>
internal static class MatrixReads
{
    /// <summary>The schemas the lists read.</summary>
    public static IReadOnlyList<string> Schemas { get; } = ["store", "public"];

    /// <summary>Every read, in the order the matrix runs them.</summary>
    public static IReadOnlyList<MatrixRead> All { get; } =
    [
        // ---- The database browser (DatabaseCatalogQueries) ----------------------------------------------------------
        new("browser: schemas", true, static (x, t) => DatabaseCatalogQueries.ReadSchemasAsync(x.Connection, x.Transaction, x.Timeout, t)),
        new("browser: relations", true, static (x, t) => DatabaseCatalogQueries.ReadRelationsAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: routines", true, static (x, t) => DatabaseCatalogQueries.ReadRoutinesAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: triggers", true, static (x, t) => DatabaseCatalogQueries.ReadTriggersAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: sequences", true, static (x, t) => DatabaseCatalogQueries.ReadSequencesAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: types", true, static (x, t) => DatabaseCatalogQueries.ReadTypesAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: foreign keys", true, static (x, t) => DatabaseCatalogQueries.ReadForeignKeysAsync(
            x.Connection, x.Transaction, x.Schemas, null, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: view dependencies", true, static (x, t) => DatabaseCatalogQueries.ReadViewDependenciesAsync(
            x.Connection, x.Transaction, x.Schemas, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: view references", true, static (x, t) => DatabaseCatalogQueries.ReadViewReferencesAsync(
            x.Connection, x.Transaction, x.Schemas, null, DatabaseCatalog.ListCap, x.Timeout, t)),
        new("browser: object counts", true, static (x, t) => DatabaseCatalogQueries.ReadObjectCountsAsync(
            x.Connection, x.Transaction, x.Schemas, [], x.Timeout, t)),
        new("browser: table detail", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadRelationDetailAsync(x.Connection, x.Transaction, "store", "child", x.Timeout, t), "store.child")),
        new("browser: partitioned table detail", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadRelationDetailAsync(x.Connection, x.Transaction, "store", "l", x.Timeout, t), "store.l")),
        new("browser: view definition", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadViewDefinitionAsync(x.Connection, x.Transaction, "store", "v", x.Timeout, t), "store.v")),
        new("browser: routine definition", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadRoutineDefinitionAsync(x.Connection, x.Transaction, "store", "touch", string.Empty, x.Timeout, t), "store.touch()")),
        new("browser: trigger definition", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadTriggerDefinitionAsync(x.Connection, x.Transaction, "store", "child", "child_touch", x.Timeout, t), "store.child.child_touch")),
        new("browser: sequence value", false, static async (x, t) => Found(
            await DatabaseCatalogQueries.ReadSequenceValueAsync(x.Connection, x.Transaction, "store", "s", x.Timeout, t), "store.s")),

        // ---- The Overview, Sessions and Relationships --------------------------------------------------------------
        new("overview: server and platform", true, static (x, t) => OverviewServerQueries.ReadPlatformAsync(x.Connection, x.Transaction, x.Timeout, t)),
        new("sessions: snapshot", true, static (x, t) => x.Services.GetRequiredService<ISessionActivityReader>().ReadAsync(x.Resolved, t)),

        // ---- X3: Sessions › Blocking's lock table read, and PostgreSQL 19's pg_stat_lock, read on 19 and absent below --------
        new("sessions: blocking and pg_stat_lock", true, static async (x, t) =>
        {
            SessionSnapshot snapshot = await x.Services.GetRequiredService<ISessionActivityReader>().ReadAsync(x.Resolved, t);
            LockTableRead blocking = snapshot.Blocking ?? throw new InvalidOperationException("The sessions snapshot read no lock table.");

            if (PostgresFeatures.Has(PostgresFeature.PgStatLock, blocking.ServerVersion))
            {
                Found(snapshot.LockStatistics?.FirstOrDefault(static y => y.LockType == "relation"), "pg_stat_lock row for relation locks, on PostgreSQL 19");
            }
            else if (snapshot.LockStatistics is not null)
            {
                throw new InvalidOperationException("pg_stat_lock was read on server_version_num " + blocking.ServerVersion + ", below 19, where it does not exist.");
            }
        }),
        // ---- Issue #20: Sessions › Holders' read of the whole server's connections by holder, on every version and every role - a
        // role without pg_read_all_stats reads other roles' sessions without a state, which are groups of their own ------------
        new("sessions: server holders", true, static async (x, t) =>
        {
            SessionSnapshot snapshot = await x.Services.GetRequiredService<ISessionActivityReader>().ReadAsync(x.Resolved, t);
            SessionServerHolders holders = snapshot.Holders ?? throw new InvalidOperationException("The sessions snapshot read no holders.");

            if (holders.ServerConnections < 1 || holders.MaxConnections < 1 || holders.Limits.Available < 1)
            {
                throw new InvalidOperationException(
                    "The holders read answered no budget: " + holders.ServerConnections + " sessions of max_connections " + holders.MaxConnections + ".");
            }

            // Every session but the studio's own is a group, so the groups add up to the server's count less its one.
            if (!holders.Truncated && holders.Groups.Sum(static y => y.Total) != holders.ServerConnections - 1)
            {
                throw new InvalidOperationException(
                    "The holders' groups hold " + holders.Groups.Sum(static y => y.Total) + " sessions of the server's " + holders.ServerConnections + ".");
            }

            // A role that reads every state has none whose state is unknown.
            if (holders.CanReadAllStats && holders.Groups.Any(static y => y.StateUnknown > 0))
            {
                throw new InvalidOperationException("A role in pg_read_all_stats was told a session's state is unknown.");
            }
        }),
        new("relationships: foreign keys", true, static (x, t) => RelationshipQueries.ReadForeignKeysTouchingAsync(
            x.Connection, x.Transaction, x.Schemas, RelationshipQueries.DefaultForeignKeyCap, x.Timeout, t)),

        // ---- W5-CAP: the Storage page's facts, and the statistics window's one statement ---------------------------------
        new("storage: facts", true, static async (x, t) =>
        {
            StorageFacts facts = await x.Services.GetRequiredService<IStorageFacts>().ReadAsync(x.Resolved, t);
            Found(facts.EstimatedDatabaseBytes > 0 ? facts : null, "the database's estimated size");

            // Only the owner has USAGE on store: a role without it is admitted no table there, as the browser admits none.
            if (x.Connection.UserName == "app")
            {
                Found(facts.LargestTables.FirstOrDefault(static y => y.Table.Table == "child"), "store.child among the largest tables");
            }
        }),
        new("stats: window", true, static async (x, t) =>
        {
            bool checkpointer = PostgresFeatures.Has(PostgresFeature.PgStatCheckpointer, x.Connection.PostgreSqlVersion.Major * 10_000);
            var query = StatisticsWindowQueries.Read(checkpointer);

            await using NpgsqlCommand command = new(query.Sql, x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(t);

            Found(await reader.ReadAsync(t) ? StatisticsWindow.Map(reader, DateTimeOffset.UtcNow) : null, "this database's pg_stat_database row");
        }),
        new("stats: control privileges", true, static async (x, t) =>
        {
            await using NpgsqlCommand command = new(StatisticsWindowQueries.ControlPrivilegesSql, x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(t);

            // Stock PostgreSQL grants both to PUBLIC, so every role of the matrix may call both.
            Found(await reader.ReadAsync(t) && reader.GetBoolean(0) && reader.GetBoolean(1) ? reader : null, "EXECUTE on both control functions");
        }),
        new("stats: control", true, static async (x, t) =>
        {
            await using NpgsqlCommand command = new(StatisticsWindowQueries.ControlSql(true, true), x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(t);

            StatisticsControl? control = await reader.ReadAsync(t) ? StatisticsWindow.MapControl(reader) : null;
            Found(control is { Timeline: not null, SystemIdentifier: not null } ? control : null, "timeline and system identifier");
        }),

        // ---- W5-R2: the Replication page's facts - every section, as the read the page makes ---------------------
        new("replication: facts", true, static async (x, t) => Clean(
            await x.Services.GetRequiredService<IReplicationFactsReader>().ReadAsync(x.Resolved, t))),
        new("replication: this database's tablespace size", false, static async (x, t) => Found(
            await x.Services.GetRequiredService<IReplicationFactsReader>().TablespaceSizeAsync(x.Resolved, "pg_default", t), "pg_default's size")),

        // ---- W5-S2: the Server page's facts - every section, as the read the page makes ----------------------------
        new("server: settings, extensions, connection and grants", true, static async (x, t) => ServerClean(
            await x.Services.GetRequiredService<PostgresStudio.Services.Server.IServerSettingsFactsReader>().ReadAsync(x.Resolved, t))),

        // ---- W5-Q1: the Statements page's facts - the statement sweep's own read, as the page takes it ----------------
        // Not lock-free: the counters read takes the extension's own AccessShareLock on pg_stat_statements_info, as the
        // statement checks always have - only DROP or ALTER EXTENSION queues against it. StatementsLiveTests proves it is the
        // only lock beyond a catalog read.
        new("statements: facts", false, static async (x, t) => StatementsClean(
            await x.Services.GetRequiredService<PostgresStudio.Services.Statements.IStatementFacts>().ReadAsync(x.Resolved, t))),
        // ---- W5-B1: the lint reads - T15's keys, the usage guard's counters age, T26's keys, T19 and T20's partitions -------
        new("lints: foreign keys", true, static async (x, t) =>
        {
            IReadOnlyList<HealthForeignKeyRow> keys = await QueryAsync(x, IndexLintQueries.ForeignKeys(LintTargets), IndexLintQueries.ReadForeignKey, t);
            Owned(x, keys.FirstOrDefault(static y => y.Table == "child" && y.ReferencedTable == "parent"), "store.child's key to store.parent");
        }),
        new("lints: counters age", true, static (x, t) => QueryAsync(
            x, IndexLintQueries.CountersAge(LintTargets, PostgresFeatures.Has(PostgresFeature.LastScanColumns, x.Connection.PostgreSqlVersion.Major * 10_000)),
            IndexLintQueries.ReadCountersAge, t)),
        new("lints: integer keys", true, static (x, t) => QueryAsync(x, IndexLintQueries.IntegerKeys(LintTargets), IndexLintQueries.ReadIntegerKey, t)),
        new("lints: partitions", true, static async (x, t) =>
        {
            // store.r's bounds are deparsed with no relation, so its partition the matrix holds ACCESS EXCLUSIVE on is read too.
            IReadOnlyList<HealthPartitionedTableRow> tables = await QueryAsync(x, PartitionQueries.Partitions(LintTargets), PartitionQueries.ReadPartitionedTable, t);
            Owned(x, tables.FirstOrDefault(static y => y.Table == "r" && y.NewestUpper is not null && y.HasDefault), "store.r's bounds and its default partition");
        }),

        // ---- X4: T18's column skew - store.l is list-partitioned on tenant_id, so it is read by its partitions, whose two
        // bounds are deparsed with no relation; store.r's tenant_id is read through pg_stats, which the setup never analysed.
        new("lints: column skew", true, static async (x, t) =>
        {
            IReadOnlyList<HealthColumnSkewRow> tables = await QueryAsync(x, ColumnSkewQueries.Skew(LintTargets, "tenant_id"), ColumnSkewQueries.ReadSkew, t);
            Owned(
                x,
                tables.FirstOrDefault(static y => y is { Table: "l", ByPartition: true, Values: 2, TopValues.Count: 2 }
                    && y.TopValues.All(static v => PartitionBoundText.SingleListValue(v) is "a" or "b")),
                "store.l's two tenant partitions, read by their bounds");
        }),
    ];

    /// <summary>The fixture's tables the lint reads are asked about (W5-B1): a range and a list partitioning, and a key.</summary>
    /// <remarks>A property, not a field: <see cref="All" /> is initialised first, and reads it.</remarks>
    private static HealthTableTarget[] LintTargets =>
        [new("store", "r"), new("store", "l"), new("store", "parent"), new("store", "child")];

    /// <summary>Runs a health statement on the read's transaction and maps every row.</summary>
    private static async Task<IReadOnlyList<T>> QueryAsync<T>(MatrixReadContext x, HealthQuery query, Func<NpgsqlDataReader, T> map, CancellationToken token)
    {
        await using NpgsqlCommand command = new(query.Sql, x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
        command.Parameters.AddRange(query.Parameters.Select(static p => p.Clone()).ToArray());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);

        List<T> rows = [];

        while (await reader.ReadAsync(token))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    /// <summary>
    /// Fails a read that found nothing the owner of <c>store</c> must find; a role without <c>USAGE</c> on it is admitted none,
    /// as the browser admits none.
    /// </summary>
    private static void Owned(MatrixReadContext x, object? value, string what)
    {
        if (x.Connection.UserName == "app")
        {
            Found(value, what);
        }
    }

    /// <summary>
    /// Fails a Statements facts read that failed: on every release the read reads, or says which expected state stopped it -
    /// the role with nothing recorded here yet - never a failure.
    /// </summary>
    private static void StatementsClean(PostgresStudio.Services.Statements.StatementFacts facts)
    {
        if (facts.State == PostgresStudio.Services.Statements.StatementsState.Failed)
        {
            throw new InvalidOperationException("The statements facts read failed: " + facts.Failure);
        }
    }

    /// <summary>
    /// Fails a facts read any section of which failed: on every release each section reads, or says it does not apply or is
    /// not on that release - never a SQLSTATE.
    /// </summary>
    private static void Clean(ReplicationFacts facts)
    {
        if (facts.Failures.Count > 0)
        {
            throw new InvalidOperationException("The replication facts read failed: " + string.Join("; ", facts.Failures));
        }
    }

    /// <summary>Fails a Server page facts read any section of which failed: on every release each one reads.</summary>
    private static void ServerClean(PostgresStudio.Services.Server.ServerSettingsFacts facts)
    {
        if (facts.Failures.Count > 0)
        {
            throw new InvalidOperationException("The server facts read failed: " + string.Join("; ", facts.Failures));
        }
    }

    /// <summary>Fails a by-name read that found nothing: the fixture made the object, so "nothing" is a read that did not read.</summary>
    private static void Found(object? value, string what)
    {
        if (value is null)
        {
            throw new InvalidOperationException("The read found no " + what + ", which the matrix fixture creates.");
        }
    }
}
