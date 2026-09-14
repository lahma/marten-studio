using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One column of a SQL console result.</summary>
/// <param name="Name">The column name as Postgres returned it.</param>
/// <param name="PostgresType">The Postgres type name, shown in the grid header.</param>
internal sealed record SqlResultColumn(string Name, string PostgresType);

/// <summary>A Postgres error, as values rather than as an exception.</summary>
/// <param name="SqlState">The five-character SQLSTATE, for example <c>25006</c> or <c>57014</c>.</param>
/// <param name="MessageText">Postgres' own message.</param>
/// <param name="Position">The one-based character position in the statement, or zero.</param>
/// <param name="Detail">Postgres' detail line, when there is one.</param>
/// <param name="Hint">Postgres' hint line, when there is one.</param>
internal sealed record SqlError(string SqlState, string MessageText, int Position, string? Detail, string? Hint);

/// <summary>The result of one SQL console run.</summary>
/// <param name="Columns">The columns, empty when the statement failed.</param>
/// <param name="Rows">The rows, already formatted and capped.</param>
/// <param name="Truncated">Whether the reader was stopped before the result ended.</param>
/// <param name="Duration">How long the statement took, measured around the execution alone.</param>
/// <param name="Error">The Postgres error, when there was one.</param>
internal sealed record SqlResultSet(
    IReadOnlyList<SqlResultColumn> Columns,
    IReadOnlyList<IReadOnlyList<SqlCell>> Rows,
    bool Truncated,
    TimeSpan Duration,
    SqlError? Error)
{
    /// <summary>Whether the statement ran without a Postgres error.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>What the SQL console is allowed to do, all of it clamped on the server.</summary>
internal sealed record ReadOnlySqlOptions
{
    private readonly TimeSpan statementTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a statement may run before Postgres cancels it. Must be positive.
    /// </summary>
    /// <remarks>
    /// <b>Zero is not "no timeout" here, it is a configuration error.</b> Postgres reads
    /// <c>statement_timeout = 0</c> as <em>disabled</em>, so a host that wrote
    /// <c>StatementTimeout = TimeSpan.Zero</c> meaning "as short as possible" would have turned the one
    /// limit that stops a console query from running until the database falls over into no limit at all.
    /// The refusal is thrown from the initialiser, which is the only place an <c>init</c>-only record can
    /// put a constructor check, so it fires where the mistake was written rather than on the first query.
    /// </remarks>
    public TimeSpan StatementTimeout
    {
        get => statementTimeout;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            statementTimeout = value;
        }
    }

    /// <summary>How long the statement may wait for a lock. Short on purpose: a console must not block DDL.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the transaction may sit idle before Postgres ends it.</summary>
    public TimeSpan IdleInTransactionTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How many rows are materialised before the reader is stopped.</summary>
    public int MaxRows { get; init; } = 500;

    /// <summary>
    /// A role to switch to for the statement, or <see langword="null"/>. Validated, and set with
    /// <c>set_config</c> so the name is a parameter rather than text in a statement.
    /// </summary>
    public string? Role { get; init; }

    /// <summary>The cell caps.</summary>
    public SqlValueFormatterOptions? Formatting { get; init; }
}

/// <summary>
/// Runs one statement, read-only, on a connection the caller supplies — and rolls back whatever happened.
/// </summary>
/// <remarks>
/// <para>
/// <b>The transaction is the guard</b> (D13). Every statement runs inside
/// <c>BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK</c>, so a write the statement parser did not spot
/// comes back from Postgres as <c>25006: cannot execute … in a read-only transaction</c> and changes
/// nothing. <see cref="ReadOnlySqlGuard"/> runs first only to produce a nicer message for the common case.
/// </para>
/// <para>
/// Three timeouts, because there are three ways to hang. <c>statement_timeout</c> bounds the query;
/// <c>lock_timeout</c> keeps the console from sitting on a lock queue in front of somebody's migration;
/// <c>idle_in_transaction_session_timeout</c> covers the transaction outliving the circuit that started
/// it. All three are <c>SET LOCAL</c>, so they die with the transaction.
/// </para>
/// <para>
/// <b>The row cap stops the reader; it never appends <c>LIMIT</c>.</b> Rewriting somebody's SQL would make
/// the result a different question from the one they asked - and would silently change an aggregate, a
/// window function or a <c>LIMIT</c> they wrote themselves. So the statement is exactly what was typed,
/// and the studio simply stops reading and says it stopped.
/// </para>
/// <para>
/// Every GUC, including the role, is set through <c>select set_config(@name, @value, true)</c> rather than
/// a <c>SET</c> statement, because <c>SET</c> takes no parameters and would mean interpolating text into
/// SQL. The role is validated as an identifier as well; both, not either.
/// </para>
/// </remarks>
internal sealed class ReadOnlySqlSession
{
    /// <summary>Sets a GUC for the current transaction. The name and the value are both parameters.</summary>
    internal const string SetConfigSql = "select set_config(@name, @value, true)";

    /// <summary>Makes the transaction read-only. No parameters, and no user input anywhere in it.</summary>
    internal const string ReadOnlySql = "set transaction read only";

    private readonly ReadOnlySqlOptions options;
    private readonly SqlValueFormatter formatter;

    /// <summary>Creates a session runner.</summary>
    public ReadOnlySqlSession(ReadOnlySqlOptions? sessionOptions = null)
    {
        options = sessionOptions ?? new ReadOnlySqlOptions();
        formatter = new SqlValueFormatter(options.Formatting);
    }

    /// <summary>
    /// Whether a role name is one the session will switch to: an unquoted Postgres identifier, at most the
    /// 63 bytes Postgres itself allows.
    /// </summary>
    public static bool IsValidRoleName(string? role)
    {
        if (string.IsNullOrEmpty(role) || role.Length > 63)
        {
            return false;
        }

        if (!char.IsAsciiLetter(role[0]) && role[0] != '_')
        {
            return false;
        }

        foreach (var c in role)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '$')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs one statement and rolls back. The connection must already be open; it is not disposed here.
    /// </summary>
    public Task<SqlResultSet> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(connection, sql, null, cancellationToken);

    /// <summary>
    /// The same run, with the statement's parameters bound by <paramref name="bind" />.
    /// </summary>
    /// <remarks>
    /// The SQL console itself never binds anything - what somebody typed is sent verbatim, which is the
    /// whole contract of a console. The hook exists for a statement the <em>studio</em> composed and wants
    /// planned: a Mode A <c>EXPLAIN</c> carries the same <c>@tenant</c> and <c>@limit</c> the query did,
    /// and without them Postgres answers "there is no parameter $1" instead of a plan.
    /// </remarks>
    public async Task<SqlResultSet> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        if (options.Role is not null && !IsValidRoleName(options.Role))
        {
            throw new InvalidOperationException($"'{options.Role}' is not a valid Postgres role name.");
        }

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(connection, transaction, ReadOnlySql, cancellationToken).ConfigureAwait(false);

            await SetLocalAsync(connection, transaction, "statement_timeout",
                Milliseconds(options.StatementTimeout), cancellationToken).ConfigureAwait(false);
            await SetLocalAsync(connection, transaction, "lock_timeout",
                Milliseconds(options.LockTimeout), cancellationToken).ConfigureAwait(false);
            await SetLocalAsync(connection, transaction, "idle_in_transaction_session_timeout",
                Milliseconds(options.IdleInTransactionTimeout), cancellationToken).ConfigureAwait(false);

            if (options.Role is { } role)
            {
                await SetLocalAsync(connection, transaction, "role", role, cancellationToken).ConfigureAwait(false);
            }

            return await ReadAsync(connection, transaction, sql, bind, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always, and with its own token: a rollback that is skipped because the caller cancelled is a
            // transaction left open on a pooled connection.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // The connection is already broken; disposing the transaction is all that is left to do.
            }
            catch (InvalidOperationException)
            {
                // The transaction has already completed.
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SqlResultSet> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        List<SqlResultColumn> columns = [];
        List<IReadOnlyList<SqlCell>> rows = [];
        var truncated = false;
        var stopwatch = Stopwatch.StartNew();

        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            // A little past the server-side timeout: statement_timeout is what should fire, because it
            // produces 57014 with a message, while a client-side timeout only breaks the connection.
            CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 5,
        };

        bind?.Invoke(command);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new SqlResultColumn(reader.GetName(i), reader.GetDataTypeName(i)));
            }

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == options.MaxRows)
                {
                    // One row past the cap only to know whether to say "truncated"; it is not kept.
                    truncated = true;
                    break;
                }

                var row = new SqlCell[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = ReadCell(reader, i, columns[i].PostgresType);
                }

                rows.Add(row);
            }

            stopwatch.Stop();
            return new SqlResultSet(columns, rows, truncated, stopwatch.Elapsed, null);
        }
        catch (PostgresException exception)
        {
            stopwatch.Stop();

            return new SqlResultSet(
                [],
                [],
                false,
                stopwatch.Elapsed,
                new SqlError(
                    exception.SqlState,
                    exception.MessageText,
                    exception.Position,
                    exception.Detail,
                    exception.Hint));
        }
        catch (DbException exception)
        {
            // Not every failure of a statement is a PostgresException. A broken connection, a protocol
            // error or a client-side timeout arrives as an NpgsqlException with no SQLSTATE, and letting it
            // escape would take down the circuit for something the console can perfectly well render as a
            // failed run. SqlState is on DbException itself, and is null for exactly those cases.
            stopwatch.Stop();

            return Failed(stopwatch.Elapsed, exception.SqlState ?? string.Empty, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            // Npgsql's answer to `copy … from stdin` reaching ExecuteReader: the statement parses and is
            // accepted by the server, and the *driver* refuses it, as a NotSupportedException that is not a
            // DbException at all.
            stopwatch.Stop();

            return Failed(stopwatch.Elapsed, string.Empty, exception.Message);
        }
    }

    private static SqlResultSet Failed(TimeSpan elapsed, string sqlState, string message) =>
        new([], [], false, elapsed, new SqlError(sqlState, message, 0, null, null));

    private SqlCell ReadCell(NpgsqlDataReader reader, int ordinal, string postgresType)
    {
        try
        {
            return formatter.Format(reader.GetValue(ordinal), postgresType);
        }
        catch (Exception exception) when (exception is InvalidCastException or NotSupportedException)
        {
            // An extension type with no Npgsql mapping. The grid says so rather than failing the whole run.
            return new SqlCell($"(cannot read {postgresType})", SqlCellKind.Text, false, 0);
        }
    }

    private static async Task SetLocalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(SetConfigSql, connection, transaction);

        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A GUC value in milliseconds, never below one.
    /// </summary>
    /// <remarks>
    /// <c>0ms</c> means <em>disabled</em> to Postgres for all three of <c>statement_timeout</c>,
    /// <c>lock_timeout</c> and <c>idle_in_transaction_session_timeout</c>, so clamping a zero or negative
    /// <see cref="TimeSpan"/> to zero turned the shortest timeout anybody could ask for into no timeout at
    /// all. One millisecond is the smallest thing Postgres can be told, and a caller who asked for less
    /// than that meant "immediately", not "never".
    /// </remarks>
    internal static string Milliseconds(TimeSpan value) =>
        Math.Max((long)value.TotalMilliseconds, 1).ToString(CultureInfo.InvariantCulture) + "ms";
}
