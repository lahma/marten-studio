using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

using static KStudio.Tests.Predicates.ValueHarness;
using static KStudio.Tests.Redaction.PayloadRedactionTests;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The decode counters prove the order and the economy of value clauses (decisions 2.4 and 2.5 of D-PAYLOAD): a clause that reads the value
/// runs after every cheaper one and decodes nothing for a record they decided, a record's value is parsed once however many clauses read it,
/// and a match made from the record does not decode it again.
/// </summary>
public class ValueDecodeOrderTests
{
    [Fact]
    public void A_key_that_decides_the_record_parses_no_value()
    {
        Outcome outcome = Run("key = 'x' and $.a = 1", Json("""{"a":1}""", key: "other"));

        outcome.Verdict.Should().Be(TriState.False);
        outcome.Counters.Total.Should().Be(0);
    }

    [Fact]
    public void A_header_that_decides_an_or_parses_no_value()
    {
        Outcome outcome = Run("header.h = 'y' or value ~ 'z'", Json("""{"a":1}""", "k", ("h", "y")));

        outcome.Verdict.Should().Be(TriState.True);
        outcome.Counters.Total.Should().Be(0);
    }

    [Fact]
    public void Two_path_clauses_parse_the_value_once_per_record()
    {
        Outcome outcome = Run("$.a = 1 and $.b = 2", Json("""{"a":1,"b":2}"""));

        outcome.Verdict.Should().Be(TriState.True);
        outcome.Counters.ValueParsed.Should().Be(1);
        outcome.Counters.ValueDetected.Should().Be(1);

        Outcome mixed = Run("value ~ 'a' and $.a = 1 and any = 'q'", Json("""{"a":1,"b":"q"}"""));

        mixed.Verdict.Should().Be(TriState.True);
        mixed.Counters.ValueParsed.Should().Be(1);
        mixed.Counters.ValueDetected.Should().Be(1);
    }

    [Fact]
    public void Any_with_a_key_that_is_true_parses_no_value()
    {
        Outcome outcome = Run("any ~ 'needle'", Json("""{"a":1}""", key: "needle"));

        outcome.Verdict.Should().Be(TriState.True);
        outcome.Counters.ValueDetected.Should().Be(0);
        outcome.Counters.ValueParsed.Should().Be(0);
    }

    [Fact]
    public void A_value_clause_runs_after_the_cheaper_clauses_of_an_and_whatever_order_it_was_typed_in()
    {
        Outcome outcome = Run("$.a = 1 and header.h = 'no'", Json("""{"a":1}""", "k", ("h", "yes")));

        outcome.Verdict.Should().Be(TriState.False);
        outcome.Counters.ValueDetected.Should().Be(0, "the header is a byte comparison and decided first");

        Outcome costly = Run("any ~ 'zzz' and $.a = 1 and key = 'k'", Json("""{"a":1}"""));

        costly.Verdict.Should().Be(TriState.False, "any, the costliest, ran last and found nothing");
        costly.Counters.ValueDetected.Should().Be(1, "the value was decoded once for the path and again for nobody");

        Outcome decided = Run("any ~ 'zzz' and $.a = 2 and key = 'k'", Json("""{"a":1}"""));

        decided.Verdict.Should().Be(TriState.False);
        decided.Counters.ValueDetected.Should().Be(1);
        decided.Counters.ValueParsed.Should().Be(1, "the path clause decided the record and any never ran");
    }

    [Fact]
    public void A_match_does_not_decode_the_value_again()
    {
        PredicateCompilation compiled = Compile("$.a = 1 and value ~ 'a'");
        WireRecord record = Json("""{"a":1}""");
        DecodeCounters counters = new();
        DecodeCounters matchCounters = new();
        RecordEvaluationContext context = new(record, KafkaKeyFormat.Utf8String, counters, 1024 * 1024, false, Payload);

        compiled.Predicate!.Evaluate(context).Should().Be(TriState.True);
        KafkaMatch match = KafkaMatchFactory.Create(record, Topic(null), Payload, new KafkaScanLimits(), matchCounters, evaluated: context);

        counters.ValueDetected.Should().Be(1);
        counters.ValueParsed.Should().Be(1);
        matchCounters.ValueDetected.Should().Be(0);
        matchCounters.ValueParsed.Should().Be(0);
        match.ValueText.Should().Be("""{"a":1}""");
    }

    [Fact]
    public void A_match_of_a_record_no_clause_read_detects_the_value_once_itself()
    {
        PredicateCompilation compiled = Compile("key = 'k'");
        WireRecord record = Json("""{"a":1}""");
        DecodeCounters counters = new();
        DecodeCounters matchCounters = new();
        RecordEvaluationContext context = new(record, KafkaKeyFormat.Utf8String, counters, 1024 * 1024, false, Payload);

        compiled.Predicate!.Evaluate(context).Should().Be(TriState.True);
        _ = KafkaMatchFactory.Create(record, Topic(null), Payload, new KafkaScanLimits(), matchCounters, evaluated: context);

        counters.ValueDetected.Should().Be(0);
        matchCounters.ValueDetected.Should().Be(1);
    }

    [Fact]
    public void A_predicate_says_whether_it_reads_values_and_a_value_null_check_does_not()
    {
        Compile("key = 'k'").Predicate!.ReadsValues.Should().BeFalse();
        Compile("value is null").Predicate!.ReadsValues.Should().BeFalse();
        Compile("value ~ 'x'").Predicate!.ReadsValues.Should().BeTrue();
        Compile("$.a = 1").Predicate!.ReadsValues.Should().BeTrue();
        Compile("key = 'k' or not $.a = 1").Predicate!.ReadsValues.Should().BeTrue();
        Compile("any ~ 'x'").Predicate!.ReadsValues.Should().BeTrue("any reads the value under the payload grant");
        Compile("any ~ 'x'", grant: KafkaReadGrant.Envelope).Predicate!.ReadsValues.Should().BeFalse();
        Compile(string.Empty).Predicate!.ReadsValues.Should().BeFalse();

        PredicateTree.ReadsValues(PredicateParser.Parse("key = 'k' and $.a = 1").Tree).Should().BeTrue();
        PredicateTree.ReadsValues(PredicateParser.Parse("value is null or value is not null").Tree).Should().BeFalse();
        PredicateTree.ReadsValues(PredicateParser.Parse("any ~ 'x'").Tree).Should().BeFalse("any widens only under a grant, which the tree does not know");
        PredicateTree.ReadsValues(null).Should().BeFalse();
    }
}
