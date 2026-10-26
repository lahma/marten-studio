using MartenStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Export;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Health;

/// <summary>
/// W5-API's export rotation (API design §3.4, the orchestrator's decision 4) and its reuse of a page's sweep (backlog C):
/// every one-database source in every run, the others from a cursor per source, each database's latest result reported
/// while it is young enough, and a page's sweep finished within the interval read rather than repeated.
/// </summary>
public class HealthExportRotationTests
{
    private static readonly StudioSourceRegistration Admin = new("admin", "admin", StudioSourceKind.DataSource);
    private static readonly StudioSourceRegistration Tenants = new("tenants", "tenants", StudioSourceKind.HostProvider);
    private static readonly StudioSourceRegistration Others = new("others", "others", StudioSourceKind.HostProvider);

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static StudioListedDatabase Single(StudioSourceRegistration source, string database) =>
        new(source, StudioSweepTargetOutcome.Database, database) { SingleDatabaseSource = true };

    private static StudioListedDatabase Many(StudioSourceRegistration source, string database) =>
        new(source, StudioSweepTargetOutcome.Database, database);

    private static string[] Names(HealthExportPlan plan) => [.. plan.Targets.Select(static x => x.Source.Key + "/" + x.DatabaseId)];

    [Fact]
    public void Every_one_database_source_is_in_every_run_and_the_rest_rotate_from_their_cursor()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [Single(Admin, "admin"), .. Enumerable.Range(1, 5).Select(static x => Many(Tenants, "t" + x))];

        Names(rotation.Plan(listed, 3)).Should().Equal("admin/admin", "tenants/t1", "tenants/t2");
        Names(rotation.Plan(listed, 3)).Should().Equal("admin/admin", "tenants/t3", "tenants/t4");
        // The cursor wraps.
        Names(rotation.Plan(listed, 3)).Should().Equal("admin/admin", "tenants/t5", "tenants/t1");
    }

    [Fact]
    public void Two_multi_database_sources_take_turns_so_neither_waits_behind_the_other()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed =
        [
            .. Enumerable.Range(1, 4).Select(static x => Many(Tenants, "t" + x)),
            .. Enumerable.Range(1, 2).Select(static x => Many(Others, "o" + x)),
        ];

        Names(rotation.Plan(listed, 3)).Should().Equal("tenants/t1", "others/o1", "tenants/t2");
        Names(rotation.Plan(listed, 3)).Should().Equal("tenants/t3", "others/o2", "tenants/t4");
    }

    [Fact]
    public void A_source_smaller_than_the_limit_is_taken_whole_and_once()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [Single(Admin, "admin"), Many(Tenants, "t1"), Many(Tenants, "t2")];

        HealthExportPlan plan = rotation.Plan(listed, 25);

        Names(plan).Should().Equal("admin/admin", "tenants/t1", "tenants/t2");
        plan.RunsPerRotation.Should().Be(1);
        plan.RunsKept.Should().Be(2, "one rotation and one run more");
    }

    [Fact]
    public void A_result_is_kept_for_one_rotation_and_one_run_more()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [Single(Admin, "admin"), .. Enumerable.Range(1, 200).Select(static x => Many(Tenants, "t" + x))];

        HealthExportPlan plan = rotation.Plan(listed, 25);

        plan.Targets.Should().HaveCount(25);
        plan.RunsPerRotation.Should().Be(9, "two hundred tenants, twenty-four a run after the admin database");
        plan.RunsKept.Should().Be(10);
    }

    [Fact]
    public void More_one_database_sources_than_one_run_takes_rotate_too()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [.. Enumerable.Range(1, 3).Select(static x => Single(new StudioSourceRegistration("ds" + x, "ds" + x, StudioSourceKind.DataSource), "ds" + x))];

        Names(rotation.Plan(listed, 2)).Should().Equal("ds1/ds1", "ds2/ds2");
        // None waits for ever.
        Names(rotation.Plan(listed, 2)).Should().Equal("ds3/ds3", "ds1/ds1");
    }

    [Fact]
    public void A_latest_result_is_reported_for_its_runs_and_only_a_database_never_checked_is_skipped()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [Many(Tenants, "t1"), Many(Tenants, "t2"), Many(Tenants, "t3")];
        const int RunsKept = 2;

        rotation.Plan(listed, 1);
        rotation.Remember(HealthExportRotation.KeyOf(listed[1]), Result(Now.AddHours(-1)));
        rotation.Plan(listed, 1);
        rotation.Plan(listed, 1);
        rotation.Remember(HealthExportRotation.KeyOf(listed[0]), Result(Now, critical: 1));

        (IReadOnlyList<HealthExportDatabaseResult> latest, int skipped) = rotation.Collect(listed, new HashSet<string>(), RunsKept);

        latest.Should().ContainSingle().Which.Checks.Should().ContainSingle(static x => x.Critical == 1, "a finding stays reported until a sweep clears it");
        skipped.Should().Be(1, "t2 was checked two runs ago - aged out, but checked, so not skipped; only t3 was never checked");

        (_, int attemptedNotSkipped) = rotation.Collect(listed, new HashSet<string> { HealthExportRotation.KeyOf(listed[2]) }, RunsKept);
        attemptedNotSkipped.Should().Be(0, "a database this run set out to check is the run's to count, as timed out or not reached");
    }

    /// <summary>
    /// W5-API-fix F3: the window is counted in runs, so a run that takes far longer than an interval - slow databases, a big
    /// rotation - does not age a result out before the rotation comes back to it. Ten runs, each an hour long on the clock.
    /// </summary>
    [Fact]
    public void A_slow_run_does_not_age_a_result_out_before_the_rotation_returns()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase[] listed = [.. Enumerable.Range(1, 6).Select(static x => Many(Tenants, "t" + x))];
        DateTimeOffset clock = Now;

        for (int i = 1; i <= 10; i++)
        {
            HealthExportPlan plan = rotation.Plan(listed, 2);
            plan.RunsPerRotation.Should().Be(3);

            foreach (StudioListedDatabase target in plan.Targets)
            {
                clock = clock.AddMinutes(30);
                rotation.Remember(HealthExportRotation.KeyOf(target), Result(clock, critical: 1));
            }

            (IReadOnlyList<HealthExportDatabaseResult> latest, int skipped) =
                rotation.Collect(listed, new HashSet<string>(plan.Targets.Select(HealthExportRotation.KeyOf)), plan.RunsKept);

            latest.Should().HaveCount(Math.Min(6, 2 * i), "run " + i + ": every database checked so far is still reported");
            skipped.Should().Be(Math.Max(0, 6 - (2 * i)), "run " + i + ": only the databases no run has reached yet");
        }
    }

    [Fact]
    public void A_database_no_longer_listed_is_forgotten()
    {
        HealthExportRotation rotation = new();
        StudioListedDatabase gone = Many(Tenants, "gone");
        rotation.Remember(HealthExportRotation.KeyOf(gone), Result(Now));

        rotation.Collect([Many(Tenants, "t1")], new HashSet<string>(), 2);

        rotation.Remembered.Should().Be(0, "its memory goes with it: the export holds a result only for a database a source lists");
    }

    /// <summary>
    /// W5-API-fix F3, through the runner: each database's sweep takes 45 s on the clock, so a run of two takes 90 s - longer
    /// than the 60 s interval. A window counted in time (a rotation and an interval, 180 s) aged the first run's second
    /// database out before the second run's collect; counted in runs, all three are reported and none is skipped.
    /// </summary>
    [Fact]
    public async Task Slow_runs_keep_every_checked_database_reported()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            limits: new HealthExportLimits(MaxDatabases: 2),
            tenancy: static options => options.MultiTenantedDatabases(static tenancy =>
            {
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_slow_a"), "a");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_slow_b"), "b");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_slow_c"), "c");
            }));

        harness.Script.Finds("K90", HealthSeverity.Critical);
        harness.Script.Gather["K90"] = (_, _) =>
        {
            harness.Clock.Advance(TimeSpan.FromSeconds(45));
            return Task.FromResult(new ScriptedFacts("K90", [], null));
        };

        HealthReportEntry first = await harness.ProbeAsync();
        first.Data["databases"].Should().Be(2);

        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();

        HealthReportEntry second = await harness.ProbeAsync();

        second.Data["databases"].Should().Be(3, "the first run's databases are within one rotation and one run of it, however long the runs took");
        second.Data["databasesSkipped"].Should().Be(0);
        ((string[]) second.Data["critical"]).Should().Equal("K90");
    }

    [Fact]
    public async Task A_second_run_checks_what_the_first_had_no_room_for_and_reports_all_of_them()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            limits: new HealthExportLimits(MaxDatabases: 2),
            tenancy: static options => options.MultiTenantedDatabases(static tenancy =>
            {
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_a"), "a");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_b"), "b");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_c"), "c");
            }));

        harness.Script.Finds("K90", HealthSeverity.Critical);

        HealthReportEntry first = await harness.ProbeAsync();
        first.Data["databases"].Should().Be(2);
        first.Data["databasesSkipped"].Should().Be(1);

        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();

        HealthReportEntry second = await harness.ProbeAsync();

        second.Data["databases"].Should().Be(3, "the second run checked the third and kept the first run's latest for the others");
        second.Data["databasesSkipped"].Should().Be(0, "only a database no run has checked yet is skipped");
        second.Status.Should().Be(HealthStatus.Unhealthy);
        ((string[]) second.Data["critical"]).Should().Equal("K90");
        harness.Script.Runs["K90"].Should().Be(4, "two a run: the third, then the first again from the cursor");
    }

    [Fact]
    public async Task A_page_sweep_finished_within_the_interval_is_reported_and_never_repeated()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        harness.Script.Finds("K90", HealthSeverity.Warning);

        // A visitor's Health page sweeps the database first.
        HealthSweepCache pages = harness.Services.GetRequiredService<HealthSweepCache>();
        int pageSweeps = pages.SweepCount;

        await using (AsyncServiceScope scope = harness.Services.CreateAsyncScope())
        {
            StudioUnattendedResolution resolved = await scope.ServiceProvider.GetRequiredService<IStudioSourceProvider>()
                .ResolveUnattendedAsync("default", string.Empty, TestContext.Current.CancellationToken);
            resolved.Resolved.Should().BeOfType<ResolvedScope>();
            await pages.GetAsync(resolved.Resolved!, HealthSweepKind.Regular, null, TestContext.Current.CancellationToken);
        }

        harness.Script.Runs["K90"].Should().Be(1);

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Data["databases"].Should().Be(1);
        entry.Status.Should().Be(HealthStatus.Degraded, "the page's Warning is the export's too");
        harness.Script.Runs["K90"].Should().Be(1, "the export read the page's sweep, a minute old at most, and ran none of its own");
        pages.SweepCount.Should().Be(pageSweeps + 1, "and filled nothing in the pages' cache");

        harness.Clock.Advance(TimeSpan.FromSeconds(61));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();

        harness.Script.Runs["K90"].Should().Be(2, "a page sweep older than the interval is not reused: the export sweeps afresh");
        pages.SweepCount.Should().Be(pageSweeps + 1, "through the sweeper, never the pages' cache");
    }

    private static HealthExportDatabaseResult Result(DateTimeOffset at, int critical = 0) =>
        new(at, critical == 0 ? [] : [new HealthExportCheckResult("K90", HealthCheckOutcome.Checked, null, critical, 0, 0)]);
}
