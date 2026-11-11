using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Wire;

namespace KStudio.Tests.Predicates;

/// <summary>
/// The review follow-ups of the predicate engine (REVIEW-ENG-B): <c>any</c> never turns "could not be judged" into false (finding 2), a JSON
/// key that a serializer escaped in a way the two byte forms miss is still found (finding 7), a host's key formatter that throws makes the key
/// unreadable and not the scan fail (finding 9), and the reason a record is counted under is the reason of the clause that left the result
/// unknown (finding 10).
/// </summary>
public class PredicateFollowUpTests
{
    private sealed record Outcome(TriState Verdict, DecodeCounters Counters, KafkaUnknownReason? Reason);

    private static Outcome Run(string text, WireRecord record, KafkaKeyFormat? format = null, bool declaresCloudEvents = false)
    {
        PredicateCompilation compiled = PredicateEvaluationTests.Compile(text, format, false, null, declaresCloudEvents);
        compiled.Errors.Should().BeEmpty();
        DecodeCounters counters = new();
        RecordEvaluationContext context = new(record, format ?? KafkaKeyFormat.Utf8String, counters, 1024 * 1024, declaresCloudEvents);
        TriState verdict = compiled.Predicate!.Evaluate(context);
        return new Outcome(verdict, counters, context.UnknownReason);
    }

    private static WireRecord Raw(byte[]? key, byte[]? value, params WireHeader[] headers) => PredicateEvaluationTests.Raw(key, value, headers);

    // Finding 2.

    [Fact]
    public void Any_contains_is_unknown_for_a_binary_header_and_a_binary_key_and_true_when_another_field_matches()
    {
        WireRecord binaryHeader = Raw(Encoding.UTF8.GetBytes("k"), [1], new WireHeader("blob", [0xff, 0xfe]));
        Outcome header = Run("any ~ 'x'", binaryHeader);

        header.Verdict.Should().Be(TriState.Unknown, "a header that is not text could not be searched, which is not the same as not containing x");
        header.Reason.Should().Be(KafkaUnknownReason.HeaderNotText);

        WireRecord binaryKey = Raw([1, 2, 3], [1]);
        Outcome key = Run("any ~ '1'", binaryKey, KafkaKeyFormat.Int64BigEndian);

        key.Verdict.Should().Be(TriState.Unknown);
        key.Reason.Should().Be(KafkaUnknownReason.KeyNotText);

        WireRecord both = Raw([1, 2, 3], [1], new WireHeader("blob", [0xff]), new WireHeader("x-tenant", Encoding.UTF8.GetBytes("acme")));
        Run("any ~ 'acm'", both, KafkaKeyFormat.Int64BigEndian).Verdict.Should().Be(TriState.True, "a field that matched decides, whatever the rest could not say");
        Run("any ~ 'zzz'", both, KafkaKeyFormat.Int64BigEndian).Verdict.Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Any_equality_and_prefix_stay_false_for_bytes_that_are_not_text_and_a_masked_field_never_makes_the_clause_unknown()
    {
        WireRecord binary = Raw([1, 2, 3], [1], new WireHeader("blob", [0xff, 0xfe]));

        Run("any = 'x'", binary, KafkaKeyFormat.Int64BigEndian).Verdict.Should().Be(TriState.False);
        Run("any ^= 'x'", binary, KafkaKeyFormat.Int64BigEndian).Verdict.Should().Be(TriState.False);

        // The deny-listed header is binary but is never looked at, so it cannot leave the clause unknown either.
        WireRecord masked = Raw(Encoding.UTF8.GetBytes("k"), [1], new WireHeader("authorization", [0xff, 0xfe]));
        Run("any ~ 'x'", masked).Verdict.Should().Be(TriState.False);
    }

    // Finding 7.

    private static WireRecord JsonKey(string json) => Raw(Encoding.UTF8.GetBytes(json), [1]);

    [Theory]
    [InlineData("\"caf\\u00e9\"")]
    [InlineData("\"caf\\u00E9\"")]
    [InlineData("\"caf\\u00c3\\u00a9\"", false)]
    [InlineData("\"café\"")]
    public void A_json_key_written_with_an_escape_the_byte_forms_do_not_cover_is_found_by_decoding_it(string json, bool matches = true)
    {
        // Python's json.dumps writes lower-case é for é, which neither of the two byte forms (raw é, and System.Text.Json's é) is.
        Run("key = 'café'", JsonKey(json), KafkaKeyFormat.JsonString).Verdict.Should().Be(matches ? TriState.True : TriState.False);
    }

    [Fact]
    public void An_escaped_json_key_is_compared_for_equality_in_and_prefix_and_only_when_no_byte_form_matched()
    {
        WireRecord python = JsonKey("\"caf\\u00e9-1\"");

        Run("key ^= 'café'", python, KafkaKeyFormat.JsonString).Verdict.Should().Be(TriState.True);
        Run("key = 'café-1'", python, KafkaKeyFormat.JsonString).Verdict.Should().Be(TriState.True);
        Run("key in ('x', 'café-1')", python, KafkaKeyFormat.JsonString).Verdict.Should().Be(TriState.True);
        Run("key = 'café'", python, KafkaKeyFormat.JsonString).Verdict.Should().Be(TriState.False);
        Run("key = 'tab\\there'", JsonKey("\"tab\\\\there\""), KafkaKeyFormat.JsonString).Verdict.Should().Be(TriState.True, "a backslash in the literal is a backslash in the key");

        Outcome fast = Run("key = 'abc'", JsonKey("\"abc\""), KafkaKeyFormat.JsonString);
        fast.Verdict.Should().Be(TriState.True);
        fast.Counters.KeyText.Should().Be(0, "a byte form matched, so nothing was decoded");

        Outcome noBackslash = Run("key = 'abd'", JsonKey("\"abc\""), KafkaKeyFormat.JsonString);
        noBackslash.Verdict.Should().Be(TriState.False);
        noBackslash.Counters.KeyText.Should().Be(0, "a key with no backslash is not decoded");

        Outcome slow = Run("key = 'nope'", JsonKey("\"caf\\u00e9\""), KafkaKeyFormat.JsonString);
        slow.Verdict.Should().Be(TriState.False);
        slow.Counters.KeyText.Should().Be(1, "a key with an escape that no form matched is decoded once");
    }

    [Fact]
    public void Other_key_formats_do_not_decode_a_key_with_a_backslash()
    {
        Outcome utf8 = Run("key = 'x'", Raw(Encoding.UTF8.GetBytes("a\\u00e9"), [1]));

        utf8.Verdict.Should().Be(TriState.False);
        utf8.Counters.KeyText.Should().Be(0);
    }

    [Fact]
    public void A_json_key_clause_says_in_its_verdict_that_a_key_with_escapes_is_decoded()
    {
        PredicateEvaluationTests.Compile("key = 'a'", KafkaKeyFormat.JsonString, declared: true).Predicate!.Verdicts[0].Description
            .Should().Be("compared as bytes; a key with escapes is decoded (JsonString)");
        PredicateEvaluationTests.Compile("key is null", KafkaKeyFormat.JsonString, declared: true).Predicate!.Verdicts[0].Description
            .Should().Be("compared as bytes (JsonString)");
    }

    // Finding 9.

    private sealed class ThrowingFormat() : KafkaKeyFormat("Throwing")
    {
        public override bool TryEncode(string literal, out IReadOnlyList<byte[]> forms, out string? problem)
        {
            forms = [Encoding.UTF8.GetBytes(literal)];
            problem = null;
            return true;
        }

        public override bool TryFormat(ReadOnlySpan<byte> key, out string? text) => throw new InvalidOperationException("the host's formatter threw");
    }

    [Fact]
    public void A_key_formatter_that_throws_makes_the_key_unreadable_and_does_not_end_the_scan()
    {
        Outcome contains = Run("key ~ 'x'", Raw(Encoding.UTF8.GetBytes("x"), [1]), new ThrowingFormat());

        contains.Verdict.Should().Be(TriState.Unknown);
        contains.Reason.Should().Be(KafkaUnknownReason.KeyNotText);

        Run("any ~ 'x'", Raw(Encoding.UTF8.GetBytes("x"), [1]), new ThrowingFormat()).Verdict.Should().Be(TriState.Unknown);
        Run("key = 'x'", Raw(Encoding.UTF8.GetBytes("x"), [1]), new ThrowingFormat()).Verdict.Should().Be(TriState.True, "equality compares bytes and never asks the formatter");
    }

    // Finding 10: the reason of the clause that decided.

    [Fact]
    public void The_reason_a_record_is_counted_under_is_the_reason_of_the_clause_that_left_the_result_unknown()
    {
        // The key is not an 8-byte integer, the header is binary, and the value is a valid event: `header.blob ~ 'x'` is unknown
        // (HeaderNotText) and runs first, `ce.type = 'z'` is false and so makes its `and` false, and `any ~ 'q'` is unknown (KeyNotText).
        // Only `any` explains the unknown result.
        WireRecord record = Raw(
            [1, 2, 3],
            Encoding.UTF8.GetBytes("""{"specversion":"1.0","type":"a"}"""),
            new WireHeader("blob", [0xff, 0xfe]));

        Outcome outcome = Run("(header.blob ~ 'x' and ce.type = 'z') or any ~ 'q'", record, KafkaKeyFormat.Int64BigEndian, declaresCloudEvents: true);

        outcome.Verdict.Should().Be(TriState.Unknown);
        outcome.Reason.Should().Be(KafkaUnknownReason.KeyNotText, "header.blob ~ 'x' was unknown, but ce.type = 'z' made its `and` false, so it explains nothing");

        // The first of two clauses that both left it unknown wins.
        Run("header.blob ~ 'x' or any ~ 'q'", record, KafkaKeyFormat.Int64BigEndian, declaresCloudEvents: true).Reason.Should().Be(KafkaUnknownReason.HeaderNotText);

        // A result that is not unknown carries no reason.
        Outcome decided = Run("header.blob is not null or any ~ 'q'", record, KafkaKeyFormat.Int64BigEndian, declaresCloudEvents: true);
        decided.Verdict.Should().Be(TriState.True);
        decided.Reason.Should().BeNull();
        Run("ce.type = 'z' and any ~ 'q'", record, KafkaKeyFormat.Int64BigEndian, declaresCloudEvents: true).Reason.Should().BeNull();
    }
}
