using System.Text.Json;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Integration.Tests.Statements;

/// <summary>
/// The Statements page's drill-in (X1) against a real <c>pg_stat_statements</c>, on the version matrix: a recorded statement's
/// generic plan on 14, 16 and 18, read with no DDL and no lock beyond the <c>AccessShareLock</c>s its own tables take; why it
/// is planned by <c>prepare</c> and <c>force_generic_plan</c> rather than <c>EXPLAIN (GENERIC_PLAN)</c> - measured through
/// Npgsql on 16 and 18; and a plan that reads a withheld schema not shown, while one that only calls into one is shown masked.
/// </summary>
public class StatementPlanLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A statement slow on purpose, so it is among the statements read: 25 calls of 20 ms.</summary>
    private const string Slow = "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where o.id = @id";

    /// <summary>The supported floor, the first major with <c>GENERIC_PLAN</c>, and the newest release.</summary>
    public static TheoryData<int> PlanLegs => [14, 16, 18];

    /// <summary>The majors that have <c>EXPLAIN (GENERIC_PLAN)</c> at all.</summary>
    public static TheoryData<int> GenericPlanLegs => [16, 18];

    private static async Task<(MatrixServer Leg, string Database, string App)> SeedAsync(PostgresVersionMatrix matrix, int major, string name)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync(name, Token);
        await leg.ExecuteAsync("create extension if not exists pg_stat_statements", database, Token);

        string app = leg.ConnectionString(MatrixRole.App, database);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.RepeatAsync(app, Slow, 25);

        return (leg, database, app);
    }

    private static StatementRowView Row(StatementsView view, string marker) =>
        view.Rows.Single(x => x.Text?.Contains(marker, StringComparison.Ordinal) == true);

    [PostgresTheory]
    [MemberData(nameof(PlanLegs))]
    public async Task The_drill_in_plans_a_recorded_statement_as_its_generic_plan_with_no_ddl_and_no_lock_beyond_its_own_tables(int major)
    {
        (MatrixServer leg, string database, string app) = await SeedAsync(matrix, major, "x1_plan");

        HealthSweepProbe probe = new(async cancellation => await leg.OpenAsync(MatrixRole.Superuser, database, cancellation));
        await using StudioHost host = StatementsLive.Host(app, probe);
        await using StudioCircuit circuit = host.Circuit();
        IStatementsService statements = circuit.Get<IStatementsService>();

        StatementsView view = await statements.GetAsync(StatementsLive.Scope, Token);
        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        StatementRowView row = Row(view, "pg_sleep");

        IReadOnlySet<string> before = await leg.SnapshotAsync(database, Token);
        int observed = probe.StatementsObserved;

        StatementPlanView plan = await statements.PlanAsync(StatementsLive.Scope, row.QueryId, Token);

        plan.Refusal.Should().Be(DatabaseRefusal.None);
        plan.NoPlan.Should().BeNull("PostgreSQL {0} plans it - by prepare and force_generic_plan, which every major from 12 has", major);
        plan.Statement!.Text.Should().Contain("$1", "the text pg_stat_statements recorded, constants replaced");

        QueryPlan tree = plan.Plan!;
        tree.Analyzed.Should().BeFalse("the drill-in never runs EXPLAIN ANALYZE (K1)");
        PlanNode scan = tree.Nodes.Single(static x => x.RelationName == "orders");
        scan.Schema.Should().Be("public", "the plan is asked for with VERBOSE, so every relation carries its schema for the mask");
        scan.Description.Should().EndWith("on public.orders o");
        (scan.IndexCondition ?? scan.Filter).Should().Contain("$1", "a generic plan keeps the placeholder: it was planned with no value");
        plan.Caveat.Should().StartWith("The plan is PostgreSQL's generic plan");
        plan.Statements.Select(static x => x.Sql).Should().Contain(static x => x.StartsWith("prepare \"marten_studio_plan\" as <the text", StringComparison.Ordinal),
            "Show SQL prints the batch's shape, never another program's text");

        MatrixServer.AssertUnchanged(before, await leg.SnapshotAsync(database, Token), "planning a statement creates nothing");

        probe.StatementsObserved.Should().BeGreaterThan(observed, "so 'no lock' is not 'nothing was planned'");
        HeldLock[] held = [.. probe.BeyondCatalogReads.Where(static x => x.Statement.StartsWith("prepare \"marten_studio_plan\"", StringComparison.Ordinal))];
        held.Should().NotBeEmpty("planning reads the statement's own table").And.OnlyContain(
            static x => x.LockType == "relation" && x.Mode == "AccessShareLock" && x.Schema == "public" && (x.Relation == "orders" || x.Relation == "orders_pkey"),
            "the plan takes the AccessShareLock a read of the statement's own table and index takes, and nothing else: " + string.Join("; ", held.Select(static x => x.ToString())));

        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == StatementsService.PlanAction)
            .Which.Should().Match<StudioActionLogEntry>(static x => x.Succeeded && x.Target.StartsWith("queryid ", StringComparison.Ordinal)
                && !x.Target.Contains("pg_sleep", StringComparison.Ordinal) && x.Capability == nameof(StudioCapability.ViewDatabaseActivity));

        // Planned again on the same pooled session: the batch deallocated what it prepared, so its prepare does not meet the name.
        StatementPlanView again = await statements.PlanAsync(StatementsLive.Scope, row.QueryId, Token);
        again.NoPlan.Should().BeNull();
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// Why the drill-in prepares: <c>EXPLAIN (GENERIC_PLAN)</c>, which the PROBE packet ran in <c>psql</c> on 16 to 19, plans
    /// nothing through Npgsql. Npgsql sends every command through the extended protocol, so the server counts the text's
    /// placeholders as the command's own parameters: with none bound the bind fails, and with a null bound the planner folds it
    /// into the plan as a constant - a plan of a different statement.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(GenericPlanLegs))]
    public async Task Through_Npgsql_GENERIC_PLAN_fails_with_nothing_bound_and_folds_what_is_bound(int major)
    {
        (MatrixServer leg, string database, string app) = await SeedAsync(matrix, major, "x1_generic");
        const string Sql = "explain (generic_plan, format json) select o.note from public.orders o where o.id = $1";
        string unpooled = new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString;

        // A connection each: Npgsql closes the one a protocol violation arrived on.
        await using (NpgsqlConnection connection = new(unpooled))
        {
            await connection.OpenAsync(Token);
            await using NpgsqlCommand unbound = new(Sql, connection);

            Func<Task> run = () => unbound.ExecuteScalarAsync(Token);
            (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ProtocolViolation,
                "the bind supplies no value for the placeholder the server counted");
        }

        await using (NpgsqlConnection connection = new(unpooled))
        {
            await connection.OpenAsync(Token);
            await using NpgsqlCommand bound = new(Sql, connection);
            bound.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });

            string json = (string) (await bound.ExecuteScalarAsync(Token))!;

            json.Should().Contain("One-Time Filter", "id = NULL folds to a constant false: the planner used the bound value");
            json.Should().NotContain("$1");
        }
    }

    [PostgresFact]
    public async Task A_plan_that_reads_a_withheld_schema_is_not_shown_and_one_that_only_calls_into_one_is_shown_masked()
    {
        (MatrixServer leg, string database, string app) = await SeedAsync(matrix, 18, "x1_withheld");

        // hr is the app's own schema, and not one BrowsableSchemas names (Postgres Studio's default is public alone): withheld.
        await StatementsLive.ExecuteAsync(app,
            "create schema hr; "
            + "create table hr.salaries (id int primary key, amount int); "
            + "insert into hr.salaries select g, g * 10 from pg_catalog.generate_series(1, 50) g; "
            + "create view public.v_salaries as select s.id, s.amount from hr.salaries s; "
            + "create function hr.scaled(i int) returns int language plpgsql immutable as $$ begin return i * 2; end $$");

        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), v.amount from public.v_salaries v where v.id = @id", 20);
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where hr.scaled(o.id) = @id", 20);

        await using StudioHost host = StatementsLive.Host(app);
        await using StudioCircuit circuit = host.Circuit();
        IStatementsService statements = circuit.Get<IStatementsService>();

        StatementsView view = await statements.GetAsync(StatementsLive.Scope, Token);
        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));

        StatementPlanView throughView = await statements.PlanAsync(StatementsLive.Scope, Row(view, "v_salaries").QueryId, Token);
        throughView.Plan.Should().BeNull();
        throughView.NoPlan.Should().Be(StatementsService.PlanReadsWithheldSchema, "the view's plan reads hr.salaries, whose schema the visitor may not see");

        StatementRowView calling = Row(view, "scaled");
        calling.Text.Should().Contain(WithheldNames.Token + ".scaled(", "the text itself is masked");

        StatementPlanView masked = await statements.PlanAsync(StatementsLive.Scope, calling.QueryId, Token);
        masked.NoPlan.Should().BeNull();
        string filter = masked.Plan!.Nodes.Single(static x => x.RelationName == "orders").Filter!;
        filter.Should().Contain(WithheldNames.Token + ".scaled(").And.NotContain("hr.", "every name the plan prints is masked as the text is");
        JsonSerializer.Serialize(masked.Plan.Nodes.Select(static x => x.Description)).Should().NotContain("hr.");
    }
}
