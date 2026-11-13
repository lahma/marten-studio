using KStudio.Core.Decoding;
using KStudio.Core.Index;
using KStudio.Core.Scanning;
using KStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// What the search page says of a scan the host's record index narrowed (AGENTS.md D48, D-M5 decision 5.3): the banner never says "every offset in the
/// window", a negative is said of the ranges read, and the caveat sits beside matches and negatives alike.
/// </summary>
public class KafkaSearchIndexTextTests
{
    private static KafkaIndexNarrowing Narrowed() =>
        KafkaIndexNarrowing.Narrowed([new KafkaIndexRange("orders", 1, 12, 15, 0, 20), new KafkaIndexRange("orders", 0, 4, 6, 0, 20)], 2, 0, 40);

    private static KafkaScanResult Result(KafkaScanStopReason reason = KafkaScanStopReason.Completed, long matches = 0, KafkaIndexNarrowing? index = null) =>
        new(
            Guid.Empty,
            reason,
            TimeSpan.FromSeconds(1),
            [new KafkaPartitionResult("orders", 0, 0, 1, 1, PartitionEndState.EndOfWindow, 0, 0, 0, 0, 0)],
            5,
            2_048,
            matches,
            0,
            0,
            0,
            0,
            new Dictionary<KafkaUnknownReason, long>(),
            [],
            [],
            [],
            new KafkaScanCursor([]),
            new DecodeCounters(),
            new DecodeCounters())
        {
            Index = index,
        };

    [Fact]
    public void A_narrowed_negative_is_said_only_of_the_ranges_read()
    {
        KafkaSearchText.Stop(Result(index: Narrowed()), new KafkaScanLimits())
            .Should().Be("No match in the 2 ranges the host's record index named (5 offsets). The rest of the window was not read.");
    }

    [Fact]
    public void A_narrowed_positive_does_not_claim_the_window_was_read()
    {
        string stop = KafkaSearchText.Stop(Result(matches: 2, index: Narrowed()), new KafkaScanLimits());

        stop.Should().Be("Read every offset in the ranges the host's record index named (5 offsets): 5 records, 2 KiB, 2 matches. The rest of the window was not read.");
        stop.Should().NotContain("in the window:");
    }

    [Fact]
    public void A_scan_the_index_did_not_narrow_says_what_it_always_said()
    {
        KafkaSearchText.Stop(Result(matches: 2), new KafkaScanLimits()).Should().Be("Read every offset in the window: 5 records, 2 KiB, 2 matches.");
        KafkaSearchText.Stop(Result(), new KafkaScanLimits()).Should().Be("No match in the 5 records read from 1 partition.");
        KafkaSearchText.Caveats(Result()).Should().BeEmpty();
    }

    [Fact]
    public void The_caveat_is_drawn_beside_matches_and_negatives_alike()
    {
        string caveat = "Read only where the host's record index pointed (5 of 40 offsets); other matches in the window were not looked for.";

        KafkaSearchText.Caveats(Result(matches: 2, index: Narrowed())).Should().Contain(caveat);
        KafkaSearchText.Caveats(Result(index: Narrowed())).Should().Contain(caveat);
        KafkaSearchText.Caveats(Result(KafkaScanStopReason.MatchLimit, matches: 1, index: Narrowed())).Should().Contain(caveat);
    }

    [Fact]
    public void The_other_stop_sentences_are_unchanged_by_a_narrowing()
    {
        KafkaScanLimits limits = new() { MaxMatches = 1 };

        KafkaSearchText.Stop(Result(KafkaScanStopReason.MatchLimit, 1, Narrowed()), limits).Should().StartWith("Stopped at 1 match, the most a search keeps");
        KafkaSearchText.Stop(Result(KafkaScanStopReason.Cancelled, 0, Narrowed()), limits).Should().Be("Stopped by you. Continue reads on from where it stopped.");
    }

    [Fact]
    public void The_settings_line_and_the_buttons_are_the_designs_words()
    {
        KafkaSearchText.RecordIndexLine("MyRecordIndex").Should().Be("Record index: MyRecordIndex");
        KafkaSearchText.RecordIndexLine(null).Should().Be("Record index: none");
        KafkaSearchText.SearchWholeWindow.Should().Be("Search the whole window");
        KafkaSearchText.IgnoreRecordIndex.Should().Be("Ignore the host's record index");
    }
}
