using System.Buffers.Binary;
using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Wire;

namespace KStudio.Tests.Decoding;

/// <summary>
/// The value-side detection order of decision 8.1, the header decoding of 8.5, the caps of 8.3 and the CloudEvents reader: tombstones and
/// empty values are never empty strings, nothing is deserialized into a CLR type, and under the envelope grant nothing of the value is kept
/// beyond its kind, its length and its CloudEvents attributes.
/// </summary>
public class DecodingTests
{
    private const KafkaReadGrant Envelope = KafkaReadGrant.Envelope;
    private const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;

    private static WireRecord Record(byte[]? value, params (string Name, string? Value)[] headers) =>
        new(
            "t", 0, 0, null, value,
            [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
            0, WireTimestampType.CreateTime, null);

    private static WireRecord Text(string? value, params (string Name, string? Value)[] headers) =>
        Record(value is null ? null : Encoding.UTF8.GetBytes(value), headers);

    private static KafkaDecodedValue Detect(
        WireRecord record,
        KafkaReadGrant grant = Payload,
        IKafkaRecordDecoder? decoder = null,
        int maxDecode = 1024 * 1024,
        int maxMatch = 64 * 1024,
        Action<Exception>? failed = null,
        DecodeCounters? counters = null) =>
        ValueDetector.Detect(record, new ValueDetection(grant, decoder, maxDecode, maxMatch, counters ?? new DecodeCounters(), failed));

    [Fact]
    public void A_null_value_is_a_tombstone_and_a_zero_length_value_is_empty_never_an_empty_string()
    {
        KafkaDecodedValue tombstone = Detect(Text(null));
        KafkaDecodedValue empty = Detect(Text(string.Empty));

        tombstone.Kind.Should().Be(KafkaValueKind.Tombstone);
        tombstone.Text.Should().BeNull();
        empty.Kind.Should().Be(KafkaValueKind.Empty);
        empty.FormatName.Should().Be("Empty (0 bytes)");
        empty.Text.Should().BeNull();
        empty.Length.Should().Be(0);
    }

    [Fact]
    public void The_detection_order_is_cloudevents_binary_then_structured_then_the_declared_decoder_then_wire_format_then_json_then_text_then_hex()
    {
        // 2. CloudEvents binary wins over everything below it, including a value that starts like the wire format.
        Detect(Record([0, 0, 0, 0, 7, 1], ("ce_specversion", "1.0"))).FormatName.Should().StartWith("CloudEvents (binary)");

        // 3. Structured by content type.
        Detect(Text("""{"specversion":"1.0","type":"a","data":{"x":1}}""", ("content-type", "application/cloudevents+json"))).FormatName.Should().Be("CloudEvents (structured), data as JSON");
        Detect(Text("""{"specversion":"1.0","type":"a","data":{"x":1}}""", ("content-type", "application/cloudevents+json")), Envelope).FormatName.Should().Be("CloudEvents (structured)");

        // 4. The declared decoder wins over the wire format, JSON and text.
        FixedDecoder decoder = new(KafkaDecodeResult.Text("decoded", "Fixed"));
        KafkaDecodedValue declared = Detect(Text("""{"a":1}"""), decoder: decoder);
        declared.FormatName.Should().Be("Fixed");
        declared.Text.Should().Be("decoded");

        // 5. The wire format.
        KafkaDecodedValue wire = Detect(Record([0, 0, 0, 0, 42, 0xde, 0xad]));
        wire.Kind.Should().Be(KafkaValueKind.ConfluentWireFormat);
        wire.SchemaId.Should().Be(42);
        wire.FormatName.Should().Be("Confluent wire format, schema id 42");
        wire.Preview.Should().Be("0xdead");

        // 6. JSON, 7. text, 8. hex.
        Detect(Text("""{"a":[1,2]}""")).Kind.Should().Be(KafkaValueKind.Json);
        Detect(Text("  [1,2,3]")).Kind.Should().Be(KafkaValueKind.Json);
        Detect(Text("\"quoted\"")).Kind.Should().Be(KafkaValueKind.Json);
        Detect(Text("true")).Kind.Should().Be(KafkaValueKind.Json);
        Detect(Text("plain words, not json")).Kind.Should().Be(KafkaValueKind.Text);
        Detect(Text("{ broken json")).Kind.Should().Be(KafkaValueKind.Text, "it does not parse, but it is text");
        KafkaDecodedValue binary = Detect(Record([0xff, 0xfe, 0x01, 0x02]));
        binary.Kind.Should().Be(KafkaValueKind.Binary);
        binary.Preview.Should().Be("0xfffe0102");
    }

    [Fact]
    public void A_value_with_a_control_character_other_than_tab_cr_and_lf_is_binary_not_text()
    {
        Detect(Text("line one\r\n\tline two")).Kind.Should().Be(KafkaValueKind.Text);
        Detect(Text("bell\a")).Kind.Should().Be(KafkaValueKind.Binary);
    }

    [Fact]
    public void A_zero_first_byte_with_fewer_than_five_bytes_is_not_the_wire_format()
    {
        Detect(Record([0, 1, 2, 3])).Kind.Should().Be(KafkaValueKind.Binary);
    }

    [Fact]
    public void Under_the_envelope_grant_only_the_kind_and_length_are_kept_never_the_text_or_the_preview()
    {
        KafkaDecodedValue json = Detect(Text("""{"password":"s3cr3t"}"""), Envelope);
        json.Kind.Should().Be(KafkaValueKind.Json);
        json.Length.Should().Be(21);
        json.Text.Should().BeNull();
        json.Preview.Should().BeNull();

        Detect(Text("words"), Envelope).Text.Should().BeNull();
        Detect(Record([0xff, 0xfe]), Envelope).Preview.Should().BeNull();

        KafkaDecodedValue wire = Detect(Record([0, 0, 0, 0, 9, 1, 2, 3]), Envelope);
        wire.SchemaId.Should().Be(9);
        wire.Preview.Should().BeNull();
    }

    [Fact]
    public void Under_the_envelope_grant_no_declared_decoder_runs()
    {
        FixedDecoder decoder = new(KafkaDecodeResult.Text("decoded"));

        Detect(Text("plain"), Envelope, decoder);

        decoder.Calls.Should().Be(0);
    }

    [Fact]
    public void The_declared_decoder_gets_the_record_and_its_json_or_text_is_parsed_by_the_engine_under_its_caps()
    {
        FixedDecoder json = new(KafkaDecodeResult.Json(Encoding.UTF8.GetBytes("""{"decoded":true}"""), "Avro (acme)"));
        KafkaDecodedValue result = Detect(Record([1, 2, 3], ("h", "v")), decoder: json);

        result.Kind.Should().Be(KafkaValueKind.Json);
        result.FormatName.Should().Be("Avro (acme)");
        result.Text.Should().Be("""{"decoded":true}""");
        result.Length.Should().Be(3, "the length is of the record's value, not of the decoder's output");
        json.Last!.Headers.Should().ContainSingle().Which.Name.Should().Be("h");
        json.Last.Value.ToArray().Should().Equal(1, 2, 3);

        KafkaDecodedValue notJson = Detect(Text("x"), decoder: new FixedDecoder(KafkaDecodeResult.Json(Encoding.UTF8.GetBytes("{ nope"))));
        notJson.Kind.Should().Be(KafkaValueKind.Undecodable);
        notJson.Reason.Should().Be(KafkaUndecodableReason.InvalidJson);

        Detect(Text("plain"), decoder: new FixedDecoder(KafkaDecodeResult.NotApplicable)).Kind.Should().Be(KafkaValueKind.Text, "NotApplicable falls through to the next step");
    }

    [Fact]
    public void A_decoder_that_throws_or_refuses_is_undecodable_and_never_takes_the_scan_down()
    {
        List<Exception> failures = [];
        KafkaDecodedValue thrown = Detect(Text("x"), decoder: new ThrowingDecoder(), failed: failures.Add);

        thrown.Kind.Should().Be(KafkaValueKind.Undecodable);
        thrown.Reason.Should().Be(KafkaUndecodableReason.DecoderFailed);
        failures.Should().ContainSingle().Which.Should().BeOfType<InvalidOperationException>();

        KafkaDecodedValue refused = Detect(Text("x"), decoder: new FixedDecoder(KafkaDecodeResult.Undecodable("unknown schema 7")));
        refused.Reason.Should().Be(KafkaUndecodableReason.DecoderRefused);
        refused.ReasonText.Should().Be("unknown schema 7");

        string longReason = new('r', 500);
        Detect(Text("x"), decoder: new FixedDecoder(KafkaDecodeResult.Undecodable(longReason))).ReasonText!.Length.Should().Be(200);
    }

    [Fact]
    public void The_built_in_decoders_force_a_format_when_a_host_names_one()
    {
        Detect(Text("not json at all"), decoder: KafkaDecoders.Json).Reason.Should().Be(KafkaUndecodableReason.InvalidJson);
        Detect(Text("""{"a":1}"""), decoder: KafkaDecoders.Json).Kind.Should().Be(KafkaValueKind.Json);
        Detect(Record([0xff, 0xfe]), decoder: KafkaDecoders.Text).Reason.Should().Be(KafkaUndecodableReason.InvalidUtf8);
        Detect(Text("""{"a":1}"""), decoder: KafkaDecoders.Text).Kind.Should().Be(KafkaValueKind.Text, "forced text is text even when it parses as JSON");

        KafkaDecodedValue ce = Detect(Text("""{"specversion":"1.0","type":"t","data":{"a":1}}"""), decoder: KafkaDecoders.CloudEvents);
        ce.FormatName.Should().Be("CloudEvents (structured), data as JSON");
        ce.Text.Should().Be("""{"a":1}""", "the value of a CloudEvent is its data");
        ce.CloudEvent!.Events.Single().Attributes["type"].Should().Be("t");

        KafkaDecoders.BuiltInKind(new FixedDecoder(KafkaDecodeResult.NotApplicable)).Should().BeNull();
    }

    [Fact]
    public void A_declared_cloudevents_topic_whose_value_has_no_specversion_is_undecodable_as_not_a_cloudevent()
    {
        KafkaDecodedValue notEvent = Detect(Text("""{"hello":"world"}"""), decoder: KafkaDecoders.CloudEvents);

        notEvent.Kind.Should().Be(KafkaValueKind.Undecodable);
        notEvent.Reason.Should().Be(KafkaUndecodableReason.NotACloudEvent);
    }

    [Fact]
    public void Binary_mode_types_the_data_by_its_content_type_header()
    {
        (string Type, KafkaValueKind Kind)[] cases =
        [
            ("application/json", KafkaValueKind.Json),
            ("application/vnd.acme+json", KafkaValueKind.Json),
            ("text/plain", KafkaValueKind.Text),
            ("application/octet-stream", KafkaValueKind.Binary),
        ];

        foreach ((string type, KafkaValueKind kind) in cases)
        {
            Detect(Text("""{"a":1}""", ("ce_specversion", "1.0"), ("content-type", type))).Kind.Should().Be(kind, type);
        }

        // Under the envelope grant the header decides and the value is not inspected.
        Detect(Text("not json at all", ("ce_specversion", "1.0"), ("content-type", "application/json")), Envelope).Kind.Should().Be(KafkaValueKind.Json);
        // With the payload grant it is, and the lie is found.
        Detect(Text("not json at all", ("ce_specversion", "1.0"), ("content-type", "application/json")), Payload).Reason.Should().Be(KafkaUndecodableReason.InvalidJson);
    }

    [Fact]
    public void Caps_a_value_larger_than_the_decode_limit_is_undecodable_as_too_large_and_a_kept_text_is_cut_at_the_match_cap()
    {
        string big = "{\"a\":\"" + new string('x', 5_000) + "\"}";

        Detect(Text(big), maxDecode: 1_000).Reason.Should().Be(KafkaUndecodableReason.TooLarge);

        KafkaDecodedValue cut = Detect(Text(big), maxMatch: 1_024);
        cut.Kind.Should().Be(KafkaValueKind.Json);
        cut.Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(cut.Text!).Should().BeLessThanOrEqualTo(1_024);
        cut.Length.Should().Be(Encoding.UTF8.GetByteCount(big), "the length is always the whole value's");
    }

    [Fact]
    public void A_kept_text_is_never_cut_in_the_middle_of_a_character()
    {
        string text = new string('é', 600);

        KafkaDecodedValue cut = Detect(Text(text), maxMatch: 1_001);

        cut.Truncated.Should().BeTrue();
        cut.Text.Should().NotContain("�");
        cut.Text!.Length.Should().Be(500, "1,001 bytes back up to 1,000 bytes of two-byte characters");
    }

    [Fact]
    public void Nesting_deeper_than_the_depth_cap_is_not_json()
    {
        string deep = string.Concat(Enumerable.Repeat("[", 100)) + string.Concat(Enumerable.Repeat("]", 100));

        Detect(Text(deep)).Kind.Should().Be(KafkaValueKind.Text, "a depth beyond 64 does not parse, so it is shown as the text it is");
    }

    [Fact]
    public void Binary_cloudevents_read_the_ce_headers_and_never_the_value()
    {
        DecodeCounters counters = new();
        WireRecord record = Text(
            "{}", ("ce_specversion", "1.0"), ("ce_id", "7"), ("ce_type", "t"), ("content-type", "application/json"));

        CloudEventView view = CloudEventReader.Read(record, CloudEventReader.Classify(record, false), 1_000, counters);

        view.Mode.Should().Be(CloudEventMode.Binary);
        view.Events.Single().Attributes.Should().Contain([KeyValuePair.Create("id", "7"), KeyValuePair.Create("type", "t"), KeyValuePair.Create("datacontenttype", "application/json")]);
        counters.CloudEventBinary.Should().Be(1);
        counters.CloudEventStructured.Should().Be(0);
    }

    [Fact]
    public void Structured_cloudevents_skip_data_read_scalars_and_ignore_nested_objects()
    {
        string json = """{"specversion":"1.0","type":"t","n":12,"ok":true,"no":false,"nothing":null,"obj":{"a":1},"arr":[1],"data":{"x":"y"},"data_base64":"AAEC"}""";
        DecodeCounters counters = new();
        WireRecord record = Text(json, ("content-type", "application/cloudevents+json"));

        CloudEventView view = CloudEventReader.Read(record, CloudEventMode.Structured, 1_000_000, counters);

        view.Unreadable.Should().BeNull();
        IReadOnlyDictionary<string, string> attributes = view.Events.Single().Attributes;
        attributes.Keys.Should().BeEquivalentTo("specversion", "type", "n", "ok", "no");
        attributes["n"].Should().Be("12");
        attributes["ok"].Should().Be("true");
        attributes["no"].Should().Be("false");
        counters.CloudEventStructured.Should().Be(1);
    }

    [Theory]
    [InlineData(null, "application/cloudevents+json", nameof(CloudEventMode.Structured))]
    [InlineData(null, "APPLICATION/CLOUDEVENTS+JSON; charset=utf-8", nameof(CloudEventMode.Structured))]
    [InlineData(null, "application/cloudevents-batch+json", nameof(CloudEventMode.StructuredBatch))]
    [InlineData(null, "application/json", nameof(CloudEventMode.None))]
    [InlineData("1.0", "application/cloudevents+json", nameof(CloudEventMode.Binary))]
    public void A_record_is_binary_when_it_has_ce_specversion_and_structured_by_its_content_type(string? specversion, string contentType, string expectedMode)
    {
        List<(string, string?)> headers = [("content-type", contentType)];

        if (specversion is not null)
        {
            headers.Add(("ce_specversion", specversion));
        }

        CloudEventReader.Classify(Text("{}", [.. headers]), declaresStructured: false).ToString().Should().Be(expectedMode);
        CloudEventReader.Classify(Text("{}"), declaresStructured: true).Should().Be(CloudEventMode.Structured);
        CloudEventReader.Classify(Text("{}"), declaresStructured: false).Should().Be(CloudEventMode.None);
    }

    [Fact]
    public void Headers_are_text_when_strict_utf8_without_controls_and_hex_otherwise()
    {
        HeaderDecoder.Display(Encoding.UTF8.GetBytes("café\twith tab")).Should().Be("café\twith tab");
        HeaderDecoder.Display(null).Should().BeNull();
        HeaderDecoder.Display([]).Should().Be(string.Empty);
        HeaderDecoder.Display([0xff, 0xfe]).Should().Be("0xfffe");
        HeaderDecoder.Display([0x01, 0x02]).Should().Be("0x0102", "a control character makes it binary");
        HeaderDecoder.Display(Encoding.UTF8.GetBytes("line\nbreak")).Should().StartWith("0x");

        HeaderDecoder.Display(new byte[200].Select(static (_, i) => (byte) (0x80 + (i % 2))).ToArray()).Should().HaveLength(2 + (64 * 2), "hex is capped at 64 bytes");
    }

    [Fact]
    public void A_w3c_traceparent_gives_a_trace_context_and_any_other_form_does_not()
    {
        const string valid = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        KafkaTraceContext? trace = HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("traceparent", Encoding.UTF8.GetBytes(valid))]);
        trace.Should().Be(new KafkaTraceContext("4bf92f3577b34da6a3ce929d0e0e4736", "00f067aa0ba902b7"));

        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("ce_traceparent", Encoding.UTF8.GetBytes(valid))]).Should().NotBeNull();
        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("parent-id", Encoding.UTF8.GetBytes(valid))]).Should().NotBeNull("Wolverine's header with the W3C form");
        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("parent-id", Encoding.UTF8.GetBytes("order-42/7"))]).Should().BeNull("any other form is shown as text only");
        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("traceparent", Encoding.UTF8.GetBytes("00-00000000000000000000000000000000-00f067aa0ba902b7-01"))]).Should().BeNull("all zeros is not a trace");
        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("traceparent", Encoding.UTF8.GetBytes("00-4BF92F3577B34DA6A3CE929D0E0E4736-00f067aa0ba902b7-01"))]).Should().BeNull("W3C ids are lower-case hex");
        HeaderDecoder.FindTrace([KeyValuePair.Create<string, byte[]?>("traceparent", null)]).Should().BeNull();
    }

    [Fact]
    public void The_confluent_wire_format_reads_a_big_endian_schema_id()
    {
        byte[] value = new byte[10];
        BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(1), 0x01020304);

        ConfluentWireFormat.TryRead(value, out int id).Should().BeTrue();
        id.Should().Be(0x01020304);
        ConfluentWireFormat.TryRead([1, 0, 0, 0, 1, 1], out _).Should().BeFalse();
        ConfluentWireFormat.TryRead([], out _).Should().BeFalse();
    }

    private sealed class FixedDecoder(KafkaDecodeResult result) : IKafkaRecordDecoder
    {
        public int Calls { get; private set; }

        public KafkaDecodeContext? Last { get; private set; }

        public string Name => "FixedDecoder";

        public KafkaDecodeResult Decode(KafkaDecodeContext context)
        {
            Calls++;
            Last = context;
            return result;
        }
    }

    private sealed class ThrowingDecoder : IKafkaRecordDecoder
    {
        public string Name => "ThrowingDecoder";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) => throw new InvalidOperationException("payload: s3cr3t");
    }
}
