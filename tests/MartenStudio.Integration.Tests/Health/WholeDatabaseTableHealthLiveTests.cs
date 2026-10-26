using Marten;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.SampleData;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Sources;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The sample's store and its relational demo schemas - Quartz.NET's <c>quartz</c>, the hand-made <c>legacy</c> and
/// <c>studio_sample.app_settings</c> - in a database of their own, seeded once, for G1M's live tests.
/// </summary>
/// <remarks>
/// A database, not a schema, for the reason <c>RelationalDemoFixture</c> gives: the demo creates the fixed schemas
/// <c>quartz</c> and <c>legacy</c> and anchors to the sample's own <c>studio_sample</c>, which would hand every other
/// class's catalog reads schemas they did not ask for.
/// </remarks>
/// <param name="postgres">The assembly's container.</param>
public sealed class WholeDatabaseTablesFixture(PostgresFixture postgres) : IAsyncLifetime
{
    private const string DatabaseName = "health_g1m_whole";

    /// <summary>The database's connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        ConnectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, DatabaseName);

        await using MartenFixture marten = await MartenFixture.CreateAsync(ConnectionString, SampleStore.DocumentSchema);
        await marten.SeedSampleDataAsync();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    public async Task ExecuteAsync(params string[] statements)
    {
        await using NpgsqlConnection connection = await OpenAsync();

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await FlushStatisticsAsync(connection);
    }

    /// <summary>A connection of the test's own.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        NpgsqlConnection connection = new(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}

/// <summary>
/// G1M (issue #8, G1): MartenStudio's table checks judge every table of the store's schemas and of the schemas the database
/// browser admits - the host's relational tables beside the store with migration SQL, a library's tables on vacuum and
/// statistics but not on index design, never a hidden document type's - and lock nothing and create nothing doing it.
/// </summary>
/// <remarks>
/// The studio is the sample host's own shape: <c>Capabilities.BrowseDatabase</c> on, <c>quartz</c> and <c>legacy</c> in
/// <c>BrowsableSchemas</c>. Every condition is the test's own DDL and DML, on tables of its own or on the demo's, by plain
/// SQL; the studio runs none of it.
/// </remarks>
/// <param name="fixture">The seeded database.</param>
public class WholeDatabaseTableHealthLiveTests(WholeDatabaseTablesFixture fixture) : IClassFixture<WholeDatabaseTablesFixture>
{
    private const string Sample = RelationalDemoSchema.SampleSchemaName;
    private const string Quartz = RelationalDemoSchema.QuartzSchemaName;
    private const string Legacy = RelationalDemoSchema.LegacySchemaName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private HealthLiveHost Host(
        ManualClock clock,
        Action<MartenStudioOptions>? studio = null,
        Action<IServiceCollection>? services = null,
        Action<StoreOptions>? store = null) =>
        HealthLiveHost.CreateOverDatabase(
            fixture.ConnectionString,
            store: store,
            studio: options =>
            {
                options.Capabilities.BrowseDatabase = true;
                options.BrowsableSchemas.Add(Quartz);
                options.BrowsableSchemas.Add(Legacy);
                studio?.Invoke(options);
            },
            services: collection =>
            {
                collection.AddSingleton<TimeProvider>(clock);
                services?.Invoke(collection);
            });

    private static IReadOnlyList<HealthReportFinding> About(HealthReport report, string code, string key) =>
        [.. Of(report, code).Where(x => x.Finding.Subject is { } subject
            && (subject == key || subject.StartsWith(key + ".", StringComparison.Ordinal)))];

    /// <summary>
    /// A plain relational table in the store's own schema - the host's, beside Marten - is judged like any of the store's:
    /// T01 on its sequential scans, T05 on an index nothing scans, T07 on its never-vacuumed inserts, each with the migration statement
    /// a DBA would put in the host's own migrations.
    /// </summary>
    [PostgresFact]
    [HealthFires("T01")]
    [HealthFires("T05")]
    [HealthFires("T07")]
    public async Task A_plain_relational_table_beside_the_store_is_judged_with_migration_SQL_remedies()
    {
        string table = SqlIdentifier.Qualify(Sample, "g1m_orders");
        string key = SchemaKey.For(Sample, "g1m_orders");

        // Two batches, each ending with a forced statistics flush: the insert's pending counters must reach shared memory
        // before ANALYZE sets n_live_tup, or - on a machine fast enough to finish the insert inside the one-second flush
        // interval - they land after it and add the 220,000 rows a second time.
        await fixture.ExecuteAsync(
            "drop table if exists " + table,
            "create table " + table + " (id bigint primary key, customer int not null, note text not null) with (autovacuum_enabled = false)",
            "create index g1m_orders_by_note on " + table + " (note)",
            "insert into " + table + " select g, g % 1000, 'note ' || g from generate_series(1, 220000) as g");

        await fixture.ExecuteAsync(
            // ANALYZE refreshes the index's relpages, which T05 sizes it by.
            "analyze " + table,
            "delete from " + table + " where id % 4 = 0");

        // T01 and T05 judge usage only over statistics a week old: a clock eight days on, from the ANALYZE above.
        ManualClock clock = new(Covering);
        await using HealthLiveHost host = Host(clock);

        HealthReport first = await ReportAsync(host);
        Of(first, "T01").Should().NotContain(x => x.Finding.Subject == key, "one sample is not a rate");

        await fixture.ExecuteAsync(
            "set enable_indexscan = off",
            "set enable_indexonlyscan = off",
            "set enable_bitmapscan = off",
            "set max_parallel_workers_per_gather = 0",
            "select pg_catalog.count(*) from " + table,
            "select pg_catalog.count(*) from " + table,
            "select pg_catalog.count(*) from " + table,
            "select pg_catalog.count(*) from " + table);

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport report = await ReportAsync(host);

        HealthReportFinding scans = About(report, "T01", key).Should().ContainSingle().Subject;
        HealthReportFinding unused = About(report, "T05", key).Should().ContainSingle().Subject;
        // G2 (W5-V1): a quarter dead is short of twice its own dead-row trigger (2 x 44,050 for 220,000 rows), so the table is
        // behind on its 220,000 never-vacuumed inserts instead.
        About(report, "T07", key).Should().NotContain(static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant);
        HealthReportFinding vacuum = About(report, "T07", key).Should().ContainSingle(static x => x.Finding.Variant == VacuumBehindCheck.InsertsVariant).Subject;

        foreach (HealthReportFinding finding in new[] { scans, unused, vacuum })
        {
            finding.Finding.GatedSchemas.Should().BeEmpty("{0}: the table is in the store's own schema", finding.Code);
            finding.Finding.Remedy.Should().NotBeNull("{0} has a remedy for the host's own table", finding.Code);
            finding.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration, "{0}: no StoreOptions line reaches a table no store declares", finding.Code);
            finding.Finding.Remedy.Code.Should().NotContain("opts.");
            finding.Evidence.Should().Contain(new HealthEvidence("Table", "the table " + key));
        }

        scans.Finding.Remedy!.Code.Should().Contain("CREATE INDEX CONCURRENTLY").And.Contain("ON \"studio_sample\".\"g1m_orders\"");
        unused.Finding.Subject.Should().Be(key + ".g1m_orders_by_note");
        unused.Finding.Remedy!.Code.Should().Contain("DROP INDEX CONCURRENTLY").And.Contain("\"studio_sample\".\"g1m_orders_by_note\"");
        vacuum.Finding.Remedy!.Code.Should().Be("ALTER TABLE \"studio_sample\".\"g1m_orders\" RESET (autovacuum_enabled);");

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// A Quartz.NET table is the library's design: a redundant index on it is no T04, though the same shape on the host's
    /// own table is - and its dead rows are still T07, whose remedy says the library's design is left alone. The table sits
    /// in a schema only <c>BrowsableSchemas</c> admits, so the finding carries that schema for the browser's gate.
    /// </summary>
    /// <remarks>
    /// A <c>qrtz_</c> table of the test's own, shaped like Quartz.NET's execution history, rather than the demo's: a table
    /// autovacuum has already been round is not one T07 calls behind for a day, and the demo's were just seeded.
    /// </remarks>
    [PostgresFact]
    [HealthFires("T04")]
    [HealthFires("T07")]
    public async Task A_librarys_table_is_exempt_from_the_index_design_rules_and_not_from_vacuum()
    {
        string history = SqlIdentifier.Qualify(Quartz, "qrtz_g1m_history");
        string key = SchemaKey.For(Quartz, "qrtz_g1m_history");
        string settings = SqlIdentifier.Qualify(Sample, RelationalDemoSchema.AppSettingsTable);

        await fixture.ExecuteAsync(
            "drop table if exists " + history,
            "drop index if exists " + SqlIdentifier.Qualify(Sample, "g1m_settings_key"),
            "create table " + history + " (sched_name text not null, entry_id text not null, fired_time bigint not null, "
            + "primary key (sched_name, entry_id)) with (autovacuum_enabled = false)",
            // A leading prefix of the primary key (sched_name, entry_id): redundant by T04's own rule.
            "create index g1m_eh_sched on " + history + " (sched_name)",
            "create index g1m_settings_key on " + settings + " (setting_key)",
            "insert into " + history + " select 'g1m', 'e' || g, g from generate_series(1, 40000) as g",
            "delete from " + history + " where fired_time % 2 = 0");

        try
        {
            await using HealthLiveHost host = Host(new ManualClock(Start));
            HealthReport report = await ReportAsync(host);

            // The condition is real: the same shape on the host's own table is a redundant index.
            HealthReportFinding hosts = About(report, "T04", SchemaKey.For(Sample, RelationalDemoSchema.AppSettingsTable)).Should().ContainSingle().Subject;
            hosts.Finding.Subject.Should().EndWith(".g1m_settings_key");
            hosts.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
            hosts.Finding.Remedy.Code.Should().Contain("DROP INDEX CONCURRENTLY");

            Of(report, "T04").Should().NotContain(x => x.Finding.Subject != null && x.Finding.Subject.StartsWith(Quartz + ".", StringComparison.Ordinal),
                "a redundant index on Quartz.NET's table is the library's design (issue #8, G1)");
            (await RedundantOnTableAsync(host, Quartz, "qrtz_g1m_history")).Should().Contain("g1m_eh_sched",
                "the redundancy is really there: only the library exemption keeps it out of T04");

            HealthReportFinding vacuum = About(report, "T07", key).Should().ContainSingle(static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant).Subject;
            vacuum.Finding.GatedSchemas.Should().Equal([Quartz], "quartz is not one of the store's own schemas (D29)");
            vacuum.Evidence.Should().Contain(new HealthEvidence("Table", "the Quartz.NET table " + key));
            vacuum.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
            vacuum.Finding.Remedy.Code.Should().Be("ALTER TABLE \"quartz\".\"qrtz_g1m_history\" RESET (autovacuum_enabled);");
            vacuum.Finding.Remedy.Explanation.Should().StartWith("The table is Quartz.NET's: its columns and indexes are that library's design");
        }
        finally
        {
            await fixture.ExecuteAsync(
                "drop table if exists " + history,
                "drop index if exists " + SqlIdentifier.Qualify(Sample, "g1m_settings_key"));
        }
    }

    /// <summary>
    /// A hidden document type and an orphan document table never appear - not in a finding, a status row's note or
    /// evidence - although both are half dead rows with autovacuum off. The same database judged by a studio that hides
    /// nothing is the proof the conditions are real: there both are T07, the orphan as Marten's own table.
    /// </summary>
    [PostgresFact]
    public async Task A_hidden_type_and_an_orphan_document_table_never_appear_while_a_type_may_be_hidden()
    {
        // The hidden type's table, in the shape the checks read (Marten's metadata columns play no part in them), and an
        // orphan no store declares - both the test's own, so no autovacuum has ever been round them.
        string secret = SqlIdentifier.Qualify(Sample, "mt_doc_healthsecretthing");
        string orphan = SqlIdentifier.Qualify(Sample, "mt_doc_g1m_orphan");

        string[] setUp = [.. new[] { secret, orphan }.SelectMany(static table => new[]
        {
            "drop table if exists " + table,
            "create table " + table + " (id uuid primary key, data jsonb not null) with (autovacuum_enabled = false)",
            "insert into " + table + " select gen_random_uuid(), jsonb_build_object('n', g) from generate_series(1, 30000) as g",
            "delete from " + table + " where (data ->> 'n')::int % 2 = 0",
        })];

        await fixture.ExecuteAsync(setUp);

        static void Declare(StoreOptions options) => options.Schema.For<HealthSecretThing>();

        try
        {
            HealthReport hiding;
            await using (HealthLiveHost host = Host(
                new ManualClock(Start),
                static o => o.IsDocumentTypeVisible = static type => type != typeof(HealthSecretThing),
                store: Declare))
            {
                hiding = await ReportAsync(host);
            }

            HealthCheckStatus t07 = HealthLiveHost.Status(hiding, "T07");
            t07.Outcome.Should().Be(HealthCheckOutcome.Checked, "T07 has to have looked for its silence about both tables to mean anything: {0}", t07.Problem);

            foreach (string name in new[] { "mt_doc_healthsecretthing", "mt_doc_g1m_orphan", "healthsecretthing", "HealthSecretThing" })
            {
                foreach (HealthReportFinding finding in hiding.Findings)
                {
                    string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Subject + " "
                        + finding.Finding.Caveat + " " + string.Join(" ", finding.Evidence.Select(static x => x.Value));
                    text.Should().NotContain(name, "{0} names nothing about a hidden type", finding.Code);
                }

                foreach (HealthCheckStatus status in hiding.Checks)
                {
                    (status.Note ?? string.Empty).Should().NotContain(name);
                    status.Statements.Should().NotContain(x => x.Sql.Contains(name, StringComparison.OrdinalIgnoreCase));
                }
            }

            HealthReport shown;
            await using (HealthLiveHost host = Host(new ManualClock(Start), store: Declare))
            {
                shown = await ReportAsync(host);
            }

            About(shown, "T07", SchemaKey.For(Sample, "mt_doc_healthsecretthing")).Should().ContainSingle(static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant, "the hidden type's table really is half dead")
                .Which.Finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthSecretThing>()", "a declared table keeps its StoreOptions line");

            HealthReportFinding stray = About(shown, "T07", SchemaKey.For(Sample, "mt_doc_g1m_orphan")).Should().ContainSingle(
                static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant,
                "with nothing hidden, an undeclared document table is judged as Marten's").Subject;
            stray.Finding.DocumentTypes.Should().BeEmpty("it is never named as a document type");
            stray.Finding.Remedy.Should().BeNull("no store declares it, so no statement or line is offered for it");
        }
        finally
        {
            await fixture.ExecuteAsync("drop table if exists " + secret, "drop table if exists " + orphan);
        }
    }

    /// <summary>
    /// The whole-database read locks nothing it lists and creates nothing: with another session holding
    /// <c>ACCESS EXCLUSIVE</c> on a partition of the demo's partitioned table - which the list rolls into its parent and
    /// never names, as Marten's tenant partitions are - every table check still runs to the end, no statement of theirs
    /// holds a lock on any user table but T11's <c>mt_hilo</c>, and the four schemas are exactly as they were.
    /// </summary>
    [PostgresFact]
    public async Task The_whole_database_read_locks_no_table_it_lists_and_creates_nothing()
    {
        string[] schemas = [Sample, SampleStore.EventSchema, Quartz, Legacy];
        HealthSweepProbe probe = new(fixture.ConnectionString);

        await using HealthLiveHost host = Host(new ManualClock(Start), services: services => services.AddSingleton<IHealthStatementObserver>(probe));

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(fixture.ConnectionString, schemas);

        await using NpgsqlConnection migration = await fixture.OpenAsync();
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await using (NpgsqlCommand hold = new(
            "lock table " + SqlIdentifier.Qualify(Legacy, "sensor_readings_2026_h1") + " in access exclusive mode", migration, holding))
        {
            await hold.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await ReportAsync(host);

        await holding.RollbackAsync(Token);

        report.Checks.Where(static x => x.Code.StartsWith('T') && x.Outcome != HealthCheckOutcome.Checked)
            .Select(static x => x.Code + ": " + x.Problem)
            .Should().BeEmpty("the table checks read the catalog, and a partition is never one of the tables they list");

        IStudioTableSet judged = await SetAsync(host);
        judged.Find(Legacy, "sensor_readings").Should().NotBeNull("the partitioned table is judged, its partitions rolled into it");
        judged.Find(Legacy, "sensor_readings_2026_h1").Should().BeNull("a partition is never listed");

        HashSet<string> statements = [.. report.Checks.Where(static x => x.Code.StartsWith('T')).SelectMany(static x => x.Statements).Select(static x => x.Sql)];
        statements.Should().Contain(HealthTableQueries.UndeclaredTablesSql, "the tables beside the store were listed");

        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Where(x => statements.Contains(x.Statement)).Should().OnlyContain(
            static x => x.Mode == "AccessShareLock" && x.Relation != null && x.Relation.Contains("mt_hilo", StringComparison.Ordinal),
            "a list locks nothing it lists (D27): the only user table a regular table check reads a row of is mt_hilo, for T11");

        (await HealthSweepProbe.SnapshotAsync(fixture.ConnectionString, schemas)).Should().BeEquivalentTo(before, "a sweep never runs DDL");
    }

    /// <summary>
    /// A migration's <c>ACCESS EXCLUSIVE</c> on a judged table - now any library's too, not only Marten's - costs the index
    /// checks (T03-T06) that one table for that one sweep and nothing more: the index read sees the lock in <c>pg_locks</c>
    /// and does not deparse behind it (<c>pg_get_indexdef</c> would open the table and queue), so they judge every other
    /// table at once and count the locked one as not judged, rather than waiting out <c>lock_timeout</c> or standing down
    /// whole; nothing is left holding a lock, and the next sweep is whole again.
    /// </summary>
    [PostgresFact]
    public async Task A_migrations_lock_on_a_judged_table_costs_the_index_checks_one_sweep_and_holds_nothing()
    {
        string[] indexChecks = ["T03", "T04", "T05", "T06"];
        HealthSweepProbe probe = new(fixture.ConnectionString);
        ManualClock clock = new(Start);

        await using HealthLiveHost host = Host(clock, services: services => services.AddSingleton<IHealthStatementObserver>(probe));

        HealthReport report;
        await using (NpgsqlConnection migration = await fixture.OpenAsync())
        {
            await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
            await using (NpgsqlCommand hold = new(
                "lock table " + SqlIdentifier.Qualify(Quartz, "qrtz_job_details") + " in access exclusive mode", migration, holding))
            {
                await hold.ExecuteNonQueryAsync(Token);
            }

            report = await ReportAsync(host);

            await holding.RollbackAsync(Token);
        }

        foreach (HealthCheckStatus status in report.Checks.Where(static x => x.Code.StartsWith('T')))
        {
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} judges every table but the locked one: {1}", status.Code, status.Problem);

            if (indexChecks.Contains(status.Code))
            {
                status.Note.Should().Contain("1 table was locked for a schema change and not judged this sweep.",
                    "{0} reads the indexes, whose definitions would open the locked table: the lock was seen in pg_locks, and counted", status.Code);
                status.Note.Should().NotContain("qrtz_job_details", "the row names no table");
            }
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport next = await ReportAsync(host);

        next.Checks.Where(static x => x.Code.StartsWith('T')).Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked,
            "the next sweep, with the migration gone, is whole again");
        next.Checks.Where(x => indexChecks.Contains(x.Code)).Should().OnlyContain(static x => x.Note == null || !x.Note.Contains("locked for a schema change"),
            "and counts no locked table");
        probe.BeyondCatalogReads.Should().NotContain(static x => x.Relation == "qrtz_job_details",
            "the deparser's lock is released as soon as it has the column names: no statement ends holding it");
    }

    /// <summary>
    /// The sample baseline, widened: the seeded sample with its Quartz.NET and legacy schemas judged, as the sample host
    /// judges them, has no table finding about any of the demo's own tables - every finding the run shows is written to
    /// the output, so a run says what the demo opens on. (Tables this class makes for its own conditions are named
    /// <c>*g1m*</c> and are not the demo's.)
    /// </summary>
    /// <remarks>
    /// Twelve minutes of regular sweeps, two apart, by the host's clock: T07 and T08 speak only once a condition has held
    /// for ten minutes of samples, so a single sweep would be quiet about the library's tables whatever they held.
    /// </remarks>
    [PostgresFact]
    public async Task The_seeded_samples_relational_schemas_raise_no_table_finding()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = Host(clock);

        for (int minutes = 0; minutes < 12; minutes += 2)
        {
            await ReportAsync(host);
            clock.Advance(TimeSpan.FromMinutes(2));
        }

        HealthReport report = await ReportAsync(host, deeper: true);

        foreach (HealthReportFinding finding in report.Findings)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(finding.Severity + " " + finding.Code + " " + finding.Finding.Subject + ": " + finding.Finding.Title);
        }

        IStudioTableSet set = await SetAsync(host);
        HashSet<string> demo = [.. set.Tables
            .Where(static x => !x.IsDeclaredByStore && !x.Table.Contains("g1m", StringComparison.Ordinal))
            .Select(static x => x.Key)];

        demo.Should().Contain(SchemaKey.For(Quartz, "qrtz_job_details")).And.Contain(SchemaKey.For(Legacy, "departments"))
            .And.Contain(SchemaKey.For(Sample, RelationalDemoSchema.AppSettingsTable), "the demo's tables are judged");

        report.Findings.Where(static x => x.Code.StartsWith('T'))
            .Where(x => x.Finding.Subject is { } subject && demo.Any(table => subject == table || subject.StartsWith(table + ".", StringComparison.Ordinal)))
            .Select(static x => x.Code + " " + x.Finding.Subject + ": " + x.Finding.Title)
            .Should().BeEmpty("the demo starts healthy, and the studio does not cry wolf about a library's design");

        foreach (HealthCheckStatus status in report.Checks.Where(static x => x.Code.StartsWith('T') && x.Tier != HealthCostTier.Statements))
        {
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} has to run for its silence to mean anything: {1}", status.Code, status.Problem);
        }
    }

    /// <summary>
    /// T09's sample reads rows, so it keeps to the store's own collections: with nothing hidden, an undeclared
    /// <c>mt_doc_*</c> table is in the judged set as Marten's - and still never sampled (D27: Marten's tables are never
    /// read raw), while a declared collection of the same size is.
    /// </summary>
    [PostgresFact]
    public async Task The_deeper_sample_reads_a_declared_collection_and_never_an_undeclared_document_table()
    {
        string orphan = SqlIdentifier.Qualify(Sample, "mt_doc_g1m_sampled");
        string customers = SqlIdentifier.Qualify(Sample, "mt_doc_customer");

        await fixture.ExecuteAsync(
            "drop table if exists " + orphan,
            "create table " + orphan + " (id uuid primary key, data jsonb not null)",
            "insert into " + orphan + " select gen_random_uuid(), jsonb_build_object('n', g) from generate_series(1, 2000) as g",
            "insert into " + customers + " (id, data) select gen_random_uuid(), jsonb_build_object('Email', 'g1m' || g || '@example.invalid') "
            + "from generate_series(1, 2000) as g");

        try
        {
            await using HealthLiveHost host = Host(new ManualClock(Start));

            (await SetAsync(host)).Find(Sample, "mt_doc_g1m_sampled").Should().BeEquivalentTo(
                new { Ownership = StudioTableOwnership.Library, Kind = StudioTableKind.DocumentTable, Library = "Marten" },
                "the precondition: nothing is hidden, so the orphan is judged as Marten's");

            HealthReport report = await ReportAsync(host, deeper: true);
            HealthCheckStatus t09 = HealthLiveHost.Status(report, "T09");

            t09.Outcome.Should().Be(HealthCheckOutcome.Checked, t09.Problem);
            t09.Statements.Should().Contain(static x => x.Sql.Contains("tablesample", StringComparison.Ordinal)
                && x.Sql.Contains("mt_doc_customer", StringComparison.Ordinal), "a declared collection of this size is sampled");
            t09.Statements.Should().NotContain(static x => x.Sql.Contains("mt_doc_g1m_sampled", StringComparison.Ordinal),
                "a document table no store declares is never sampled");
        }
        finally
        {
            await fixture.ExecuteAsync(
                "drop table if exists " + orphan,
                "delete from " + customers + " where data ->> 'Email' like 'g1m%'");
        }
    }

    /// <summary>The live classification: whose each of the demo's tables is, as a sweep's own source reads it.</summary>
    [PostgresFact]
    public async Task The_demos_tables_are_classified_as_whose_they_are()
    {
        await using HealthLiveHost host = Host(new ManualClock(Start));
        IStudioTableSet set = await SetAsync(host);

        set.Find(Sample, RelationalDemoSchema.AppSettingsTable).Should().BeEquivalentTo(
            new { Ownership = StudioTableOwnership.Owned, Kind = StudioTableKind.Relational, GatedSchemas = Array.Empty<string>() });
        set.Find(Quartz, "qrtz_job_details").Should().BeEquivalentTo(
            new { Ownership = StudioTableOwnership.Library, Kind = StudioTableKind.Relational, Library = "Quartz.NET", GatedSchemas = new[] { Quartz } });
        set.Find(Legacy, "departments").Should().BeEquivalentTo(
            new { Ownership = StudioTableOwnership.Other, Kind = StudioTableKind.Relational, GatedSchemas = new[] { Legacy } });
        set.Find(Sample, "mt_doc_customer").Should().BeEquivalentTo(
            new { Ownership = StudioTableOwnership.Owned, Kind = StudioTableKind.DocumentTable, TypeName = "Customer" },
            "a declared table is exactly what the store declares");
        set.Tables.Where(static x => x.Schema == Quartz).Should().HaveCount(RelationalDemoSchema.QuartzTableCount);
        set.Tables.Should().NotContain(static x => x.Schema.StartsWith("pg_", StringComparison.Ordinal) || x.Schema == "information_schema");
        set.Coverage.Should().BeNull("one store, a few dozen tables: nothing is left out");
    }

    /// <summary>The set a sweep of <paramref name="host" /> judges, read through its own source in a scope of its own.</summary>
    private static async Task<IStudioTableSet> SetAsync(HealthLiveHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await using HealthConnection connection = new(MartenStudioDatabase.Of(host.Store.Storage.Database));

        HealthCheckContext context = new(
            new ResolvedScope(
                host.Scope,
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                host.Store,
                host.Store.Storage.Database),
            scope.ServiceProvider.GetRequiredService<PostgresStudio.Services.Settings.IStudioSettings>(),
            MartenStudio.Services.Configuration.MartenStudioRuntime.Current,
            scope.ServiceProvider,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        return await HealthTables.SetAsync(context);
    }

    /// <summary>The indexes on one table that T04's own rule calls redundant, whatever the table's ownership.</summary>
    private static async Task<IReadOnlyList<string>> RedundantOnTableAsync(HealthLiveHost host, string schema, string table)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await using HealthConnection connection = new(MartenStudioDatabase.Of(host.Store.Storage.Database));

        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions(), null);
        IReadOnlyList<HealthIndexRow> rows = await sql.QueryAsync(
            HealthTableQueries.Indexes([new HealthTableTarget(schema, table)]), HealthTableQueries.ReadIndex, Token);

        return [.. IndexRedundancy.Find(rows, static _ => 1).Select(static x => x.Index.Name)];
    }
}
