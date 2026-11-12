using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Tests.Partitioning;

namespace KStudio.Tests.Scanning;

/// <summary>
/// One scan whose topics are searched with their own predicates and grants (D-CORR decision 2): each topic matches only its own text and
/// reads under its own grant, pruning and the audit text are per topic, a continuation keeps every topic's text, and every violation of the
/// request's rules is an <see cref="ArgumentException" />.
/// </summary>
public class PerTopicScanTests
{
    private const KafkaReadGrant Envelope = KafkaReadGrant.Envelope;
    private const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;

    private static KafkaScanRequest Request(KafkaReadGrant grant, params (string Topic, string Text, KafkaReadGrant Grant)[] queries) =>
        ScanFixture.Request([.. queries.Select(static x => x.Topic)], grant: grant) with
        {
            PerTopic = [.. queries.Select(static x => new KafkaTopicQuery(new KafkaTopicName(x.Topic), x.Text, x.Grant))],
        };

    private static async Task<string> RefusalAsync(ScanFixture fixture, KafkaScanRequest request)
    {
        Func<Task> plan = () => fixture.PlanAsync(request);
        return (await plan.Should().ThrowAsync<ArgumentException>()).Which.Message;
    }

    [Fact]
    public async Task Two_topics_with_different_texts_each_match_only_their_own_in_one_scan_with_one_consumer()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");

        fixture.Produce(a, 0, 0, "a1", "v");
        fixture.Produce(a, 0, 1, "b1", "v");
        fixture.Produce(b, 0, 2, "a1", "v");
        fixture.Produce(b, 0, 3, "b1", "v");

        KafkaScanRequest request = Request(Envelope, (a, "key = 'a1'", Envelope), (b, "key = 'b1'", Envelope));
        KafkaScanPlan plan = await fixture.PlanAsync(request);
        int readersBefore = fixture.Readers.Count;

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(request);

        plan.IsValid.Should().BeTrue();
        matches.Select(static x => (x.Topic, x.Key.Text)).Should().BeEquivalentTo([(a, "a1"), (b, "b1")]);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        (fixture.Readers.Count - readersBefore).Should().Be(1, "one scan is one consumer, whatever the number of topics");
    }

    [Fact]
    public async Task A_topic_whose_text_matches_nothing_is_still_read_and_the_others_still_match()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");

        fixture.Produce(a, 0, 0, "x", "v");
        fixture.Produce(b, 0, 1, "y", "v");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(Request(Envelope, (a, "key = 'absent'", Envelope), (b, "key = 'y'", Envelope)));

        matches.Should().ContainSingle().Which.Topic.Should().Be(b);
        result.Records.Should().Be(2);
    }

    [Fact]
    public async Task A_topic_that_redacts_a_field_without_searchable_beside_one_that_does_not_gives_a_valid_plan_because_the_builder_left_the_field_out()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string redacting = fixture.Topic("redacting");
        string plain = fixture.Topic("plain");
        Action<KafkaTopicBuilder> redact = t => t.Redact(r => r.Header("correlation-id"));
        fixture.Declare(redacting, redact);

        fixture.Produce(redacting, 0, 0, "k", "v", ("correlation-id", "c-1"), ("id", "m-1"));
        fixture.Produce(plain, 0, 1, "k", "v", ("correlation-id", "c-1"), ("id", "m-9"));

        CorrelationKeySet keys = new([new("correlation", "c-1", CorrelationValueForm.Text, "t"), new("message", "m-1", CorrelationValueForm.Text, "t")], []);
        KafkaTopicBuilder builder = Tests.Correlation.CorrelationRig.Declare(redact);
        CorrelationTopicQuery forRedacting = CorrelationQueryBuilder.Build(keys, new KafkaTopicDescriptor(redacting, builder), new KafkaRedactor(builder.Rules), payloadGranted: false);
        CorrelationTopicQuery forPlain = CorrelationQueryBuilder.Build(keys, new KafkaTopicDescriptor(plain, new KafkaTopicBuilder()), new KafkaRedactor(null), payloadGranted: false);

        forRedacting.PredicateText.Should().NotContain("correlation-id");
        forRedacting.Notes.Should().ContainSingle().Which.Should().Contain("redacted on this topic");

        (KafkaScanPlan plan, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(
            Request(Envelope, (redacting, forRedacting.PredicateText!, Envelope), (plain, forPlain.PredicateText!, Envelope)));

        plan.IsValid.Should().BeTrue();
        matches.Select(static x => x.Offset).Should().BeEquivalentTo(new long[] { 0, 0 }, "each topic found its record by the fields it can search");
        matches.Select(static x => x.Topic).Should().BeEquivalentTo([redacting, plain]);
    }

    [Fact]
    public async Task A_hand_written_query_that_names_a_redacted_field_is_invalid_and_says_on_which_topic()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string redacting = fixture.Topic("redacting");
        string plain = fixture.Topic("plain");
        fixture.Declare(redacting, t => t.Redact(r => r.Header("correlation-id")));

        KafkaScanPlan plan = await fixture.PlanAsync(
            Request(Envelope, (redacting, "header.correlation-id = 'c-1'", Envelope), (plain, "header.correlation-id = 'c-1'", Envelope)));

        plan.IsValid.Should().BeFalse();
        plan.Errors.Should().ContainSingle().Which.Message.Should().StartWith($"On {redacting}: ").And.NotContain("c-1");
        plan.Topics.Keys.Should().Equal(plain);
    }

    [Fact]
    public async Task A_text_that_does_not_parse_makes_the_plan_invalid_with_the_topic_in_the_message()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(Envelope, (a, "key = 'x'", Envelope), (b, "key =", Envelope)));

        plan.IsValid.Should().BeFalse();
        plan.Errors.Should().NotBeEmpty();
        plan.Errors.Should().OnlyContain(x => x.Message.StartsWith($"On {b}: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_topic_searched_under_the_envelope_reads_no_value_beside_one_searched_under_the_payload()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string envelope = fixture.Topic("envelope");
        string payload = fixture.Topic("payload");

        fixture.Produce(envelope, 0, 0, "k", """{"a":1}""");
        fixture.Produce(payload, 0, 1, "k", """{"a":1}""");

        (KafkaScanPlan plan, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(
            Request(Payload, (envelope, "key = 'k'", Envelope), (payload, "$.a = 1", Payload)));

        plan.ReadsValues.Should().BeTrue("the second topic's search reads values under a grant that allows it");
        KafkaMatch fromEnvelope = matches.Single(x => x.Topic == envelope);
        KafkaMatch fromPayload = matches.Single(x => x.Topic == payload);
        fromEnvelope.ValueText.Should().BeNull("a topic searched under the Envelope never reads a value, though the scan's grant allows more");
        fromEnvelope.ReadWithPayload.Should().BeFalse();
        fromPayload.ValueText.Should().Be("""{"a":1}""");
        fromPayload.ReadWithPayload.Should().BeTrue();
        plan.Topics[envelope].Grant.Should().Be(Envelope);
        plan.Topics[payload].Grant.Should().Be(Payload);
    }

    [Fact]
    public async Task A_value_clause_on_a_topic_searched_under_the_envelope_is_refused_on_that_topic_only()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string envelope = fixture.Topic("envelope");
        string payload = fixture.Topic("payload");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(Payload, (envelope, "$.a = 1", Envelope), (payload, "$.a = 1", Payload)));

        plan.IsValid.Should().BeFalse();
        plan.Errors.Should().ContainSingle().Which.Message.Should().StartWith($"On {envelope}: ").And.Contain("ReadPayloads");
    }

    [Fact]
    public async Task Reads_values_is_false_when_no_topic_that_reads_values_has_the_payload_grant()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");

        KafkaScanPlan plan = await fixture.PlanAsync(Request(Payload, (a, "key = 'k'", Envelope), (b, "key = 'k'", Payload)));

        plan.IsValid.Should().BeTrue();
        plan.ReadsValues.Should().BeFalse("no clause reads a value");
    }

    [Fact]
    public async Task The_audit_text_names_each_topic_and_hides_a_literal_that_any_topic_redacts_on_both()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string redacting = fixture.Topic("redacting");
        string plain = fixture.Topic("plain");
        fixture.Declare(redacting, t => t.Redact(r => r.Header("x").Searchable()));

        KafkaScanPlan plan = await fixture.PlanAsync(Request(Envelope, (redacting, "header.x = 'secret-v'", Envelope), (plain, "header.x = 'secret-v' and key = 'open'", Envelope)));

        plan.IsValid.Should().BeTrue();
        plan.AuditText.Should().Be($"{redacting}: header.x = <redacted>; {plain}: header.x = <redacted> and key = 'open'");
        plan.AuditText.Should().NotContain("secret-v");
    }

    [Fact]
    public async Task Pruning_on_one_topics_key_clause_leaves_the_other_topic_whole()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        const int partitions = 6;
        string pruned = fixture.Topic("pruned", partitions);
        string whole = fixture.Topic("whole", partitions);
        fixture.Declare(pruned, t => t.Partitioner(KafkaPartitioner.Murmur2));
        fixture.Declare(whole, t => t.Partitioner(KafkaPartitioner.Murmur2));
        PartitionedData.Fill(fixture, pruned, partitions, KafkaPartitioner.Murmur2, 4_000);
        PartitionedData.Fill(fixture, whole, partitions, KafkaPartitioner.Murmur2, 4_000);
        KafkaScanRequest request = Request(Envelope, (pruned, "key = 'k-0042'", Envelope), (whole, "header.id = 'm-1'", Envelope)) with { Window = ScanFixture.Window(0, 2_000) };

        KafkaScanPlan plan = await fixture.PlanAsync(request);

        plan.IsValid.Should().BeTrue();
        plan.Ranges.Where(x => x.Topic == pruned).Should().ContainSingle("the key maps to one partition");
        plan.Ranges.Where(x => x.Topic == whole).Select(static x => x.Partition).Should().Equal(Enumerable.Range(0, partitions), "a header search fixes no key");
        plan.Pruning.Should().ContainSingle().Which.Topic.Should().Be(pruned);

        KafkaScanRequest both = Request(Envelope, (pruned, "key = 'k-0042'", Envelope), (whole, "key = 'k-0043'", Envelope)) with { Window = ScanFixture.Window(0, 2_000) };
        KafkaScanPlan bothPruned = await fixture.PlanAsync(both);
        bothPruned.Ranges.Where(x => x.Topic == pruned).Should().ContainSingle();
        bothPruned.Ranges.Where(x => x.Topic == whole).Should().ContainSingle("a topic is pruned by its own key clause and by no other topic's");
        bothPruned.Ranges.Single(x => x.Topic == pruned).Partition.Should().Be(KafkaPartitioner.Murmur2.Partition(System.Text.Encoding.UTF8.GetBytes("k-0042"), partitions));
        bothPruned.Ranges.Single(x => x.Topic == whole).Partition.Should().Be(KafkaPartitioner.Murmur2.Partition(System.Text.Encoding.UTF8.GetBytes("k-0043"), partitions));
    }

    [Fact]
    public async Task A_continuation_keeps_each_topics_own_text()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");

        for (int i = 0; i < 10; i++)
        {
            fixture.Produce(a, 0, i, "k", "v", ("id", "a"));
            fixture.Produce(b, 0, i, "k", "v", ("id", i % 2 == 0 ? "a" : "b"));
        }

        KafkaScanRequest request = Request(Envelope, (a, "header.id = 'a'", Envelope), (b, "header.id = 'b'", Envelope))
            with
            {
                Lower = new KafkaScanLowering { MaxMatches = 4 },
            };

        List<KafkaMatch> all = [];
        KafkaScanCursor? cursor = null;
        int scans = 0;

        do
        {
            (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(request with { ContinueFrom = cursor });
            all.AddRange(matches);
            cursor = result.StopReason == KafkaScanStopReason.Completed ? null : result.Cursor;
            scans++;
        }
        while (cursor is not null && scans < 10);

        scans.Should().BeGreaterThan(1, "the first scan stopped at the match limit");
        all.Count(x => x.Topic == a).Should().Be(10);
        all.Count(x => x.Topic == b).Should().Be(5, "topic b's text is header.id = 'b' in every continuation, and never topic a's");
        all.Where(x => x.Topic == b).Should().OnlyContain(static x => x.Headers.Single(h => h.Name == "id").Value.Text == "b");
        all.Select(static x => (x.Topic, x.Offset)).Distinct().Should().HaveCount(15, "a continuation reads exactly what the first left");
    }

    [Fact]
    public async Task Every_violation_of_the_requests_rules_throws()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");
        KafkaScanRequest valid = Request(Payload, (a, "key = 'x'", Envelope), (b, "key = 'y'", Payload));

        // A shared text beside the per-topic ones.
        (await RefusalAsync(fixture, valid with { PredicateText = "key = 'x'" })).Should().Contain("no PredicateText");

        // Topics that are not exactly the per-topic ones, in order.
        (await RefusalAsync(fixture, valid with { Topics = [new KafkaTopicName(a)] })).Should().Contain("exactly those topics");
        (await RefusalAsync(fixture, valid with { Topics = [new KafkaTopicName(b), new KafkaTopicName(a)] })).Should().Contain("exactly those topics");
        (await RefusalAsync(fixture, valid with { Topics = [new KafkaTopicName(a), new KafkaTopicName(b), new KafkaTopicName(b)] })).Should().Contain("exactly those topics");

        // A topic twice.
        KafkaScanRequest twice = Request(Payload, (a, "key = 'x'", Envelope), (a, "key = 'y'", Envelope));
        (await RefusalAsync(fixture, twice)).Should().Contain("one search");

        // A grant without the envelope.
        (await RefusalAsync(fixture, Request(Payload, (a, "key = 'x'", KafkaReadGrant.Payload)))).Should().Contain("Envelope");
        (await RefusalAsync(fixture, Request(Payload, (a, "key = 'x'", 0)))).Should().Contain("Envelope");

        // A grant above the request's.
        (await RefusalAsync(fixture, Request(Envelope, (a, "key = 'x'", Payload)))).Should().Contain("more than the request's grant");

        // No topic at all, and no text.
        (await RefusalAsync(fixture, Request(Envelope) with { Topics = [] })).Should().Contain("at least one topic");
        (await RefusalAsync(fixture, Request(Envelope, (a, null!, Envelope)))).Should().Contain("a text");
    }

    [Fact]
    public async Task A_request_without_per_topic_queries_is_unchanged()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string a = fixture.Topic("a");
        string b = fixture.Topic("b");
        fixture.Produce(a, 0, 0, "k", "v");
        fixture.Produce(b, 0, 1, "k", "v");

        (KafkaScanPlan plan, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([a, b], "key = 'k'"));

        plan.Request.PerTopic.Should().BeNull();
        plan.AuditText.Should().Be("key = 'k'");
        plan.Topics.Values.Should().OnlyContain(static x => x.Grant == Envelope);
        matches.Should().HaveCount(2);
    }
}
