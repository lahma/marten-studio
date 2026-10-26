using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// H3's half of X3: a regular sweep reads the lock table once, whichever of its checks need it - the table checks' strong
/// and <c>ACCESS EXCLUSIVE</c> locks (V1's autovacuum triggers and the index read's deparse guard), C04's relation-lock
/// counts, T14's and C04's daemon-lock holders, and C06 and C12's waiters and blockers.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> Every statement the sweep ran, as the studio's own statement observer is told of it while its
/// transaction is open, and in each the relations it reads <c>pg_locks</c> as (<c>from</c> or <c>join</c>, comments
/// left out): each is one <c>Function Scan on pg_lock_status</c> in its plan, one walk of the lock manager's partitions
/// with all of them held. A reference inside a CTE the statement reads twice is still one scan, because such a CTE is
/// materialized. <c>pg_blocking_pids</c> is counted apart: it is called once per waiting session, by design.
/// </para>
/// <para>
/// <b>In a database of its own</b> (the FLK-T25 lesson): the holder below is a transaction with a lock, which must never
/// sit in a database another class's checks read.
/// </para>
/// </remarks>
public class LockTableReadLiveTests(PostgresFixture postgres)
{
    private const string Application = "ps_x3_lock_reads_waiter";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A reference to <c>pg_locks</c> as a relation the statement reads.</summary>
    private static readonly Regex ReadsTheLockTable = new(
        @"\b(from|join)\s+(pg_catalog\.)?pg_locks\b(?!\.)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex CallsBlockingPids = new(
        @"\bpg_blocking_pids\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    [PostgresFact]
    public async Task A_regular_sweep_reads_the_lock_table_once_with_a_holder_and_a_waiter_present()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_x3_lock_reads"], Token);
        await OwnedDatabases.ExecuteAsync(
            owned[0],
            "create table public.jobs (id int primary key, note text); insert into public.jobs select g, 'n' || g from generate_series(1, 100) g",
            Token);

        await using NpgsqlConnection holder = new(ServerSnapshot.Unpooled(owned[0]));
        await holder.OpenAsync(Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("lock table public.jobs in share row exclusive mode", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection waiter = new(new NpgsqlConnectionStringBuilder(ServerSnapshot.Unpooled(owned[0])) { ApplicationName = Application }.ConnectionString);
        await waiter.OpenAsync(Token);
        await using NpgsqlCommand blocked = new("update public.jobs set note = note where id = 1", waiter) { CommandTimeout = 120 };
        Task<int> waiting = blocked.ExecuteNonQueryAsync(Token);

        StatementRecorder recorder = new();

        try
        {
            await LiveLocks.UntilWaitingAsync(owned[0], Application, Token);

            await using StudioHost host = StudioHost.Create(services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
                services.AddSingleton<IHealthStatementObserver>(recorder);
            });

            HealthSweep sweep = await host.CircuitAsync("admin", async circuit =>
            {
                ResolvedSource resolved = await circuit.GetRequiredService<StudioSourceResolver>()
                    .ResolveAsync(new StudioScope("default", "default", null), null, Token);

                return await host.Services.GetRequiredService<HealthSweeper>().SweepAsync(resolved, HealthSweepKind.Regular, Token);
            });

            sweep.Results.Should().NotContain(static x => x.Outcome == HealthCheckOutcome.Failed, "every check reads or says why not");

            IReadOnlyList<string> statements = recorder.Statements;
            int scans = statements.Sum(static x => ReadsTheLockTable.Count(WithoutComments(x)));
            int readers = statements.Count(static x => ReadsTheLockTable.IsMatch(WithoutComments(x)));
            int blockingCalls = statements.Sum(static x => CallsBlockingPids.Count(WithoutComments(x)));

            TestContext.Current.TestOutputHelper?.WriteLine(
                "Regular sweep: {0} statements, {1} of them read pg_locks, {2} pg_locks scans in all, {3} pg_blocking_pids call sites.",
                statements.Count, readers, scans, blockingCalls);

            readers.Should().Be(1, "one statement of the sweep reads the lock table, and every check that needs it takes its answer");
            scans.Should().Be(1, "and that statement reads it once");
            blockingCalls.Should().Be(1, "pg_blocking_pids is called in that one statement, for the waiting sessions alone");
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            (await waiting).Should().Be(1, "the waiter ran once the holder let go");
        }
    }

    /// <summary>The statement's text without its <c>--</c> and <c>/* */</c> comments, which may name a view in prose.</summary>
    private static string WithoutComments(string sql) =>
        Regex.Replace(Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline, TimeSpan.FromSeconds(5)), "--[^\n]*", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>The studio's statement observer, recording each statement's text as it runs.</summary>
    private sealed class StatementRecorder : IHealthStatementObserver
    {
        private readonly ConcurrentQueue<string> statements = new();

        public IReadOnlyList<string> Statements => [.. statements];

        public Task ObserveAsync(int backendProcessId, HealthStatement statement, CancellationToken cancellationToken)
        {
            statements.Enqueue(statement.Sql);
            return Task.CompletedTask;
        }
    }
}

/// <summary>Waits on a lock condition, never on the clock.</summary>
internal static class LiveLocks
{
    /// <summary>Waits until a session of <paramref name="application" /> has an ungranted lock in <c>pg_locks</c>.</summary>
    public static async Task UntilWaitingAsync(string connectionString, string application, CancellationToken token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        await using NpgsqlConnection probe = new(ServerSnapshot.Unpooled(connectionString));
        await probe.OpenAsync(deadline.Token);

        do
        {
            await using NpgsqlCommand command = new(
                "select exists (select 1 from pg_catalog.pg_locks l join pg_catalog.pg_stat_activity a on a.pid = l.pid "
                + "where not l.granted and a.application_name = @application)",
                probe);
            command.Parameters.AddWithValue("application", application);

            if ((bool) (await command.ExecuteScalarAsync(deadline.Token))!)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(deadline.Token));
    }
}
