using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A sweep of the nine connection checks, with a session idle in a transaction and another waiting behind it so
/// that every statement has rows to read, takes no lock beyond a catalog read and creates nothing - measured with
/// H1's <see cref="HealthSweepProbe" />, which reads <c>pg_locks</c> for the sweep's backend from a second
/// connection while each statement's transaction is still open.
/// </summary>
public class ConnectionSweepNoDdlLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Type[] ConnectionChecks =
    [
        typeof(PoolSaturationCheck),
        typeof(ConnectionBudgetCheck),
        typeof(ServerConnectionHeadroomCheck),
        typeof(IdleInTransactionCheck),
        typeof(LongRunningStatementCheck),
        typeof(LockWaitCheck),
        typeof(PreparedTransactionCheck),
        typeof(DatabaseCounterCheck),
        typeof(DaemonPoolCheck),
    ];

    [PostgresFact]
    public async Task A_sweep_of_the_connection_checks_creates_nothing_and_holds_no_lock_beyond_a_catalog_read()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_connections_noddl");
        HealthSweepProbe probe = new(postgres);

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            studio: static options => options.Capabilities = MartenStudioCapabilities.All(),
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(HealthCheckRegistry.ForTypes(ConnectionChecks)));

        await using (NpgsqlConnection setup = await host.OpenAsync())
        await using (NpgsqlCommand create = new("create table h3_rows (id int primary key, v int); insert into h3_rows values (1, 0);", setup))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        IReadOnlySet<string> before = await ObjectsAsync(database);

        await using NpgsqlConnection holder = await host.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("update h3_rows set v = 1 where id = 1", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection waiter = await host.OpenAsync();
        // No client-side timeout: the waiter waits for as long as the sweep takes, which is not Npgsql's to cut short.
        await using NpgsqlCommand blocked = new("update h3_rows set v = 2 where id = 1", waiter) { CommandTimeout = 0 };
        Task waiting = blocked.ExecuteNonQueryAsync(Token);

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database, "select exists (select 1 from pg_stat_activity where pid = @pid and wait_event_type = 'Lock')", ("pid", waiter.ProcessID)),
                TimeSpan.FromSeconds(20),
                "the waiter to queue behind the holder");

            HealthReport report = await host.ReportAsync();

            report.Checks.Where(static x => x.Area == HealthArea.Connections)
                .Should().HaveCount(9)
                .And.OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked, "every connection check ran");

            probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
            probe.BeyondCatalogReads.Should().BeEmpty(
                "reading pg_stat_activity, pg_locks, pg_prepared_xacts and pg_stat_database locks nothing it lists");
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            await waiting;
        }

        (await ObjectsAsync(database)).Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }

    /// <summary>Every relation, routine and schema in the test's database, by name and kind.</summary>
    private static async Task<IReadOnlySet<string>> ObjectsAsync(string database)
    {
        const string Sql =
            "select 'schema:' || n.nspname from pg_catalog.pg_namespace n "
            + "union all select 'relation:' || n.nspname || '.' || c.relname || ':' || c.relkind::text from pg_catalog.pg_class c "
            + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname not in ('pg_catalog', 'information_schema', 'pg_toast') "
            + "union all select 'routine:' || n.nspname || '.' || p.proname from pg_catalog.pg_proc p "
            + "join pg_catalog.pg_namespace n on n.oid = p.pronamespace where n.nspname not in ('pg_catalog', 'information_schema')";

        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(database) { Pooling = false }.ConnectionString);
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
