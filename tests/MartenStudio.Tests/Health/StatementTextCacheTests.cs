using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// Security review item 3: the texts a sweep read are held per store and database by <c>queryid</c>, so a sweep that
/// keeps only statements whose text it holds makes no text call - and the holding is bounded.
/// </summary>
public class StatementTextCacheTests
{
    [Fact]
    public void A_text_is_held_for_its_own_store_and_database_only()
    {
        StatementTextCache cache = new();
        cache.Remember("default", "db1", [new StatementTextRow(7, "select $1", false)]);

        cache.TryGet("default", "db1", 7, out StatementTextRow? held).Should().BeTrue();
        held!.Text.Should().Be("select $1");

        cache.TryGet("default", "db2", 7, out _).Should().BeFalse("a queryid is a hash of a statement's shape, and another database's is another statement");
        cache.TryGet("other", "db1", 7, out _).Should().BeFalse();
        cache.TryGet("default", "db1", 8, out _).Should().BeFalse();
        cache.Known("default", "db1").Should().Equal(7L);
    }

    [Fact]
    public void A_database_that_would_hold_more_than_its_bound_starts_again_with_the_newest()
    {
        StatementTextCache cache = new();
        cache.Remember("default", "db", Rows(1, StatementTextCache.MaxPerDatabase));
        cache.Known("default", "db").Should().HaveCount(StatementTextCache.MaxPerDatabase);

        // A text it already holds replaces itself and costs nothing.
        cache.Remember("default", "db", [new StatementTextRow(1, "select $2", false)]);
        cache.Known("default", "db").Should().HaveCount(StatementTextCache.MaxPerDatabase);
        cache.TryGet("default", "db", 1, out StatementTextRow? replaced).Should().BeTrue();
        replaced!.Text.Should().Be("select $2");

        // One more starts the database again: one text call's worth, never an unbounded map.
        cache.Remember("default", "db", Rows(StatementTextCache.MaxPerDatabase + 1, 1));
        cache.Known("default", "db").Should().Equal(StatementTextCache.MaxPerDatabase + 1L);
    }

    /// <summary>
    /// W5-Q1-fix F7: at its bound a database forgets what the sweep remembering did not keep and keeps what it did, so the
    /// next sweep - whose statements are mostly this one's - still finds their texts and pays for no whole text file.
    /// </summary>
    [Fact]
    public void At_its_bound_a_database_keeps_the_texts_the_sweep_kept_and_forgets_the_rest()
    {
        StatementTextCache cache = new();
        cache.Remember("default", "db", Rows(1, StatementTextCache.MaxPerDatabase));

        // This sweep kept 1..200 (found here) and 1001..1020 (just read): 201..256 are nobody's any more.
        long[] kept = [.. Enumerable.Range(1, 200).Select(static x => (long) x), .. Enumerable.Range(1001, 20).Select(static x => (long) x)];
        cache.Remember("default", "db", Rows(1001, 20), kept);

        IReadOnlySet<long> known = cache.Known("default", "db");
        known.Should().HaveCount(220).And.BeEquivalentTo(kept);
        cache.TryGet("default", "db", 200, out _).Should().BeTrue("a text the sweep kept from the cache is still held");
        cache.TryGet("default", "db", 201, out _).Should().BeFalse("a text no sweep kept is what goes");

        // A sweep that kept more than the bound holds is more than one sweep keeps: nothing past the bound, either way.
        cache.Remember("default", "db", Rows(5000, 40), [.. known, .. Enumerable.Range(5000, 40).Select(static x => (long) x)]);
        cache.Known("default", "db").Count.Should().BeLessThanOrEqualTo(StatementTextCache.MaxPerDatabase);
    }

    private static List<StatementTextRow> Rows(long first, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new StatementTextRow(first + i, "select " + i, false))];
}
