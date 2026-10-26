using System.Linq.Expressions;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// Each tables-and-indexes check, made to fire against a real Postgres by producing its condition - in the
/// test's own schema, by plain SQL - and read through the studio's own services.
/// </summary>
/// <remarks>
/// The quiet half is <c>TableHealthQuietLiveTests</c>. Every write here ends with a forced statistics flush
/// (<see cref="TableHealthLive.FlushStatisticsAsync" />), which is what makes a counter test deterministic
/// rather than a wait.
/// </remarks>
public class TableHealthFiresLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("T01")]
    public async Task Sequential_scans_that_keep_reading_a_large_table_are_T01()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_t01_fires", clock, static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        await InsertAsync(postgres, table, 120_000);

        (await ReportAsync(host)).Findings.Should().NotContain(static x => x.Code == "T01", "one sample is not a rate");

        await ExecuteAsync(
            postgres,
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

        HealthReportFinding finding = Of(report, "T01").Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().Be(KeyOf<HealthScanThing>(host.Store));
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthScanThing>()");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthFires("T03")]
    public async Task A_failed_concurrent_unique_build_leaves_an_invalid_index_that_is_a_critical_T03()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t03_fires", store: static o => o.Schema.For<HealthIndexedThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 2, "jsonb_build_object('Name', 'same')");

        Func<Task> build = () => ExecuteAsync(postgres, "create unique index concurrently hand_unique_name on " + table + " ((data ->> 'Name'))");
        (await build.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505", "two documents share the name");

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = OnTable(report, "T03", KeyOf<HealthIndexedThing>(host.Store)).Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Finding.Subject.Should().Be(KeyOf<HealthIndexedThing>(host.Store) + ".hand_unique_name");
        finding.Finding.WhyItMatters.Should().Contain("next apply drops it", "nothing declares the hand-made index");
    }

    [PostgresFact]
    [HealthFires("T04")]
    public async Task An_index_declared_beside_a_wider_one_that_starts_with_it_is_a_redundant_T04()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_t04_fires",
            store: static o => o.Schema.For<HealthIndexedThing>()
                .Index(x => x.Name)
                .Index(new Expression<Func<HealthIndexedThing, object>>[] { x => x.Name, x => x.Rank }));
        await ApplyAsync(host.Store);

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = OnTable(report, "T04", KeyOf<HealthIndexedThing>(host.Store)).Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().StartWith(KeyOf<HealthIndexedThing>(host.Store) + ".").And.EndWith("_idx_name");
        finding.Finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthIndexedThing>()");
        finding.Evidence.Should().Contain(static x => x.Label == "How" && x.Value == "a leading prefix of a b-tree index");
    }

    [PostgresFact]
    [HealthFires("T05")]
    public async Task A_declared_index_nothing_scans_on_a_table_of_a_hundred_thousand_inserts_is_T05()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t05_fires", store: static o => o.Schema.For<HealthIndexedThing>().Index(x => x.Name));
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 110_000);

        // ANALYZE refreshes the index's relpages, which is what the check sizes it by: inserts alone do not.
        await ExecuteAsync(postgres, "analyze " + table);

        HealthReport report = await ReportAsync(host);

        HealthReportFinding finding = OnTable(report, "T05", KeyOf<HealthIndexedThing>(host.Store)).Should().ContainSingle().Subject;
        finding.Finding.Subject.Should().EndWith("_idx_name");
        finding.Finding.Caveat.Should().Contain("statistics were last reset");
        finding.Finding.Caveat.Should().NotContain("multi-host", "the fixture's data source names one host, read off a connection it never opened");
    }

    /// <summary>The findings of one code about one table's indexes - the sample domain's own tables sit beside it.</summary>
    private static IReadOnlyList<HealthReportFinding> OnTable(HealthReport report, string code, string tableKey) =>
        [.. Of(report, code).Where(x => x.Finding.Subject?.StartsWith(tableKey + ".", StringComparison.Ordinal) == true)];

    [PostgresFact]
    [HealthFires("T06")]
    public async Task Updates_on_full_pages_with_no_blocking_index_are_a_T06_FillFactor_warning_worded_for_this_Marten()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_t06_fires", clock, static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        await InsertAsync(postgres, table, 12_000);
        await ReportAsync(host);

        await ExecuteAsync(postgres, "update " + table + " set data = data || jsonb_build_object('Rank', 0)");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReportFinding finding = Of(await ReportAsync(host), "T06").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthScanThing>().FillFactor(80);");

        // The version half, at whichever Marten built this run: below 9.47 the page says what this host runs.
        bool available = RuntimeFeature.DocumentFillFactor.IsAvailable(MartenRuntime.Current);
        finding.RemedyAvailable.Should().Be(available);
        finding.HostRuns.Should().Be(available ? null : MartenRuntime.Current.Describe(RuntimePackage.Marten));
    }

    [PostgresFact]
    [HealthFires("T06")]
    public async Task With_a_computed_index_every_upsert_blocks_HOT_and_T06_says_fillfactor_cannot_help()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_t06_blocked", clock, static o => o.Schema.For<HealthIndexedThing>().Index(x => x.Name));
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 12_000);
        await ReportAsync(host);

        await ExecuteAsync(postgres, "update " + table + " set data = data || jsonb_build_object('Rank', 0)");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReportFinding finding = Of(await ReportAsync(host), "T06").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Title.Should().Contain("fillfactor cannot help");
        finding.Finding.WhyItMatters.Should().Contain("reads data", "Postgres records the computed index's dependency on the data column");
    }

    [PostgresFact]
    [HealthFires("T07")]
    public async Task Half_the_table_dead_with_autovacuum_off_for_it_is_T07_at_once()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t07_fires", store: static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        await ExecuteAsync(postgres, "alter table " + table + " set (autovacuum_enabled = false)");
        await InsertAsync(postgres, table, 24_000);
        await ExecuteAsync(postgres, "delete from " + table + " where (data ->> 'Rank')::int % 2 = 0");

        // G2 (W5-V1): never vacuumed since its 24,000 inserts, the table is behind on those too; this is the dead-row finding.
        HealthReportFinding finding = Of(await ReportAsync(host), "T07").Should().ContainSingle(static x => x.Finding.Variant == VacuumBehindCheck.DeadRowsVariant).Subject;

        finding.Finding.Subject.Should().Be(KeyOf<HealthScanThing>(host.Store));
        finding.Finding.WhyItMatters.Should().Contain("switched off for this table");
        finding.Finding.Remedy!.Code.Should().Contain("StorageParameter(\"autovacuum_enabled\", true)");
        finding.Finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DocumentStorageParameter);
    }

    [PostgresFact]
    [HealthFires("T08")]
    public async Task Two_thousand_rows_never_analysed_with_autovacuum_off_for_the_table_is_T08()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t08_fires", store: static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        await ExecuteAsync(postgres, "alter table " + table + " set (autovacuum_enabled = false)");
        await InsertAsync(postgres, table, 2_000);

        HealthReportFinding finding = Of(await ReportAsync(host), "T08").Should().ContainSingle().Subject;

        finding.Finding.Title.Should().Contain("never been analysed");
        finding.Evidence.Should().Contain(new HealthEvidence("Estimated rows (reltuples)", "not analysed"));
    }

    [PostgresFact]
    [HealthFires("T09")]
    public async Task Documents_stored_at_seventy_kilobytes_are_T09_behind_the_deeper_checks()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t09_fires", store: static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);

        // Uncompressed, so the stored size is the size: a cheap way to write 1,050 large documents.
        await ExecuteAsync(postgres, "alter table " + table + " alter column data set storage external");
        await InsertAsync(postgres, table, 1_050, "jsonb_build_object('Name', 'n' || g, 'Payload', repeat('x', 70000))");
        await ExecuteAsync(postgres, "analyze " + table);

        (await ReportAsync(host)).Findings.Should().NotContain(static x => x.Code == "T09", "the regular sweep never reads data");

        HealthReport report = await ReportAsync(host, deeper: true);

        HealthReportFinding finding = Of(report, "T09").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Subject.Should().Be(KeyOf<HealthScanThing>(host.Store));
        report.DeeperMeasuredAt.Should().NotBeNull();
    }

    [PostgresFact]
    [HealthFires("T10")]
    public async Task Half_of_a_soft_deleted_collection_deleted_is_T10()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t10_fires", store: static o => o.Schema.For<HealthTombstoneThing>().SoftDeleted());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthTombstoneThing>(host.Store);
        await InsertAsync(postgres, table, 2_000, "jsonb_build_object('Rank', g)");
        await ExecuteAsync(
            postgres,
            "update " + table + " set mt_deleted = true, mt_deleted_at = pg_catalog.now() where (data ->> 'Rank')::int % 2 = 0",
            "analyze " + table);

        HealthReportFinding finding = Of(await ReportAsync(host), "T10").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Finding.Remedy!.Code.Should().Contain("HardDeleteWhere<HealthTombstoneThing>");
    }

    [PostgresFact]
    [HealthFires("T11")]
    public async Task A_HiLo_sequence_far_through_int_MaxValue_is_a_critical_T11()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t11_fires", store: static o => o.Schema.For<HealthCounterThing>());
        await ApplyAsync(host.Store);

        // One real id, so Marten writes the sequence's row itself; then the row is moved where it would be after
        // 1.8 billion ids.
        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Store(new HealthCounterThing { Name = "first" });
            await session.SaveChangesAsync(Token);
        }

        await ExecuteAsync(postgres, "update " + SqlIdentifier.Qualify(host.Schema, "mt_hilo") + " set hi_value = 1800000 where entity_name = 'HealthCounterThing'");

        HealthReportFinding finding = Of(await ReportAsync(host), "T11").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Finding.Subject.Should().Be("HealthCounterThing");
        finding.Evidence.Should().Contain(new HealthEvidence("hi_value", "1,800,000"));
    }

    /// <summary>
    /// T12 cannot be made to fire for real: it needs a table a billion transactions behind its last freeze, and a
    /// test can burn perhaps ten thousand a second. So this proves the two halves apart, honestly: the real
    /// gatherer reads the real ages of the store's tables (checked against Postgres' own <c>age()</c>), and the
    /// same live facts, aged by 1.6 billion, are judged Critical.
    /// </summary>
    [PostgresFact]
    [HealthFires("T12")]
    public async Task The_live_transaction_id_ages_are_read_and_aged_past_the_line_they_are_a_critical_T12()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t12_fires");
        await ApplyAsync(host.Store);

        // The shared table checks read the store's tables through the studio's table source, as a sweep's scope has it.
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<PostgresStudio.Services.Sources.IHealthTableSource, MartenHealthTableSource>()
            .BuildServiceProvider();
        await using HealthConnection connection = new(MartenStudioDatabase.Of(host.Store.Storage.Database));

        HealthCheckContext context = new(
            new ResolvedScope(
                host.Scope,
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                host.Store,
                host.Store.Storage.Database),
            MartenStudioSettings.Of(new MartenStudioOptions()),
            MartenStudioRuntime.Current,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        TransactionIdAgeCheck check = new();
        TransactionIdAgeFacts facts = await check.GatherAsync(context, Token);

        TableFacts events = facts.Tables.Single(static x => x.Table.Table == "mt_events");
        events.Row.OldestXidAge.Should().NotBeNull("an ordinary table always has a relfrozenxid");
        events.Row.FreezeMaxAge.Should().Be(200_000_000, "the container runs Postgres' default autovacuum_freeze_max_age");

        await using (NpgsqlConnection direct = await postgres.OpenAsync(Token))
        await using (NpgsqlCommand age = new("select pg_catalog.age(c.relfrozenxid)::bigint from pg_catalog.pg_class c where c.oid = @table::regclass", direct))
        {
            age.Parameters.AddWithValue("table", SqlIdentifier.Qualify(events.Table.Schema, "mt_events"));
            long postgresSays = (long) (await age.ExecuteScalarAsync(Token))!;
            events.Row.OldestXidAge.Should().BeInRange(postgresSays - 1_000, postgresSays + 1_000,
                "the gatherer's age is the table's own, its TOAST table's being no older on a fresh table");
        }

        TransactionIdAgeFacts aged = new([.. facts.Tables.Select(static x => x with { Row = x.Row with { OldestXidAge = x.Row.OldestXidAge + 1_600_000_000 } })]);

        HealthFinding finding = check.Evaluate(aged, HealthEvaluation.At(DateTimeOffset.UtcNow)).Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
    }
}
