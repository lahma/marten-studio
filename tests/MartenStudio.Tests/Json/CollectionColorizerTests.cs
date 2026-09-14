using MartenStudio.Services;

namespace MartenStudio.Tests.Json;

public class CollectionColorizerTests
{
    [Fact]
    public void There_are_twelve_distinct_hues_in_each_ring()
    {
        CollectionColorizer.Hues.Should().HaveCount(12).And.OnlyHaveUniqueItems();
        CollectionColorizer.EventTypeHues.Should().HaveCount(12).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void No_hue_can_be_mistaken_for_a_status()
    {
        foreach (var hue in CollectionColorizer.Hues.Concat(CollectionColorizer.EventTypeHues))
        {
            CollectionColorizer.CircularDistance(hue, CollectionColorizer.DangerHue)
                .Should().BeGreaterThan(CollectionColorizer.StatusExclusionDegrees, "hue {0} must not read as a failure", hue);

            CollectionColorizer.CircularDistance(hue, CollectionColorizer.SuccessHue)
                .Should().BeGreaterThan(CollectionColorizer.StatusExclusionDegrees, "hue {0} must not read as a success", hue);

            CollectionColorizer.IsSafeHue(hue).Should().BeTrue();
        }
    }

    [Fact]
    public void The_status_hues_themselves_are_not_safe()
    {
        CollectionColorizer.IsSafeHue(0).Should().BeFalse();
        CollectionColorizer.IsSafeHue(140).Should().BeFalse();
        CollectionColorizer.IsSafeHue(355).Should().BeFalse("hue distance wraps around the wheel");
    }

    [Fact]
    public void The_same_alias_always_gets_the_same_hue()
    {
        var first = CollectionColorizer.HueFor("user");
        var second = CollectionColorizer.HueFor("user");

        second.Should().Be(first);
        CollectionColorizer.Hues.Should().Contain(first);
    }

    [Fact]
    public void The_mapping_is_pinned_so_a_collection_keeps_its_colour_across_releases()
    {
        CollectionColorizer.Fnv1a("user").Should().Be(1618501362);
        CollectionColorizer.Fnv1a("order").Should().Be(1932267671);
        CollectionColorizer.Fnv1a(string.Empty).Should().Be(2166136261);

        CollectionColorizer.SlotFor("user").Should().Be(6);
        CollectionColorizer.HueFor("user").Should().Be(CollectionColorizer.Hues[6]);
        CollectionColorizer.SlotFor("order").Should().Be(11);
    }

    [Fact]
    public void Event_types_use_a_ring_of_their_own_so_they_never_match_a_collection()
    {
        foreach (var name in new[] { "user", "order", "AccountOpened", "shipment" })
        {
            CollectionColorizer.EventTypeHueFor(name).Should().NotBe(CollectionColorizer.HueFor(name));
        }
    }

    [Fact]
    public void Different_aliases_land_across_the_ring_rather_than_in_one_slot()
    {
        var aliases = new[]
        {
            "user", "order", "invoice", "shipment", "account", "customer",
            "product", "payment", "subscription", "ticket", "review", "session",
        };

        aliases.Select(CollectionColorizer.SlotFor).Distinct().Should().HaveCountGreaterThan(5);
    }

    [Fact]
    public void An_empty_or_missing_alias_still_gets_a_colour()
    {
        CollectionColorizer.HueFor(null).Should().Be(CollectionColorizer.Hues[0]);
        CollectionColorizer.HueFor(string.Empty).Should().Be(CollectionColorizer.Hues[0]);
    }

    [Fact]
    public void Circular_distance_takes_the_short_way_round()
    {
        CollectionColorizer.CircularDistance(350, 10).Should().Be(20);
        CollectionColorizer.CircularDistance(10, 350).Should().Be(20);
        CollectionColorizer.CircularDistance(0, 180).Should().Be(180);
    }
}
