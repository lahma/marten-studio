using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

using static KStudio.Tests.Predicates.ValueHarness;
using static KStudio.Tests.Redaction.PayloadRedactionTests;

namespace KStudio.Tests.Redaction;

/// <summary>
/// Searching a payload that rules mask (decisions 3.4 and 3.5 of D-PAYLOAD): value-side clauses compare what the visitor would see, a masked
/// leaf is found only by an exact match on a searchable rule, and a literal on a covered path is <c>&lt;redacted&gt;</c> in every text a sink
/// gets. A host decoder never sees a header a rule masks.
/// </summary>
public class RedactedPayloadPredicateTests
{
    private static string Refusal(string text, KafkaRedactionRules rules) => Compile(text, rules).Errors.Single().Message;

    [Theory]
    [InlineData("$.customer.email ~ 'SENTINEL'")]
    [InlineData("$.customer.email ^= 'SENTINEL'")]
    [InlineData("$.customer.email is null")]
    [InlineData("$.customer.email is not null")]
    [InlineData("$.CUSTOMER.Email ~ 'x'")]
    [InlineData("$.customer.email.deeper ~ 'x'")]
    public void A_covered_path_takes_only_equals_and_in_even_when_the_rule_is_searchable(string text)
    {
        string field = PredicateFormatter.Field(((ClauseNode) PredicateParser.Parse(text).Tree!).Field);

        Refusal(text, SentinelRules()).Should().Be($"{field} is redacted by $.customer.email: only = and in can search it.");
    }

    [Fact]
    public void A_covered_path_whose_rule_is_not_searchable_cannot_be_searched_at_all()
    {
        KafkaRedactionRules rules = Rules(static r => r.JsonPath("$.customer.email"));

        foreach (string text in new[] { "$.customer.email = 'x'", "$.customer.email in ('x')", "$.customer.email ~ 'x'" })
        {
            Refusal(text, rules).Should().Be("$.customer.email is redacted by $.customer.email and cannot be searched.");
        }
    }

    [Fact]
    public void A_pattern_is_never_searchable_and_never_refuses_a_path()
    {
        KafkaRedactionRules rules = Rules(static r => r.Pattern(PhonePattern, "phone"));

        Compile("$.note = 'x'", rules).Errors.Should().BeEmpty("a pattern covers content, not a path");
        Compile("value ~ 'x'", rules).Errors.Should().BeEmpty();
    }

    [Fact]
    public void A_searchable_rules_exact_clause_compiles_and_says_the_masked_leaf_is_compared_exactly_and_never_shown()
    {
        PredicateCompilation compiled = Compile("$.customer.email = 'x'", SentinelRules());

        compiled.Errors.Should().BeEmpty();
        compiled.Predicate!.Verdicts.Single().Description.Should().Be("slow: each value is parsed as JSON; the masked leaf is compared exactly, and never shown");
        compiled.Predicate.AuditText.Should().Be("$.customer.email = <redacted>");
    }

    [Fact]
    public void A_clause_on_an_ancestor_or_a_sibling_of_a_masked_node_compiles_and_cannot_reach_it()
    {
        Compile("$.customer = 'x'", SentinelRules()).Errors.Should().BeEmpty();
        Compile("$.customer.name = 'Ann'", SentinelRules()).Errors.Should().BeEmpty();

        Verdict("$.customer = 'x'", Json(Document), SentinelRules()).Should().Be(TriState.False);
        Verdict("$.customer.name = 'Ann'", Json(Document), SentinelRules()).Should().Be(TriState.True);
        Verdict("$.customer[*] = 'x'", Json(Document), SentinelRules()).Should().Be(TriState.False, "[*] never selects the members of an object");
    }

    [Fact]
    public void A_wildcard_and_an_index_are_covered_by_a_wildcard_rule_and_not_by_a_narrower_one()
    {
        KafkaRedactionRules wildcard = Rules(static r => r.JsonPath("$.items[*].secret"));
        KafkaRedactionRules narrow = Rules(static r => r.JsonPath("$.items[0].secret"));

        Compile("$.items[2].secret ~ 'x'", wildcard).Errors.Should().ContainSingle();
        Compile("$.items[*].secret ~ 'x'", wildcard).Errors.Should().ContainSingle();
        Compile("$.items[*] = 'x'", wildcard).Errors.Should().BeEmpty("an ancestor");
        Compile("$.items[0].secret ~ 'x'", narrow).Errors.Should().ContainSingle();
        Compile("$.items[*].secret ~ 'x'", narrow).Errors.Should().BeEmpty("a [*] clause is covered only by a [*] rule: it also reaches elements the rule does not mask");
        Compile("$.items[1].secret ~ 'x'", narrow).Errors.Should().BeEmpty();

        // The elements the narrow rule does not mask are searched as they are; the masked one reads as its mask.
        WireRecord record = Json("""{"items":[{"secret":"alpha"},{"secret":"beta"}]}""");
        Verdict("$.items[*].secret ~ 'beta'", record, narrow).Should().Be(TriState.True);
        Verdict("$.items[*].secret ~ 'alpha'", record, narrow).Should().Be(TriState.False);
    }

    [Fact]
    public void Where_two_rules_cover_a_path_the_one_that_cannot_be_searched_wins()
    {
        KafkaRedactionRules rules = Rules(static r =>
        {
            r.JsonPath("$.a").Searchable();
            r.JsonPath("$.a.b");
        });

        Refusal("$.a.b = 'x'", rules).Should().Be("$.a.b is redacted by $.a.b and cannot be searched.");
        Compile("$.a.c = 'x'", rules).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Value_side_clauses_never_find_what_a_rule_masks_and_still_find_what_it_leaves()
    {
        WireRecord record = Json(Document);
        KafkaRedactionRules rules = SentinelRules();

        Verdict("value ~ 'SENTINEL'", record, rules).Should().Be(TriState.False);
        Verdict("value ~ '555'", record, rules).Should().Be(TriState.False);
        Verdict("value ~ '0199'", record, rules).Should().Be(TriState.False);
        Verdict("$.note ~ '555'", record, rules).Should().Be(TriState.False);
        Verdict("$.note ^= 'call +358'", record, rules).Should().Be(TriState.False);
        Verdict("any ~ 'SENTINEL'", record, rules).Should().Be(TriState.False);
        Verdict("any ~ '555'", record, rules).Should().Be(TriState.False);
        Verdict($"$.note = 'call {Phone} now'", record, rules).Should().Be(TriState.False);

        Verdict("$.note ^= 'call'", record, rules).Should().Be(TriState.True);
        Verdict("$.customer.name = 'Ann'", record, rules).Should().Be(TriState.True);
        Verdict("any = 'Ann'", record, rules).Should().Be(TriState.True);
        Verdict("value ~ 'redacted by pattern phone'", record, rules).Should().Be(TriState.True, "the visitor sees the mask, so the search does");
        Verdict("value ~ 'Ann'", record, rules).Should().Be(TriState.True);
    }

    [Fact]
    public void The_exact_match_on_a_searchable_leaf_finds_the_raw_value_and_nothing_less_than_exact_does()
    {
        WireRecord record = Json(Document);
        KafkaRedactionRules rules = SentinelRules();

        Verdict($"$.customer.email = '{Email}'", record, rules).Should().Be(TriState.True);
        Verdict($"$.customer.email in ('other', '{Email}')", record, rules).Should().Be(TriState.True);
        Verdict("$.customer.email = 'SENTINEL'", record, rules).Should().Be(TriState.False);
        Verdict($"$.customer.email = '{Email.ToLowerInvariant()}'", record, rules).Should().Be(TriState.False);
        Verdict($"$.customer.email = '{Email}' and $.customer.name = 'Ann'", record, rules).Should().Be(TriState.True);
    }

    [Fact]
    public void A_literal_on_a_covered_path_is_redacted_in_the_audit_text_even_when_the_clause_is_refused()
    {
        KafkaRedactionRules rules = SentinelRules();
        KafkaRedactor redactor = new(rules);

        string audit = PredicateFormatter.Format(
            PredicateParser.Parse($"$.customer.email = '{Email}' and $.customer.email ~ '{Email}' and $.customer.name = 'Ann'").Tree,
            clause => PredicateCompiler.IsRedacted(clause.Field, redactor));

        audit.Should().Be("$.customer.email = <redacted> and $.customer.email ~ <redacted> and $.customer.name = 'Ann'");
        audit.Should().NotContain("SENTINEL");
    }

    [Fact]
    public void A_refusal_names_the_rule_and_never_the_literal()
    {
        string message = Refusal($"$.customer.email ~ '{Email}'", SentinelRules());

        message.Should().NotContain("SENTINEL").And.NotContain("example.com");
    }

    [Fact]
    public void A_value_a_rule_cannot_be_applied_to_is_unknown_for_every_value_side_clause()
    {
        KafkaRedactionRules pathRule = Rules(static r => r.JsonPath("$.a"));

        foreach (string text in new[] { "value ~ 'x'", "any ~ 'x'" })
        {
            Outcome outcome = Run(text, Json("plain text"), pathRule);

            outcome.Verdict.Should().Be(TriState.Unknown, text);
            outcome.Reason.Should().Be(KafkaUnknownReason.RedactionNotApplied, text);
        }

        Outcome named = Run("value ~ 'x'", Json("""{"4111111111111111":"y"}"""), Rules(static r => r.Pattern(@"\b[0-9]{16}\b", "card")));
        named.Verdict.Should().Be(TriState.Unknown);
        named.Reason.Should().Be(KafkaUnknownReason.RedactionNotApplied);

        KafkaUnknownReasonText.Describe(KafkaUnknownReason.RedactionNotApplied).Should().Be("could not be searched, because a redaction rule could not be applied to their value");
        KafkaUnknownReasonText.Describe(KafkaUnknownReason.RedactionNotApplied, one: true).Should().Be("could not be searched, because a redaction rule could not be applied to its value");
        KafkaUnknownReasonText.Describe(KafkaUnknownReason.NotACloudEvent, one: true).Should().Be("was not a CloudEvent although the topic declares them");
        KafkaUnknownReasonText.Describe(KafkaUnknownReason.TooLarge, one: true).Should().Be("was larger than the decode limit");
        KafkaUnknownReasonText.Describe(KafkaUnknownReason.InvalidJson, one: true).Should().Be("could not be decoded as JSON");
    }

    [Fact]
    public void A_withheld_value_tells_the_log_once_through_the_callback_and_the_masking_runs_once_per_record()
    {
        List<RedactedValue> failures = [];
        Outcome withheld = Run("value ~ 'x' and any ~ 'y'", Json("plain text"), Rules(static r => r.JsonPath("$.a")), onRedactionFailed: failures.Add);

        withheld.Verdict.Should().Be(TriState.Unknown);
        failures.Should().ContainSingle().Which.WithheldBy.Should().Be("$.a");
        withheld.Counters.ValueRedacted.Should().Be(1, "value and any share one masking");

        Outcome fine = Run("value ~ 'Ann' and $.customer.name = 'Ann' and any = 'Ann'", Json(Document), SentinelRules());

        fine.Verdict.Should().Be(TriState.True);
        fine.Counters.ValueRedacted.Should().Be(1);
        fine.Counters.ValueDetected.Should().Be(1);
    }

    // ---- 3.5: what the host decoder sees

    [Fact]
    public void A_host_decoder_is_handed_only_the_headers_no_rule_masks()
    {
        RecordingDecoder decoder = new();
        KafkaRedactionRules rules = Rules(static r => r.Header("x-email"));
        WireRecord record = Json("anything", "k", ("authorization", "Bearer SENTINEL-token"), ("X-Email", "SENTINEL-mail"), ("x-tenant", "acme"), ("COOKIE", "SENTINEL-cookie"));

        _ = Run("value ~ 'x'", record, rules, decoder);
        _ = Match(record, rules, decoder);

        decoder.Seen.Should().HaveCount(2, "once for the clause, once for the match built without its context");
        decoder.Seen.Should().OnlyContain(static x => x.SequenceEqual(new[] { "x-tenant" }));
    }

    [Fact]
    public void A_host_decoder_runs_once_per_matching_record_when_the_match_reuses_the_clauses_value()
    {
        RecordingDecoder decoder = new();
        PredicateCompilation compiled = Compile("value ~ 'decoded'");
        WireRecord record = Json("anything", "k", ("x-tenant", "acme"));
        DecodeCounters counters = new();
        DecodeCounters matchCounters = new();
        RecordEvaluationContext context = new(record, KafkaKeyFormat.Utf8String, counters, 1024 * 1024, false, Payload, decoder, KafkaRedactor.DenyListOnly);

        compiled.Predicate!.Evaluate(context).Should().Be(TriState.True);
        KafkaMatch match = KafkaMatchFactory.Create(
            record, Topic(null, decoder), Payload, new KafkaScanLimits(), matchCounters, evaluated: context);

        decoder.Seen.Should().HaveCount(1);
        counters.ValueDetected.Should().Be(1);
        matchCounters.ValueDetected.Should().Be(0, "the match took the value the predicate had decoded");
        match.ValueText.Should().Be("decoded");
    }

    private sealed class RecordingDecoder : IKafkaRecordDecoder
    {
        public List<string[]> Seen { get; } = [];

        public string Name => "Recording";

        public KafkaDecodeResult Decode(KafkaDecodeContext context)
        {
            Seen.Add([.. context.Headers.Select(static x => x.Name)]);
            return KafkaDecodeResult.Text("decoded", "Recording");
        }
    }
}
