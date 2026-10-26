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

    private static HealthVerdict Evaluate(ReplicationRole role, params StandbyRow[] standbys)
    {
        StandbySnapshot? snapshot = role.InRecovery ? null : new StandbySnapshot(standbys.Length, standbys, Now);
        HealthVerdict verdict = Check.Evaluate(new StandbyLagFacts(ServerOf(), role, snapshot, Grants()), At());

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

    [Fact]
    public void On_a_standby_the_bytes_it_holds_and_the_age_of_its_last_replay_are_judged()
    {
        HealthFinding finding = Evaluate(Standby(13_254_232, TimeSpan.FromMinutes(6))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("standby-view");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Measured on", "this standby: what it has received less what it has replayed"));
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
