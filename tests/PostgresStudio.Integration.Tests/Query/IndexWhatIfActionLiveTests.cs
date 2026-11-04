using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Statements;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// 1.2-INDEX-B against a real hypopg (<see cref="HypopgServer" />), through the Health page's own runner: T15's "What if it had an
/// index?" plans the foreign key's lookup now and with an index on the key's columns - and does so for a console role that may only
/// SELECT, because the lookup carries no row lock; it refuses a partitioned child and an absent hypopg; it creates nothing. T01's "Try
/// indexes" is the Statements advisor narrowed to the table, and a press is one audit entry.
/// </summary>
public class IndexWhatIfActionLiveTests(HypopgServer server) : IClassFixture<HypopgServer>
{
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
            + "insert into public.orders select g, 'n' || g, g % 1000 from pg_catalog.generate_series(1, 20000) g; "
            + "create table public.order_lines (id int primary key, order_id int not null references public.orders (id), a int not null, b int not null, note text); "
            + "insert into public.order_lines select g, (g % 20000) + 1, g % 100, g % 7, pg_catalog.md5(g::text) || pg_catalog.md5((g + 1)::text) "
            + "from pg_catalog.generate_series(1, 100000) g; "
            + "analyze public.orders; analyze public.order_lines");

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

    private static HealthReportFinding Finding(string code, string subject)
    {
        HealthFinding finding = new()
        {
            Code = code,
            Severity = HealthSeverity.Warning,
            Title = "A finding",
            WhyItMatters = "Because the test says so.",
            Subject = subject,
            Area = HealthArea.Tables,
            CheckTitle = code + " check",
            MeasuredAt = DateTimeOffset.UtcNow,
            Evidence = [new HealthEvidence("Setting", "value")],
            Remedy = HealthRemedy.ForMigration("CREATE INDEX CONCURRENTLY ON \"public\".\"order_lines\" (\"order_id\");", "It makes things better."),
        };

        return new HealthReportFinding(finding, finding.Evidence, HealthService.MuteKeyOf("default", "default", finding), "https://example.invalid/", true, null);
    }

    private static Task<HealthActionResult> PressAsync(StudioCircuit circuit, string code, string subject, string actionId) =>
        circuit.Get<IHealthActionService>().RunAsync(Scope, Finding(code, subject), actionId, Token);

    [PostgresFact]
    public async Task T15_plans_the_lookup_now_and_with_the_index_the_planner_would_use_and_creates_nothing()
    {
        (string database, _) = await SeedAsync("idx_b_t15", hypopg: true);
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder, pool: 1);
        await using StudioCircuit circuit = host.Circuit();
        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        HealthActionResult result = await PressAsync(circuit, "T15", "public.order_lines.order_lines_order_id_fkey", ForeignKeyWhatIfAction.ActionId);

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Summary.Should().StartWith("Postgres would use it: the plan's estimated cost goes from ");
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Cells[0].Text.Should().Be("Now");
        result.Rows[0].Cells[1].Text.Should().StartWith("Seq Scan on public.order_lines");
        result.Rows[1].Cells[0].Text.Should().Be("With the index");
        result.Rows[1].Cells[1].Text.Should().Contain("btree_order_lines_order_id");
        double costNow = double.Parse(result.Rows[0].Cells[3].Text, System.Globalization.CultureInfo.InvariantCulture);
        double costWith = double.Parse(result.Rows[1].Cells[3].Text, System.Globalization.CultureInfo.InvariantCulture);
        costWith.Should().BeLessThan(costNow);
        result.Remedies.Should().ContainSingle("the finding's own remedy is shown; the what-if writes none");
        result.Notes.Should().Contain(ForeignKeyWhatIfAction.LookupCaveat);
        result.Statements.Should().Contain(static x => x.Sql.Contains("pg_constraint", StringComparison.Ordinal));

        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "an imagined index is no catalog row");
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.Reset);

        await using NpgsqlConnection next = await host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Token);
        next.ProcessID.Should().Be(recorder.ProcessIds[0]);
        await using NpgsqlCommand count = new("select count(*) from public.hypopg()", next);
        ((long) (await count.ExecuteScalarAsync(Token))!).Should().Be(0);

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == "What-if foreign key lookup").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Contain("would be used").And.NotContain("order_lines");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_console_role_that_may_only_select_still_compares_because_the_lookup_carries_no_row_lock()
    {
        (string database, _) = await SeedAsync("idx_b_t15_select_only");
        await server.ExecuteAsync("grant select on public.order_lines to console_role", database, Token);
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder, static o => o.SqlConsoleRole = "console_role", role: "console_login");
        await using StudioCircuit circuit = host.Circuit();

        HealthActionResult result = await PressAsync(circuit, "T15", "public.order_lines.order_lines_order_id_fkey", ForeignKeyWhatIfAction.ActionId);

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Rows[1].Cells[1].Text.Should().Contain("btree_order_lines_order_id");
        recorder.Cleanups.Should().Equal(HypotheticalIndexCleanup.Reset);
    }

    [PostgresFact]
    public async Task A_key_that_is_partly_covered_is_compared_on_all_of_its_columns()
    {
        (string database, string app) = await SeedAsync("idx_b_t15_partly");
        await StatementsLive.ExecuteAsync(
            app,
            "create table public.pairs (a int, b int, primary key (a, b)); insert into public.pairs select g % 100, g % 7 from pg_catalog.generate_series(1, 700) g on conflict do nothing; "
            + "alter table public.order_lines add constraint order_lines_pair_fk foreign key (a, b) references public.pairs (a, b); "
            + "create index order_lines_a on public.order_lines (a); analyze public.order_lines");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        HealthActionResult result = await PressAsync(circuit, "T15", "public.order_lines.order_lines_pair_fk", ForeignKeyWhatIfAction.ActionId);

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Rows.Should().HaveCount(2);
        result.Notes.Should().Contain(ForeignKeyWhatIfAction.IndexNote);
    }

    [PostgresFact]
    public async Task A_partitioned_child_is_refused_and_hypopg_is_never_asked()
    {
        (string database, string app) = await SeedAsync("idx_b_t15_partitioned");
        await StatementsLive.ExecuteAsync(
            app,
            "create table public.events (id int not null, order_id int not null, region int) partition by range (id); "
            + "create table public.events_p1 partition of public.events for values from (0) to (1000000); "
            + "alter table public.events add constraint events_order_fk foreign key (order_id) references public.orders (id)");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        HealthActionResult result = await PressAsync(circuit, "T15", "public.events.events_order_fk", ForeignKeyWhatIfAction.ActionId);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Be(ForeignKeyWhatIfAction.PartitionedRefusal);
        recorder.Made.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_subject_the_catalog_does_not_know_is_refused_and_without_hypopg_the_sentence_is_the_answer()
    {
        (string database, _) = await SeedAsync("idx_b_t15_absent");
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        HealthActionResult gone = await PressAsync(circuit, "T15", "public.order_lines.no_such_fk", ForeignKeyWhatIfAction.ActionId);
        gone.Outcome.Should().Be(HealthActionOutcome.Refused);
        gone.Reason.Should().Be(ForeignKeyWhatIfAction.NotFound);

        (string plain, _) = await SeedAsync("idx_b_t15_no_hypopg", hypopg: false);
        await using StudioHost without = Host(plain, new AdvisorRecorder());
        await using StudioCircuit other = without.Circuit();

        HealthActionResult none = await PressAsync(other, "T15", "public.order_lines.order_lines_order_id_fkey", ForeignKeyWhatIfAction.ActionId);
        none.Outcome.Should().Be(HealthActionOutcome.Refused);
        none.Reason.Should().Be(HypotheticalIndexAvailability.NotInstalledSentence);
    }

    [PostgresFact]
    public async Task T01_tries_the_indexes_the_tables_statements_suggest_and_a_press_is_one_audit_entry()
    {
        (string database, string app) = await SeedAsync("idx_b_t01");
        await StatementsLive.RepeatAsync(app, "select pg_catalog.pg_sleep(0.01), o.note from public.orders o where o.customer = @id", 30);
        AdvisorRecorder recorder = new();
        await using StudioHost host = Host(database, recorder);
        await using StudioCircuit circuit = host.Circuit();

        HealthActionResult result = await PressAsync(circuit, "T01", "public.orders", SequentialScanWhatIfAction.ActionId);

        result.Outcome.Should().Be(HealthActionOutcome.Done, result.Reason);
        result.Summary.Should().StartWith("Add this index: saves ");
        HealthActionRow row = result.Rows.Should().ContainSingle().Subject;
        row.Cells[0].Text.Should().Be("An index on public.orders (customer)");
        row.Cells[4].Text.Should().Be("CREATE INDEX CONCURRENTLY ON \"public\".\"orders\" (\"customer\");");
        row.Cells[5].Link!.Query.Should().ContainSingle().Which.Value.Should().Be("public.orders");
        recorder.Cleanups.Should().NotBeEmpty().And.OnlyContain(static x => x == HypotheticalIndexCleanup.Reset);

        host.Ring.GetLatest().Should().NotContain(static x => x.Action == StatementsService.IndexAdviceAction, "the runner's entry is the press's");
        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == "Try indexes for a table").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        entry.Message.Should().Contain("would help").And.NotContain("orders");
    }
}
