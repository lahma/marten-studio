using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// G2 against autovacuum itself (W5-V1): T07 judged against each table's own triggers on PostgreSQL 17 and 18 and quiet once
/// autovacuum has run, T08's pg_upgrade shape, S02 on a server with autovacuum switched off, the Maintenance page's view and
/// bloat measure, and C06 with a VACUUM at the head of a lock chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Autovacuum kept off a table by a lock, not a setting.</b> Autovacuum skips a table whose <c>SHARE UPDATE
/// EXCLUSIVE</c> lock it cannot take, so a session holding that lock keeps it away while autovacuum stays on for the
/// table and the server - the shape T07 is about. Releasing it lets autovacuum, woken every second, vacuum the table for
/// real, and the quiet half is that vacuum.
/// </para>
/// <para>
/// <b>Two hours on.</b> T07 wants the last vacuum over an hour old, and a test cannot wait for one: the studio's clock
/// starts two hours past the server's, so the manual <c>VACUUM</c> that shapes a table is two hours old to the check.
/// </para>
/// </remarks>
public class MaintenanceLiveTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// B0's <c>ev</c> (append-only, insert threshold 1,000, insert scale 0.5, frozen by a <c>VACUUM (FREEZE)</c>) and a
    /// table tuned to <c>autovacuum_vacuum_scale_factor = 0.01</c> that is 7.5% dead. On 18 the insert trigger is the
    /// threshold alone, its scale part cancelled by the frozen share, so 3,500 appends are past it; on 17 they are not, and
    /// both are T07 once far past twice their own trigger. Released, autovacuum vacuums both, and T07 is quiet.
    /// </summary>
    [PostgresTheory]
    [InlineData(17)]
    [InlineData(18)]
    [HealthFires("T07")]
    [HealthQuiet("T07")]
    public async Task Tables_past_twice_their_own_triggers_are_T07_and_quiet_once_autovacuum_has_run(int major)
    {
        await using AutovacuumServer server = await AutovacuumServer.StartAsync(major);

        await server.ExecuteAsync(
            "create schema ps_g2",
            "create table ps_g2.ev (id bigint, payload text) with (autovacuum_vacuum_insert_threshold = 1000, "
            + "autovacuum_vacuum_insert_scale_factor = 0.5, autovacuum_analyze_threshold = 1000000000, autovacuum_enabled = false)",
            "create table ps_g2.dr (id int, rank int) with (autovacuum_vacuum_scale_factor = 0.01, autovacuum_analyze_threshold = 1000000000, "
            + "autovacuum_enabled = false)",
            "insert into ps_g2.ev select g, 'e' || g from generate_series(1, 10000) g",
            "insert into ps_g2.dr select g, g from generate_series(1, 200000) g");

        // A session of its own, once the load's counts have reached the server: a VACUUM in the session that loaded would be
        // undone by that session's own pending counts (B0 7b). Autovacuum is kept off the tables while they are shaped, so
        // it cannot race the VACUUM, and switched back on once their counters are nought and nothing is due.
        //
        // A page is frozen only when every row on it is visible to every snapshot, and an autovacuum worker woken every second
        // takes one to list a database's tables: a VACUUM (FREEZE) that runs while such a snapshot predates the load freezes
        // nothing (measured: 55 pages, none frozen), so on 18, whose insert trigger the frozen share scales, it runs until
        // every page is.
        await AutovacuumServer.UntilAsync(
            async () =>
            {
                await server.ExecuteAsync("vacuum (freeze, analyze) ps_g2.ev");
                return major < 18 || await server.ScalarAsync<bool>(
                    "select c.relpages > 0 and c.relallfrozen = c.relpages from pg_catalog.pg_class c where c.oid = 'ps_g2.ev'::regclass");
            },
            TimeSpan.FromSeconds(60),
            "VACUUM (FREEZE) to freeze every page of ps_g2.ev");

        await server.ExecuteAsync(
            "vacuum (analyze) ps_g2.dr",
            "alter table ps_g2.ev reset (autovacuum_enabled)",
            "alter table ps_g2.dr reset (autovacuum_enabled)");

        await using NpgsqlConnection holder = await server.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand lockThem = new("lock table ps_g2.ev, ps_g2.dr in share update exclusive mode", holder, held))
        {
            await lockThem.ExecuteNonQueryAsync(Token);
        }

        ManualClock clock = new(DateTimeOffset.UtcNow.AddHours(2));
        await using StudioHost host = server.Host("ps_g2", clock);

        await server.ExecuteAsync("insert into ps_g2.ev select g, 'a' || g from generate_series(10001, 13500) g");

        MaintenanceTableView appended = (await ViewAsync(host)).Tables.Single(static x => x.Key == "ps_g2.ev");
        appended.Insert!.Counter.Should().Be(3_500);
        Autovacuum.Rounded(appended.Insert.Trigger).Should().Be(major >= 18 ? 1_000 : 6_000);
        appended.Due.Should().Be(major >= 18, "3,500 appends are past 18's frozen-share trigger and short of 17's 6,000 (B0 7b)");

        await server.ExecuteAsync(
            "insert into ps_g2.ev select g, 'b' || g from generate_series(13501, 38500) g",
            "delete from ps_g2.dr where id % 40 < 3");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport first = await AutovacuumServer.ReportAsync(host);
        first.Findings.Should().NotContain(static x => x.Code == "T07" && x.Finding.Subject!.StartsWith("ps_g2.", StringComparison.Ordinal),
            "autovacuum is on: a finding needs the condition held for ten minutes");
        AutovacuumServer.Status(first, "T07").Note.Should().Contain("Watching 2 tables");

        clock.Advance(TimeSpan.FromMinutes(11));
        HealthReport report = await AutovacuumServer.ReportAsync(host);

        HealthReportFinding inserts = AutovacuumServer.About(report, "T07", "ps_g2.ev").Should().ContainSingle().Subject;
        inserts.Finding.Variant.Should().Be(VacuumBehindCheck.InsertsVariant);
        inserts.Severity.Should().Be(major >= 18 ? HealthSeverity.Critical : HealthSeverity.Warning,
            "28,500 inserts are 28.5 times 18's trigger, past ten, and 4.75 times 17's");
        inserts.Evidence.Should().Contain(new HealthEvidence("Inserted since the last vacuum", "28,500"));
        inserts.Evidence.Single(static x => x.Label == "Its insert trigger").Value.Should().StartWith(major >= 18 ? "1,000 (" : "6,000 (");
        // What keeps autovacuum away is the test's own lock, and the finding says so - with no setting to change (W5-V1 review).
        inserts.Evidence.Should().Contain(new HealthEvidence("What keeps autovacuum away", AutovacuumHindrances.LockedLabel));
        inserts.Finding.Remedy.Should().BeNull();

        HealthReportFinding dead = AutovacuumServer.About(report, "T07", "ps_g2.dr").Should().ContainSingle().Subject;
        dead.Finding.Variant.Should().Be(VacuumBehindCheck.DeadRowsVariant);
        dead.Severity.Should().Be(HealthSeverity.Warning);
        dead.Finding.Title.Should().StartWith("ps_g2.dr has 15,000 dead rows, ", "7.5% dead, nowhere near the fifth 0.4 waited for");
        dead.Evidence.Single(static x => x.Label == "Its dead-row trigger").Value.Should().StartWith("2,050 (")
            .And.Contain("autovacuum_vacuum_scale_factor 0.01, the table's own storage parameter");
        dead.Evidence.Should().Contain(new HealthEvidence("What keeps autovacuum away", AutovacuumHindrances.LockedLabel));
        dead.Finding.Remedy.Should().BeNull();

        // Released: autovacuum, woken every second, vacuums both - the trigger read said it would.
        await held.CommitAsync(Token);

        // Both counters back at nought, by autovacuum's hand: the dead rows cleared and the inserts counted as vacuumed. (An
        // autovacuum count alone would not do: autovacuum may have visited a table after its load, before the test's VACUUM.)
        await AutovacuumServer.UntilAsync(
            async () => await server.ScalarAsync<long>(
                "select pg_catalog.count(*) from pg_catalog.pg_stat_user_tables where schemaname = 'ps_g2' "
                + "and last_autovacuum is not null and n_dead_tup = 0 and n_ins_since_vacuum = 0") == 2,
            TimeSpan.FromSeconds(120),
            "autovacuum to vacuum both tables once the lock is gone");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport quiet = await AutovacuumServer.ReportAsync(host);

        quiet.Findings.Should().NotContain(static x => x.Code == "T07" && x.Finding.Subject!.StartsWith("ps_g2.", StringComparison.Ordinal),
            string.Join(" | ", quiet.Findings.Where(static x => x.Code == "T07").Select(static x => x.Finding.Title)));
        AutovacuumServer.Status(quiet, "T07").Outcome.Should().Be(HealthCheckOutcome.Checked);
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// T08's widened <c>never-analysed</c>, on the shape <c>pg_upgrade</c> leaves: a table with no statistics
    /// (<c>reltuples = -1</c>, <c>relpages = 0</c>) whose counters say nothing lives in it, read but not written - so
    /// autovacuum never analyses it. Its reads are what T08 names; an <c>ANALYZE</c> makes it quiet.
    /// </summary>
    [PostgresFact]
    [HealthFires("T08")]
    [HealthQuiet("T08")]
    public async Task A_table_read_but_never_analysed_after_its_counters_began_again_is_T08_until_it_is_analysed()
    {
        const string schema = "ps_maint_t08";
        await postgres.CreateSchemaAsync(schema, Token);
        ManualClock clock = new(Start);
        await using StudioHost host = FixtureHost(schema, clock);

        // Loaded with autovacuum off, so nothing analyses it; then its counters reset, as pg_upgrade starts them, and
        // autovacuum on again - with nothing modified, it has no reason to analyse.
        await ExecuteAsync(
            "create table ps_maint_t08.upgraded (id int primary key, note text) with (autovacuum_enabled = false)",
            "insert into ps_maint_t08.upgraded select g, 'n' || g from generate_series(1, 5000) g");
        await ExecuteAsync(
            "select pg_catalog.pg_stat_reset_single_table_counters('ps_maint_t08.upgraded'::regclass)",
            "alter table ps_maint_t08.upgraded reset (autovacuum_enabled)");
        await ExecuteAsync(
            "select pg_catalog.count(*) from ps_maint_t08.upgraded",
            "select pg_catalog.count(*) from ps_maint_t08.upgraded");

        (await ScalarAsync<double>("select c.reltuples::float8 from pg_catalog.pg_class c where c.oid = 'ps_maint_t08.upgraded'::regclass"))
            .Should().Be(-1, "the shape pg_upgrade leaves: no statistics");
        (await ScalarAsync<long>("select n_live_tup from pg_catalog.pg_stat_user_tables where relid = 'ps_maint_t08.upgraded'::regclass"))
            .Should().Be(0, "and counters that say nothing lives there, so the live-row rule alone never holds");

        await AutovacuumServer.ReportAsync(host);
        clock.Advance(TimeSpan.FromMinutes(11));
        HealthReport report = await AutovacuumServer.ReportAsync(host);

        HealthReportFinding finding = AutovacuumServer.About(report, "T08", schema + ".upgraded").Should().ContainSingle().Subject;
        finding.Finding.Variant.Should().Be(StaleStatisticsCheck.NeverAnalysedVariant);
        finding.Finding.Title.Should().Be(schema + ".upgraded has been read 10,000 rows deep and has never been analysed");
        finding.Evidence.Should().Contain(new HealthEvidence("Rows read since the counters began", "10,000"));
        finding.Finding.WhyItMatters.Should().Contain("vacuumdb --all --analyze-in-stages");

        await ExecuteAsync("analyze ps_maint_t08.upgraded");
        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport quiet = await AutovacuumServer.ReportAsync(host);

        AutovacuumServer.About(quiet, "T08", schema + ".upgraded").Should().BeEmpty();
        AutovacuumServer.Status(quiet, "T08").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }

    /// <summary>
    /// The healthy shapes the widened rule must not name (W5-V1 review): a five-row table, its key index built while it was
    /// empty so that <c>reltuples</c> stays <c>-1</c> - every Marten document table - read two thousand times by its key; and a
    /// table partitioned into thirty small tenants, each read often and none analysed, whose rows read add up to thousands.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("T08")]
    public async Task A_small_table_read_by_its_key_and_thirty_small_partitions_never_analysed_are_quiet()
    {
        const string schema = "ps_maint_t08_quiet";
        await postgres.CreateSchemaAsync(schema, Token);
        ManualClock clock = new(Start);
        await using StudioHost host = FixtureHost(schema, clock);

        List<string> statements =
        [
            "create table ps_maint_t08_quiet.small (id int primary key, note text not null)",
            "insert into ps_maint_t08_quiet.small select g, 'n' || g from generate_series(1, 5) g",
            "create table ps_maint_t08_quiet.tenants (tenant int not null, id int not null, note text) partition by list (tenant)",
        ];

        for (int tenant = 1; tenant <= 30; tenant++)
        {
            string number = Number(tenant);
            statements.Add("create table ps_maint_t08_quiet.tenants_" + number + " partition of ps_maint_t08_quiet.tenants for values in (" + number + ")");
        }

        statements.Add("insert into ps_maint_t08_quiet.tenants select t, g, 'n' || g from generate_series(1, 30) t, generate_series(1, 30) g");
        await ExecuteAsync([.. statements]);

        // Two thousand reads by the key, and each partition scanned ten times: 2,000 rows read from five, and 300 from each.
        await ExecuteAsync(
            "set enable_seqscan = off",
            "do $$ begin for i in 1..2000 loop perform note from ps_maint_t08_quiet.small where id = 1 + i % 5; end loop; end $$",
            "reset enable_seqscan",
            "do $$ begin for i in 1..10 loop perform pg_catalog.count(*) from ps_maint_t08_quiet.tenants; end loop; end $$");

        (await ScalarAsync<double>("select c.reltuples::float8 from pg_catalog.pg_class c where c.oid = 'ps_maint_t08_quiet.small'::regclass"))
            .Should().Be(-1, "its index was built while it was empty, and five rows never reach autoanalyze's 50");
        (await ScalarAsync<long>("select s.idx_tup_fetch from pg_catalog.pg_stat_user_tables s where s.relid = 'ps_maint_t08_quiet.small'::regclass"))
            .Should().BeGreaterThanOrEqualTo(2_000, "the reads that 0.5's first cut of the rule counted as a size");
        (await ScalarAsync<long>("select sum(s.seq_tup_read)::bigint from pg_catalog.pg_stat_user_tables s where s.relname like 'tenants\\_%'"))
            .Should().BeGreaterThanOrEqualTo(9_000, "thirty partitions' reads add up to a large table's, which a sum would have judged");

        await AutovacuumServer.ReportAsync(host);
        clock.Advance(TimeSpan.FromMinutes(11));
        HealthReport report = await AutovacuumServer.ReportAsync(host);

        IReadOnlyList<HealthReportFinding> ours = [.. report.Findings.Where(static x => x.Code == "T08"
            && x.Finding.Subject?.StartsWith(schema + ".", StringComparison.Ordinal) == true)];
        ours.Should().BeEmpty(string.Join(" | ", ours.Select(static x => x.Finding.Title)));
        AutovacuumServer.Status(report, "T08").Outcome.Should().Be(HealthCheckOutcome.Checked);
    }

    /// <summary>
    /// S02 on a server whose <c>autovacuum</c> and <c>track_activities</c> are switched off with <c>ALTER SYSTEM</c> and a
    /// reload: Critical and Warning, each named by its setting; T16 says no worker runs; reset, both are gone.
    /// </summary>
    /// <remarks>
    /// No <c>[HealthFires]</c> or <c>[HealthQuiet]</c> mark: S02 is one of the checks this suite's convention names as proven
    /// in the Marten suite (<c>HealthLiveTestConventionTests.ProvenElsewhere</c>), where its quiet half needs a server with
    /// <c>fsync</c> and <c>full_page_writes</c> on, and a mark on a code this suite does not claim fails the convention.
    /// </remarks>
    [PostgresFact]
    public async Task Autovacuum_and_track_activities_switched_off_server_wide_are_S02_until_they_are_reset()
    {
        await using AutovacuumServer server = await AutovacuumServer.StartAsync(17);
        await server.ExecuteAsync(
            "create schema ps_s02",
            "alter system set autovacuum = off",
            "alter system set track_activities = off",
            "select pg_catalog.pg_reload_conf()");

        await AutovacuumServer.UntilAsync(
            async () => await server.ScalarAsync<string>("select pg_catalog.current_setting('autovacuum')") == "off",
            TimeSpan.FromSeconds(30),
            "the reload to reach a new session");

        ManualClock clock = new(Start);
        await using StudioHost host = server.Host("ps_s02", clock);
        HealthReport report = await AutovacuumServer.ReportAsync(host);

        HealthReportFinding autovacuum = report.Findings.Should().ContainSingle(static x => x.Code == "S02" && x.Finding.Subject == "autovacuum").Subject;
        autovacuum.Severity.Should().Be(HealthSeverity.Critical);
        autovacuum.Finding.Title.Should().Be("Autovacuum is switched off for the whole server (autovacuum = off)");

        HealthReportFinding activities = report.Findings.Should().ContainSingle(static x => x.Code == "S02" && x.Finding.Subject == "track_activities").Subject;
        activities.Severity.Should().Be(HealthSeverity.Warning);
        activities.Finding.WhyItMatters.Should().Contain("C04, C05 and T14 cannot see sessions' state or age");

        AutovacuumServer.Status(report, "T16").Note.Should().Be("Autovacuum is switched off for the whole server (S02), so no worker runs.");

        await server.ExecuteAsync("alter system reset autovacuum", "alter system reset track_activities", "select pg_catalog.pg_reload_conf()");
        await AutovacuumServer.UntilAsync(
            async () => await server.ScalarAsync<string>("select pg_catalog.current_setting('autovacuum')") == "on",
            TimeSpan.FromSeconds(30),
            "the reset to reach a new session");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport reset = await AutovacuumServer.ReportAsync(host);
        reset.Findings.Should().NotContain(static x => x.Code == "S02" && (x.Finding.Subject == "autovacuum" || x.Finding.Subject == "track_activities"));
    }

    /// <summary>
    /// The Maintenance page's one statement of its own: a bloat measure, refused with the extension's sentence until
    /// <c>pgstattuple</c> is installed, then measured - the heap and its index - and audited either way, by table only.
    /// The page's view of the same database reads the triggers, the workers and the freeze forecast from the sweep.
    /// </summary>
    [PostgresFact]
    public async Task The_bloat_measure_refuses_without_pgstattuple_and_measures_the_table_and_its_index_with_it()
    {
        string connectionString = await postgres.CreateDatabaseAsync("ps_maint_bloat", Token);

        await RunAsync(
            connectionString,
            "create schema ps_bloat",
            "create table ps_bloat.notes (id int primary key, body text not null)",
            "insert into ps_bloat.notes select g, pg_catalog.repeat('n', 100) from generate_series(1, 20000) g",
            "delete from ps_bloat.notes where id % 2 = 0");

        ManualClock clock = new(Start);
        await using StudioHost host = StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
                services.AddSingleton<TimeProvider>(clock);
            },
            options => options.BrowsableSchemas.Add("ps_bloat"));

        MaintenanceView view = await ViewAsync(host);
        view.Refusal.Should().Be(MaintenanceRefusal.None, view.Reason);
        MaintenanceTableView notes = view.Tables.Should().ContainSingle(static x => x.Key == "ps_bloat.notes").Subject;
        notes.Measurable.Should().BeTrue();
        view.Workers.Should().NotBeNull("the sweep's workers read");
        view.Freeze.Should().NotBeNull("T12 and T13's facts, read by the sweep");

        BloatMeasureResult refused = await MeasureAsync(host);
        refused.Succeeded.Should().BeFalse();
        refused.Refusal.Should().Contain("CREATE EXTENSION pgstattuple");

        await RunAsync(connectionString, "create extension pgstattuple");
        clock.Advance(TimeSpan.FromMinutes(2));

        BloatMeasureResult measured = await MeasureAsync(host);
        measured.Refusal.Should().BeNull();
        measured.Measure!.DeadTuples.Should().BeGreaterThan(9_000, "half the rows were deleted and nothing has vacuumed them");
        measured.Measure.Indexes.Should().ContainSingle(static x => x.Name == "notes_pkey").Which.LeafDensity.Should().NotBeNull();

        StudioActionLogEntry[] entries = [.. host.Ring.GetLatest().Where(static x => x.Action == MaintenanceService.MeasureAction)];
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(static x => x.Target == "ps_bloat.notes", "the measure is audited by table only");
        entries.Select(static x => x.Succeeded).Should().BeEquivalentTo([true, false]);
    }

    /// <summary>
    /// C06's head of a chain is a VACUUM: a manual one, slowed to a crawl, holds <c>SHARE UPDATE EXCLUSIVE</c>; an
    /// <c>ALTER TABLE</c> queues behind it for <c>ACCESS EXCLUSIVE</c>, and a reader queues behind the ALTER. Judged a
    /// minute on, the finding names the VACUUM and says to let it finish.
    /// </summary>
    [PostgresFact]
    public async Task A_slowed_VACUUM_at_the_head_of_a_lock_chain_is_named_as_one_by_C06()
    {
        const string schema = "ps_maint_c06";
        await postgres.CreateSchemaAsync(schema, Token);
        await ExecuteAsync(
            // Autovacuum off for it, so the VACUUM the test starts has the dead rows to crawl through, not autovacuum before it.
            "create table ps_maint_c06.big (id int primary key, pad text not null) with (autovacuum_enabled = false)",
            "insert into ps_maint_c06.big select g, pg_catalog.repeat('p', 200) from generate_series(1, 20000) g",
            "delete from ps_maint_c06.big where id % 2 = 0");

        await using NpgsqlConnection vacuuming = await postgres.OpenAsync(Token);
        await using NpgsqlConnection altering = await postgres.OpenAsync(Token);
        await using NpgsqlConnection reading = await postgres.OpenAsync(Token);

        int vacuumPid = await PidAsync(vacuuming);
        await RunOnAsync(vacuuming, "set vacuum_cost_delay = 100", "set vacuum_cost_limit = 1");

        Task vacuum = RunOnAsync(vacuuming, "vacuum ps_maint_c06.big");
        Task alter = Task.CompletedTask;
        Task read = Task.CompletedTask;

        try
        {
            await AutovacuumServer.UntilAsync(
                async () => await ScalarAsync<long>("select pg_catalog.count(*) from pg_catalog.pg_stat_progress_vacuum where pid = " + Number(vacuumPid)) == 1,
                TimeSpan.FromSeconds(30),
                "the VACUUM to start");

            int alterPid = await PidAsync(altering);
            alter = RunOnAsync(altering, "alter table ps_maint_c06.big add column extra int");
            await WaitingAsync(alterPid);

            int readPid = await PidAsync(reading);
            read = RunOnAsync(reading, "select pg_catalog.count(*) from ps_maint_c06.big");
            await WaitingAsync(readPid);

            await using StudioHost host = FixtureHost(schema, new ManualClock(Start));
            LockWaitCheck check = new();
            LockWaitFacts? facts = null;

            // C06 reads a wait once it is a second old (ConnectionThresholds.CandidateFloor): read until both are.
            await AutovacuumServer.UntilAsync(
                async () => (facts = await GatherAsync(host, check)).Waiters.Count >= 2,
                TimeSpan.FromSeconds(30),
                "C06 to read the ALTER and the reader waiting");

            facts!.Blockers.Should().Contain(x => x.ProcessId == vacuumPid && x.Vacuum, "the head is the VACUUM, seen in pg_stat_progress_vacuum");

            HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddMinutes(1))).Findings
                .Should().ContainSingle(static x => x.Code == "C06").Subject;

            finding.Evidence.Should().Contain(x => x.Value.Contains("a VACUUM", StringComparison.Ordinal),
                "the head is labelled as the VACUUM it is: " + string.Join(" | ", finding.Evidence.Select(static x => x.Label + ": " + x.Value)));
            (finding.WhyItMatters + " " + finding.Remedy?.Explanation).Should().Contain("The head of the chain is a VACUUM.");
        }
        finally
        {
            await ExecuteAsync("select pg_catalog.pg_cancel_backend(" + Number(vacuumPid) + ")");

            try
            {
                await vacuum;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                // Cancelled on purpose: the ALTER and the reader then run.
            }

            await alter;
            await read;
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task WaitingAsync(int pid) =>
        await AutovacuumServer.UntilAsync(
            async () => await ScalarAsync<bool>("select exists (select 1 from pg_catalog.pg_locks where not granted and pid = " + Number(pid) + ")"),
            TimeSpan.FromSeconds(30),
            "process " + Number(pid) + " to wait for its lock");

    private static async Task<int> PidAsync(NpgsqlConnection connection)
    {
        await using NpgsqlCommand command = new("select pg_catalog.pg_backend_pid()", connection);
        return (int) (await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task RunOnAsync(NpgsqlConnection connection, params string[] statements)
    {
        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    private static async Task RunAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await RunOnAsync(connection, statements);

        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }

    private static Task<MaintenanceView> ViewAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IMaintenanceService>().GetAsync(Scope, Token));

    private static Task<BloatMeasureResult> MeasureAsync(StudioHost host) =>
        host.CircuitAsync("admin", services => services.GetRequiredService<IMaintenanceService>().MeasureBloatAsync(Scope, "ps_bloat", "notes", Token));

    /// <summary>One check's gatherer, run against the host's source the way a sweep runs it.</summary>
    private static async Task<TFacts> GatherAsync<TFacts>(StudioHost host, HealthCheck<TFacts> check)
        where TFacts : notnull
    {
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

        return await check.GatherAsync(context, Token);
    }

    private StudioHost FixtureHost(string schema, ManualClock clock) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.ConnectionString));
                services.AddSingleton<TimeProvider>(clock);
            },
            options => options.BrowsableSchemas.Add(schema));

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return (T) (await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    private async Task ExecuteAsync(params string[] statements)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await RunOnAsync(connection, statements);

        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }
}
