using System.Globalization;

using MartenStudio.SampleDomain.Generation;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.SampleDomain;

/// <summary>What the demo's own schemas in a database say about who seeded them.</summary>
public enum SampleDatabaseState
{
    /// <summary>No demo document holds a row: nothing to migrate over, so the seeder may start from here.</summary>
    Empty,

    /// <summary>This build's seeder seeded it (<see cref="SeedMarker" /> at <see cref="SampleDataSeeder.SeedVersion" />).</summary>
    Current,

    /// <summary>
    /// Another build's seeder seeded it: its marker names another version, and its tables may have a shape this build's
    /// store cannot migrate over the rows in them.
    /// </summary>
    Stale,

    /// <summary>Demo document tables hold rows, and no seed marker says the demo wrote them.</summary>
    Unmarked,
}

/// <summary>What <see cref="SampleDatabase.InspectAsync(string, CancellationToken)" /> found.</summary>
/// <param name="State">Who seeded the demo's schemas.</param>
/// <param name="SeedVersion">The version the database's <see cref="SeedMarker" /> names, when it has one.</param>
public readonly record struct SampleDatabaseInspection(SampleDatabaseState State, int? SeedVersion)
{
    /// <summary>Whether the demo's schemas have to be dropped before this build can seed them.</summary>
    public bool NeedsReseed => State is SampleDatabaseState.Stale or SampleDatabaseState.Unmarked;
}

/// <summary>
/// The demo's schemas in a database, judged before Marten touches them, and dropped when this build cannot seed over
/// them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why before Marten.</b> A database another build of the sample seeded can hold rows this build's store cannot
/// migrate over. The case that was hit: a database seeded on 2026-09-14 (seed version 2), before invoices had a
/// foreign key to their customer, whose customers had since been emptied. Marten builds a document type's storage the
/// first time anything asks for it, and the seeder's re-seed path asks for it first - <c>DeleteDocumentsByTypeAsync</c>
/// ensures the storage exists before it truncates - so adding <c>mt_doc_invoice.customer_id</c> (which Marten
/// backfills from the JSON) and then its foreign key failed with <c>23503 mt_doc_invoice_customer_id_fkey</c>, and the
/// host never started. Nothing the seeder does once Marten is in the loop can avoid that: the migration runs first.
/// </para>
/// <para>
/// <b>What is read.</b> The catalog and the one well-known <see cref="SeedMarker" /> row, through plain Npgsql: no
/// Marten call, so nothing here creates or migrates anything. A database is <see cref="SampleDatabaseState.Stale" />
/// when its marker names another <see cref="SampleDataSeeder.SeedVersion" />, and
/// <see cref="SampleDatabaseState.Unmarked" /> when a demo document table holds rows and no marker says the demo wrote
/// them.
/// </para>
/// <para>
/// <b>What is dropped.</b> <see cref="ReseedAsync" /> drops the demo's two Marten schemas,
/// <see cref="SampleStore.DocumentSchema" /> and <see cref="SampleStore.EventSchema" />, with everything in them, so
/// that Marten builds them again in this build's shape and the seeder fills them from nothing. The relational demo's
/// <c>quartz</c> and <c>legacy</c> schemas are left alone: they are idempotent and carry markers of their own, and the
/// foreign key <c>legacy.customer_credit</c> loses with <c>mt_doc_customer</c> is added again by the next apply. Whether
/// a database may be dropped is the host's decision, not this class's.
/// </para>
/// </remarks>
public static class SampleDatabase
{
    /// <summary>The table Marten keeps <see cref="SeedMarker" /> in.</summary>
    public const string MarkerTable = "mt_doc_seedmarker";

    /// <summary>
    /// Serialises an inspection and a drop against two hosts started at once on one database. Transaction-scoped, and
    /// a different number from the relational demo's lock.
    /// </summary>
    private const long AdvisoryLockKey = 0x4D53_5F53_5441_4C45;

    /// <summary>The schemas <see cref="ReseedAsync" /> drops, in that order.</summary>
    public static IReadOnlyList<string> Schemas { get; } = [SampleStore.DocumentSchema, SampleStore.EventSchema];

    /// <summary>Judges the demo's schemas in the database <paramref name="connectionString" /> names.</summary>
    /// <param name="connectionString">The database the sample's store is pointed at.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Who seeded it.</returns>
    public static async Task<SampleDatabaseInspection> InspectAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await InspectAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the demo's two Marten schemas when the database still needs it, in one transaction under an advisory lock,
    /// judging the database again under the lock first.
    /// </summary>
    /// <param name="connectionString">The database the sample's store is pointed at.</param>
    /// <param name="cancellationToken">Cancels the drop, which then rolls back as a whole.</param>
    /// <returns>
    /// What the database held when the lock was taken. When <see cref="SampleDatabaseInspection.NeedsReseed" /> is
    /// false nothing was dropped: another host got there first, or the database never needed it.
    /// </returns>
    public static async Task<SampleDatabaseInspection> ReseedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand("select pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = AdvisoryLockKey });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SampleDatabaseInspection found = await InspectAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (found.NeedsReseed)
        {
            foreach (string schema in Schemas)
            {
                await using var drop = new NpgsqlCommand(
                    "drop schema if exists " + DemoDataSql.Quote(schema) + " cascade",
                    connection,
                    transaction);

                await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return found;
    }

    private static async Task<SampleDatabaseInspection> InspectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        int? version = await SeedVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        if (version is { } seeded)
        {
            return new SampleDatabaseInspection(
                seeded == SampleDataSeeder.SeedVersion ? SampleDatabaseState.Current : SampleDatabaseState.Stale,
                seeded);
        }

        return await AnyDocumentRowAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ? new SampleDatabaseInspection(SampleDatabaseState.Unmarked, null)
            : new SampleDatabaseInspection(SampleDatabaseState.Empty, null);
    }

    /// <summary>
    /// The version the well-known <see cref="SeedMarker" /> names, or <see langword="null" /> when there is no marker
    /// table, no marker row or no version in it.
    /// </summary>
    private static async Task<int?> SeedVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await ExistsAsync(connection, transaction, MarkerTable, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        // The property is spelt as the store's serializer writes it: as declared, which is the sample's setting, and
        // camel case for a store configured otherwise.
        await using var command = new NpgsqlCommand(
            "select coalesce(data ->> 'Version', data ->> 'version') from "
            + DemoDataSql.QuoteQualified(SampleStore.DocumentSchema, MarkerTable) + " where id = @id",
            connection,
            transaction);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = SeedMarker.WellKnownId });

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    /// <summary>Whether any demo document table - <c>mt_doc_*</c> in the document schema - holds a row.</summary>
    private static async Task<bool> AnyDocumentRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        List<string> tables = [];

        await using (var list = new NpgsqlCommand(
            "select c.relname from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
            + "where n.nspname = @schema and c.relkind in ('r', 'p') and c.relname like 'mt\\_doc\\_%' order by 1",
            connection,
            transaction))
        {
            list.Parameters.Add(new NpgsqlParameter("schema", NpgsqlDbType.Text) { Value = SampleStore.DocumentSchema });

            await using NpgsqlDataReader reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (string table in tables)
        {
            await using var probe = new NpgsqlCommand(
                "select exists (select 1 from " + DemoDataSql.QuoteQualified(SampleStore.DocumentSchema, table) + ")",
                connection,
                transaction);

            if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_catalog.to_regclass(@name) is not null", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = DemoDataSql.QuoteQualified(SampleStore.DocumentSchema, table) });

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
