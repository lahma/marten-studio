using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>K03's judgement, over stated facts.</summary>
public class AdvancedAsyncTrackingCheckTests
{
    private static readonly HealthEvaluation Evaluation = HealthEvaluation.At(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    private static HealthVerdict Evaluate(bool? hasAsyncWork, bool enabled) =>
        new AdvancedAsyncTrackingCheck().Evaluate(new AsyncTrackingFacts(hasAsyncWork, 1, enabled), Evaluation);

    [Fact]
    public void Async_work_with_the_tracking_off_is_one_info_finding()
    {
        HealthFinding finding = Evaluate(hasAsyncWork: true, enabled: false).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("K03");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.WhyItMatters.Should().Contain("mt_high_water_skips");
        finding.Evidence.Should().Contain(new HealthEvidence("Async projections and subscriptions", "1 shard"));
        finding.Remedy!.Code.Should().Be("opts.Events.EnableAdvancedAsyncTracking = true;");
    }

    [Fact]
    public void The_Marten_9_default_is_quiet()
    {
        Evaluate(hasAsyncWork: true, enabled: true).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_store_with_nothing_for_the_daemon_to_run_is_quiet()
    {
        Evaluate(hasAsyncWork: false, enabled: false).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_store_whose_projections_could_not_be_read_is_quiet_and_says_so()
    {
        HealthVerdict verdict = Evaluate(hasAsyncWork: null, enabled: false);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be(AsyncTrackingFacts.UnreadableNote);
    }
}
