using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Redaction;

/// <summary>
/// Redaction inside the engine, before any sink (D40, decision 10): the built-in deny-list masks its five headers in every match, a rule
/// masks its field, no match exists without a redactor, a match keeps no unredacted copy, and the caps hold. The searchability of a masked
/// field is the predicate tests'.
/// </summary>
public class RedactionTests
{
    private const string Secret = "s3cr3t-value";

    private static TopicScanContext Topic(KafkaRedactionRules? rules = null, KafkaKeyFormat? format = null, IKafkaRecordDecoder? decoder = null)
    {
        KafkaTopicBuilder builder = new() { Rules = rules };

        if (format is not null)
        {
            builder.Key(format);
        }

        KafkaTopicDescriptor descriptor = new("t", builder);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            null,
            new PredicateCompileContext("t", descriptor.KeyFormat, descriptor.KeyFormatDeclared, new KafkaRedactor(rules), KafkaReadGrant.Envelope, false));

        return new TopicScanContext("t", descriptor, new KafkaRedactor(rules), compiled.Predicate!, decoder);
    }

    private static KafkaMatch Match(
        WireRecord record,
        TopicScanContext? topic = null,
        KafkaReadGrant grant = KafkaReadGrant.Envelope,
        Action<KafkaScanLimits>? limits = null)
    {
        KafkaScanLimits caps = new();
        limits?.Invoke(caps);
        return KafkaMatchFactory.Create(record, topic ?? Topic(), grant, caps, new DecodeCounters());
    }

    private static WireRecord Record(byte[]? key, byte[]? value, params (string Name, string? Value)[] headers) =>
        new("t", 3, 99, key, value, [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))], 1_700_000_000_000, WireTimestampType.CreateTime, 4);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_five_deny_listed_headers_are_masked_in_every_match_whatever_the_case()
    {
        KafkaMatch match = Match(Record(
            Bytes("k"),
            Bytes("v"),
            ("Authorization", "Bearer " + Secret),
            ("proxy-authorization", Secret),
            ("COOKIE", Secret),
            ("Set-Cookie", Secret),
            ("x-api-key", Secret),
            ("x-tenant", "acme")));

        match.Headers.Where(static x => x.Name != "x-tenant").Should().OnlyContain(static x => x.Value.IsRedacted && x.Value.RedactedBy == "the built-in header deny-list");
        match.Headers.Single(static x => x.Name == "x-tenant").Value.Text.Should().Be("acme");
        match.Headers.Select(static x => x.Name).Should().Equal("Authorization", "proxy-authorization", "COOKIE", "Set-Cookie", "x-api-key", "x-tenant");
        match.Headers[0].Value.Display.Should().Be("••• (redacted by the built-in header deny-list)");
        JsonSerializer.Serialize(match).Should().NotContain(Secret);
    }

    [Fact]
    public void A_deny_listed_header_with_a_null_value_is_null_not_masked()
    {
        Match(Record(Bytes("k"), Bytes("v"), ("authorization", null))).Headers.Single().Value.IsNull.Should().BeTrue();
    }

    [Fact]
    public void A_host_opts_a_deny_listed_header_out_by_name_and_only_that_one()
    {
        KafkaRedactionRules rules = new();
        rules.AllowHeader("cookie");

        KafkaMatch match = Match(Record(Bytes("k"), Bytes("v"), ("Cookie", "a=b"), ("authorization", Secret)), Topic(rules));

        match.Headers.Single(static x => x.Name == "Cookie").Value.Text.Should().Be("a=b");
        match.Headers.Single(static x => x.Name == "authorization").Value.IsRedacted.Should().BeTrue();
    }

    [Fact]
    public void A_key_rule_masks_the_key_and_keeps_not_even_its_length()
    {
        KafkaRedactionRules rules = new();
        rules.Key();

        KafkaMatch match = Match(Record(Bytes(Secret), Bytes("v")), Topic(rules));

        match.Key.IsRedacted.Should().BeTrue();
        match.Key.RedactedBy.Should().Be("key");
        match.Key.ByteLength.Should().Be(0);
        match.Key.Text.Should().BeNull();
        JsonSerializer.Serialize(match).Should().NotContain(Secret);
    }

    [Fact]
    public void A_header_rule_masks_that_header_whatever_its_case()
    {
        KafkaRedactionRules rules = new();
        rules.Header("x-email");

        KafkaMatch match = Match(Record(Bytes("k"), Bytes("v"), ("x-email", Secret), ("X-Email", "visible")), Topic(rules));

        match.Headers[0].Value.RedactedBy.Should().Be("header x-email");
        match.Headers[1].Value.RedactedBy.Should().Be("header x-email", "redaction fails closed: a rule's name is compared ignoring case (decision 4)");
        JsonSerializer.Serialize(match).Should().NotContain("visible");
    }

    [Fact]
    public void A_cloudevents_rule_masks_the_attribute_and_the_ce_header_that_carries_it()
    {
        KafkaRedactionRules rules = new();
        rules.CloudEvent("subject");

        KafkaMatch match = Match(Record(Bytes("k"), Bytes("{}"), ("ce_specversion", "1.0"), ("ce_type", "t"), ("ce_subject", Secret)), Topic(rules));

        match.CloudEventAttributes.Single(static x => x.Name == "subject").Value.IsRedacted.Should().BeTrue();
        match.CloudEventAttributes.Single(static x => x.Name == "type").Value.Text.Should().Be("t");
        match.Headers.Single(static x => x.Name == "ce_subject").Value.IsRedacted.Should().BeTrue("the same attribute travels as a header in binary mode");
        JsonSerializer.Serialize(match).Should().NotContain(Secret);
    }

    [Fact]
    public void A_structured_cloudevent_keeps_only_its_data_so_a_masked_attribute_is_not_in_the_value()
    {
        KafkaRedactionRules rules = new();
        rules.CloudEvent("subject");
        string envelope = $$$"""{"specversion":"1.0","type":"t","subject":"{{{Secret}}}","data":{"a":1}}""";

        KafkaMatch withheld = Match(
            Record(Bytes("k"), Bytes(envelope), ("content-type", "application/cloudevents+json")),
            Topic(rules),
            KafkaReadGrant.Envelope | KafkaReadGrant.Payload);

        withheld.ValueText.Should().Be("""{"a":1}""", "the value of a CloudEvent is its data: the envelope that holds the masked attribute is not kept");
        withheld.ValueWithheldBy.Should().BeNull();
        withheld.CloudEventAttributes.Single(static x => x.Name == "subject").Value.IsRedacted.Should().BeTrue();
        JsonSerializer.Serialize(withheld).Should().NotContain(Secret);

        KafkaMatch unmasked = Match(
            Record(Bytes("k"), Bytes(envelope), ("content-type", "application/cloudevents+json")),
            Topic(),
            KafkaReadGrant.Envelope | KafkaReadGrant.Payload);

        unmasked.ValueText.Should().Be("""{"a":1}""");
        unmasked.ValueWithheldBy.Should().BeNull();
    }

    [Fact]
    public void Under_the_envelope_grant_a_match_holds_the_values_kind_and_length_and_nothing_of_it()
    {
        KafkaMatch match = Match(Record(Bytes("k"), Bytes($$"""{"password":"{{Secret}}"}""")));

        match.ValueKind.Should().Be(KafkaValueKind.Json);
        match.ValueFormat.Should().Be("JSON");
        match.ValueLength.Should().Be(Bytes($$"""{"password":"{{Secret}}"}""").Length);
        match.ValueText.Should().BeNull();
        match.ValuePreview.Should().BeNull();
        JsonSerializer.Serialize(match).Should().NotContain(Secret);
    }

    [Fact]
    public void Under_the_payload_grant_the_value_is_kept_up_to_the_match_cap_and_flagged_when_cut()
    {
        KafkaMatch whole = Match(Record(Bytes("k"), Bytes("""{"a":1}""")), grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload);
        whole.ValueText.Should().Be("""{"a":1}""");
        whole.ValueTruncated.Should().BeFalse();

        KafkaMatch cut = Match(
            Record(Bytes("k"), Bytes("{\"a\":\"" + new string('x', 5_000) + "\"}")),
            grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload,
            limits: static l => l.MaxMatchBytes = 1_024);
        cut.ValueTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(cut.ValueText!).Should().BeLessThanOrEqualTo(1_024);
        cut.ValueLength.Should().BeGreaterThan(5_000);
    }

    [Fact]
    public void A_match_caps_the_key_at_1_KiB_a_header_value_at_4_KiB_and_the_headers_at_64()
    {
        string longKey = new('k', 5_000);
        List<(string, string?)> headers = [("big", new string('h', 10_000))];

        for (int i = 0; i < 70; i++)
        {
            headers.Add(("h" + i, "v" + i));
        }

        KafkaMatch match = Match(Record(Bytes(longKey), Bytes("v"), [.. headers]));

        match.Key.Truncated.Should().BeTrue();
        match.Key.Text!.Length.Should().Be(KafkaMatchFactory.MaxKeyBytes);
        match.Key.ByteLength.Should().Be(5_000);
        match.Headers.Should().HaveCount(KafkaMatchFactory.MaxHeaders);
        match.HeadersOmitted.Should().Be(71 - 64);
        match.Headers[0].Value.Truncated.Should().BeTrue();
        match.Headers[0].Value.Text!.Length.Should().Be(KafkaMatchFactory.MaxHeaderBytes);
        match.Headers[0].Value.ByteLength.Should().Be(10_000);
    }

    [Fact]
    public void A_cloudevents_attribute_is_capped_at_1_KiB()
    {
        KafkaMatch match = Match(Record(Bytes("k"), Bytes("{}"), ("ce_specversion", "1.0"), ("ce_subject", new string('s', 3_000))));

        match.CloudEventAttributes.Single(static x => x.Name == "subject").Value.Text!.Length.Should().Be(KafkaMatchFactory.MaxAttributeBytes);
    }

    [Fact]
    public void A_match_copies_what_it_keeps_and_never_pins_the_wire_records_arrays()
    {
        byte[] key = Bytes("the-key");
        WireRecord record = Record(key, Bytes("v"), ("h", "value"));

        KafkaMatch match = Match(record);
        key[0] = (byte) 'X';

        match.Key.Text.Should().Be("the-key");
        typeof(KafkaMatch).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(WireRecord)).And.NotContain(typeof(byte[]));
        typeof(KafkaMatchField).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(byte[]));
    }

    [Fact]
    public void A_key_that_is_not_text_in_its_format_is_shown_as_hex_a_null_key_as_null_and_the_format_says_assumed()
    {
        KafkaMatch hex = Match(Record([0xff, 0x00, 0x01], Bytes("v")), Topic(format: KafkaKeyFormat.Int64BigEndian));
        hex.Key.Text.Should().Be("0xff0001");
        hex.KeyFormat.Should().Be("Int64BigEndian");

        Match(Record(null, Bytes("v"))).Key.IsNull.Should().BeTrue();
        Match(Record(Bytes("k"), Bytes("v"))).KeyFormat.Should().Be("Utf8String (assumed)");
        Match(Record(Bytes("k"), null)).ValueKind.Should().Be(KafkaValueKind.Tombstone);
    }

    [Fact]
    public void A_match_carries_the_records_coordinates_and_its_trace_context()
    {
        KafkaMatch match = Match(Record(Bytes("k"), Bytes("v"), ("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")));

        (match.Topic, match.Partition, match.Offset, match.LeaderEpoch).Should().Be(("t", 3, 99L, (int?) 4));
        match.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        match.Trace.Should().NotBeNull();
        match.Trace!.TraceId.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
    }

    [Fact]
    public void There_is_no_match_without_a_redactor()
    {
        KafkaTopicDescriptor descriptor = new("t", null);
        PredicateCompilation compiled = PredicateCompiler.Compile(null, new PredicateCompileContext("t", descriptor.KeyFormat, false, KafkaRedactor.DenyListOnly, KafkaReadGrant.Envelope, false));

        Action withoutOne = () => _ = new TopicScanContext("t", descriptor, null!, compiled.Predicate!, null);

        withoutOne.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Only_the_match_factory_constructs_a_match()
    {
        Regex construction = new(@"\bnew\s+KafkaMatch\s*(\(|\{|$)", RegexOptions.Compiled | RegexOptions.Multiline, TimeSpan.FromSeconds(5));
        string root = RepositoryRoot.Combine("src", "KStudio.Core");

        string[] offenders =
        [
            .. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Where(file => construction.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file))))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().Equal("src/KStudio.Core/Scanning/KafkaMatch.cs");
        construction.IsMatch("var m = new KafkaMatch { Topic = 1 };").Should().BeTrue("the scan can see the construction it forbids");
    }

    [Fact]
    public void The_deny_list_is_exactly_the_five_names()
    {
        HeaderDenyList.Names.Should().BeEquivalentTo("authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key");
        HeaderDenyList.Contains("X-API-KEY").Should().BeTrue();
        HeaderDenyList.Contains("x-tenant").Should().BeFalse();
    }
}
