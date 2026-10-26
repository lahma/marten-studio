using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>T24 (autovacuum switched off for a table) and T16 (workers saturated) over stated facts (W5-V1).</summary>
public class AutovacuumChecksTests
{
    // -----------------------------------------------------------------------------------------------
    // T24 Autovacuum switched off for a table
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_switched_off_by_its_own_storage_parameter_is_a_warning_with_the_line_that_switches_it_on()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { StorageOptions = ["autovacuum_enabled=false"], AutovacuumOffLeaves = 1 });

        HealthFinding finding = new AutovacuumDisabledCheck().Evaluate(new AutovacuumDisabledFacts([table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T24");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be(orders.Key);
        finding.Title.Should().Be("Autovacuum is switched off for " + orders.Key);
        finding.Remedy!.Code.Should().Be("opts.Schema.For<HealthOrderDoc>().StorageParameter(\"autovacuum_enabled\", true);");
    }

    [Theory]
    [InlineData(99_999_999, false)]
    [InlineData(100_000_001, true)]
    public void Past_half_of_autovacuum_freeze_max_age_it_is_Critical(long xidAge, bool critical)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { AutovacuumOffLeaves = 1, OldestXidAge = xidAge, FreezeMaxAge = 200_000_000 });

        new AutovacuumDisabledCheck().Evaluate(new AutovacuumDisabledFacts([table]), Evaluation())
            .Findings.Should().ContainSingle().Which.Severity.Should().Be(critical ? HealthSeverity.Critical : HealthSeverity.Warning);
    }

    [Fact]
    public void Only_the_TOAST_table_switched_off_is_named_as_such_and_a_Marten_table_gets_no_line_Marten_cannot_declare()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { ToastAutovacuumOffLeaves = 1, ToastStorageOptions = ["autovacuum_enabled=false"] });

        HealthFinding finding = new AutovacuumDisabledCheck().Evaluate(new AutovacuumDisabledFacts([table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("Autovacuum is switched off for the TOAST table of " + orders.Key);
        finding.Evidence.Should().Contain(new HealthEvidence("TOAST storage parameters", "autovacuum_enabled=false"));
        finding.Remedy.Should().BeNull("Marten's StorageParameter does not manage toast.* parameters");
    }

    [Fact]
    public void A_host_s_relational_table_gets_the_migration_that_resets_its_TOAST_setting()
    {
        MartenTable orders = new("public", "orders", PostgresStudio.Services.Sources.StudioTableKind.Relational, PostgresStudio.Services.Sources.StudioTableOwnership.Owned);
        HealthRemedy remedy = PostgresStudio.Services.Postgres.PostgresHealthTableRemedies.Instance.ReenableToastAutovacuum(orders)!;

        remedy.Kind.Should().Be(HealthRemedyKind.Migration);
        remedy.Code.Should().Be("ALTER TABLE \"public\".\"orders\" RESET (toast.autovacuum_enabled);");
    }

    [Fact]
    public void Some_partitions_switched_off_are_counted_and_never_named()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { Kind = "p", PartitionLeaves = 12, AutovacuumOffLeaves = 3 });

        HealthFinding finding = new AutovacuumDisabledCheck().Evaluate(new AutovacuumDisabledFacts([table]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("Autovacuum is switched off for 3 of the 12 partitions of " + orders.Key);
        finding.Evidence.Should().Contain(new HealthEvidence("Switched off for", "3 partitions of 12"));
    }

    [Fact]
    public void Nothing_switched_off_is_quiet()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthVerdict verdict = new AutovacuumDisabledCheck().Evaluate(new AutovacuumDisabledFacts([Table(orders)]), Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("No judged table has autovacuum switched off");
    }

    // -----------------------------------------------------------------------------------------------
    // T16 Autovacuum workers saturated
    // -----------------------------------------------------------------------------------------------

    private static AutovacuumWorkersFacts Workers(int busy, bool canReadAllStats, int maxWorkers = 3, int? slots = null, string context = "postmaster") => new(
        Now,
        new MaintenanceWorkersRow
        {
            MaxWorkers = maxWorkers,
            MaxWorkersContext = context,
            WorkerSlots = slots,
            NaptimeSeconds = 60,
            Autovacuum = true,
            CanReadAllStats = canReadAllStats,
            WorkersSeen = canReadAllStats ? busy : 0,
            MaintenanceRunning = busy,
        },
        [],
        PostgresStudio.Services.Health.PostgresPlatform.SelfHosted);

    private static HealthHistoryView Saturated(int samples, bool brokenBefore = false)
    {
        List<(TimeSpan, (string, double)[])> history = [];

        if (brokenBefore)
        {
            history.Add((TimeSpan.FromMinutes(samples + 1), [(AutovacuumWorkersCheck.SweptSeries, 1)]));
        }

        for (int i = samples - 1; i >= 0; i--)
        {
            history.Add((TimeSpan.FromMinutes(i), [(AutovacuumWorkersCheck.SweptSeries, 1), (AutovacuumWorkersCheck.SaturatedSeries, 1)]));
        }

        return History([.. history]);
    }

    [Fact]
    public void Every_worker_busy_for_three_samples_running_is_a_warning_with_the_setting_to_raise()
    {
        HealthFinding finding = new AutovacuumWorkersCheck().Evaluate(Workers(3, canReadAllStats: true), Evaluation(Saturated(3)))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T16");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("Every one of the 3 autovacuum workers has been busy for 3 samples running");
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        finding.Remedy.Code.Should().EndWith("autovacuum_max_workers = 6");
        finding.Remedy.Explanation.Should().Contain("restarts", "before PostgreSQL 18 the setting is postmaster");
    }

    [Fact]
    public void Two_samples_are_watched_not_reported()
    {
        HealthVerdict verdict = new AutovacuumWorkersCheck().Evaluate(Workers(3, canReadAllStats: true), Evaluation(Saturated(2)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("3 samples running, and this is 2");
    }

    [Fact]
    public void A_break_in_the_run_starts_it_again()
    {
        AutovacuumWorkersCheck.SaturatedRun(Saturated(2, brokenBefore: true)).Should().Be(2);
        AutovacuumWorkersCheck.SaturatedRun(Saturated(5)).Should().Be(5);
    }

    [Fact]
    public void Without_pg_read_all_stats_it_counts_every_maintenance_process_and_says_so()
    {
        HealthFinding finding = new AutovacuumWorkersCheck().Evaluate(Workers(3, canReadAllStats: false), Evaluation(Saturated(4)))
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("At least 3 maintenance processes - as many as autovacuum_max_workers, manual VACUUMs included");
        finding.Caveat.Should().Contain("not a member of pg_read_all_stats").And.Contain("includes manual runs");
        finding.Evidence.Should().Contain(new HealthEvidence("Busy", "at least 3 maintenance processes running, of 3 workers, manual VACUUMs included"));
    }

    [Fact]
    public void On_PostgreSQL_18_it_changes_with_a_reload_up_to_the_worker_slots()
    {
        HealthFinding finding = new AutovacuumWorkersCheck().Evaluate(Workers(3, true, slots: 5, context: "sighup"), Evaluation(Saturated(3)))
            .Findings.Should().ContainSingle().Subject;

        finding.Remedy!.Explanation.Should().NotContain("restarts").And.Contain("only up to autovacuum_worker_slots (5)");
        finding.Evidence.Should().Contain(x => x.Label == "autovacuum_worker_slots");
    }

    [Fact]
    public void Fewer_busy_than_the_workers_is_quiet_and_says_how_many()
    {
        HealthVerdict verdict = new AutovacuumWorkersCheck().Evaluate(Workers(1, true), Evaluation(Saturated(5)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("1 of 3 autovacuum workers are busy.");
    }

    [Fact]
    public void Sampling_writes_the_saturation_and_each_running_command_s_blocks_by_number_only()
    {
        AutovacuumWorkersFacts facts = Workers(3, true) with
        {
            Running =
            [
                new MaintenanceProgressRow { Kind = "vacuum", ProcessId = 42, RelationId = 16_384, Schema = "public", Table = "orders", BlocksDone = 1_200, BlocksTotal = 10_000 },
                new MaintenanceProgressRow { Kind = "analyze", ProcessId = 43, RelationId = null, BlocksDone = 5 },
            ],
        };

        new AutovacuumWorkersCheck().Sample(facts).Should().BeEquivalentTo(
        [
            new HealthSamplePart(AutovacuumWorkersCheck.SweptSeries, 1),
            new HealthSamplePart(AutovacuumWorkersCheck.SaturatedSeries, 1),
            new HealthSamplePart(AutovacuumWorkersCheck.ProgressSeries + "vacuum:42:16384", 1_200),
        ]);
    }
}
