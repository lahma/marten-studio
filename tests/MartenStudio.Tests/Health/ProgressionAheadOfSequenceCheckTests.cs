using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Health;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D09's judgement, over stated facts: the sequence read as Marten reads it, each of the three kinds on its own, and the
/// remedy each one gets.
/// </summary>
public class ProgressionAheadOfSequenceCheckTests
{
    /// <summary>A store whose sequence has handed out <paramref name="allocated" />, with the mark and the highest event given.</summary>
    private static HighWaterReading Reading(long allocated, long? mark, long? highest) => new(
        Now,
        HighestSequence: allocated,
        Mark: mark,
        MarkUpdated: Now,
        StuckMark: null,
        StuckSince: null,
        FenceMark: null,
        FenceUpdated: null,
        LatestWrite: Now,
        NextCommitted: null)
    {
        AllocatedHigh = allocated,
        HighestCommitted = highest,
    };

    private static DaemonShardRow Shard(string name, long position) => new(name, position, Now);

    private static HealthVerdict Evaluate(HighWaterReading reading, DaemonCheckConfiguration? configuration = null, params DaemonShardRow[] shards) =>
        new ProgressionAheadOfSequenceCheck().Evaluate(
            new ProgressionAheadFacts(configuration ?? DaemonConfiguration(), reading, shards),
            HealthEvaluation.At(Now));

    [Fact]
    public void A_healthy_store_whose_sequence_is_ahead_of_everything_is_quiet()
    {
        Evaluate(Reading(120, mark: 110, highest: 120), null, Shard("DailySales:All", 110), Shard("ShipmentTracker:All", 100))
            .Findings.Should().BeEmpty();

        Evaluate(Reading(0, mark: null, highest: null)).Findings.Should().BeEmpty("a store nothing has been appended to");
    }

    /// <summary>
    /// <c>RESTART WITH 51</c> above a mark of 50: Marten's reading is 50 handed out, so nothing is ahead. The projections
    /// screen's <c>highest_sequence</c> would read 0 for the never-called sequence and call the mark ahead.
    /// </summary>
    [Fact]
    public void A_sequence_restarted_above_the_mark_is_read_as_Marten_reads_it_and_is_quiet()
    {
        HighWaterReading restarted = Reading(50, mark: 50, highest: 50) with { HighestSequence = 0 };

        Evaluate(restarted, null, Shard("DailySales:All", 50)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_mark_above_what_the_sequence_has_handed_out_is_a_critical_naming_the_shards_ahead_with_it()
    {
        DaemonCheckConfiguration configuration = DaemonConfiguration() with
        {
            HiddenProjections = new HashSet<string>(["ShipmentTracker"], StringComparer.OrdinalIgnoreCase),
        };

        HealthFinding finding = Evaluate(Reading(0, mark: 50, highest: null), configuration, Shard("DailySales:All", 50), Shard("ShipmentTracker:All", 50))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D09");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("HighWaterMark");
        finding.Title.Should().StartWith("The high-water mark (50) is ahead of the event sequence, which hands out 1 next");
        finding.Evidence.Should().Contain(new HealthEvidence("Shards ahead of the sequence", "DailySales:All at 50"),
            "a projection of a hidden type is never named");
        finding.Evidence.Should().Contain(new HealthEvidence("Highest committed seq_id", "none: mt_events holds no event"));
        finding.Remedy!.Code.Should().Contain("await store.Advanced.TryCorrectProgressInDatabaseAsync(cancellationToken);");
        finding.Remedy.Explanation.Should().Contain("only while the mark is above it");
        finding.WhyItMatters.Should().Contain(ProgressionAheadOfSequenceCheck.Causes);
        finding.Link!.Route.Should().Be("projections");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_shard_ahead_of_a_mark_that_is_not_is_a_critical_per_shard_and_never_names_a_hidden_projection()
    {
        DaemonCheckConfiguration configuration = DaemonConfiguration() with
        {
            HiddenProjections = new HashSet<string>(["ShipmentTracker"], StringComparer.OrdinalIgnoreCase),
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase)
            {
                ["DailySales"] = [typeof(HealthOrderDoc)],
            },
        };

        HealthFinding finding = Evaluate(Reading(20, mark: 20, highest: 20), configuration, Shard("DailySales:All", 40), Shard("ShipmentTracker:All", 40))
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("DailySales:All");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.DocumentTypes.Should().Equal([typeof(HealthOrderDoc)], "a finding naming a projection carries what it writes");
        finding.Title.Should().StartWith("Shard DailySales:All (40) is ahead of the event sequence, which hands out 21 next");
        finding.Remedy!.Explanation.Should().StartWith("Correct progression does nothing here",
            "Marten corrects progression only while the mark itself is ahead");
    }

    [Fact]
    public void An_unregistered_progression_row_is_not_judged()
    {
        Evaluate(Reading(20, mark: 20, highest: 20), null, Shard("Retired:All", 40)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_sequence_at_or_below_the_highest_committed_event_is_a_critical_whose_repair_is_said_in_words()
    {
        HealthFinding finding = Evaluate(Reading(0, mark: null, highest: 50))
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("studio_events.mt_events_sequence");
        finding.Title.Should().Be("The event sequence is behind the events: it hands out 1 next, and mt_events already holds events up to 50");
        finding.WhyItMatters.Should().Contain("unique violation (23505)");
        finding.Remedy!.Code.Should().StartWith("// A DBA's repair, not a StoreOptions line, with appends stopped:")
            .And.Contain("setval it to that maximum");
        finding.Remedy.Code.Split('\n').Should().OnlyContain(static x => x.StartsWith("//", StringComparison.Ordinal), "the repair is said in words");

        Evaluate(Reading(49, mark: null, highest: 50)).Findings.Should().ContainSingle("the next number, 50, is one mt_events holds");
        Evaluate(Reading(50, mark: null, highest: 50)).Findings.Should().BeEmpty("the next number, 51, is free");
    }

    [Fact]
    public void A_reset_sequence_below_the_mark_and_the_events_is_both_findings()
    {
        HealthVerdict verdict = Evaluate(Reading(0, mark: 50, highest: 50), null, Shard("DailySales:All", 50));

        verdict.Findings.Select(static x => x.Subject).Should().BeEquivalentTo(["studio_events.mt_events_sequence", "HighWaterMark"]);
        verdict.Findings.Should().OnlyContain(static x => x.Severity == HealthSeverity.Critical);
        verdict.Findings.Single(static x => x.Subject == "HighWaterMark").Remedy!.Explanation
            .Should().Contain("fix that first", "the correction moves progression to the sequence, which has to be past the events");
    }

    [Fact]
    public void A_reading_without_the_sequence_position_compares_nothing()
    {
        HighWaterReading old = Reading(0, mark: 50, highest: 50) with { AllocatedHigh = null };

        HealthVerdict verdict = Evaluate(old);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("not read");
    }

    [Fact]
    public void Its_three_kinds_are_its_listed_variants_and_each_finding_says_which()
    {
        HealthCheckRegistry.Describe(typeof(ProgressionAheadOfSequenceCheck)).Variants
            .Should().Equal(["mark-ahead", "shard-ahead", "sequence-behind"]);
        HealthCheckRegistry.Problems([typeof(ProgressionAheadOfSequenceCheck)]).Should().BeEmpty();

        HealthVerdict both = Evaluate(Reading(0, mark: 50, highest: 50));
        both.Findings.Select(static x => (x.Variant, x.Key)).Should().BeEquivalentTo(
        [
            ("sequence-behind", "D09|sequence-behind|studio_events.mt_events_sequence"),
            ("mark-ahead", "D09|mark-ahead|HighWaterMark"),
        ]);

        Evaluate(Reading(20, mark: 20, highest: 20), null, Shard("DailySales:All", 40))
            .Findings.Should().ContainSingle().Which.Variant.Should().Be("shard-ahead");
    }
}
