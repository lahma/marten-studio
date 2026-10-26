using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Maintenance;

/// <summary>
/// W5-V1's autovacuum checks against real servers: T24 on a table switched off and on its TOAST table, quiet once the
/// remedies it prints are run; T16 with every worker of an <c>autovacuum_max_workers = 1</c> server held by slow tables,
/// as a role that sees the workers and as one that sees only the progress views.
/// </summary>
/// <remarks>
/// <b>Slow on purpose, briefly.</b> T16's tables vacuum at a crawl - <c>autovacuum_vacuum_cost_limit = 1</c> and
/// <c>autovacuum_vacuum_cost_delay = 100</c> as their own storage parameters - so the one worker stays on them for the
/// three sweeps the finding needs. The server is the test's own and goes with it, vacuum and all.
/// </remarks>
public class AutovacuumLiveTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// T24 names a table whose own storage parameter switches autovacuum off, and one switched off only on its TOAST table,
    /// each with the migration statement that switches it back - and once those statements are run it is quiet. T16 looks
    /// in the same sweeps, on a server whose three workers are not all busy, and says how many are.
    /// </summary>
    [PostgresFact]
    [HealthFires("T24")]
    [HealthQuiet("T24")]
    [HealthQuiet("T16")]
    public async Task A_table_switched_off_and_one_off_on_its_TOAST_table_are_T24_until_their_remedies_run()
    {
        const string schema = "ps_maint_t24";
        await postgres.CreateSchemaAsync(schema, Token);
        ManualClock clock = new(Start);
        await using StudioHost host = Host(schema, clock);

        await ExecuteAsync(
            "create table ps_maint_t24.loaded (id int primary key, note text) with (autovacuum_enabled = false)",
            "create table ps_maint_t24.notes (id int primary key, body text) with (toast.autovacuum_enabled = false)",
            "create table ps_maint_t24.healthy (id int primary key, note text)",
            "insert into ps_maint_t24.loaded select g, 'n' || g from generate_series(1, 100) g",
            "insert into ps_maint_t24.healthy select g, 'n' || g from generate_series(1, 100) g");

        HealthReport report = await AutovacuumServer.ReportAsync(host);

        HealthReportFinding loaded = AutovacuumServer.About(report, "T24", schema + ".loaded").Should().ContainSingle().Subject;
        loaded.Severity.Should().Be(HealthSeverity.Warning, "the table is nowhere near its forced freeze");
        loaded.Finding.Title.Should().Be("Autovacuum is switched off for " + schema + ".loaded");
        loaded.Evidence.Should().Contain(new HealthEvidence("Switched off for", "the table"));
        loaded.Finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
        loaded.Finding.Remedy.Code.Should().Be("ALTER TABLE \"ps_maint_t24\".\"loaded\" SET (autovacuum_enabled = true);");

        HealthReportFinding notes = AutovacuumServer.About(report, "T24", schema + ".notes").Should().ContainSingle().Subject;
        notes.Finding.Title.Should().Be("Autovacuum is switched off for the TOAST table of " + schema + ".notes");
        notes.Evidence.Should().Contain(new HealthEvidence("TOAST storage parameters", "autovacuum_enabled=false"));
        notes.Finding.Remedy!.Code.Should().Be("ALTER TABLE \"ps_maint_t24\".\"notes\" RESET (toast.autovacuum_enabled);");

        AutovacuumServer.About(report, "T24", schema + ".healthy").Should().BeEmpty();

        // The remedies, run as printed: the check is quiet once they are, because it looked.
        await ExecuteAsync(loaded.Finding.Remedy.Code, notes.Finding.Remedy.Code);

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport fixedReport = await AutovacuumServer.ReportAsync(host);

        fixedReport.Findings.Should().NotContain(x => x.Code == "T24" && x.Finding.Subject!.StartsWith(schema + ".", StringComparison.Ordinal));
        HealthCheckStatus t24 = AutovacuumServer.Status(fixedReport, "T24");
        t24.Outcome.Should().Be(HealthCheckOutcome.Checked, t24.Problem);
        t24.Statements.Should().NotBeEmpty("T24 read the table facts");

        HealthCheckStatus t16 = AutovacuumServer.Status(fixedReport, "T16");
        t16.Outcome.Should().Be(HealthCheckOutcome.Checked, t16.Problem);
        t16.FindingCount.Should().Be(0);
        t16.Note.Should().Contain(" of 3 autovacuum workers are busy", "the superuser sees the workers for what they are, and three is the default");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// T16, held: <c>autovacuum_max_workers = 1</c>, two tables that vacuum at a crawl, and three sweeps while the one
    /// worker is on them. The superuser counts the worker by <c>backend_type</c>; a role in no monitoring role counts the
    /// processes in the progress views, which it sees for every role, and says that is at least the workers.
    /// </summary>
    [PostgresFact]
    [HealthFires("T16")]
    public async Task One_worker_held_by_slow_tables_for_three_sweeps_is_T16_as_either_role_can_count_it()
    {
        await using AutovacuumServer server = await AutovacuumServer.StartAsync(18, "autovacuum_max_workers=1");

        await server.ExecuteAsync(
            "create role probe login password 'probe'",
            "create schema ps_t16",
            "create table ps_t16.slow_a (id int, pad text) with (autovacuum_vacuum_cost_limit = 1, autovacuum_vacuum_cost_delay = 100)",
            "create table ps_t16.slow_b (id int, pad text) with (autovacuum_vacuum_cost_limit = 1, autovacuum_vacuum_cost_delay = 100)",
            "insert into ps_t16.slow_a select g, pg_catalog.repeat('x', 200) from generate_series(1, 10000) g",
            "insert into ps_t16.slow_b select g, pg_catalog.repeat('x', 200) from generate_series(1, 10000) g",
            "analyze ps_t16.slow_a",
            "analyze ps_t16.slow_b");

        // Half of each dead: past their 2,050-row triggers, so the one worker takes one and the other waits for it.
        await server.ExecuteAsync("delete from ps_t16.slow_a where id % 2 = 0", "delete from ps_t16.slow_b where id % 2 = 0");

        await AutovacuumServer.UntilAsync(
            async () => await server.ScalarAsync<long>(
                "select pg_catalog.count(*) from pg_catalog.pg_stat_progress_vacuum v join pg_catalog.pg_class c on c.oid = v.relid "
                + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = 'ps_t16'") > 0,
            TimeSpan.FromSeconds(60),
            "the one autovacuum worker to start on a slow table");

        ManualClock privilegedClock = new(Start);
        ManualClock probeClock = new(Start);
        await using StudioHost privileged = server.Host("ps_t16", privilegedClock);
        await using StudioHost probe = server.Host("ps_t16", probeClock, "probe", "probe");

        HealthReport? seen = null;
        HealthReport? seenAsProbe = null;

        for (int sample = 1; sample <= AutovacuumWorkersCheck.SamplesNeeded; sample++)
        {
            seen = await AutovacuumServer.ReportAsync(privileged);
            seenAsProbe = await AutovacuumServer.ReportAsync(probe);

            if (sample < AutovacuumWorkersCheck.SamplesNeeded)
            {
                seen.Findings.Should().NotContain(static x => x.Code == "T16", "a busy minute is not a finding: sample {0}", sample);
                AutovacuumServer.Status(seen, "T16").Note.Should().Contain("Every autovacuum worker is busy (1 of 1 autovacuum workers are busy)");
            }

            privilegedClock.Advance(TimeSpan.FromMinutes(2));
            probeClock.Advance(TimeSpan.FromMinutes(2));
        }

        HealthReportFinding finding = seen!.Findings.Should().ContainSingle(static x => x.Code == "T16").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Be("Every one of the 1 autovacuum workers has been busy for 3 samples running");
        finding.Evidence.Should().Contain(new HealthEvidence("Busy", "1 of 1 autovacuum workers are busy"));
        finding.Evidence.Should().Contain(new HealthEvidence("autovacuum_max_workers", "1 (changes with a reload)"), "PostgreSQL 18 reloads it");
        finding.Finding.Remedy!.Code.Should().Contain("autovacuum_max_workers").And.Contain("2");
        finding.Finding.Caveat.Should().StartWith("Counted in pg_stat_activity by backend_type");

        HealthReportFinding counted = seenAsProbe!.Findings.Should().ContainSingle(static x => x.Code == "T16",
            "the progress views show every role the process ids of the vacuums running").Subject;
        counted.Finding.Title.Should().StartWith("At least 1 maintenance processes - as many as autovacuum_max_workers, manual VACUUMs included");
        counted.Finding.Caveat.Should().Contain("is not a member of pg_read_all_stats");

        privileged.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    private StudioHost Host(string schema, ManualClock clock) =>
        StudioHost.Create(
            services =>
            {
                services.AddSingleton(_ => NpgsqlDataSource.Create(postgres.ConnectionString));
                services.AddSingleton<TimeProvider>(clock);
            },
            options => options.BrowsableSchemas.Add(schema));

    /// <summary>Runs statements on one connection of the test's own, then flushes its statistics.</summary>
    private async Task ExecuteAsync(params string[] statements)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand flush = new("select pg_catalog.pg_stat_force_next_flush()", connection);
        await flush.ExecuteNonQueryAsync(Token);
    }
}
