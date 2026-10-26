using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>R04 at every edge: the share over each release's own denominator, and the "more than the timeout allows" trigger.</summary>
public class RequestedCheckpointsCheckTests
{
    private static readonly RequestedCheckpointsCheck Check = new();
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    private static CheckpointStatistics Counters(CheckpointCounting counting, long timed, long requested, long? done = null) =>
        new(counting == CheckpointCounting.Bgwriter ? "pg_stat_bgwriter" : "pg_stat_checkpointer", counting, timed, requested, done,
            null, null, null, null, null, null, null, null, null, null, Now.AddDays(-3));

    [Fact]
    public void R04_is_a_database_gate_catalog_check_with_one_kind_of_finding()
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(RequestedCheckpointsCheck));

        info.Gate.Should().Be(HealthGate.Database);
        info.Variants.Should().BeEmpty();
    }

    // ---- The share, per release -------------------------------------------------------------------

    [Theory]
    [InlineData(8, 2, true)]   // 2 of 10: 20%
    [InlineData(9, 2, false)]  // 2 of 11: 18%
    [InlineData(8, 1, false)]  // 9 checkpoints: too few to judge
    [InlineData(40, 9, false)] // 9 of 49: 18%
    [InlineData(40, 10, true)] // 10 of 50: 20%
    public void Up_to_16_the_share_is_requested_over_timed_plus_requested(int timed, int requested, bool expected)
    {
        RequestedCheckpointsCheck.Judgement judged = RequestedCheckpointsCheck.Judge(CheckpointCounting.Bgwriter, timed, requested, null, OneHour, FiveMinutes);

        judged.Denominator.Should().Be(timed + requested);
        judged.ByShare.Should().Be(expected);
    }

    [Fact]
    public void On_17_the_denominator_is_the_same_but_counts_skipped_checkpoints()
    {
        // 17's num_timed counts the checkpoints an idle server skipped: 2 requested of 8 + 2 is 20%.
        RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerWithSkipped, 8, 2, null, OneHour, FiveMinutes).ByShare.Should().BeTrue();

        // An idle-then-busy server: ninety skipped timed checkpoints dilute twelve requested ones below the share...
        RequestedCheckpointsCheck.Judgement diluted = RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerWithSkipped, 90, 12, null, TimeSpan.FromMinutes(30), FiveMinutes);
        diluted.ByShare.Should().BeFalse();

        // ...and the second trigger, which no skipped checkpoint dilutes, still fires: 12 requested in 30 minutes, where
        // checkpoint_timeout would have run 6.
        diluted.ByTimeout.Should().BeTrue();
        diluted.Fires.Should().BeTrue();
    }

    [Fact]
    public void From_18_the_share_is_requested_over_num_done()
    {
        // B0 G8: 8 timed, 2 requested and 6 done - num_timed counts skipped ones, num_done the ten that ran here.
        RequestedCheckpointsCheck.Judgement judged = RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerDone, 18, 2, 10, OneHour, FiveMinutes);

        judged.Denominator.Should().Be(10);
        judged.Share.Should().Be(0.2);
        judged.ByShare.Should().BeTrue();

        RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerDone, 18, 1, 10, OneHour, FiveMinutes).ByShare.Should().BeFalse();
        RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerDone, 18, 2, 9, OneHour, FiveMinutes).ByShare.Should().BeFalse("nine done are too few");
    }

    [Fact]
    public void The_since_reset_share_uses_the_same_denominators()
    {
        Counters(CheckpointCounting.Bgwriter, 8, 2).RequestedShare.Should().Be(0.2);
        Counters(CheckpointCounting.CheckpointerWithSkipped, 8, 2).RequestedShare.Should().Be(0.2);
        Counters(CheckpointCounting.CheckpointerDone, 8, 2, 6).RequestedShare.Should().BeApproximately(1d / 3, 1e-9);
        Counters(CheckpointCounting.CheckpointerDone, 0, 0, 0).RequestedShare.Should().BeNull();
    }

    // ---- More than checkpoint_timeout allows ------------------------------------------------------

    [Theory]
    [InlineData(10, 60, false)] // 10 requested in an hour: the timeout allows 12
    [InlineData(13, 60, true)]  // 13 > 12
    [InlineData(10, 30, true)]  // 10 > 6
    [InlineData(9, 30, false)]  // fewer than ten
    public void More_requested_than_the_timeout_would_have_run_fires_on_every_release(int requested, int minutes, bool expected)
    {
        RequestedCheckpointsCheck.Judge(CheckpointCounting.CheckpointerWithSkipped, 10_000, requested, null, TimeSpan.FromMinutes(minutes), FiveMinutes)
            .ByTimeout.Should().Be(expected);
    }

    [Fact]
    public void Without_checkpoint_timeout_the_second_trigger_says_nothing()
    {
        RequestedCheckpointsCheck.Judge(CheckpointCounting.Bgwriter, 1000, 50, null, TimeSpan.FromMinutes(5), checkpointTimeout: null).ByTimeout.Should().BeFalse();
    }

    // ---- Evaluate: the advisor's window, and the remedy ------------------------------------------

    private static RequestedCheckpointsFacts Facts(CheckpointStatistics checkpoints, PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted) =>
        new(ServerOf(170011, platform), checkpoints, new WalStatistics(1000, 200, 5e9, 0, null, null, null, null, null), Archiving());

    [Fact]
    public void One_sample_is_watching()
    {
        HealthVerdict verdict = Check.Evaluate(Facts(Counters(CheckpointCounting.CheckpointerWithSkipped, 100, 50)), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().StartWith("Watching:").And.Contain("100 timed, 50 requested");
    }

    [Fact]
    public void Requested_checkpoints_in_the_window_are_a_warning_with_max_wal_size_doubled()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-20), [(ReplicationHistory.CheckpointsTimed, 100), (ReplicationHistory.CheckpointsRequested, 50)]),
            (Now, [(ReplicationHistory.CheckpointsTimed, 104), (ReplicationHistory.CheckpointsRequested, 62)]));

        HealthFinding finding = Check.Evaluate(Facts(Counters(CheckpointCounting.CheckpointerWithSkipped, 104, 62)), At(history))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("12 checkpoints in 20 min were requested rather than timed (75%)");
        finding.Evidence.Should().Contain(new HealthEvidence("Requested checkpoints", "12"));
        finding.Evidence.Should().Contain(new HealthEvidence("Checkpoints counted (num_timed + num_requested, skipped ones included)", "16"));
        finding.Caveat.Should().Contain("skipped too");

        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        finding.Remedy.Code.Should().Be("# A server setting, not a StoreOptions one:\nmax_wal_size = 2GB");
        finding.Remedy.Explanation.Should().StartWith("Double it (from 1GB)").And.EndWith("Set it in postgresql.conf, followed by a reload.");
    }

    [Fact]
    public void A_managed_platform_words_where_max_wal_size_changes()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-20), [(ReplicationHistory.CheckpointsTimed, 0), (ReplicationHistory.CheckpointsRequested, 0)]),
            (Now, [(ReplicationHistory.CheckpointsTimed, 2), (ReplicationHistory.CheckpointsRequested, 12)]));

        HealthFinding finding = Check.Evaluate(Facts(Counters(CheckpointCounting.Bgwriter, 2, 12), PostgresPlatformKind.AmazonRds), At(history)).Findings.Single();

        finding.Remedy!.Explanation.Should().EndWith("Set it in the instance's DB parameter group (Amazon RDS).");
        finding.Caveat.Should().Contain("Up to PostgreSQL 16, checkpoints_timed counts the checkpoints an idle server skipped too",
            "16 and older count skipped checkpoints as timed, as 17 does (W5-R2-fix 10)");
    }

    [Fact]
    public void A_counter_reset_starts_the_window_again()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-30), [(ReplicationHistory.CheckpointsTimed, 900), (ReplicationHistory.CheckpointsRequested, 400)]),
            (Now.AddMinutes(-10), [(ReplicationHistory.CheckpointsTimed, 1), (ReplicationHistory.CheckpointsRequested, 0)]),
            (Now, [(ReplicationHistory.CheckpointsTimed, 3), (ReplicationHistory.CheckpointsRequested, 1)]));

        HealthVerdict verdict = Check.Evaluate(Facts(Counters(CheckpointCounting.Bgwriter, 3, 1)), At(history));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("In the last 10 min, 1 requested checkpoint of 3 - fewer than 10 to judge a share by.");
    }

    [Fact]
    public void It_samples_the_checkpoint_counters_and_the_wal_counters_the_page_reads()
    {
        Check.Sample(Facts(Counters(CheckpointCounting.CheckpointerDone, 8, 2, 6))).Select(static x => x.Series).Should().Equal(
            ReplicationHistory.CheckpointsTimed, ReplicationHistory.CheckpointsRequested, ReplicationHistory.CheckpointsDone,
            ReplicationHistory.WalRecords, ReplicationHistory.WalFullPageImages, ReplicationHistory.WalBytes);
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024 * 2, "2GB")]
    [InlineData(1024L * 1024 * 1536, "1536MB")]
    [InlineData(1024L * 1024 * 64, "64MB")]
    public void A_doubled_size_is_written_as_the_setting_takes_it(long bytes, string text)
    {
        RequestedCheckpointsCheck.Size(bytes).Should().Be(text);
    }
}
