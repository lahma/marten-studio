using Marten;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;

using Npgsql;

namespace MartenStudio.Services.Health;

/// <summary>
/// What a check's gatherer is handed: the database being swept, the host's configuration, and the only way
/// to query it.
/// </summary>
/// <remarks>
/// <para>
/// <b>About the database, never about a visitor.</b> A sweep's result is shared by every circuit that
/// watches the database (D10), so nothing here knows who is looking: <see cref="Services" /> is a scope the
/// sweep created for itself from the root container, not a circuit's, and the scope is resolved for the
/// database as a whole (<c>TenantId = null</c>). Who is shown what is decided afterwards, per visitor
/// (<see cref="HealthGate" />). A gatherer that asked for <c>StudioAuthorization</c> or the circuit's state
/// would be answering one visitor's question into everybody's cache.
/// </para>
/// <para>
/// <b>SQL only through <see cref="Sql" />.</b> Its statements come from the builders under
/// <c>Internal/Sql/</c>, and it runs each in a read-only transaction under the studio's
/// <c>statement_timeout</c> and <c>lock_timeout</c>, as <c>SqlConsoleRole</c> when one is set - the role the
/// catalog reads use. Opening a connection, a Marten session or a Weasel migration of any kind is not
/// something a gatherer does; nor is anything hard rule 14 names.
/// </para>
/// <para>
/// <b>A read several checks need is read once per sweep</b>, through <see cref="Shared{T}" />.
/// </para>
/// </remarks>
internal sealed class HealthCheckContext
{
    private readonly Func<Type, bool>? isDocumentTypeVisible;
    private readonly HealthSweepMemo memo;
    private readonly CancellationToken sweepToken;

    internal HealthCheckContext(
        ResolvedScope scope,
        MartenStudioOptions studioOptions,
        MartenRuntime runtime,
        IServiceProvider services,
        HealthSqlRunner sql,
        DateTimeOffset now,
        HealthSweepMemo memo,
        CancellationToken sweepToken)
    {
        Scope = scope;
        StudioOptions = studioOptions;
        Runtime = runtime;
        Services = services;
        Sql = sql;
        Now = now;
        this.memo = memo;
        this.sweepToken = sweepToken;
        isDocumentTypeVisible = studioOptions.IsDocumentTypeVisible;
    }

    /// <summary>The store and database being swept, resolved with no tenant.</summary>
    public ResolvedScope Scope { get; }

    /// <summary>The store.</summary>
    public IDocumentStore Store => Scope.Store;

    /// <summary>What the host told Marten.</summary>
    public IReadOnlyStoreOptions StoreOptions => Scope.Store.Options;

    /// <summary>The database.</summary>
    public IMartenDatabase Database => Scope.Database;

    /// <summary>The store's registration key.</summary>
    public string StoreKey => Scope.Registration.Key;

    /// <summary>The database's identity.</summary>
    public string DatabaseId => Scope.Database.Id.Identity;

    /// <summary>The studio's own options.</summary>
    public MartenStudioOptions StudioOptions { get; }

    /// <summary>The Marten this host runs, and its light-up probes.</summary>
    public MartenRuntime Runtime { get; }

    /// <summary>
    /// A DI scope of the sweep's own, from the root container - for a host service such as
    /// <c>IHostEnvironment</c>, which may be absent (a value, not an error).
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>The only way to query the database.</summary>
    public HealthSqlRunner Sql { get; }

    /// <summary>When the sweep began, by the studio's clock.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>
    /// Whether <c>MartenStudioOptions.IsDocumentTypeVisible</c> lets the studio show
    /// <paramref name="documentType" />. A gatherer leaves a hidden type out of what it reads where it can;
    /// the sweep drops any finding that names one either way.
    /// </summary>
    public bool IsDocumentTypeVisible(Type documentType) => isDocumentTypeVisible?.Invoke(documentType) ?? true;

    /// <summary>
    /// A read that several checks of one sweep need, run once for the sweep and handed to every check that
    /// asks for it by the same <paramref name="key" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Single flight, and never past the sweep.</b> The first check to ask runs <paramref name="read" />;
    /// every later one - in this sweep, of this database - gets the same task, finished or not, and so the
    /// same answer or the same exception. The memo belongs to the sweep and is dropped with it, so the next
    /// sweep reads afresh: it is a way to read once per sweep, not a cache.
    /// </para>
    /// <para>
    /// <b>Its SQL is every user's.</b> <paramref name="read" /> queries through <see cref="Sql" />, and the
    /// statements it runs are added to the "Show SQL" of every check that asks for the key, not only the one
    /// that happened to ask first.
    /// </para>
    /// <para>
    /// The read is handed the sweep's own budget rather than the asking check's, because its answer is not
    /// that check's alone. A key names one shape of answer: asking for it with another type is a bug, and
    /// throws.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">What the read answers.</typeparam>
    /// <param name="key">What the read is, unique within a sweep - <c>"tables.stats"</c>.</param>
    /// <param name="read">The read. Every query in it goes through <see cref="Sql" />.</param>
    public Task<T> Shared<T>(string key, Func<CancellationToken, Task<T>> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(read);

        return memo.GetAsync(key, Sql, read, sweepToken);
    }
}

/// <summary>What a check's evaluator is handed besides its facts: everything it may use, as data.</summary>
/// <param name="Now">When the facts were read. An evaluator never asks the clock.</param>
/// <param name="Runtime">The Marten the host runs, for a remedy that needs a newer one.</param>
/// <param name="History">
/// The database's samples from regular sweeps, oldest first, this sweep's included when it is a regular one.
/// </param>
internal sealed record HealthEvaluation(DateTimeOffset Now, MartenRuntime Runtime, HealthHistoryView History)
{
    /// <summary>An evaluation at <paramref name="now" />, for a unit test.</summary>
    public static HealthEvaluation At(DateTimeOffset now, MartenRuntime? runtime = null, HealthHistoryView? history = null) =>
        new(now, runtime ?? MartenRuntime.Current, history ?? HealthHistoryView.Empty);
}

/// <summary>
/// A test seam, and only that: told about every health statement while its transaction is still open.
/// </summary>
/// <remarks>
/// The live no-lock helper registers one to look at <c>pg_locks</c> from a second connection while the
/// sweep's session still holds whatever it took. Nothing in the studio registers one, so in a host it is
/// absent and costs a null check per statement.
/// </remarks>
internal interface IHealthStatementObserver
{
    /// <summary>Called after a statement has been read to the end, before its transaction rolls back.</summary>
    /// <param name="backendProcessId">The Postgres backend the statement ran on.</param>
    /// <param name="statement">What ran.</param>
    /// <param name="cancellationToken">The check's budget.</param>
    Task ObserveAsync(int backendProcessId, HealthStatement statement, CancellationToken cancellationToken);
}

/// <summary>One sweep's shared reads, by key: <see cref="HealthCheckContext.Shared{T}" />.</summary>
internal sealed class HealthSweepMemo
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    /// <summary>How many shared reads ran, for a test and for nothing else.</summary>
    internal int Reads { get; private set; }

    /// <summary>The shared read for <paramref name="key" />: started by the first caller, awaited by every one.</summary>
    public async Task<T> GetAsync<T>(string key, HealthSqlRunner caller, Func<CancellationToken, Task<T>> read, CancellationToken sweepToken)
    {
        Entry entry;
        bool first = false;

        lock (gate)
        {
            if (!entries.TryGetValue(key, out Entry? existing))
            {
                // Created under the lock, run outside it: a second caller that arrives while the first is still
                // reading joins the same task rather than finding none.
                Entry created = new(typeof(T));
                created.Work = new Lazy<Task<object?>>(
                    () => RunAsync(created, caller, read, sweepToken),
                    LazyThreadSafetyMode.ExecutionAndPublication);

                existing = created;
                entries[key] = existing;
                first = true;
                Reads++;
            }
            else if (existing.Type != typeof(T))
            {
                throw new InvalidOperationException(
                    $"The shared health read '{key}' answers {existing.Type.Name}, and was asked for {typeof(T).Name}.");
            }

            entry = existing;
        }

        try
        {
            return (T) (await entry.Work!.Value.ConfigureAwait(false))!;
        }
        finally
        {
            if (!first)
            {
                caller.Attribute(entry.Statements);
            }
        }
    }

    private static async Task<object?> RunAsync<T>(Entry entry, HealthSqlRunner caller, Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        int before = caller.Statements.Count;

        try
        {
            return await read(token).ConfigureAwait(false);
        }
        finally
        {
            entry.Statements = [.. caller.Statements.Skip(before)];
        }
    }

    /// <summary>One key's read: its type, its task and the statements it ran.</summary>
    private sealed class Entry(Type type)
    {
        public Type Type { get; } = type;

        public Lazy<Task<object?>>? Work { get; set; }

        public IReadOnlyList<HealthStatement> Statements { get; set; } = [];
    }
}

/// <summary>
/// Runs a check's statements: one read-only transaction each, on the sweep's one connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>One connection per sweep, opened only if a check needs it.</b> A sweep of configuration checks opens
/// none. The connection is the sweep's, not the check's, so ten catalog checks cost one connection and not
/// ten; and each statement is its own transaction, so one that fails - a timeout, a privilege - rolls back
/// alone and the next check runs on a clean session.
/// </para>
/// <para>
/// <b>Recorded.</b> Every statement is kept, text and parameter names, for the finding's "Show SQL" - and is
/// kept even when it fails, which is when somebody most wants to see it.
/// </para>
/// <para>
/// <b>Parameters are cloned.</b> An <see cref="NpgsqlParameter" /> belongs to one command's collection, so a
/// builder that hands out the same <see cref="HealthQuery" /> twice - a cached one, a static one - would
/// otherwise fail on its second use with "already contained by another collection". Each command gets
/// copies, and the query's own parameters are never attached to anything.
/// </para>
/// </remarks>
internal sealed class HealthSqlRunner
{
    /// <summary>The most rows one statement may return before the check fails, rather than the page.</summary>
    public const int MaxRows = 10_000;

    private readonly HealthConnection connection;
    private readonly ReadOnlySqlSession session;
    private readonly int commandTimeoutSeconds;
    private readonly IHealthStatementObserver? observer;
    private readonly List<HealthStatement> statements = [];

    internal HealthSqlRunner(
        HealthConnection connection,
        ReadOnlySqlOptions sessionOptions,
        IHealthStatementObserver? observer)
    {
        this.connection = connection;
        session = new ReadOnlySqlSession(sessionOptions);
        this.observer = observer;

        // A little past the server-side statement_timeout, which is what should fire: it produces 57014 with
        // a message, where a client-side timeout only breaks the connection.
        commandTimeoutSeconds = (int) Math.Ceiling(sessionOptions.StatementTimeout.TotalSeconds) + 5;
    }

    /// <summary>Every statement this check has run, or shares through a memo, in order.</summary>
    public IReadOnlyList<HealthStatement> Statements => statements;

    /// <summary>Runs <paramref name="query" /> and maps every row.</summary>
    /// <exception cref="HealthSweepRuleException">It returned more than <see cref="MaxRows" /> rows.</exception>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        HealthQuery query,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(map);

        HealthStatement statement = new(query.Sql, query.ParameterNames);
        statements.Add(statement);

        NpgsqlConnection open = await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await session.InTransactionAsync(
                    open,
                    async (transaction, token) =>
                    {
                        await DatabaseCatalogQueries.PinSearchPathAsync(open, transaction, commandTimeoutSeconds, token)
                            .ConfigureAwait(false);

                        List<T> rows = [];

                        await using (NpgsqlCommand command = new(query.Sql, open, transaction) { CommandTimeout = commandTimeoutSeconds })
                        {
                            foreach (NpgsqlParameter parameter in query.Parameters)
                            {
                                command.Parameters.Add(parameter.Clone());
                            }

                            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                            while (await reader.ReadAsync(token).ConfigureAwait(false))
                            {
                                if (rows.Count == MaxRows)
                                {
                                    string bounded = $"A health statement returned more than {MaxRows:N0} rows; a check reads a bounded answer.";
                                    throw new HealthSweepRuleException(bounded, bounded);
                                }

                                rows.Add(map(reader));
                            }
                        }

                        if (observer is not null)
                        {
                            await observer.ObserveAsync(open.ProcessID, statement, token).ConfigureAwait(false);
                        }

                        return (IReadOnlyList<T>) rows;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (open.State is not System.Data.ConnectionState.Open)
        {
            // The connection broke under the statement. The next check gets a fresh one.
            await connection.ResetAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs <paramref name="query" /> and reads the first column of its first row.</summary>
    public async Task<T?> ScalarAsync<T>(HealthQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<T?> rows = await QueryAsync(
                query,
                static reader => reader.IsDBNull(0) ? default : reader.GetFieldValue<T>(0),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? default : rows[0];
    }

    /// <summary>Adds statements a shared read ran for somebody else to this check's "Show SQL".</summary>
    internal void Attribute(IEnumerable<HealthStatement> shared)
    {
        foreach (HealthStatement statement in shared)
        {
            if (!statements.Contains(statement))
            {
                statements.Add(statement);
            }
        }
    }
}

/// <summary>The sweep's one connection, opened on first use and disposed with the sweep.</summary>
/// <remarks>
/// <para>
/// <b>The primary.</b> It is opened for <c>ConnectionUsage.ReadWrite</c>, which on a multi-host data source
/// targets the host Marten writes to, even though every statement on it runs inside a read-only transaction.
/// A health read describes the database the application writes to - its sessions, its locks, its counters -
/// and a standby's would be a different database's numbers under this one's name.
/// </para>
/// <para>
/// A database that refused the connection once is not asked again within the same sweep: every later check
/// gets the same failure straight away, rather than each waiting out its own connect timeout against a
/// server that is down.
/// </para>
/// </remarks>
internal sealed class HealthConnection : IAsyncDisposable
{
    private readonly IMartenDatabase database;
    private NpgsqlConnection? connection;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? openFailure;

    public HealthConnection(IMartenDatabase database) => this.database = database;

    /// <summary>Whether a check has needed it yet, for a test and for nothing else.</summary>
    internal bool WasOpened { get; private set; }

    /// <summary>The open connection, opening it if this is the first statement of the sweep.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (connection is { State: System.Data.ConnectionState.Open } open)
        {
            return open;
        }

        openFailure?.Throw();

        await ResetAsync().ConfigureAwait(false);

        NpgsqlConnection fresh = database.CreateConnection(ConnectionUsage.ReadWrite);

        try
        {
            await PostgresFailure.OpenAsync(fresh, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await fresh.DisposeAsync().ConfigureAwait(false);

            if (PostgresFailure.IsConnectionFailure(exception))
            {
                openFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
            }

            throw;
        }

        connection = fresh;
        WasOpened = true;
        return fresh;
    }

    /// <summary>Drops the connection, so the next statement opens another.</summary>
    public async Task ResetAsync()
    {
        NpgsqlConnection? old = connection;
        connection = null;

        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);
}
