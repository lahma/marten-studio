using System.Globalization;
using System.Text.RegularExpressions;

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
/// and the legacy schema - and on the tables created there later, and no other table. It is also <c>pg_monitor</c>, because the
/// studio's health checks, Sessions, Maintenance and the browser's catalog reads run as this role too: <c>pg_monitor</c> lets
/// them see every session, setting and statistic as the superuser did - the demo's health page would otherwise read only its
/// own sessions. That is more than "no rows": <c>pg_monitor</c> reads every session's current statement text, server-wide,
/// and <c>pg_stat_statements</c>' texts, so the console reads them too. On the demo's own throwaway server that is the point.
/// </para>
/// <para>
/// <b>Idempotent, the sample's to run, and only on a database that is the demo's.</b> <see cref="EnsureAsync(string, TextWriter?, CancellationToken)" />
/// runs on every start, before the store is touched, and only on the throwaway container or a database
/// <c>--allow-data-generation</c> names as the demo's, because a role is the server's and not the database's; it says whether
/// the role is usable. <see cref="GrantAsync" /> runs in the seeding, once the schemas exist, and grants only a role this login
/// is a member of - so on any other database it grants nothing. A login that may not create roles leaves the role unset and the
/// sample reads as its login, as before. The studio itself never creates a role or grants anything: this is the host's.
/// </para>
/// <para>
/// <b>Safe when two hosts start at once.</b> Concurrent <c>CREATE ROLE</c>s of one name fail with <c>duplicate_object</c> or,
/// in the race between the existence check and the catalog insert, <c>unique_violation</c> (23505); concurrent grants on one
/// schema fail with <c>tuple concurrently updated</c> (XX000). Measured on Postgres 17 by the W6 review. So every statement
/// here runs in a transaction that first takes the advisory lock <see cref="LockKey" />, and the role's creation also treats
/// either error as "somebody else made it".
/// </para>
/// </remarks>
public static partial class SampleConsoleRole
{
    /// <summary>The role's name.</summary>
    public const string Name = "studio_sample_reader";

    /// <summary>
    /// The transaction-level advisory lock every statement here takes first, so two hosts or two test runs that start together
    /// create the role and grant to it one after the other: the ASCII of <c>MSSAMPLE</c>.
    /// </summary>
    public const long LockKey = 0x4D53_5341_4D50_4C45;

    /// <summary>The schemas it reads: the store's documents and events, and the two relational demo schemas.</summary>
    public static IReadOnlyList<string> Schemas { get; } =
    [
        SampleStore.DocumentSchema,
        SampleStore.EventSchema,
        RelationalDemoSchema.QuartzSchemaName,
        RelationalDemoSchema.LegacySchemaName,
    ];

    private static string Lock => "pg_advisory_xact_lock(" + LockKey.ToString(CultureInfo.InvariantCulture) + ")";

    /// <summary>
    /// Creates the sample's role if it is missing, makes the connection's login a member so that <c>SET LOCAL ROLE</c> reaches
    /// it, and gives it <c>pg_monitor</c>; answers whether the login may use it. Call it only for a database that is the demo's.
    /// </summary>
    /// <param name="connectionString">The store's connection string.</param>
    /// <param name="output">Where to say why the role could not be made, or <see langword="null" />.</param>
    /// <param name="cancellationToken">The token.</param>
    public static Task<bool> EnsureAsync(string connectionString, TextWriter? output = null, CancellationToken cancellationToken = default) =>
        EnsureAsync(connectionString, Name, output, cancellationToken);

    /// <summary>
    /// <see cref="EnsureAsync(string, TextWriter?, CancellationToken)" /> for a role of another name - a test's own, so that it
    /// can start from a server that has none.
    /// </summary>
    /// <param name="connectionString">The store's connection string.</param>
    /// <param name="role">The role's name: lower-case letters, digits and underscores.</param>
    /// <param name="output">Where to say why the role could not be made, or <see langword="null" />.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async Task<bool> EnsureAsync(string connectionString, string role, TextWriter? output, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        RequirePlainName(role);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // The lock first, held to the commit: a second host waits here, then finds the role and only joins it.
            await ExecuteInLockAsync(
                    connection,
                    "do $$ begin "
                    + "perform " + Lock + "; "
                    + "if not exists (select from pg_catalog.pg_roles where rolname = '" + role + "') then "
                    + "begin create role " + role + " nologin; exception when duplicate_object or unique_violation then null; end; "
                    + "end if; end $$; "
                    + "grant " + role + " to current_user;",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            output?.WriteLine("The sample's studio reads as the store's login: it may not create or join role " + role + " ("
                + exception.MessageText + ").");
            return false;
        }

        try
        {
            await ExecuteInLockAsync(connection, "select " + Lock + "; grant pg_monitor to " + role + ";", cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            // The console still reads only what it is granted; the health checks see the role's own sessions alone.
            output?.WriteLine("Role " + role + " is not pg_monitor (" + exception.MessageText + "): the studio's health checks see only its own sessions.");
        }

        return true;
    }

    /// <summary>
    /// Grants the role <c>USAGE</c> on each of <paramref name="schemas" /> that exists, <c>SELECT</c> on its tables and
    /// sequences, and <c>SELECT</c> on the tables the login creates there later - nothing on a schema that is not there yet, and
    /// nothing at all unless the login is a member of the role, which <see cref="EnsureAsync(string, TextWriter?, CancellationToken)" />
    /// makes it on a database that is the demo's.
    /// </summary>
    /// <param name="connection">An open connection, as the store's login, in no transaction.</param>
    /// <param name="logger">Where a refusal is said, or <see langword="null" />.</param>
    /// <param name="schemas">The schemas, <see cref="Schemas" /> unless a test names its own.</param>
    /// <param name="role">The role, <see cref="Name" /> unless a test names its own.</param>
    /// <param name="cancellationToken">The token.</param>
    public static async Task GrantAsync(
        NpgsqlConnection connection,
        ILogger? logger = null,
        IReadOnlyList<string>? schemas = null,
        string role = Name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        RequirePlainName(role);
        logger ??= NullLogger.Instance;

        if (!await IsMemberAsync(connection, role, cancellationToken).ConfigureAwait(false))
        {
            // Not a role this login reads as: the host did not make it here (another database than the demo's), so the
            // sample grants it nothing.
            return;
        }

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
                await ExecuteInLockAsync(
                        connection,
                        "select " + Lock + "; "
                        + "grant usage on schema " + quoted + " to " + role + "; "
                        + "grant select on all tables in schema " + quoted + " to " + role + "; "
                        + "grant select on all sequences in schema " + quoted + " to " + role + "; "
                        + "alter default privileges in schema " + quoted + " grant select on tables to " + role + ";",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.UndefinedObject)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("The sample did not grant {Role} the schema {Schema}: {Reason}", role, schema, exception.MessageText);
                }
            }
        }
    }

    /// <summary>Whether the connection's login is a member of <paramref name="role" />, which must exist for that.</summary>
    private static async Task<bool> IsMemberAsync(NpgsqlConnection connection, string role, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand member = new(
            "select coalesce((select pg_catalog.pg_has_role(current_user, oid, 'MEMBER') from pg_catalog.pg_roles where rolname = @role), false)",
            connection);
        member.Parameters.AddWithValue("role", role);
        return await member.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Runs <paramref name="sql" /> - which takes <see cref="LockKey" /> first - in a transaction of its own, and commits.</summary>
    private static async Task ExecuteInLockAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand command = new(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A role name that goes into the statements as it is: lower-case letters, digits and underscores, never quoted.</summary>
    private static void RequirePlainName(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);

        if (!PlainName().IsMatch(role))
        {
            throw new ArgumentException("A role name here is lower-case letters, digits and underscores, starting with a letter.", nameof(role));
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainName();
}
