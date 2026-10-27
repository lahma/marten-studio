using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// X3 on a real PostgreSQL 17: Database › Sessions › Blocking and C12 against sessions of the test's own - a head idle in its
/// transaction holding <c>ACCESS EXCLUSIVE</c> on a table the visitor may see and one in a withheld schema, with a waiter
/// behind each; a head running a statement, with a waiter behind it; and a session idle in its transaction that blocks nobody.
/// </summary>
/// <remarks>
/// <para>
/// <b>A database of each test's own</b> (the FLK-T25 lesson): a held lock is a transaction with an id, which must never sit
/// in a database another class's checks read. Every wait is on <c>pg_locks</c> saying the waiter waits, never on the clock;
/// a threshold minutes long is reached by judging the read facts as of a later moment, as C05's live test does
/// (<c>SnapshotAge</c>), never by waiting it out.
/// </para>
/// <para>
/// <b>The studio never ends a session.</b> Each test ends its own sessions - the holder rolls back, or its statement's token
/// is cancelled - and the statements C12 and the tab hand over are asserted as text, never run.
/// </para>
/// </remarks>
public class BlockingLiveTests(PostgresFixture postgres)
{
    private const string JobsWaiter = "ps_x3_waiter_jobs";
    private const string SalariesWaiter = "ps_x3_waiter_salaries";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Setup =
    [
        "create table public.jobs (id int primary key)",
        "insert into public.jobs select g from generate_series(1, 10) g",
        "create schema payroll",
        "create table payroll.salaries (id int primary key, amount numeric)",
        "insert into payroll.salaries select g, g * 1000 from generate_series(1, 10) g",
    ];

    [PostgresFact]
    [HealthFires("C12")]
    public async Task A_head_idle_in_its_transaction_with_two_waiters_is_one_tree_the_withheld_relation_is_masked_and_C12_fires()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_x3_blocking_idle"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], string.Join(";\n", Setup), Token);

        await using NpgsqlConnection holder = new(ServerSnapshot.Unpooled(owned[0]));
        await holder.OpenAsync(Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("lock table public.jobs in access exclusive mode; lock table payroll.salaries in access exclusive mode", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        (NpgsqlConnection jobs, Task<object?> jobsWait) = await WaitBehindAsync(owned[0], JobsWaiter, "select count(*) from public.jobs");
        (NpgsqlConnection salaries, Task<object?> salariesWait) = await WaitBehindAsync(owned[0], SalariesWaiter, "select count(*) from payroll.salaries");

        HealthSweepProbe probe = new(async token =>
        {
            NpgsqlConnection second = new(ServerSnapshot.Unpooled(owned[0]));
            await second.OpenAsync(token);
            return second;
        });

        try
        {
            await LiveLocks.UntilWaitingAsync(owned[0], JobsWaiter, Token);
            await LiveLocks.UntilWaitingAsync(owned[0], SalariesWaiter, Token);

            await using StudioHost host = StudioHost.Create(
                services =>
                {
                    services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
                    services.AddSingleton<IHealthStatementObserver>(probe);
                },
                static o => o.Capabilities = PostgresStudioCapabilities.All(),
                writePolicy: static (resource, user) => user.Identity?.Name == "admin");

            // The regular sweep: C12 checks, nothing fails, and the sweep took no lock but the catalog's - though public.jobs
            // is under ACCESS EXCLUSIVE, the index read was handed it by the one read of the lock table and deparsed none of it.
            HealthSweep sweep = await SweepAsync(host);
            sweep.Results.Should().NotContain(static x => x.Outcome == HealthCheckOutcome.Failed, "every check reads or says why not");
            sweep.Results.Single(static x => x.Check.Code == "C12").Outcome.Should().Be(HealthCheckOutcome.Checked);
            sweep.Results.Should().Contain(static x => x.Note != null && x.Note.Contains("1 table was locked for a schema change", StringComparison.Ordinal),
                "the index checks left public.jobs out, as the sweep's one read of the lock table found it under ACCESS EXCLUSIVE");
            probe.StatementsObserved.Should().BePositive("no locks is not nothing ran");
            probe.BeyondCatalogReads.Should().BeEmpty("the lock table read, like every health read, locks nothing it lists (D27)");

            // C12, judged as of ten seconds past the read: idle-head, the holder named, the terminate statement handed over.
            BlockingChainFacts facts = await GatherAsync(host);
            HealthFinding finding = new BlockingChainCheck().Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddSeconds(15), text: PostgresStudioText.Instance))
                .Findings.Should().ContainSingle().Subject;

            finding.Variant.Should().Be(BlockingChainCheck.IdleHead);
            finding.Evidence.Single(static x => x.Label == "Sessions held up").Value.Should().Be("2");
            finding.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().StartWith("pid " + holder.ProcessID)
                .And.Contain("idle in transaction").And.Contain("AccessExclusiveLock");
            finding.Evidence.Single(static x => x.Label == "Hand over 1").Value.Should().StartWith("select pg_terminate_backend(" + holder.ProcessID + ");");
            finding.Evidence.Should().NotContain(static x => x.Value.Contains("payroll", StringComparison.Ordinal) || x.Value.Contains("salaries", StringComparison.Ordinal),
                "a finding names no relation Postgres Studio does not own; the tab names it, masked");
            finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
            finding.Remedy.Code.Should().EndWith("idle_in_transaction_session_timeout = '5min'");

            // Sessions › Blocking, past the gate: one tree, the head and both waiters, the withheld schema's relation masked.
            await using (StudioCircuit admin = host.Circuit("admin"))
            {
                ISessionActivityService sessions = admin.Get<ISessionActivityService>();

                SessionActivitySummary summary = await sessions.GetSummaryAsync(Scope, Token);
                summary.Blocking.Should().Be(summary.Blocking with { Chains = 1, Held = 2, IdleHeads = 1, Unrooted = 0 });

                SessionActivityDetails details = await sessions.GetDetailsAsync(Scope, Token);
                details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);

                BlockingChainView chain = details.Blocking.Should().ContainSingle("one head, two waiters: one tree").Subject;
                chain.HeadWaits.Should().BeFalse();
                chain.IdleHead.Should().BeTrue();
                chain.Head.ProcessId.Should().Be(holder.ProcessID);
                chain.Head.State.Should().Be("idle in transaction");
                chain.Head.HeldModes.Should().Contain("AccessExclusiveLock");
                chain.Terminate.Should().Be(SessionHandOver.Terminate(holder.ProcessID));
                chain.Cancel.Should().BeNull("cancel does nothing to a session idle in its transaction");

                chain.Head.Waiters.Select(static x => x.ProcessId).Should().BeEquivalentTo([jobs.ProcessID, salaries.ProcessID]);
                BlockingNodeView onJobs = chain.Head.Waiters.Single(x => x.ProcessId == jobs.ProcessID);
                onJobs.Relation.Should().Be("public.jobs");
                onJobs.WantedMode.Should().Be("AccessShareLock");
                onJobs.Application.Should().Be(JobsWaiter);

                BlockingNodeView onSalaries = chain.Head.Waiters.Single(x => x.ProcessId == salaries.ProcessID);
                onSalaries.Relation.Should().Be(WithheldNames.Token, "payroll is not among the schemas the visitor may see (D27)");
                onSalaries.Target.Should().NotContain("payroll").And.NotContain("salaries");
            }

            host.Logs.WarningsOrWorse.Should().BeEmpty("a finding is what the page says; the log is for failures: {0}",
                string.Join("; ", host.Logs.WarningsOrWorse.Select(static x => x.EventId + " " + x.Message)));

            // Without the write policy's yes: the counts, and no tree.
            await using (StudioCircuit viewer = host.Circuit("viewer"))
            {
                ISessionActivityService sessions = viewer.Get<ISessionActivityService>();

                (await sessions.GetSummaryAsync(Scope, Token)).Blocking.Chains.Should().Be(1, "the counts name nobody");
                SessionActivityDetails refused = await sessions.GetDetailsAsync(Scope, Token);
                refused.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
                refused.Blocking.Should().BeEmpty("the tree is a session detail");
            }

            host.Logs.WarningsOrWorse.Should().ContainSingle("the viewer's refusal is audited, and logged as every refusal is")
                .Which.Message.Should().Contain("viewer");

        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            (await jobsWait).Should().Be(10L, "the waiter ran once the holder let go");
            (await salariesWait).Should().Be(10L);
            await jobs.DisposeAsync();
            await salaries.DisposeAsync();
        }
    }

    [PostgresFact]
    [HealthFires("C12")]
    [HealthQuiet("C12")]
    public async Task A_running_heads_chain_is_quiet_under_two_minutes_and_a_long_chain_from_then_on()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_x3_blocking_running"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], string.Join(";\n", Setup), Token);

        // The head runs one statement that takes the lock and keeps running: active, not idle.
        await using NpgsqlConnection head = new(new NpgsqlConnectionStringBuilder(ServerSnapshot.Unpooled(owned[0])) { ApplicationName = "ps_x3_head" }.ConnectionString);
        await head.OpenAsync(Token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using NpgsqlCommand running = new("do $$ begin lock table public.jobs in access exclusive mode; perform pg_sleep(300); end $$", head) { CommandTimeout = 0 };
        Task headRuns = running.ExecuteNonQueryAsync(stop.Token);

        await HeadHoldsAsync(owned[0], "ps_x3_head");
        (NpgsqlConnection waiter, Task<object?> waiting) = await WaitBehindAsync(owned[0], JobsWaiter, "select count(*) from public.jobs");

        try
        {
            await LiveLocks.UntilWaitingAsync(owned[0], JobsWaiter, Token);

            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])),
                static o => o.Capabilities = PostgresStudioCapabilities.All());

            BlockingChainFacts facts = await GatherAsync(host);
            BlockingChainCheck check = new();

            HealthVerdict now = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt, text: PostgresStudioText.Instance));
            now.Findings.Should().BeEmpty("a few seconds behind a running statement is a lock wait, not yet a long chain");
            now.Note.Should().Contain("2 min");

            HealthFinding later = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(2), text: PostgresStudioText.Instance))
                .Findings.Should().ContainSingle().Subject;

            later.Variant.Should().Be(BlockingChainCheck.LongChain);
            later.Severity.Should().Be(HealthSeverity.Warning);
            later.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().StartWith("pid " + head.ProcessID).And.Contain("active");
            later.Remedy!.Kind.Should().Be(HealthRemedyKind.Repair);
            later.Remedy.Code.Should().Contain(SessionHandOver.Cancel(head.ProcessID)).And.Contain(SessionHandOver.Terminate(head.ProcessID));

            await using StudioCircuit admin = host.Circuit("admin");
            BlockingChainView chain = (await admin.Get<ISessionActivityService>().GetDetailsAsync(Scope, Token)).Blocking.Should().ContainSingle().Subject;
            chain.Head.ProcessId.Should().Be(head.ProcessID);
            chain.Cancel.Should().Be(SessionHandOver.Cancel(head.ProcessID), "a head running a statement may have it cancelled");
        }
        finally
        {
            // The test ends its own head - its statement's token - and the studio never did.
            await stop.CancelAsync();
            Func<Task> ended = () => headRuns;
            await ended.Should().ThrowAsync<Exception>();
            (await waiting).Should().Be(10L, "the waiter ran once the head's statement was cancelled");
            await waiter.DisposeAsync();
        }
    }

    [PostgresFact]
    [HealthQuiet("C12")]
    public async Task A_session_idle_in_its_transaction_that_blocks_nobody_is_no_chain_and_C12_is_quiet()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_x3_blocking_quiet"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], string.Join(";\n", Setup), Token);

        await using NpgsqlConnection holder = new(ServerSnapshot.Unpooled(owned[0]));
        await holder.OpenAsync(Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("update public.jobs set id = id where id = 1", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        try
        {
            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])),
                static o => o.Capabilities = PostgresStudioCapabilities.All());

            HealthSweep sweep = await SweepAsync(host);
            HealthCheckResult c12 = sweep.Results.Single(static x => x.Check.Code == "C12");

            c12.Outcome.Should().Be(HealthCheckOutcome.Checked, c12.Problem?.Reason);
            c12.Findings.Should().BeEmpty();
            c12.Note.Should().StartWith("No blocking chain's head");
            c12.Statements.Should().ContainSingle(static x => x.Sql.Contains("from pg_locks", StringComparison.Ordinal),
                "C12's Show SQL is the sweep's one read of the lock table");

            BlockingChainFacts facts = await GatherAsync(host);
            new BlockingChainCheck().Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddHours(1), text: PostgresStudioText.Instance))
                .Findings.Should().BeEmpty("a transaction that blocks nobody is C04's, never a chain - however long it sits");

            await using StudioCircuit admin = host.Circuit("admin");
            ISessionActivityService sessions = admin.Get<ISessionActivityService>();
            (await sessions.GetSummaryAsync(Scope, Token)).Blocking.Should().Be(SessionBlockingCounts.None);
            (await sessions.GetDetailsAsync(Scope, Token)).Blocking.Should().BeEmpty();
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>Opens a session of <paramref name="application" /> and starts <paramref name="sql" />, which will wait.</summary>
    private static async Task<(NpgsqlConnection Connection, Task<object?> Waiting)> WaitBehindAsync(string connectionString, string application, string sql)
    {
        NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(ServerSnapshot.Unpooled(connectionString)) { ApplicationName = application }.ConnectionString);
        await connection.OpenAsync(Token);

        NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        return (connection, RunAsync(command));

        static async Task<object?> RunAsync(NpgsqlCommand command)
        {
            await using (command)
            {
                return await command.ExecuteScalarAsync(Token);
            }
        }
    }

    /// <summary>Waits until the session of <paramref name="application" /> holds a granted relation lock: its statement has the lock.</summary>
    private static async Task HeadHoldsAsync(string connectionString, string application)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        await using NpgsqlConnection probe = new(ServerSnapshot.Unpooled(connectionString));
        await probe.OpenAsync(deadline.Token);

        do
        {
            await using NpgsqlCommand command = new(
                "select exists (select 1 from pg_catalog.pg_locks l join pg_catalog.pg_stat_activity a on a.pid = l.pid "
                + "where l.granted and l.mode = 'AccessExclusiveLock' and l.locktype = 'relation' and a.application_name = @application)",
                probe);
            command.Parameters.AddWithValue("application", application);

            if ((bool) (await command.ExecuteScalarAsync(deadline.Token))!)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(deadline.Token));
    }

    /// <summary>A regular sweep of the host's one source, resolved as a circuit would.</summary>
    private static Task<HealthSweep> SweepAsync(StudioHost host) => host.CircuitAsync("admin", async circuit =>
    {
        ResolvedSource resolved = await circuit.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
        return await host.Services.GetRequiredService<HealthSweeper>().SweepAsync(resolved, HealthSweepKind.Regular, Token);
    });

    /// <summary>C12's own gatherer, in a context of its own over the host's source - so its facts can be judged at a later moment.</summary>
    private static Task<BlockingChainFacts> GatherAsync(StudioHost host) => host.CircuitAsync("admin", async circuit =>
    {
        ResolvedSource resolved = await circuit.GetRequiredService<StudioSourceResolver>().ResolveAsync(Scope, null, Token);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await using HealthConnection connection = new(resolved.Target);
        HealthCheckContext context = new(
            resolved,
            host.Services.GetRequiredService<IStudioSettings>(),
            NoStudioRuntime.Instance,
            scope.ServiceProvider,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), observer: null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        return await new BlockingChainCheck().GatherAsync(context, Token);
    });
}
