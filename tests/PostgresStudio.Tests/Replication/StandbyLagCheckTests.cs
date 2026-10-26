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

    /// <summary>The standby's own unreplayed WAL, one sample <paramref name="minutesAgo" /> before now.</summary>
    private static HealthHistoryView HeldBefore(double bytes, double minutesAgo = 1) =>
        History((Now.AddMinutes(-minutesAgo), [(StandbyLagCheck.ReplayBehindSeries, bytes)]));

    [Fact]
    public void On_a_standby_the_age_of_its_last_replay_is_judged_once_it_has_held_WAL_on_two_samples()
    {
        HealthFinding finding = Evaluate(Standby(13_254_232, TimeSpan.FromMinutes(6)), HeldBefore(9_000_000), null, 0)
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("standby-view");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Measured on", "this standby: what it has received less what it has replayed"));
        finding.Caveat.Should().Contain("two consecutive samples").And.Contain("16.0 MiB or more now");
    }

    [Fact]
    public void On_a_standby_one_samples_record_in_flight_is_never_an_hours_lag()
    {
        // W5-R2-fix 7: the primary was idle for two hours, then one commit arrived - in flight on this one read.
        HealthVerdict once = Evaluate(Standby(4096, TimeSpan.FromHours(2)));

        once.Findings.Should().BeEmpty("an instant's bytes above zero below the floor are not lag until a second sample says so");
        once.Note.Should().Contain("on this sample only");

        Evaluate(Standby(4096, TimeSpan.FromHours(2)), HeldBefore(0), null, 0).Findings.Should().BeEmpty("the sample before held nothing");

        Evaluate(Standby(4096, TimeSpan.FromHours(2)), HeldBefore(2048), null, 0).Findings.Should().ContainSingle()
            .Which.Severity.Should().Be(HealthSeverity.Critical, "held on two consecutive samples, an hour behind");
    }

    [Fact]
    public void On_a_standby_a_segments_worth_unreplayed_is_lag_at_once()
    {
        Evaluate(Standby(StandbyLagCheck.StandbyTimeFloorBytes, TimeSpan.FromMinutes(6))).Findings.Should().ContainSingle()
            .Which.Severity.Should().Be(HealthSeverity.Warning, "16 MiB or more is judged on one sample");
        Evaluate(Standby(StandbyLagCheck.StandbyTimeFloorBytes - 1, TimeSpan.FromMinutes(6))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_deliberately_delayed_standby_is_judged_only_beyond_its_delay()
    {
        TimeSpan delay = TimeSpan.FromHours(1);

        // 65 minutes since the last replayed commit on a standby that waits an hour on purpose: 5 minutes beyond it.
        HealthFinding finding = Evaluate(Standby(GiB * 4, TimeSpan.FromMinutes(65)), HeldBefore(GiB), delay, 0)
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "five minutes beyond the delay, and the WAL the delay holds is not judged");
        finding.Evidence.Should().Contain(new HealthEvidence("Furthest behind in time", "5 min"));
        finding.Evidence.Should().Contain(x => x.Label == "recovery_min_apply_delay" && x.Value.StartsWith("1 h 0 min: only the time beyond it is judged", StringComparison.Ordinal));

        HealthVerdict within = Evaluate(Standby(GiB * 4, TimeSpan.FromMinutes(59)), HeldBefore(GiB), delay, 0);
        within.Findings.Should().BeEmpty("within its own delay, four GiB held is the delay's by design");
        within.Note.Should().Contain("beyond its 1 h 0 min recovery_min_apply_delay");
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
    public void It_samples_a_standbys_own_unreplayed_WAL_and_nothing_on_a_primary()
    {
        Check.Sample(new StandbyLagFacts(ServerOf(), Standby(4096, TimeSpan.FromMinutes(1)), null, Grants(), TimeSpan.Zero))
            .Select(static x => (x.Series, x.Value)).Should().Equal((StandbyLagCheck.ReplayBehindSeries, 4096d));
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
