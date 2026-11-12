using System.Diagnostics;

using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;

namespace KStudio.Tests.Correlation;

/// <summary>The duplicates panel's finder (D-CORR decision 4): copies are on one topic, by the first identity a record has, from what is shown.</summary>
public class KafkaDuplicateFinderTests
{
    private const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;

    private static KafkaMatch Made(
        KafkaTopicBuilder builder,
        KafkaReadGrant grant,
        string key,
        string? value,
        string topic = "orders",
        long offset = 1,
        long seconds = 0,
        (string Name, string? Value)[]? headers = null) =>
        CorrelationRig.Match(builder, grant, key, value, headers ?? [], topic, offset, 0, 1_700_000_000_000 + (seconds * 1000));

    [Fact]
    public void Two_records_with_one_envelope_id_on_one_topic_are_copies()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch[] matches =
        [
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 1, seconds: 5, headers: [("id", "m-1")]),
            Made(builder, KafkaReadGrant.Envelope, "b", null, offset: 2, seconds: 1, headers: [("id", "m-1")]),
            Made(builder, KafkaReadGrant.Envelope, "c", null, offset: 3, seconds: 2, headers: [("id", "m-2")]),
        ];

        IReadOnlyList<KafkaDuplicateGroup> groups = KafkaDuplicateFinder.Find(matches);

        KafkaDuplicateGroup group = groups.Should().ContainSingle().Subject;
        group.Basis.Should().Be(KafkaDuplicateBasis.EnvelopeId);
        group.Topic.Should().Be("orders");
        group.Copies.Select(static x => x.Offset).Should().Equal(new long[] { 2, 1 }, "copies are ordered by time");
        group.Count.Should().Be(2);
        group.First.Should().Be(group.Copies[0].Timestamp);
        group.Last.Should().Be(group.Copies[1].Timestamp);
        group.ComparedAsShown.Should().BeFalse();
    }

    [Fact]
    public void Cloudevents_source_and_id_are_the_second_basis_and_both_must_be_literals()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        static string Event(string id, string source) => "{\"specversion\":\"1.0\",\"id\":\"" + id + "\",\"source\":\"" + source + "\",\"type\":\"t\",\"data\":{}}";
        (string, string?)[] ce = [("content-type", "application/cloudevents+json")];

        KafkaMatch[] matches =
        [
            Made(builder, Payload, "a", Event("e-1", "/s"), offset: 1, headers: ce),
            Made(builder, Payload, "b", Event("e-1", "/s"), offset: 2, seconds: 1, headers: ce),
            Made(builder, Payload, "c", Event("e-1", "/other"), offset: 3, seconds: 2, headers: ce),
        ];

        IReadOnlyList<KafkaDuplicateGroup> groups = KafkaDuplicateFinder.Find(matches);

        groups.Should().ContainSingle().Which.Should().Match<KafkaDuplicateGroup>(static x => x.Basis == KafkaDuplicateBasis.CloudEvent && x.Count == 2);
    }

    [Fact]
    public void Key_and_value_are_the_last_basis_and_need_the_payload()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch[] withPayload = [Made(builder, Payload, "k", "same", offset: 1), Made(builder, Payload, "k", "same", offset: 2, seconds: 1), Made(builder, Payload, "k", "other", offset: 3)];
        KafkaMatch[] withoutPayload = [Made(builder, KafkaReadGrant.Envelope, "k", "same", offset: 1), Made(builder, KafkaReadGrant.Envelope, "k", "same", offset: 2, seconds: 1)];

        KafkaDuplicateFinder.Find(withPayload).Should().ContainSingle().Which.Basis.Should().Be(KafkaDuplicateBasis.KeyAndValue);
        KafkaDuplicateFinder.Find(withoutPayload).Should().BeEmpty("a value never read says nothing about equality");
    }

    [Fact]
    public void A_key_with_the_same_value_but_another_key_is_no_copy()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();

        KafkaDuplicateFinder.Find([Made(builder, Payload, "k1", "same", offset: 1), Made(builder, Payload, "k2", "same", offset: 2)]).Should().BeEmpty();
    }

    [Fact]
    public void A_masked_id_header_is_no_identity()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("id")));
        KafkaMatch[] matches =
        [
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 1, headers: [("id", "m-1")]),
            Made(builder, KafkaReadGrant.Envelope, "b", null, offset: 2, headers: [("id", "m-2")]),
        ];

        matches[0].Headers.Single().Value.IsRedacted.Should().BeTrue();
        KafkaDuplicateFinder.Find(matches).Should().BeEmpty("two masks are equal, and two records with different ids are not copies");
    }

    [Fact]
    public void A_cut_or_withheld_value_is_no_identity()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        string big = new('x', 400_000);
        KafkaMatch first = Made(builder, Payload, "k", big, offset: 1);
        KafkaMatch second = Made(builder, Payload, "k", big, offset: 2);

        first.ValueTruncated.Should().BeTrue();
        KafkaDuplicateFinder.Find([first, second]).Should().BeEmpty();
    }

    [Fact]
    public void A_masked_value_is_compared_as_shown_and_the_group_says_so()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.JsonPath("$.secret")));
        KafkaMatch[] matches =
        [
            Made(builder, Payload, "k", """{"secret":"one","n":1}""", offset: 1),
            Made(builder, Payload, "k", """{"secret":"two","n":1}""", offset: 2, seconds: 1),
        ];

        KafkaDuplicateGroup group = KafkaDuplicateFinder.Find(matches).Should().ContainSingle().Subject;

        group.Basis.Should().Be(KafkaDuplicateBasis.KeyAndValue);
        group.ComparedAsShown.Should().BeTrue("equal masks compare equal, and the panel says so");
    }

    [Fact]
    public void Copies_on_two_topics_are_no_duplicates()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch[] matches =
        [
            Made(builder, KafkaReadGrant.Envelope, "a", null, topic: "orders", headers: [("id", "m-1")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, topic: "orders.retry", offset: 2, headers: [("id", "m-1")]),
        ];

        KafkaDuplicateFinder.Find(matches).Should().BeEmpty();
    }

    [Fact]
    public void Groups_are_ordered_by_size_then_by_first_time()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch[] matches =
        [
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 1, seconds: 10, headers: [("id", "late")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 2, seconds: 11, headers: [("id", "late")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 3, seconds: 1, headers: [("id", "early")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 4, seconds: 2, headers: [("id", "early")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 5, seconds: 3, headers: [("id", "triple")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 6, seconds: 4, headers: [("id", "triple")]),
            Made(builder, KafkaReadGrant.Envelope, "a", null, offset: 7, seconds: 5, headers: [("id", "triple")]),
        ];

        KafkaDuplicateFinder.Find(matches).Select(static x => x.Count).Should().Equal(3, 2, 2);
        KafkaDuplicateFinder.Find(matches).Skip(1).Select(static x => x.Copies[0].Offset).Should().Equal(new long[] { 3, 1 });
    }

    [Fact]
    public void A_single_record_and_no_records_make_no_group()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();

        KafkaDuplicateFinder.Find([]).Should().BeEmpty();
        KafkaDuplicateFinder.Find([Made(builder, KafkaReadGrant.Envelope, "a", null, headers: [("id", "m-1")])]).Should().BeEmpty();
    }

    [Fact]
    public void Five_hundred_matches_are_grouped_in_well_under_twenty_milliseconds()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch[] matches = [.. Enumerable.Range(0, 500).Select(i => Made(builder, KafkaReadGrant.Envelope, "k", null, offset: i, seconds: i, headers: [("id", "m-" + (i % 250))]))];

        // Warm the JIT, then measure: a bound and not a benchmark.
        KafkaDuplicateFinder.Find(matches);
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<KafkaDuplicateGroup> groups = KafkaDuplicateFinder.Find(matches);
        watch.Stop();

        groups.Should().HaveCount(250);
        watch.ElapsedMilliseconds.Should().BeLessThan(20);
    }

    [Fact]
    public void Five_hundred_matches_with_the_largest_values_are_grouped_in_under_twenty_milliseconds_and_copy_no_value()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        string Value(int i) => i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + new string('x', 40_000);
        KafkaMatch[] matches = [.. Enumerable.Range(0, 500).Select(i => Made(builder, Payload, "k" + (i % 250), Value(i % 250), offset: i, seconds: i))];

        matches.Should().OnlyContain(static x => !x.ValueTruncated && x.ValueText != null && x.ValueText.Length > 40_000);

        KafkaDuplicateFinder.Find(matches);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<KafkaDuplicateGroup> groups = KafkaDuplicateFinder.Find(matches);
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        groups.Should().HaveCount(250).And.OnlyContain(static x => x.Basis == KafkaDuplicateBasis.KeyAndValue);
        watch.ElapsedMilliseconds.Should().BeLessThan(20);
        allocated.Should().BeLessThan(2_000_000, "a value of 80 KB copied 500 times would be 40 MB");
    }

    [Fact]
    public void The_finder_reads_only_the_matches_it_is_given()
    {
        typeof(KafkaDuplicateFinder).GetMethods().Where(static x => x.DeclaringType == typeof(KafkaDuplicateFinder) && x.IsPublic).Select(static x => x.Name).Should().Equal("Find");
    }
}
