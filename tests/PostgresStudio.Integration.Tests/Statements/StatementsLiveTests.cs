using System.Globalization;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Statements;

using Testcontainers.PostgreSql;

namespace PostgresStudio.Integration.Tests.Statements;

/// <summary>
/// A Postgres server of the Statements page's own, with <c>pg_stat_statements</c> in <c>shared_preload_libraries</c> - which
/// the shared server does not have, and which only a restart changes. Never reused: its settings are this fixture's alone.
/// Every test takes a database of its own on it, and every read is bounded to the database it is about (<c>dbid</c>).
/// </summary>
public sealed class StatementsPageServer : IAsyncLifetime
{
    /// <summary>The role a test's database belongs to, and the studio's data source logs in as.</summary>
    public const string AppRole = "q1_app";

    /// <summary>Another role, whose statements the page must never show.</summary>
    public const string OtherRole = "q1_other";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
        .WithCommand("-c", "pg_stat_statements.track=top")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>A connection string to <paramref name="database" /> as <paramref name="role" />, or as the superuser.</summary>
    public string ConnectionString(string database, string? role = null)
    {
        if (!started)
        {
            throw new InvalidOperationException("The statements server is not running; a test that needs it must be a [PostgresFact].");
        }

        NpgsqlConnectionStringBuilder builder = new(container.GetConnectionString()) { Database = database, Pooling = false };

        if (role is not null)
        {
            (builder.Username, builder.Password) = (role, role);
        }

        return builder.ConnectionString;
    }

    /// <summary>A fresh database owned by <see cref="AppRole" />, with the extension created in it.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        string quoted = SqlIdentifier.Quote(name);
        string admin = ConnectionString("postgres");

        await StatementsLive.ExecuteAsync(admin, "drop database if exists " + quoted + " with (force)");
        await StatementsLive.ExecuteAsync(admin, "create database " + quoted + " owner " + AppRole);
        await StatementsLive.ExecuteAsync(ConnectionString(name), "create extension if not exists pg_stat_statements");

        return name;
    }

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;

        await StatementsLive.ExecuteAsync(
            ConnectionString("postgres"),
            "create role " + AppRole + " login password '" + AppRole + "'; create role " + OtherRole + " login password '" + OtherRole + "'");
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

/// <summary>What the Statements page's live tests share.</summary>
internal static class StatementsLive
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The scope of a host's one data source, as a whole.</summary>
    public static readonly StudioScope Scope = new("default", "default", null);

    /// <summary>One statement - or a batch - on a connection of the test's own.</summary>
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

    /// <summary>Runs <paramref name="sql" /> <paramref name="times" /> times on one connection, one parameter <c>@id</c> each time.</summary>
    public static async Task RepeatAsync(string connectionString, string sql, int times)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        for (int i = 0; i < times; i++)
        {
            await using NpgsqlCommand command = new(sql, connection);
            command.Parameters.AddWithValue("id", (i % 5) + 1);
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    /// <summary>The application's own table, owned by the application's role.</summary>
    public static Task SeedAsync(string connectionString) =>
        ExecuteAsync(connectionString, "create table public.orders (id int primary key, note text); insert into public.orders select g, 'n' || g from pg_catalog.generate_series(1, 50) g");

    /// <summary>A Postgres Studio host over <paramref name="connectionString" />, with the activity gate open.</summary>
    public static StudioHost Host(string connectionString, IHealthStatementObserver? observer = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

                if (observer is not null)
                {
                    services.AddSingleton(observer);
                }
            },
            static options => options.Capabilities = PostgresStudioCapabilities.All());

    /// <summary>The page's view for the host's one database, through a circuit of its own.</summary>
    public static async Task<StatementsView> ReadAsync(StudioHost host)
    {
        await using StudioCircuit circuit = host.Circuit();
        return await circuit.Get<IStatementsService>().GetAsync(Scope, Token);
    }

    /// <summary>The view's rows, as the page's default table draws them.</summary>
    public static IReadOnlyList<StatementRowView> Top(StatementsView view, StatementRanking ranking = StatementRanking.TotalTime) =>
        view.Top(StatementReads.PageTop, ranking, ranking == StatementRanking.StorageReads ? StatementSort.StorageReads : StatementSort.TotalTime);

    /// <summary>Every row, for a failure's message.</summary>
    public static string Describe(StatementsView view)
    {
        StringBuilder text = new();
        text.AppendLine(view.State + ": " + view.Status);

        foreach (StatementRowView row in view.Rows)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {row.Key} [{row.GroupLabel}] {row.Window?.Calls} calls {row.Window?.ExecMs:0.0} ms storage {row.Window?.StorageBlocksRead}: {row.Text ?? row.TextNote}");
        }

        return text.ToString();
    }
}

/// <summary>
/// The Statements page against a real <c>pg_stat_statements</c> (W5-Q1, issue #8 G6, A5): on a server of its own with the
/// library preloaded, a known statement ranks first with the right calls, a literal-bearing utility text is withheld, a
/// comment is masked, another role's statement is absent, and the read locks nothing beyond the extension's own view and
/// creates nothing; on PostgreSQL 14 and 18 through the version matrix, the same ranking; and on the Aurora shim, in the 15/16
/// shape and in 17's colliding one, "rank by storage reads" orders by the shim's column.
/// </summary>
public class StatementsLiveTests(StatementsPageServer server, PostgresVersionMatrix matrix) : IClassFixture<StatementsPageServer>
{
    /// <summary>A statement that is slow on purpose, so it ranks first by total time: 25 calls of 20 ms.</summary>
    private const string Slow = "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where o.id = @id";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_known_statement_ranks_first_with_its_calls_and_the_read_locks_nothing_beyond_the_extensions_own_and_creates_nothing()
    {
        string database = await server.CreateDatabaseAsync("q1_rank");
        string app = server.ConnectionString(database, StatementsPageServer.AppRole);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.RepeatAsync(app, Slow, 25);

        HealthSweepProbe probe = new(async cancellation =>
        {
            NpgsqlConnection second = new(server.ConnectionString(database));
            await second.OpenAsync(cancellation);
            return second;
        });

        await using StudioHost host = StatementsLive.Host(app, probe);
        IReadOnlySet<string> before = await SnapshotAsync(server.ConnectionString(database));

        StatementsView view = await StatementsLive.ReadAsync(host);

        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        StatementRowView first = StatementsLive.Top(view)[0];
        first.Text.Should().Contain("pg_sleep").And.Contain("$1", "it is the text pg_stat_statements recorded, constants replaced");
        first.Window!.Calls.Should().Be(25);
        first.GroupLabel.Should().Be("select · public.orders");
        first.Share.Should().BePositive();
        view.Watched.Should().BeFalse("one statement sweep is not a window: the totals since the counters began");
        view.StudioCost.Should().NotBeNull();
        view.StudioCost!.Value.Connections.Should().BePositive("the sweep's own connection is metered");

        (await SnapshotAsync(server.ConnectionString(database))).Should().BeEquivalentTo(before, "a read path creates nothing");
        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads
            .Where(static x => x.Relation is not ("pg_stat_statements_info" or "pg_stat_statements"))
            .Should().BeEmpty("the page's reads lock nothing beyond the extension's own view: " + string.Join("; ", probe.BeyondCatalogReads));

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_literal_bearing_utility_text_is_withheld_a_comment_is_masked_and_another_roles_statement_is_absent()
    {
        string database = await server.CreateDatabaseAsync("q1_texts");
        string app = server.ConnectionString(database, StatementsPageServer.AppRole);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.ExecuteAsync(app, "grant select on public.orders to " + StatementsPageServer.OtherRole);

        // A COPY keeps its constants as written on PostgreSQL 17.
        await using (NpgsqlConnection connection = new(app))
        {
            await connection.OpenAsync(Token);
            using TextReader export = await connection.BeginTextExportAsync("copy (select 'alice@example.com' as who from public.orders) to stdout", Token);
            await export.ReadToEndAsync(Token);
        }

        await StatementsLive.RepeatAsync(app, "/* user='alice@example.com' */ select count(*) from public.orders where id > @id", 3);
        await StatementsLive.RepeatAsync(server.ConnectionString(database, StatementsPageServer.OtherRole), "select 42 as q1_other_role_marker from public.orders where id = @id", 3);

        await using StudioHost host = StatementsLive.Host(app);
        StatementsView view = await StatementsLive.ReadAsync(host);
        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));

        StatementRowView copy = view.Rows.Single(static x => x.GroupLabel.StartsWith("copy", StringComparison.Ordinal));
        copy.Text.Should().BeNull("a COPY keeps its constants as written");
        copy.TextNote.Should().Be(StatementReads.WithheldUtility);

        StatementRowView commented = view.Rows.Single(static x => x.Text?.Contains("count(*) from public.orders where id >", StringComparison.Ordinal) == true);
        commented.Text.Should().StartWith(StatementReads.CommentMask).And.NotContain("alice");

        view.Rows.Should().NotContain(static x => (x.Text ?? string.Empty).Contains("q1_other_role_marker", StringComparison.Ordinal),
            "the read is fenced to the store's own role (D29): another role's statements are not among them");
        view.Rows.Should().NotContain(static x => (x.Text ?? string.Empty).Contains("alice", StringComparison.Ordinal));
    }

    /// <summary>The supported floor and the newest release, through the version matrix (VERS).</summary>
    public static TheoryData<int> MatrixLegs => [14, 18];

    [PostgresTheory]
    [MemberData(nameof(MatrixLegs))]
    public async Task On_14_and_18_a_known_statement_ranks_first_with_its_calls(int major)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync("q1_statements");
        await leg.ExecuteAsync("create extension if not exists pg_stat_statements", database, Token);

        string app = leg.ConnectionString(MatrixRole.App, database);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.RepeatAsync(app, Slow, 25);

        await using StudioHost host = StatementsLive.Host(app);
        StatementsView view = await StatementsLive.ReadAsync(host);

        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        StatementRowView first = StatementsLive.Top(view)[0];
        first.Text.Should().Contain("pg_sleep");
        first.Window!.Calls.Should().Be(25);
        view.StorageReads.Should().BeFalse("not Aurora");
    }

    /// <summary>The shim's shapes: Aurora's documented "every column, then eleven more" (15/16), and 17's colliding one.</summary>
    public static TheoryData<int> AuroraLegs => [16, 17];

    [PostgresTheory]
    [MemberData(nameof(AuroraLegs))]
    public async Task On_the_Aurora_shim_rank_by_storage_reads_orders_by_the_shims_column(int major)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await AuroraShim.ApplyAsync(leg, "q1_aurora", Token);
        await leg.ExecuteAsync("create extension if not exists pg_stat_statements", database, Token);
        await leg.ExecuteAsync(await AuroraStatementsShimAsync(leg, database, major), database, Token);

        string app = leg.ConnectionString(MatrixRole.App, database);
        await StatementsLive.SeedAsync(app);

        // A: three slow calls - the most total time. B: two hundred cheap ones - the most storage reads, by the shim's column.
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.05), o.note from public.orders o where o.id = @id", 3);
        await StatementsLive.RepeatAsync(app, "select o.note from public.orders o where o.id = @id and o.note is not null", 200);

        await using StudioHost host = StatementsLive.Host(app);
        StatementsView view = await StatementsLive.ReadAsync(host);

        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        view.StorageReads.Should().BeTrue("the platform is Aurora and pg_catalog.aurora_stat_statements is there");

        StatementsLive.Top(view, StatementRanking.TotalTime)[0].Text.Should().Contain("pg_sleep");

        StatementRowView byStorage = StatementsLive.Top(view, StatementRanking.StorageReads)[0];
        byStorage.Text.Should().Contain("is not null", StatementsLive.Describe(view));
        byStorage.Window!.StorageBlocksRead.Should().Be(200 * 1000, "the shim counts a thousand storage reads a call");
    }

    /// <summary>
    /// <c>pg_catalog.aurora_stat_statements(showtext)</c> as a plain SQL function: on 15/16 every column of the extension's
    /// own, then Aurora's eleven (B0's Aurora table); on 17, whose extension has its own <c>local_blk_read_time</c>, the four
    /// join keys and the eleven - the shape whose collision the studio's by-name join exists for. A thousand storage reads a
    /// call, so the ranking is the shim's own.
    /// </summary>
    private static async Task<string> AuroraStatementsShimAsync(MatrixServer leg, string database, int major)
    {
        List<(string Name, string Type)> columns = [];

        await using (NpgsqlConnection connection = await leg.OpenAsync(MatrixRole.Superuser, database, Token))
        await using (NpgsqlCommand command = new(
            "select a.attname::text, pg_catalog.format_type(a.atttypid, a.atttypmod) from pg_catalog.pg_attribute a "
            + "where a.attrelid = 'public.pg_stat_statements'::pg_catalog.regclass and a.attnum > 0 and not a.attisdropped order by a.attnum",
            connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        (string Name, string Type, string Value)[] extras =
        [
            ("storage_blks_read", "bigint", "s.calls * 1000"),
            ("orcache_blks_hit", "bigint", "0::bigint"),
            ("storage_blk_read_time", "double precision", "0::float8"),
            ("local_blk_read_time", "double precision", "0::float8"),
            ("orcache_blk_read_time", "double precision", "0::float8"),
            ("total_plan_peakmem", "bigint", "0::bigint"),
            ("min_plan_peakmem", "bigint", "0::bigint"),
            ("max_plan_peakmem", "bigint", "0::bigint"),
            ("total_exec_peakmem", "bigint", "0::bigint"),
            ("min_exec_peakmem", "bigint", "0::bigint"),
            ("max_exec_peakmem", "bigint", "0::bigint"),
        ];

        bool collides = columns.Any(static x => x.Name == "local_blk_read_time");
        (major >= 17).Should().Be(collides, "17's extension has its own local_blk_read_time, which is the collision");

        IEnumerable<(string Name, string Type)> leading = collides
            ? columns.Where(static x => x.Name is "userid" or "dbid" or "toplevel" or "queryid")
            : columns;

        string outs = string.Join(", ", leading.Select(static x => "out " + x.Name + " " + x.Type).Concat(extras.Select(static x => "out " + x.Name + " " + x.Type)));
        string select = string.Join(", ", leading.Select(static x => "s." + x.Name).Concat(extras.Select(static x => x.Value)));

        // test shim: Aurora's aurora_stat_statements from the AWS documentation, B0 2026-10-20 - in pg_catalog, as the superuser.
        return "create function pg_catalog.aurora_stat_statements(showtext boolean, " + outs + ") returns setof record language sql as $$ "
            + "select " + select + " from public.pg_stat_statements(showtext) s $$";
    }

    /// <summary>Every object in every schema of the test's database but Postgres' own, every extension - the no-DDL fingerprint.</summary>
    private static async Task<IReadOnlySet<string>> SnapshotAsync(string connectionString)
    {
        const string Sql =
            "with s as (select n.oid, n.nspname from pg_catalog.pg_namespace n where n.nspname not in ('pg_catalog', 'information_schema', 'pg_toast') and n.nspname not like 'pg\\_temp\\_%' and n.nspname not like 'pg\\_toast\\_temp\\_%') "
            + "select 'schema:' || s.nspname from s "
            + "union all select 'relation:' || s.nspname || '.' || c.relname || ':' || c.relkind::text from pg_catalog.pg_class c join s on s.oid = c.relnamespace "
            + "union all select 'routine:' || s.nspname || '.' || p.proname from pg_catalog.pg_proc p join s on s.oid = p.pronamespace "
            + "union all select 'extension:' || e.extname || ':' || e.extversion from pg_catalog.pg_extension e";

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(Sql, connection);

        HashSet<string> objects = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }
}
