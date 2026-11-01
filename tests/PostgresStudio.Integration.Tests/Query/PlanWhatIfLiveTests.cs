using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Statements;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// 0.10-B (issue #28): the Plan page's What if? in place against a real hypopg - the page's own statement, with its value, planned
/// before and after the index its plan suggests; the pooled backend is clean afterwards; nothing is left prepared; a statement
/// Postgres refuses is a sentence, not a plan; and the index the page names is built again on the server from its parts.
/// </summary>
public class PlanWhatIfLiveTests(HypopgServer server) : IClassFixture<HypopgServer>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<string> SeedAsync(string name)
    {
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync(name, true, Token);

        await StatementsLive.ExecuteAsync(
            server.ConnectionString("app", database),
            "create table public.t (id bigint primary key, a int, b int, c text); "
            + "insert into public.t select g, g % 1000, g % 7, pg_catalog.md5(g::text) from pg_catalog.generate_series(1, 200000) g; "
            + "create index t_a on public.t (a); "
            + "analyze public.t");

        return database;
    }

    private StudioHost Host(string database, Failing? observer = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(server.ConnectionString("app", database, maxPoolSize: 1)));

                if (observer is not null)
                {
                    services.AddSingleton<IHypotheticalIndexObserver>(observer);
                }
            },
            options => options.Capabilities = PostgresStudioCapabilities.All());

    /// <summary>An observer that fails the what-if the moment hypopg has made its index, and remembers how the connection was left.</summary>
    private sealed class Failing : IHypotheticalIndexObserver
    {
        private readonly List<HypotheticalIndexCleanup> cleanups = [];
        private readonly List<int> processIds = [];
        private readonly List<HypotheticalIndexMade> made = [];

        public IReadOnlyList<HypotheticalIndexCleanup> Cleanups => cleanups;

        public IReadOnlyList<int> ProcessIds => processIds;

        public IReadOnlyList<HypotheticalIndexMade> Made => made;

        public Task MadeAsync(int processId, HypotheticalIndexMade index, CancellationToken cancellationToken)
        {
            made.Add(index);
            throw new InvalidOperationException("A failure between hypopg's index and the second plan.");
        }

        public void Finished(int processId, HypotheticalIndexCleanup cleanup)
        {
            processIds.Add(processId);
            cleanups.Add(cleanup);
        }
    }

    private static async Task<HypotheticalIndex> SuggestedAsync(ISqlConsoleService console, string statement)
    {
        PlanRunResult planned = await console.PlanAsync(Scope, new PlanRunRequest(statement, PlanMode.Plan), Token);
        planned.Error.Should().BeNull();

        return PlanWhatIfCandidates.For(QueryPlanReader.Read(planned.PlanJson).Plan!, PostgresStudio.Services.Settings.StudioText.Marten)
            .Should().ContainSingle().Subject;
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (long) (await command.ExecuteScalarAsync(Token))!;
    }

    [PostgresFact]
    public async Task The_page_plans_its_own_statement_for_the_value_it_was_given_before_and_after_the_hypothetical_index_and_leaves_nothing_behind()
    {
        string database = await SeedAsync("b_whatif");
        await using StudioHost host = Host(database);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Statement = "select * from public.t where b = $1";
        PlanRunRequest request = new(Statement, PlanMode.Plan) { Values = ["5"] };

        // The plan the page drew for this statement and value suggests the index; the console builds it again from its parts.
        PlanRunResult planned = await console.PlanAsync(Scope, request, Token);
        HypotheticalIndex index = PlanWhatIfCandidates.For(QueryPlanReader.Read(planned.PlanJson).Plan!, PostgresStudio.Services.Settings.StudioText.Marten)
            .Should().ContainSingle().Subject;
        index.Definition.Should().Be("CREATE INDEX ON \"t\" (\"b\");");

        // Analyze is planned plain: a hypothetical index serves EXPLAIN alone.
        WhatIfResult result = await console.PlanWhatIfAsync(Scope, request with { Mode = PlanMode.Analyze }, index, Token);

        result.Reason.Should().BeNull();
        result.Used.Should().BeTrue();
        result.Before!.Nodes.Should().Contain(static x => x.NodeType == "Seq Scan" && x.RelationName == "t");
        result.After!.Root.TotalCost.Should().BeLessThan(result.Before.Root.TotalCost!.Value);
        result.Before.Analyzed.Should().BeFalse("both plans are plain plans");

        PlanComparison comparison = PlanDiff.Compare(result.Before, result.After);
        comparison.Differences.Should().NotBeEmpty();
        comparison.CostRatio.Should().BeLessThan(1);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        (await CountAsync(next, "select count(*) from public.hypopg()")).Should().Be(0, "the reset ran on the same pooled backend");
        (await CountAsync(next, "select count(*) from pg_catalog.pg_prepared_statements")).Should().Be(0, "nothing is left prepared");

        host.Ring.GetLatest().Should().Contain(static x => x.Action == SqlConsoleService.WhatIfAction && x.Succeeded && x.Capability == nameof(StudioCapability.RunSql));
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_statement_Postgres_refuses_is_a_sentence_with_a_SQLSTATE_and_the_backend_is_clean_afterwards()
    {
        string database = await SeedAsync("b_whatif_error");
        await using StudioHost host = Host(database);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex index = await SuggestedAsync(console, "select * from public.t where b = 5");

        // A value Postgres cannot read as the placeholder's type: the first plan fails when the value is bound to its EXPLAIN - after the
        // prepare that read the placeholder's types was deallocated, and before hypopg was asked for anything.
        WhatIfResult result = await console.PlanWhatIfAsync(
            Scope, new PlanRunRequest("select * from public.t where b = $1", PlanMode.Plan) { Values = ["not a number"] }, index, Token);

        result.Before.Should().BeNull();
        result.Reason.Should().StartWith("Not compared: Postgres refused the what-if (SQLSTATE 22P02)");
        result.Reason.Should().NotContain("not a number", "never Postgres' own words");

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        (await CountAsync(next, "select count(*) from public.hypopg()")).Should().Be(0);
        (await CountAsync(next, "select count(*) from pg_catalog.pg_prepared_statements")).Should().Be(0, "nothing was left prepared");
    }

    [PostgresFact]
    public async Task A_failure_after_the_hypothetical_index_exists_resets_it_on_the_same_backend_before_the_connection_goes_back()
    {
        string database = await SeedAsync("b_whatif_after_index");
        Failing observer = new();
        await using StudioHost host = Host(database, observer);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Statement = "select * from public.t where b = $1";
        PlanRunRequest request = new(Statement, PlanMode.Plan) { Values = ["5"] };

        PlanRunResult planned = await console.PlanAsync(Scope, request, Token);
        HypotheticalIndex index = PlanWhatIfCandidates.For(QueryPlanReader.Read(planned.PlanJson).Plan!, PostgresStudio.Services.Settings.StudioText.Marten)
            .Should().ContainSingle().Subject;

        // hypopg has made the index when the observer throws, between the first plan and the second.
        WhatIfResult result = await console.PlanWhatIfAsync(Scope, request, index, Token);

        result.Before.Should().BeNull();
        result.Reason.Should().Be("Not compared: the what-if failed (InvalidOperationException).");
        observer.Made.Should().ContainSingle("the index existed when the what-if failed");
        observer.Cleanups.Should().Equal([HypotheticalIndexCleanup.ResetAfterFailure]);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().Be(observer.ProcessIds.Single(), "the pool holds one connection: the next user is the same backend");
        (await CountAsync(next, "select count(*) from public.hypopg()")).Should().Be(0, "the failure path reset hypopg on that backend");
        (await CountAsync(next, "select count(*) from pg_catalog.pg_prepared_statements")).Should().Be(0);

        host.Ring.GetLatest().Should().Contain(static x => x.Action == SqlConsoleService.WhatIfAction && !x.Succeeded);
    }

    [PostgresFact]
    public async Task An_index_the_page_made_up_is_refused_before_anything_is_imagined()
    {
        string database = await SeedAsync("b_whatif_forged");
        await using StudioHost host = Host(database);
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        HypotheticalIndex good = await SuggestedAsync(console, "select * from public.t where b = 5");
        HypotheticalIndex forged = new()
        {
            Key = good.Key,
            Definition = "CREATE INDEX ON \"t\" (\"b\"); DROP TABLE public.t;",
            Caption = good.Caption,
            Origin = HypotheticalIndexOrigin.Plan,
            Schema = good.Schema,
            Table = good.Table,
            Columns = good.Columns,
        };

        WhatIfResult result = await console.PlanWhatIfAsync(Scope, new PlanRunRequest("select * from public.t where b = 5", PlanMode.Plan), forged, Token);

        result.Reason.Should().Be(SqlConsoleService.NotBuiltMessage);
        result.Before.Should().BeNull();
    }
}
