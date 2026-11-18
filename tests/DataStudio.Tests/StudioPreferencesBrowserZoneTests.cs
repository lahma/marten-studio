using DataStudio.Services;

using Microsoft.AspNetCore.Http;

namespace DataStudio.Tests;

/// <summary>
/// A visitor with no stored zone reads times in their browser's own: the id the browser reports becomes the circuit's zone when this server knows it, and only
/// then, and it is a default, not a choice - nothing is stored and no preference is counted.
/// </summary>
public class StudioPreferencesBrowserZoneTests
{
    private static StudioPreferences Preferences() => new(new HttpContextAccessor());

    [Theory]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Niue")]
    [InlineData("UTC")]
    public void A_zone_the_server_knows_becomes_the_circuits_zone_without_being_a_visitors_choice(string id)
    {
        StudioPreferences preferences = Preferences();
        long version = preferences.PreferenceVersion;

        preferences.TryUseBrowserTimeZone(id).Should().BeTrue();

        preferences.SelectedTimeZoneId.Should().Be(id);
        preferences.BrowserTimeZoneId.Should().Be(id);
        preferences.PreferenceVersion.Should().Be(version, "a default the browser reported is not an expression of the visitor's, which a read racing a choice depends on");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("Europe/")]
    [InlineData("Europe/Helsinki\0")]
    [InlineData("Europe\\Helsinki")]
    [InlineData("FLE Standard Time")]
    public void An_id_the_server_does_not_know_or_that_is_not_shaped_like_one_leaves_the_servers_zone(string? id)
    {
        StudioPreferences preferences = Preferences();
        string before = preferences.SelectedTimeZoneId;

        preferences.TryUseBrowserTimeZone(id).Should().BeFalse();

        preferences.SelectedTimeZoneId.Should().Be(before);
        preferences.BrowserTimeZoneId.Should().BeNull();
    }

    [Fact]
    public void An_id_too_long_to_be_a_zone_is_not_looked_up()
    {
        StudioPreferences preferences = Preferences();
        string before = preferences.SelectedTimeZoneId;

        preferences.TryUseBrowserTimeZone("Europe/" + new string('a', 200)).Should().BeFalse();

        preferences.SelectedTimeZoneId.Should().Be(before);
    }

    [Fact]
    public void Times_are_spelt_in_the_browsers_zone_once_it_is_used()
    {
        StudioPreferences preferences = Preferences();
        DateTimeOffset instant = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        preferences.TryUseBrowserTimeZone("Pacific/Kiritimati");
        DateTimeOffset kiritimati = preferences.ConvertToSelectedTimeZone(instant);
        preferences.TryUseBrowserTimeZone("Pacific/Niue");
        DateTimeOffset niue = preferences.ConvertToSelectedTimeZone(instant);

        kiritimati.Offset.Should().Be(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati").GetUtcOffset(instant));
        niue.Offset.Should().Be(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Niue").GetUtcOffset(instant));
        (kiritimati.DateTime - niue.DateTime).Should().Be(kiritimati.Offset - niue.Offset, "the same instant reads as many hours apart as the two zones are, whatever this machine's own zone is");
    }
}
