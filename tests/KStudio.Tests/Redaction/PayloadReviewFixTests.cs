using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Tests.Contract;
using KStudio.Tests.Scanning;

using static KStudio.Tests.Predicates.ValueHarness;
using static KStudio.Tests.Redaction.PayloadRedactionTests;

namespace KStudio.Tests.Redaction;

/// <summary>
/// REVIEW-ENG-D's findings: a text cut at the decode limit is withheld under a pattern, masking cannot grow a value without bound, the match
/// budget counts the masks, the focus budget is clamped, and no record type prints the content it holds.
/// </summary>
public class PayloadReviewFixTests
{
    private const int Limit = 64 * 1024;

    private static RecordValue Detect(string text, int maxDecode = Limit) =>
        ValueDetector.DetectValue(Json(text), new ValueDetection(Payload, null, maxDecode, maxDecode, new DecodeCounters(), null, KafkaRedactor.DenyListOnly));

    /// <summary>A text whose phone number starts five bytes before the decode limit, so that the cut falls inside it.</summary>
    private static string TextCutInsideAPhone() => new string('x', Limit - 5) + Phone + new string('y', 500);

    // ---- 1: a cut text is withheld under a pattern

    [Fact]
    public void A_text_cut_at_the_decode_limit_is_withheld_under_a_pattern_rule()
    {
        RecordValue value = Detect(TextCutInsideAPhone());

        value.Cut.Should().BeTrue();

        RedactedValue? redacted = PayloadRedactor.Apply(value, new KafkaRedactor(Rules(static r => r.Pattern(PhonePattern, "phone"))), PayloadRedactor.Budget, Limit);

        redacted!.WithheldBy.Should().Be("pattern phone");
        redacted.Failure.Should().Be(PayloadRedactionFailure.NotApplicable);
        redacted.Text.Should().BeNull();
    }

    [Fact]
    public async Task A_focus_read_of_a_text_cut_inside_a_phone_number_withholds_it()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxDecodeBytes = Limit);
        string topic = fixture.Topic("cut-focus");
        fixture.Declare(topic, static t => t.Redact(static r => r.Pattern(PhonePattern, "phone")));
        fixture.Produce(topic, 0, 1, "k", TextCutInsideAPhone());

        KafkaRecordRead read = await fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 0, 0, Payload, KafkaIsolation.Committed, Limit),
            fixture.Owner,
            TestContext.Current.CancellationToken);

        read.Record!.ValueWithheldBy.Should().Be("pattern phone");
        read.Record.ValueText.Should().BeNull();
        NoSentinelExcept(read, "+358");
    }

    [Fact]
    public async Task A_scan_whose_match_budget_equals_the_decode_limit_withholds_a_cut_text_too()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o =>
        {
            o.Limits.MaxDecodeBytes = Limit;
            o.Limits.MaxMatchBytes = Limit;
        });
        string topic = fixture.Topic("cut-scan");
        fixture.Declare(topic, static t => t.Redact(static r => r.Pattern(PhonePattern, "phone")));
        fixture.Produce(topic, 0, 1, "k", TextCutInsideAPhone());

        (_, _, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "key = 'k'", grant: Payload));

        matches.Should().ContainSingle().Which.ValueWithheldBy.Should().Be("pattern phone");
        matches[0].ValueText.Should().BeNull();
        NoSentinelExcept(matches, "+358");
    }

    // ---- 2: masking cannot grow a value without bound

    [Fact]
    public void A_pattern_that_grows_a_text_past_twice_the_decode_limit_withholds_it()
    {
        RecordValue text = Detect(string.Concat(Enumerable.Repeat("1 ", 20_000)));
        IKafkaRedactor redactor = new KafkaRedactor(Rules(static r => r.Pattern("1", "digit")));

        text.Cut.Should().BeFalse();

        RedactedValue? redacted = PayloadRedactor.Apply(text, redactor, PayloadRedactor.Budget, Limit);

        redacted!.WithheldBy.Should().Be("pattern digit");
        redacted.Failure.Should().Be(PayloadRedactionFailure.NotApplicable);
        redacted.ExceptionKind.Should().Be("GrowthException");

        RecordValue json = Detect("{\"a\":\"" + string.Concat(Enumerable.Repeat("1 ", 20_000)) + "\"}");

        PayloadRedactor.Apply(json, redactor, PayloadRedactor.Budget, Limit)!.WithheldBy.Should().Be("pattern digit");
    }

    [Fact]
    public void Masking_that_stays_inside_twice_the_decode_limit_is_kept()
    {
        RecordValue text = Detect(string.Concat(Enumerable.Repeat("1 ", 1_000)));

        PayloadRedactor.Apply(text, new KafkaRedactor(Rules(static r => r.Pattern("1", "digit"))), PayloadRedactor.Budget, Limit)!.WithheldBy.Should().BeNull();
    }

    // ---- 3: the match budget counts the masks

    [Fact]
    public void The_paths_of_masked_nodes_are_bounded_at_4_KiB_and_counted_against_the_value_budget()
    {
        string member = new('m', 120);
        string items = string.Join(",", Enumerable.Range(0, 80).Select(static i => $"{{\"{new string('m', 120)}\":\"secret{i}\"}}"));
        KafkaRedactionRules rules = Rules(r => r.JsonPath($"$.items[*].{member}"));
        KafkaMatch match = Match(Json("{\"items\":[" + items + "]}"), rules, limits: static l => l.MaxMatchBytes = 8_192);

        int pathBytes = match.MaskedPaths.Sum(static x => Encoding.UTF8.GetByteCount(x.Text) + Encoding.UTF8.GetByteCount(x.Rule));

        pathBytes.Should().BeLessThanOrEqualTo(KafkaMatchFactory.MaxMaskedPathBytes);
        match.MaskedPaths.Should().NotBeEmpty().And.HaveCountLessThan(64, "the 4 KiB cap came before the 64-path cap");
        (match.MaskedPaths.Count + match.MaskedPathsOmitted).Should().Be(80);

        int masks = pathBytes + match.ValueMaskedBy.Sum(static x => Encoding.UTF8.GetByteCount(x));

        (masks + Encoding.UTF8.GetByteCount(match.ValueText ?? string.Empty)).Should().BeLessThanOrEqualTo(8_192 - 1, "the key is 1 byte and the rest is the masks and the value");
    }

    // ---- 4: the focus budget is clamped

    [Fact]
    public async Task A_focus_budget_above_the_decode_limit_is_clamped_to_it()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxDecodeBytes = Limit);
        string topic = fixture.Topic("clamp");
        fixture.Declare(topic, static t => t.Redact(static r => r.Pattern("ab", "ab")));

        // 3,000 matches grow the text to about 100 KB (inside twice the decode limit), which a 10 MiB focus budget would keep whole.
        fixture.Produce(topic, 0, 1, "k", string.Concat(Enumerable.Repeat("ab ", 3_000)));

        KafkaRecordRead read = await fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, 0, 0, Payload, KafkaIsolation.Committed, 10 * 1024 * 1024),
            fixture.Owner,
            TestContext.Current.CancellationToken);

        read.Record!.ValueWithheldBy.Should().BeNull();
        read.Record.ValueTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(read.Record.ValueText!).Should().BeLessThanOrEqualTo(Limit);
    }

    // ---- 8: no record prints the content it holds

    [Fact]
    public void No_record_type_that_holds_content_prints_it()
    {
        RecordValue value = Detect("""{"secret":"SENTINEL-value"}""");

        value.ToString().Should().NotContain("SENTINEL").And.Contain("Kind = Json");

        KafkaMaskedPath path = new([new JsonMemberStep("SENTINEL-name")], "$['SENTINEL-name']", "$.rule");
        path.ToString().Should().NotContain("SENTINEL").And.Contain("$.rule");
        new JsonMemberStep("SENTINEL-name").ToString().Should().NotContain("SENTINEL");

        RedactionRule rule = new KafkaRedactor(Rules(static r => r.Pattern("SENTINEL-[a-z]+", "secret"))).PayloadRules.Single();
        rule.ToString().Should().NotContain("SENTINEL").And.Contain("pattern secret");

        RedactedValue? redacted = PayloadRedactor.Apply(value, new KafkaRedactor(Rules(static r => r.JsonPath("$.secret"))), PayloadRedactor.Budget, Limit);
        redacted!.ToString().Should().NotContain("SENTINEL");
        redacted.MaskedPaths.Single().ToString().Should().NotContain("SENTINEL");
    }
}
