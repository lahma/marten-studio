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

    // ---- #92: a legacy spelling is the same zone as the current name ----------------------------------------------------------------

    private static bool Finds(string id) => TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);

    /// <summary>A host that finds both spellings of a zone (every Linux with tzdata, and Windows with ICU); on one that does not there is nothing to alias.</summary>
    private static bool HostKnowsBothSpellings => Finds("Asia/Calcutta") && Finds("Asia/Kolkata");

    private static StudioPreferences PreferencesWithCookie(string zone)
    {
        DefaultHttpContext context = new();
        context.Request.Headers.Cookie = StudioPreferences.TimeZonePreferenceKey + "=" + zone;

        return new StudioPreferences(new HttpContextAccessor { HttpContext = context });
    }

    private const string BothSpellingsReason =
        "This host's zone data does not have both Asia/Calcutta and Asia/Kolkata, so there is no alias to prove here; the table's own answer is proved where the resolver is given.";

    [Fact]
    public void A_browser_that_reports_a_legacy_spelling_selects_and_marks_the_current_name_and_stores_nothing()
    {
        // Only the current name has to be known: a host whose zone data has no legacy spellings is where this used to fail.
        Assert.SkipUnless(Finds("Asia/Kolkata"), "This host's zone data does not have Asia/Kolkata.");

        StudioPreferences preferences = Preferences();
        long version = preferences.PreferenceVersion;

        preferences.TryUseBrowserTimeZone("Asia/Calcutta").Should().BeTrue();

        preferences.SelectedTimeZoneId.Should().Be("Asia/Kolkata");
        preferences.BrowserTimeZoneId.Should().Be("Asia/Kolkata", "the entry the select marks is the one it lists");
        preferences.PreferenceVersion.Should().Be(version);
    }

    [Fact]
    public void A_stored_legacy_spelling_is_read_as_the_current_name_and_a_browser_answer_in_either_spelling_changes_nothing()
    {
        Assert.SkipUnless(HostKnowsBothSpellings, BothSpellingsReason);

        StudioPreferences preferences = PreferencesWithCookie("Asia/Calcutta");

        preferences.SelectedTimeZoneId.Should().Be("Asia/Kolkata");

        long display = preferences.DisplayVersion;
        long version = preferences.PreferenceVersion;

        preferences.TryUseBrowserTimeZone("Asia/Calcutta").Should().BeTrue();
        preferences.TryUseBrowserTimeZone("Asia/Kolkata").Should().BeTrue();
        preferences.SelectedTimeZoneId = "Asia/Calcutta";
        preferences.SelectedTimeZoneId = "Asia/Kolkata";

        preferences.SelectedTimeZoneId.Should().Be("Asia/Kolkata");
        preferences.DisplayVersion.Should().Be(display, "a spelling alone is no change of the zone every time is drawn in");
        preferences.PreferenceVersion.Should().Be(version, "and it is not an expression of the visitor's");
    }

    [Fact]
    public void Choosing_the_current_name_stores_the_current_name_and_a_legacy_value_is_the_same_choice()
    {
        Assert.SkipUnless(HostKnowsBothSpellings, BothSpellingsReason);

        StudioPreferences preferences = Preferences();

        preferences.SelectedTimeZoneId = "Asia/Calcutta";
        long display = preferences.DisplayVersion;
        preferences.SelectedTimeZoneId = "Asia/Kolkata";

        preferences.SelectedTimeZoneId.Should().Be("Asia/Kolkata", "what is stored when a zone is picked is the current name");
        preferences.DisplayVersion.Should().Be(display);
    }
}
