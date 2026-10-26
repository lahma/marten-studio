using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using PostgresStudio.SampleData;

namespace MartenStudio.SampleDomain;

/// <summary>
/// The role the sample's studio reads as (<c>MartenStudioOptions.SqlConsoleRole</c>, wave 6): narrower than the store's own
/// login, which on the throwaway container is the server's superuser - so K05, "SQL console without a narrowed role", shows
/// its remedy applied rather than a Warning.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it may read.</b> <c>SELECT</c> on the demo's four schemas - the store's documents and events, Quartz.NET's job store
/// and the legacy schema - and on the tables created there later, and nothing else of the data. It is also
/// <c>pg_monitor</c>, because the studio's health checks, Sessions, Maintenance and the browser's catalog reads run as this role
/// too: <c>pg_monitor</c> lets them see every session, setting and statistic as the superuser did - the demo's health page
/// would otherwise read only its own sessions - and reads no row of any table.
/// </para>
/// <para>
/// <b>Idempotent, and the sample's to run.</b> <see cref="EnsureAsync" /> runs on every start, before the store is touched, and
/// says whether the role is usable; <see cref="GrantAsync" /> runs in the seeding, once the schemas exist. A login that may not
/// create roles - a database somebody pointed the sample at with <c>--allow-data-generation</c> - leaves the role unset and the
/// sample reads as its login, as before. The studio itself never creates a role or grants anything: this is the host's.
/// </para>
/// </remarks>
public static class SampleConsoleRole
{
    /// <summary>The role's name.</summary>
    public const string Name = "studio_sample_reader";

    /// <summary>The schemas it reads: the store's documents and events, and the two relational demo schemas.</summary>
    public static IReadOnlyList<string> Schemas { get; } =
    [
        SampleStore.DocumentSchema,
        SampleStore.EventSchema,
        RelationalDemoSchema.QuartzSchemaName,
        RelationalDemoSchema.LegacySchemaName,
    ];

    /// <summary>
    /// Creates the role if it is missing, makes the connection's login a member so that <c>SET LOCAL ROLE</c> reaches it, and
    /// gives it <c>pg_monitor</c>; answers whether the login may use it.
    /// </summary>
    /// <param name="connectionString">The store's connection string.</param>
    /// <param name="output">Where to say why the role could not be made, or <see langword="null" />.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async Task<bool> EnsureAsync(string connectionString, TextWriter? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Two hosts starting at once may both see no role: the loser's duplicate is the role it wanted.
            await ExecuteAsync(
                    connection,
                    "do $$ begin "
                    + "if not exists (select from pg_catalog.pg_roles where rolname = '" + Name + "') then "
                    + "begin create role " + Name + " nologin; exception when duplicate_object then null; end; "
                    + "end if; end $$; "
                    + "grant " + Name + " to current_user;",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            output?.WriteLine("The sample's studio reads as the store's login: it may not create or join role " + Name + " ("
                + exception.MessageText + ").");
            return false;
        }

        try
        {
            await ExecuteAsync(connection, "grant pg_monitor to " + Name + ";", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            // The console still reads only what it is granted; the health checks see the role's own sessions alone.
            output?.WriteLine("Role " + Name + " is not pg_monitor (" + exception.MessageText + "): the studio's health checks see only its own sessions.");
        }

        return true;
    }

    /// <summary>
    /// Grants the role <c>USAGE</c> on each of <paramref name="schemas" /> that exists, <c>SELECT</c> on its tables and
    /// sequences, and <c>SELECT</c> on the tables the login creates there later - nothing on a schema that is not there yet.
    /// </summary>
    /// <param name="connection">An open connection, as the store's login.</param>
    /// <param name="logger">Where a refusal is said, or <see langword="null" />.</param>
    /// <param name="schemas">The schemas, <see cref="Schemas" /> unless a test names its own.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async Task GrantAsync(
        NpgsqlConnection connection,
        ILogger? logger = null,
        IReadOnlyList<string>? schemas = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        logger ??= NullLogger.Instance;

        List<string> existing = [];

        await using (NpgsqlCommand find = new("select nspname from pg_catalog.pg_namespace where nspname = any(@schemas) order by nspname", connection))
        {
            find.Parameters.AddWithValue("schemas", (schemas ?? Schemas).ToArray());
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (string schema in existing)
        {
            string quoted = "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

            try
            {
                await ExecuteAsync(
                        connection,
                        "grant usage on schema " + quoted + " to " + Name + "; "
                        + "grant select on all tables in schema " + quoted + " to " + Name + "; "
                        + "grant select on all sequences in schema " + quoted + " to " + Name + "; "
                        + "alter default privileges in schema " + quoted + " grant select on tables to " + Name + ";",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedObject)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("The sample did not grant {Role} the schema {Schema}: {Reason}", Name, schema, exception.MessageText);
                }
            }
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
