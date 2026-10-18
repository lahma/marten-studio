using System.Diagnostics;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A document the statement checks' live tests make expensive.</summary>
public sealed class StatementTarget
{
    public Guid Id { get; set; }

    public int Number { get; set; }

    public string Color { get; set; } = string.Empty;
}

/// <summary>A document the statement checks' live tests hide with <c>IsDocumentTypeVisible</c>.</summary>
public sealed class StatementHidden
{
    public Guid Id { get; set; }

    public string Secret { get; set; } = string.Empty;
}

/// <summary>
/// A Postgres server of the statement checks' own, with <c>pg_stat_statements</c> in <c>shared_preload_libraries</c> -
/// which the shared server does not have, and which only a restart changes. Never reused: its settings are this
/// fixture's alone. Every test takes a database of its own on it, and every read is bounded to the database it is
/// about (<c>dbid</c>), so tests that run on it do not read each other's statements.
/// </summary>
public sealed class StatementsServerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
        .WithCommand("-c", "pg_stat_statements.track=top")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The statements server is not running; a test that needs it must be a [PostgresFact].");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

/// <summary>One lock a health statement's session held while its transaction was still open.</summary>
/// <param name="LockType">pg_locks.locktype.</param>
/// <param name="Mode">pg_locks.mode.</param>
/// <param name="Schema">The relation's schema.</param>
/// <param name="Relation">The relation's name.</param>
/// <param name="Statement">What "Show SQL" says had just run.</param>
internal sealed record StatementLock(string LockType, string Mode, string? Schema, string? Relation, string Statement)
{
    /// <summary>A catalog read, or the transaction's lock on its own virtual id.</summary>
    public bool IsCatalogOrOwn =>
        (LockType == "relation" && Mode == "AccessShareLock" && Schema is "pg_catalog" or "pg_toast" or "information_schema")
        || (LockType == "virtualxid" && Mode == "ExclusiveLock");

    public override string ToString() => $"{Mode} on {LockType} {Schema}.{Relation} after: {Statement}";
}

/// <summary>
/// <c>HealthSweepProbe</c>'s no-lock half for a server other than the shared one: told about every health statement
/// while its transaction is open, it reads that backend's granted locks from a second connection.
/// </summary>
internal sealed class StatementLocksProbe(string connectionString) : IHealthStatementObserver
{
    private const string LocksSql =
        "select l.locktype, l.mode, n.nspname, c.relname from pg_catalog.pg_locks l "
        + "left join pg_catalog.pg_class c on c.oid = l.relation left join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
        + "where l.pid = @pid and l.granted";

    private readonly List<StatementLock> locks = [];
    private readonly List<string> statements = [];
    private readonly Lock gate = new();
    private volatile bool recording;

    /// <summary>
    /// Whether statements are recorded - off until a test turns it on, so that the regular sweep a report also runs is
    /// left out of what the statement sweep is judged by.
    /// </summary>
    public bool Recording
    {
        get => recording;
        set => recording = value;
    }

    /// <summary>Every statement observed, as "Show SQL" records it, in order.</summary>
    public IReadOnlyList<string> Statements
    {
        get
        {
            lock (gate)
            {
                return [.. statements];
            }
        }
    }

    /// <summary>Every lock beyond a catalog read and the transaction's own.</summary>
    public IReadOnlyList<StatementLock> BeyondCatalogReads
    {
        get
        {
            lock (gate)
            {
                return [.. locks.Where(static x => !x.IsCatalogOrOwn)];
            }
        }
    }

    public async Task ObserveAsync(int backendProcessId, HealthStatement statement, CancellationToken cancellationToken)
    {
        if (!recording)
        {
            return;
        }

        await using NpgsqlConnection second = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await second.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(LocksSql, second);
        command.Parameters.AddWithValue("pid", backendProcessId);

        List<StatementLock> held = [];

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                held.Add(new StatementLock(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    statement.Sql));
            }
        }

        lock (gate)
        {
            statements.Add(statement.Sql);
            locks.AddRange(held);
        }
    }
}

/// <summary>What the statement checks' live tests share.</summary>
internal static class StatementsLive
{
    /// <summary>The login role a narrowed-console test's store connects as.</summary>
    public const string AppRole = "h7_app";

    /// <summary>Its password.</summary>
    public const string AppPassword = "h7-app-password";

    /// <summary>A console role that is neither a member of pg_read_all_stats nor of the store's role.</summary>
    public const string ConsoleRole = "h7_console";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A database of the test's own, with the extension created in it unless <paramref name="extension" /> says not.</summary>
    public static async Task<string> CreateDatabaseAsync(string adminConnectionString, string name, bool extension = true, string? owner = null)
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(adminConnectionString, name);

        if (owner is not null)
        {
            await ExecuteAsync(adminConnectionString, "alter database " + SqlIdentifier.Quote(name) + " owner to " + SqlIdentifier.Quote(owner));
        }

        if (extension)
        {
            await ExecuteAsync(database, "create extension if not exists pg_stat_statements");
        }

        return database;
    }

    /// <summary>The store's login role and a narrowed console role it may switch to - created once per server.</summary>
    public static async Task EnsureRolesAsync(string adminConnectionString) =>
        await ExecuteAsync(
            adminConnectionString,
            "do $$ begin "
            + "if not exists (select 1 from pg_roles where rolname = '" + AppRole + "') then create role " + AppRole + " login password '" + AppPassword + "'; end if; "
            + "if not exists (select 1 from pg_roles where rolname = '" + ConsoleRole + "') then create role " + ConsoleRole + " nologin; end if; "
            + "end $$; grant " + ConsoleRole + " to " + AppRole);

    /// <summary><paramref name="connectionString" /> as another login.</summary>
    public static string As(string connectionString, string user, string password) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = user, Password = password }.ConnectionString;

    /// <summary>Runs <paramref name="work" /> over and over until <paramref name="duration" /> of wall-clock time has passed.</summary>
    public static async Task<int> RepeatAsync(TimeSpan duration, Func<Task> work)
    {
        Stopwatch clock = Stopwatch.StartNew();
        int runs = 0;

        do
        {
            await work();
            runs++;
        }
        while (clock.Elapsed < duration);

        return runs;
    }

    /// <summary>The report with the statement checks run now, through a circuit-shaped scope of its own.</summary>
    public static async Task<HealthReport> StatementsAsync(ConnectionsLiveHost host)
    {
        await using AsyncServiceScope circuit = host.Circuit();

        return await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(host.Scope, new HealthReportRequest(Statements: HealthDeeperChecks.Run), Token);
    }

    /// <summary>One statement on a connection of the test's own.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Every object in <paramref name="schemas" />, by name and kind - the no-DDL half.</summary>
    public static async Task<IReadOnlySet<string>> SnapshotAsync(string connectionString, params string[] schemas)
    {
        const string Sql =
            "select 'relation:' || n.nspname || '.' || c.relname || ':' || c.relkind::text from pg_catalog.pg_class c "
            + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@schemas) "
            + "union all select 'routine:' || n.nspname || '.' || p.proname from pg_catalog.pg_proc p "
            + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@schemas) "
            + "union all select 'constraint:' || n.nspname || '.' || o.conname from pg_catalog.pg_constraint o "
            + "join pg_catalog.pg_namespace n on n.oid = o.connamespace where n.nspname = any(@schemas)";

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(Sql, connection);
        command.Parameters.AddWithValue("schemas", schemas);

        HashSet<string> objects = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }
}
