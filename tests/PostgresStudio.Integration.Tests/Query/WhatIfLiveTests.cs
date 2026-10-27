using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Statements;
using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// X2 against a real hypopg (1.4.x on PostgreSQL 17, <see cref="HypopgServer" />): the console's and the Statements drill-in's
/// what-if plan the statement under a hypothetical index and the plan reads <c>&lt;oid&gt;btree_…</c>; nothing is created; the
/// pooled connection's next user - the same backend - sees no hypothetical index; a what-if that fails half-way still resets;
/// one whose reset fails ends its own session, which then never reaches the pool; and the role that may not use hypopg is told
/// so.
/// </summary>
public class WhatIfLiveTests(HypopgServer server) : IClassFixture<HypopgServer>
{
    private const string Statement = "select * from public.t where b = 5";

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A database of the test's own with PROBE's table: 200,000 rows, an index on <c>a</c> and none on <c>b</c>, owned by
    /// <c>app</c> and analyzed.
    /// </summary>
    private async Task<(string Database, string App)> SeedAsync(string name, bool hypopg = true)
    {
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync(name, hypopg, Token);
        string app = server.ConnectionString("app", database);

        await StatementsLive.ExecuteAsync(app,
            "create table public.t (id bigint primary key, a int, b int, c text); "
            + "insert into public.t select g, g % 1000, g % 7, pg_catalog.md5(g::text) from pg_catalog.generate_series(1, 200000) g; "
            + "create index t_a on public.t (a); "
            + "analyze public.t");

        return (database, app);
    }

    /// <summary>A host over one data source whose pool holds one connection, so the next user is the what-if's own backend.</summary>
    private StudioHost Host(string database, Recorder recorder, Action<PostgresStudioOptions>? studio = null, string role = "app") =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(server.ConnectionString(role, database, maxPoolSize: 1)));
                services.AddSingleton<IHypotheticalIndexObserver>(recorder);
            },
            options =>
            {
                options.Capabilities = PostgresStudioCapabilities.All();
                studio?.Invoke(options);
            });

    private static async Task<HypotheticalIndex> SuggestedAsync(ISqlConsoleService console)
    {
        SqlConsoleResult planned = await console.RunSqlAsync(Scope, new SqlConsoleRequest(Statement) { Explain = SqlExplain.Plan }, Token);
        planned.Error.Should().BeNull();

        return HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(planned.PlanJson).Plan!).Should().ContainSingle().Subject;
    }

    /// <summary>How many hypothetical indexes the backend behind <paramref name="connection" /> holds: <c>hypopg()</c>, which any role may call.</summary>
    private static async Task<long> HypotheticalCountAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select count(*) from public.hypopg()", connection);
        return (long) (await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string> PlanTextAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("explain " + Statement, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        List<string> lines = [];

        while (await reader.ReadAsync(Token))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join("\n", lines);
    }

    [PostgresFact]
    public async Task The_console_plans_the_statement_under_the_index_its_plan_suggests_and_the_pools_next_user_sees_no_hypothetical_index()
    {
        (string database, _) = await SeedAsync("x2_console");
        Recorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex index = await SuggestedAsync(console);
        index.Definition.Should().Be("CREATE INDEX ON \"t\" (\"b\");", "a plain plan names no schema, so the table resolves as the statement's did");

        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);
        (long Rows, long HighestOid) classBefore = await server.PgClassAsync(database, Token);

        WhatIfResult result = await console.WhatIfAsync(Scope, Statement, index, Token);

        result.Reason.Should().BeNull();
        result.Availability.Should().Be(HypotheticalIndexState.Available);
        result.IndexName.Should().MatchRegex("^<[0-9]+>btree_t_b$", "hypopg names a hypothetical index <oid>btree_<table>_<column>");
        result.Used.Should().BeTrue();
        result.After!.Nodes.Should().Contain(x => x.IndexName == result.IndexName);
        result.Before!.Nodes.Should().Contain(static x => x.NodeType == "Seq Scan" && x.RelationName == "t");
        result.After.Root.TotalCost.Should().BeLessThan(result.Before.Root.TotalCost!.Value);
        result.Verdict.Should().StartWith("Postgres would use it: the plan's estimated cost goes from ");

        // Nothing created: no object, no catalog row.
        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "a hypothetical index is no catalog row");
        (await server.PgClassAsync(database, Token)).Should().Be(classBefore, "pg_class is unchanged: not a row, not an oid");

        // The reset proof: the pool's one connection is the what-if's own backend, and its planner sees no hypothetical index.
        recorder.Cleanups.Should().Equal([HypotheticalIndexCleanup.Reset], "the reset was the transaction's last statement");
        await using (NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token))
        {
            next.ProcessID.Should().Be(recorder.ProcessIds.Single(), "the pool holds one connection: the next user is the same backend");
            (await HypotheticalCountAsync(next)).Should().Be(0);
            (await PlanTextAsync(next)).Should().Contain("Seq Scan").And.NotContain("btree_t_b");
        }

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == SqlConsoleService.WhatIfAction).Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
        entry.Target.Should().StartWith("explain (format json) select * from public.t", "audited as the console audits a run: by the statement");
        entry.Message.Should().Contain("would be used").And.NotContain("t_b", "the outcome names no index");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_what_if_that_fails_half_way_still_resets_before_the_connection_goes_back()
    {
        (string database, _) = await SeedAsync("x2_halfway");
        Recorder recorder = new() { FailAfterMade = true };
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex index = await SuggestedAsync(console);
        WhatIfResult result = await console.WhatIfAsync(Scope, Statement, index, Token);

        result.Before.Should().BeNull();
        result.Reason.Should().Be("Not compared: the what-if failed (InvalidOperationException).");
        recorder.Made.Should().ContainSingle("hypopg made the index before the failure");
        recorder.Cleanups.Should().Equal([HypotheticalIndexCleanup.ResetAfterFailure]);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().Be(recorder.ProcessIds.Single(), "the same backend went back to the pool");
        (await HypotheticalCountAsync(next)).Should().Be(0, "the failure path reset it on the same connection");
        (await PlanTextAsync(next)).Should().NotContain("btree_t_b");

        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == SqlConsoleService.WhatIfAction && !x.Succeeded);
    }

    [PostgresFact]
    public async Task A_what_if_whose_reset_fails_ends_its_own_session_so_the_backend_never_reaches_the_pool()
    {
        (string database, _) = await SeedAsync("x2_reset_fails");
        Recorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        host.Services.GetRequiredService<HypotheticalIndexReader>().ResetSqlOverride = "select public.hypopg_reset_that_is_not_there()";
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex index = await SuggestedAsync(console);
        WhatIfResult result = await console.WhatIfAsync(Scope, Statement, index, Token);

        result.Before.Should().BeNull();
        result.Reason.Should().StartWith("Not compared: Postgres refused the what-if (SQLSTATE 42883)");
        recorder.Cleanups.Should().Equal([HypotheticalIndexCleanup.SessionEnded], "the reset failed twice, so the session was ended");

        int ended = recorder.ProcessIds.Single();
        await StorageLive.UntilAsync(async () => !await server.BackendExistsAsync(ended, Token), TimeSpan.FromSeconds(15), "backend " + ended + " to exit");

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().NotBe(ended, "Npgsql held the ended connection broken and never pooled it again");
        (await HypotheticalCountAsync(next)).Should().Be(0);
        host.Logs.WarningsOrWorse.Should().BeEmpty("ending the session worked: nothing was left behind to warn about");
    }

    [PostgresFact]
    public async Task The_role_the_studio_plans_as_without_hypopg_execute_is_told_so_and_nothing_is_imagined()
    {
        (string database, _) = await SeedAsync("x2_no_privilege");
        await server.ExecuteAsync(
            "revoke execute on function public.hypopg_create_index(text) from public; "
            + "grant select on public.t to console_role",
            database,
            Token);

        Recorder recorder = new();
        await using StudioHost host = Host(database, recorder, static o => o.SqlConsoleRole = "console_role", role: "console_login");
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex index = await SuggestedAsync(console);
        WhatIfResult result = await console.WhatIfAsync(Scope, Statement, index, Token);

        result.Availability.Should().Be(HypotheticalIndexState.NoPrivilege);
        result.Reason.Should().Be(HypotheticalIndexAvailability.NoPrivilegeSentence);
        recorder.Made.Should().BeEmpty("hypopg is never asked by a role that may not use it");
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == SqlConsoleService.WhatIfAction && !x.Succeeded);
    }

    [PostgresFact]
    public async Task The_console_role_plans_the_what_if_and_its_index_is_used()
    {
        (string database, _) = await SeedAsync("x2_console_role");
        await server.ExecuteAsync("grant select on public.t to console_role", database, Token);

        Recorder recorder = new();
        await using StudioHost host = Host(database, recorder, static o => o.SqlConsoleRole = "console_role", role: "console_login");
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        WhatIfResult result = await console.WhatIfAsync(Scope, Statement, await SuggestedAsync(console), Token);

        result.Used.Should().BeTrue("hypopg works under SET LOCAL ROLE in BEGIN READ ONLY, for a role short of a superuser (PROBE)");
        recorder.Cleanups.Should().Equal([HypotheticalIndexCleanup.Reset]);
    }

    [PostgresFact]
    public async Task The_drill_in_plans_a_recorded_statement_under_the_index_its_generic_plan_suggests_and_only_that_one()
    {
        (string database, string app) = await SeedAsync("x2_drill_in");
        await StatementsLive.ExecuteAsync(app,
            "create table public.orders (id int primary key, note text, customer int); "
            + "insert into public.orders select g, 'n' || g, g % 1000 from pg_catalog.generate_series(1, 20000) g; "
            + "analyze public.orders");
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where o.customer = @id", 25);

        Recorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();
        IStatementsService statements = circuit.Get<IStatementsService>();

        StatementsView view = await statements.GetAsync(Scope, Token);
        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        long queryId = view.Rows.Single(static x => x.Text?.Contains("o.customer", StringComparison.Ordinal) == true).QueryId;

        StatementPlanView plan = await statements.PlanAsync(Scope, queryId, Token);
        HypotheticalIndex index = HypotheticalIndexCandidates.FromPlan(plan.Plan!).Should().ContainSingle().Subject;
        index.Definition.Should().Be("CREATE INDEX ON \"public\".\"orders\" (\"customer\");", "the drill-in plans with VERBOSE, so the scan names its schema");

        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        WhatIfResult result = await statements.WhatIfAsync(Scope, queryId, index, Token);

        result.Reason.Should().BeNull();
        result.Used.Should().BeTrue();
        result.IndexName.Should().MatchRegex("^<[0-9]+>btree_orders_customer$");
        result.After!.Nodes.Should().Contain(x => x.IndexName == result.IndexName);
        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "the drill-in's what-if creates nothing");
        recorder.Cleanups.Should().Equal([HypotheticalIndexCleanup.Reset]);

        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == StatementsService.WhatIfAction)
            .Which.Should().Match<StudioActionLogEntry>(x => x.Succeeded && x.Target == "queryid " + queryId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && x.Capability == nameof(StudioCapability.ViewDatabaseActivity) && !x.Target.Contains("orders", StringComparison.Ordinal));

        // An index the plan does not suggest is never imagined, whatever a page sends.
        HypotheticalIndex forged = index with
        {
            Key = HypotheticalIndexCandidates.Key("public", "orders", ["note"]),
            Columns = ["note"],
            Definition = "CREATE INDEX ON \"public\".\"orders\" (\"note\");",
        };

        WhatIfResult refused = await statements.WhatIfAsync(Scope, queryId, forged, Token);
        refused.Reason.Should().Be(StatementsService.NotSuggested);
        recorder.Made.Should().ContainSingle("the forged index was refused before hypopg was asked");
    }

    /// <summary>
    /// SEC-FIX: the drill-in's what-if plans the statement twice, so a text that names a schema the visitor may not see is
    /// refused before hypopg is asked - the plans of <c>hr.v_vip</c> are made of its definition over <c>public.orders</c>. A
    /// visitor whose browser gate admits <c>hr</c> compares the same index and sees the view's condition in both plans.
    /// </summary>
    [PostgresFact]
    public async Task The_drill_in_imagines_nothing_for_a_text_that_names_a_schema_the_visitor_may_not_see()
    {
        (string database, string app) = await SeedAsync("secfix_whatif");
        await StatementsLive.ExecuteAsync(app,
            "create table public.orders (id int primary key, note text, customer int, tier text); "
            + "insert into public.orders select g, 'n' || g, g % 1000, case when g % 5 = 0 then 'vip' else 'std' end "
            + "from pg_catalog.generate_series(1, 20000) g; "
            + "analyze public.orders; "
            + "create schema hr; "
            + "create view hr.v_vip as select o.id, o.note, o.customer from public.orders o where o.tier = 'vip'");
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), v.note from hr.v_vip v where v.customer = @id", 25);

        static long QueryIdOf(StatementsView view) =>
            view.Rows.Single(static x => x.Text?.Contains("v_vip", StringComparison.Ordinal) == true).QueryId;

        // A visitor who may see hr: the plan suggests an index, and the what-if compares it - over the view's own condition.
        Recorder admittedRecorder = new();
        HypotheticalIndex index;

        await using (StudioHost admitted = Host(database, admittedRecorder, static o =>
        {
            o.BrowsableSchemas.Add("public");
            o.BrowsableSchemas.Add("hr");
        }))
        {
            await using StudioCircuit circuit = admitted.Circuit();
            IStatementsService statements = circuit.Get<IStatementsService>();
            long queryId = QueryIdOf(await statements.GetAsync(Scope, Token));

            StatementPlanView plan = await statements.PlanAsync(Scope, queryId, Token);
            plan.NoPlan.Should().BeNull();
            index = HypotheticalIndexCandidates.FromPlan(plan.Plan!).Should().NotBeEmpty().And.Subject.First();

            WhatIfResult compared = await statements.WhatIfAsync(Scope, queryId, index, Token);
            compared.Reason.Should().BeNull();
            compared.Before!.Nodes.Should().Contain(static x => x.Filter != null && x.Filter.Contains("'vip'::text", StringComparison.Ordinal),
                "the plan is made of the view's definition");
        }

        // A visitor who may not: the same statement and the same index, refused before anything is planned or imagined.
        Recorder withheldRecorder = new();

        await using (StudioHost withheld = Host(database, withheldRecorder))
        {
            await using StudioCircuit circuit = withheld.Circuit();
            IStatementsService statements = circuit.Get<IStatementsService>();
            long queryId = QueryIdOf(await statements.GetAsync(Scope, Token));

            WhatIfResult refused = await statements.WhatIfAsync(Scope, queryId, index, Token);

            refused.Reason.Should().Be(StatementsService.NamesWithheldSchema);
            refused.Before.Should().BeNull();
            refused.After.Should().BeNull();
            withheldRecorder.Made.Should().BeEmpty("hypopg was never asked");
            withheldRecorder.Cleanups.Should().BeEmpty("no what-if session was opened at all");
        }
    }

    /// <summary>Records what the engine did, by backend; and fails a what-if after hypopg made its index, when asked to.</summary>
    private sealed class Recorder : IHypotheticalIndexObserver
    {
        private readonly ConcurrentQueue<HypotheticalIndexCleanup> cleanups = new();
        private readonly ConcurrentQueue<int> processIds = new();
        private readonly ConcurrentQueue<HypotheticalIndexMade> made = new();

        public bool FailAfterMade { get; init; }

        public IReadOnlyList<HypotheticalIndexCleanup> Cleanups => [.. cleanups];

        public IReadOnlyList<int> ProcessIds => [.. processIds];

        public IReadOnlyList<HypotheticalIndexMade> Made => [.. made];

        public Task MadeAsync(int processId, HypotheticalIndexMade index, CancellationToken cancellationToken)
        {
            made.Enqueue(index);
            return FailAfterMade ? throw new InvalidOperationException("A failure between hypopg's index and the second plan.") : Task.CompletedTask;
        }

        public void Finished(int processId, HypotheticalIndexCleanup cleanup)
        {
            processIds.Enqueue(processId);
            cleanups.Enqueue(cleanup);
        }
    }
}

/// <summary>X2 without hypopg: the regular fixture's Postgres, where the extension is not installed, says so and imagines nothing.</summary>
public class WhatIfWithoutHypopgLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Without_hypopg_the_what_if_says_it_is_not_installed_and_that_installing_it_is_the_hosts_decision()
    {
        const string Database = "x2_without_hypopg";
        string connection = await postgres.CreateDatabaseAsync(Database, Token);

        await StatementsLive.ExecuteAsync(connection,
            "create table public.t (id bigint primary key, b int); insert into public.t select g, g % 7 from pg_catalog.generate_series(1, 2000) g; analyze public.t");

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(connection)),
            static o => o.Capabilities.RunSql = true);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();
        StudioScope scope = new("default", "default", null);

        SqlConsoleResult planned = await console.RunSqlAsync(scope, new SqlConsoleRequest("select * from public.t where b = 5") { Explain = SqlExplain.Plan }, Token);
        HypotheticalIndex index = HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(planned.PlanJson).Plan!).Single();

        WhatIfResult result = await console.WhatIfAsync(scope, "select * from public.t where b = 5", index, Token);

        result.Availability.Should().Be(HypotheticalIndexState.NotInstalled);
        result.Reason.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence);
        result.Reason.Should().Contain("is the host's decision").And.Contain("never installs an extension, and never creates an index");
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == SqlConsoleService.WhatIfAction && !x.Succeeded);

        await postgres.DropDatabaseAsync(Database, Token);
    }
}
