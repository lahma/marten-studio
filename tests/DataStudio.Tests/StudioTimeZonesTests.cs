using DataStudio.Services;

namespace DataStudio.Tests;

/// <summary>#80: a time zone is named by its IANA id wherever the studio lists or labels one, on any operating system, and never fails for want of a conversion.</summary>
public class StudioTimeZonesTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_Windows_id_is_converted_for_the_region_asked_and_for_its_default_without_one()
    {
        // What a host can convert depends on its globalization data, and the two forms are independent: a GitHub Ubuntu runner converts a Windows
        // id for a region it is given and none without a region; a host with no such data converts neither. Where a form is missing the id is
        // kept, never an error.
        if (ConvertsForARegion)
        {
            StudioTimeZones.IanaId("FLE Standard Time", "FI").Should().Be("Europe/Helsinki", "the region picks Helsinki among the zones the id stands for");

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId("Pacific Standard Time", "FI", out string? pacific))
            {
                StudioTimeZones.IanaId("Pacific Standard Time", "FI").Should().Be(pacific).And.Be("America/Los_Angeles");
            }
        }
        else
        {
            StudioTimeZones.IanaId("FLE Standard Time", "FI").Should().Be("FLE Standard Time");
        }

        if (ConvertsWithoutARegion)
        {
            StudioTimeZones.IanaId("FLE Standard Time", "XX").Should().Contain("/").And.NotContain(" ", "a region with no mapping falls back to the id's default");
            StudioTimeZones.IanaId("FLE Standard Time", null).Should().Contain("/").And.NotContain(" ");
        }
        else
        {
            StudioTimeZones.IanaId("FLE Standard Time", null).Should().Be("FLE Standard Time");
        }
    }

    private static bool ConvertsForARegion => TimeZoneInfo.TryConvertWindowsIdToIanaId("FLE Standard Time", "FI", out string? _);

    private static bool ConvertsWithoutARegion => TimeZoneInfo.TryConvertWindowsIdToIanaId("FLE Standard Time", out string? _);

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
        StudioTimeZones.Label(windowsStyle, At, "FI").Should().Be(
            ConvertsForARegion ? "Europe/Helsinki (UTC+02:00)" : "FLE Standard Time (UTC+02:00)", "a host that cannot convert the id names the zone by its own");
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

    private static (string Id, TimeZoneInfo Zone) Zone(string id, int hours) =>
        (id, TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), id, id));

    [Fact]
    public void The_options_hold_the_circuits_zone_and_the_browsers_under_their_own_ids_when_the_list_lacks_them()
    {
        // A Windows host in another region lists FLE as Europe/Kiev; a browser says Europe/Helsinki.
        (string Id, TimeZoneInfo Zone)[] listed = [Zone("Africa/Abidjan", 0), Zone("Europe/Kiev", 2), Zone("Pacific/Niue", -11)];
        (string Id, TimeZoneInfo Zone) helsinki = Zone("Europe/Helsinki", 2);

        IReadOnlyList<StudioTimeZones.ZoneOption> options = StudioTimeZones.Options(listed, helsinki, helsinki);

        options.Select(static x => x.Id).Should().Equal("Europe/Helsinki", "Africa/Abidjan", "Europe/Kiev", "Pacific/Niue");
        options.Single(static x => x.IsBrowser).Id.Should().Be("Europe/Helsinki");
        options.Select(static x => x.Id).Should().OnlyHaveUniqueItems("the circuit's zone and the browser's are one entry when they are one zone");
    }

    [Fact]
    public void The_options_put_the_selected_zone_first_then_the_browsers_and_mark_only_the_browsers()
    {
        (string Id, TimeZoneInfo Zone)[] listed = [Zone("Africa/Abidjan", 0), Zone("Pacific/Niue", -11)];
        (string Id, TimeZoneInfo Zone) selected = Zone("Asia/Calcutta", 5);
        (string Id, TimeZoneInfo Zone) browser = Zone("Europe/Helsinki", 2);

        IReadOnlyList<StudioTimeZones.ZoneOption> options = StudioTimeZones.Options(listed, selected, browser);

        options.Select(static x => x.Id).Should().Equal("Asia/Calcutta", "Europe/Helsinki", "Africa/Abidjan", "Pacific/Niue");
        options.Where(static x => x.IsBrowser).Select(static x => x.Id).Should().Equal("Europe/Helsinki");
    }

    [Fact]
    public void The_options_add_nothing_for_a_zone_the_list_holds_and_mark_the_listed_one_the_browser_reported()
    {
        (string Id, TimeZoneInfo Zone)[] listed = [Zone("Africa/Abidjan", 0), Zone("Europe/Helsinki", 2), Zone("Pacific/Niue", -11)];

        IReadOnlyList<StudioTimeZones.ZoneOption> options = StudioTimeZones.Options(listed, listed[2], listed[1]);

        options.Select(static x => x.Id).Should().Equal("Africa/Abidjan", "Europe/Helsinki", "Pacific/Niue");
        options.Where(static x => x.IsBrowser).Select(static x => x.Id).Should().Equal("Europe/Helsinki");
        StudioTimeZones.Options(listed, listed[2], null).Should().NotContain(static x => x.IsBrowser);
        StudioTimeZones.Options(listed, null, null).Select(static x => x.Id).Should().Equal(listed.Select(static x => x.Id));
    }

    [Fact]
    public void The_list_the_options_are_made_from_is_the_host_list_and_is_never_changed()
    {
        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed("FI");
        int before = listed.Count;

        _ = StudioTimeZones.Options(listed, Zone("Nowhere/Land", 3), Zone("Elsewhere/Land", 4));

        listed.Count.Should().Be(before);
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
