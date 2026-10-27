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
/// The Plan page's door into the console (X3, issue #28) on the version matrix - 14, 17 and 18: <c>EXPLAIN ANALYZE</c> of a read
/// fires P10 on a table built to do it and P09 on an index-only scan over a table nobody vacuumed; a write is refused by the
/// guard in every mode and, hidden in a CTE, is never analyzed (<c>25006</c>, nothing changed) though it may be planned; a
/// statement with a placeholder is planned for the value typed - prepared, its types read, executed with the value bound - and
/// leaves no prepared statement behind on the session, whichever way the run ended; and every run, refusals included, is audited as
/// a <c>RunSql</c> run.
/// </summary>
public class PlanAnalyzerLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<int> Legs => [14, 17, 18];

    private const string Seed =
        """
        create table public.orders (id int primary key, note text);
        insert into public.orders select g, 'note ' || g from pg_catalog.generate_series(1, 2000) g;
        analyze public.orders;

        -- Ten blocks of twenty thousand rows, one account each and physically in order, so an index on the account reads one block;
        -- fifty of the rows in the block of account 5 are refunds, which the index knows nothing about.
        create table public.events (id int primary key, account int not null, kind text not null);
        insert into public.events select g, g / 20000, case when g / 20000 = 5 and g % 400 = 0 then 'refund' else 'sale' end
          from pg_catalog.generate_series(0, 199999) g;
        create index events_account_idx on public.events (account);
        analyze public.events;

        -- Never vacuumed, and told never to be: the visibility map stays empty, so an index-only scan visits the table for every row.
        create table public.unvacuumed (id int primary key, v int not null) with (autovacuum_enabled = false);
        insert into public.unvacuumed select g, g from pg_catalog.generate_series(1, 100000) g;
        analyze public.unvacuumed;
        """;

    private sealed record Harness(StudioHost Host, NpgsqlDataSource Source, string Connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Source.DisposeAsync();
        }
    }

    /// <summary>
    /// A host over one data source of one connection that is never reset on close: a prepared statement a run left behind would
    /// still be on the session the next caller gets, and <see cref="PreparedAsync" /> reads exactly that session.
    /// </summary>
    private static async Task<Harness> HostAsync(PostgresVersionMatrix matrix, int major)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync("x3_plan", Token);
        string app = leg.ConnectionString(MatrixRole.App, database);

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using NpgsqlCommand seed = new(Seed, connection);
            await seed.ExecuteNonQueryAsync(Token);
        }

        NpgsqlDataSource source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(app) { MaxPoolSize = 1, NoResetOnClose = true }.ConnectionString);

        StudioHost host = StudioHost.Create(
            services => services.AddSingleton(source),
            static o => o.Capabilities.RunSql = true);

        return new Harness(host, source, app);
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The prepared statements on the session the pool of one connection hands out.</summary>
    private static Task<long> PreparedAsync(NpgsqlDataSource source) => ScalarAsync(source, "select count(*) from pg_catalog.pg_prepared_statements");

    private static Task<long> OrdersAsync(NpgsqlDataSource source) => ScalarAsync(source, "select count(*) from public.orders");

    private static IReadOnlyList<string> Codes(StudioCircuit circuit, QueryPlan plan) =>
        [.. PlanInsights.Evaluate(plan, circuit.Services.GetRequiredService<IStudioSettings>().Text).Select(static x => x.Insight.Code)];

    // ---- Insights on real plans ----------------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Explain_analyze_of_a_select_fires_P10_on_a_table_built_to_do_it(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope, new PlanRunRequest("select * from public.events where account = 5 and kind = 'refund'", PlanMode.Analyze), Token);

        result.Error.Should().BeNull();
        result.Rejection.Should().BeNull();
        result.Succeeded.Should().BeTrue();
        result.Options.Should().Be("analyze, buffers, format json");

        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        plan.Analyzed.Should().BeTrue();
        plan.HasBuffers.Should().BeTrue();

        PlanNode scan = plan.Nodes.Single(static x => x.RelationName == "events");
        scan.NodeType.Should().BeOneOf("Index Scan", "Bitmap Heap Scan");
        scan.RowsRemovedByFilter.Should().BeGreaterThanOrEqualTo(10_000);
        scan.ActualRows.Should().Be(50);

        PlanInsightFinding finding = PlanInsights.Evaluate(plan, circuit.Services.GetRequiredService<IStudioSettings>().Text).Single(static x => x.Insight.Code == "P10");
        finding.Message.Should().Contain("returned 50 rows and threw away");
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Explain_analyze_of_an_index_only_scan_over_an_unvacuumed_table_fires_P09(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope, new PlanRunRequest("select id from public.unvacuumed where id between 1 and 5000 order by id", PlanMode.Analyze), Token);

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);

        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        PlanNode scan = plan.Nodes.Single(static x => x.RelationName == "unvacuumed");

        scan.NodeType.Should().Be("Index Only Scan");
        scan.HeapFetches.Should().BeGreaterThanOrEqualTo(1000, "no page of a table nobody vacuumed is all-visible");
        Codes(circuit, plan).Should().Contain("P09");
    }

    // ---- A write is never analyzed -------------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_write_is_refused_by_the_guard_in_every_mode_and_one_it_misses_is_never_analyzed_though_it_may_be_planned(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        // What the guard sees: refused as typed, whatever the mode - nothing is sent.
        foreach (PlanMode mode in new[] { PlanMode.Plan, PlanMode.Analyze })
        {
            PlanRunResult refused = await console.PlanAsync(Scope, new PlanRunRequest("delete from public.orders where id <= 10", mode), Token);

            refused.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.DisallowedStatement));
            refused.PlanJson.Should().BeNull();
        }

        // What it misses: a data-modifying CTE. Analyzed, it is the transaction that refuses it (25006), exactly as for the console.
        const string Cte = "with d as (delete from public.orders where id <= 10 returning *) select count(*) from d";

        PlanRunResult analyzed = await console.PlanAsync(Scope, new PlanRunRequest(Cte, PlanMode.Analyze), Token);

        analyzed.Rejection.Should().BeNull("the guard accepts a with statement; the transaction is the guard (D13)");
        analyzed.Error!.SqlState.Should().Be(PostgresErrorCodes.ReadOnlySqlTransaction, "EXPLAIN ANALYZE runs the DELETE, inside BEGIN READ ONLY");
        analyzed.PlanJson.Should().BeNull();
        (await OrdersAsync(harness.Source)).Should().Be(2000, "nothing was deleted");

        // Planned only, the same statement is a plan - and still changes nothing.
        PlanRunResult planned = await console.PlanAsync(Scope, new PlanRunRequest(Cte, PlanMode.Plan), Token);

        planned.Error.Should().BeNull(planned.Error?.MessageText);
        QueryPlanReader.Read(planned.PlanJson).Plan!.Analyzed.Should().BeFalse("a plain plan runs nothing");
        (await OrdersAsync(harness.Source)).Should().Be(2000);

        // Every one of the four is a RunSql run in the audit, the refusals and the failure included.
        IReadOnlyList<StudioActionLogEntry> ring = [.. harness.Host.Ring.GetLatest().Where(static x => x.Action == "RunSql")];

        ring.Should().HaveCount(4);
        ring.Count(static x => !x.Succeeded).Should().Be(3);
        ring.Should().ContainSingle(static x => x.Succeeded).Which.Target.Should().StartWith("explain (format json) with d as (delete");
        ring.Should().OnlyContain(static x => x.Capability == nameof(StudioCapability.RunSql));
    }

    // ---- A placeholder ---------------------------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_statement_with_a_placeholder_is_planned_for_its_value_prepared_and_leaves_no_prepared_statement_behind(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Statement = "select * from public.orders where id = $1";

        // A custom plan: planned for 5, whose type Postgres inferred from the column.
        PlanRunResult custom = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan) { Values = ["5"] }, Token);

        custom.Succeeded.Should().BeTrue(custom.Error?.MessageText);
        custom.ParameterTypes.Should().Equal("integer");
        QueryPlanReader.Read(custom.PlanJson).Plan!.Nodes.Single(static x => x.RelationName == "orders").IndexCondition.Should().Be("(id = 5)", "a custom plan is planned for the value");
        (await PreparedAsync(harness.Source)).Should().Be(0, "the prepared statement was deallocated before the transaction ended");

        // Analyzed for a value: it runs, prepared, and the row is counted.
        PlanRunResult analyzed = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Analyze) { Values = ["7"] }, Token);

        analyzed.Succeeded.Should().BeTrue(analyzed.Error?.MessageText);
        QueryPlanReader.Read(analyzed.PlanJson).Plan!.Root.ActualRows.Should().Be(1);
        (await PreparedAsync(harness.Source)).Should().Be(0);

        // The generic plan: no value, the placeholder stays a placeholder.
        PlanRunResult generic = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Generic), Token);

        generic.Succeeded.Should().BeTrue(generic.Error?.MessageText);
        QueryPlanReader.Read(generic.PlanJson).Plan!.Nodes.Single(static x => x.RelationName == "orders").IndexCondition.Should().Be("(id = $1)");
        (await PreparedAsync(harness.Source)).Should().Be(0);

        // NULL is a value: planned for null.
        PlanRunResult nothing = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan) { Values = [null] }, Token);
        nothing.Succeeded.Should().BeTrue(nothing.Error?.MessageText);

        // A value Postgres cannot read as the placeholder's type: an error in a bound value, not in the typed text.
        PlanRunResult bad = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan) { Values = ["not a number"] }, Token);

        bad.PlanJson.Should().BeNull();
        bad.ParameterTypes.Should().Equal("integer"); // read before the value was tried
        bad.Error!.SqlState.Should().Be(PostgresErrorCodes.InvalidTextRepresentation);
        bad.Error.Position.Should().Be(0, "the error is in a bound value, so it points at no character of the statement's text");
        (await PreparedAsync(harness.Source)).Should().Be(0);

        // An error in the typed text itself: the prepare refuses it, and the editor can point at the character.
        PlanRunResult typo = await console.PlanAsync(Scope, new PlanRunRequest("select nope from public.orders where id = $1", PlanMode.Plan) { Values = ["1"] }, Token);

        typo.Error!.SqlState.Should().Be(PostgresErrorCodes.UndefinedColumn);
        typo.ErrorOffset.Should().Be("prepare \"marten_studio_plan\" as ".Length);
        (typo.Error.Position - 1 - typo.ErrorOffset).Should().Be("select ".Length, "the caret falls on `nope`");
        (await PreparedAsync(harness.Source)).Should().Be(0);

        // Every run is a RunSql run, and no value of a placeholder is in what was recorded.
        IReadOnlyList<StudioActionLogEntry> ring = [.. harness.Host.Ring.GetLatest().Where(static x => x.Action == "RunSql")];

        ring.Should().HaveCount(6, "custom, analyzed, generic, null, a bad value and a typo");
        ring.Count(static x => x.Succeeded).Should().Be(4);
        ring.Should().NotContain(static x => x.Target.Contains("not a number", StringComparison.Ordinal));
        ring.Where(static x => x.Target.StartsWith("prepare", StringComparison.Ordinal)).Should().OnlyContain(static x => x.Target.Contains("$1", StringComparison.Ordinal));
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_name_a_run_left_on_the_session_is_taken_back_and_the_plan_is_drawn(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        // Another run's leftover, on the one session the pool has.
        await using (NpgsqlConnection leftover = await harness.Source.OpenConnectionAsync(Token))
        {
            await using NpgsqlCommand prepare = new("prepare \"marten_studio_plan\" as select 1", leftover);
            await prepare.ExecuteNonQueryAsync(Token);
        }

        (await PreparedAsync(harness.Source)).Should().Be(1, "the premise: the name is there");

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope, new PlanRunRequest("select * from public.orders where id = $1", PlanMode.Plan) { Values = ["3"] }, Token);

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        (await PreparedAsync(harness.Source)).Should().Be(0);
    }

    // ---- Options -------------------------------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task The_options_the_server_has_are_asked_for_and_the_ones_it_lacks_are_left_out_and_named(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanOptions options = new() { Verbose = true, Settings = true, Wal = true, Memory = true, Serialize = true };

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope, new PlanRunRequest("select * from public.orders where id < 100", PlanMode.Analyze) { Options = options }, Token);

        result.Succeeded.Should().BeTrue(result.Error?.MessageText);
        result.ServerMajor.Should().Be(major);

        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        plan.Root.WalRecords.Should().NotBeNull("WAL was asked for under ANALYZE, on every major from 14");
        plan.Root.Schema.Should().Be("public", "VERBOSE prints every relation's schema");

        if (major >= 17)
        {
            result.LeftOut.Should().BeEmpty();
            result.Options.Should().Be("analyze, verbose, buffers, settings, wal, memory, serialize, format json");
            plan.PlanningMemoryBytes.Should().BeGreaterThan(0);
            plan.SerializationMs.Should().NotBeNull();
        }
        else
        {
            result.LeftOut.Should().Equal("MEMORY (needs PostgreSQL 17)", "SERIALIZE (needs PostgreSQL 17)");
            result.Options.Should().Be("analyze, verbose, buffers, settings, wal, format json");
            plan.PlanningMemoryBytes.Should().BeNull();
        }
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_plain_plan_asks_for_no_counts_and_turning_BUFFERS_and_TIMING_off_under_ANALYZE_is_honoured(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        PlanRunResult plain = await console.PlanAsync(Scope, new PlanRunRequest("select * from public.orders", PlanMode.Plan), Token);

        plain.Succeeded.Should().BeTrue(plain.Error?.MessageText);
        QueryPlanReader.Read(plain.PlanJson).Plan!.Analyzed.Should().BeFalse();

        PlanRunResult bare = await console.PlanAsync(
            Scope, new PlanRunRequest("select * from public.orders", PlanMode.Analyze) { Options = new PlanOptions { Buffers = false, Timing = false } }, Token);

        bare.Succeeded.Should().BeTrue(bare.Error?.MessageText);
        QueryPlan plan = QueryPlanReader.Read(bare.PlanJson).Plan!;
        plan.Analyzed.Should().BeTrue();
        plan.HasBuffers.Should().BeFalse("buffers off is honoured, from 18 where they are on by default too");
        plan.Timed.Should().BeFalse("timing off: rows and loops, no time");
    }

    // ---- Refusals before a connection ----------------------------------------------------------------------------------

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task An_error_in_a_plain_statement_comes_back_with_the_position_in_the_typed_text(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(Scope, new PlanRunRequest("select nope from public.orders", PlanMode.Plan), Token);

        result.Error!.SqlState.Should().Be(PostgresErrorCodes.UndefinedColumn);
        result.ErrorOffset.Should().Be("explain (format json) ".Length);
        (result.Error.Position - 1 - result.ErrorOffset).Should().Be("select ".Length);
        result.Succeeded.Should().BeFalse();
        (await PreparedAsync(harness.Source)).Should().Be(0);
    }
}
