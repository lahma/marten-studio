using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>K02's judgement, over stated facts.</summary>
public class ExtendedProgressionTrackingCheckTests
{
    private static readonly HealthEvaluation Evaluation = HealthEvaluation.At(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));

    private static HealthVerdict Evaluate(bool hasAsyncWork, bool enabled, int? shards = 2) =>
        new ExtendedProgressionTrackingCheck().Evaluate(new AsyncTrackingFacts(hasAsyncWork, shards, enabled), Evaluation);

    [Fact]
    public void Async_work_with_the_tracking_off_is_one_info_finding()
    {
        HealthFinding finding = Evaluate(hasAsyncWork: true, enabled: false).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("K02");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(new HealthEvidence("Events.EnableExtendedProgressionTracking", "false"));
        finding.Evidence.Should().Contain(new HealthEvidence("Async projections and subscriptions", "2 shards"));
        finding.Remedy!.Code.Should().Be("opts.Events.EnableExtendedProgressionTracking = true;");
    }

    [Fact]
    public void A_shard_count_the_options_could_not_give_is_said_as_at_least_one()
    {
        Evaluate(hasAsyncWork: true, enabled: false, shards: null).Findings.Single().Evidence
            .Should().Contain(new HealthEvidence("Async projections and subscriptions", "at least one"));
    }

    [Fact]
    public void The_tracking_on_is_quiet()
    {
        Evaluate(hasAsyncWork: true, enabled: true).Findings.Should().BeEmpty();
    }

    /// <summary>No shard, nothing to track: an Info that told such a store to turn it on would be crying wolf.</summary>
    [Fact]
    public void A_store_with_nothing_for_the_daemon_to_run_is_quiet()
    {
        HealthVerdict verdict = Evaluate(hasAsyncWork: false, enabled: false, shards: 0);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("no async projection");
    }
}
