using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// W5-AUR A4: what a sweep samples of Aurora's counters, and the Server page's window deltas over those samples - each wait's
/// own window, the commit latency as Δ latency over Δ commits, never across a reset or another instance.
/// </summary>
public class AuroraWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly PostgresPlatform Aurora = new(PostgresPlatformKind.AmazonAurora, "marker");

    private static readonly PostgresServerFacts Writer = new(Now, 160004, "16.4", Aurora) { Instance = "studio-test-instance-1" };

    private static AuroraWaitEvent Wait(string type, string name, double waits, double microseconds, long id = 1) =>
        new(10, id, type, name, waits, microseconds);

    private static AuroraWaitProfile Profile(params AuroraWaitEvent[] events) =>
        new(Now, [.. events.OrderByDescending(static x => x.TimeMicroseconds)], 0);

    private static HealthSample Sample(DateTimeOffset at, PostgresServerFacts server, AuroraWaitProfile? waits, AuroraDatabaseActivity? database) =>
        new(at, AuroraSamples.Of(server, waits, database).ToDictionary(static x => x.Series, static x => x.Value));

    [Fact]
    public void A_sweep_samples_the_instance_the_twenty_longest_waits_the_commits_and_the_dml_at_the_database_gate()
    {
        AuroraWaitProfile profile = Profile([.. Enumerable.Range(1, 25).Select(i => Wait("IO", "E" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i, i * 100, i))]);
        AuroraDatabaseActivity database = AuroraDatabaseActivity.FromJson(Now, 40, "8000", """{"select_count": 10, "select_latency_microsecs": 500}""");

        List<HealthSamplePart> parts = [.. AuroraSamples.Of(Writer, profile, database)];

        parts.Should().OnlyContain(static x => x.Gate == HealthGate.Database, "an instance's waits and a database's commits are database-wide numbers");
        parts.Select(static x => x.Series).Should().OnlyHaveUniqueItems();
        parts.Should().ContainSingle(static x => x.Series == AuroraSamples.InstanceSeries).Which.Value.Should().Be(StatisticsWindow.Hash("studio-test-instance-1"));
        parts.Count(static x => x.Series.StartsWith(AuroraSamples.WaitTimePrefix, StringComparison.Ordinal)).Should().Be(AuroraWaitProfile.Sampled);
        parts.Should().Contain(new HealthSamplePart("aurora.wait:IO:E25", 2500) { Gate = HealthGate.Database });
        parts.Should().NotContain(static x => x.Series == "aurora.wait:IO:E5", "only the twenty longest are sampled");
        parts.Should().Contain(new HealthSamplePart(AuroraSamples.CommitLatencySeries, 8000) { Gate = HealthGate.Database });
        parts.Should().Contain(new HealthSamplePart(AuroraSamples.CommitsSeries, 40) { Gate = HealthGate.Database });
        parts.Should().Contain(new HealthSamplePart("aurora.dml-count:select", 10) { Gate = HealthGate.Database });
        parts.Should().NotContain(static x => x.Series == "aurora.dml-count:insert", "a kind the release did not report is not sampled");
    }

    [Fact]
    public void Before_two_samples_the_waits_are_the_totals_since_the_instance_started()
    {
        AuroraWaitProfile profile = Profile(Wait("IO", "XactSync", 187073, 597041642), Wait("IO", "WALWrite", 52544463, 388850428, 2));

        AuroraWaitsView view = AuroraService.Waits(profile, HealthHistoryView.Empty, Now);

        view.Window.Should().BeNull();
        view.Rows.Select(static x => x.Event.Name).Should().Equal("IO:XactSync", "IO:WALWrite");
        view.Rows.Should().OnlyContain(static x => x.WindowMicroseconds == null);
    }

    [Fact]
    public void The_waits_are_ranked_by_their_time_over_the_window_not_since_the_instance_started()
    {
        AuroraWaitProfile then = Profile(Wait("IO", "XactSync", 1_000, 900_000_000), Wait("Lock", "transactionid", 10, 1_000_000, 2));
        AuroraWaitProfile now = Profile(Wait("IO", "XactSync", 1_100, 900_100_000), Wait("Lock", "transactionid", 500, 61_000_000, 2));

        HealthHistoryView history = new([Sample(Now.AddMinutes(-10), Writer, then, null)]);

        AuroraWaitsView view = AuroraService.Waits(now, history, Now);

        view.Window.Should().Be(TimeSpan.FromMinutes(10));
        view.Rows.Select(static x => x.Event.Name).Should().Equal("Lock:transactionid", "IO:XactSync");
        view.Rows[0].WindowMicroseconds.Should().Be(60_000_000);
        view.Rows[0].WindowWaits.Should().Be(490);
        view.Rows[1].WindowMicroseconds.Should().Be(100_000);
    }

    [Fact]
    public void A_counter_that_went_down_starts_the_window_again_and_another_instances_samples_are_never_subtracted()
    {
        AuroraWaitProfile high = Profile(Wait("IO", "XactSync", 1_000, 900_000_000));
        AuroraWaitProfile low = Profile(Wait("IO", "XactSync", 10, 5_000));

        // A restart: the instance's waits begin again, so the sample before it is no baseline.
        AuroraWaitsView restarted = AuroraService.Waits(low, new HealthHistoryView([Sample(Now.AddMinutes(-10), Writer, high, null)]), Now);
        restarted.Window.Should().BeNull("the counter went down since the last sample: no window yet");

        // A failover to a reader whose counters happen to be higher: the window filters samples to the same instance first.
        PostgresServerFacts reader = Writer with { Instance = "studio-test-instance-2", InRecovery = true };
        HealthHistoryView mixed = new([Sample(Now.AddMinutes(-10), Writer, low, null)]);

        AuroraSamples.SameInstance(mixed, reader).Samples.Should().BeEmpty();
        AuroraSamples.SameInstance(mixed, Writer).Samples.Should().ContainSingle();
    }

    [Fact]
    public void The_commit_latency_over_the_window_is_the_latencys_gain_over_the_commits_gain()
    {
        AuroraDatabaseActivity then = AuroraDatabaseActivity.FromJson(Now, 1_000, "2000000", """{"insert_count": 100, "insert_latency_microsecs": 20000}""");
        AuroraDatabaseActivity now = AuroraDatabaseActivity.FromJson(Now, 1_500, "3500000", """{"insert_count": 300, "insert_latency_microsecs": 80000}""");

        AuroraDatabaseView view = AuroraService.Database(now, new HealthHistoryView([Sample(Now.AddMinutes(-5), Writer, null, then)]), Now);

        view.WindowCommits.Should().Be(500);
        view.WindowCommitMeanMicroseconds.Should().Be(3_000, "1,500,000 µs over 500 commits");
        view.Activity.MeanCommitMicroseconds.Should().BeApproximately(2_333.33, 0.01, "3,500,000 µs over 1,500 commits since the reset");
        view.Window.Should().Be(TimeSpan.FromMinutes(5));

        AuroraDmlRow insert = view.Dml.Single(static x => x.Kind.Kind == "insert");
        insert.WindowCount.Should().Be(200);
        insert.WindowMeanMicroseconds.Should().Be(300, "60,000 µs over 200 inserts");
        view.Dml.Single(static x => x.Kind.Kind == "select").WindowCount.Should().BeNull("the release reported no selects");
    }

    [Fact]
    public void No_commit_in_the_window_is_no_mean_rather_than_a_division_by_nothing()
    {
        AuroraDatabaseActivity then = AuroraDatabaseActivity.FromJson(Now, 1_000, "2000000", null);

        AuroraDatabaseView view = AuroraService.Database(then, new HealthHistoryView([Sample(Now.AddMinutes(-5), Writer, null, then)]), Now);

        view.WindowCommits.Should().Be(0);
        view.WindowCommitMeanMicroseconds.Should().BeNull();
    }

    [Fact]
    public void The_view_shows_nothing_of_Aurora_off_Aurora_and_the_answering_instance_on_it()
    {
        CapacityReading none = new(null, Now, null, false);

        AuroraView self = AuroraService.Build(
            new AuroraFacts(Now, new PostgresServerFacts(Now, 170004, "17.4", PostgresPlatform.SelfHosted), none, null, null, null, []),
            HealthHistoryView.Empty);

        self.IsAurora.Should().BeFalse();
        self.AnsweredBy.Should().BeNull();
        self.Waits.Should().BeNull();

        AuroraView aurora = AuroraService.Build(
            new AuroraFacts(
                Now,
                Writer with { AuroraVersion = "16.4.1" },
                none,
                ServerRead<AuroraReplicaStatus>.Of(AuroraReplicaStatus.FromJson(Now, [])),
                ServerRead<AuroraWaitProfile>.Of(Profile(Wait("IO", "XactSync", 187073, 597041642))),
                new ServerRead<AuroraDatabaseActivity>(null, "The role the studio reads as may not read this (SQLSTATE 42501)."),
                []),
            HealthHistoryView.Empty);

        aurora.IsAurora.Should().BeTrue();
        aurora.AnsweredBy.Should().Be("instance studio-test-instance-1 (the writer), Aurora 16.4.1");
        aurora.Waits!.Value!.Rows.Single().Event.Name.Should().Be("IO:XactSync");
        aurora.Database!.Value.Should().BeNull();
        aurora.Database.Note.Should().Contain("42501");
        aurora.SessionsShown.Should().BeFalse("the sessions are the activity gate's, decided by the service after this");
    }
}
