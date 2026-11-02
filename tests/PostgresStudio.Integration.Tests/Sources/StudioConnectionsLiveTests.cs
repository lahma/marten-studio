using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Sources;

/// <summary>
/// Issue #44, on a server: the studio's sessions carry a name of their own in <c>pg_stat_activity</c> for exactly as long as its
/// transaction - the pooled connection goes back to the application with the application's name, even with Npgsql's reset on close
/// turned off - and it holds at most <c>MaxStudioConnections</c> of the application's pool, a read beyond it waiting for one, or
/// saying so when none comes free; a whole health sweep, nested reads and all, needs no more than one.
/// </summary>
public class StudioConnectionsLiveTests(PostgresFixture postgres)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (string) (await command.ExecuteScalarAsync(Token))!;
    }

    [PostgresFact]
    public async Task The_studio_names_its_transactions_and_hands_the_pooled_connection_back_unnamed()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_named"], Token);

        // One physical connection, never reset on return: a name the studio left behind would be the application's next.
        string connectionString = new NpgsqlConnectionStringBuilder(owned[0])
        {
            ApplicationName = "orders-api",
            MaxPoolSize = 1,
            NoResetOnClose = true,
        }.ConnectionString;

        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(dataSource),
            static o => o.Capabilities.RunSql = true);
        await using StudioCircuit circuit = host.Circuit();

        SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(
            Scope,
            new SqlConsoleRequest("select current_setting('application_name') as app, (select application_name from pg_stat_activity where pid = pg_backend_pid()) as seen"),
            Token);

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.Rows.Single()[0].Text.Should().Be("orders-api (Postgres Studio)");
        result.Rows.Single()[1].Text.Should().Be("orders-api (Postgres Studio)", "pg_stat_activity, and so Sessions and any monitor, see it");

        await using NpgsqlConnection application = await dataSource.OpenConnectionAsync(Token);
        (await ScalarAsync(application, "select current_setting('application_name')")).Should().Be("orders-api",
            "the name was the studio transaction's alone, and reverted at its rollback");
    }

    [PostgresFact]
    public async Task A_host_name_is_used_as_written_and_an_empty_one_leaves_the_applications()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_named_host", "ps_conn_named_none"], Token);

        foreach ((string database, string? configured, string expected) in new[]
                 {
                     (owned[0], (string?) "ops-console", "ops-console"),
                     (owned[1], "", "orders-api"),
                 })
        {
            NpgsqlDataSource dataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(database) { ApplicationName = "orders-api" }.ConnectionString);

            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(dataSource),
                o =>
                {
                    o.Capabilities.RunSql = true;
                    o.StudioApplicationName = configured;
                });
            await using StudioCircuit circuit = host.Circuit();

            SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select current_setting('application_name')"), Token);

            result.Succeeded.Should().BeTrue(result.Error?.MessageText);
            result.Rows.Single()[0].Text.Should().Be(expected);
        }
    }

    [PostgresFact]
    public async Task A_read_beyond_the_cap_waits_for_a_connection_and_then_runs()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_cap"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(dataSource),
            static o =>
            {
                o.Capabilities.RunSql = true;
                o.MaxStudioConnections = 1;
                o.QueryTimeout = TimeSpan.FromSeconds(30);
            });
        await using StudioCircuit first = host.Circuit();
        await using StudioCircuit second = host.Circuit();

        Task<SqlConsoleResult> slow = first.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select pg_sleep(2)::text"), Token);

        StudioConnectionPool pool = await PoolAsync(dataSource);
        await UntilAsync(() => pool.InUse == 1, "the slow statement holds the one connection");

        Task<SqlConsoleResult> next = second.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select 'next'"), Token);
        await UntilAsync(() => pool.Waiting == 1, "the next read waits for it");
        next.IsCompleted.Should().BeFalse();

        (await slow).Succeeded.Should().BeTrue();
        SqlConsoleResult ran = await next;

        ran.Succeeded.Should().BeTrue(ran.Error?.MessageText);
        ran.Rows.Single()[0].Text.Should().Be("next");
        await UntilAsync(() => pool.InUse == 0, "both gave their connection back");
    }

    [PostgresFact]
    public async Task A_read_that_finds_every_connection_held_past_the_timeout_says_so()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_busy"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(dataSource),
            static o =>
            {
                o.Capabilities.RunSql = true;
                o.MaxStudioConnections = 1;
                o.QueryTimeout = TimeSpan.FromSeconds(1);
            });
        await using StudioCircuit circuit = host.Circuit();

        // A first read, so the studio has registered this data source's share under its own settings - a cap of one.
        (await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select 1"), Token)).Succeeded.Should().BeTrue();

        StudioConnectionPool pool = await PoolAsync(dataSource);
        pool.Max.Should().Be(1);
        StudioConnectionLease held = await pool.AcquireAsync(new StudioConnectionLease(pool, reentrant: false), Token);

        try
        {
            SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select 1"), Token);

            result.Succeeded.Should().BeFalse();
            result.Error!.MessageText.Should().Contain("MaxStudioConnections");
            host.Ring.GetLatest().Should().Contain(static x => !x.Succeeded && x.Message != null && x.Message.Contains("MaxStudioConnections", StringComparison.Ordinal),
                "the refused run is audited as a failure (hard rule 5)");
        }
        finally
        {
            held.Release();
        }
    }

    [PostgresFact]
    public async Task A_whole_health_sweep_runs_under_a_cap_of_one()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_sweep"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(dataSource),
            static o =>
            {
                o.MaxStudioConnections = 1;
                o.QueryTimeout = TimeSpan.FromSeconds(10);
            });
        await using StudioCircuit circuit = host.Circuit();

        HealthReport report = await circuit.Get<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token);

        report.Refusal.Should().Be(HealthRefusal.None, report.Reason);
        report.Checks.Should().NotBeEmpty();
        report.Checks.Should().NotContain(static x => x.Note != null && x.Note.Contains("MaxStudioConnections", StringComparison.Ordinal),
            "the sweep's nested reads ride on the connection it already holds");
        (await PoolAsync(dataSource)).InUse.Should().Be(0);
    }

    [PostgresFact]
    public async Task A_sweep_that_finds_every_connection_held_is_not_checked_rather_than_failed()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_sweep_busy"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(dataSource),
            static o =>
            {
                o.Capabilities.RunSql = true;
                o.MaxStudioConnections = 1;
                o.QueryTimeout = TimeSpan.FromSeconds(1);
            });
        await using StudioCircuit circuit = host.Circuit();

        // A first read registers this data source's share under the host's settings.
        (await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest("select 1"), Token)).Succeeded.Should().BeTrue();

        StudioConnectionPool pool = await PoolAsync(dataSource);
        StudioConnectionLease held = await pool.AcquireAsync(new StudioConnectionLease(pool, reentrant: false), Token);

        try
        {
            HealthReport report = await circuit.Get<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token);

            report.Checks.Should().Contain(static x => x.Outcome == HealthCheckOutcome.Unavailable
                && x.Problem != null && x.Problem.Contains("MaxStudioConnections", StringComparison.Ordinal));
            report.Checks.Should().NotContain(static x => x.Outcome == HealthCheckOutcome.Failed,
                "a share of the pool that visitors hold is the host's cap at work, not a check that failed");
            report.Checks.Should().NotContain(static x => x.Problem != null && x.Problem.Contains("could not connect", StringComparison.Ordinal));
        }
        finally
        {
            held.Release();
        }
    }

    [PostgresFact]
    public async Task A_nested_open_in_the_same_flow_rides_on_the_slot_it_already_holds()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_conn_nested"], Token);
        NpgsqlDataSource dataSource = NpgsqlDataSource.Create(owned[0]);
        StudioConnectionPool pool = new(1, "nested-test", TimeSpan.FromSeconds(1));

        await using NpgsqlConnection outer = StudioConnectionMeter.Attach(dataSource.CreateConnection(), pool: pool);
        await outer.OpenStudioAsync(Token);
        pool.InUse.Should().Be(1);

        // The same flow opens a second connection of the same share: it neither waits out the second's timeout nor takes a slot.
        await using (NpgsqlConnection inner = StudioConnectionMeter.Attach(dataSource.CreateConnection(), pool: pool))
        {
            await inner.OpenStudioAsync(Token);
            pool.InUse.Should().Be(1);
            pool.Waiting.Should().Be(0);
        }

        await outer.CloseAsync();
        pool.InUse.Should().Be(0, "the outer connection's close gives the one slot back");
    }

    /// <summary>The studio's share of <paramref name="dataSource" />, once the host has registered it.</summary>
    private static async Task<StudioConnectionPool> PoolAsync(NpgsqlDataSource dataSource)
    {
        StudioConnectionPool? pool = null;
        await UntilAsync(() => (pool = StudioConnectionPools.Of(dataSource)) is not null, "the studio registered the pool");
        return pool!;
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Waited 30 s for " + what + ".");
            }

            await Task.Delay(20, Token);
        }
    }
}
