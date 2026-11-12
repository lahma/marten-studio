using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Correlation;

namespace KStudio.Tests.Services;

/// <summary>What the related-records page derives from the matches it holds (decision 3.5 of D-CORR): matched-by, the lane order, the strip, the order of rows.</summary>
public class KafkaRelatedViewTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static long Ms(int seconds) => From.AddSeconds(seconds).ToUnixTimeMilliseconds();

    private static KafkaMatch Match(string topic, long offset, int seconds, params (string, string?)[] headers) =>
        CorrelationRig.Match(CorrelationRig.Declare(), KafkaReadGrant.Envelope, headers: headers, topic: topic, offset: offset, timestampMs: Ms(seconds));

    private static KafkaRelatedLane Lane(string topic, bool scanned = true) =>
        new(
            new KafkaTopicName(topic),
            new CorrelationTopicQuery(new KafkaTopicName(topic), scanned ? "header.correlation-id = 'c-1'" : null, false, scanned ? [] : ["no field"]),
            KafkaReadGrant.Envelope,
            scanned ? "header.correlation-id = 'c-1'" : null);

    [Fact]
    public void Matched_by_names_the_kind_and_the_field_of_each_literal_that_is_in_the_key_set()
    {
        KafkaTopicDescriptor descriptor = CorrelationRig.Descriptor(CorrelationRig.Declare());
        KafkaMatch row = Match("orders", 1, 5, ("correlation-id", "c-1"), ("id", "m-9"), ("conversation-id", "m-1"));
        CorrelationKeySet keys = new([new CorrelationValue("correlation", "c-1", CorrelationValueForm.Text, "header.correlation-id"), new CorrelationValue("message", "m-1", CorrelationValueForm.Text, "header.id")], []);

        KafkaRelatedView.MatchedBy(row, descriptor.CorrelationFields, keys).Should().Equal("correlation (header.correlation-id)", "message (header.conversation-id)");
    }

    [Fact]
    public void Matched_by_says_a_redacted_field_when_no_literal_field_can_have_matched()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id").Searchable()));
        KafkaMatch row = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", "c-1")]);
        CorrelationKeySet keys = CorrelationKeySet.Seed("correlation", "c-1");

        KafkaRelatedView.MatchedBy(row, CorrelationRig.Descriptor(builder).CorrelationFields, keys).Should().Equal(KafkaRelatedView.RedactedField);
    }

    [Fact]
    public void Matched_by_does_not_claim_a_redacted_field_when_none_of_a_searched_kind_is_masked_on_the_row()
    {
        KafkaTopicDescriptor descriptor = CorrelationRig.Descriptor(CorrelationRig.Declare());
        KafkaMatch row = Match("orders", 1, 5, ("correlation-id", "c-1"));

        KafkaRelatedView.MatchedBy(row, descriptor.CorrelationFields, CorrelationKeySet.Seed("correlation", "c-9")).Should().Equal(KafkaRelatedView.NotShownField);
        KafkaRelatedView.NotShownField.Should().Be("a field not shown here");
    }

    [Fact]
    public void Matched_by_says_a_redacted_field_only_for_a_kind_that_was_searched()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id").Searchable()));
        KafkaMatch row = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", "c-1")]);
        IReadOnlyList<CorrelationField> fields = CorrelationRig.Descriptor(builder).CorrelationFields;

        KafkaRelatedView.MatchedBy(row, fields, CorrelationKeySet.Seed("saga", "s-1")).Should().Equal([KafkaRelatedView.NotShownField], "the masked field is of a kind nobody searched for");
        KafkaRelatedView.MatchedBy(row, fields, CorrelationKeySet.Seed("correlation", "c-1")).Should().Equal(KafkaRelatedView.RedactedField);
    }

    [Fact]
    public void Rows_are_ordered_by_time_then_topic_partition_and_offset()
    {
        KafkaMatch[] matches = [Match("b", 1, 10), Match("a", 3, 10), Match("a", 2, 10), Match("z", 0, 5)];

        KafkaRelatedView.InOrder(matches).Select(static x => (x.Topic, x.Offset)).Should().Equal(("z", 0L), ("a", 2L), ("a", 3L), ("b", 1L));
    }

    [Fact]
    public void Lanes_are_ordered_the_anchor_first_then_by_first_match_then_by_name_then_not_readable()
    {
        KafkaRelatedLane[] lanes = [Lane("zeta"), Lane("alpha"), Lane("late"), Lane("early"), Lane("blocked"), Lane("anchor.v1"), Lane("bare", scanned: false)];
        KafkaMatch[] matches = [Match("late", 0, 30), Match("early", 0, 10), Match("anchor.v1", 0, 20)];
        KafkaScanResult result = Result(refused: "blocked");

        IReadOnlyList<KafkaTimelineLane> drawn = KafkaRelatedView.Lanes(lanes, matches, result, running: false, started: true, anchorTopic: "anchor.v1");

        drawn.Select(static x => x.Topic).Should().Equal("anchor.v1", "early", "late", "alpha", "bare", "zeta", "blocked");
        drawn.Single(static x => x.Topic == "blocked").State.Should().StartWith("not readable: ");
        drawn.Single(static x => x.Topic == "bare").State.Should().Be("Nothing to compare on this topic: no field");
    }

    [Fact]
    public void The_strip_places_a_dot_along_the_window_and_the_summary_is_its_text()
    {
        KafkaMatch anchor = Match("a", 0, 30);
        KafkaMatch[] matches = [anchor, Match("b", 0, 60)];
        IReadOnlyList<KafkaTimelineLane> drawn = KafkaRelatedView.Lanes([Lane("a"), Lane("b")], matches, null, running: false, started: false, anchorTopic: "a");

        KafkaTimelineModel strip = KafkaRelatedView.Strip(drawn, matches, From, From.AddSeconds(120), anchor);

        strip.Lanes.Should().Equal("a", "b");
        strip.Dots.Should().BeEquivalentTo([new KafkaTimelineDot(0, 25d, true), new KafkaTimelineDot(1, 50d, false)]);
        strip.Summary.Should().Be("2 records on 2 topics from 12:00:30 to 12:01:00 UTC");
    }

    [Fact]
    public void A_causation_id_header_is_a_chip()
    {
        KafkaMatch row = Match("orders", 1, 5, ("causation-id", "m-0"), ("correlation-id", "c-1"));

        KafkaMatchView.Chips(row).Select(static x => x.Name).Should().Contain("causation-id");
        KafkaMatchView.Chips(row).Single(static x => x.Name == "causation-id").Predicate.Should().Be("header.causation-id = 'm-0'");
    }

    private static KafkaScanResult Result(string refused) =>
        new(
            Guid.NewGuid(),
            KafkaScanStopReason.Completed,
            TimeSpan.Zero,
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new Dictionary<KafkaUnknownReason, long>(),
            [],
            [],
            [new KafkaTopicRefusal(refused, "Not authorized to describe the topic.")],
            new KafkaScanCursor([]),
            new DecodeCounters(),
            new DecodeCounters());

    [Fact]
    public void Before_the_first_search_the_summary_says_nothing_was_searched_rather_than_nothing_found()
    {
        KafkaRelatedView.Summary([], [], searched: false).Should().Be(KafkaRelatedView.NotSearchedYet);
        KafkaRelatedView.Summary([], [], searched: true).Should().Be("No related records found.");
    }
}
