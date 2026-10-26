using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>R03 at every threshold edge, from a primary's view, a standby's own and a paused replay.</summary>
public class StandbyLagCheckTests
{
    private const long GiB = 1L << 30;

    private static readonly StandbyLagCheck Check = new();
    private static readonly HealthCheckInfo Info = HealthCheckRegistry.Describe(typeof(StandbyLagCheck));

    private static ReplicationRole Primary { get; } = new(false, null, null, null, null, null, null, Now);

    private static ReplicationRole Standby(long? behind, TimeSpan? age, string? pause = "not paused") =>
        new(true, "0/5000000", "0/4000000", behind, Now - age, age, pause, Now);

    private static StandbyRow Row(string name, long? behind, TimeSpan? replayLag, bool readable = true) => new(
        name, "10.0.0.3", "replicator", readable ? "streaming" : null, readable ? "async" : null,
        readable ? behind : null, readable ? behind : null, null, null, readable ? replayLag : null);

    private static HealthVerdict Evaluate(ReplicationRole role, params StandbyRow[] standbys) =>
        Evaluate(role, null, null, 0, standbys);

    private static HealthVerdict Evaluate(ReplicationRole role, HealthHistoryView? history, TimeSpan? delay, long otherDatabases, params StandbyRow[] standbys)
    {
        StandbySnapshot? snapshot = role.InRecovery ? null : new StandbySnapshot(standbys.Length, standbys, Now, otherDatabases);
        HealthVerdict verdict = Check.Evaluate(new StandbyLagFacts(ServerOf(), role, snapshot, Grants(), role.InRecovery ? delay ?? TimeSpan.Zero : null), At(history));

        foreach (HealthFinding finding in verdict.Findings)
        {
            HealthCheckRegistry.FindingProblems(Info, finding).Should().BeEmpty();
        }

        return verdict;
    }

    [Fact]
    public void R03_lists_its_three_variants_at_the_database_gate()
    {
        Info.Gate.Should().Be(HealthGate.Database);
        Info.Variants.Should().Equal("primary-view", "standby-view", "paused");
    }

    [Theory]
    [InlineData(GiB - 1, 0, null)]
    [InlineData(GiB, 0, "Warning")]
    [InlineData(0, 299, null)]
    [InlineData(0, 300, "Warning")]
    [InlineData(0, 3599, "Warning")]
    [InlineData(0, 3600, "Critical")]
    [InlineData(5 * GiB, 3600, "Critical")]
    public void The_thresholds_are_five_minutes_or_a_gibibyte_and_an_hour(long bytes, int seconds, string? severity)
    {
        string? judged = StandbyLagCheck.Judge(new StandbyLagReading("r", bytes, TimeSpan.FromSeconds(seconds)))?.ToString();

        judged.Should().Be(severity);
    }

    [Fact]
    public void Nothing_measured_is_nothing_judged()
    {
        StandbyLagCheck.Judge(new StandbyLagReading("r", null, null)).Should().BeNull();
    }

    [Fact]
    public void No_standby_is_quiet()
    {
        Evaluate(Primary).Note.Should().Be("No standby streams from this server.");
    }

    [Fact]
    public void A_standby_an_hour_behind_by_replay_lag_is_critical_from_the_primary_and_named_only_behind_the_activity_gate()
    {
        HealthFinding finding = Evaluate(Primary, Row("reporting", 13_254_232, TimeSpan.FromMinutes(61)), Row("dr", 0, null))
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("primary-view");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().BeNull("a subject would be a standby's application name, in every visitor's mute key");
        finding.Title.Should().Be("A standby is 1 h 1 min behind");
        finding.Evidence.Should().Contain(new HealthEvidence("Behind", "1 of 2 replicas judged"));
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in time", "1 h 1 min"));

        HealthEvidence named = finding.Evidence.Single(static x => x.Label.StartsWith("Replica", StringComparison.Ordinal));
        named.Gate.Should().Be(HealthGate.Activity);
        named.Value.Should().Contain("'reporting'").And.Contain("from 10.0.0.3").And.Contain("role replicator");
        finding.Evidence.Where(static x => x.Gate != HealthGate.Activity).Should().NotContain(static x => x.Value.Contains("reporting", StringComparison.Ordinal));
    }

    [Fact]
    public void A_gibibyte_behind_with_no_replay_lag_is_a_warning()
    {
        Evaluate(Primary, Row("r", GiB, null)).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    [Fact]
    public void Caught_up_standbys_are_quiet_and_say_how_far_the_furthest_is()
    {
        HealthVerdict verdict = Evaluate(Primary, Row("a", 1024, TimeSpan.FromMilliseconds(40)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().StartWith("1 standby streams from this server, none of them behind");
    }

    [Fact]
    public void Standbys_this_role_cannot_read_are_not_checked_and_name_the_grant()
    {
        HealthVerdict verdict = Evaluate(Primary, Row("a", 1024, null), Row("b", null, null, readable: false));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("1 other standby: Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role app.");

        StandbyLagCheck.NotChecked(ServerOf(), Grants(), 2).Should().Be(
            PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, PostgresPlatform.SelfHosted, "app")
            + " 2 standbys stream from this server, and without it pg_stat_replication shows none of their positions or lag. A data source "
            + "that reaches a standby itself - a multi-host data source - is measured there instead, with no grant: what the standby has "
            + "received less what it has replayed.");
    }

    // The standby every test below reads: received up to 0/5000000, replayed up to 0/4000000 (Standby()).
    private const ulong Replayed = 0x4000000;
    private const ulong Before = Replayed - 0x100000;
    private const ulong After = Replayed + 0x100000;

    /// <summary>The advisor's samples of what the standby had received, each (minutes before now, position).</summary>
    private static HealthHistoryView Received(params (double MinutesAgo, ulong Position)[] points) =>
        History([.. points.Select(static x => (Now.AddMinutes(-x.MinutesAgo), new[] { (StandbyLagCheck.ReceivedSeries, (double) x.Position) }))]);

    /// <summary>A sample every <paramref name="every" /> minutes from <paramref name="from" /> to <paramref name="to" /> minutes ago.</summary>
    private static IEnumerable<(double MinutesAgo, ulong Position)> Every(double every, double from, double to, ulong position)
    {
        for (double minutes = from; minutes >= to; minutes -= every)
        {
            yield return (minutes, position);
        }
    }

    [Fact]
    public void On_a_standby_the_time_is_how_long_WAL_it_received_has_gone_unreplayed()
    {
        // Replay reached what was received ten minutes ago, and not what was received six minutes ago: six minutes behind.
        HealthFinding finding = Evaluate(Standby(13_254_232, TimeSpan.FromMinutes(1)), Received((10, Before), (6, After), (3, After), (1, After)), null, 0)
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("standby-view");
        finding.Severity.Should().Be(HealthSeverity.Warning, "the last commit is a minute old; what matters is the WAL received six minutes ago");
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in time", "6 min"));
        finding.Evidence.Should().Contain(new HealthEvidence("Unreplayed since", "2026-10-26 11:54:00Z"));
        finding.Caveat.Should().Contain("the age of the oldest sample whose position replay has still not reached");

        Evaluate(Standby(13_254_232, TimeSpan.FromHours(2)), Received((70, Before), (65, After), (1, After)), null, 0).Findings.Should().ContainSingle()
            .Which.Severity.Should().Be(HealthSeverity.Critical, "WAL received 65 minutes ago is still not replayed");
    }

    [Fact]
    public void A_stale_last_commit_on_a_standby_that_keeps_up_is_never_lag()
    {
        // W5-R2-fix2 3: a freeze VACUUM writes WAL for hours and commits nothing; the standby holds an instant's records in
        // flight at every sample, and its last replayed commit is two hours old. Replay keeps passing what it received.
        HealthVerdict verdict = Evaluate(Standby(4096, TimeSpan.FromHours(2)), Received([.. Every(1, 60, 1, Before)]), null, 0);

        verdict.Findings.Should().BeEmpty("replay has reached everything it had received a minute ago");
        verdict.Note.Should().Be("This server is a standby, 4.0 KiB behind what it has received; replay has reached everything it had received at the "
            + "advisor's last sample, 1 min ago, so it is not behind in time.");
    }

    [Fact]
    public void With_no_earlier_sample_the_time_is_not_judged_yet_and_the_bytes_still_are()
    {
        HealthVerdict first = Evaluate(Standby(4096, TimeSpan.FromHours(2)));

        first.Findings.Should().BeEmpty("an idle primary's two-hour-old commit is not two hours of lag");
        first.Note.Should().Contain("the time is not judged yet");

        Evaluate(Standby(GiB, TimeSpan.FromHours(2))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning, "a gibibyte held is judged at once");
    }

    [Fact]
    public void A_standby_stuck_since_before_the_advisor_watched_fires_on_the_lower_bound_once_it_reaches_a_threshold()
    {
        // Replay has reached nothing the samples saw received: the WAL received when they begin is unreplayed, at least.
        HealthFinding finding = Evaluate(Standby(13_254_232, TimeSpan.FromHours(5)), Received((7, After), (3, After)), null, 0)
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "at least seven minutes, which is all the samples can say");
        finding.Evidence.Should().Contain(x => x.Label.StartsWith("Replica", StringComparison.Ordinal) && x.Value.Contains("at least", StringComparison.Ordinal));

        HealthVerdict young = Evaluate(Standby(13_254_232, TimeSpan.FromHours(5)), Received((3, After), (1, After)), null, 0);
        young.Findings.Should().BeEmpty("three minutes of samples bound it below the threshold, and say nothing more");
        young.Note.Should().Contain("replay has reached nothing the advisor's samples saw it receive over the 3 min they cover, so the time is not judged yet.");
    }

    [Fact]
    public void A_delayed_standby_after_a_quiet_night_is_quiet()
    {
        // W5-R2-fix2 1: an hour's delay; nothing committed overnight, and the first commit came 30 minutes ago - so the last
        // replayed commit is 8.5 hours old and replay waits, correctly, before the commit that arrived half an hour ago.
        TimeSpan delay = TimeSpan.FromHours(1);
        HealthHistoryView night = Received([.. Every(5, 60, 35, Before), .. Every(5, 30, 1, After)]);

        HealthVerdict verdict = Evaluate(Standby(GiB * 4, TimeSpan.FromHours(8.5)), night, delay, 0);

        verdict.Findings.Should().BeEmpty("half an hour of unreplayed WAL is inside the hour the standby waits on purpose, whatever the last commit's age");
        verdict.Note.Should().Contain("WAL it received 30 min ago is not replayed yet, 0 s beyond its 1 h 0 min recovery_min_apply_delay.");
    }

    [Fact]
    public void A_delayed_standby_whose_replay_is_stuck_fires_on_the_time_beyond_its_delay()
    {
        TimeSpan delay = TimeSpan.FromHours(1);

        // Replay reached what was received 80 minutes ago and nothing since: WAL received 70 minutes ago is unreplayed.
        HealthFinding finding = Evaluate(Standby(GiB * 4, TimeSpan.FromMinutes(75)), Received([(80, Before), .. Every(5, 70, 1, After)]), delay, 0)
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "ten minutes beyond the delay, and the WAL the delay holds is not judged");
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in time", "10 min"));
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in WAL", "not measured"));
        finding.Evidence.Should().Contain(x => x.Label == "recovery_min_apply_delay" && x.Value.StartsWith("1 h 0 min: only the time beyond it is judged", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delayed_standby_is_not_judged_until_the_samples_reach_back_past_its_delay()
    {
        TimeSpan delay = TimeSpan.FromHours(1);

        HealthVerdict verdict = Evaluate(Standby(GiB * 4, TimeSpan.FromHours(3)), Received([.. Every(5, 40, 1, After)]), delay, 0);

        verdict.Findings.Should().BeEmpty("forty minutes of samples cannot tell a healthy hour's delay from a stuck replay");
        verdict.Note.Should().Contain("so the time is not judged yet: with a recovery_min_apply_delay of 1 h 0 min, it is judged once the samples reach back more than 1 h 5 min");
    }

    [Fact]
    public void Logical_senders_are_not_standbys_and_another_databases_are_only_counted()
    {
        StandbyRow subscription = new("my_subscription", "10.0.0.9", "app", "streaming", null, GiB * 5, GiB * 5, null, null, TimeSpan.FromHours(3), Physical: false);

        HealthVerdict verdict = Evaluate(Primary, null, null, 2, Row("dr", 0, null), subscription);

        verdict.Findings.Should().BeEmpty("a logical sender of this database is not a standby, however far behind");
        verdict.Note.Should().Contain("Not standbys, and not judged: 1 logical sender of this database and 2 logical senders of other databases on this server, never named.");

        Evaluate(Primary, null, null, 3).Note.Should().Be(
            "No standby streams from this server. Not standbys, and not judged: 3 logical senders of other databases on this server, never named.");
    }

    [Fact]
    public void It_samples_what_a_standby_has_received_and_nothing_on_a_primary()
    {
        Check.Sample(new StandbyLagFacts(ServerOf(), Standby(4096, TimeSpan.FromMinutes(1)), null, Grants(), TimeSpan.Zero))
            .Select(static x => (x.Series, x.Value)).Should().Equal((StandbyLagCheck.ReceivedSeries, (double) 0x5000000));
        Check.Sample(new StandbyLagFacts(ServerOf(), Primary, new StandbySnapshot(0, [], Now), Grants())).Should().BeEmpty();
    }

    [Fact]
    public void On_a_standby_an_idle_primary_ages_the_last_replay_with_no_lag_and_is_quiet()
    {
        HealthVerdict verdict = Evaluate(Standby(0, TimeSpan.FromHours(3)));

        verdict.Findings.Should().BeEmpty("with nothing received and not replayed, the age is an idle primary's, not lag");
        verdict.Note.Should().Be("This server is a standby, 0 B behind what it has received.");
    }

    [Fact]
    public void A_paused_replay_is_its_own_warning()
    {
        HealthFinding finding = Evaluate(Standby(GiB * 3, TimeSpan.FromHours(2), "paused")).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("paused");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("Replay is paused on this standby, 3.0 GiB behind");
        finding.WhyItMatters.Should().Contain("pg_wal_replay_resume()");
    }

    [Fact]
    public void A_standby_that_streams_nothing_is_not_measured()
    {
        Evaluate(Standby(null, TimeSpan.FromHours(1))).Note.Should().Contain("receives nothing by streaming");
    }

    [Fact]
    public void The_evaluator_judges_any_list_of_readings_alike()
    {
        // AUR's path: instances read elsewhere, judged by the same thresholds.
        HealthFinding? finding = StandbyLagCheck.Finding(
            "primary-view",
            [new StandbyLagReading("instance-2", null, TimeSpan.FromMinutes(7)), new StandbyLagReading("instance-3", null, TimeSpan.FromSeconds(1))],
            2,
            "why",
            "caveat",
            []);

        finding!.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Behind", "1 of 2 replicas judged"));

        StandbyLagCheck.Finding("primary-view", [new StandbyLagReading("x", 10, TimeSpan.FromSeconds(1))], 1, "w", "c", []).Should().BeNull();
    }
}
