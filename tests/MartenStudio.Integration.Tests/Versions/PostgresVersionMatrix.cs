using System.Collections.Concurrent;
using System.Globalization;

using MartenStudio.Integration.Tests.Versions;

using Npgsql;

using Testcontainers.PostgreSql;

// One matrix for the whole run, started lazily per major: a class that never asks for a server costs nothing.
[assembly: AssemblyFixture(typeof(PostgresVersionMatrix))]

namespace MartenStudio.Integration.Tests.Versions;

/// <summary>A login of the matrix servers, as B0's <c>setup.sql</c> made them.</summary>
public enum MatrixRole
{
    /// <summary><c>postgres</c>, the image's superuser: what seeds and inspects, never what the studio reads as.</summary>
    Superuser,

    /// <summary><c>app</c>: logs in and owns schema <c>store</c> - the role a store or an application connects as. Not a superuser.</summary>
    App,

    /// <summary><c>probe</c>: in no monitoring role, with no grant on anything in <c>store</c>.</summary>
    Probe,

    /// <summary><c>stats</c>: a member of <c>pg_read_all_stats</c>.</summary>
    Stats,

    /// <summary><c>mon</c>: a member of <c>pg_monitor</c>.</summary>
    Monitor,
}

/// <summary>One leg of the matrix: a PostgreSQL major and the exact image it runs.</summary>
/// <param name="Major">The major: 14 to 19.</param>
/// <param name="Image">The image, pinned to the minor B0 measured (2026-10-20) - or 19's release candidate.</param>
public sealed record MatrixVersion(int Major, string Image)
{
    /// <summary>
    /// Whether the image is a pre-release (<c>rc</c>, <c>beta</c>): a preview leg runs and reports, and fails CI only
    /// once its tag is a general-availability one.
    /// </summary>
    public bool Preview => Image.Contains("rc", StringComparison.Ordinal) || Image.Contains("beta", StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Major.ToString(CultureInfo.InvariantCulture) + " (" + Image + ")";
}

/// <summary>
/// PostgreSQL 14 to 18 - and 19's release candidate as a preview leg - one server per major, started on first use and
/// shared by every class of the run: the fixture every W5 packet's live tests run their version legs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Using it.</b> Take <see cref="PostgresVersionMatrix" /> in a test class's constructor (it is an assembly fixture,
/// so nothing else is needed), write a <c>[PostgresTheory]</c> over <see cref="Supported" /> (or <see cref="All" />, to
/// include the preview), and start with <c>MatrixServer server = await matrix.ServerAsync(major);</c> followed by
/// <c>server.SkipIfUnavailable();</c>. Then <see cref="MatrixServer.DataSource" /> or <see cref="MatrixServer.OpenAsync" />
/// as the role the test is about, <see cref="MatrixServer.CreateDatabaseAsync" /> for a database of the test's own, and
/// <see cref="MatrixServer.SnapshotAsync" /> for a no-DDL fingerprint. A read that should run on every version is
/// appended to <see cref="MatrixReads" /> instead, and <c>VersionMatrixLiveTests</c> runs it.
/// </para>
/// <para>
/// <b>What each server is.</b> The exact tags B0 ran (<see cref="Versions" />), started with
/// <c>shared_preload_libraries=pg_stat_statements</c> and <c>track_io_timing=on</c>, and seeded in its <c>postgres</c>
/// database with B0's <c>setup.sql</c> (<see cref="SetupSql" />): the roles of <see cref="MatrixRole" />, the
/// <c>pg_stat_statements</c> extension, and <c>store</c>, owned by <c>app</c>, holding a range-partitioned table with a
/// default partition (<c>store.r</c>), a list-partitioned per-tenant table (<c>store.l</c>), a parent and child with a
/// foreign key, a default and a check, a sequence, a view, a function and a trigger.
/// </para>
/// <para>
/// <b>Never reused.</b> Each server is this run's alone: it ignores <c>MARTENSTUDIO_PG_REUSE</c>, is always fresh, and is
/// disposed when the run ends. It is named <c>ms-matrix-&lt;major&gt;-&lt;run&gt;</c> and labelled <c>ms-matrix=&lt;major&gt;</c>
/// and <c>ms-matrix-run=&lt;run&gt;</c> - the run's own suffix because several agent sessions run this suite at once on
/// this machine, and two containers cannot share a name.
/// </para>
/// <para>
/// <b>D19's rule.</b> An image that cannot be pulled or started skips its leg locally, with the <c>docker pull</c> line in
/// the reason (<see cref="MatrixServer.Unavailable" />), and fails the run on CI - except a <see cref="MatrixVersion.Preview" />
/// leg, which skips on CI too until its tag is a released one. No Docker at all is <c>[PostgresTheory]</c>'s skip.
/// </para>
/// <para>
/// <b>What it costs.</b> Five servers (six with the preview), started one after another as the first test of each major
/// reaches it: about 3-6 s each on this machine once the images are local, and a ~110 MB pull per image the first time.
/// </para>
/// </remarks>
public sealed class PostgresVersionMatrix : IAsyncLifetime
{
    /// <summary>Every leg, oldest first. 14.24 to 18.6 are B0's pins; 19 runs its release candidate.</summary>
    public static IReadOnlyList<MatrixVersion> Versions { get; } =
    [
        new(14, "postgres:14.24-alpine"),
        new(15, "postgres:15.19-alpine"),
        new(16, "postgres:16.15-alpine"),
        new(17, "postgres:17.11-alpine"),
        new(18, "postgres:18.6-alpine"),
        new(19, "postgres:19rc1-alpine"),
    ];

    /// <summary>The supported majors, 14 to 18, for a theory.</summary>
    public static TheoryData<int> Supported => [.. Versions.Where(static x => !x.Preview).Select(static x => x.Major)];

    /// <summary>Every major the matrix runs, the preview included, for a theory.</summary>
    public static TheoryData<int> All => [.. Versions.Select(static x => x.Major)];

    /// <summary>The suffix this run's containers carry.</summary>
    public static string RunId { get; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// B0's <c>setup.sql</c> (<c>C:\Users\marko\AppData\Local\Temp\claude\ms05-b0\setup.sql</c>), run as the superuser in
    /// the server's <c>postgres</c> database: verbatim but for the <c>psql</c> meta-command, <c>dblink</c> (which B0's lock
    /// probes needed and nothing here does), the <c>mon</c> role B0 made where needed, and the function, trigger and NOT NULL
    /// columns the database browser's detail reads need to have something to read.
    /// </summary>
    public const string SetupSql =
        """
        create role app login password 'app';
        create role probe login password 'probe';
        create role stats login password 'stats';
        create role mon login password 'mon';
        grant pg_read_all_stats to stats;
        grant pg_monitor to mon;
        create extension if not exists pg_stat_statements;
        create schema store authorization app;

        set role app;
        create table store.r (id int, tenant_id text, ts timestamptz not null) partition by range (ts);
        create table store.r_2026_10 partition of store.r for values from ('2026-10-01') to ('2026-11-01');
        create table store.r_2026_11 partition of store.r for values from ('2026-11-01') to ('2026-12-01');
        create table store.r_default partition of store.r default;
        create index r_id on store.r (id);
        create table store.l (id int, tenant_id text not null, data jsonb, primary key (tenant_id, id)) partition by list (tenant_id);
        create table store.l_a partition of store.l for values in ('a');
        create table store.l_b partition of store.l for values in ('b');
        create table store.parent (id int primary key);
        create table store.child (id int primary key, parent_id int references store.parent (id), n int not null default 42 check (n > 0), label text not null default 'x');
        create sequence store.s as int;
        create view store.v as select id from store.child;
        create function store.touch() returns trigger language plpgsql as $$ begin return new; end $$;
        create trigger child_touch before update on store.child for each row execute function store.touch();
        insert into store.parent select g from pg_catalog.generate_series(1, 100) g;
        insert into store.child select g, 1 + g % 100, 1 + g % 7, 'row ' || g from pg_catalog.generate_series(1, 1000) g;
        insert into store.r select g, 'a', '2026-10-15'::timestamptz + (g || ' minutes')::interval from pg_catalog.generate_series(1, 200) g;
        insert into store.l select g, case when g % 2 = 0 then 'a' else 'b' end, '{}' from pg_catalog.generate_series(1, 200) g;
        select pg_catalog.nextval('store.s');
        analyze store.parent;
        analyze store.child;
        reset role;
        """;

    private readonly ConcurrentDictionary<int, Lazy<Task<MatrixServer>>> servers = new();

    /// <summary>The server of <paramref name="major" />, started and seeded the first time any class asks for it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The matrix has no such major.</exception>
    public Task<MatrixServer> ServerAsync(int major)
    {
        MatrixVersion version = Versions.FirstOrDefault(x => x.Major == major)
            ?? throw new ArgumentOutOfRangeException(nameof(major), major, "The version matrix runs 14 to 19.");

        return servers.GetOrAdd(major, _ => new Lazy<Task<MatrixServer>>(() => MatrixServer.StartAsync(version), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (Lazy<Task<MatrixServer>> server in servers.Values.Where(static x => x.IsValueCreated))
        {
            try
            {
                await (await server.Value).DisposeAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A server that never started has nothing to dispose; Testcontainers' reaper removes anything left.
            }
        }
    }
}

/// <summary>One running server of the matrix, or the reason its leg cannot run here.</summary>
public sealed class MatrixServer : IAsyncDisposable
{
    private readonly PostgreSqlContainer? container;
    private readonly ConcurrentDictionary<(MatrixRole, string), NpgsqlDataSource> dataSources = new();

    private MatrixServer(MatrixVersion version, PostgreSqlContainer? container, string? unavailable, int versionNumber, string serverVersion)
    {
        Version = version;
        this.container = container;
        Unavailable = unavailable;
        VersionNumber = versionNumber;
        ServerVersion = serverVersion;
    }

    /// <summary>The leg.</summary>
    public MatrixVersion Version { get; }

    /// <summary>Why the leg cannot run here, or <see langword="null" /> when the server is up.</summary>
    public string? Unavailable { get; }

    /// <summary><c>server_version_num</c>, as the server reported it: <c>180006</c>.</summary>
    public int VersionNumber { get; }

    /// <summary><c>server_version</c>: <c>18.6</c>, <c>19rc1</c>.</summary>
    public string ServerVersion { get; }

    /// <summary>The database the fixtures are seeded in.</summary>
    public const string Database = "postgres";

    /// <summary>Skips the current test when the leg cannot run here.</summary>
    public void SkipIfUnavailable()
    {
        if (Unavailable is { } why)
        {
            Assert.Skip(why);
        }
    }

    /// <summary>A connection string as <paramref name="role" />, to <paramref name="database" />.</summary>
    public string ConnectionString(MatrixRole role, string database = Database)
    {
        NpgsqlConnectionStringBuilder builder = new((container ?? throw new InvalidOperationException(Unavailable)).GetConnectionString())
        {
            Database = database,
            Pooling = true,
            ApplicationName = "ms-matrix-" + role.ToString().ToLowerInvariant(),
        };

        (builder.Username, builder.Password) = role switch
        {
            MatrixRole.Superuser => (builder.Username, builder.Password),
            MatrixRole.App => ("app", "app"),
            MatrixRole.Probe => ("probe", "probe"),
            MatrixRole.Stats => ("stats", "stats"),
            MatrixRole.Monitor => ("mon", "mon"),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };

        return builder.ConnectionString;
    }

    /// <summary>The data source of <paramref name="role" /> to <paramref name="database" />, one per pair, disposed with the server.</summary>
    public NpgsqlDataSource DataSource(MatrixRole role, string database = Database) =>
        dataSources.GetOrAdd((role, database), key => NpgsqlDataSource.Create(ConnectionString(key.Item1, key.Item2)));

    /// <summary>An open connection as <paramref name="role" />.</summary>
    public async Task<NpgsqlConnection> OpenAsync(MatrixRole role = MatrixRole.Superuser, string database = Database, CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = new(ConnectionString(role, database));

        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs <paramref name="sql" /> as the superuser in <paramref name="database" />.</summary>
    public async Task ExecuteAsync(string sql, string database = Database, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = await OpenAsync(MatrixRole.Superuser, database, cancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// A database of the caller's own, owned by <c>app</c>, dropped first if a test of this run made it before - so a
    /// test that changes the database (an Aurora shim) never shares it with one that reads the seeded <c>postgres</c>.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        string quoted = PostgresStudio.Internal.Sql.SqlIdentifier.Quote(name);

        await ExecuteAsync("drop database if exists " + quoted + " with (force)", Database, cancellationToken);
        await ExecuteAsync("create database " + quoted + " owner app", Database, cancellationToken);
        return name;
    }

    /// <summary>
    /// Every object in every schema of <paramref name="database" /> but Postgres' own, every extension and every role, by
    /// name and kind - the no-DDL fingerprint (<c>HealthSweepProbe.SnapshotAsync</c>'s, over the whole database, which is
    /// this run's alone).
    /// </summary>
    public async Task<IReadOnlySet<string>> SnapshotAsync(string database = Database, CancellationToken cancellationToken = default)
    {
        const string Sql =
            "with s as (select n.oid, n.nspname from pg_catalog.pg_namespace n where n.nspname not in ('pg_catalog', 'information_schema', 'pg_toast') and n.nspname not like 'pg\\_temp\\_%' and n.nspname not like 'pg\\_toast\\_temp\\_%') "
            + "select 'schema:' || s.nspname from s "
            + "union all select 'relation:' || s.nspname || '.' || c.relname || ':' || c.relkind::text from pg_catalog.pg_class c join s on s.oid = c.relnamespace "
            + "union all select 'routine:' || s.nspname || '.' || p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' from pg_catalog.pg_proc p join s on s.oid = p.pronamespace "
            + "union all select 'type:' || s.nspname || '.' || t.typname from pg_catalog.pg_type t join s on s.oid = t.typnamespace "
            + "union all select 'trigger:' || s.nspname || '.' || c.relname || '.' || g.tgname from pg_catalog.pg_trigger g join pg_catalog.pg_class c on c.oid = g.tgrelid join s on s.oid = c.relnamespace "
            + "union all select 'constraint:' || s.nspname || '.' || o.conname from pg_catalog.pg_constraint o join s on s.oid = o.connamespace "
            + "union all select 'extension:' || e.extname || ':' || e.extversion from pg_catalog.pg_extension e "
            + "union all select 'role:' || r.rolname from pg_catalog.pg_roles r";

        await using NpgsqlConnection connection = await OpenAsync(MatrixRole.Superuser, database, cancellationToken);
        await using NpgsqlCommand command = new(Sql, connection);

        HashSet<string> objects = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (NpgsqlDataSource dataSource in dataSources.Values)
        {
            await dataSource.DisposeAsync();
        }

        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>Starts and seeds one leg's server, or says why it cannot.</summary>
    internal static async Task<MatrixServer> StartAsync(MatrixVersion version)
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return new MatrixServer(version, null, DockerAvailability.SkipReason, 0, string.Empty);
        }

        string major = version.Major.ToString(CultureInfo.InvariantCulture);
        PostgreSqlContainer container = new PostgreSqlBuilder(version.Image)
            .WithReuse(false)
            .WithName("ms-matrix-" + major + "-" + PostgresVersionMatrix.RunId)
            .WithLabel("ms-matrix", major)
            .WithLabel("ms-matrix-run", PostgresVersionMatrix.RunId)
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
            .WithCommand("-c", "track_io_timing=on")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration || version.Preview)
        {
            await container.DisposeAsync();

            return new MatrixServer(
                version,
                null,
                "PostgreSQL " + major + "'s leg could not start " + version.Image + " here (" + exception.GetType().Name + ": " + exception.Message
                + "); `docker pull " + version.Image + "` makes it run."
                + (version.Preview ? " It is a preview leg, which never fails a run." : " On CI this is a failure, not a skip."),
                0,
                string.Empty);
        }

        MatrixServer server = new(version, container, null, 0, string.Empty);

        await server.ExecuteAsync(PostgresVersionMatrix.SetupSql);

        await using NpgsqlConnection connection = await server.OpenAsync();
        await using NpgsqlCommand command = new("select pg_catalog.current_setting('server_version_num')::int4, pg_catalog.current_setting('server_version')", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return new MatrixServer(version, container, null, reader.GetInt32(0), reader.GetString(1));
    }
}
