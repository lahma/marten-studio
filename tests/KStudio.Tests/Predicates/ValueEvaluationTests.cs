using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Wire;

using static KStudio.Tests.Predicates.ValueHarness;

namespace KStudio.Tests.Predicates;

/// <summary>
/// What the value-side clauses say of each kind of value (decision 2.2 of D-PAYLOAD): <c>value</c>, JSON paths and <c>any</c> over JSON, text,
/// empty, tombstone, binary and undecodable values, the number rules, existential selections, and CloudEvents data in binary, structured and
/// batch modes. A record that cannot be judged is unknown with its reason, never a non-match.
/// </summary>
public class ValueEvaluationTests
{
    private static readonly (string Name, string Value) Structured = ("content-type", "application/cloudevents+json");
    private static readonly (string Name, string Value) Batch = ("content-type", "application/cloudevents-batch+json");

    private static void Check(string text, WireRecord record, TriState expected, KafkaUnknownReason? reason = null, IKafkaRecordDecoder? decoder = null, int maxDecodeBytes = 1024 * 1024)
    {
        Outcome outcome = Run(text, record, decoder: decoder, maxDecodeBytes: maxDecodeBytes);

        outcome.Verdict.Should().Be(expected, text);

        if (expected == TriState.Unknown)
        {
            outcome.Reason.Should().Be(reason, text);
        }
    }

    // ---- the kind table

    [Fact]
    public void A_json_value_is_compared_as_its_own_bytes_and_by_its_paths()
    {
        WireRecord json = Json("""{"a":"x"}""");

        Check("value = 'x'", json, TriState.False);
        Check("""value = '{"a":"x"}'""", json, TriState.True);
        Check("""value ^= '{"a"'""", json, TriState.True);
        Check("value ~ 'X'", json, TriState.True);
        Check("value in ('nope', 'x')", json, TriState.False);
        Check("$.a = 'x'", json, TriState.True);
        Check("any ~ 'x'", json, TriState.True);
        Check("any = 'x'", json, TriState.True);
        Check("any ^= 'y'", json, TriState.False);
    }

    [Fact]
    public void A_json_value_is_never_reformatted_for_a_value_clause()
    {
        Check("""value = '{ "a" :  "x" }'""", Json("""{ "a" :  "x" }"""), TriState.True);
        Check("""value = '{"a":"x"}'""", Json("""{ "a" :  "x" }"""), TriState.False);
    }

    [Fact]
    public void A_text_value_is_its_text_and_has_no_paths()
    {
        WireRecord text = Json("plain x text");

        Check("value = 'plain x text'", text, TriState.True);
        Check("value ^= 'plain'", text, TriState.True);
        Check("value ~ 'X TEXT'", text, TriState.True);
        Check("value = 'x'", text, TriState.False);
        Check("$.a = 'x'", text, TriState.False);
        Check("any ~ 'x'", text, TriState.True);
    }

    [Fact]
    public void An_empty_value_is_the_empty_text_and_a_tombstone_is_nothing_at_all()
    {
        WireRecord empty = Bytes([]);
        WireRecord tombstone = Json(null);

        Check("value = ''", empty, TriState.True);
        Check("value ~ 'x'", empty, TriState.False);
        Check("$.a = 'x'", empty, TriState.False);
        Check("any ~ 'x'", empty, TriState.False);

        Check("value = ''", tombstone, TriState.False);
        Check("value ~ ''", tombstone, TriState.False);
        Check("value ~ 'x'", tombstone, TriState.False);
        Check("$.a = null", tombstone, TriState.False);
        Check("any ~ 'x'", tombstone, TriState.False);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("wire")]
    public void Bytes_are_never_equal_to_a_text_and_cannot_be_searched_for_one(string kind)
    {
        WireRecord record = kind == "binary" ? Bytes([0xff, 0xfe, 0x01, 0x02]) : Bytes([0, 0, 0, 0, 7, 1, 2, 3]);

        Check("value = 'x'", record, TriState.False);
        Check("value ^= 'x'", record, TriState.False);
        Check("value ~ 'x'", record, TriState.Unknown, KafkaUnknownReason.ValueNotText);
        Check("$.a = 'x'", record, TriState.False);
        Check("any ~ 'x'", record, TriState.False);
    }

    [Fact]
    public void A_value_that_starts_like_json_and_does_not_parse_is_text_for_value_and_unknown_for_a_path()
    {
        WireRecord broken = Json("{ broken");

        Check("value = '{ broken'", broken, TriState.True);
        Check("value ~ 'zzz'", broken, TriState.False);
        Check("$.a = 'x'", broken, TriState.Unknown, KafkaUnknownReason.InvalidJson);
        Check("any ~ 'broken'", broken, TriState.True);
    }

    [Fact]
    public void A_value_declared_as_json_that_is_not_is_unknown_for_every_clause()
    {
        WireRecord record = Json("not json");

        foreach (string text in new[] { "value = 'x'", "value ~ 'x'", "$.a = 'x'", "any ~ 'x'" })
        {
            Check(text, record, TriState.Unknown, KafkaUnknownReason.InvalidJson, decoder: KafkaDecoders.Json);
        }
    }

    [Fact]
    public void A_value_over_the_decode_limit_is_unknown_with_its_reason_and_a_text_is_unknown_for_a_value_clause()
    {
        WireRecord json = Json("""{"a":"x","b":"yyyyyyyyyyy"}""");

        foreach (string text in new[] { "value ~ 'x'", "$.a = 'x'", "any ~ 'x'" })
        {
            Check(text, json, TriState.Unknown, KafkaUnknownReason.TooLarge, maxDecodeBytes: 10);
        }

        WireRecord longText = Json("hello " + new string('w', 30));
        Check("value ~ 'hello'", longText, TriState.Unknown, KafkaUnknownReason.TooLarge, maxDecodeBytes: 10);
        Check("$.a = 'x'", longText, TriState.False, maxDecodeBytes: 10);
    }

    [Fact]
    public void A_decoder_that_throws_or_refuses_makes_the_record_unknown()
    {
        WireRecord record = Json("anything");
        ThrowingDecoder throwing = new();
        RefusingDecoder refusing = new();

        foreach (string text in new[] { "value ~ 'x'", "$.a = 'x'", "any ~ 'x'" })
        {
            Check(text, record, TriState.Unknown, KafkaUnknownReason.DecoderFailed, throwing);
            Check(text, record, TriState.Unknown, KafkaUnknownReason.DecoderFailed, refusing);
        }
    }

    [Fact]
    public void A_host_decoder_decides_the_kind_the_clauses_see()
    {
        FixedDecoder json = new(KafkaDecodeResult.Json("""{"decoded":42}"""u8.ToArray(), "Fixed JSON"));
        FixedDecoder text = new(KafkaDecodeResult.Text("decoded text", "Fixed text"));

        Check("$.decoded = 42", Bytes([0xff, 0x00]), TriState.True, decoder: json);
        Check("value = 'decoded text'", Bytes([0xff, 0x00]), TriState.True, decoder: text);
        Check("value = 'decoded text'", Bytes([0xff, 0x00]), TriState.False);
    }

    // ---- is null

    [Fact]
    public void Value_is_null_is_true_of_a_tombstone_only_and_decodes_nothing()
    {
        Outcome tombstone = Run("value is null", Json(null), grant: KafkaReadGrant.Envelope);

        tombstone.Verdict.Should().Be(TriState.True);
        tombstone.Counters.Total.Should().Be(0);
        Run("value is null", Bytes([]), grant: KafkaReadGrant.Envelope).Verdict.Should().Be(TriState.False, "an empty value is not null");
        Run("value is null", Json("x"), grant: KafkaReadGrant.Envelope).Verdict.Should().Be(TriState.False);
        Run("value is not null", Json("x"), grant: KafkaReadGrant.Envelope).Verdict.Should().Be(TriState.True);
        Run("value is not null", Json(null), grant: KafkaReadGrant.Envelope).Verdict.Should().Be(TriState.False);
        tombstone.Predicate.ReadsValues.Should().BeFalse();
    }

    // ---- paths: operators and numbers

    private static readonly WireRecord Document = Json(
        """{"a":"x","n":42,"f":1.50,"t":true,"z":null,"s":"Hello World","big":1e400,"items":[{"sku":"A","qty":2},{"sku":"B","qty":5}],"nums":[1,2,3]}""");

    [Theory]
    [InlineData("$.a = 'x'", true)]
    [InlineData("$.a = 'X'", false)]
    [InlineData("$.a in ('q', 'x')", true)]
    [InlineData("$.s ^= 'Hello'", true)]
    [InlineData("$.s ^= 'hello'", false)]
    [InlineData("$.s ~ 'WORLD'", true)]
    [InlineData("$.s ~ 'nope'", false)]
    [InlineData("$.n = 42", true)]
    [InlineData("$.n = 42.0", true)]
    [InlineData("$.n = 43", false)]
    [InlineData("$.n < 43", true)]
    [InlineData("$.n < 42", false)]
    [InlineData("$.n <= 42", true)]
    [InlineData("$.n > 41", true)]
    [InlineData("$.n > 42", false)]
    [InlineData("$.n >= 42", true)]
    [InlineData("$.n in (1, 42)", true)]
    [InlineData("$.f = 1.5", true)]
    [InlineData("$.f = 1.50", true)]
    [InlineData("$.f = 1.51", false)]
    [InlineData("$.t = true", true)]
    [InlineData("$.t = false", false)]
    [InlineData("$.t in (false, true)", true)]
    [InlineData("$.z = null", true)]
    [InlineData("$.a = null", false)]
    [InlineData("$.missing = null", false)]
    [InlineData("$.t = 1", false)]
    [InlineData("$.n = '42'", false)]
    [InlineData("$.a = 42", false)]
    [InlineData("$.n = true", false)]
    [InlineData("$.nope = 'x'", false)]
    [InlineData("$.nope.deeper = 'x'", false)]
    [InlineData("$.a.deeper = 'x'", false)]
    [InlineData("$.items[0].sku = 'A'", true)]
    [InlineData("$.items[1].qty = 5", true)]
    [InlineData("$.items[2].sku = 'A'", false)]
    [InlineData("$.nums[*] = 3", true)]
    [InlineData("$.nums[*] = 4", false)]
    [InlineData("$.nums[*] >= 3", true)]
    [InlineData("$.nums[*] > 3", false)]
    [InlineData("$.items[*].sku = 'B'", true)]
    [InlineData("$.items[*].sku = 'C'", false)]
    [InlineData("$.items[*].sku in ('A-1', 'B')", true)]
    [InlineData("$.A = 'x'", false)]
    [InlineData("$.a[0] = 'x'", false)]
    [InlineData("$.items[*] = 'x'", false)]
    [InlineData("$.n in ('x', 42, null)", true)]
    public void A_path_compares_the_leaves_it_selects_by_their_own_type(string text, bool expected)
    {
        Check(text, Document, expected ? TriState.True : TriState.False);
    }

    [Fact]
    public void The_root_is_a_path_too()
    {
        Check("$ = 'x'", Json("\"x\""), TriState.True);
        Check("$ = 7", Json("7"), TriState.True);
        Check("$ = null", Json("null"), TriState.True);
        Check("$[1] = 2", Json("[1,2,3]"), TriState.True);
        Check("$[*] = 3", Json("[1,2,3]"), TriState.True);
    }

    [Fact]
    public void A_number_too_large_to_compare_is_unknown_unless_another_leaf_decides()
    {
        Check("$.big = 1", Document, TriState.Unknown, KafkaUnknownReason.NumberOutOfRange);
        Check("$.big > 1", Document, TriState.Unknown, KafkaUnknownReason.NumberOutOfRange);
        Check("$.big = 'x'", Document, TriState.False);
        Check("$.nums[*] = 2", Json("""{"nums":[1e400,2]}"""), TriState.True);
        Check("$.nums[*] = 9", Json("""{"nums":[1e400,2]}"""), TriState.Unknown, KafkaUnknownReason.NumberOutOfRange);
    }

    [Fact]
    public void A_selection_is_existential_and_not_negates_the_whole_clause()
    {
        Check("$.items[*].sku = 'A'", Document, TriState.True);
        Check("not $.items[*].sku = 'A'", Document, TriState.False);
        Check("not $.items[*].sku = 'C'", Document, TriState.True);
        Check("$.items[*].sku = 'A' and $.items[*].qty = 5", Document, TriState.True);
        Check("not $.big = 1", Document, TriState.Unknown, KafkaUnknownReason.NumberOutOfRange);
    }

    [Fact]
    public void A_member_name_is_compared_exactly_and_a_duplicate_member_is_selected_too()
    {
        Check("$.a = 'x'", Json("""{"A":"y","a":"x"}"""), TriState.True);
        Check("$.a = 'x'", Json("""{"A":"x"}"""), TriState.False);
        Check("$.a = 'x'", Json("""{"a":"y","a":"x"}"""), TriState.True);
        Check("$['customer-email'] = 'e'", Json("""{"customer-email":"e"}"""), TriState.True);
        Check("$['$type'] = 'T'", Json("""{"$type":"T"}"""), TriState.True);
    }

    [Fact]
    public void Any_reads_every_string_in_the_value_but_no_member_name_and_no_number()
    {
        WireRecord record = Json("""{"nested":{"deep":["one","two"]},"n":12345,"flag":true}""");

        Check("any = 'two'", record, TriState.True);
        Check("any ~ 'ONE'", record, TriState.True);
        Check("any ^= 'tw'", record, TriState.True);
        Check("any = 'nested'", record, TriState.False);
        Check("any ~ '2345'", record, TriState.False);
        Check("any ~ 'true'", record, TriState.False);
        Check("any in ('q', 'one')", record, TriState.True);
    }

    [Fact]
    public void Any_is_unknown_only_when_nothing_is_true_and_something_could_not_be_judged()
    {
        WireRecord record = Json("not json", key: "needle");

        Check("any = 'needle'", record, TriState.True, decoder: KafkaDecoders.Json);
        Check("any = 'other'", record, TriState.Unknown, KafkaUnknownReason.InvalidJson, decoder: KafkaDecoders.Json);
    }

    // ---- CloudEvents: the value is the data

    [Fact]
    public void A_binary_mode_event_has_its_value_as_data()
    {
        WireRecord record = Json("""{"orderId":7,"customer":{"id":"c1"}}""", "k", ("ce_specversion", "1.0"), ("ce_type", "t"), ("content-type", "application/json"));

        Check("$.orderId = 7", record, TriState.True);
        Check("$.customer.id = 'c1'", record, TriState.True);
        Check("ce.type = 't'", record, TriState.True);
        Check("any = 'c1'", record, TriState.True);
    }

    [Fact]
    public void A_structured_event_has_its_data_as_the_value_and_its_envelope_is_for_attributes()
    {
        WireRecord record = Json("""{"specversion":"1.0","type":"order.placed","id":"e-1","data":{"orderId":7,"customer":{"id":"c1"}}}""", "k", Structured);

        Check("$.orderId = 7", record, TriState.True);
        Check("$.customer.id = 'c1'", record, TriState.True);
        Check("$.data.orderId = 7", record, TriState.False);
        Check("$.type = 'order.placed'", record, TriState.False);
        Check("ce.type = 'order.placed'", record, TriState.True);
        Check("""value = '{"orderId":7,"customer":{"id":"c1"}}'""", record, TriState.True);
        Check("any = 'c1'", record, TriState.True);
        Check("ce.type = 'order.placed' and $.orderId = 7", record, TriState.True);
    }

    [Fact]
    public void A_structured_event_reads_data_base64_by_its_content_type()
    {
        string json = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"orderId":8}"""));
        string text = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello"));
        string bytes = Convert.ToBase64String([0xff, 0xfe]);

        Check("$.orderId = 8", Json($$"""{"specversion":"1.0","datacontenttype":"application/json","data_base64":"{{json}}"}""", "k", Structured), TriState.True);
        Check("value = 'hello'", Json($$"""{"specversion":"1.0","datacontenttype":"text/plain","data_base64":"{{text}}"}""", "k", Structured), TriState.True);
        Check("value ~ 'x'", Json($$"""{"specversion":"1.0","datacontenttype":"application/octet-stream","data_base64":"{{bytes}}"}""", "k", Structured), TriState.Unknown, KafkaUnknownReason.ValueNotText);
        Check("value ~ 'x'", Json("""{"specversion":"1.0","datacontenttype":"application/json","data_base64":"!!not base64!!"}""", "k", Structured), TriState.Unknown, KafkaUnknownReason.InvalidBase64);
        Check("$.a = 1", Json($$"""{"specversion":"1.0","datacontenttype":"application/json","data_base64":"{{text}}"}""", "k", Structured), TriState.Unknown, KafkaUnknownReason.InvalidJson);
    }

    [Fact]
    public void A_structured_event_with_text_data_is_text_and_without_data_has_no_value()
    {
        Check("value = 'hello'", Json("""{"specversion":"1.0","datacontenttype":"text/plain","data":"hello"}""", "k", Structured), TriState.True);
        Check("$.a = 1", Json("""{"specversion":"1.0","datacontenttype":"text/plain","data":"hello"}""", "k", Structured), TriState.False);

        WireRecord noData = Json("""{"specversion":"1.0","type":"t"}""", "k", Structured);
        Check("value ~ ''", noData, TriState.False);
        Check("value = ''", noData, TriState.False);
        Check("$.a = 1", noData, TriState.False);
        Check("any ~ 'type'", noData, TriState.False);
        Check("ce.type = 't'", noData, TriState.True);
    }

    [Fact]
    public void A_clause_on_a_batch_is_true_if_it_is_true_of_any_event()
    {
        WireRecord batch = Json(
            """[{"specversion":"1.0","type":"a","data":{"id":1,"name":"one"}},{"specversion":"1.0","type":"b","data":{"id":2,"name":"two"}}]""",
            "k",
            Batch);

        Check("$.id = 2", batch, TriState.True);
        Check("$.id = 1", batch, TriState.True);
        Check("$.id = 3", batch, TriState.False);
        Check("any = 'two'", batch, TriState.True);
        Check("value ~ 'two'", batch, TriState.True);
        Check("not $.id = 3", batch, TriState.True);
    }

    [Fact]
    public void A_declared_cloudevents_topic_reads_the_data_of_every_value()
    {
        Check("$.orderId = 7", Json("""{"specversion":"1.0","data":{"orderId":7}}"""), TriState.True, decoder: KafkaDecoders.CloudEvents);
        Check("$.orderId = 7", Json("""{"hello":"world"}"""), TriState.Unknown, KafkaUnknownReason.NotACloudEvent, decoder: KafkaDecoders.CloudEvents);
    }

    // ---- the grant

    [Fact]
    public void Under_the_envelope_grant_a_value_clause_does_not_compile_and_any_does_not_read_the_value()
    {
        Compile("$.a = 'x'", grant: KafkaReadGrant.Envelope).Errors.Single().Message.Should()
            .Be("$.a reads the record's value, which needs ReadPayloads (KafkaStudioOptions.Capabilities.ReadPayloads).");
        Compile("value ~ 'x'", grant: KafkaReadGrant.Envelope).Errors.Single().Message.Should()
            .Be("value reads the record's value, which needs ReadPayloads (KafkaStudioOptions.Capabilities.ReadPayloads).");

        Run("any ~ 'x'", Json("""{"a":"x"}"""), grant: KafkaReadGrant.Envelope).Verdict.Should().Be(TriState.False, "any widens over the value only under the payload grant");
        Outcome envelope = Run("any ~ 'x'", Json("""{"a":"x"}"""), grant: KafkaReadGrant.Envelope);
        envelope.Counters.ValueDetected.Should().Be(0);
        envelope.Counters.ValueParsed.Should().Be(0);
    }

    // ---- compile-time refusals

    [Theory]
    [InlineData("value < 1", "'<' cannot be used on value (position 1): numbers compare only JSON paths.")]
    [InlineData("value >= 'a'", "'>=' cannot be used on value (position 1): numbers compare only JSON paths.")]
    [InlineData("value = 5", "Put the value at position 9 in quotes: value compares as text.")]
    [InlineData("value in ('a', true)", "Put the value at position 16 in quotes: value compares as text.")]
    [InlineData("$.a is null", "'is null' cannot be used on $.a (position 1): write $.a = null for a JSON null.")]
    [InlineData("$.a is not null", "'is not null' cannot be used on $.a (position 1): write $.a = null for a JSON null.")]
    [InlineData("$.a < 'x'", "'<' cannot be used on $.a (position 1): it compares numbers, and the value at position 7 is not one.")]
    [InlineData("$.a >= true", "'>=' cannot be used on $.a (position 1): it compares numbers, and the value at position 8 is not one.")]
    [InlineData("$.a ^= 5", "'^=' cannot be used on $.a (position 1): it compares text, and the value at position 8 is not a string.")]
    [InlineData("$.a ~ null", "'~' cannot be used on $.a (position 1): it compares text, and the value at position 7 is not a string.")]
    [InlineData("any is null", "'is null' cannot be used on any (position 1): is null applies to key, header.<name>, ce.<attribute> and value.")]
    public void A_clause_the_value_cannot_answer_is_refused_with_its_reason(string text, string message)
    {
        Compile(text).Errors.Single().Message.Should().Be(message);
    }

    [Fact]
    public void A_refusal_never_repeats_a_literal()
    {
        Compile("$.a < 'SECRET-literal'").Errors.Single().Message.Should().NotContain("SECRET");
        Compile("value = 12345").Errors.Single().Message.Should().NotContain("12345");
    }

    [Fact]
    public void The_cost_classes_put_value_clauses_after_every_cheaper_one()
    {
        // key (0) < header bytes (1) < ce binary (2) < ~ on key (3) < ce structured (4) < any (5) < value (6) < path (7) < any under payload (8)
        WireRecord record = Json("""{"a":1}""", "no-match", ("h", "no-match"));

        Outcome outcome = Run("$.a = 1 and value ~ 'x' and header.h = 'y' and key = 'z'", record);

        outcome.Verdict.Should().Be(TriState.False);
        outcome.Counters.ValueDetected.Should().Be(0, "the key decided the record before any value clause ran");
    }

    // ---- hosts

    private sealed class FixedDecoder(KafkaDecodeResult result) : IKafkaRecordDecoder
    {
        public string Name => "Fixed";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => result;
    }

    private sealed class ThrowingDecoder : IKafkaRecordDecoder
    {
        public string Name => "Throwing";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => throw new InvalidOperationException("boom");
    }

    private sealed class RefusingDecoder : IKafkaRecordDecoder
    {
        public string Name => "Refusing";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => KafkaDecodeResult.Undecodable("no schema for this id");
    }
}
