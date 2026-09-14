using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The bounded, expiring cache behind <c>ColumnCatalog</c> and <c>IndexCatalog</c>.
/// </summary>
/// <remarks>
/// Both properties are corrections rather than optimisations. Without expiry, a studio that has read a
/// table's columns once goes on selecting that column set for the life of the process — through every
/// migration somebody else applies. Without a bound, the cache grows with the number of distinct table
/// names a long-lived process is ever asked about, and the table name comes from a URL.
/// </remarks>
public class CatalogCacheTests
{
    [Fact]
    public void A_value_is_remembered_until_the_ttl_runs_out()
    {
        var cache = new CatalogCache<string> { Ttl = TimeSpan.FromMinutes(5) };

        cache.Set("a", "one");

        cache.TryGet("a", out var value).Should().BeTrue();
        value.Should().Be("one");
    }

    [Fact]
    public void An_expired_value_is_not_returned()
    {
        // Zero, so the entry is already past its expiry by the time it is asked for: no waiting, and no
        // clock to fake.
        var cache = new CatalogCache<string> { Ttl = TimeSpan.Zero };

        cache.Set("a", "one");

        cache.TryGet("a", out _).Should().BeFalse("a migration somebody else applied must become visible");
    }

    [Fact]
    public void A_value_that_was_never_there_is_a_miss() =>
        new CatalogCache<string>().TryGet("nothing", out _).Should().BeFalse();

    [Fact]
    public void The_cache_is_bounded_and_forgets_everything_rather_than_growing()
    {
        var cache = new CatalogCache<string> { MaxEntries = 3 };

        for (var i = 0; i < 20; i++)
        {
            cache.Set(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "value");
        }

        cache.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void One_key_can_be_forgotten_and_so_can_all_of_them()
    {
        var cache = new CatalogCache<string>();

        cache.Set("a", "one");
        cache.Set("b", "two");

        cache.Remove("a");

        cache.TryGet("a", out _).Should().BeFalse();
        cache.TryGet("b", out _).Should().BeTrue();

        cache.Clear();

        cache.TryGet("b", out _).Should().BeFalse();
        cache.Count.Should().Be(0);
    }
}
