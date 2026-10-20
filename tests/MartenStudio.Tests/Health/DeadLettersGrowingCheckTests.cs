using JasperFx.Events.Daemon;

using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>D06's judgement: growth across the window, never presence.</summary>
public class DeadLettersGrowingCheckTests
{
    private static HealthSample Earlier(TimeSpan ago, params (string Projection, long Count)[] counts)
    {
        Dictionary<string, double> values = new() { [DeadLettersGrowingCheck.Total] = counts.Sum(static x => x.Count) };

        foreach ((string projection, long count) in counts)
        {
            values[DaemonSeries.DeadLetters(projection)] = count;
        }

        return new HealthSample(Now - ago, values);
    }

    private static HealthVerdict Evaluate(IReadOnlyDictionary<string, long> now, params HealthSample[] earlier) =>
        new DeadLettersGrowingCheck().Evaluate(new DeadLettersFacts(now), HealthEvaluation.At(Now, history: new HealthHistoryView(earlier)));

    [Fact]
    public void Dead_letters_added_across_the_window_are_a_warning_naming_the_projection()
    {
        HealthFinding finding = Evaluate(
                new Dictionary<string, long> { ["ShipmentTracker"] = 7 },
                Earlier(TimeSpan.FromMinutes(5), ("ShipmentTracker", 1)))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D06");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("ShipmentTracker");
        finding.Title.Should().StartWith("6 dead letter(s) added for ShipmentTracker in the last 5 min");
        finding.DocumentTypes.Should().Equal(typeof(DeadLetterEvent));
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();

        finding.Link!.Route.Should().Be("events/dead-letters", "the dead-letter screen, filtered to this projection");
        finding.Link.Query.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string?>("projection", "ShipmentTracker"));
    }

    /// <summary>A finding names a projection, so it carries what that projection writes beside DeadLetterEvent itself.</summary>
    [Fact]
    public void A_finding_carries_the_dead_letter_type_and_what_its_projection_writes()
    {
        DeadLettersFacts facts = new(new Dictionary<string, long> { ["ShipmentTracker"] = 7 })
        {
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase) { ["ShipmentTracker"] = [typeof(Uri)] },
        };

        new DeadLettersGrowingCheck()
            .Evaluate(facts, HealthEvaluation.At(Now, history: new HealthHistoryView([Earlier(TimeSpan.FromMinutes(5), ("ShipmentTracker", 1))])))
            .Findings.Single().DocumentTypes.Should().Equal(typeof(DeadLetterEvent), typeof(Uri));
    }

    /// <summary>The sample store has a dead letter on purpose; one that does not grow is the dead-letter screen's, not an alarm.</summary>
    [Fact]
    public void A_steady_count_is_quiet()
    {
        HealthVerdict verdict = Evaluate(
            new Dictionary<string, long> { ["ShipmentTracker"] = 1 },
            Earlier(TimeSpan.FromMinutes(5), ("ShipmentTracker", 1)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("none added in the last 5 min");
    }

    /// <summary>A projection with no series in an earlier sample in which D06 ran had none then.</summary>
    [Fact]
    public void A_projection_that_had_none_at_the_baseline_grew_from_zero()
    {
        Evaluate(new Dictionary<string, long> { ["DailySales"] = 3 }, Earlier(TimeSpan.FromMinutes(5), ("ShipmentTracker", 1)))
            .Findings.Single().Subject.Should().Be("DailySales");
    }

    [Fact]
    public void Without_a_sample_a_minute_old_it_is_only_watching()
    {
        HealthVerdict verdict = Evaluate(new Dictionary<string, long> { ["ShipmentTracker"] = 9 }, Earlier(TimeSpan.FromSeconds(30), ("ShipmentTracker", 1)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Growth needs two samples a minute apart");
    }

    /// <summary>A sample in which D06 did not run is no baseline: "no series" there means "not read", not "none".</summary>
    [Fact]
    public void A_sample_from_a_sweep_without_D06_is_no_baseline()
    {
        HealthSample withoutD06 = new(Now - TimeSpan.FromMinutes(5), new Dictionary<string, double> { [DaemonSeries.HighestSequence] = 10 });

        Evaluate(new Dictionary<string, long> { ["ShipmentTracker"] = 9 }, withoutD06).Findings.Should().BeEmpty();
    }

    [Fact]
    public void It_samples_the_total_every_time_so_a_later_sweep_can_tell_none_from_not_read()
    {
        new DeadLettersGrowingCheck().Sample(new DeadLettersFacts(new Dictionary<string, long>())).Should()
            .Equal(new HealthSamplePart(DeadLettersGrowingCheck.Total, 0));
    }
}
