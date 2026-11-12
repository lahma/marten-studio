using System.Text;

using KStudio.Core.Scanning;
using KStudio.Core.Wire;

using static KStudio.Tests.Predicates.ValueHarness;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The M3 exit criterion, on the scan engine over the fake broker: a value search counts the records it could not judge, per reason and with
/// the engine's own sentence, and never calls them non-matches (decisions 2.2 and 2.6 of D-PAYLOAD).
/// </summary>
public class UndecidableValueScanTests
{
    private const int Undecidable = 1_204;
    private const int Decidable = 1_796;

    private static async Task<KafkaScanResult> ScanAsync(ScanFixture fixture, string topic, string predicate)
    {
        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], predicate, grant: Payload));

        matches.Should().BeEmpty("no record satisfies the search; the undecidable ones are not matches");
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        return result;
    }

    /// <summary>Seeds so that exactly <paramref name="undecidable" /> records are bad, spread through the topic.</summary>
    private static void SeedMixed(ScanFixture fixture, string topic, int undecidable, int decidable, Func<byte[]> bad, Func<byte[]> good)
    {
        int remainingBad = undecidable;
        int remainingGood = decidable;

        for (int i = 0; i < undecidable + decidable; i++)
        {
            bool takeBad = remainingBad > 0 && (remainingGood == 0 || i % 5 < 2);

            if (takeBad)
            {
                fixture.ProduceRaw(topic, 0, 1, Encoding.UTF8.GetBytes("k"), bad());
                remainingBad--;
            }
            else
            {
                fixture.ProduceRaw(topic, 0, 1, Encoding.UTF8.GetBytes("k"), good());
                remainingGood--;
            }
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public async Task Records_that_start_like_json_and_do_not_parse_are_counted_as_could_not_be_decoded_as_JSON()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("badjson");
        SeedMixed(fixture, topic, Undecidable, Decidable, () => Utf8("{ broken"), () => Utf8("""{"kind":"other"}"""));

        KafkaScanResult result = await ScanAsync(fixture, topic, "$.kind = 'ok'");

        result.Records.Should().Be(Undecidable + Decidable);
        result.Undecidable.Should().Be(Undecidable);
        result.UndecidableSentences.Should().Equal("1,204 records could not be decoded as JSON");
    }

    [Fact]
    public async Task Binary_values_are_counted_as_had_a_value_that_could_not_be_read_as_text()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("binary");
        SeedMixed(fixture, topic, Undecidable, Decidable, () => [0xff, 0xfe, 0x01], () => Utf8("plain words"));

        KafkaScanResult result = await ScanAsync(fixture, topic, "value ~ 'needle'");

        result.Undecidable.Should().Be(Undecidable);
        result.UndecidableSentences.Should().Equal("1,204 records had a value that could not be read as text");
    }

    [Fact]
    public async Task A_decoder_that_throws_is_counted_as_could_not_be_decoded_by_the_topics_decoder()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("decoder");
        fixture.Declare(topic, static t => t.Value(new ThrowsOnBad()));
        SeedMixed(fixture, topic, Undecidable, Decidable, () => Utf8("bad"), () => Utf8("fine"));

        KafkaScanResult result = await ScanAsync(fixture, topic, "value ~ 'needle'");

        result.Undecidable.Should().Be(Undecidable);
        result.UndecidableSentences.Should().Equal("1,204 records could not be decoded by the topic's decoder");
    }

    [Fact]
    public async Task A_numeric_leaf_too_large_to_compare_is_counted()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("number");
        SeedMixed(fixture, topic, Undecidable, Decidable, () => Utf8("""{"n":1e400}"""), () => Utf8("""{"n":2}"""));

        KafkaScanResult result = await ScanAsync(fixture, topic, "$.n = 1");

        result.Undecidable.Should().Be(Undecidable);
        result.UndecidableSentences.Should().Equal("1,204 records had a number too large to compare");
    }

    [Fact]
    public async Task Invalid_base64_data_is_counted()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("base64");
        WireHeader contentType = new("content-type", Utf8("application/cloudevents+json"));

        for (int i = 0; i < Undecidable + Decidable; i++)
        {
            bool bad = i % 5 < 2;
            string body = bad
                ? """{"specversion":"1.0","datacontenttype":"application/json","data_base64":"!!not base64!!"}"""
                : """{"specversion":"1.0","datacontenttype":"application/json","data":{"kind":"other"}}""";

            fixture.ProduceRaw(topic, 0, 1, Utf8("k"), Utf8(body), contentType);
        }

        KafkaScanResult result = await ScanAsync(fixture, topic, "$.kind = 'ok'");

        // 2 of every 5 of 3,000 records.
        result.Undecidable.Should().Be(1_200);
        result.UndecidableSentences.Should().Equal("1,200 records had CloudEvents data_base64 that is not valid base64");
    }

    [Fact]
    public async Task A_value_over_the_decode_limit_is_counted_as_larger_than_the_decode_limit()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxDecodeBytes = 64 * 1024);
        string topic = fixture.Topic("large");
        string large = "{\"kind\":\"" + new string('x', 70_000) + "\"}";

        for (int i = 0; i < 60; i++)
        {
            fixture.ProduceRaw(topic, 0, 1, Utf8("k"), Utf8(i % 3 == 0 ? large : """{"kind":"other"}"""));
        }

        KafkaScanResult result = await ScanAsync(fixture, topic, "$.kind = 'ok'");

        result.Undecidable.Should().Be(20);
        result.UndecidableSentences.Should().Equal("20 records were larger than the decode limit");
    }

    [Fact]
    public async Task A_rule_that_cannot_be_applied_is_counted_as_could_not_be_searched()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("rules");
        fixture.Declare(topic, static t => t.Redact(static r => r.JsonPath("$.secret")));
        SeedMixed(fixture, topic, Undecidable, Decidable, () => Utf8("a text value"), () => Utf8("""{"kind":"other"}"""));

        KafkaScanResult result = await ScanAsync(fixture, topic, "value ~ 'needle'");

        result.Undecidable.Should().Be(Undecidable);
        result.UndecidableSentences.Should().Equal("1,204 records could not be searched, because a redaction rule could not be applied to their value");
        result.Decodes.ValueRedacted.Should().Be(Undecidable + Decidable);
    }

    [Fact]
    public async Task Several_reasons_are_counted_separately_and_the_matches_are_not_among_them()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("several");

        for (int i = 0; i < 100; i++)
        {
            byte[] value = i switch
            {
                < 10 => Utf8("{ broken"),
                < 15 => Utf8("""{"n":1e400}"""),
                < 20 => Utf8("""{"n":1}"""),
                _ => Utf8("""{"n":2}"""),
            };
            fixture.ProduceRaw(topic, 0, 1, Utf8("k"), value);
        }

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "$.n = 1", grant: Payload));

        matches.Should().HaveCount(5);
        result.Undecidable.Should().Be(15);
        result.UndecidableSentences.Should().BeEquivalentTo("10 records could not be decoded as JSON", "5 records had a number too large to compare");
    }

    private sealed class ThrowsOnBad : IKafkaRecordDecoder
    {
        public string Name => "ThrowsOnBad";

        public KafkaDecodeResult Decode(KafkaDecodeContext context) =>
            Encoding.UTF8.GetString(context.Value.Span) == "bad" ? throw new InvalidOperationException("bad value") : KafkaDecodeResult.Text("fine", "Fine");
    }
}
