using System.Buffers.Binary;
using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;

namespace KStudio.Tests.Predicates;

/// <summary>
/// Compiling and evaluating a search (decisions 7.4 to 7.6, 10.5 and 10.6): the semantics of every field and operator, the three-valued
/// logic as truth tables, the order clauses run in and the short circuit proved by the decode counters, and the refusals - every message
/// without a literal.
/// </summary>
public class PredicateEvaluationTests
{
    internal static PredicateCompilation Compile(
        string text,
        KafkaKeyFormat? format = null,
        bool declared = false,
        KafkaRedactionRules? rules = null,
        bool declaresCloudEvents = false,
        KafkaReadGrant grant = KafkaReadGrant.Envelope)
    {
        PredicateParseResult parsed = PredicateParser.Parse(text);
        parsed.Errors.Should().BeEmpty("'" + text + "' parses");
        return PredicateCompiler.Compile(
            parsed.Tree,
            new PredicateCompileContext("t", format ?? KafkaKeyFormat.Utf8String, declared, new KafkaRedactor(rules), grant, declaresCloudEvents));
    }

    internal static WireRecord Record(string? key, string? value, params (string Name, string? Value)[] headers) =>
        Raw(key is null ? null : Encoding.UTF8.GetBytes(key), value is null ? null : Encoding.UTF8.GetBytes(value), [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))]);

    internal static WireRecord Raw(byte[]? key, byte[]? value, params WireHeader[] headers) =>
        new("t", 0, 5, key, value, headers, 1_000, WireTimestampType.CreateTime, 0);

    private sealed record Outcome(TriState Verdict, DecodeCounters Counters, KafkaUnknownReason? Reason);

    private static Outcome Run(
        string text,
        WireRecord record,
        KafkaKeyFormat? format = null,
        bool declaresCloudEvents = false,
        KafkaRedactionRules? rules = null,
        int maxDecodeBytes = 1024 * 1024)
    {
        PredicateCompilation compiled = Compile(text, format, false, rules, declaresCloudEvents);
        compiled.Errors.Should().BeEmpty();
        DecodeCounters counters = new();
        RecordEvaluationContext context = new(record, format ?? KafkaKeyFormat.Utf8String, counters, maxDecodeBytes, declaresCloudEvents);
        TriState verdict = compiled.Predicate!.Evaluate(context);
        return new Outcome(verdict, counters, context.UnknownReason);
    }

    private static TriState Verdict(string text, WireRecord record, KafkaKeyFormat? format = null, bool declaresCloudEvents = false) =>
        Run(text, record, format, declaresCloudEvents).Verdict;

    [Fact]
    public void An_empty_query_matches_every_record_without_decoding_anything()
    {
        Outcome outcome = Run(string.Empty, Record("k", "v"));

        outcome.Verdict.Should().Be(TriState.True);
        outcome.Counters.Total.Should().Be(0);
        Compile(string.Empty).Predicate!.MatchesEverything.Should().BeTrue();
    }

    [Fact]
    public void Key_equality_prefix_in_and_null_checks_compare_bytes()
    {
        Verdict("key = 'order-1'", Record("order-1", "v")).Should().Be(TriState.True);
        Verdict("key = 'order-1'", Record("order-2", "v")).Should().Be(TriState.False);
        Verdict("key in ('a', 'order-1', 'b')", Record("order-1", "v")).Should().Be(TriState.True);
        Verdict("key ^= 'order-'", Record("order-9", "v")).Should().Be(TriState.True);
        Verdict("key ^= 'order-'", Record("ord", "v")).Should().Be(TriState.False);
        Verdict("key = 'x'", Record(null, "v")).Should().Be(TriState.False, "a null key equals nothing");

        Verdict("key is null", Record(null, "v")).Should().Be(TriState.True);
        Verdict("key is null", Record(string.Empty, "v")).Should().Be(TriState.False, "an empty key is not a null key");
        Verdict("key is not null", Record(string.Empty, "v")).Should().Be(TriState.True);
        Verdict("key = ''", Record(string.Empty, "v")).Should().Be(TriState.True);
    }

    [Fact]
    public void Key_literals_compile_to_the_bytes_a_producer_wrote_in_the_topics_format()
    {
        byte[] int64 = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(int64, 42);
        Verdict("key = '42'", Raw(int64, [1]), KafkaKeyFormat.Int64BigEndian).Should().Be(TriState.True);
        Verdict("key in ('1', '42')", Raw(int64, [1]), KafkaKeyFormat.Int64BigEndian).Should().Be(TriState.True);
        Verdict("key = '43'", Raw(int64, [1]), KafkaKeyFormat.Int64BigEndian).Should().Be(TriState.False);

        Guid guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Verdict("key = '00112233-4455-6677-8899-aabbccddeeff'", Raw(guid.ToByteArray(bigEndian: true), [1]), KafkaKeyFormat.GuidRfc4122).Should().Be(TriState.True);
        Verdict("key = '00112233-4455-6677-8899-aabbccddeeff'", Raw(guid.ToByteArray(), [1]), KafkaKeyFormat.GuidDotNet).Should().Be(TriState.True);

        Verdict("key ^= '0xdead'", Raw([0xde, 0xad, 0xbe, 0xef], [1]), KafkaKeyFormat.Bytes).Should().Be(TriState.True);
        Verdict("key = 'deadbeef'", Raw([0xde, 0xad, 0xbe, 0xef], [1]), KafkaKeyFormat.Bytes).Should().Be(TriState.True);
    }

    [Fact]
    public void A_json_string_key_matches_both_byte_forms_a_producer_could_have_written()
    {
        // System.Text.Json escapes non-ASCII by default, Newtonsoft does not: one value, two byte forms (F8).
        Verdict("key = 'café'", Raw(Encoding.UTF8.GetBytes("\"café\""), [1]), KafkaKeyFormat.JsonString).Should().Be(TriState.True);
        Verdict("key = 'café'", Raw(Encoding.UTF8.GetBytes("\"caf\\u00E9\""), [1]), KafkaKeyFormat.JsonString).Should().Be(TriState.True);
        Verdict("key ^= 'caf'", Raw(Encoding.UTF8.GetBytes("\"caf\\u00E9\""), [1]), KafkaKeyFormat.JsonString).Should().Be(TriState.True);
        Verdict("key = 'caf'", Raw(Encoding.UTF8.GetBytes("\"café\""), [1]), KafkaKeyFormat.JsonString).Should().Be(TriState.False);
    }

    [Fact]
    public void Contains_formats_the_key_as_text_once_ignoring_case_and_is_unknown_when_the_key_is_not_text()
    {
        Outcome contains = Run("key ~ 'RDER' and key ~ 'der-'", Record("Order-1", "v"));

        contains.Verdict.Should().Be(TriState.True);
        contains.Counters.KeyText.Should().Be(1, "the key is decoded at most once per record");

        byte[] notEightBytes = [1, 2, 3];
        Outcome unknown = Run("key ~ '1'", Raw(notEightBytes, [1]), KafkaKeyFormat.Int64BigEndian);

        unknown.Verdict.Should().Be(TriState.Unknown);
        unknown.Reason.Should().Be(KafkaUnknownReason.KeyNotText);
        Verdict("key ~ 'x'", Record(null, "v")).Should().Be(TriState.False, "a null key contains nothing");
    }

    [Fact]
    public void Header_clauses_compare_the_utf8_bytes_of_a_header_with_that_exact_name()
    {
        WireRecord record = Record("k", "v", ("trace-id", "abc-123"), ("Trace-Id", "other"), ("trace-id", "second"), ("empty", string.Empty), ("gone", null));

        Verdict("header.trace-id = 'abc-123'", record).Should().Be(TriState.True);
        Verdict("header.trace-id = 'second'", record).Should().Be(TriState.True, "duplicate headers are kept, and any may match");
        Verdict("header.trace-id = 'other'", record).Should().Be(TriState.False, "names are case-sensitive");
        Verdict("header['Trace-Id'] = 'other'", record).Should().Be(TriState.True);
        Verdict("header.trace-id ^= 'abc'", record).Should().Be(TriState.True);
        Verdict("header.trace-id in ('x', 'second')", record).Should().Be(TriState.True);
        Verdict("header.trace-id ~ 'ABC-1'", record).Should().Be(TriState.True);
        Verdict("header.empty = ''", record).Should().Be(TriState.True);

        Verdict("header.missing is null", record).Should().Be(TriState.True);
        Verdict("header.gone is null", record).Should().Be(TriState.True, "a header with a null value counts as absent");
        Verdict("header.empty is null", record).Should().Be(TriState.False);
        Verdict("header.trace-id is not null", record).Should().Be(TriState.True);
    }

    [Fact]
    public void Contains_on_a_header_is_unknown_when_no_value_is_text_and_false_when_a_text_value_does_not_contain()
    {
        WireRecord binary = Raw([1], [1], new WireHeader("blob", [0xff, 0xfe, 0x00]));

        Outcome unknown = Run("header.blob ~ 'x'", binary);
        unknown.Verdict.Should().Be(TriState.Unknown);
        unknown.Reason.Should().Be(KafkaUnknownReason.HeaderNotText);

        WireRecord mixed = Raw([1], [1], new WireHeader("blob", [0xff, 0xfe, 0x00]), new WireHeader("blob", Encoding.UTF8.GetBytes("has x inside")));
        Verdict("header.blob ~ 'x'", mixed).Should().Be(TriState.True, "a text value that contains it decides");
        Verdict("header.blob ~ 'zzz'", mixed).Should().Be(TriState.Unknown, "a value that cannot be read might have contained it");
        Verdict("header.nothing ~ 'zzz'", mixed).Should().Be(TriState.False);
    }

    [Fact]
    public void CloudEvents_binary_attributes_are_read_from_the_ce_headers_and_the_content_type()
    {
        WireRecord record = Record(
            "k",
            "{}",
            ("ce_specversion", "1.0"),
            ("ce_type", "com.acme.order.created"),
            ("ce_time", "2026-11-09T10:00:00Z"),
            ("content-type", "application/json"));

        Outcome equals = Run("ce.type = 'com.acme.order.created'", record);
        equals.Verdict.Should().Be(TriState.True);
        equals.Counters.CloudEventBinary.Should().Be(1);
        equals.Counters.CloudEventStructured.Should().Be(0, "binary mode never parses the value");

        Verdict("ce.time ^= '2026-11-09'", record).Should().Be(TriState.True, "a prefix of the time is a day");
        Verdict("ce.type ~ 'ORDER'", record).Should().Be(TriState.True);
        Verdict("ce.datacontenttype = 'application/json'", record).Should().Be(TriState.True);
        Verdict("ce.subject is null", record).Should().Be(TriState.True);
        Verdict("ce.type is null", record).Should().Be(TriState.False);
        Verdict("ce.type is not null", record).Should().Be(TriState.True);
    }

    [Fact]
    public void CloudEvents_structured_attributes_are_the_envelopes_top_level_members_and_data_is_never_read()
    {
        string envelope = """{"specversion":"1.0","type":"com.acme.order.created","id":"7","time":"2026-11-09T10:00:00Z","data":{"type":"WRONG","secret":"s3cr3t"},"extra":42}""";
        WireRecord record = Record("k", envelope, ("content-type", "application/cloudevents+json; charset=utf-8"));

        Outcome outcome = Run("ce.type = 'com.acme.order.created' and ce.extra = '42'", record);

        outcome.Verdict.Should().Be(TriState.True);
        outcome.Counters.CloudEventStructured.Should().Be(1);
        Verdict("ce.type = 'WRONG'", record).Should().Be(TriState.False, "the type inside data is not the event's type");
        Verdict("any ~ 's3cr3t'", record).Should().Be(TriState.False, "the data member is skipped, never materialised");
    }

    [Fact]
    public void A_batch_is_true_when_it_is_true_of_any_event()
    {
        string batch = """[{"specversion":"1.0","type":"a"},{"specversion":"1.0","type":"b"}]""";
        WireRecord record = Record("k", batch, ("content-type", "application/cloudevents-batch+json"));

        Verdict("ce.type = 'b'", record).Should().Be(TriState.True);
        Verdict("ce.type = 'a'", record).Should().Be(TriState.True);
        Verdict("ce.type = 'c'", record).Should().Be(TriState.False);
    }

    [Fact]
    public void A_record_that_is_not_a_CloudEvent_has_no_attributes_and_a_broken_structured_one_is_unknown()
    {
        Verdict("ce.type = 'a'", Record("k", "plain")).Should().Be(TriState.False);
        Verdict("ce.type is null", Record("k", "plain")).Should().Be(TriState.True);

        WireRecord broken = Record("k", "{ not json", ("content-type", "application/cloudevents+json"));
        Outcome unknown = Run("ce.type = 'a'", broken);
        unknown.Verdict.Should().Be(TriState.Unknown);
        unknown.Reason.Should().Be(KafkaUnknownReason.InvalidJson);

        Outcome notCe = Run("ce.type = 'a'", Record("k", """{"hello":"world"}"""), declaresCloudEvents: true);
        notCe.Verdict.Should().Be(TriState.Unknown);
        notCe.Reason.Should().Be(KafkaUnknownReason.NotACloudEvent);

        Outcome large = Run("ce.type = 'a'", Record("k", """{"specversion":"1.0"}"""), declaresCloudEvents: true, maxDecodeBytes: 5);
        large.Verdict.Should().Be(TriState.Unknown);
        large.Reason.Should().Be(KafkaUnknownReason.TooLarge);

        Verdict("ce.type = 'a'", Record("k", null), declaresCloudEvents: true).Should().Be(TriState.False, "a tombstone has no event");
    }

    [Fact]
    public void Any_looks_at_the_key_text_every_header_value_and_every_attribute()
    {
        WireRecord headerHit = Record("k", "v", ("x-tenant", "acme-corp"));

        Verdict("any = 'acme-corp'", headerHit).Should().Be(TriState.True);
        Verdict("any ^= 'acme'", headerHit).Should().Be(TriState.True);
        Verdict("any ~ 'CORP'", headerHit).Should().Be(TriState.True);
        Verdict("any in ('nope', 'k')", headerHit).Should().Be(TriState.True, "the key's text is a field of any");
        Verdict("any = 'nope'", headerHit).Should().Be(TriState.False);
        Verdict("any = 'v'", headerHit).Should().Be(TriState.False, "the value is not a field of any under the envelope grant");

        WireRecord binary = Raw([1], [1], new WireHeader("blob", [0xff, 0xfe]));
        Verdict("any = 'x'", binary).Should().Be(TriState.False, "a header that is not text is skipped, not unknown");

        WireRecord ce = Record("k", "{}", ("ce_specversion", "1.0"), ("ce_subject", "orders/42"));
        Verdict("any = 'orders/42'", ce).Should().Be(TriState.True);
    }

    [Fact]
    public void Any_is_unknown_when_nothing_matched_and_an_event_could_not_be_read()
    {
        WireRecord broken = Record("k", "{ nope", ("content-type", "application/cloudevents+json"));

        Verdict("any = 'zzz'", broken).Should().Be(TriState.Unknown);
        Verdict("any = 'k'", broken).Should().Be(TriState.True, "a field that matched decides, whatever the rest could not say");
    }

    [Fact]
    public void Any_never_looks_at_the_deny_listed_headers_or_at_a_redacted_key_or_attribute()
    {
        WireRecord record = Record(
            "secret-key",
            "{}",
            ("authorization", "Bearer s3cr3t"),
            ("Cookie", "session=s3cr3t"),
            ("x-api-key", "s3cr3t"),
            ("ce_specversion", "1.0"),
            ("ce_subject", "s3cr3t-subject"));

        Verdict("any ~ 's3cr3t'", record).Should().Be(TriState.True, "the subject and the key are not masked yet");

        KafkaRedactionRules rules = new();
        rules.Key();
        rules.CloudEvent("subject");
        Outcome masked = Run("any ~ 's3cr3t'", record, rules: rules);

        masked.Verdict.Should().Be(TriState.False, "the deny-list, the masked key and the masked attribute are all skipped by any");
    }

    [Fact]
    public void The_truth_tables_are_three_valued()
    {
        // key = 'k' is true, key = 'z' is false, and ce.type = 'x' on a topic of structured events over a broken value is unknown.
        WireRecord record = Record("k", "{ broken");
        const string T = "key = 'k'";
        const string F = "key = 'z'";
        const string U = "ce.type = 'x'";

        TriState Eval(string text) => Verdict(text, record, declaresCloudEvents: true);

        Eval(U).Should().Be(TriState.Unknown);

        Eval($"{T} and {T}").Should().Be(TriState.True);
        Eval($"{T} and {F}").Should().Be(TriState.False);
        Eval($"{T} and {U}").Should().Be(TriState.Unknown);
        Eval($"{U} and {T}").Should().Be(TriState.Unknown);
        Eval($"{F} and {U}").Should().Be(TriState.False);
        Eval($"{U} and {F}").Should().Be(TriState.False);
        Eval($"{U} and {U}").Should().Be(TriState.Unknown);

        Eval($"{F} or {F}").Should().Be(TriState.False);
        Eval($"{T} or {F}").Should().Be(TriState.True);
        Eval($"{T} or {U}").Should().Be(TriState.True);
        Eval($"{U} or {T}").Should().Be(TriState.True);
        Eval($"{F} or {U}").Should().Be(TriState.Unknown);
        Eval($"{U} or {F}").Should().Be(TriState.Unknown);
        Eval($"{U} or {U}").Should().Be(TriState.Unknown);

        Eval($"not {T}").Should().Be(TriState.False);
        Eval($"not {F}").Should().Be(TriState.True);
        Eval($"not {U}").Should().Be(TriState.Unknown);
        Eval($"not ({F} and {U})").Should().Be(TriState.True);
        Eval($"not ({T} or {U})").Should().Be(TriState.False);
    }

    [Fact]
    public void Clauses_run_cheapest_first_whatever_order_they_were_typed_in_and_a_false_and_short_circuits()
    {
        WireRecord structured = Record("k", """{"specversion":"1.0","type":"x"}""");

        // The structured-event clause is typed first and costs a parse; the key is a byte comparison and runs first.
        Outcome keyFirst = Run("ce.type = 'x' and key = 'nope'", structured, declaresCloudEvents: true);

        keyFirst.Verdict.Should().Be(TriState.False);
        keyFirst.Counters.CloudEventStructured.Should().Be(0, "the byte comparison decided before the value was parsed");
        keyFirst.Counters.Total.Should().Be(0);

        Outcome contains = Run("key ~ 'k' and key = 'nope'", Record("k", "v"));
        contains.Verdict.Should().Be(TriState.False);
        contains.Counters.KeyText.Should().Be(0, "= on the key runs before ~ and decides");

        Outcome any = Run("any ~ 'q' and header.a = 'nope'", Record("k", "v", ("a", "b")));
        any.Verdict.Should().Be(TriState.False);
        any.Counters.HeaderText.Should().Be(0, "any is the costliest clause and never ran");
        any.Counters.KeyText.Should().Be(0);
    }

    [Fact]
    public void A_true_or_short_circuits_and_a_clause_that_must_run_is_decoded_once()
    {
        Outcome orOutcome = Run("ce.type = 'x' or key = 'k'", Record("k", """{"specversion":"1.0","type":"y"}"""), declaresCloudEvents: true);

        orOutcome.Verdict.Should().Be(TriState.True);
        orOutcome.Counters.CloudEventStructured.Should().Be(0, "the key is true, so the parse is never needed");

        Outcome twice = Run("ce.type = 'y' and ce.specversion = '1.0'", Record("k", """{"specversion":"1.0","type":"y"}"""), declaresCloudEvents: true);

        twice.Verdict.Should().Be(TriState.True);
        twice.Counters.CloudEventStructured.Should().Be(1, "both clauses read one parse");

        Outcome headers = Run("header.a ~ 'b' and header.a ~ 'c'", Record("k", "v", ("a", "bc")));
        headers.Counters.HeaderText.Should().Be(1, "a header's text is decoded once per record");
    }

    [Fact]
    public void Equal_cost_clauses_keep_the_order_they_were_typed_in()
    {
        // Both are byte comparisons (cost 0): the first typed decides, and the second must not be needed.
        Outcome outcome = Run("key = 'nope' and key ~ 'k'", Record("k", "v"));

        outcome.Verdict.Should().Be(TriState.False);
        outcome.Counters.KeyText.Should().Be(0);
    }

    [Fact]
    public void Number_bool_and_null_literals_are_refused_on_text_fields_with_a_hint_to_quote()
    {
        Compile("key = 5").Errors.Single().Message.Should().Be("put this value in quotes: key compares as text.");
        Compile("header.a in ('x', 3)").Errors.Single().Message.Should().Be("put this value in quotes: header.a compares as text.");
        Compile("ce.type = true").Errors.Single().Message.Should().Be("put this value in quotes: ce.type compares as text.");
        Compile("any = null").Errors.Single().Message.Should().Be("put this value in quotes: any compares as text.");
    }

    [Fact]
    public void Ordering_operators_any_is_null_and_prefix_on_a_non_text_format_are_refused_with_their_reasons()
    {
        Compile("key < 'a'").Errors.Single().Message.Should().Be("'<' cannot be used on key: numbers compare only JSON paths.");
        Compile("header.a >= 'a'").Errors.Single().Message.Should().Be("'>=' cannot be used on header.a: numbers compare only JSON paths.");
        Compile("any is null").Errors.Single().Message.Should().Be("'is null' cannot be used on any: is null applies to key, header.<name>, ce.<attribute> and value.");
        Compile("any is not null").Errors.Single().Message.Should().Contain("'is not null' cannot be used on any");

        PredicateError prefix = Compile("key ^= '4'", KafkaKeyFormat.Int64BigEndian).Errors.Single();
        prefix.Message.Should().Be("'^=' cannot be used on key: a prefix needs a text key format, and this topic's keys are Int64BigEndian.");
    }

    [Fact]
    public void A_key_literal_the_topics_format_cannot_write_is_refused_without_repeating_it()
    {
        PredicateError error = Compile("key = 'not-a-number'", KafkaKeyFormat.Int64BigEndian).Errors.Single();

        error.Message.Should().StartWith("this value is not a valid Int64BigEndian key: ");
        error.Message.Should().NotContain("not-a-number");

        Compile("key in ('1', 'oops')", KafkaKeyFormat.Int32BigEndian).Errors.Single().Message.Should().NotContain("oops");
    }

    [Fact]
    public void All_the_problems_of_a_text_are_reported_together()
    {
        IReadOnlyList<PredicateError> errors = Compile("key = 5 and header.a = 6 and key < 'a'").Errors;

        errors.Should().HaveCount(3);
        errors.Select(static x => x.Start).Should().Equal(6, 23, 29);
    }

    [Fact]
    public void Value_and_json_path_searches_need_the_payload_grant()
    {
        Compile("value ~ 'x'").Errors.Single().Message.Should().Be("value reads the record's value, which is not available here.");
        Compile("$.a.b = 'x'").Errors.Single().Message.Should().Contain("$.a.b reads the record's value, which is not available here");
        Compile("value ~ 'x'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload).Errors.Should().BeEmpty();
        Compile("$.a.b = 'x'", grant: KafkaReadGrant.Envelope | KafkaReadGrant.Payload).Errors.Should().BeEmpty();
        Compile("value is null").Errors.Should().BeEmpty("a tombstone is a fact about the record, not about its content");
    }

    [Fact]
    public void The_built_in_deny_list_makes_its_headers_unsearchable_whatever_the_case_and_the_operator()
    {
        foreach (string name in new[] { "authorization", "Proxy-Authorization", "COOKIE", "set-cookie", "X-Api-Key" })
        {
            foreach (string op in new[] { "= 'x'", "^= 'x'", "~ 'x'", "in ('x')", "is null", "is not null" })
            {
                PredicateError error = Compile($"header.{name} {op}").Errors.Single();

                error.Message.Should().Be($"header.{name} is redacted by the built-in header deny-list and cannot be searched.", $"header.{name} {op}");
            }
        }
    }

    [Fact]
    public void A_host_can_opt_a_deny_listed_header_out_by_name_and_then_it_is_searchable()
    {
        KafkaRedactionRules rules = new();
        rules.AllowHeader("Cookie");

        Compile("header.cookie = 'a'", rules: rules).Errors.Should().BeEmpty();
        Compile("header.authorization = 'a'", rules: rules).Errors.Should().ContainSingle("only the named header was opted out");
    }

    [Fact]
    public void A_rule_makes_its_field_unsearchable_unless_it_is_marked_searchable_and_then_only_exactly()
    {
        KafkaRedactionRules rules = new();
        rules.Key();
        rules.Header("x-email").Searchable();
        rules.CloudEvent("subject").Searchable();

        Compile("key = 'a'", rules: rules).Errors.Single().Message.Should().Be("key is redacted by key and cannot be searched.");
        Compile("key in ('a')", rules: rules).Errors.Should().ContainSingle();

        Compile("header.x-email = 'a@b.c'", rules: rules).Errors.Should().BeEmpty();
        Compile("header.x-email in ('a@b.c', 'd@e.f')", rules: rules).Errors.Should().BeEmpty();
        Compile("header.x-email ^= 'a'", rules: rules).Errors.Single().Message.Should().Be("header.x-email is redacted by header x-email: only = and in can search it.");
        Compile("header.x-email ~ 'a'", rules: rules).Errors.Should().ContainSingle();
        Compile("header.x-email is null", rules: rules).Errors.Should().ContainSingle();
        Compile("ce.subject = 'a'", rules: rules).Errors.Should().BeEmpty();
        Compile("ce.subject ~ 'a'", rules: rules).Errors.Single().Message.Should().Contain("only = and in can search it");
    }

    [Fact]
    public void A_searchable_redacted_header_is_matched_exactly_and_still_masked_everywhere_else()
    {
        KafkaRedactionRules rules = new();
        rules.Header("x-email").Searchable();

        Run("header.x-email = 'a@b.c'", Record("k", "v", ("x-email", "a@b.c")), rules: rules).Verdict.Should().Be(TriState.True);
        Run("header.x-email = 'a@b.c'", Record("k", "v", ("x-email", "other")), rules: rules).Verdict.Should().Be(TriState.False);
    }

    [Fact]
    public void The_text_for_a_sink_hides_the_literals_of_redacted_fields_and_a_verdict_names_no_literal()
    {
        KafkaRedactionRules rules = new();
        rules.Key().Searchable();

        PredicateCompilation compiled = Compile("key = 'secret-id' and header.tenant = 'acme'", rules: rules);

        compiled.Predicate!.AuditText.Should().Be("key = <redacted> and header.tenant = 'acme'");
        compiled.Predicate.Verdicts.Select(static x => x.Field + x.Description).Should().NotContain(x => x.Contains("secret-id", StringComparison.Ordinal) || x.Contains("acme", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_clause_has_a_verdict_before_anything_is_read_and_key_verdicts_say_assumed_for_an_undeclared_format()
    {
        PredicateCompilation assumed = Compile("key = 'a' and key ~ 'b' and header.x = 'c' and header.y ~ 'd' and ce.type = 'e' and any ~ 'f'");

        IReadOnlyList<ClauseVerdict> verdicts = assumed.Predicate!.Verdicts;
        verdicts.Should().HaveCount(6);
        verdicts[0].Kind.Should().Be(ClauseVerdictKind.Scan);
        verdicts[0].Description.Should().Be("compared as bytes (Utf8String, assumed)");
        verdicts[1].Kind.Should().Be(ClauseVerdictKind.SlowScan);
        verdicts[2].Kind.Should().Be(ClauseVerdictKind.Scan);
        verdicts[3].Kind.Should().Be(ClauseVerdictKind.SlowScan);
        verdicts[4].Kind.Should().Be(ClauseVerdictKind.Scan);
        verdicts[5].Kind.Should().Be(ClauseVerdictKind.SlowScan);

        Compile("key = 'a'", KafkaKeyFormat.JsonString, declared: true).Predicate!.Verdicts[0].Description.Should().Be("compared as bytes; a key with escapes is decoded (JsonString)");
        Compile("ce.type = 'a'", declaresCloudEvents: true).Predicate!.Verdicts[0].Kind.Should().Be(ClauseVerdictKind.SlowScan);
    }
}
