using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

namespace KStudio.Tests.Correlation;

/// <summary>What an anchor's correlation values are made of (D-CORR 1.4): only literals the visitor sees whole, never a masked, cut or binary field.</summary>
public class CorrelationExtractorTests
{
    private const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string TraceParent = "00-" + TraceId + "-00f067aa0ba902b7-01";

    private static CorrelationKeySet Extract(KafkaTopicBuilder builder, KafkaMatch match) => CorrelationExtractor.Extract(match, CorrelationRig.Descriptor(builder).CorrelationFields);

    private static (string Name, string? Value) Header(string name, string value) => (name, value);

    private static string[] Values(CorrelationKeySet keys, string kind) => [.. keys.Of(kind).Select(static x => x.Value)];

    [Fact]
    public void The_default_fields_give_their_kinds()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", "c-1"), ("id", "m-1"), ("conversation-id", "m-0"), ("saga-id", "s-1"), ("tenant-id", "acme")]);

        CorrelationKeySet keys = Extract(builder, match);

        Values(keys, "correlation").Should().Equal("c-1");
        Values(keys, "message").Should().Equal("m-1", "m-0");
        Values(keys, "saga").Should().Equal("s-1");
        keys.Values.Select(static x => x.Value).Should().NotContain("acme", "tenant-id is a scope and not a key");
        keys.Values.Single(static x => x.Value == "c-1").From.Should().Be("header.correlation-id");
    }

    [Fact]
    public void A_header_is_matched_by_name_ignoring_case()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("Correlation-Id", "c-1")]);

        Values(Extract(builder, match), "correlation").Should().Equal("c-1");
    }

    [Fact]
    public void A_masked_header_gives_no_key_and_no_trace_of_its_value()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Redact(r => r.Header("correlation-id").Searchable()));
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", "secret-corr"), ("id", "m-1")]);

        CorrelationKeySet keys = Extract(builder, match);

        Values(keys, "correlation").Should().BeEmpty();
        keys.Values.Select(static x => x.Value).Should().Equal("m-1");
    }

    [Fact]
    public void A_cut_header_gives_no_key()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", new string('a', KafkaMatchFactory.MaxHeaderBytes + 100))]);

        match.Headers.Single().Value.Truncated.Should().BeTrue();
        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_header_longer_than_a_search_literal_gives_no_key()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", new string('a', 1_001))]);

        Extract(builder, match).IsEmpty.Should().BeTrue();
        Values(Extract(builder, CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", new string('a', 1_000))])), "correlation").Should().ContainSingle();
    }

    [Fact]
    public void A_binary_header_gives_no_key()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, "k", null, [new WireHeader("correlation-id", [0xFF, 0xFE, 0x00, 0x01])]);

        match.Headers.Single().Value.Text.Should().StartWith("0x", "a binary header is kept as hex");
        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_null_or_empty_header_gives_no_key()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("correlation-id", null), ("id", string.Empty)]);

        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_header_is_read_at_most_four_times()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: Enumerable.Range(1, 6).Select(static x => Header("id", "m-" + x)));

        Values(Extract(builder, match), "message").Should().Equal("m-1", "m-2", "m-3", "m-4");
    }

    [Fact]
    public void A_cloudevents_attribute_is_read_and_a_masked_one_is_not()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.CloudEvent("subject").As("order")));
        string json = """{"specversion":"1.0","id":"ce-1","source":"/orders","type":"x","subject":"order-9","data":{"a":1}}""";
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", json, [("content-type", "application/cloudevents+json")]);

        CorrelationKeySet keys = Extract(builder, match);

        Values(keys, "order").Should().Equal("order-9");
        Values(keys, "message").Should().Equal("ce-1");

        KafkaTopicBuilder masking = CorrelationRig.Declare(t => t.Correlation(c => c.CloudEvent("subject").As("order")).Redact(r => r.CloudEvent("subject")));
        Values(Extract(masking, CorrelationRig.Match(masking, Payload, "k", json, [("content-type", "application/cloudevents+json")])), "order").Should().BeEmpty();
    }

    [Fact]
    public void The_key_is_read_when_it_is_a_literal_only()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c => c.Key().As("order")));
        Values(Extract(builder, CorrelationRig.Match(builder, KafkaReadGrant.Envelope, key: "order-1")), "order").Should().Equal("order-1");

        KafkaTopicBuilder masking = CorrelationRig.Declare(t => t.Correlation(c => c.Key().As("order")).Redact(r => r.Key()));
        Values(Extract(masking, CorrelationRig.Match(masking, KafkaReadGrant.Envelope, key: "order-1")), "order").Should().BeEmpty();

        Values(Extract(builder, CorrelationRig.Match(builder, KafkaReadGrant.Envelope, key: null)), "order").Should().BeEmpty();
    }

    // JSON paths.

    private static KafkaTopicBuilder Path(string path, Action<KafkaTopicBuilder>? more = null) =>
        CorrelationRig.Declare(t =>
        {
            t.Correlation(c =>
            {
                c.WithoutDefaults();
                c.JsonPath(path).As("order");
            });
            more?.Invoke(t);
        });

    [Fact]
    public void A_json_path_gives_a_scalar_in_its_own_form()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c =>
        {
            c.WithoutDefaults();
            c.JsonPath("$.s").As("a");
            c.JsonPath("$.n").As("b");
            c.JsonPath("$.t").As("c");
            c.JsonPath("$.f").As("d");
            c.JsonPath("$.nul").As("e");
            c.JsonPath("$.o").As("g");
            c.JsonPath("$.arr").As("h");
        }));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"s":"text","n":42.5,"t":true,"f":false,"nul":null,"o":{"x":1},"arr":[1]}""");

        CorrelationKeySet keys = Extract(builder, match);

        keys.Values.Select(static x => (x.Kind, x.Value, x.Form)).Should().Equal(
            ("a", "text", CorrelationValueForm.Text),
            ("b", "42.5", CorrelationValueForm.Number),
            ("c", "true", CorrelationValueForm.Bool),
            ("d", "false", CorrelationValueForm.Bool));
    }

    [Fact]
    public void A_json_path_with_an_index_and_a_wildcard_reads_each_element()
    {
        KafkaTopicBuilder builder = Path("$.items[*].id");
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"items":[{"id":"a"},{"id":"b"},{"x":1}]}""");

        Values(Extract(builder, match), "order").Should().Equal("a", "b");

        KafkaTopicBuilder indexed = Path("$.items[1].id");
        Values(Extract(indexed, CorrelationRig.Match(indexed, Payload, "k", """{"items":[{"id":"a"},{"id":"b"}]}""")), "order").Should().Equal("b");
    }

    [Fact]
    public void A_json_path_under_a_mask_gives_none()
    {
        KafkaTopicBuilder builder = Path("$.order.id", t => t.Redact(r => r.JsonPath("$.order.id")));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"order":{"id":"secret-1"}}""");

        match.MaskedPaths.Should().NotBeEmpty();
        Extract(builder, match).IsEmpty.Should().BeTrue();
        match.ValueText.Should().NotContain("secret-1");
    }

    [Fact]
    public void A_json_path_under_a_masked_subtree_gives_none()
    {
        KafkaTopicBuilder builder = Path("$.order.id", t => t.Redact(r => r.JsonPath("$.order")));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"order":{"id":"secret-1"}}""");

        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_json_path_beside_a_mask_is_still_read()
    {
        KafkaTopicBuilder builder = Path("$.order.id", t => t.Redact(r => r.JsonPath("$.order.email")));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"order":{"id":"o-1","email":"a@b.c"}}""");

        Values(Extract(builder, match), "order").Should().Equal("o-1");
    }

    [Fact]
    public void A_match_with_masked_paths_left_out_gives_no_json_path_value()
    {
        KafkaTopicBuilder builder = Path("$.order.id", t => t.Redact(r => r.JsonPath("$.items[*].secret")));
        string items = string.Join(",", Enumerable.Range(0, 80).Select(static x => $$"""{"secret":"s{{x}}"}"""));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", $$"""{"order":{"id":"o-1"},"items":[{{items}}]}""");

        match.MaskedPathsOmitted.Should().BeGreaterThan(0, "more nodes were masked than a match names");
        Extract(builder, match).IsEmpty.Should().BeTrue("with some masked paths left out, no node can be proved unmasked");
    }

    [Fact]
    public void An_anchor_read_without_the_payload_gives_no_json_path_value()
    {
        KafkaTopicBuilder builder = Path("$.order.id");
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, "k", """{"order":{"id":"o-1"}}""");

        match.ReadWithPayload.Should().BeFalse();
        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_cut_or_non_json_value_gives_no_json_path_value()
    {
        KafkaTopicBuilder builder = Path("$.order.id");
        KafkaMatch plain = CorrelationRig.Match(builder, Payload, "k", "order id o-1");
        Extract(builder, plain).IsEmpty.Should().BeTrue();

        string big = "{\"order\":{\"id\":\"o-1\"},\"pad\":\"" + new string('x', 200_000) + "\"}";
        KafkaMatch cut = CorrelationRig.Match(builder, Payload, "k", big);
        cut.ValueTruncated.Should().BeTrue();
        Extract(builder, cut).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_value_that_holds_a_mask_is_not_a_key()
    {
        KafkaTopicBuilder builder = Path("$.note", t => t.Redact(r => r.Pattern(@"[0-9]{4}", "digits")));
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", """{"note":"call 1234 now"}""");

        match.ValueText.Should().Contain("redacted by");
        Extract(builder, match).IsEmpty.Should().BeTrue();
    }

    // Trace.

    [Fact]
    public void A_trace_comes_from_a_literal_source_header_only()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("traceparent", TraceParent)]);

        Values(Extract(builder, match), "trace").Should().Equal(TraceId);

        KafkaTopicBuilder masking = CorrelationRig.Declare(t => t.Redact(r => r.Header("traceparent")));
        KafkaMatch masked = CorrelationRig.Match(masking, KafkaReadGrant.Envelope, headers: [("traceparent", TraceParent)]);
        Values(Extract(masking, masked), "trace").Should().BeEmpty("the trace is not read from a masked header");

        KafkaTopicBuilder without = CorrelationRig.Declare(t => t.Correlation(c => c.WithoutDefaults().Header("x-a")));
        Values(Extract(without, CorrelationRig.Match(without, KafkaReadGrant.Envelope, headers: [("traceparent", TraceParent)])), "trace").Should().BeEmpty("the topic has no trace field");
    }

    [Fact]
    public void A_trace_is_read_from_the_header_it_came_from_whichever_of_the_three()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();

        foreach (string header in new[] { "traceparent", "ce_traceparent", "parent-id" })
        {
            Values(Extract(builder, CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [(header, TraceParent)])), "trace").Should().Equal(new[] { TraceId }, header);
        }
    }

    // Bounds.

    [Fact]
    public void A_kind_has_at_most_ten_values_and_the_rest_are_counted()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(
            builder,
            KafkaReadGrant.Envelope,
            headers: [.. Enumerable.Range(1, 4).Select(static x => Header("id", "a-" + x)), .. Enumerable.Range(1, 4).Select(static x => Header("conversation-id", "b-" + x)), .. Enumerable.Range(1, 4).Select(static x => Header("causation-id", "c-" + x))]);

        CorrelationKeySet keys = Extract(builder, match);

        Values(keys, "message").Should().HaveCount(10);
        keys.Notes.Should().Equal("2 more `message` values were not used.");
    }

    [Fact]
    public void A_set_has_at_most_24_values_in_all_and_the_rest_are_counted()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare(t => t.Correlation(c =>
        {
            c.WithoutDefaults();
            c.JsonPath("$.a[*]").As("ka");
            c.JsonPath("$.b[*]").As("kb");
            c.JsonPath("$.c[*]").As("kc");
        }));
        string Array(string prefix) => "[" + string.Join(",", Enumerable.Range(0, 10).Select(x => $"\"{prefix}{x}\"")) + "]";
        KafkaMatch match = CorrelationRig.Match(builder, Payload, "k", $$"""{"a":{{Array("a")}},"b":{{Array("b")}},"c":{{Array("c")}}}""");

        CorrelationKeySet keys = Extract(builder, match);

        keys.Values.Should().HaveCount(24);
        keys.Notes.Should().Equal("6 more values were not used.");
        Values(keys, "kc").Should().HaveCount(4, "fields are read in order, so the last kind is cut");
    }

    [Fact]
    public void A_repeated_value_is_kept_once()
    {
        KafkaTopicBuilder builder = CorrelationRig.Declare();
        KafkaMatch match = CorrelationRig.Match(builder, KafkaReadGrant.Envelope, headers: [("id", "m-1"), ("conversation-id", "m-1"), ("causation-id", "m-1")]);

        Values(Extract(builder, match), "message").Should().Equal("m-1");
    }

    [Fact]
    public void Seed_makes_a_set_of_one_value()
    {
        CorrelationKeySet seed = CorrelationKeySet.Seed("document:Order", "o-1");

        seed.Values.Should().ContainSingle().Which.Should().Be(new CorrelationValue("document:Order", "o-1", CorrelationValueForm.Text, "link"));
    }
}
