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
    public void A_partition_the_scan_stopped_inside_is_told_from_one_it_never_reached()
    {
        KafkaSearchText.State(PartitionEndState.NotReached, 50).Should().Be("stopped before its end");
        KafkaSearchText.State(PartitionEndState.NotReached, 0).Should().Be("not reached");
        KafkaSearchText.State(PartitionEndState.EndOfWindow, 50).Should().Be("read to the window's end");
    }

    [Fact]
    public void A_short_window_shows_times_only()
    {
        TimeZoneInfo helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        DateTimeOffset from = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(from, from.AddHours(1), helsinki).Should().Be("10:00–11:00 Europe/Helsinki (UTC+02:00) · 08:00Z–09:00Z");
    }

    [Fact]
    public void A_zone_is_an_IANA_id_or_a_UTC_offset_and_never_a_Windows_id()
    {
        DateTimeOffset at = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);
        TimeZoneInfo windowsStyle = TimeZoneInfo.CreateCustomTimeZone("FLE Standard Time", TimeSpan.FromHours(2), "(UTC+02:00) Helsinki", "FLE Standard Time");
        TimeZoneInfo negative = TimeZoneInfo.CreateCustomTimeZone("Pacific Standard Time", TimeSpan.FromHours(-8), "Pacific", "Pacific Standard Time");
        TimeZoneInfo half = TimeZoneInfo.CreateCustomTimeZone("India Standard Time", new TimeSpan(5, 30, 0), "India", "India Standard Time");

        KafkaSearchText.ZoneLabel(TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki"), at).Should().Be("Europe/Helsinki (UTC+02:00)", "an IANA id keeps its name and gains the offset");
        KafkaSearchText.ZoneLabel(windowsStyle, at).Should().Be("UTC+02:00");
        KafkaSearchText.ZoneLabel(negative, at).Should().Be("UTC-08:00");
        KafkaSearchText.ZoneLabel(half, at).Should().Be("UTC+05:30");
        KafkaSearchText.ZoneLabel(TimeZoneInfo.Utc, at).Should().Be("UTC");

        KafkaSearchText.WindowLine(at, at.AddHours(1), windowsStyle).Should().Be("10:00–11:00 UTC+02:00 · 08:00Z–09:00Z");
    }

    [Fact]
    public void A_skipped_local_time_names_the_zone_as_the_label_does_never_by_a_Windows_id()
    {
        TimeZoneInfo helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        TimeZoneInfo windowsStyle = TimeZoneInfo.CreateCustomTimeZone("FLE Standard Time", TimeSpan.FromHours(2), "(UTC+02:00) Helsinki", "FLE Standard Time");
        DateTime gap = new(2026, 3, 29, 3, 30, 0, DateTimeKind.Unspecified);

        helsinki.IsInvalidTime(gap).Should().BeTrue();
        KafkaSearchText.SkippedLocalTime("2026-03-29T03:30", gap, helsinki).Should().Be("2026-03-29T03:30 does not exist in Europe/Helsinki (UTC+02:00) (the clocks skip it).");
        KafkaSearchText.SkippedLocalTime("2026-03-29T03:30", gap, windowsStyle).Should().Be("2026-03-29T03:30 does not exist in UTC+02:00 (the clocks skip it).");
    }

    [Fact]
    public void The_topic_count_says_what_it_counts()
    {
        KafkaStudioText.TopicsSelected(1).Should().Be("1 of 20 topics selected (the most one search reads)");
    }

    [Fact]
    public void Continued_says_which_matches_the_page_shows_and_that_the_earlier_ones_were_cleared()
    {
        // N earlier matches, with and without an Export menu on the page.
        KafkaStudioText.ContinuedShowing(500, 500, canExport: true).Should().Be("Showing matches 501-1,000 of this search; the earlier 500 matches were cleared from the page. Export before Continue to keep them.");
        KafkaStudioText.ContinuedShowing(500, 500, canExport: false).Should().Be("Showing matches 501-1,000 of this search; the earlier 500 matches were cleared from the page.");
        KafkaStudioText.ContinuedShowing(4, 1, canExport: true).Should().Be("Showing match 5 of this search; the earlier 4 matches were cleared from the page. Export before Continue to keep it.");
        KafkaStudioText.ContinuedShowing(500, 0, canExport: true).Should().Be("No new matches in this run; the earlier 500 matches were cleared from the page. Export before Continue to keep them.");

        // One earlier match: singular.
        KafkaStudioText.ContinuedShowing(1, 3, canExport: false).Should().Be("Showing matches 2-4 of this search; the earlier match was cleared from the page.");

        // No earlier match (a budget stop before any match): nothing was cleared, so no clause and no advice.
        KafkaStudioText.ContinuedShowing(0, 3, canExport: true).Should().Be("Showing matches 1-3 of this search.");
        KafkaStudioText.ContinuedShowing(0, 1, canExport: true).Should().Be("Showing match 1 of this search.");
        KafkaStudioText.ContinuedShowing(0, 0, canExport: true).Should().Be("No new matches in this run.");
    }

    [Fact]
    public void A_window_of_a_day_or_more_shows_the_dates()
    {
        TimeZoneInfo helsinki = TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki");
        DateTimeOffset to = new(2026, 11, 12, 11, 48, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(to.AddHours(-24), to, helsinki).Should()
            .Be("2026-11-11 13:48–2026-11-12 13:48 Europe/Helsinki (UTC+02:00) · 2026-11-11 11:48Z–2026-11-12 11:48Z", "24 h must not read 13:48–13:48");
    }

    [Fact]
    public void A_short_window_across_midnight_shows_the_dates_too()
    {
        TimeZoneInfo utc = TimeZoneInfo.Utc;
        DateTimeOffset from = new(2026, 11, 11, 23, 30, 0, TimeSpan.Zero);

        KafkaSearchText.WindowLine(from, from.AddHours(1), utc).Should().Be("2026-11-11 23:30–2026-11-12 00:30 UTC · 2026-11-11 23:30Z–2026-11-12 00:30Z");
    }
}
