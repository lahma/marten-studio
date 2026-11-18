using DataStudio.Services;

namespace DataStudio.Tests;

/// <summary>#80: a time zone is named by its IANA id wherever the studio lists or labels one, on any operating system, and never fails for want of a conversion.</summary>
public class StudioTimeZonesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_Windows_id_is_converted_for_the_region_asked_and_for_its_default_without_one()
    {
        StudioTimeZones.IanaId("FLE Standard Time", "FI").Should().Be("Europe/Helsinki", "the region picks Helsinki among the zones the id stands for");
        StudioTimeZones.IanaId("FLE Standard Time", "XX").Should().Contain("/").And.NotContain(" ", "a region with no mapping falls back to the id's default");
        StudioTimeZones.IanaId("FLE Standard Time", null).Should().Contain("/").And.NotContain(" ");
        StudioTimeZones.IanaId("Pacific Standard Time", "FI").Should().Be("America/Los_Angeles");
    }

    [Fact]
    public void An_IANA_id_is_kept_and_so_is_one_that_cannot_be_converted()
    {
        StudioTimeZones.IanaId("Europe/Helsinki", "FI").Should().Be("Europe/Helsinki");
        StudioTimeZones.IanaId("Europe/Helsinki", null).Should().Be("Europe/Helsinki");
        StudioTimeZones.IanaId("UTC", "FI").Should().Be("UTC", "UTC is an IANA id as much as a Windows one, and the studio has always called it that");
        StudioTimeZones.IanaId("Studio Test Standard Time", "FI").Should().Be("Studio Test Standard Time", "a zone that cannot be converted keeps its own id rather than failing");
        StudioTimeZones.IanaId(string.Empty, "FI").Should().BeEmpty();
    }

    [Fact]
    public void A_label_is_the_IANA_id_and_the_offset()
    {
        TimeZoneInfo windowsStyle = TimeZoneInfo.CreateCustomTimeZone("FLE Standard Time", TimeSpan.FromHours(2), "Helsinki", "FLE Standard Time");
        TimeZoneInfo unknown = TimeZoneInfo.CreateCustomTimeZone("Studio Test Standard Time", TimeSpan.FromHours(-8), "Test", "Studio Test Standard Time");

        StudioTimeZones.Label(TimeZoneInfo.FindSystemTimeZoneById("Europe/Helsinki"), At, "FI").Should().Be("Europe/Helsinki (UTC+02:00)");
        StudioTimeZones.Label(windowsStyle, At, "FI").Should().Be("Europe/Helsinki (UTC+02:00)");
        StudioTimeZones.Label(unknown, At, "FI").Should().Be("Studio Test Standard Time (UTC-08:00)", "a list names a zone it cannot convert by its own id");
        StudioTimeZones.Label(TimeZoneInfo.Utc, At, "FI").Should().Be("UTC");
    }

    [Fact]
    public void A_label_of_a_name_already_held_converts_nothing_again()
    {
        TimeZoneInfo windowsStyle = TimeZoneInfo.CreateCustomTimeZone("FLE Standard Time", TimeSpan.FromHours(2), "Helsinki", "FLE Standard Time");

        StudioTimeZones.Label("Listed/Name", windowsStyle, At).Should().Be("Listed/Name (UTC+02:00)", "the list's own id is the name: the zone's id is not converted again");
        StudioTimeZones.Label("UTC", TimeZoneInfo.Utc, At).Should().Be("UTC");
    }

    [Fact]
    public void The_listed_zones_are_distinct_found_by_the_id_they_are_listed_under_and_in_the_order_of_those_ids()
    {
        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed("FI");

        listed.Should().NotBeEmpty();
        listed.Select(static x => x.Id).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);

        foreach ((string id, TimeZoneInfo zone) in listed)
        {
            TimeZoneInfo.FindSystemTimeZoneById(id).BaseUtcOffset.Should().Be(zone.BaseUtcOffset, "the id the select stores is one the studio can read back: " + id);
        }
    }
}
