using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// The Plan page's facts (0.10-A, issue #28) on a real server, through the studio's own services: what the database knows about
/// the relations a VERBOSE plan names and the columns it uses, P15 to P18 on plans built to fire them, and the gates in the order
/// that is the security property - the console's gate, then each relation's schema (a schema the visitor may not see is
/// <c>‹withheld›</c> with no facts and no name in the audit), then the values' row gate (<c>BrowseDatabase</c>, the write policy,
/// never a Marten table), each read audited as it is. The matrix's half (every statement on 14 to 18, behind a held lock) is
/// <c>MatrixReads</c>' "plan facts" read.
/// </summary>
public class PlanFactsLiveTests(PlanFactsLiveTests.Fixture database) : IClassFixture<PlanFactsLiveTests.Fixture>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's database, seeded once.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The database's connection string.</summary>
        public string ConnectionString => owned![0];

        /// <summary>The second database, in which the console's role may not call <c>left()</c>: the values read fails there and nothing else does.</summary>
        public string FailingConnectionString => owned![1];

        /// <summary>The role that reads in the second database: a login-less role of this class's own, dropped with it.</summary>
        public const string ReaderRole = "ps_plan_facts_reader";

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(postgres, ["ps_plan_facts", "ps_plan_facts_fail"], CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + ReaderRole + "; create role " + ReaderRole + " nologin;", CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(
                FailingConnectionString,
                """
                create table public.orders (id int primary key, status text not null);
                insert into public.orders select g, case when g % 2 = 0 then 'open' else 'closed' end from pg_catalog.generate_series(1, 2000) g;
                analyze public.orders;
                grant usage on schema public to ps_plan_facts_reader;
                grant select on public.orders to ps_plan_facts_reader;
                revoke execute on function pg_catalog.left(text, integer) from public;
                """,
                CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(
                ConnectionString,
                """
                create table public.orders (id int primary key, status text not null, tier text not null, note text);
                insert into public.orders select g, case when g % 2 = 0 then 'open' else 'closed' end, case when g % 10 = 0 then 'gold' else 'basic' end, 'n' || g
                  from pg_catalog.generate_series(1, 10000) g;
                create index orders_status_idx on public.orders (status);
                analyze public.orders;

                create table public.users (id int primary key, email text not null);
                insert into public.users select g, 'user' || g || '@example.com' from pg_catalog.generate_series(1, 20000) g;
                create index users_email_idx on public.users (email);
                analyze public.users;

                -- Never analyzed, and told never to be: Postgres has no row estimate for it.
                create table public.never_analyzed (id int, status text) with (autovacuum_enabled = false);
                insert into public.never_analyzed select g, case when g % 2 = 0 then 'open' else 'closed' end from pg_catalog.generate_series(1, 20000) g;

                -- A postcode says which city it is in: city and postcode are not independent.
                create table public.addresses (id int primary key, city text not null, postcode text not null);
                insert into public.addresses select g, 'c' || (g % 20), 'p' || (g % 100) from pg_catalog.generate_series(1, 200000) g;
                analyze public.addresses;

                create table public.log (id int, ts date not null) partition by range (ts);
                create table public.log_1 partition of public.log for values from ('2026-01-01') to ('2026-02-01');
                create table public.log_2 partition of public.log for values from ('2026-02-01') to ('2026-03-01');
                create table public.log_3 partition of public.log for values from ('2026-03-01') to ('2026-04-01');
                create table public.log_4 partition of public.log for values from ('2026-04-01') to ('2026-05-01');
                create table public.log_5 partition of public.log for values from ('2026-05-01') to ('2026-06-01');
                create table public.log_6 partition of public.log for values from ('2026-06-01') to ('2026-07-01');
                insert into public.log select g, '2026-01-01'::date + (g % 180) from pg_catalog.generate_series(1, 6000) g;
                analyze public.log;

                create table public.mt_doc_order (id uuid primary key, data jsonb not null);
                insert into public.mt_doc_order select pg_catalog.gen_random_uuid(), '{"secret": "a Marten document"}' from pg_catalog.generate_series(1, 2000);
                analyze public.mt_doc_order;

                create schema secret;
                create type secret.mood as enum ('calm', 'angry');
                create table public.moods (id int, mood secret.mood not null);
                insert into public.moods select g, case when g % 2 = 0 then 'calm' else 'angry' end::secret.mood from pg_catalog.generate_series(1, 2000) g;
                analyze public.moods;

                -- A partition in public of a parent in a schema the visitor may not see.
                create table secret.parts (id int, k int not null) partition by range (k);
                create table public.parts_1 partition of secret.parts for values from (0) to (100);
                insert into public.parts_1 select g, g % 100 from pg_catalog.generate_series(1, 500) g;
                analyze public.parts_1;

                create table secret.salaries (id int primary key, amount int not null);
                insert into secret.salaries select g, 1000 + g from pg_catalog.generate_series(1, 2000) g;
                analyze secret.salaries;
                """,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
                await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + ReaderRole, CancellationToken.None);
            }
        }
    }

    private StudioHost Host(
        Action<PostgresStudioOptions> studio,
        Func<PostgresStudioResource, System.Security.Claims.ClaimsPrincipal, bool>? writePolicy = null) =>
        StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString)),
            studio,
            writePolicy: writePolicy);

    private static void RunSqlOnly(PostgresStudioOptions options) => options.Capabilities.RunSql = true;

    /// <summary>Plans <paramref name="sql" /> as the Plan page does - analyzed, VERBOSE - and reads its facts.</summary>
    private static async Task<(QueryPlan Plan, PlanFacts Facts)> FactsOfAsync(StudioCircuit circuit, string sql, bool analyze = true)
    {
        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope, new PlanRunRequest(sql, analyze ? PlanMode.Analyze : PlanMode.Plan) { Options = new PlanOptions { Verbose = true } }, Token);

        result.Error.Should().BeNull();
        result.Rejection.Should().BeNull();

        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        PlanFacts facts = await circuit.Get<IPlanFactsService>().ReadAsync(Scope, plan, Token);

        return (plan, facts);
    }

    private static IReadOnlyList<string> Codes(StudioCircuit circuit, QueryPlan plan, PlanFacts facts) =>
        [.. PlanInsights.Evaluate(plan, circuit.Get<IStudioSettings>().Text, facts).Select(static x => x.Insight.Code)];

    // ---- The facts ------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_facts_of_an_analyzed_table_are_its_estimate_its_indexes_its_columns_and_the_planner_settings()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (QueryPlan plan, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.orders where status = 'open' and tier = 'gold'");

        facts.Failure.Should().BeNull();
        facts.Unqualified.Should().Be(0, "VERBOSE gives every relation its schema");

        PlanRelationFacts orders = facts.Relations.Should().ContainSingle().Subject;
        orders.State.Should().Be(PlanRelationState.Read);
        orders.Label.Should().Be("public.orders");
        orders.Kind.Should().Be("r");
        orders.EstimatedRows.Should().BeInRange(9_000, 11_000);
        orders.Pages.Should().BeGreaterThan(0);
        orders.LastAnalyze.Should().NotBeNull("ANALYZE was the last thing the fixture did to it");
        orders.NeverAnalyzed.Should().BeFalse();
        orders.Indexes.Select(static x => x.Name).Should().BeEquivalentTo("orders_pkey", "orders_status_idx");
        PlanIndexFact primary = orders.Indexes.Single(static x => x.Name == "orders_pkey");
        primary.IsPrimary.Should().BeTrue();
        primary.KeyColumns.Should().Equal("id");
        orders.Indexes.Single(static x => x.Name == "orders_status_idx").SizeBytes.Should().BeGreaterThan(0);
        orders.ExtendedStatistics.Should().BeEmpty();

        orders.Columns.Select(static x => x.Column).Should().BeEquivalentTo("status", "tier");
        PlanColumnFact status = orders.FindColumn("status")!;
        status.HasStatistics.Should().BeTrue();
        status.Distinct.Should().Be(2);
        status.NullFraction.Should().Be(0);
        status.Type.Should().Be("text");

        orders.Values.Should().Be(PlanValuesState.NeedsBrowse, "BrowseDatabase is off, so the values stay in the database");
        orders.Columns.Should().OnlyContain(static x => x.Values == null);

        facts.Settings.Select(static x => x.Name).Should().Contain(["work_mem", "random_page_cost", "default_statistics_target"]);
        facts.Settings.Should().OnlyContain(static x => !x.FromPlan, "the plan was not asked for SETTINGS");
        plan.Nodes.Should().Contain(static x => x.RelationName == "orders");
    }

    [PostgresFact]
    public async Task A_table_that_was_never_analyzed_fires_P15_behind_the_misestimate()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (QueryPlan plan, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.never_analyzed where status = 'open'");

        PlanRelationFacts relation = facts.Relations.Should().ContainSingle().Subject;
        relation.NeverAnalyzed.Should().BeTrue();
        relation.EstimatedRows.Should().BeNull("reltuples is -1 until the first ANALYZE or VACUUM");

        PlanInsights.Evaluate(plan, circuit.Get<IStudioSettings>().Text, facts).Single(static x => x.Insight.Code == "P15").Message
            .Should().Contain("public.never_analyzed has never been analyzed");
    }

    [PostgresFact]
    public async Task Two_columns_of_one_functional_dependency_compared_together_fire_P16_and_extended_statistics_quiet_it()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        const string sql = "select * from public.addresses where city = 'c3' and postcode = 'p3'";
        (QueryPlan plan, PlanFacts facts) = await FactsOfAsync(circuit, sql);

        PlanNode scan = plan.Nodes.Single(static x => x.RelationName == "addresses");
        scan.PlanRows.Should().BeLessThan(200, "independent columns: 200,000 x 1/20 x 1/100 is 100, per worker less");
        (scan.ActualRows * scan.ActualLoops).Should().Be(2_000, "a postcode is in one city: every tenth of a percent of the rows");

        PlanInsightFinding finding = PlanInsights.Evaluate(plan, circuit.Get<IStudioSettings>().Text, facts).Single(static x => x.Insight.Code == "P16");
        finding.Remedy.Should().Contain("CREATE STATISTICS \"public\".\"addresses_city_postcode_stats\"");

        // The statement P16 offers, run by the test the way a host would run it from its own migration: the finding goes quiet.
        await OwnedDatabases.ExecuteAsync(
            database.ConnectionString,
            MigrationSqlText.CreateStatistics("public", "addresses", ["city", "postcode"])! + " analyze public.addresses;",
            Token);

        try
        {
            (QueryPlan after, PlanFacts afterFacts) = await FactsOfAsync(circuit, sql);

            afterFacts.Relations.Single().ExtendedStatistics.Should().ContainSingle().Which.Columns.Should().BeEquivalentTo("city", "postcode");
            Codes(circuit, after, afterFacts).Should().NotContain("P16", "extended statistics now cover the two columns");
        }
        finally
        {
            await OwnedDatabases.ExecuteAsync(database.ConnectionString, "drop statistics public.addresses_city_postcode_stats; analyze public.addresses;", CancellationToken.None);
        }
    }

    [PostgresFact]
    public async Task A_function_around_an_indexed_column_fires_P17_and_the_bare_column_does_not()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (QueryPlan plan, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.users where lower(email) = 'user42@example.com'");
        plan.Nodes.Single(static x => x.RelationName == "users").NodeType.Should().Be("Seq Scan", "lower(email) cannot use the index on email");

        PlanInsights.Evaluate(plan, circuit.Get<IStudioSettings>().Text, facts).Single(static x => x.Insight.Code == "P17").Message
            .Should().Contain("lower()").And.Contain("public.users.email").And.Contain("users_email_idx");

        (QueryPlan bare, PlanFacts bareFacts) = await FactsOfAsync(circuit, "select * from public.users where email = 'user42@example.com'");
        Codes(circuit, bare, bareFacts).Should().NotContain("P17");
    }

    [PostgresFact]
    public async Task A_scan_of_every_partition_fires_P18_and_a_filter_on_the_key_prunes_it_away()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (QueryPlan plan, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.log where id < 100");

        facts.Relations.Should().HaveCount(6).And.OnlyContain(static x => x.Partition != null && x.Partition.ParentName == "log" && x.Partition.Partitions == 6);
        facts.Relations[0].Partition!.KeyColumns.Should().Equal("ts");

        PlanInsights.Evaluate(plan, circuit.Get<IStudioSettings>().Text, facts).Single(static x => x.Insight.Code == "P18").Message
            .Should().Contain("reads 6 of the 6 partitions of public.log").And.Contain("partition key (ts)");

        (QueryPlan pruned, PlanFacts prunedFacts) = await FactsOfAsync(circuit, "select * from public.log where ts >= '2026-01-01' and ts < '2026-02-01'");
        Codes(circuit, pruned, prunedFacts).Should().NotContain("P18", "only one partition is read");
    }

    // ---- The gate -------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_relation_in_a_schema_the_visitor_may_not_see_is_withheld_with_no_facts_and_its_name_is_nowhere_in_the_audit()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        circuit.Get<IStudioSettings>().BrowsableSchemas.Should().Equal(["public"], "the premise: with nothing named, public is the one schema");

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select o.id from public.orders o join secret.salaries s on s.id = o.id where s.amount > 1500");

        facts.Relations.Should().HaveCount(2);
        facts.Relations.Single(static x => x.State == PlanRelationState.Read).Label.Should().Be("public.orders");

        PlanRelationFacts withheld = facts.Relations.Single(static x => x.State == PlanRelationState.Withheld);
        withheld.Label.Should().Be(PlanFacts.Withheld);
        withheld.Indexes.Should().BeEmpty();
        withheld.Columns.Should().BeEmpty();
        withheld.ExtendedStatistics.Should().BeEmpty();
        withheld.EstimatedRows.Should().BeNull();
        withheld.Pages.Should().BeNull();

        // Nothing the facts say, and nothing audited, names the schema or the table.
        string everything = JsonSerializer.Serialize(facts)
            + string.Join("|", host.Ring.GetLatest().Where(static x => x.Action is PlanFactsService.FactsAction or PlanFactsService.ValuesAction).Select(static x => x.Target + " " + x.Message));
        everything.Should().NotContain("salaries").And.NotContain("secret");

        // The plan's own run is audited with the statement the visitor typed, as every console run is; the facts' read names no relation.
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == PlanFactsService.FactsAction).Which.Message.Should().Be("read for 2 relations");
    }

    [PostgresFact]
    public async Task Naming_the_schema_in_BrowsableSchemas_with_BrowseDatabase_on_lifts_the_withholding()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.BrowsableSchemas.Clear();
            o.BrowsableSchemas.Add("public");
            o.BrowsableSchemas.Add("secret");
        });
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from secret.salaries where amount > 1500");

        PlanRelationFacts salaries = facts.Relations.Should().ContainSingle().Subject;
        salaries.State.Should().Be(PlanRelationState.Read);
        salaries.Label.Should().Be("secret.salaries");
        salaries.Indexes.Should().ContainSingle().Which.Name.Should().Be("salaries_pkey");
    }

    [PostgresFact]
    public async Task Values_are_read_past_the_row_gate_and_each_relations_read_is_audited_as_a_BrowseDatabase_read()
    {
        await using StudioHost host = Host(static o => o.Capabilities = PostgresStudioCapabilities.All());
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.orders where status = 'open'");

        PlanRelationFacts orders = facts.Relations.Single();
        orders.Values.Should().Be(PlanValuesState.Read);
        orders.FindColumn("status")!.Values!.MostCommonValues.Should().Contain("open").And.Contain("closed");

        IReadOnlyList<StudioActionLogEntry> ring = host.Ring.GetLatest();
        StudioActionLogEntry values = ring.Should().ContainSingle(static x => x.Action == PlanFactsService.ValuesAction).Subject;
        values.Target.Should().Be("public.orders");
        values.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
        values.Succeeded.Should().BeTrue();
        values.TenantId.Should().BeNull();

        StudioActionLogEntry read = ring.Should().ContainSingle(static x => x.Action == PlanFactsService.FactsAction).Subject;
        read.Capability.Should().Be(nameof(StudioCapability.RunSql));
        read.Succeeded.Should().BeTrue();
        read.Target.Should().Be("plan facts", "the read names no relation");
        read.Message.Should().Be("read for 1 relation");
    }

    [PostgresFact]
    public async Task Without_BrowseDatabase_the_values_stay_in_the_database_and_nothing_is_audited_as_a_browse_read()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.orders where status = 'open'");

        facts.Relations.Single().Values.Should().Be(PlanValuesState.NeedsBrowse);
        facts.Relations.Single().Columns.Should().OnlyContain(static x => x.Values == null);
        host.Ring.GetLatest().Should().NotContain(static x => x.Action == PlanFactsService.ValuesAction || x.Capability == nameof(StudioCapability.BrowseDatabase),
            "a visitor without BrowseDatabase fills the audit with nothing");
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_BrowseDatabase_leaves_the_values_where_they_are_while_RunSql_still_reads_the_structure()
    {
        await using StudioHost host = Host(
            static o => o.Capabilities = PostgresStudioCapabilities.All(),
            writePolicy: static (resource, _) => resource.Capability != nameof(StudioCapability.BrowseDatabase));
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.orders where status = 'open'");

        facts.Failure.Should().BeNull();
        facts.Relations.Single().State.Should().Be(PlanRelationState.Read);
        facts.Relations.Single().Values.Should().Be(PlanValuesState.NeedsBrowse);
        facts.Relations.Single().Columns.Should().OnlyContain(static x => x.Values == null);
        host.Ring.GetLatest().Should().NotContain(static x => x.Action == PlanFactsService.ValuesAction);
    }

    [PostgresFact]
    public async Task A_Marten_document_table_shows_its_structure_and_never_its_values_whatever_the_options_say()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.BrowsableSchemas.Clear();
            o.BrowsableSchemas.Add("*");
        });
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.mt_doc_order where data ->> 'secret' = 'x'");

        PlanRelationFacts order = facts.Relations.Should().ContainSingle().Subject;
        order.State.Should().Be(PlanRelationState.Read);
        order.Columns.Select(static x => x.Column).Should().Contain("data");
        order.Indexes.Should().ContainSingle().Which.Name.Should().Be("mt_doc_order_pkey");
        order.Values.Should().Be(PlanValuesState.Refused);
        order.ValuesNote.Should().NotBeNullOrWhiteSpace();
        order.Columns.Should().OnlyContain(static x => x.Values == null);

        JsonSerializer.Serialize(facts).Should().NotContain("a Marten document");
        host.Ring.GetLatest().Should().NotContain(static x => x.Action == PlanFactsService.ValuesAction);
    }

    [PostgresFact]
    public async Task Without_RunSql_nothing_is_read_and_the_refusal_is_audited()
    {
        await using StudioHost host = Host(static o => o.Capabilities.RunSql = false);
        await using StudioCircuit circuit = host.Circuit();

        QueryPlan plan = QueryPlanReader.Read(
            """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"orders","Schema":"public","Alias":"orders","Total Cost":10}}]""").Plan!;

        Func<Task> read = () => circuit.Get<IPlanFactsService>().ReadAsync(Scope, plan, Token);

        await read.Should().ThrowAsync<StudioCapabilityDeniedException>();
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == PlanFactsService.FactsAction && !x.Succeeded && x.Capability == nameof(StudioCapability.RunSql));
    }

    [PostgresFact]
    public async Task A_plan_run_without_VERBOSE_names_no_schema_so_nothing_is_resolved_and_the_page_can_say_why()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(Scope, new PlanRunRequest("select * from public.orders where status = 'open'", PlanMode.Analyze), Token);
        QueryPlan plan = QueryPlanReader.Read(result.PlanJson).Plan!;
        PlanFacts facts = await circuit.Get<IPlanFactsService>().ReadAsync(Scope, plan, Token);

        facts.Relations.Should().BeEmpty();
        facts.Unqualified.Should().Be(1);
        facts.Failure.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_column_type_in_a_schema_the_visitor_may_not_see_is_masked()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.moods where mood = 'calm'");

        PlanRelationFacts moods = facts.Relations.Should().ContainSingle().Subject;
        moods.State.Should().Be(PlanRelationState.Read);

        string type = moods.FindColumn("mood")!.Type!;
        type.Should().Contain(PlanFacts.Withheld).And.NotContain("secret", "the read pins search_path to pg_catalog, so Postgres prints the type with its schema");
        JsonSerializer.Serialize(facts).Should().NotContain("secret");
    }

    [PostgresFact]
    public async Task A_partition_whose_root_is_in_a_schema_the_visitor_may_not_see_is_withheld_with_neither_name()
    {
        await using StudioHost host = Host(RunSqlOnly);
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.parts_1 where k = 5");

        PlanRelationFacts partition = facts.Relations.Should().ContainSingle().Subject;
        partition.State.Should().Be(PlanRelationState.Withheld, "public.parts_1 is visible by its schema, but its root, secret.parts, is not");
        partition.Schema.Should().BeEmpty();
        partition.Name.Should().BeEmpty();
        partition.Partition.Should().BeNull();

        // The whole value, not the records that were read: nothing in it - nor in what the facts audited - names either table.
        string everything = JsonSerializer.Serialize(facts) + string.Join("|", host.Ring.GetLatest().Where(static x => x.Action is PlanFactsService.FactsAction or PlanFactsService.ValuesAction).Select(static x => x.Target + " " + x.Message));
        everything.Should().NotContain("parts").And.NotContain("secret");
    }

    [PostgresFact]
    public async Task A_values_read_that_fails_leaves_a_failed_BrowseDatabase_entry_for_the_relation()
    {
        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(database.FailingConnectionString)),
            static o =>
            {
                o.Capabilities = PostgresStudioCapabilities.All();
                o.SqlConsoleRole = Fixture.ReaderRole;
            });
        await using StudioCircuit circuit = host.Circuit();

        (_, PlanFacts facts) = await FactsOfAsync(circuit, "select * from public.orders where status = 'open'");

        facts.Failure.Should().NotBeNull("the values statement calls left(), which this role may not");

        IReadOnlyList<StudioActionLogEntry> ring = host.Ring.GetLatest();
        StudioActionLogEntry values = ring.Should().ContainSingle(static x => x.Action == PlanFactsService.ValuesAction).Subject;
        values.Succeeded.Should().BeFalse();
        values.Target.Should().Be("public.orders");
        values.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
        values.Message.Should().Contain("SQLSTATE 42501").And.NotContain("permission denied", "the studio's sentence, never Postgres' words");

        ring.Should().ContainSingle(static x => x.Action == PlanFactsService.FactsAction && !x.Succeeded);
    }
}
