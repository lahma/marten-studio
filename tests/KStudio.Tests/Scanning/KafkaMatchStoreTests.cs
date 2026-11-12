using KStudio.Core.Scanning;
using KStudio.Tests.Redaction;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The match store (D-M5 decision 3.4): a search's store is append-only and refuses when full, exactly as it always was; a live tail's is a ring
/// that evicts its oldest match, numbers every match from 0 and says how many a reader missed.
/// </summary>
public class KafkaMatchStoreTests
{
    private static KafkaMatch Match(long offset) => PayloadRedactionTests.Match(ScanFixture.Rec("t", 0, offset, 1), rules: null);

    private static long[] Offsets(IReadOnlyList<KafkaMatch> matches) => [.. matches.Select(static x => x.Offset)];

    [Fact]
    public void Without_the_ring_the_store_is_append_only_refuses_when_full_and_never_skips()
    {
        KafkaMatchStore store = new(3);

        store.TryAdd(Match(0)).Should().BeTrue();
        store.TryAdd(Match(1)).Should().BeTrue();
        store.TryAdd(Match(2)).Should().BeTrue();
        store.TryAdd(Match(3)).Should().BeFalse("a search keeps the first MaxMatches");

        store.IsRing.Should().BeFalse();
        store.IsFull.Should().BeTrue();
        store.Count.Should().Be(3);
        store.Added.Should().Be(3, "a refused match was never added");
        store.Evicted.Should().Be(0);
        Offsets(store.From(0)).Should().Equal(0, 1, 2);
        Offsets(store.From(2)).Should().Equal(2);
        store.From(3).Should().BeEmpty();
        store.From(99).Should().BeEmpty();

        // The sequence of a match is its index: the new overload answers what the old one does, and skips nothing.
        Offsets(store.From(1L, out long skipped)).Should().Equal(1, 2);
        skipped.Should().Be(0);
        store.From(5L, out skipped).Should().BeEmpty();
        skipped.Should().Be(0);
    }

    [Fact]
    public void A_ring_never_refuses_and_keeps_the_newest_in_order_numbering_every_match_from_zero()
    {
        KafkaMatchStore store = new(3, ring: true);

        for (int i = 0; i < 8; i++)
        {
            store.TryAdd(Match(i)).Should().BeTrue("a ring evicts its oldest match instead of refusing");
        }

        store.IsRing.Should().BeTrue();
        store.Count.Should().Be(3);
        store.Added.Should().Be(8);
        store.Evicted.Should().Be(5);
        Offsets(store.From(0)).Should().Equal([5L, 6L, 7L], "the kept matches, oldest first");
    }

    [Fact]
    public void A_reader_is_told_exactly_how_many_matches_were_evicted_before_it_read_them()
    {
        KafkaMatchStore store = new(4, ring: true);

        for (int i = 0; i < 10; i++)
        {
            store.TryAdd(Match(i));
        }

        // Sequences 6..9 are kept; 0..5 were evicted.
        Offsets(store.From(0L, out long skipped)).Should().Equal(6, 7, 8, 9);
        skipped.Should().Be(6);

        Offsets(store.From(4L, out skipped)).Should().Equal(6, 7, 8, 9);
        skipped.Should().Be(2, "sequences 4 and 5 were evicted before they were read");

        Offsets(store.From(6L, out skipped)).Should().Equal(6, 7, 8, 9);
        skipped.Should().Be(0);

        Offsets(store.From(8L, out skipped)).Should().Equal(8, 9);
        skipped.Should().Be(0);

        store.From(10L, out skipped).Should().BeEmpty();
        skipped.Should().Be(0, "a reader that has read everything missed nothing");

        // The next call asks for sequence + skipped + count, so a reader that follows that rule is told every match exactly once.
        long next = 0;
        long seen = 0;
        long missed = 0;

        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 7; i++)
            {
                store.TryAdd(Match(100 + (round * 7) + i));
            }

            IReadOnlyList<KafkaMatch> batch = store.From(next, out long lost);
            next += lost + batch.Count;
            seen += batch.Count;
            missed += lost;
        }

        (seen + missed).Should().Be(45, "the ten added before and the 35 added in the rounds were each either given to the reader or counted as missed");
        next.Should().Be(store.Added);
    }

    [Fact]
    public void A_ring_of_one_keeps_the_newest_and_a_ring_that_has_not_filled_keeps_everything()
    {
        KafkaMatchStore one = new(1, ring: true);
        one.TryAdd(Match(0));
        one.TryAdd(Match(1));
        one.TryAdd(Match(2));
        Offsets(one.From(0)).Should().Equal(2);
        one.Evicted.Should().Be(2);

        KafkaMatchStore roomy = new(10_000, ring: true);

        for (int i = 0; i < 40; i++)
        {
            roomy.TryAdd(Match(i));
        }

        roomy.Count.Should().Be(40);
        roomy.Evicted.Should().Be(0);
        Offsets(roomy.From(0L, out long skipped)).Should().Equal(Enumerable.Range(0, 40).Select(static x => (long) x));
        skipped.Should().Be(0);
    }

    [Fact]
    public void A_store_that_grows_to_its_capacity_keeps_every_match_in_order_across_the_growth()
    {
        KafkaMatchStore store = new(100, ring: true);

        for (int i = 0; i < 250; i++)
        {
            store.TryAdd(Match(i));
        }

        Offsets(store.From(0)).Should().Equal(Enumerable.Range(150, 100).Select(static x => (long) x));
        store.Evicted.Should().Be(150);
    }
}
