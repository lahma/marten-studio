using System.Data;
using System.Text;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.SampleDomain.Relational;

/// <summary>What one <see cref="RelationalDemoSchema.ApplyAsync" /> did and found.</summary>
/// <param name="RowsWritten">
/// Rows the seeding inserted or updated, as Postgres counted them. Zero when every table already had its
/// rows, which is what a second run against the same database reports.
/// </param>
/// <param name="HasCitextColumn">
/// Whether <c>legacy.integration_messages</c> has its <c>citext</c> column. It is only added when the
/// <c>citext</c> extension was already installed; the demo never installs an extension itself.
/// </param>
/// <param name="HasCustomerForeignKey">
/// Whether <c>legacy.customer_credit</c> has its foreign key into <c>studio_sample.mt_doc_customer</c>,
/// which needs Marten to have created that table first.
/// </param>
/// <param name="HasAppSettings">
/// Whether <c>studio_sample.app_settings</c> exists, which needs Marten to have created its schema first.
/// </param>
public readonly record struct RelationalDemoResult(
    int RowsWritten,
    bool HasCitextColumn,
    bool HasCustomerForeignKey,
    bool HasAppSettings);

/// <summary>
/// Two non-Marten schemas that live beside the demo store: Quartz.NET's real PostgreSQL job store in
/// <c>quartz</c>, and a hand-made <c>legacy</c> schema that exercises every catalog edge case - plus one
/// ordinary table inside the Marten document schema itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the sample has them.</b> The studio's database browser reads the tables, views, routines,
/// triggers, sequences and types that share a database with the Marten store without being Marten's.
/// A demo whose database holds nothing but Marten's own tables has nothing to show it with, and a test
/// that builds its own three tables proves only what its author thought of. These are the realistic
/// shapes: composite keys everywhere and cascading foreign keys (Quartz), and a table with no key, a
/// NOT VALID foreign key with a row that violates it, an unpopulated materialized view, a partitioned
/// table, a disabled trigger and a foreign key into a Marten document table (legacy).
/// </para>
/// <para>
/// <b>No package.</b> Everything here is plain SQL over Npgsql, which arrives with Marten; the Quartz
/// schema is vendored rather than referenced (AGENTS.md hard rule 1), and this project still does not
/// reference MartenStudio (D17).
/// </para>
/// <para>
/// <b>Idempotent, and in one transaction.</b> <see cref="ApplyAsync" /> takes a transaction-scoped
/// advisory lock, runs <c>QuartzSchema.sql</c> and <c>LegacySchema.sql</c> (both written to be run any
/// number of times and never to drop anything), then fills each table that is empty and leaves every
/// table that is not alone. So a second start of the sample on a reused database changes nothing, and a
/// table something else emptied - Marten's <c>truncate … cascade</c> of <c>mt_doc_customer</c> reaches
/// <c>legacy.customer_credit</c> through its foreign key - is refilled on the next start.
/// </para>
/// <para>
/// <b>Run it after Marten has seeded.</b> <c>legacy.customer_credit</c>'s foreign key and rows need
/// <c>studio_sample.mt_doc_customer</c> and the seeded customers in it, and <c>app_settings</c> needs the
/// <c>studio_sample</c> schema; <see cref="SampleDataSeeder" /> calls this last for exactly that reason.
/// Against a database with no Marten store the Marten-anchored half is skipped rather than failed, and
/// <see cref="RelationalDemoResult" /> says which parts are present.
/// </para>
/// </remarks>
public static class RelationalDemoSchema
{
    /// <summary>The schema Quartz.NET's tables are vendored into.</summary>
    public const string QuartzSchemaName = "quartz";

    /// <summary>The hand-made schema of catalog edge cases.</summary>
    public const string LegacySchemaName = "legacy";

    /// <summary>The Marten document schema <see cref="AppSettingsTable" /> is created in.</summary>
    public const string MartenDocumentSchemaName = SampleStore.DocumentSchema;

    /// <summary>How many tables Quartz.NET's schema has.</summary>
    public const int QuartzTableCount = 14;

    /// <summary>Quartz's job definitions: the parent of every trigger.</summary>
    public const string QuartzJobDetailsTable = "qrtz_job_details";

    /// <summary>Quartz's triggers, with three-column keys and tick timestamps.</summary>
    public const string QuartzTriggersTable = "qrtz_triggers";

    /// <summary>The ordinary table inside the Marten document schema.</summary>
    public const string AppSettingsTable = "app_settings";

    /// <summary>The legacy table with a foreign key into <c>mt_doc_customer</c>.</summary>
    public const string CustomerCreditTable = "customer_credit";

    /// <summary>The legacy table with no primary key and no unique index.</summary>
    public const string AuditLogTable = "audit_log";

    /// <summary>The legacy table with a self-referencing foreign key.</summary>
    public const string EmployeesTable = "employees";

    /// <summary>The legacy table whose foreign key is NOT VALID, with one row that violates it.</summary>
    public const string PurchaseOrderLinesTable = "purchase_order_lines";

    /// <summary>The legacy table with jsonb, TOASTed text, bytea and both kinds of timestamp.</summary>
    public const string IntegrationMessagesTable = "integration_messages";

    /// <summary>The forty-column legacy table.</summary>
    public const string ProductCatalogueTable = "product_catalogue";

    /// <summary>The range-partitioned legacy table.</summary>
    public const string SensorReadingsTable = "sensor_readings";

    /// <summary>The materialized view that is populated, and refreshed after seeding.</summary>
    public const string StockByRegionView = "stock_by_region";

    /// <summary>The materialized view created <c>WITH NO DATA</c> and never refreshed.</summary>
    public const string MonthlyOrderTotalsView = "monthly_order_totals";

    /// <summary>The two Quartz schedulers the seeding writes jobs and triggers for.</summary>
    public static IReadOnlyList<string> QuartzSchedulerNames => QuartzDemoData.SchedulerNames;

    /// <summary>
    /// Serialises two appliers against one database - two sample hosts started at once on the same
    /// connection string - so that they cannot both see an empty table and both fill it, or race each
    /// other's <c>CREATE TABLE IF NOT EXISTS</c> into a catalog unique violation. Transaction-scoped, so
    /// it cannot outlive the apply. An arbitrary constant, well away from Marten's own lock ids.
    /// </summary>
    private const long AdvisoryLockKey = 0x4D53_5F52_454C_4442;

    private const string QuartzScriptName = "QuartzSchema.sql";

    private const string LegacyScriptName = "LegacySchema.sql";

    /// <summary>The Quartz.NET schema script, exactly as embedded.</summary>
    public static string QuartzScript => ReadScript(QuartzScriptName);

    /// <summary>The legacy schema script, exactly as embedded.</summary>
    public static string LegacyScript => ReadScript(LegacyScriptName);

    /// <summary>
    /// Creates both schemas if they are missing, then fills every demo table that is empty.
    /// </summary>
    /// <param name="connection">
    /// A connection to the database the Marten store uses. Opened if it is not open yet; never closed or
    /// disposed here, because the caller owns it.
    /// </param>
    /// <param name="cancellationToken">Cancels the apply, which then rolls back as a whole.</param>
    /// <returns>What was written, and which of the optional parts are present.</returns>
    public static async Task<RelationalDemoResult> ApplyAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand("select pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bigint) { Value = AdvisoryLockKey });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteScriptAsync(connection, transaction, QuartzScript, cancellationToken).ConfigureAwait(false);
        await ExecuteScriptAsync(connection, transaction, LegacyScript, cancellationToken).ConfigureAwait(false);

        // Quartz stores every instant as UTC ticks and every duration as whole milliseconds; "now" is read
        // once so that a trigger's next fire time and the check-in times it is compared with agree.
        int rows = await QuartzDemoData.SeedAsync(connection, transaction, DateTime.UtcNow.Ticks, cancellationToken)
            .ConfigureAwait(false);

        rows += await LegacyDemoData.SeedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        RelationalDemoResult result = await ReadFactsAsync(connection, transaction, rows, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task ExecuteScriptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string script,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(script, connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RelationalDemoResult> ReadFactsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int rows,
        CancellationToken cancellationToken)
    {
        const string Sql =
            """
            select exists (select 1 from pg_catalog.pg_attribute
                           where attrelid = 'legacy.integration_messages'::regclass
                             and attname = 'sender' and not attisdropped),
                   exists (select 1 from pg_catalog.pg_constraint
                           where conrelid = 'legacy.customer_credit'::regclass
                             and conname = 'customer_credit_customer_id_fkey'),
                   pg_catalog.to_regclass('studio_sample.app_settings') is not null
            """;

        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new RelationalDemoResult(rows, reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2));
    }

    private static string ReadScript(string fileName)
    {
        string resource = typeof(RelationalDemoSchema).Namespace + "." + fileName;

        using Stream stream = typeof(RelationalDemoSchema).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                "The embedded script " + resource + " is missing. It is an EmbeddedResource in "
                + "MartenStudio.SampleDomain.csproj; a rename there has to be made here too.");

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
