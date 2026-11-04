using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Statements;
using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>Records what the what-if engine did, by backend; holds a trial open, or cancels one, when a test asks.</summary>
internal sealed class AdvisorRecorder : IHypotheticalIndexObserver
{
    private readonly ConcurrentQueue<HypotheticalIndexCleanup> cleanups = new();
    private readonly ConcurrentQueue<int> processIds = new();
    private readonly ConcurrentQueue<HypotheticalIndexMade> made = new();

    /// <summary>When set, the first trial waits here after hypopg made its index.</summary>
    public TaskCompletionSource? Hold { get; init; }

    /// <summary>When set, cancelled as soon as hypopg made an index: the next command of the trial is cancelled.</summary>
    public CancellationTokenSource? CancelAfterMade { get; init; }

    public IReadOnlyList<HypotheticalIndexCleanup> Cleanups => [.. cleanups];

    public IReadOnlyList<int> ProcessIds => [.. processIds];

    public IReadOnlyList<HypotheticalIndexMade> Made => [.. made];

    public async Task MadeAsync(int processId, HypotheticalIndexMade index, CancellationToken cancellationToken)
    {
        made.Enqueue(index);

        if (CancelAfterMade is { } cancel)
        {
            await cancel.CancelAsync();
            return;
        }

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }
    }

    public void Finished(int processId, HypotheticalIndexCleanup cleanup)
    {
        processIds.Enqueue(processId);
        cleanups.Enqueue(cleanup);
    }
}

/// <summary>
/// 1.2-INDEX-A: the index advisor's engine against a real hypopg (<see cref="HypopgServer" />): one connection, a read-only
/// transaction per candidate, hypopg reset as each one's last statement; a trial Postgres refuses is recorded and the next still
/// runs; a reset that fails ends the session and the run; the caller's cancellation resets; the deadline stops between trials.
/// </summary>
public class HypotheticalWorkloadLiveTests(HypopgServer server) : IClassFixture<HypopgServer>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<string> SeedAsync(string name)
    {
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync(name, hypopg: true, Token);
        await StatementsLive.ExecuteAsync(
            server.ConnectionString("app", database),
            "create table public.t (id bigint primary key, a int, b int, c int); "
            + "insert into public.t select g, g % 1000, g % 7, g % 13 from pg_catalog.generate_series(1, 100000) g; analyze public.t; "
            + "create table public.secret (id int primary key, v int); insert into public.secret select g, g from pg_catalog.generate_series(1, 10) g");
        await server.ExecuteAsync("grant select on public.t to console_role", database, Token);

        return database;
    }

    private StudioHost Host(string database, AdvisorRecorder recorder, string? consoleRole = null, string role = "app") =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(server.ConnectionString(role, database, maxPoolSize: 1)));
                services.AddSingleton<IHypotheticalIndexObserver>(recorder);
            },
            options =>
            {
                options.Capabilities = PostgresStudioCapabilities.All();
                options.SqlConsoleRole = consoleRole;
            });

    private static HypotheticalIndex Index(string column) => PlanWhatIfCandidates.Candidate("public", "t", [column])!;

    private static HypotheticalWorkloadPlanner Planner(IReadOnlyList<string> statements, Func<int, Task>? before = null) =>
        new(
            PrepareAsync: (_, _, _) => Task.CompletedTask,
            PlanAsync: async (i, connection, transaction, token) =>
            {
                if (before is not null)
                {
                    await before(i);
                }

                (string? json, _) = await StatementPlanQueries.ReadPlanAsync(
                    connection, transaction, StatementPlanQueries.GenericPlan(statements[i], false)!, ExplainStatements.MaxPlanCharacters, 35, token);
                return json;
            },
            AfterFailureAsync: async connection =>
            {
                // The drill-in's own: the statement a failed batch left prepared on the session is taken back.
                try
                {
                    await using NpgsqlCommand forget = new(StatementPlanQueries.Deallocate(), connection);
                    await forget.ExecuteNonQueryAsync(Token);
                }
                catch (PostgresException)
                {
                }
            });

    private static async Task<long> HypotheticalCountAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select count(*) from public.hypopg()", connection);
        return (long) (await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<IStudioDatabase> DatabaseAsync(StudioCircuit circuit) =>
        (await circuit.Get<StudioSourceResolver>().ResolveAsync(Scope, null, Token)).Target;

    [PostgresFact]
    public async Task Three_trials_on_one_connection_each_reset_and_a_fresh_session_on_the_same_backend_sees_no_hypothetical_index()
    {
        string database = await SeedAsync("adv_engine_three");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        HypotheticalIndexReader reader = host.Services.GetRequiredService<HypotheticalIndexReader>();
        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        HypotheticalWorkloadRead read = await reader.ReadTrialsAsync(
            await DatabaseAsync(circuit),
            [new HypotheticalIndexTrial(Index("b"), [0]), new HypotheticalIndexTrial(Index("a"), [0]), new HypotheticalIndexTrial(Index("c"), [0])],
            Planner(["select * from public.t where b = 5"]),
            DateTimeOffset.MaxValue,
            TimeProvider.System,
            Token);

        read.Stopped.Should().Be(HypotheticalWorkloadStop.None);
        read.Trials.Should().HaveCount(3);
        read.Trials[0].SqlState.Should().BeNull();
        WhatIfResult.Reads(QueryPlanReader.Read(read.Trials[0].After[0]!).Plan, read.Trials[0].Made!.Name).Should().BeTrue("the plan under the index on b reads it");
        WhatIfResult.Reads(QueryPlanReader.Read(read.Trials[1].After[0]!).Plan, read.Trials[1].Made!.Name).Should().BeFalse("an index on a does not serve b = 5");

        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.Reset, HypotheticalIndexCleanup.Reset, HypotheticalIndexCleanup.Reset);
        recorder.ProcessIds.Distinct().Should().ContainSingle("one connection for the whole run");
        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "nothing is created");

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().Be(recorder.ProcessIds[0], "the pool holds one connection: the next user is the run's own backend");
        (await HypotheticalCountAsync(next)).Should().Be(0);
    }

    [PostgresFact]
    public async Task A_trial_whose_plan_fails_resets_and_the_next_trial_still_runs()
    {
        string database = await SeedAsync("adv_engine_fails");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder, consoleRole: "console_role", role: "console_login");
        await using StudioCircuit circuit = host.Circuit();
        HypotheticalIndexReader reader = host.Services.GetRequiredService<HypotheticalIndexReader>();

        HypotheticalWorkloadRead read = await reader.ReadTrialsAsync(
            await DatabaseAsync(circuit),
            [new HypotheticalIndexTrial(Index("b"), [0]), new HypotheticalIndexTrial(Index("a"), [1]), new HypotheticalIndexTrial(Index("c"), [0])],
            Planner(["select * from public.t where b = 5", "select * from public.secret"]),
            DateTimeOffset.MaxValue,
            TimeProvider.System,
            Token);

        read.Trials.Select(static x => x.SqlState).Should().Equal(null, "42501", null);
        read.Stopped.Should().Be(HypotheticalWorkloadStop.None);
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.Reset, HypotheticalIndexCleanup.ResetAfterFailure, HypotheticalIndexCleanup.Reset);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        (await HypotheticalCountAsync(next)).Should().Be(0);
    }

    [PostgresFact]
    public async Task A_reset_that_fails_ends_the_session_and_the_run_with_SessionEnded()
    {
        string database = await SeedAsync("adv_engine_reset");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        HypotheticalIndexReader reader = host.Services.GetRequiredService<HypotheticalIndexReader>();
        reader.ResetSqlOverride = "select public.hypopg_reset_that_is_not_there()";
        await using StudioCircuit circuit = host.Circuit();

        HypotheticalWorkloadRead read = await reader.ReadTrialsAsync(
            await DatabaseAsync(circuit),
            [new HypotheticalIndexTrial(Index("b"), [0]), new HypotheticalIndexTrial(Index("a"), [0]), new HypotheticalIndexTrial(Index("c"), [0])],
            Planner(["select * from public.t where b = 5"]),
            DateTimeOffset.MaxValue,
            TimeProvider.System,
            Token);

        read.Stopped.Should().Be(HypotheticalWorkloadStop.SessionEnded);
        read.Trials.Should().ContainSingle().Which.SqlState.Should().Be("42883");
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.SessionEnded);

        int ended = recorder.ProcessIds.Single();
        await StorageLive.UntilAsync(async () => !await server.BackendExistsAsync(ended, Token), TimeSpan.FromSeconds(15), "backend " + ended + " to exit");
    }

    [PostgresFact]
    public async Task The_callers_cancellation_in_the_middle_of_a_trial_resets_hypopg_before_it_throws()
    {
        string database = await SeedAsync("adv_engine_cancel");
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        AdvisorRecorder recorder = new() { CancelAfterMade = cancel };
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        HypotheticalIndexReader reader = host.Services.GetRequiredService<HypotheticalIndexReader>();
        IStudioDatabase target = await DatabaseAsync(circuit);

        Func<Task> run = () => reader.ReadTrialsAsync(
            target,
            [new HypotheticalIndexTrial(Index("b"), [0]), new HypotheticalIndexTrial(Index("a"), [0])],
            Planner(["select * from public.t where b = 5"]),
            DateTimeOffset.MaxValue,
            TimeProvider.System,
            cancel.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();

        recorder.Made.Should().ContainSingle("the second trial never started");
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.ResetAfterFailure);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        (await HypotheticalCountAsync(next)).Should().Be(0);
    }

    [PostgresFact]
    public async Task The_deadline_is_checked_between_trials_and_stops_the_run_with_what_it_has()
    {
        string database = await SeedAsync("adv_engine_deadline");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        HypotheticalIndexReader reader = host.Services.GetRequiredService<HypotheticalIndexReader>();
        Clock clock = new(DateTimeOffset.UnixEpoch);

        HypotheticalWorkloadRead read = await reader.ReadTrialsAsync(
            await DatabaseAsync(circuit),
            [new HypotheticalIndexTrial(Index("b"), [0]), new HypotheticalIndexTrial(Index("a"), [0]), new HypotheticalIndexTrial(Index("c"), [0])],
            Planner(["select * from public.t where b = 5"], before: _ =>
            {
                clock.Now = DateTimeOffset.UnixEpoch.AddMinutes(2);
                return Task.CompletedTask;
            }),
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            clock,
            Token);

        read.Stopped.Should().Be(HypotheticalWorkloadStop.Budget);
        read.Trials.Should().ContainSingle("a trial in flight is finished; none is started after the deadline");
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.Reset);
    }
}

/// <summary>
/// 1.2-INDEX-A: the Statements page's index advisor against a real hypopg: it recommends the index a selective unindexed filter
/// needs, with the remedy and the statements it helps, and creates nothing and holds no lock; it plans nothing without hypopg or
/// without the privilege; it never tries another role's statement, a statement in a schema the visitor may not see, or an index on
/// a partitioned table; one run per tab and per database; and the visitor's own columns must be ones the plans compare.
/// </summary>
public class IndexAdvisorLiveTests(HypopgServer server) : IClassFixture<HypopgServer>
{
    private const string OrdersStatement = "select pg_catalog.pg_sleep(0.01), o.note from public.orders o where o.customer = @id";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(string Database, string App)> SeedAsync(string name, bool hypopg = true)
    {
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync(name, hypopg, Token);
        string app = server.ConnectionString("app", database);

        await StatementsLive.ExecuteAsync(
            app,
            "create table public.orders (id int primary key, note text, customer int); "
            + "insert into public.orders select g, 'n' || g, g % 1000 from pg_catalog.generate_series(1, 50000) g; analyze public.orders");
        await StatementsLive.RepeatAsync(app, OrdersStatement, 30);

        return (database, app);
    }

    private StudioHost Host(string database, AdvisorRecorder recorder, Action<PostgresStudioOptions>? studio = null, string role = "app", int? pool = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(server.ConnectionString(role, database, pool)));
                services.AddSingleton<IHypotheticalIndexObserver>(recorder);
            },
            options =>
            {
                options.Capabilities = PostgresStudioCapabilities.All();
                studio?.Invoke(options);
            });

    private static IEnumerable<string> Words(IndexAdviceView view) =>
        view.NotTried.Concat(view.Skipped).Concat(view.ComparedColumns)
            .Concat(view.Recommended.Concat(view.NotUsed).SelectMany(static x => new[] { x.Index.Caption, x.Remedy ?? string.Empty, x.FoldedNote ?? string.Empty }))
            .Concat([view.Reason ?? string.Empty, view.Stopped ?? string.Empty, view.AuditOutcome ?? string.Empty]);

    [PostgresFact]
    public async Task A_selective_unindexed_filter_is_recommended_with_its_remedy_and_nothing_is_created_or_left_locked()
    {
        (string database, _) = await SeedAsync("adv_live_recommend");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder, pool: 1);
        await using StudioCircuit circuit = host.Circuit();
        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Reason.Should().BeNull();
        view.Unavailable.Should().BeNull();
        IndexAdviceItem item = view.Recommended.Should().ContainSingle().Subject;
        item.Index.Definition.Should().Be("CREATE INDEX ON \"public\".\"orders\" (\"customer\");");
        item.Remedy.Should().Be("CREATE INDEX CONCURRENTLY ON \"public\".\"orders\" (\"customer\");");
        item.Saved.Should().BeGreaterThan(0);
        item.Helped.Should().BeGreaterThanOrEqualTo(1);
        item.Statements.Should().Contain(static x => x.Used && x.After < x.Before);
        item.TableWrites.Should().BeGreaterThan(0);
        view.Statements.Should().NotBeEmpty("Show SQL");
        view.Workload.Should().BeGreaterThan(0);

        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "an imagined index is no catalog row");
        recorder.Cleanups.Should().OnlyContain(static x => x == HypotheticalIndexCleanup.Reset);
        recorder.Cleanups.Should().NotBeEmpty();

        await using (NpgsqlConnection admin = await server.OpenSuperuserAsync(database, Token))
        {
            await using NpgsqlCommand locks = new(
                "select count(*) from pg_catalog.pg_locks l join pg_catalog.pg_class c on c.oid = l.relation "
                + "where c.relname = 'orders' and l.pid <> pg_catalog.pg_backend_pid()", admin);
            ((long) (await locks.ExecuteScalarAsync(Token))!).Should().Be(0, "no transaction of the advisor is left holding a lock");
        }

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().Be(recorder.ProcessIds[^1]);
        await using NpgsqlCommand count = new("select count(*) from public.hypopg()", next);
        ((long) (await count.ExecuteScalarAsync(Token))!).Should().Be(0);

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == StatementsService.IndexAdviceAction).Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        entry.Message.Should().Contain("would help").And.NotContain("orders").And.NotContain("customer");
        entry.Target.Should().StartWith("the top ").And.NotContain("orders");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Without_hypopg_the_sentence_is_the_answer_and_nothing_is_planned_or_imagined()
    {
        (string database, _) = await SeedAsync("adv_live_without", hypopg: false);
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Unavailable.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence);
        view.Recommended.Should().BeEmpty();
        view.StatementsTried.Should().Be(0);
        recorder.Made.Should().BeEmpty();
        recorder.Cleanups.Should().BeEmpty();
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == StatementsService.IndexAdviceAction).Which.Message.Should().Be("Not run: hypopg is not installed.");
    }

    [PostgresFact]
    public async Task A_role_without_the_privilege_to_use_hypopg_is_told_so()
    {
        (string database, _) = await SeedAsync("adv_live_privilege");
        await server.ExecuteAsync(
            "revoke execute on function public.hypopg_create_index(text) from public; grant select on public.orders to console_role",
            database,
            Token);
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder, static o => o.SqlConsoleRole = "console_role", role: "console_login");
        await using StudioCircuit circuit = host.Circuit();

        IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Unavailable.Should().Be(HypotheticalIndexAvailability.NoPrivilegeSentence);
        recorder.Made.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Another_roles_statement_is_never_among_those_tried()
    {
        (string database, _) = await SeedAsync("adv_live_fence");
        await server.ExecuteAsync("create role rival login password 'rival'; grant select on public.orders to rival", database, Token);
        await StatementsLive.RepeatAsync(
            server.ConnectionString("rival", database),
            "select pg_catalog.pg_sleep(0.01), o.id, 'rivalmark'::text as marker from public.orders o where o.customer = @id",
            30);

        long rivalQueryId;

        await using (NpgsqlConnection admin = await server.OpenSuperuserAsync(database, Token))
        {
            await using NpgsqlCommand find = new(
                "select s.queryid from pg_stat_statements s join pg_roles r on r.oid = s.userid where r.rolname = 'rival' and s.query like '%o.id%' limit 1",
                admin);
            rivalQueryId = (long) (await find.ExecuteScalarAsync(Token))!;
        }

        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Recommended.Should().ContainSingle();
        view.Recommended.Concat(view.NotUsed).SelectMany(static x => x.Statements).Select(static x => x.QueryId).Should().NotContain(rivalQueryId);
    }

    [PostgresFact]
    public async Task A_statement_in_a_schema_the_visitor_may_not_see_is_never_planned_and_never_named_and_an_admitted_visitor_gets_it()
    {
        (string database, string app) = await SeedAsync("adv_live_withheld");
        await StatementsLive.ExecuteAsync(
            app,
            "create schema hr; create table hr.salaries (id int primary key, amount int, who int); "
            + "insert into hr.salaries select g, g, g % 1000 from pg_catalog.generate_series(1, 50000) g; analyze hr.salaries");
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.05), s.amount from hr.salaries s where s.who = @id", 30);

        AdvisorRecorder withheldRecorder = new();

        await using (StudioHost withheld = Host(database, withheldRecorder))
        {
            await using StudioCircuit circuit = withheld.Circuit();
            IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

            Words(view).Should().NotContain(static x => x.Contains("salaries", StringComparison.OrdinalIgnoreCase) || x.Contains("hr.", StringComparison.Ordinal));
            view.Skipped.Should().Contain(static x => x.Contains("not shown to you", StringComparison.Ordinal));
            view.Recommended.Should().ContainSingle().Which.TableKey.Should().Be("public.orders");
        }

        AdvisorRecorder admittedRecorder = new();

        await using StudioHost admitted = Host(database, admittedRecorder, static o =>
        {
            o.BrowsableSchemas.Add("public");
            o.BrowsableSchemas.Add("hr");
        });
        await using StudioCircuit admittedCircuit = admitted.Circuit();
        IndexAdviceView seen = await admittedCircuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        seen.Recommended.Select(static x => x.TableKey).Should().Contain("hr.salaries");
    }

    [PostgresFact]
    public async Task A_partitioned_tables_candidate_is_not_tried()
    {
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("adv_live_partition", hypopg: true, Token);
        string app = server.ConnectionString("app", database);
        await StatementsLive.ExecuteAsync(
            app,
            "create table public.events (id int, region int, v int) partition by range (id); "
            + "create table public.events_p1 partition of public.events for values from (0) to (100000); "
            + "insert into public.events select g, g % 1000, g % 500 from pg_catalog.generate_series(1, 50000) g; analyze public.events");
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.01), e.v from public.events e where e.region = @id", 30);

        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        IndexAdviceView view = await circuit.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Recommended.Should().BeEmpty();
        view.NotTried.Should().Contain(static x => x.Contains("partition", StringComparison.OrdinalIgnoreCase));
        recorder.Made.Should().BeEmpty("hypopg was never asked");
    }

    [PostgresFact]
    public async Task One_run_at_a_time_in_a_tab_and_on_a_database()
    {
        (string database, _) = await SeedAsync("adv_live_flights");
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AdvisorRecorder recorder = new() { Hold = hold };
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit first = host.Circuit();
        await using StudioCircuit other = host.Circuit("someone-else");

        Task<IndexAdviceView> running = first.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        try
        {
            await StorageLive.UntilAsync(() => Task.FromResult(recorder.Made.Count > 0), TimeSpan.FromSeconds(60), "the first run to hold a trial open");

            IndexAdviceView sameTab = await first.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);
            sameTab.Reason.Should().Be(IndexAdvisor.AlreadyRunningInTab);

            IndexAdviceView otherTab = await other.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);
            otherTab.Reason.Should().Be(IndexAdvisor.AlreadyRunningForDatabase, "refused, never joined");
        }
        finally
        {
            hold.TrySetResult();
        }

        (await running).Recommended.Should().ContainSingle();
        host.Services.GetRequiredService<IndexAdvisorFlights>().Count.Should().Be(0);

        IndexAdviceView again = await other.Get<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);
        again.Reason.Should().BeNull("the database is free again");
    }

    [PostgresFact]
    public async Task The_visitors_own_columns_must_be_ones_the_plans_compare_and_are_tried_when_they_are()
    {
        (string database, _) = await SeedAsync("adv_live_own");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        IStatementsService statements = circuit.Get<IStatementsService>();

        IndexAdviceView first = await statements.AdviseIndexesAsync(Scope, new IndexAdviceRequest { Table = "public.orders" }, Token);
        first.ComparedColumns.Should().Contain("customer").And.NotContain("note");

        IndexAdviceView refused = await statements.AdviseIndexesAsync(Scope, new IndexAdviceRequest { Table = "public.orders", Columns = ["note"] }, Token);
        refused.Reason.Should().Be("Not tried: note is not a column these statements compare.");
        int madeAfterRefusal = recorder.Made.Count;

        IndexAdviceView own = await statements.AdviseIndexesAsync(Scope, new IndexAdviceRequest { Table = "public.orders", Columns = ["customer"] }, Token);
        own.Reason.Should().BeNull();
        own.Recommended.Should().ContainSingle().Which.Own.Should().BeTrue();
        recorder.Made.Count.Should().BeGreaterThan(madeAfterRefusal);

        IndexAdviceView nobody = await statements.AdviseIndexesAsync(Scope, new IndexAdviceRequest { Table = "public.nowhere" }, Token);
        nobody.Reason.Should().Be(IndexAdvisor.NoStatementNamesTable);
    }
}
