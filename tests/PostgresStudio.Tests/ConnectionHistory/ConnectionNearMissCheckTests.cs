using PostgresStudio.Services;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Settings;
using PostgresStudio.Tests.Server;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.ConnectionHistory;

/// <summary>
/// C15 "Connections came close to the limit" (1.0.1, issue #47) over stated history and no Postgres: Warning at 80 % and Critical
/// at 95 % of the client limit <i>now</i>, a resize clearing it, no finding for a limit of zero, C03's remedy, the finding at C03's
/// gate with the applications' peaks behind the Activity gate - for the studio's visitors and for the diagnostics API.
/// </summary>
public class ConnectionNearMissCheckTests
{
    private const string Application = "SENTINEL-APP-billing";

    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static PostgresConnectionSample Sample(int minutesAgo, long connections, int limit, string instance = "a", params (string? Application, int Total)[] applications) => new()
    {
        At = Now.AddMinutes(-minutesAgo),
        Server = "db:5432",
        Instance = instance,
        Connections = connections,
        ClientLimit = limit,
        ByApplication = [.. applications.Select(static x => new PostgresConnectionCounts { ApplicationName = x.Application, Total = x.Total })],
    };

    /// <summary>Facts for a peak of <paramref name="peak" /> connections against <paramref name="limit" />, the limit unchanged at the latest sample.</summary>
    private static ConnectionNearMissFacts Facts(long peak, int limit = 100, int limitNow = 0, PostgresPlatform? platform = null, ServerConnectionLimits? limits = null, CapacityReading? capacity = null) => Facts(
        [
            Sample(180, 12, limit),
            Sample(120, peak, limit, "a", (Application, (int) peak - 5), ("reports", 3), (null, 2)),
            Sample(60, 20, limitNow == 0 ? limit : limitNow, "b"),
        ],
        platform,
        limits,
        capacity);

    private static ConnectionNearMissFacts Facts(PostgresConnectionSample[] samples, PostgresPlatform? platform = null, ServerConnectionLimits? limits = null, CapacityReading? capacity = null) => new(
        ConnectionHistoryView.Of(samples, names: true),
        "db:5432",
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7),
        limits,
        platform ?? PostgresPlatform.SelfHosted,
        capacity);

    private static HealthVerdict Evaluate(ConnectionNearMissFacts facts) =>
        new ConnectionNearMissCheck().Evaluate(facts, HealthEvaluation.At(Now)).ShouldBeWellFormedFor<ConnectionNearMissCheck>();

    // ----------------------------------------------------------------------------------------------
    // What it is
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void It_is_the_connections_check_C15_at_C03s_gate_with_no_variants_and_a_heading_in_the_docs()
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(ConnectionNearMissCheck));
        HealthCheckInfo c03 = HealthCheckRegistry.Describe(typeof(ServerConnectionHeadroomCheck));

        info.Code.Should().Be("C15");
        info.Area.Should().Be(HealthArea.Connections);
        info.Tier.Should().Be(HealthCostTier.Catalog);
        info.Gate.Should().Be(c03.Gate, "the finding and its numbers are at C03's gate");
        info.Gate.Should().NotBe(HealthGate.Activity, "only the applications' evidence is Activity-grade");
        info.Variants.Should().BeEmpty();
        info.DocsHeading.Should().Be("## C15 Connections came close to the limit");
    }

    [Fact]
    public void It_judges_with_C03s_lines()
    {
        ConnectionThresholds.ServerUsedWarning.Should().Be(0.8);
        ConnectionThresholds.ServerUsedCritical.Should().Be(0.95);
        ConnectionThresholds.NearMissWindow.Should().Be(TimeSpan.FromHours(24));
    }

    // ----------------------------------------------------------------------------------------------
    // The lines, exactly
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(79, false, false)]
    [InlineData(80, true, false)]
    [InlineData(94, true, false)]
    [InlineData(95, true, true)]
    [InlineData(100, true, true)]
    [InlineData(112, true, true)]
    public void A_peak_of_79_percent_is_quiet_80_is_a_Warning_and_95_is_Critical(long peak, bool fires, bool critical)
    {
        HealthVerdict verdict = Evaluate(Facts(peak));

        (verdict.Findings.Count == 1).Should().Be(fires);

        if (fires)
        {
            verdict.Findings[0].Severity.Should().Be(critical ? HealthSeverity.Critical : HealthSeverity.Warning);
            verdict.Findings[0].Code.Should().Be("C15");
        }
        else
        {
            verdict.Note.Should().Contain("The peak of the server").And.Contain("under the 80%");
        }
    }

    [Fact]
    public void A_resize_after_the_peak_clears_it_because_the_peak_is_judged_against_the_limit_now()
    {
        // 90 of 100 at the peak, then the cluster's capacity was raised: the latest sample says 200.
        Evaluate(Facts(90, limit: 100, limitNow: 200)).Findings.Should().BeEmpty();
        Evaluate(Facts(90, limit: 100, limitNow: 100)).Findings.Should().ContainSingle();
    }

    [Fact]
    public void A_smaller_limit_now_makes_an_earlier_peak_a_finding_and_shows_both_limits()
    {
        HealthFinding finding = Evaluate(Facts(70, limit: 100, limitNow: 80)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "70 of the 80 there are now is 87.5 %");
        finding.Evidence.Should().Contain(static x => x.Label == "Client limit now" && x.Value == "80")
            .And.Contain(static x => x.Label == "Client limit at the peak" && x.Value == "100");
    }

    [Fact]
    public void A_limit_of_zero_is_unknown_and_no_finding()
    {
        HealthVerdict verdict = Evaluate(Facts([Sample(60, 500, 100), Sample(1, 5, 0)]));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("did not record the server's client limit");
    }

    [Fact]
    public void Without_a_sample_in_the_window_it_is_quiet()
    {
        HealthVerdict verdict = Evaluate(new ConnectionNearMissFacts(null, "db:5432", TimeSpan.FromHours(24), TimeSpan.FromDays(7), null, PostgresPlatform.SelfHosted, null));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("no sample").And.Contain("24 hours");
    }

    // ----------------------------------------------------------------------------------------------
    // The finding
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_finding_says_when_the_peak_was_against_what_limit_and_where_the_whole_window_is()
    {
        HealthFinding finding = Evaluate(Facts(90)).Findings.Single();

        finding.Title.Should().Contain("90 of 100").And.Contain("90%").And.Contain("24 hours");
        finding.WhyItMatters.Should().Contain("2026-11-02 04:00:00Z").And.Contain("within the last 24 hours").And.Contain("client limit of 100 connections now");
        finding.Caveat.Should().Contain("Database › Sessions › Holders").And.Contain("7 days").And.Contain("a resize clears");
        finding.Link.Should().Be(SessionEvidence.HoldersLink);
        finding.Evidence.Select(static x => x.Label).Should().Contain(["Peak", "Client limit now", "Samples", "Instance changes"]);
        finding.Evidence.Should().Contain(static x => x.Label == "Instance changes" && x.Value == "1");
        finding.Evidence.Should().NotContain(static x => x.Label == "Client limit at the peak", "the limit did not change");
    }

    [Fact]
    public void A_critical_peak_has_its_own_title()
    {
        Evaluate(Facts(97)).Findings.Single().Title.Should().StartWith("Connections almost reached the client limit");
        Evaluate(Facts(85)).Findings.Single().Title.Should().StartWith("Connections came within 15 of the client limit");
    }

    [Fact]
    public void The_remedy_is_C03s_server_remedy_not_a_copy_of_it()
    {
        ServerConnectionLimits limits = new(0, 0, 103, 3, 0, 170000);

        HealthFinding plain = Evaluate(Facts(90, limits: limits)).Findings.Single();
        plain.Remedy.Should().Be(ServerConnectionHeadroomCheck.ServerRemedy(PostgresPlatform.SelfHosted, null, limits, StudioText.Marten));

        PostgresPlatform aurora = ServerTestFacts.Platform(PostgresPlatformKind.AmazonAurora);
        CapacityReading reading = new(PostgresComputeCapacity.AuroraServerlessV2(4, 0.5, 16, Now), Now, null, true);
        HealthFinding onAurora = Evaluate(Facts(90, platform: aurora, limits: limits, capacity: reading)).Findings.Single();

        onAurora.Remedy.Should().Be(ServerConnectionHeadroomCheck.AuroraRemedy(reading, limits));
        onAurora.Remedy!.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
        onAurora.Remedy.Explanation.Should().Contain("maximum capacity");
    }

    [Fact]
    public void The_remedy_is_the_platform_neutral_one_when_the_servers_settings_could_not_be_read()
    {
        PostgresPlatform aurora = ServerTestFacts.Platform(PostgresPlatformKind.AmazonAurora);

        Evaluate(Facts(90, platform: aurora, limits: null)).Findings.Single().Remedy!.Kind.Should().NotBe(HealthRemedyKind.ManagedPlatform);
    }

    // ----------------------------------------------------------------------------------------------
    // The gates
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Which_applications_held_the_most_is_Activity_grade_evidence_and_the_rest_is_not()
    {
        HealthFinding finding = Evaluate(Facts(90)).Findings.Single();

        finding.Evidence.Where(static x => x.Gate == HealthGate.Activity).Select(static x => x.Label)
            .Should().Equal("Application 1 (highest peak)", "Application 2", "Application 3");
        finding.Evidence.Where(static x => x.Gate != HealthGate.Activity).Select(static x => x.Label)
            .Should().Contain(["Peak", "Client limit now", "Samples", "Instance changes"]);
        finding.Evidence.Where(static x => x.Label != "Application 1 (highest peak)" && x.Label != "Application 2" && x.Label != "Application 3")
            .Should().OnlyContain(static x => !x.Value.Contains(Application, StringComparison.Ordinal));
    }

    private static (IReadOnlyList<PostgresFinding> Findings, int Withheld) Build(bool includeActivity)
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(ConnectionNearMissCheck));
        HealthFinding[] findings =
        [
            .. new ConnectionNearMissCheck().Evaluate(Facts(90), HealthEvaluation.At(Now)).Findings
                .Select(x => x with { Area = info.Area, Gate = info.Gate, CheckTitle = info.Title, MeasuredAt = Now }),
        ];

        (IReadOnlyList<PostgresFinding> described, _, int withheld) = DiagnosticsSnapshotBuilder.Findings(
            Sweep(Result(info, findings: findings)).Results, Gate(includeActivity), NoStudioRuntime.Instance, "store", new StudioScope("main", "db1", null));

        return (described, withheld);
    }

    [Fact]
    public void The_API_returns_the_finding_without_IncludeActivity_with_the_applications_withheld_and_names_them_with_it()
    {
        (IReadOnlyList<PostgresFinding> without, int withheldWithout) = Build(includeActivity: false);
        (IReadOnlyList<PostgresFinding> with, int withheldWith) = Build(includeActivity: true);

        withheldWithout.Should().Be(0, "the finding is not Activity-grade; its application evidence is");
        without.Should().ContainSingle().Which.Code.Should().Be("C15");
        without[0].Evidence.Should().Contain(new KeyValuePair<string, string>("Application 1 (highest peak)", HostGate.WithheldValue));
        System.Text.Json.JsonSerializer.Serialize(without).Should().NotContain(Application);
        without[0].Evidence.Should().Contain(static x => x.Key == "Peak" && x.Value.StartsWith("90 of 100", StringComparison.Ordinal));

        withheldWith.Should().Be(0);
        System.Text.Json.JsonSerializer.Serialize(with).Should().Contain(Application);
    }

    [Fact]
    public void It_can_be_accepted_like_any_check()
    {
        // Acceptance is by code, applied by the sweep to a finding of any check (AcceptedChecks): C15 asks for nothing special.
        HealthFinding finding = Evaluate(Facts(90)).Findings.Single();

        finding.Key.Should().Be("C15");
        finding.Accepted.Should().BeFalse();
        (finding with { Accepted = true, AcceptedReason = "We run close to the limit on purpose." }).Accepted.Should().BeTrue();
    }
}
