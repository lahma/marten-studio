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

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId("Pacific Standard Time", "FI", out string? pacific) && TimeZoneInfo.TryFindSystemTimeZoneById(pacific, out TimeZoneInfo? _))
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

    // A conversion counts only when the host also knows the zone it names: a GitHub Ubuntu runner converts FLE Standard Time with no region to
    // Europe/Kiev, an old name its zone database no longer carries, and the studio then keeps the id it was given.
    private static bool ConvertsForARegion =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId("FLE Standard Time", "FI", out string? iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out TimeZoneInfo? _);

    private static bool ConvertsWithoutARegion =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId("FLE Standard Time", out string? iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out TimeZoneInfo? _);

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
    public void The_listed_zones_are_distinct_found_by_the_id_they_are_listed_under_and_the_IANA_ids_are_in_their_order()
    {
        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed("FI");

        listed.Should().NotBeEmpty();
        listed.Select(static x => x.Id).Should().OnlyHaveUniqueItems();

        // The IANA ids in the order of those ids; after them (#92) the Windows zones that have no IANA name, which are no part of that order.
        listed.Where(static x => !StudioTimeZones.IsUnconvertedWindowsId(x.Id)).Select(static x => x.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);

        foreach ((string id, TimeZoneInfo zone) in listed)
        {
            TimeZoneInfo.FindSystemTimeZoneById(id).BaseUtcOffset.Should().Be(zone.BaseUtcOffset, "the id the select stores is one the studio can read back: " + id);
        }
    }

    // ---- #92: current IANA names, obsolete Windows zones, and zones with no IANA name --------------------------------------------------

    private static readonly (string Legacy, string Current)[] Pairs =
    [
        ("Africa/Asmera", "Africa/Asmara"),
        ("America/Buenos_Aires", "America/Argentina/Buenos_Aires"),
        ("America/Catamarca", "America/Argentina/Catamarca"),
        ("America/Coral_Harbour", "America/Atikokan"),
        ("America/Cordoba", "America/Argentina/Cordoba"),
        ("America/Godthab", "America/Nuuk"),
        ("America/Indianapolis", "America/Indiana/Indianapolis"),
        ("America/Jujuy", "America/Argentina/Jujuy"),
        ("America/Louisville", "America/Kentucky/Louisville"),
        ("America/Mendoza", "America/Argentina/Mendoza"),
        ("Asia/Calcutta", "Asia/Kolkata"),
        ("Asia/Katmandu", "Asia/Kathmandu"),
        ("Asia/Rangoon", "Asia/Yangon"),
        ("Asia/Saigon", "Asia/Ho_Chi_Minh"),
        ("Atlantic/Faeroe", "Atlantic/Faroe"),
        ("Europe/Kiev", "Europe/Kyiv"),
        ("Pacific/Enderbury", "Pacific/Kanton"),
        ("Pacific/Ponape", "Pacific/Pohnpei"),
        ("Pacific/Truk", "Pacific/Chuuk"),
    ];

    public static TheoryData<string, string> LegacyNames
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach ((string legacy, string current) in Pairs)
            {
                data.Add(legacy, current);
            }

            return data;
        }
    }

    private static bool Finds(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    private static TimeZoneInfo Custom(string id, int hours, string displayName) =>
        TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(hours), displayName, id);

    [Theory]
    [MemberData(nameof(LegacyNames))]
    public void A_legacy_spelling_is_the_current_name_where_the_host_finds_a_zone_by_it_and_stays_where_it_does_not(string legacy, string current)
    {
        StudioTimeZones.CurrentName(legacy, static _ => true).Should().Be(current);
        StudioTimeZones.CurrentName(legacy, static _ => false).Should().Be(legacy, "a host that cannot find the current name keeps the spelling it can read");
        StudioTimeZones.CurrentName(legacy, name => name == legacy).Should().Be(legacy, "only the current name is asked about");
        StudioTimeZones.CurrentName(current, static _ => true).Should().Be(current, "a current name is not in the table");

        // The host's own answer decides the same way for the function every list and label asks.
        string expected = Finds(current) ? current : legacy;
        StudioTimeZones.CurrentName(legacy).Should().Be(expected);
        StudioTimeZones.IanaId(legacy, "FI").Should().Be(expected);
    }

    [Theory]
    [InlineData("Europe/Helsinki")]
    [InlineData("FLE Standard Time")]
    [InlineData("UTC")]
    [InlineData("")]
    [InlineData("Studio/Nowhere")]
    public void An_id_outside_the_table_is_never_changed_by_it(string id) =>
        StudioTimeZones.CurrentName(id, static _ => true).Should().Be(id);

    [Fact]
    public void A_legacy_and_a_current_spelling_of_one_zone_are_one_entry_under_the_current_name_where_the_host_finds_it()
    {
        TimeZoneInfo[] zones = [Custom("Asia/Calcutta", 5, "Calcutta"), Custom("Asia/Kolkata", 5, "Kolkata"), Custom("Africa/Abidjan", 0, "Abidjan")];

        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed(zones, "FI");

        if (Finds("Asia/Kolkata"))
        {
            listed.Select(static x => x.Id).Should().Equal("Africa/Abidjan", "Asia/Kolkata");
        }
        else
        {
            listed.Select(static x => x.Id).Should().Equal("Africa/Abidjan", "Asia/Calcutta", "Asia/Kolkata");
        }
    }

    [Theory]
    [InlineData("Kamchatka Standard Time")]
    [InlineData("Mid-Atlantic Standard Time")]
    public void The_obsolete_Windows_zones_are_left_out_of_the_list_and_offered_to_the_visitor_who_holds_one(string obsolete)
    {
        TimeZoneInfo old = Custom(obsolete, 12, "(UTC+12:00) Petropavlovsk-Kamchatsky - Old");
        TimeZoneInfo[] zones = [Custom("Europe/Helsinki", 2, "Helsinki"), old, Custom("Pacific/Niue", -11, "Niue")];

        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed(zones, "FI");

        listed.Select(static x => x.Id).Should().Equal("Europe/Helsinki", "Pacific/Niue");

        // A stored or reported zone the list lacks is an option of its own, as any such id is, under its display name.
        IReadOnlyList<StudioTimeZones.ZoneOption> options = StudioTimeZones.Options(listed, (obsolete, old), null);

        options.Select(static x => x.Id).Should().Equal(obsolete, "Europe/Helsinki", "Pacific/Niue");
        options[0].Name.Should().Be("Petropavlovsk-Kamchatsky - Old");
    }

    [Fact]
    public void The_obsolete_ids_are_exactly_the_two_and_are_not_matched_by_display_name()
    {
        StudioTimeZones.ObsoleteWindowsIds.Should().BeEquivalentTo("Kamchatka Standard Time", "Mid-Atlantic Standard Time");

        // A zone whose display name says "Old" but whose id is not one of the two is listed: the name is localized and decides nothing.
        TimeZoneInfo[] zones = [Custom("Studio Test Standard Time", -7, "(UTC-07:00) Something - Old")];
        StudioTimeZones.Listed(zones, "FI").Select(static x => x.Id).Should().Equal("Studio Test Standard Time");
    }

    [Fact]
    public void A_Windows_zone_with_no_IANA_name_is_listed_after_the_IANA_ids_under_its_display_name_and_is_read_back_by_its_id()
    {
        TimeZoneInfo canada = Custom("Studio Test Standard Time", -7, "(UTC-07:00) Pacific Time (British Columbia)");
        TimeZoneInfo manitoba = Custom("Studio Other Standard Time", -5, "(UTC-05:00) Manitoba");
        TimeZoneInfo bare = Custom("Studio Bare Standard Time", 3, string.Empty);
        TimeZoneInfo[] zones = [canada, Custom("Zulu/Last", 1, "Last"), manitoba, bare, Custom("Africa/Abidjan", 0, "Abidjan")];

        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed(zones, "FI");

        // After the IANA ids, in the order of their names: "Manitoba" < "Pacific Time (British Columbia)" < the bare zone's own id, "Studio Bare Standard Time".
        listed.Select(static x => x.Id).Should().Equal("Africa/Abidjan", "Zulu/Last", "Studio Other Standard Time", "Studio Test Standard Time", "Studio Bare Standard Time");
        listed.Select(static x => StudioTimeZones.NameOf(x.Id, x.Zone)).Should().Equal(
            "Africa/Abidjan", "Zulu/Last", "Manitoba", "Pacific Time (British Columbia)", "Studio Bare Standard Time");
        listed.Single(static x => x.Id == "Studio Test Standard Time").Zone.Should().BeSameAs(canada, "the id is the one the host reads the zone back by");
    }

    [Fact]
    public void A_zone_with_no_IANA_name_is_labelled_by_its_name_and_the_studios_own_offset()
    {
        TimeZoneInfo canada = Custom("Studio Test Standard Time", -7, "(UTC-07:00) British Columbia");
        TimeZoneInfo utcStyle = Custom("Studio Utc Standard Time", 0, "(UTC) Coordinated Universal Time");
        TimeZoneInfo bare = Custom("Studio Bare Standard Time", 3, string.Empty);

        IReadOnlyList<StudioTimeZones.ZoneOption> options = StudioTimeZones.Options(StudioTimeZones.Listed([canada, utcStyle, bare], "FI"), null, null);

        options.Should().HaveCount(3);
        StudioTimeZones.Label(options.Single(static x => x.Id == "Studio Test Standard Time").Name, canada, At).Should().Be("British Columbia (UTC-07:00)");
        StudioTimeZones.Label(options.Single(static x => x.Id == "Studio Utc Standard Time").Name, utcStyle, At).Should().Be("Coordinated Universal Time (UTC)");
        StudioTimeZones.Label(options.Single(static x => x.Id == "Studio Bare Standard Time").Name, bare, At).Should().Be("Studio Bare Standard Time (UTC+03:00)");
    }

    [Fact]
    public void An_IANA_entry_is_named_by_its_id_whatever_its_display_name_says()
    {
        TimeZoneInfo helsinki = Custom("Europe/Helsinki", 2, "(UTC+02:00) Helsinki");

        StudioTimeZones.NameOf("Europe/Helsinki", helsinki).Should().Be("Europe/Helsinki");
        StudioTimeZones.NameOf("UTC", TimeZoneInfo.Utc).Should().Be("UTC");
    }

    [Fact]
    public void The_list_of_this_host_holds_the_ids_first_then_the_unconverted_Windows_zones_and_neither_an_obsolete_zone_nor_a_replaceable_legacy_spelling()
    {
        IReadOnlyList<(string Id, TimeZoneInfo Zone)> listed = StudioTimeZones.Listed("FI");

        int firstWindows = listed.ToList().FindIndex(static x => StudioTimeZones.IsUnconvertedWindowsId(x.Id));
        if (firstWindows >= 0)
        {
            listed.Skip(firstWindows).Should().OnlyContain(static x => StudioTimeZones.IsUnconvertedWindowsId(x.Id), "a Windows zone with no IANA name is never sorted among the IANA ids");
        }

        listed.Select(static x => x.Id).Should().NotContain(StudioTimeZones.ObsoleteWindowsIds);

        foreach ((string legacy, string current) in Pairs)
        {
            if (Finds(current))
            {
                listed.Select(static x => x.Id).Should().NotContain(legacy, "the host finds the current name, so the legacy one is not a second entry");
            }
        }
    }
}
