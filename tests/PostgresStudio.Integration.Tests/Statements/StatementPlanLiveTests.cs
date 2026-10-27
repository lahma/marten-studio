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

    /// <summary>
    /// A view the visitor may see that reads a withheld schema's table plans as a scan of that table, and is not shown; a text
    /// that calls a <c>plpgsql</c> function in the withheld schema by its qualified name is not planned at all (SEC-FIX) - before
    /// 0.6.0's fix it was planned and shown masked, which a function's default arguments make a definition leak too (see
    /// <see cref="A_qualified_name_in_a_withheld_schema_is_not_planned_for_a_visitor_who_may_not_see_it_and_is_for_one_who_may" />).
    /// </summary>
    [PostgresFact]
    public async Task A_plan_that_reads_a_withheld_schema_is_not_shown_and_a_text_that_calls_into_one_is_not_planned()
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

        StatementPlanView named = await statements.PlanAsync(StatementsLive.Scope, calling.QueryId, Token);
        named.Plan.Should().BeNull();
        named.NoPlan.Should().Be(StatementsService.NamesWithheldSchema, "its text names hr, so it is not prepared");
        named.Statement!.Text.Should().Contain(WithheldNames.Token + ".scaled(", "the statement itself is still shown, masked");

        host.Ring.GetLatest().Where(static x => x.Action == StatementsService.PlanAction).Should().ContainSingle(
            "the view's plan was planned and not shown, and audited; the text that names hr was never planned");
    }

    /// <summary>
    /// SEC-FIX: a schema-qualified reference to a withheld schema's view, or to its <c>LANGUAGE sql</c> function, resolves
    /// whatever the <c>search_path</c> is, and the planner expands the view and inlines the function into nodes over tables the
    /// visitor may see - with <c>VERBOSE</c> printing their conditions and constants. So for a visitor who may not see
    /// <c>hr</c> none of these is planned (nor is a <c>plpgsql</c> call, whose plan prints the function's default argument);
    /// for one whose browser gate admits <c>hr</c> each is planned, and the plan shows exactly what the other visitor's would
    /// have shown.
    /// </summary>
    [PostgresFact]
    public async Task A_qualified_name_in_a_withheld_schema_is_not_planned_for_a_visitor_who_may_not_see_it_and_is_for_one_who_may()
    {
        (_, _, string app) = await SeedAsync(matrix, 18, "secfix_named");

        await StatementsLive.ExecuteAsync(app,
            "alter table public.orders add column tier text; "
            + "update public.orders set tier = case when id % 5 = 0 then 'vip' else 'std' end; "
            + "create schema hr; "
            + "create view hr.v_vip as select o.id, o.note from public.orders o where o.tier = 'vip'; "
            + "create function hr.is_gold(t text) returns boolean language sql immutable as $$ select t = 'gold' $$; "
            + "create function hr.graded(i int, floor int default 7) returns int language plpgsql immutable as $$ begin return greatest(i, floor); end $$");

        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), v.note from hr.v_vip v where v.id = @id", 20);
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where o.id = @id and hr.is_gold(o.tier)", 20);
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.02), o.note from public.orders o where hr.graded(o.id) = @id", 20);

        // What each plan carries of the definition behind the name, for a visitor who may see it.
        (string Marker, string Definition)[] cases =
        [
            ("v_vip", "'vip'::text"),
            ("is_gold", "'gold'::text"),
            ("graded", "graded(o.id, 7)"),
        ];

        // hr withheld - Postgres Studio's default BrowsableSchemas is public alone.
        await using (StudioHost withheld = StatementsLive.Host(app))
        {
            await using StudioCircuit circuit = withheld.Circuit();
            IStatementsService statements = circuit.Get<IStatementsService>();

            StatementsView view = await statements.GetAsync(StatementsLive.Scope, Token);
            view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));

            foreach ((string marker, string definition) in cases)
            {
                StatementRowView row = Row(view, marker);
                row.Text.Should().Contain(WithheldNames.Token + "." + marker, "the text names hr, masked");

                StatementPlanView plan = await statements.PlanAsync(StatementsLive.Scope, row.QueryId, Token);

                plan.Plan.Should().BeNull("a plan of {0} would show {1}", marker, definition);
                plan.NoPlan.Should().Be(StatementsService.NamesWithheldSchema);
            }

            withheld.Ring.GetLatest().Should().NotContain(static x => x.Action == StatementsService.PlanAction, "nothing was prepared");
        }

        // hr admitted: named exactly in BrowsableSchemas, with BrowseDatabase on and no write policy.
        await using (StudioHost admitted = StatementsLive.Host(app, studio: static o =>
        {
            o.BrowsableSchemas.Add("public");
            o.BrowsableSchemas.Add("hr");
        }))
        {
            await using StudioCircuit circuit = admitted.Circuit();
            IStatementsService statements = circuit.Get<IStatementsService>();

            StatementsView view = await statements.GetAsync(StatementsLive.Scope, Token);

            foreach ((string marker, string definition) in cases)
            {
                StatementRowView row = Row(view, marker);
                row.Text.Should().Contain("hr." + marker, "nothing is withheld from this visitor");

                StatementPlanView plan = await statements.PlanAsync(StatementsLive.Scope, row.QueryId, Token);

                plan.NoPlan.Should().BeNull(marker);
                string.Join("\n", plan.Plan!.Nodes.Select(static x => x.Filter + " " + x.IndexCondition))
                    .Should().Contain(definition, "the plan of {0} carries the definition behind the name", marker);
            }
        }
    }
}
