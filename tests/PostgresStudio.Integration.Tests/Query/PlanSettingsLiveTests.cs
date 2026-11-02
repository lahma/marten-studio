using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// 0.10-B (issue #28) on the version matrix - 14, 17 and 18: a setting changes the plan of the run that names it and of no other, a
/// value that is an attack is a parameter Postgres refuses as a value and drops nothing, the server's offered list is what its
/// <c>pg_settings</c> has of the allow-list, a statement timeout never goes above the host's, and the run's audit entry names what
/// it changed. The offered list on every version, and <c>set_config</c> in a read-only transaction by a role with no privilege, are
/// <c>MatrixReads</c>' "plan settings" read.
/// </summary>
public class PlanSettingsLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<int> Legs => [15, 17, 18, 19];

    private const string Seed =
        """
        create table public.orders (id int primary key, note text);
        insert into public.orders select g, 'note ' || g from pg_catalog.generate_series(1, 20000) g;
        analyze public.orders;
        """;

    private sealed record Harness(StudioHost Host, NpgsqlDataSource Source) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Source.DisposeAsync();
        }
    }

    /// <summary>A host over one data source of one connection that is never reset on close: a setting a run leaked would still be on the session the next one gets.</summary>
    private static async Task<Harness> HostAsync(PostgresVersionMatrix matrix, int major, TimeSpan? queryTimeout = null)
    {
        MatrixServer leg = await matrix.ServerAsync(major);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync("b_plan_settings", Token);
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
            o =>
            {
                o.Capabilities.RunSql = true;

                if (queryTimeout is { } timeout)
                {
                    o.QueryTimeout = timeout;
                }
            });

        return new Harness(host, source);
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource source, string sql)
    {
        await using NpgsqlConnection connection = await source.OpenConnectionAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> NodeTypes(PlanRunResult result) =>
        [.. QueryPlanReader.Read(result.PlanJson).Plan!.Nodes.Select(static x => x.NodeType)];

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_setting_changes_the_plan_of_the_run_that_names_it_and_of_no_other(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Statement = "select * from public.orders where id = 5";

        PlanRunResult before = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan), Token);
        PlanRunResult changed = await console.PlanAsync(
            Scope,
            new PlanRunRequest(Statement, PlanMode.Plan) { SessionSettings = [new PlanSetting("enable_indexscan", "off"), new PlanSetting("enable_bitmapscan", "off")] },
            Token);
        PlanRunResult after = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan), Token);

        before.Error.Should().BeNull();
        changed.Error.Should().BeNull(changed.Error?.MessageText);
        NodeTypes(before).Should().Equal("Index Scan");
        NodeTypes(changed).Should().Equal("Seq Scan");
        NodeTypes(after).Should().Equal(["Index Scan"], "the settings lasted for one transaction, which was rolled back");

        (await ScalarAsync(harness.Source, "select (current_setting('enable_indexscan') = 'on')::int")).Should().Be(1, "nothing leaked onto the session");

        StudioActionLogEntry entry = harness.Host.Ring.GetLatest().Should().Contain(static x => x.Message != null && x.Message.Contains("enable_indexscan=off", StringComparison.Ordinal)).Subject;
        entry.Message.Should().Contain("enable_bitmapscan=off");
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_value_that_is_an_attack_reaches_Postgres_only_as_a_parameter_and_fails_as_a_value_and_drops_nothing(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope,
            new PlanRunRequest("select * from public.orders", PlanMode.Plan) { SessionSettings = [new PlanSetting("random_page_cost", "1'; drop table public.orders; --")] },
            Token);

        result.Rejection.Should().BeNull("the studio does not read a value; Postgres does");
        result.Error.Should().NotBeNull();
        result.Error!.SqlState.Should().Be("22023", "invalid_parameter_value: Postgres refused it as a random_page_cost");
        result.ErrorOffset.Should().Be(-1, "the error is not in the typed statement");

        (await ScalarAsync(harness.Source, "select count(*) from public.orders")).Should().Be(20000);

        StudioActionLogEntry entry = harness.Host.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("random_page_cost=1'; drop table public.orders; --", "what a run changed is named, whole, as the visitor typed it");
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task The_server_offers_what_pg_settings_has_of_the_allow_list_and_a_run_that_did_not_ask_reads_nothing(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        PlanRunResult quiet = await console.PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Plan), Token);
        PlanRunResult asked = await console.PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Plan) { OfferSettings = true }, Token);

        quiet.SettingOffers.Should().BeEmpty();
        asked.SettingOffers.Select(static x => x.Name).Should().Contain(["work_mem", "jit", "enable_seqscan", "enable_hashjoin"]).And.OnlyContain(static x => PlanSettingsQueries.IsAllowed(x));

        PlanSettingRow workMem = asked.SettingOffers.Single(static x => x.Name == "work_mem");
        workMem.VarType.Should().Be("integer");
        workMem.Unit.Should().Be("kB");
        workMem.MinValue.Should().Be("64");

        asked.SettingOffers.Single(static x => x.Name == "jit").VarType.Should().Be("bool");
        asked.SettingOffers.Select(static x => PlanSettingsQueries.Allowed.ToList().IndexOf(x.Name)).Should().BeInAscendingOrder("listed in the allow-list's order");
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_timeout_is_clamped_to_the_hosts_and_the_statement_that_outlasts_it_is_cancelled(int major)
    {
        await using Harness harness = await HostAsync(matrix, major, queryTimeout: TimeSpan.FromSeconds(2));
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        PlanRunResult clamped = await console.PlanAsync(
            Scope, new PlanRunRequest("select pg_sleep(30)", PlanMode.Analyze) { TimeoutSeconds = 3600 }, Token);

        clamped.TimeoutSeconds.Should().Be(2, "an hour is held to the host's two seconds");
        clamped.Error.Should().NotBeNull();
        clamped.Error!.SqlState.Should().Be("57014", "query_canceled by statement_timeout");

        PlanRunResult shorter = await console.PlanAsync(
            Scope, new PlanRunRequest("select pg_sleep(30)", PlanMode.Analyze) { TimeoutSeconds = 1 }, Token);

        shorter.TimeoutSeconds.Should().Be(1);
        shorter.Error!.SqlState.Should().Be("57014");
        harness.Host.Ring.GetLatest().Should().Contain(static x => x.Message != null && x.Message.Contains("statement_timeout=1s", StringComparison.Ordinal));

        PlanRunResult own = await console.PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Plan), Token);
        own.TimeoutSeconds.Should().Be(2, "no override is the host's own");
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_custom_plan_and_the_generic_plan_of_one_statement_are_two_runs_with_a_prepared_statement_left_behind_by_neither(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();
        ISqlConsoleService console = circuit.Get<ISqlConsoleService>();

        const string Statement = "select * from public.orders where id = $1";

        PlanRunResult generic = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Generic), Token);
        PlanRunResult custom = await console.PlanAsync(Scope, new PlanRunRequest(Statement, PlanMode.Plan) { Values = ["5"] }, Token);

        generic.Error.Should().BeNull();
        custom.Error.Should().BeNull();

        QueryPlan genericPlan = QueryPlanReader.Read(generic.PlanJson).Plan!;
        QueryPlan customPlan = QueryPlanReader.Read(custom.PlanJson).Plan!;

        PlanComparison comparison = PlanDiff.Compare(genericPlan, customPlan);
        comparison.Entries.Should().NotBeEmpty();
        (await ScalarAsync(harness.Source, "select count(*) from pg_catalog.pg_prepared_statements")).Should().Be(0);
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task A_setting_off_the_allow_list_is_refused_before_it_reaches_the_server_and_audited(int major)
    {
        await using Harness harness = await HostAsync(matrix, major);
        await using StudioCircuit circuit = harness.Host.Circuit();

        PlanRunResult result = await circuit.Get<ISqlConsoleService>().PlanAsync(
            Scope,
            new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("session_replication_role", "replica")] },
            Token);

        result.Rejection!.Reason.Should().Be(PlanRequests.SettingNotAllowedReason);
        result.PlanJson.Should().BeNull();
        harness.Host.Ring.GetLatest().Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }
}
