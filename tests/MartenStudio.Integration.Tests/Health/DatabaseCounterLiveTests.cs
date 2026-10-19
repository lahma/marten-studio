using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// C08 over two real sweeps one <c>HealthInterval</c> apart (on a clock the test moves): a deadlock, a thousand
/// rollbacks and a sort that spills to disk in between fire it three ways, and a quiet database between two
/// sweeps - the studio's own rolled-back reads included - fires nothing.
/// </summary>
public class DatabaseCounterLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static Task<long> CounterAsync(string database, string column) =>
        LiveWait.ScalarAsync<long>(database, "select " + column + " from pg_stat_database where datname = current_database()");

    [PostgresFact]
    [HealthFires("C08")]
    public async Task A_deadlock_rollbacks_and_a_temp_spill_between_two_checks_are_three_findings()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c08_fires");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

        await using (NpgsqlConnection setup = new(Unpooled(database)))
        {
            await setup.OpenAsync(Token);
            await using NpgsqlCommand create = new("create table h3_pairs (id int primary key, v int); insert into h3_pairs values (1, 0), (2, 0);", setup);
            await create.ExecuteNonQueryAsync(Token);
        }

        HealthReport first = await host.ReportAsync();
        ConnectionsLiveHost.Status(first, "C08").Note.Should().Contain("Watching");

        long deadlocksBefore = await CounterAsync(database, "deadlocks");
        long rollbacksBefore = await CounterAsync(database, "xact_rollback");
        long tempBefore = await CounterAsync(database, "temp_files");

        await DeadlockAsync(database);
        await RollBackAsync(database, 1_200);
        await SpillAsync(database);

        // Backends report their counters when they go idle or exit; these did both, and the reader waits for it.
        await LiveWait.UntilAsync(
            async () => await CounterAsync(database, "deadlocks") > deadlocksBefore
                && await CounterAsync(database, "xact_rollback") >= rollbacksBefore + 1_200
                && await CounterAsync(database, "temp_files") > tempBefore,
            TimeSpan.FromSeconds(30),
            "pg_stat_database to count the deadlock, the rollbacks and the temp file");

        clock.Advance(host.Provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value.EffectiveHealthInterval + TimeSpan.FromSeconds(1));

        HealthReport second = await host.ReportAsync();

        HealthReportFinding[] findings = [.. second.Findings.Where(static x => x.Code == "C08")];
        findings.Select(static x => x.Finding.Subject).Should().BeEquivalentTo(["deadlocks", "rollbacks", "temp-files"]);
        findings.Single(static x => x.Finding.Subject == "deadlocks").Severity.Should().Be(HealthSeverity.Warning);
        findings.Single(static x => x.Finding.Subject == "rollbacks").Severity.Should().Be(HealthSeverity.Warning);
        findings.Single(static x => x.Finding.Subject == "temp-files").Severity.Should().Be(HealthSeverity.Info);
        findings.Should().OnlyContain(static x => x.Finding.Caveat!.Contains("statistics", StringComparison.Ordinal));
    }

    [PostgresFact]
    [HealthQuiet("C08")]
    public async Task A_quiet_database_watched_by_the_studio_is_quiet_between_two_checks()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_c08_quiet");
        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

        await host.ReportAsync();
        clock.Advance(host.Provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value.EffectiveHealthInterval + TimeSpan.FromSeconds(1));
        HealthReport second = await host.ReportAsync();

        second.Findings.Should().NotContain(static x => x.Code == "C08", "every read the studio made rolled back, and they are its own");
        HealthCheckStatus status = ConnectionsLiveHost.Status(second, "C08");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked);
        status.Note.Should().Contain("No deadlock");
    }

    /// <summary>Two transactions taking two rows in opposite orders; Postgres ends one of them after deadlock_timeout.</summary>
    private static async Task DeadlockAsync(string database)
    {
        await using NpgsqlConnection one = new(Unpooled(database));
        await using NpgsqlConnection two = new(Unpooled(database));
        await one.OpenAsync(Token);
        await two.OpenAsync(Token);

        await using NpgsqlTransaction first = await one.BeginTransactionAsync(Token);
        await using NpgsqlTransaction second = await two.BeginTransactionAsync(Token);

        await ExecuteAsync(one, first, "update h3_pairs set v = v + 1 where id = 1");
        await ExecuteAsync(two, second, "update h3_pairs set v = v + 1 where id = 2");

        Task crossing = ExecuteAsync(one, first, "update h3_pairs set v = v + 1 where id = 2");
        Task closing = ExecuteAsync(two, second, "update h3_pairs set v = v + 1 where id = 1");

        int deadlocks = 0;

        foreach (Task statement in new[] { crossing, closing })
        {
            try
            {
                await statement;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DeadlockDetected)
            {
                deadlocks++;
            }
        }

        deadlocks.Should().Be(1, "Postgres breaks a deadlock by ending exactly one of its transactions");
    }

    private static async Task RollBackAsync(string database, int count)
    {
        await using NpgsqlConnection connection = new(Unpooled(database));
        await connection.OpenAsync(Token);

        for (int i = 0; i < count; i++)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);
            await ExecuteAsync(connection, transaction, "select 1");
            await transaction.RollbackAsync(Token);
        }
    }

    private static async Task SpillAsync(string database)
    {
        await using NpgsqlConnection connection = new(Unpooled(database));
        await connection.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        await ExecuteAsync(connection, transaction, "set local work_mem = '64kB'");
        await ExecuteAsync(connection, transaction, "select count(*) from (select g from generate_series(1, 200000) g order by g desc) s");
        await transaction.CommitAsync(Token);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Token);
    }
}
