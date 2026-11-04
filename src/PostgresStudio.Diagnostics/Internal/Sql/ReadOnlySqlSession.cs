using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

using Npgsql;

using NpgsqlTypes;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Internal.Sql;

/// <summary>One column of a SQL console result.</summary>
/// <param name="Name">The column name as Postgres returned it.</param>
/// <param name="PostgresType">The Postgres type name, shown in the grid header.</param>
internal sealed record SqlResultColumn(string Name, string PostgresType)
{
    /// <summary>
    /// The pgvector type the column holds - <c>vector</c>, <c>halfvec</c> or <c>sparsevec</c>, owned by the <c>vector</c>
    /// extension - or <see langword="null" /> for any other column. Its cells are the text Postgres prints, so the grid can
    /// collapse them as the database browser does. The dimensions are not known here: a result column carries a type, not
    /// the declaration of a table's column.
    /// </summary>
    public PgVectorColumn? Vector { get; init; }
}

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

    /// <summary>
    /// The plan, whole, when the result is exactly what <c>EXPLAIN (FORMAT JSON)</c> returns - one <c>json</c> column named
    /// <c>QUERY PLAN</c> - and it is at most <see cref="ExplainStatements.MaxPlanCharacters" /> long: the grid's cell is capped
    /// for a grid, and a plan view needs every character of it (X1).
    /// </summary>
    public string? Plan { get; init; }

    /// <summary>How long a plan was that is longer than <see cref="ExplainStatements.MaxPlanCharacters" />, and so not kept.</summary>
    public long? PlanCharacters { get; init; }
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

    /// <summary>
    /// Where the session is counted for the "studio cost" line - its rollback always, and its wall time when its connection
    /// is not metered already (<see cref="StudioConnectionMeter" />) - or <see langword="null" /> for the process's own
    /// <see cref="StudioDatabaseTime.Shared" />.
    /// </summary>
    public StudioDatabaseTime? Meter { get; init; }
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
/// <para>
/// <b>The statement is described before it runs</b> (1.2-VEC): one extra round trip, Parse and Describe only, inside the same
/// transaction and under the same timeouts, tells which result columns Npgsql has no handler for, and those - pgvector's
/// <c>vector</c>, <c>halfvec</c> and <c>sparsevec</c> first among them - are requested in text form
/// (<see cref="ResultColumnTypes" />). The guard, the transaction and what is audited are exactly what they were.
/// </para>
/// </remarks>
internal sealed class ReadOnlySqlSession
{
    /// <summary>Sets a GUC for the current transaction. The name and the value are both parameters.</summary>
    internal const string SetConfigSql = "select pg_catalog.set_config(@name, @value, true)";

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
    public Task<SqlResultSet> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(connection, sql, bind, null, cancellationToken);

    /// <summary>
    /// The same run, calling <paramref name="beforeExecute" /> just before the statement itself is executed - after the
    /// describe, which can raise the same notices again.
    /// </summary>
    /// <remarks>
    /// A caller that collects the connection's notices clears them there: Postgres raises some at parse time (an over-long
    /// identifier that will be truncated) and raises them again when the statement runs, so without it each is listed twice.
    /// A describe that fails runs nothing after it, so its notices are kept.
    /// </remarks>
    public async Task<SqlResultSet> ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        Action<NpgsqlCommand>? bind,
        Action? beforeExecute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        return await InTransactionAsync(
                connection,
                (transaction, token) => ReadAsync(connection, transaction, sql, bind, beforeExecute, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the read-only transaction on <paramref name="connection" />, runs <paramref name="work" />
    /// inside it, and rolls back whatever happened.
    /// </summary>
    /// <param name="connection">An already-open connection. It is not disposed here.</param>
    /// <param name="work">
    /// What to do inside the transaction. It is handed the transaction so that every command it issues is
    /// enlisted in it - a command created without one runs outside the read-only flag.
    /// </param>
    /// <param name="cancellationToken">The token.</param>
    /// <remarks>
    /// <para>
    /// <b>This is the preamble, and there is only one of it.</b>
    /// <see cref="ExecuteAsync(NpgsqlConnection, string, Action{NpgsqlCommand}?, CancellationToken)" /> is
    /// one caller and the Query page's Mode A reader is the other. Mode A used to run its composed
    /// <c>select</c> as a plain command on a read connection, with no <c>SET TRANSACTION READ ONLY</c>, no
    /// server-side <c>statement_timeout</c> and no <c>SET LOCAL ROLE</c> - which is what let a visitor
    /// holding <c>RunSql</c> reach session-level state from the mode that needs no capability. Two copies
    /// of <c>BEGIN; SET TRANSACTION READ ONLY; …</c> is how that happens again, so there is one.
    /// </para>
    /// <para>
    /// A caller that needs the rows typed rather than formatted reads them itself: a Mode A row is a
    /// document's whole <c>data</c> column, where <see cref="SqlValueFormatter" /> caps a console cell for
    /// a grid. What it must not do is create its own connection or its own transaction.
    /// </para>
    /// </remarks>
    public async Task<T> InTransactionAsync<T>(
        NpgsqlConnection connection,
        Func<NpgsqlTransaction, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(work);

        if (options.Role is not null && !IsValidRoleName(options.Role))
        {
            throw new InvalidOperationException($"'{options.Role}' is not a valid Postgres role name.");
        }

        // The studio's own database time, for the health advisor's "studio cost" line: begin to rollback,
        // whatever happened in between.
        long started = Stopwatch.GetTimestamp();

        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(connection, transaction, ReadOnlySql, cancellationToken).ConfigureAwait(false);

            // Issue #44: the studio's own sessions carry a name of their own in pg_stat_activity - set for this transaction only
            // (set_config's is_local), so it reverts at the rollback below and a pooled connection goes back to the application as
            // it came, whatever the host's No Reset On Close says. Before the role: any role may set it.
            if (StudioConnectionMeter.ApplicationNameOf(connection) is { } applicationName)
            {
                await SetLocalAsync(connection, transaction, "application_name", applicationName, cancellationToken).ConfigureAwait(false);
            }

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

            return await work(transaction, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Always, and with its own token: a rollback that is skipped because the caller cancelled is a
            // transaction left open on a pooled connection.
            bool rolledBack = false;

            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                rolledBack = true;
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

            // A metered connection already times everything on it, open to close (StudioConnectionMeter): the session counts
            // itself and its rollback there - C08 subtracts those exactly - and adds no time of its own, so no statement is
            // timed twice. One the studio did not meter is timed here, begin to rollback, as before.
            StudioDatabaseTime meter = options.Meter ?? StudioDatabaseTime.Shared;
            string key = StudioDatabaseTime.KeyOf(connection);

            if (StudioConnectionMeter.IsMetered(connection))
            {
                meter.RecordSession(key, rolledBack);
            }
            else
            {
                meter.RecordSince(key, started, rolledBack);
            }
        }
    }

    private async Task<SqlResultSet> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand>? bind,
        Action? beforeExecute,
        CancellationToken cancellationToken)
    {
        long vectorCharacters = 0;
        List<SqlResultColumn> columns = [];
        List<IReadOnlyList<SqlCell>> rows = [];
        var truncated = false;
        var stopwatch = Stopwatch.StartNew();
        string? plan = null;
        long? planCharacters = null;

        try
        {
            // One extra round trip, before the statement runs and inside the same transaction: which columns the driver cannot
            // read as a value. An error here is the error the statement would have raised (it is the same Parse), and is
            // answered by the same handlers below.
            //
            // Not on a connection that prepares statements automatically (Max Auto Prepare > 0): Npgsql keys an auto-prepared
            // statement by its SQL text alone and fixes the result formats into it, so a text request made now would be
            // replayed on a host command with the same text, and a later run's request would disagree with the cached formats.
            // There the statement runs exactly as it did before 1.2, and a column Npgsql cannot read says how to see it.
            ResultTextPlan textPlan = AutoPrepares(connection)
                ? ResultTextPlan.None(0)
                : await DescribeAsync(connection, transaction, sql, bind, cancellationToken).ConfigureAwait(false);

            beforeExecute?.Invoke();

            // The duration is the execution's alone, as it always was: the describe is not the statement's time.
            stopwatch.Restart();

            await using var command = NewCommand(connection, transaction, sql, bind);

            if (textPlan.UnknownResultTypes is { } unknown)
            {
                // Requested from Postgres in text form: the driver makes no attempt to parse these, and hands each back as the
                // string Postgres prints. It applies to the first statement only, which is the only result set read.
                command.UnknownResultTypeList = unknown;
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(new SqlResultColumn(reader.GetName(i), reader.GetDataTypeName(i))
                {
                    Vector = i < textPlan.Vectors.Count ? textPlan.Vectors[i] : null,
                });
            }

            // EXPLAIN (FORMAT JSON)'s one json column: kept whole for the plan view (X1), as well as capped for the grid.
            bool planShaped = columns.Count == 1 && ExplainStatements.IsPlanColumn(columns[0].Name, columns[0].PostgresType);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count == options.MaxRows)
                {
                    // One row past the cap only to know whether to say "truncated"; it is not kept.
                    truncated = true;
                    break;
                }

                var row = new SqlCell[reader.FieldCount];

                if (planShaped && rows.Count == 0 && !await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
                {
                    string text = reader.GetString(0);

                    if (text.Length <= ExplainStatements.MaxPlanCharacters)
                    {
                        plan = text;
                    }
                    else
                    {
                        planCharacters = text.Length;
                    }

                    row[0] = formatter.Format(text, columns[0].PostgresType);
                }
                else
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[i] = ReadCell(reader, i, columns[i], ref vectorCharacters);
                    }
                }

                rows.Add(row);
            }

            stopwatch.Stop();
            return new SqlResultSet(columns, rows, truncated, stopwatch.Elapsed, null) { Plan = plan, PlanCharacters = planCharacters };
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

    /// <summary>
    /// A command for the statement, with the timeout and whatever the caller binds. A new one each time: the describe and the
    /// run are two commands, so neither inherits the other's result-type list or prepared state.
    /// </summary>
    private NpgsqlCommand NewCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Action<NpgsqlCommand>? bind)
    {
        var command = new NpgsqlCommand(sql, connection, transaction)
        {
            // A little past the server-side timeout: statement_timeout is what should fire, because it
            // produces 57014 with a message, while a client-side timeout only breaks the connection.
            CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 5,
        };

        bind?.Invoke(command);

        return command;
    }

    /// <summary>
    /// Describes the statement - Parse and Describe, never Bind or Execute: nothing in it runs (<c>CommandBehavior.SchemaOnly</c>,
    /// Npgsql 9.0.4 and 10.0.3) - and decides which result columns arrive as text (<see cref="ResultColumnTypes" />).
    /// </summary>
    /// <remarks>
    /// The second round trip, which asks the catalog who owns the types of the columns the driver cannot read, is made only
    /// when there is such a column or one named like a pgvector type.
    /// </remarks>
    private async Task<ResultTextPlan> DescribeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand>? bind,
        CancellationToken cancellationToken)
    {
        DescribedColumn[] described;

        await using (var command = NewCommand(connection, transaction, sql, bind))
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly, cancellationToken).ConfigureAwait(false))
        {
            described = new DescribedColumn[reader.FieldCount];

            for (var i = 0; i < described.Length; i++)
            {
                described[i] = new DescribedColumn(reader.GetDataTypeOID(i), reader.GetPostgresType(i).Name, IsReadable(reader, i));
            }
        }

        if (described.Length == 0)
        {
            return ResultTextPlan.None(0);
        }

        long[] candidates = ResultColumnTypes.Candidates(described);
        Dictionary<long, string> owned = [];

        if (candidates.Length > 0)
        {
            await using var command = new NpgsqlCommand(ResultColumnTypes.PgVectorTypesSql, connection, transaction)
            {
                CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 5,
            };

            command.Parameters.Add(new NpgsqlParameter("oids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = candidates });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                owned[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        return ResultColumnTypes.Plan(described, owned);
    }

    /// <summary>Whether the driver can read the column as a value: it has a handler for the column's type.</summary>
    private static bool IsReadable(NpgsqlDataReader reader, int ordinal)
    {
        try
        {
            _ = reader.GetFieldType(ordinal);
            return true;
        }
        catch (Exception exception) when (exception is InvalidCastException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the connection's string asks Npgsql to prepare statements automatically (<c>Max Auto Prepare</c> above zero),
    /// read from the string as <see cref="StudioDatabaseTime.KeyOf" /> reads its host.
    /// </summary>
    internal static bool AutoPrepares(NpgsqlConnection connection) =>
        new NpgsqlConnectionStringBuilder(connection.ConnectionString).MaxAutoPrepare > 0;

    private SqlCell ReadCell(NpgsqlDataReader reader, int ordinal, SqlResultColumn column, ref long vectorCharacters)
    {
        try
        {
            object value = reader.GetValue(ordinal);

            // A pgvector value is the text Postgres prints (ResultColumnTypes), kept to a cap of its own: a 1536-dimension
            // embedding is some 15 000 characters, which the 1 KiB a cell gets would cut in the middle of the export. The
            // result as a whole has a budget of them.
            return column.Vector is not null && value is string text
                ? formatter.FormatPgVector(text, ref vectorCharacters)
                : formatter.Format(value, column.PostgresType);
        }
        catch (Exception exception) when (exception is InvalidCastException or NotSupportedException or ArgumentException)
        {
            // A type the driver has no handler for that was not requested as text: only on a connection that prepares
            // statements automatically, where the console asks for no text. ArgumentException is the base of the decoder's
            // fallback exception, which a binary value read as text raises - nothing may escape from here unaudited.
            return new SqlCell(Unreadable(column.PostgresType), SqlCellKind.Text, false, 0);
        }
    }

    /// <summary>What a cell says when its value could not be read, and how to see it.</summary>
    internal static string Unreadable(string postgresType)
    {
        int dot = postgresType.LastIndexOf('.');
        string name = (dot >= 0 ? postgresType[(dot + 1)..] : postgresType).Trim('"');

        return name is "vector" or "halfvec" or "sparsevec"
            ? $"(cannot read {postgresType}) - this connection prepares statements automatically (Max Auto Prepare), so the console cannot ask for text: cast it to ::text in the statement to see it"
            : $"(cannot read {postgresType}) - cast it to ::text in the statement to see it";
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
