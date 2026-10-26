using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
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
    ];

    /// <summary>Fails a by-name read that found nothing: the fixture made the object, so "nothing" is a read that did not read.</summary>
    private static void Found(object? value, string what)
    {
        if (value is null)
        {
            throw new InvalidOperationException("The read found no " + what + ", which the matrix fixture creates.");
        }
    }
}
