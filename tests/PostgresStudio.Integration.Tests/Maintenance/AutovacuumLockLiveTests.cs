using Npgsql;

using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// The locks autovacuum gives way to, as the table read sees them (W5-V1 review, N1): one an orphaned prepared transaction
/// holds - whose locks have no process - and a strong request queued behind a reader, which keeps autovacuum's conditional
/// lock away as surely as a granted one. A table nobody locks is the control.
/// </summary>
public class AutovacuumLockLiveTests
{
    private const string Gid = "ps_locks_orphan";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_prepared_transactions_lock_and_a_queued_ALTER_are_locks_autovacuum_gives_way_to()
    {
        await using AutovacuumServer server = await AutovacuumServer.StartAsync(17, "max_prepared_transactions=2");

        await server.ExecuteAsync(
            "create schema ps_parity",
            "create table ps_parity.prepared_t (id int)",
            "create table ps_parity.queued_t (id int)",
            "create table ps_parity.free_t (id int)");

        // An orphaned two-phase commit: its ACCESS EXCLUSIVE lock outlives the session that took it, with no process.
        await server.ExecuteAsync("begin", "lock table ps_parity.prepared_t in access exclusive mode", "prepare transaction '" + Gid + "'");

        await using NpgsqlConnection reader = await server.OpenAsync();
        await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);
        await using (NpgsqlCommand read = new("select pg_catalog.count(*) from ps_parity.queued_t", reader, reading))
        {
            await read.ExecuteScalarAsync(Token);
        }

        await using NpgsqlConnection altering = await server.OpenAsync();
        Task alter = Task.Run(
            async () =>
            {
                await using NpgsqlCommand command = new("alter table ps_parity.queued_t add column extra int", altering) { CommandTimeout = 120 };
                await command.ExecuteNonQueryAsync(Token);
            },
            Token);

        try
        {
            await AutovacuumServer.UntilAsync(
                async () => await server.ScalarAsync<bool>(
                    "select exists (select 1 from pg_catalog.pg_locks l where not l.granted and l.relation = 'ps_parity.queued_t'::regclass)"),
                TimeSpan.FromSeconds(30),
                "the ALTER to queue behind the reader");

            (await server.ScalarAsync<bool>(
                    "select exists (select 1 from pg_catalog.pg_locks l where l.pid is null and l.relation = 'ps_parity.prepared_t'::regclass)"))
                .Should().BeTrue("a prepared transaction's locks have no process");

            IReadOnlyList<TableFacts> tables = await TriggerParityLiveTests.TablesAsync(server);

            tables.Single(static x => x.Table.Table == "prepared_t").Row.Vacuum!.Locked.Should().BeTrue("an orphaned 2PC keeps autovacuum off the table for good");
            tables.Single(static x => x.Table.Table == "queued_t").Row.Vacuum!.Locked.Should().BeTrue("a queued ACCESS EXCLUSIVE keeps autovacuum's conditional lock away");
            tables.Single(static x => x.Table.Table == "free_t").Row.Vacuum!.Locked.Should().BeFalse();
        }
        finally
        {
            await server.ExecuteAsync("rollback prepared '" + Gid + "'");
            await reading.CommitAsync(CancellationToken.None);
            await alter;
        }
    }
}
