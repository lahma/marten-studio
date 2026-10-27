using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// The SQL console's Explain buttons (X1, K1) on the version matrix, 14 and 18: <c>EXPLAIN ANALYZE</c> runs in the console's
/// read-only transaction, so a write the guard does not spot - a data-modifying CTE - comes back as <c>25006</c> and changes
/// nothing, audited as a failed <c>RunSql</c> run; a read's measured plan comes back whole, with its buffers, and its insights
/// fire; a plain plan runs nothing; and an <c>EXPLAIN (FORMAT JSON)</c> typed and Run is drawn as a plan too.
/// </summary>
public class ConsoleExplainLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<int> Legs => [14, 18];

    private static async Task<(StudioHost Host, string Connection)> HostAsync(PostgresVersionMatrix matrix, int major)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync("x1_console", Token);
        string app = leg.ConnectionString(MatrixRole.App, database);

        // Unpooled, here and in CountAsync: each case drops and recreates the database, which ends every session a pool kept.
        await using (NpgsqlConnection connection = new(Unpooled(app)))
        {
            await connection.OpenAsync(Token);
            await using NpgsqlCommand seed = new(
                "create table public.orders (id int primary key, note text); "
                + "insert into public.orders select g, 'note ' || g from pg_catalog.generate_series(1, 2000) g; analyze public.orders",
                connection);
            await seed.ExecuteNonQueryAsync(Token);
        }

        StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(app)),
            static o => o.Capabilities.RunSql = true);

        return (host, app);
    }

    private static string Unpooled(string connectionString) => new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task<long> CountAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(Unpooled(connectionString));
        await connection.OpenAsync(Token);
        await using NpgsqlCommand count = new("select count(*) from public.orders", connection);
        return (long) (await count.ExecuteScalarAsync(Token))!;
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Explain_analyze_of_a_delete_the_guard_lets_through_is_refused_by_the_transaction_and_changes_nothing(int major)
    {
        (StudioHost host, string app) = await HostAsync(matrix, major);
        await using StudioHost owned = host;
        await using StudioCircuit circuit = host.Circuit();

        const string Delete = "with d as (delete from public.orders where id <= 10 returning *) select count(*) from d";

        SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest(Delete) { Explain = SqlExplain.Analyze }, Token);

        result.Rejection.Should().BeNull("the guard accepts a with statement; the transaction is the guard (D13)");
        result.Error!.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction, "EXPLAIN ANALYZE runs the DELETE, inside BEGIN READ ONLY");
        result.PlanJson.Should().BeNull();
        (await CountAsync(app)).Should().Be(2000, "nothing was deleted");

        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == "RunSql")
            .Which.Should().Match<StudioActionLogEntry>(static x => !x.Succeeded
                && x.Target.StartsWith("explain (analyze, buffers, format json) with d as (delete", StringComparison.Ordinal)
                && x.Capability == nameof(StudioCapability.RunSql));
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Explain_analyze_of_a_delete_the_guard_sees_is_refused_before_anything_is_sent(int major)
    {
        (StudioHost host, string app) = await HostAsync(matrix, major);
        await using StudioHost owned = host;
        await using StudioCircuit circuit = host.Circuit();

        SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(
            Scope, new SqlConsoleRequest("delete from public.orders where id = 1") { Explain = SqlExplain.Analyze }, Token);

        result.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.DisallowedStatement));
        (await CountAsync(app)).Should().Be(2000);
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == "RunSql" && !x.Succeeded);
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Explain_analyze_of_a_read_comes_back_whole_measured_with_buffers_and_its_insights_fire(int major)
    {
        (StudioHost host, _) = await HostAsync(matrix, major);
        await using StudioHost owned = host;
        await using StudioCircuit circuit = host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Like = "select * from public.orders where note like '%99%'";

        SqlConsoleResult analyzed = await console.RunSqlAsync(Scope, new SqlConsoleRequest(Like) { Explain = SqlExplain.Analyze }, Token);

        analyzed.Error.Should().BeNull();
        QueryPlan plan = QueryPlanReader.Read(analyzed.PlanJson).Plan!;
        plan.Analyzed.Should().BeTrue();
        plan.HasBuffers.Should().BeTrue("BUFFERS is asked for on every major");
        plan.ExecutionMs.Should().NotBeNull();
        plan.Root.ActualRows.Should().BeGreaterThan(0);
        (plan.Root.IndexSearches is null).Should().BeTrue("a sequential scan has no index searches on any major");
        (plan.Root.Disabled is not null).Should().Be(major >= 18, "18 prints Disabled on every node");
        PlanInsights.Evaluate(plan, circuit.Services.GetRequiredService<IStudioSettings>().Text).Select(static x => x.Insight.Code).Should().Contain("P07");

        SqlConsoleResult planned = await console.RunSqlAsync(Scope, new SqlConsoleRequest(Like) { Explain = SqlExplain.Plan }, Token);
        QueryPlanReader.Read(planned.PlanJson).Plan!.Analyzed.Should().BeFalse("Explain plans and runs nothing");

        SqlConsoleResult typed = await console.RunSqlAsync(Scope, new SqlConsoleRequest("explain (format json, costs) " + Like), Token);
        QueryPlanReader.Read(typed.PlanJson).Plan.Should().NotBeNull("an EXPLAIN (FORMAT JSON) typed and Run is drawn as a plan too");

        SqlConsoleResult text = await console.RunSqlAsync(Scope, new SqlConsoleRequest("explain " + Like), Token);
        text.PlanJson.Should().BeNull("FORMAT TEXT is rows of text, shown in the grid");
        text.Rows.Should().NotBeEmpty();

        host.Ring.GetLatest().Where(static x => x.Action == "RunSql").Should().HaveCount(4).And.OnlyContain(static x => x.Succeeded);
    }

    [PostgresFact]
    public async Task A_plan_larger_than_a_grid_cell_comes_back_whole()
    {
        (StudioHost host, _) = await HostAsync(matrix, 18);
        await using StudioHost owned = host;
        await using StudioCircuit circuit = host.Circuit();

        // A union of forty selects: a plan of some 40 nodes, far over the 4 KB a grid cell of JSON is capped at.
        string union = string.Join(" union all ", Enumerable.Range(1, 40).Select(static i => "select id from public.orders where id = " + i));

        SqlConsoleResult result = await circuit.Get<ISqlConsoleService>().RunSqlAsync(Scope, new SqlConsoleRequest(union) { Explain = SqlExplain.Plan }, Token);

        result.Rows[0][0].IsTruncated.Should().BeTrue("the grid's cell is capped for a grid");
        result.PlanJson!.Length.Should().BeGreaterThan(4096);
        QueryPlanReader.Read(result.PlanJson).Plan!.NodeCount.Should().BeGreaterThan(40);
    }
}
