using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Windows;
using KStudio.Core.Scanning;
using KStudio.Tests.Contract;

using static KStudio.Tests.Predicates.ValueHarness;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The value budget of a record read (decision 4.2 of D-PAYLOAD): the record asked for may keep up to the focus budget of value, its
/// neighbours keep what the match budget leaves, so a record read holds at most 21 matches plus one value of the decode limit.
/// </summary>
public class RecordReadFocusTests
{
    private static readonly string Big = "{\"a\":\"" + new string('x', 100_000) + "\"}";

    private static Task<KafkaRecordRead> ReadAsync(ScanFixture fixture, string topic, long offset, KafkaReadGrant grant, int focus, int neighbours = 2) =>
        fixture.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 0, offset, neighbours, grant, KafkaIsolation.Committed, focus),
            fixture.Owner,
            TestContext.Current.CancellationToken);

    private static string Seed(ScanFixture fixture)
    {
        string topic = fixture.Topic("focus");

        for (int i = 0; i < 12; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, Big);
        }

        return topic;
    }

    [Fact]
    public async Task The_record_asked_for_keeps_its_whole_value_and_its_neighbours_keep_the_match_budget()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = Seed(fixture);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 6, Payload, focus: 1024 * 1024);

        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.Record!.ValueText.Should().Be(Big);
        read.Record.ValueTruncated.Should().BeFalse();
        read.Record.ValueLength.Should().Be(Encoding.UTF8.GetByteCount(Big));

        KafkaMatch[] neighbours = [.. read.Before, .. read.After];

        neighbours.Should().HaveCount(4);
        neighbours.Should().OnlyContain(static x => x.ValueTruncated && Encoding.UTF8.GetByteCount(x.ValueText!) <= 64 * 1024);
    }

    [Fact]
    public async Task Without_a_focus_budget_the_record_asked_for_is_cut_like_a_neighbour()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = Seed(fixture);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 6, Payload, focus: 0);

        read.Record!.ValueTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(read.Record.ValueText!).Should().BeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task A_focus_budget_below_what_is_left_changes_nothing_and_the_envelope_grant_ignores_it()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = Seed(fixture);

        KafkaRecordRead small = await ReadAsync(fixture, topic, 6, Payload, focus: 1_000);
        KafkaRecordRead envelope = await ReadAsync(fixture, topic, 6, KafkaReadGrant.Envelope, focus: 1024 * 1024);

        Encoding.UTF8.GetByteCount(small.Record!.ValueText!).Should().BeLessThanOrEqualTo(64 * 1024).And.BeGreaterThan(1_000, "the budget left is what a neighbour gets");
        envelope.Record!.ValueText.Should().BeNull("a value is read only under the payload grant");
        envelope.Record.ValueLength.Should().BeGreaterThan(100_000);
    }

    [Fact]
    public async Task The_focus_budget_is_the_decode_limit_at_most_because_a_larger_json_value_is_not_decoded_at_all()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxDecodeBytes = 64 * 1024);
        string topic = Seed(fixture);

        KafkaRecordRead read = await ReadAsync(fixture, topic, 6, Payload, focus: 64 * 1024);

        read.Record!.ValueKind.Should().Be(KafkaValueKind.Undecodable);
        read.Record.UndecodableReason.Should().Be(KafkaUndecodableReason.TooLarge);
        read.Record.ValueText.Should().BeNull();
    }

    [Fact]
    public async Task A_text_value_over_the_decode_limit_is_cut_and_says_so()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxDecodeBytes = 64 * 1024);
        string topic = fixture.Topic("focus-text");
        string text = "words " + new string('w', 100_000);

        for (int i = 0; i < 3; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, text);
        }

        KafkaRecordRead read = await ReadAsync(fixture, topic, 1, Payload, focus: 64 * 1024, neighbours: 1);

        read.Record!.ValueTruncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(read.Record.ValueText!).Should().BeLessThanOrEqualTo(64 * 1024).And.BeGreaterThan(60 * 1024);
    }
}
