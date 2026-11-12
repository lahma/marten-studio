using System.Collections;
using System.Reflection;
using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

using static KStudio.Tests.Predicates.ValueHarness;

namespace KStudio.Tests.Redaction;

/// <summary>
/// Payload redaction inside the engine, before any sink (decision 3 of D-PAYLOAD, D40): a JSON-path rule and a pattern mask parts of a
/// value, a value a rule cannot be applied to is withheld whole, and no string reachable from a match holds the raw secret. The predicate
/// side is <c>RedactedPayloadPredicateTests</c>, the scan and the logs <c>PayloadRedactionScanTests</c>.
/// </summary>
public class PayloadRedactionTests
{
    internal const string Email = "SENTINEL-7f3c2a@example.com";
    internal const string Phone = "+358 40 555 0199";
    internal const string PhonePattern = @"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}";

    internal static readonly string Document =
        $$"""{"customer":{"email":"{{Email}}","name":"Ann"},"note":"call {{Phone}} now","n":1}""";

    internal static KafkaRedactionRules SentinelRules() =>
        Rules(static r =>
        {
            r.JsonPath("$.customer.email").Searchable();
            r.Pattern(PhonePattern, "phone");
        });

    internal static TopicScanContext Topic(KafkaRedactionRules? rules, IKafkaRecordDecoder? decoder = null)
    {
        KafkaTopicBuilder builder = new() { Rules = rules };
        KafkaTopicDescriptor descriptor = new("t", builder);
        KafkaRedactor redactor = new(rules);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            null,
            new PredicateCompileContext("t", descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, Payload, KafkaDecoders.BuiltInKind(decoder) == KafkaBuiltInDecoder.CloudEvents));

        return new TopicScanContext("t", descriptor, redactor, compiled.Predicate!, decoder);
    }

    internal static KafkaMatch Match(WireRecord record, KafkaRedactionRules? rules, IKafkaRecordDecoder? decoder = null, Action<KafkaScanLimits>? limits = null, int focus = 0)
    {
        KafkaScanLimits caps = new();
        limits?.Invoke(caps);
        return KafkaMatchFactory.Create(record, Topic(rules, decoder), Payload, caps, new DecodeCounters(), focusValueBytes: focus);
    }

    /// <summary>Every string reachable from an object by its public properties, collections included.</summary>
    internal static IEnumerable<string> Strings(object? root)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        Stack<object> pending = new();

        if (root is not null)
        {
            pending.Push(root);
        }

        while (pending.Count > 0)
        {
            object current = pending.Pop();

            if (current is string text)
            {
                yield return text;
                continue;
            }

            Type type = current.GetType();

            if (type.IsPrimitive || type.IsEnum || current is DateTimeOffset or DateTime or TimeSpan or Guid || !seen.Add(current))
            {
                continue;
            }

            if (current is IEnumerable sequence)
            {
                foreach (object? item in sequence)
                {
                    if (item is not null)
                    {
                        pending.Push(item);
                    }
                }

                continue;
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0 && property.GetValue(current) is { } value)
                {
                    pending.Push(value);
                }
            }
        }
    }

    internal static void NoSentinel(object? root)
    {
        string[] strings = [.. Strings(root)];

        strings.Should().NotBeEmpty("walking nothing would pass for the wrong reason");
        strings.Should().NotContain(static x => x.Contains("SENTINEL", StringComparison.Ordinal) || x.Contains("555 0199", StringComparison.Ordinal));
    }

    private static RecordValue Detect(string json, KafkaRedactionRules? rules = null) =>
        ValueDetector.DetectValue(Json(json), new ValueDetection(Payload, null, 1024 * 1024, 64 * 1024, new DecodeCounters(), null, new KafkaRedactor(rules)));

    [Fact]
    public void A_json_path_and_a_pattern_mask_the_value_before_it_is_kept()
    {
        KafkaMatch match = Match(Json(Document), SentinelRules());

        match.ValueText.Should().Be(
            """{"customer":{"email":"••• (redacted by $.customer.email)","name":"Ann"},"note":"call ••• (redacted by pattern phone) now","n":1}""");
        match.ValueTextKind.Should().Be(KafkaValueTextKind.Json);
        match.ValueKind.Should().Be(KafkaValueKind.Json);
        match.ValueWithheldBy.Should().BeNull();
        match.MaskedPaths.Should().ContainSingle().Which.Text.Should().Be("$.customer.email");
        match.MaskedPaths[0].Rule.Should().Be("$.customer.email");
        match.MaskedPathsOmitted.Should().Be(0);
        match.ValueMaskedBy.Should().BeEquivalentTo("$.customer.email", "pattern phone");
        match.ValueLength.Should().Be(Encoding.UTF8.GetByteCount(Document), "redaction hides content, not size");
        NoSentinel(match);
    }

    [Fact]
    public void A_match_with_no_rule_keeps_the_value_as_the_producer_wrote_it()
    {
        KafkaMatch match = Match(Json("""{ "a" : 1 }"""), null);

        match.ValueText.Should().Be("""{ "a" : 1 }""");
        match.ValueTextKind.Should().Be(KafkaValueTextKind.Json);
        match.MaskedPaths.Should().BeEmpty();
        match.ValueMaskedBy.Should().BeEmpty();
    }

    [Fact]
    public void A_subtree_rule_masks_the_whole_subtree_and_a_wildcard_rule_every_element()
    {
        KafkaMatch subtree = Match(
            Json("""{"customer":{"email":"a@b","address":{"street":"Main"}},"n":1}"""),
            Rules(static r => r.JsonPath("$.customer")));

        subtree.ValueText.Should().Be("""{"customer":"••• (redacted by $.customer)","n":1}""");
        subtree.MaskedPaths.Should().ContainSingle().Which.Text.Should().Be("$.customer");

        KafkaMatch wildcard = Match(
            Json("""{"items":[{"sku":"A","secret":"s1"},{"sku":"B","secret":"s2"}]}"""),
            Rules(static r => r.JsonPath("$.items[*].secret")));

        wildcard.ValueText.Should().Be(
            """{"items":[{"sku":"A","secret":"••• (redacted by $.items[*].secret)"},{"sku":"B","secret":"••• (redacted by $.items[*].secret)"}]}""");
        wildcard.MaskedPaths.Select(static x => x.Text).Should().Equal("$.items[0].secret", "$.items[1].secret");
        wildcard.MaskedPaths.Select(static x => x.Rule).Should().OnlyContain(static x => x == "$.items[*].secret");

        KafkaMatch indexed = Match(Json("""{"items":["s0","s1","s2"]}"""), Rules(static r => r.JsonPath("$.items[1]")));
        indexed.ValueText.Should().Be("""{"items":["s0","••• (redacted by $.items[1])","s2"]}""");
    }

    [Fact]
    public void A_rule_masks_every_case_variant_of_a_member_name()
    {
        KafkaMatch match = Match(Json("""{"Customer":{"EMAIL":"SENTINEL-x"}}"""), Rules(static r => r.JsonPath("$.customer.email")));

        match.ValueText.Should().Be("""{"Customer":{"EMAIL":"••• (redacted by $.customer.email)"}}""");
        match.MaskedPaths.Should().ContainSingle().Which.Text.Should().Be("$.Customer.EMAIL");
        NoSentinel(match);
    }

    [Fact]
    public void A_rule_masks_whatever_its_node_is_a_number_an_object_or_an_array()
    {
        KafkaMatch match = Match(
            Json("""{"a":12345,"b":{"c":1},"d":[1,2],"e":true,"f":null}"""),
            Rules(static r =>
            {
                r.JsonPath("$.a");
                r.JsonPath("$.b");
                r.JsonPath("$.d");
                r.JsonPath("$.e");
                r.JsonPath("$.f");
            }));

        match.ValueText.Should().Be(
            """{"a":"••• (redacted by $.a)","b":"••• (redacted by $.b)","d":"••• (redacted by $.d)","e":"••• (redacted by $.e)","f":"••• (redacted by $.f)"}""");
    }

    [Fact]
    public void A_rule_on_the_root_masks_the_whole_value()
    {
        Match(Json("""{"a":1}"""), Rules(static r => r.JsonPath("$"))).ValueText.Should().Be("\"••• (redacted by $)\"");
    }

    [Fact]
    public void At_most_64_masked_paths_are_kept_and_the_rest_counted()
    {
        string items = string.Join(",", Enumerable.Range(0, 80).Select(static i => $"\"s{i}\""));
        KafkaMatch match = Match(Json($$"""{"items":[{{items}}]}"""), Rules(static r => r.JsonPath("$.items[*]")));

        match.MaskedPaths.Should().HaveCount(64);
        match.MaskedPathsOmitted.Should().Be(16);
        match.ValueText!.Split("redacted by $.items[*]").Length.Should().Be(81, "every one of the 80 elements is masked in the text");
    }

    [Fact]
    public void A_pattern_masks_a_number_as_a_string_and_a_member_name_it_matches_withholds_the_value()
    {
        KafkaRedactionRules card = Rules(static r => r.Pattern(@"\b[0-9]{16}\b", "card"));

        Match(Json("""{"card":4111111111111111,"n":7}"""), card).ValueText.Should().Be("""{"card":"••• (redacted by pattern card)","n":7}""");
        Match(Json("""{"note":"card 4111111111111111 and 5500005555555559"}"""), card).ValueText.Should().Be(
            """{"note":"card ••• (redacted by pattern card) and ••• (redacted by pattern card)"}""");

        KafkaMatch named = Match(Json("""{"4111111111111111":"x","n":1}"""), card);

        named.ValueText.Should().BeNull("the name is the secret");
        named.ValueWithheldBy.Should().Be("pattern card");
        NoSentinelExcept(named, "4111111111111111");
    }

    [Fact]
    public void Overlapping_matches_of_two_patterns_are_one_mask()
    {
        KafkaMatch match = Match(
            Json("""{"s":"id abcdef123456 end"}"""),
            Rules(static r =>
            {
                r.Pattern("abcdef", "letters");
                r.Pattern("def123", "mixed");
            }));

        match.ValueText.Should().Be("""{"s":"id ••• (redacted by pattern letters)456 end"}""", "the union of the two matches is masked, and what neither matched stays");
        match.ValueMaskedBy.Should().Equal("pattern letters");
    }

    [Fact]
    public void A_pattern_without_a_name_is_named_by_its_place_among_the_topics_patterns()
    {
        KafkaMatch match = Match(
            Json("""{"s":"aa bb"}"""),
            Rules(static r =>
            {
                r.Pattern("aa");
                r.Pattern("bb");
            }));

        match.ValueText.Should().Be("""{"s":"••• (redacted by pattern 1) ••• (redacted by pattern 2)"}""");
    }

    [Fact]
    public void A_text_value_has_its_pattern_matches_replaced_and_is_withheld_under_a_path_rule()
    {
        KafkaMatch text = Match(Json($"call {Phone} now"), Rules(static r => r.Pattern(PhonePattern, "phone")));

        text.ValueText.Should().Be("call ••• (redacted by pattern phone) now");
        text.ValueTextKind.Should().Be(KafkaValueTextKind.Text);
        text.MaskedPaths.Should().BeEmpty();
        text.ValueMaskedBy.Should().Equal("pattern phone");
        NoSentinel(text);

        KafkaMatch underPath = Match(Json($"call {Phone} now"), Rules(static r => r.JsonPath("$.note")));

        underPath.ValueText.Should().BeNull("a path cannot be found in text");
        underPath.ValueWithheldBy.Should().Be("$.note");
        underPath.ValuePreview.Should().BeNull();
        NoSentinelExcept(underPath, Phone);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("wire")]
    public void Bytes_are_withheld_under_any_payload_rule(string kind)
    {
        WireRecord record = kind == "binary" ? Bytes([0xff, 0xfe, 0x01, 0x02]) : Bytes([0, 0, 0, 0, 7, 0xde, 0xad]);

        foreach (KafkaRedactionRules rules in new[] { Rules(static r => r.JsonPath("$.a")), Rules(static r => r.Pattern("zzz")) })
        {
            KafkaMatch match = Match(record, rules);

            match.ValuePreview.Should().BeNull();
            match.ValueText.Should().BeNull();
            match.ValueWithheldBy.Should().NotBeNull();
            match.ValueKind.Should().NotBe(KafkaValueKind.Undecodable);
        }

        Match(record, null).ValuePreview.Should().NotBeNull("with no rule the preview is kept as before");
    }

    [Fact]
    public void A_value_with_nothing_to_mask_is_still_rewritten_compactly_and_a_tombstone_empty_or_undecodable_value_is_untouched()
    {
        KafkaRedactionRules rules = Rules(static r => r.JsonPath("$.zzz"));

        Match(Json("""{ "a" : 1 }"""), rules).ValueText.Should().Be("""{"a":1}""", "a topic with rules shows the masked form, which is written compactly");
        Match(Json(null), rules).ValueKind.Should().Be(KafkaValueKind.Tombstone);
        Match(Bytes([]), rules).ValueKind.Should().Be(KafkaValueKind.Empty);
        Match(Json("not json"), rules, KafkaDecoders.Json).ValueKind.Should().Be(KafkaValueKind.Undecodable);
        Match(Json(null), rules).ValueWithheldBy.Should().BeNull();
    }

    [Fact]
    public void The_time_the_rules_may_take_bounds_a_value_and_a_value_over_it_is_withheld()
    {
        string items = string.Join(",", Enumerable.Range(0, 5_000).Select(static i => $"\"s{i}\""));
        RecordValue value = Detect($$"""{"items":[{{items}}]}""");
        IKafkaRedactor redactor = new KafkaRedactor(Rules(static r => r.JsonPath("$.nothing")));

        RedactedValue? whole = PayloadRedactor.Apply(value, redactor, TimeSpan.FromSeconds(30));
        RedactedValue? none = PayloadRedactor.Apply(value, redactor, TimeSpan.Zero);

        whole!.WithheldBy.Should().BeNull();
        none!.WithheldBy.Should().Be("$.nothing");
        none.Failure.Should().Be(PayloadRedactionFailure.OutOfTime);
        none.Text.Should().BeNull();
        none.MaskedPaths.Should().BeEmpty();
    }

    [Fact]
    public void Redaction_is_not_applied_to_a_topic_without_a_payload_rule_and_costs_nothing()
    {
        RecordValue value = Detect("""{"a":1}""");

        PayloadRedactor.Apply(value, new KafkaRedactor(Rules(static r => r.Header("x"))), PayloadRedactor.Budget).Should().BeNull();
        PayloadRedactor.Apply(value, KafkaRedactor.DenyListOnly, PayloadRedactor.Budget).Should().BeNull();
    }

    // ---- CloudEvents: the data is masked, in every mode

    [Fact]
    public void A_binary_mode_events_data_is_masked_as_the_value_is()
    {
        KafkaMatch match = Match(Json(Document, "k", ("ce_specversion", "1.0"), ("content-type", "application/json")), SentinelRules());

        match.ValueText.Should().Contain("••• (redacted by $.customer.email)").And.Contain("pattern phone");
        match.ValueIsCloudEventData.Should().BeTrue();
        NoSentinel(match);
    }

    [Fact]
    public void A_structured_events_data_is_masked_and_the_envelope_is_not_kept()
    {
        string envelope = $$"""{"specversion":"1.0","type":"t","subject":"{{Email}}","data":{{Document}}}""";
        KafkaMatch match = Match(Json(envelope, "k", ("content-type", "application/cloudevents+json")), SentinelRules());

        match.ValueText.Should().Be(
            """{"customer":{"email":"••• (redacted by $.customer.email)","name":"Ann"},"note":"call ••• (redacted by pattern phone) now","n":1}""");
        match.ValueIsCloudEventData.Should().BeTrue();
        match.ValueWithheldBy.Should().BeNull();
        match.MaskedPaths.Single().Text.Should().Be("$.customer.email");

        // The envelope's own attribute holds the sentinel here: a rule on it masks the attribute, and the envelope is not the value.
        KafkaMatch withAttributeRule = Match(
            Json(envelope, "k", ("content-type", "application/cloudevents+json")),
            Rules(static r =>
            {
                r.JsonPath("$.customer.email");
                r.CloudEvent("subject");
                r.Pattern(PhonePattern, "phone");
            }));
        NoSentinel(withAttributeRule);
    }

    [Fact]
    public void A_batch_keeps_the_first_events_data_and_data_base64_is_decoded_then_masked()
    {
        string first = "{\"specversion\":\"1.0\",\"data\":{\"customer\":{\"email\":\"" + Email + "\"},\"id\":1}}";
        string second = "{\"specversion\":\"1.0\",\"data\":{\"customer\":{\"email\":\"" + Email + "\"}}}";
        KafkaMatch batch = Match(
            Json("[" + first + "," + second + "]", "k", ("content-type", "application/cloudevents-batch+json")),
            SentinelRules());

        batch.ValueText.Should().Be("""{"customer":{"email":"••• (redacted by $.customer.email)"},"id":1}""");
        NoSentinel(batch);

        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Document));
        KafkaMatch encoded = Match(
            Json($$"""{"specversion":"1.0","datacontenttype":"application/json","data_base64":"{{base64}}"}""", "k", ("content-type", "application/cloudevents+json")),
            SentinelRules());

        encoded.ValueText.Should().Contain("••• (redacted by $.customer.email)");
        encoded.ValueFormat.Should().Be("CloudEvents (structured), data_base64 as JSON");
        NoSentinel(encoded);
    }

    [Fact]
    public void A_cloudevent_whose_data_is_binary_is_withheld_under_a_payload_rule()
    {
        string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Email));
        KafkaMatch match = Match(
            Json($$"""{"specversion":"1.0","datacontenttype":"application/octet-stream","data_base64":"{{base64}}"}""", "k", ("content-type", "application/cloudevents+json")),
            SentinelRules());

        match.ValuePreview.Should().BeNull();
        match.ValueWithheldBy.Should().NotBeNull();
    }

    // ---- the focus budget of a record read

    [Fact]
    public void The_record_asked_for_may_keep_its_focus_budget_of_value_and_its_neighbours_keep_what_is_left()
    {
        string big = "{\"a\":\"" + new string('x', 20_000) + "\"}";

        KafkaMatch neighbour = Match(Json(big), null, limits: static l => l.MaxMatchBytes = 4_096);
        KafkaMatch focus = Match(Json(big), null, limits: static l => l.MaxMatchBytes = 4_096, focus: 64 * 1024);

        neighbour.ValueTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(neighbour.ValueText!).Should().BeLessThanOrEqualTo(4_096);
        focus.ValueTruncated.Should().BeFalse();
        focus.ValueText.Should().Be(big);
        focus.ValueLength.Should().Be(neighbour.ValueLength);
    }

    [Fact]
    public void A_masked_value_is_cut_after_the_rules_ran_so_a_secret_past_the_cut_is_masked_not_lost_or_leaked()
    {
        string filler = new('x', 5_000);
        string value = "{\"a\":\"" + filler + "\",\"customer\":{\"email\":\"" + Email + "\"}}";

        KafkaMatch match = Match(Json(value), Rules(static r => r.JsonPath("$.customer.email")), limits: static l => l.MaxMatchBytes = 4_096);

        match.ValueTruncated.Should().BeTrue();
        NoSentinel(match);

        KafkaMatch focus = Match(Json(value), Rules(static r => r.JsonPath("$.customer.email")), limits: static l => l.MaxMatchBytes = 4_096, focus: 64 * 1024);

        focus.ValueText.Should().EndWith("""{"email":"••• (redacted by $.customer.email)"}}""");
    }

    internal static void NoSentinelExcept(object? root, string raw)
    {
        string[] strings = [.. Strings(root)];

        strings.Should().NotBeEmpty();
        strings.Should().NotContain(x => x.Contains(raw, StringComparison.Ordinal));
    }
}
