using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>The sentences the search page builds around a count and a window (UI-VERIFY wording): one helper says a count, and no sentence says "1 records".</summary>
public class KafkaSearchTextTests
{
    private static KafkaScanResult Result(
        KafkaScanStopReason reason = KafkaScanStopReason.Completed,
        long records = 0,
        long matches = 0,
        long outside = 0,
        long noTimestamp = 0,
        int groupRefusals = 0,
        int partitions = 1,
        IReadOnlyList<KafkaScanGap>? gaps = null) =>
        new(
            Guid.Empty,
            reason,
            TimeSpan.FromSeconds(1),
            [.. Enumerable.Range(0, partitions).Select(i => new KafkaPartitionResult("orders", i, 0, 1, 1, PartitionEndState.EndOfWindow, 0, 0, 0, 0, 0))],
            records,
            2_048,
            matches,
            0,
            outside,
            noTimestamp,
            groupRefusals,
            new Dictionary<KafkaUnknownReason, long>(),
            gaps ?? [],
            [],
            [],
            new KafkaScanCursor([]),
            new DecodeCounters(),
            new DecodeCounters());

    [Theory]
    [InlineData(0, "0 matches")]
    [InlineData(1, "1 match")]
    [InlineData(2, "2 matches")]
    [InlineData(1_200, "1,200 matches")]
    public void A_count_says_its_noun_in_the_right_number(long value, string expected)
    {
        KafkaDisplay.Count(value, "match", "matches").Should().Be(expected);
    }

    [Fact]
    public void A_noun_without_a_plural_takes_an_s()
    {
        KafkaDisplay.Count(1, "record").Should().Be("1 record");
        KafkaDisplay.Count(3, "record").Should().Be("3 records");
        KafkaDisplay.Agree(1, "was", "were").Should().Be("was");
        KafkaDisplay.Agree(0, "was", "were").Should().Be("were");
    }

    [Fact]
    public void The_stop_sentences_use_singular_forms()
    {
        KafkaScanLimits limits = new() { MaxMatches = 1 };

        KafkaSearchText.Stop(Result(records: 1, matches: 0), limits).Should().Be("No match in the 1 record read from 1 partition.");
        KafkaSearchText.Stop(Result(records: 1, matches: 1), limits).Should().Be("Read every offset in the window: 1 record, 2 KiB, 1 match.");
        KafkaSearchText.Stop(Result(KafkaScanStopReason.MatchLimit), limits).Should().StartWith("Stopped at 1 match, the most a search keeps");
        KafkaSearchText.Stop(Result(KafkaScanStopReason.RecordBudget, records: 1), limits).Should().StartWith("Stopped after 1 record, this search's budget");
        KafkaSearchText.Stop(Result(records: 2, matches: 2, partitions: 3), limits).Should().Be("Read every offset in the window: 2 records, 2 KiB, 2 matches.");
        KafkaSearchText.Stop(Result(records: 0, matches: 0, partitions: 3), limits).Should().Be("No match in the 0 records read from 3 partitions.");
    }

    [Fact]
    public void The_caveats_use_singular_forms_and_agree_with_their_verbs()
    {
        IReadOnlyList<string> one = KafkaSearchText.Caveats(Result(
            outside: 1,
            noTimestamp: 1,
            groupRefusals: 1,
            gaps: [new KafkaScanGap("orders", 0, 5, 5, 1)]));

        one.Should().Contain("1 offset of orders/0 (5–5) was removed by retention while the search read them.");
        one.Should().Contain("1 record was read past the window's edges and left out by their own timestamp.");
        one.Should().Contain("1 record had no timestamp and was left out.");
        one.Should().Contain("1 consumer could not use its group (expected without a group ACL; nothing was affected).");

        IReadOnlyList<string> many = KafkaSearchText.Caveats(Result(
            outside: 3,
            noTimestamp: 2,
            groupRefusals: 2,
            gaps: [new KafkaScanGap("orders", 0, 5, 8, 4)]));

        many.Should().Contain("4 offsets of orders/0 (5–8) were removed by retention while the search read them.");
        many.Should().Contain("3 records were read past the window's edges and left out by their own timestamp.");
        many.Should().Contain("2 records had no timestamp and were left out.");
        many.Should().Contain("2 consumers could not use their group (expected without a group ACL; nothing was affected).");
    }

    [Fact]
    public void The_continued_line_and_the_audit_messages_use_singular_forms()
    {
        KafkaStudioText.ContinuedAfter(1).Should().Be("Continued after offsets read by the previous search (1 record).");
        KafkaStudioText.ContinuedAfter(50).Should().Be("Continued after offsets read by the previous search (50 records).");
        KafkaStudioText.OpenTransaction(1).Should().EndWith("on 1 partition.");
        KafkaStudioText.OpenTransaction(2).Should().EndWith("on 2 partitions.");
    }

    [Fact]
    public void A_short_window_shows_times_only()
    {
        TimeZoneInfo helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        DateTimeOffset from = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(from, from.AddHours(1), helsinki).Should().Be("10:00–11:00 Europe/Helsinki · 08:00Z–09:00Z");
    }

    [Fact]
    public void A_window_of_a_day_or_more_shows_the_dates()
    {
        TimeZoneInfo helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        DateTimeOffset to = new(2026, 11, 12, 11, 48, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(to.AddHours(-24), to, helsinki).Should()
            .Be("2026-11-11 13:48–2026-11-12 13:48 Europe/Helsinki · 2026-11-11 11:48Z–2026-11-12 11:48Z", "24 h must not read 13:48–13:48");
    }

    [Fact]
    public void A_short_window_across_midnight_shows_the_dates_too()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DateTimeOffset from = new(2026, 11, 11, 23, 30, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(from, from.AddHours(1), utc).Should().Be("2026-11-11 23:30–2026-11-12 00:30 UTC · 2026-11-11 23:30Z–2026-11-12 00:30Z");
    }
}
