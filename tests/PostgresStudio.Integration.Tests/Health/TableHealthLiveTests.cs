using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// The table checks against a plain database, read through PS-3b's whole-database table source: each one made to fire
/// by producing its condition with plain SQL in the test's own schema, and all of them quiet on healthy tables - with a
/// recognised library's table exempt from the index rules alone, and every remedy a migration statement whose
/// identifiers are quoted.
/// </summary>
/// <remarks>
/// <para>
/// The same conditions <c>MartenStudio.Integration.Tests</c>' <c>TableHealthFiresLiveTests</c> produces on a Marten
/// store's tables, produced here on the application's own: what differs is the table source (every table of the schemas
/// <c>BrowsableSchemas</c> names, each the host's, a library's or somebody else's) and the remedy, which is SQL for the
/// host's migrations rather than a line of Marten configuration.
/// </para>
/// <para>
/// <b>Wait on the condition, never on the clock.</b> Every write ends with <c>pg_stat_force_next_flush()</c>, so the
/// counters a check reads have moved before the next statement; a check that needs a rate gets two sweeps a moved
/// <see cref="ManualClock" /> apart.
/// </para>
/// </remarks>
public class TableHealthLiveTests(PostgresFixture postgres)
{
    private static readonly string[] TableCodes = ["T01", "T03", "T04", "T05", "T06", "T07", "T08", "T12"];

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A studio clock eight days past the server's: T01, T05 and T06 give no usage verdict over statistics under seven days
    /// old (issue #8 G4), and a fresh container's are minutes old. Paired with an <c>ANALYZE</c> of a table in the schema, so
    /// that a database whose own reset time is null (15+) records a moment its counters provably cover (W5-B1).
    /// </summary>
    private static DateTimeOffset Covering => DateTimeOffset.UtcNow.AddDays(8);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("T01")]
    public async Task Sequential_scans_that_keep_reading_a_large_table_are_T01_with_an_index_to_add_in_a_migration()
    {
        const string schema = "ps_health_t01";
        ManualClock clock = new(Covering);
        await using StudioHost host = await HostAsync(schema, clock);

        // No ANALYZE in the same batch: run in the backend whose insert counts are still pending, it reports its own estimate
        // and the flush then adds the inserts again - 240,000 live rows for 120,000, and every whole-table read counted half.
        await ExecuteAsync(
            "create table ps_health_t01.orders (id int primary key, customer int not null, note text)",
            "insert into ps_health_t01.orders select g, g % 1000, 'n' || g from generate_series(1, 120000) g");
        await ExecuteAsync("create table ps_health_t01.anchor (id int primary key)", "analyze ps_health_t01.anchor");

        OnTable(await ReportAsync(host), "T01", schema + ".orders").Should().BeEmpty("one sample is not a rate");

        await ExecuteAsync(
            "set enable_indexscan = off",
            "set enable_indexonlyscan = off",
            "set enable_bitmapscan = off",
            "set max_parallel_workers_per_gather = 0",
            "select pg_catalog.count(*) from ps_health_t01.orders where customer = 1",
            "select pg_catalog.count(*) from ps_health_t01.orders where customer = 2",
            "select pg_catalog.count(*) from ps_health_t01.orders where customer = 3",
            "select pg_catalog.count(*) from ps_health_t01.orders where customer = 4");

        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T01", schema + ".orders").Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().Be(schema + ".orders");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy!.Code.Should().Contain("CREATE INDEX CONCURRENTLY ON \"ps_health_t01\".\"orders\" (");
        finding.Finding.Remedy.Explanation.Should().Contain(HealthRemedy.HandOverNote, "the studio never runs it");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>The quoting half: a schema, table and index whose names need quotes, in the remedy as Postgres needs them.</summary>
    [PostgresFact]
    [HealthFires("T03")]
    public async Task A_failed_concurrent_unique_build_is_a_critical_T03_whose_reindex_quotes_every_name()
    {
        const string schema = "PS Health T03";
        await using StudioHost host = await HostAsync(schema, new ManualClock(Start));

        string table = SqlIdentifier.Qualify(schema, "Order Lines");
        await ExecuteAsync(
            "create table " + table + " (id int primary key, \"Line Name\" text not null)",
            "insert into " + table + " values (1, 'same'), (2, 'same')");

        Func<Task> build = () => ExecuteAsync("create unique index concurrently \"Lines By Name\" on " + table + " (\"Line Name\")");
        (await build.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505", "two rows share the name");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T03", schema + ".Order Lines").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Finding.Subject.Should().Be(schema + ".Order Lines.Lines By Name");
        finding.Finding.Remedy!.Code.Should().Be("REINDEX INDEX CONCURRENTLY \"PS Health T03\".\"Lines By Name\";");
    }

    /// <summary>
    /// The library half: the same redundant pair on the application's table and on a Quartz.NET table beside it, and only
    /// the application's is called redundant - a library's index design is the library's.
    /// </summary>
    [PostgresFact]
    [HealthFires("T04")]
    public async Task An_index_a_wider_one_starts_with_is_T04_on_the_hosts_table_and_never_on_a_libraries()
    {
        const string schema = "ps_health_t04";
        await using StudioHost host = await HostAsync(schema, new ManualClock(Start));

        await ExecuteAsync(
            "create table ps_health_t04.orders (id int primary key, name text not null, rank int not null)",
            "create index orders_by_name on ps_health_t04.orders (name)",
            "create index orders_by_name_rank on ps_health_t04.orders (name, rank)",
            "create table ps_health_t04.qrtz_triggers (id int primary key, name text not null, rank int not null)",
            "create index qrtz_by_name on ps_health_t04.qrtz_triggers (name)",
            "create index qrtz_by_name_rank on ps_health_t04.qrtz_triggers (name, rank)");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = OnTable(report, "T04", schema + ".orders").Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().Be(schema + ".orders.orders_by_name");
        finding.Finding.Remedy!.Code.Should().Be(
            "-- \"orders_by_name_rank\" already does this index's whole job:\nDROP INDEX CONCURRENTLY \"ps_health_t04\".\"orders_by_name\";");
        finding.Evidence.Should().Contain(static x => x.Label == "How" && x.Value == "a leading prefix of a b-tree index");

        OnTable(report, "T04", schema + ".qrtz_triggers").Should().BeEmpty("a library's table is exempt from the index-design rules");
    }

    [PostgresFact]
    [HealthFires("T05")]
    public async Task An_index_nothing_scans_on_a_table_of_a_hundred_thousand_inserts_is_T05_on_the_hosts_table_only()
    {
        const string schema = "ps_health_t05";
        await using StudioHost host = await HostAsync(schema, new ManualClock(Covering));

        foreach (string table in new[] { "orders", "qrtz_fired_triggers" })
        {
            await ExecuteAsync(
                $"create table ps_health_t05.{table} (id int primary key, name text not null)",
                $"create index {table}_by_name on ps_health_t05.{table} (name)",
                $"insert into ps_health_t05.{table} select g, 'name ' || g from generate_series(1, 110000) g",
                // ANALYZE refreshes the index's relpages, which is what the check sizes it by.
                $"analyze ps_health_t05.{table}");
        }

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = OnTable(report, "T05", schema + ".orders").Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().Be(schema + ".orders.orders_by_name");
        finding.Finding.Remedy!.Code.Should().Be("DROP INDEX CONCURRENTLY \"ps_health_t05\".\"orders_by_name\";");
        finding.Finding.Caveat.Should().Contain("a usage verdict is never given over less than 7 days");

        OnTable(report, "T05", schema + ".qrtz_fired_triggers").Should().BeEmpty("a library's table is exempt from the index-design rules");
    }

    /// <summary>
    /// T06 judges a Marten document table - the one kind of table whose every write rewrites its row - and on Postgres
    /// Studio a table the <c>mt_</c> rule calls Marten's is told what is wrong and given no migration SQL: its store
    /// manages its storage, and Marten Studio is where its remedy is worded.
    /// </summary>
    [PostgresFact]
    [HealthFires("T06")]
    public async Task Updates_on_full_pages_of_a_Marten_document_table_are_a_T06_warning_with_no_migration_SQL()
    {
        const string schema = "ps_health_t06";
        ManualClock clock = new(Covering);
        await using StudioHost host = await HostAsync(schema, clock);

        await ExecuteAsync(
            "create table ps_health_t06.mt_doc_thing (id uuid primary key, data jsonb not null)",
            "insert into ps_health_t06.mt_doc_thing select gen_random_uuid(), jsonb_build_object('Name', 'n' || g, 'Rank', g) from generate_series(1, 12000) g");
        await ExecuteAsync("create table ps_health_t06.anchor (id int primary key)", "analyze ps_health_t06.anchor");
        await ReportAsync(host);

        await ExecuteAsync("update ps_health_t06.mt_doc_thing set data = data || jsonb_build_object('Rank', 0)");

        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T06", schema + ".mt_doc_thing").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Contain("were HOT updates");
        finding.Finding.Remedy.Should().BeNull("a Marten table is its store's to configure, not the host's migrations'");
    }

    /// <summary>A library's table is judged on vacuum like any other, and is told how to switch autovacuum back on.</summary>
    [PostgresFact]
    [HealthFires("T07")]
    public async Task Half_a_table_dead_with_autovacuum_off_is_T07_on_the_hosts_table_and_on_a_libraries()
    {
        const string schema = "ps_health_t07";
        await using StudioHost host = await HostAsync(schema, new ManualClock(Start));

        foreach (string table in new[] { "orders", "qrtz_triggers" })
        {
            await ExecuteAsync(
                $"create table ps_health_t07.{table} (id int primary key, rank int not null) with (autovacuum_enabled = false)",
                $"insert into ps_health_t07.{table} select g, g from generate_series(1, 24000) g",
                $"delete from ps_health_t07.{table} where rank % 2 = 0");
        }

        HealthReport report = await ReportAsync(host);

        foreach (string table in new[] { "orders", "qrtz_triggers" })
        {
            // G2 (W5-V1): judged against its own triggers, a table never vacuumed since 24,000 inserts is behind on those too -
            // one finding, the dead-row one, with both counters (W5-V1 review).
            HealthReportFinding finding = OnTable(report, "T07", schema + "." + table).Should().ContainSingle(
                "{0} is judged on vacuum whoever owns its index design", table).Subject;
            finding.Finding.Variant.Should().Be(VacuumBehindCheck.DeadRowsVariant);
            finding.Evidence.Should().Contain(new HealthEvidence("Inserted since the last vacuum", "24,000"));
            finding.Severity.Should().Be(HealthSeverity.Critical, "autovacuum is switched off for a table behind");
            finding.Finding.WhyItMatters.Should().Contain("switched off for this table");
            finding.Finding.Remedy!.Code.Should().Be($"ALTER TABLE \"ps_health_t07\".\"{table}\" RESET (autovacuum_enabled);");
        }
    }

    [PostgresFact]
    [HealthFires("T08")]
    public async Task Two_thousand_rows_never_analysed_with_autovacuum_off_are_T08()
    {
        const string schema = "ps_health_t08";
        await using StudioHost host = await HostAsync(schema, new ManualClock(Start));

        await ExecuteAsync(
            "create table ps_health_t08.orders (id int primary key, rank int not null) with (autovacuum_enabled = false)",
            "insert into ps_health_t08.orders select g, g from generate_series(1, 2000) g");

        HealthReportFinding finding = OnTable(await ReportAsync(host), "T08", schema + ".orders").Should().ContainSingle().Subject;
        finding.Finding.Title.Should().Contain("never been analysed");
        finding.Evidence.Should().Contain(new HealthEvidence("Estimated rows (reltuples)", "not analysed"));
    }

    /// <summary>
    /// T12 cannot be made to fire for real - it needs a table a billion transactions behind its last freeze - so, as the
    /// Marten suite proves it: the real gatherer, over the real table source, reads the real ages of the application's
    /// tables (checked against Postgres' own <c>age()</c>), and the same live facts, aged by 1.6 billion, are Critical.
    /// </summary>
    [PostgresFact]
    [HealthFires("T12")]
    public async Task The_live_transaction_id_ages_of_the_hosts_tables_are_read_and_aged_past_the_line_they_are_a_critical_T12()
    {
        const string schema = "ps_health_t12";
        await postgres.CreateSchemaAsync(schema, Token);
        await ExecuteAsync("create table ps_health_t12.orders (id int primary key)");

        await using StudioHost host = Host(schema, new ManualClock(Start));
        await using StudioCircuit circuit = host.Circuit();

        ResolvedSource resolved = await circuit.Get<StudioSourceResolver>().ResolveAsync(Scope, null, Token);
        await using HealthConnection connection = new(resolved.Target);

        HealthCheckContext context = new(
            resolved,
            host.Services.GetRequiredService<IStudioSettings>(),
            NoStudioRuntime.Instance,
            host.Services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        TransactionIdAgeCheck check = new();
        TransactionIdAgeFacts facts = await check.GatherAsync(context, Token);

        TableFacts orders = facts.Tables.Single(static x => x.Table.Table == "orders");
        orders.Table.Ownership.Should().Be(StudioTableOwnership.Owned, "a table in a schema BrowsableSchemas names is the host's");

        await using (NpgsqlConnection direct = await postgres.OpenAsync(Token))
        await using (NpgsqlCommand age = new("select pg_catalog.age(c.relfrozenxid)::bigint from pg_catalog.pg_class c where c.oid = 'ps_health_t12.orders'::regclass", direct))
        {
            long postgresSays = (long) (await age.ExecuteScalarAsync(Token))!;
            orders.Row.OldestXidAge.Should().BeInRange(postgresSays - 1_000, postgresSays + 1_000);
        }

        TransactionIdAgeFacts aged = new([.. facts.Tables.Select(static x => x with { Row = x.Row with { OldestXidAge = x.Row.OldestXidAge + 1_600_000_000 } })]);

        HealthFinding finding = check.Evaluate(aged, HealthEvaluation.At(DateTimeOffset.UtcNow)).Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().StartWith(schema + ".orders is ", "the oldest table is the host's own, and named");
        finding.GatedSchemas.Should().BeEmpty("a database-wide wraparound warning never vanishes behind the browser's gate");
    }

    /// <summary>
    /// The anti-cry-wolf half: an application's tables with keys and the indexes its queries use, a Quartz.NET table with
    /// an index design the rules would question, written, read and analysed the way an application does, then swept twice
    /// an interval apart - and nothing is said about any of them, by checks that looked.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("T01")]
    [HealthQuiet("T03")]
    [HealthQuiet("T04")]
    [HealthQuiet("T05")]
    [HealthQuiet("T06")]
    [HealthQuiet("T07")]
    [HealthQuiet("T08")]
    [HealthQuiet("T12")]
    public async Task Healthy_tables_and_a_librarys_table_are_quiet_on_every_table_check_and_the_sweep_writes_and_locks_nothing()
    {
        const string schema = "ps_health_quiet";
        ManualClock clock = new(Covering);
        HealthSweepProbe probe = new(postgres);
        await using StudioHost host = await HostAsync(schema, clock, probe);

        await ExecuteAsync(
            "create table ps_health_quiet.customers (id int primary key, name text not null)",
            "create table ps_health_quiet.orders (id int primary key, customer_id int not null references ps_health_quiet.customers (id), placed date not null)",
            "create index orders_by_customer on ps_health_quiet.orders (customer_id)",
            "create table ps_health_quiet.qrtz_triggers (sched_name text not null, trigger_name text not null, state text not null, primary key (sched_name, trigger_name))",
            "create index qrtz_by_sched on ps_health_quiet.qrtz_triggers (sched_name)",
            "create index qrtz_by_sched_state on ps_health_quiet.qrtz_triggers (sched_name, state)",
            "insert into ps_health_quiet.customers select g, 'customer ' || g from generate_series(1, 2000) g",
            "insert into ps_health_quiet.orders select g, 1 + g % 2000, date '2026-01-01' + g % 300 from generate_series(1, 5000) g",
            "insert into ps_health_quiet.qrtz_triggers select 'main', 'trigger ' || g, 'WAITING' from generate_series(1, 200) g",
            "analyze ps_health_quiet.customers",
            "analyze ps_health_quiet.orders",
            "analyze ps_health_quiet.qrtz_triggers");

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema);

        HealthReport first = await ReportAsync(host);
        Quiet(first, schema);

        for (int customer = 1; customer <= 20; customer++)
        {
            await ExecuteAsync(
                "select pg_catalog.count(*) from ps_health_quiet.orders where customer_id = " + customer.ToString(CultureInfo.InvariantCulture),
                "select name from ps_health_quiet.customers where id = " + customer.ToString(CultureInfo.InvariantCulture));
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport second = await ReportAsync(host);
        Quiet(second, schema);

        foreach (string code in TableCodes)
        {
            HealthCheckStatus status = second.Checks.Single(x => x.Code == code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked, not because it did not run: {1}", code, status.Problem);
            status.Statements.Should().NotBeEmpty("{0} read the catalog", code);
        }

        probe.StatementsObserved.Should().BePositive();
        probe.BeyondCatalogReads.Should().BeEmpty("a sweep locks nothing it lists (D27)");
        (await HealthSweepProbe.SnapshotAsync(postgres, schema)).Should().BeEquivalentTo(before, "a sweep writes nothing");
        host.Logs.WarningsOrWorse.Should().BeEmpty("a healthy database is nothing to warn about");
    }

    private static readonly StudioScope Scope = new("default", "default", null);

    private static void Quiet(HealthReport report, string schema)
    {
        report.Refusal.Should().Be(HealthRefusal.None, report.Reason);

        IReadOnlyList<HealthReportFinding> ours = [.. report.Findings.Where(x =>
            TableCodes.Contains(x.Code) && x.Finding.Subject?.StartsWith(schema + ".", StringComparison.Ordinal) == true)];

        ours.Should().BeEmpty(string.Join(" | ", ours.Select(static x => x.Code + " " + x.Finding.Subject + ": " + x.Finding.Title)));
    }

    /// <summary>A host over the <c>postgres</c> database whose only browsable schema is <paramref name="schema" />, created empty.</summary>
    private async Task<StudioHost> HostAsync(string schema, ManualClock clock, IHealthStatementObserver? observer = null)
    {
        await postgres.CreateSchemaAsync(schema, Token);
        return Host(schema, clock, observer);
    }

    private StudioHost Host(string schema, ManualClock clock, IHealthStatementObserver? observer = null) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.ConnectionString));
                services.AddSingleton<TimeProvider>(clock);

                if (observer is not null)
                {
                    services.AddSingleton(observer);
                }
            },
            options =>
            {
                options.BrowsableSchemas.Add(schema);
            });

    /// <summary>The visitor's report of the regular checks, through a circuit of its own.</summary>
    private static Task<HealthReport> ReportAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>().GetReportAsync(Scope, HealthReportRequest.Regular, Token));

    /// <summary>The findings of one code about one table or its indexes.</summary>
    private static IReadOnlyList<HealthReportFinding> OnTable(HealthReport report, string code, string tableKey) =>
        [.. report.Findings.Where(x => x.Code == code
            && (x.Finding.Subject == tableKey || x.Finding.Subject?.StartsWith(tableKey + ".", StringComparison.Ordinal) == true))];

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    private async Task ExecuteAsync(params string[] statements)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        // A backend sends its statistics at most once a second; this makes the next idle moment flush them, and Postgres
        // flushes before it tells the client it is ready.
        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }
}

/// <summary>A clock the test moves, so two sweeps a test asks for are an interval apart.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}
