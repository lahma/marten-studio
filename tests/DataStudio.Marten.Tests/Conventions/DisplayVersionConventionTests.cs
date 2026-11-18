using System.Text.RegularExpressions;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// A change of the visitor's time zone reaches a component only if the component takes the layout's cascaded display version (<c>StudioDisplayComponent</c>):
/// a page has no parameters, so the router never draws a retained page again by itself, and a component that spells a time and does not take the version
/// keeps the old zone on screen until something else happens to draw it (F5). This scans <c>src/</c> for the components that spell a time through the
/// preferences and holds each to the base class, so a new page cannot forget it with every other test green.
/// </summary>
public class DisplayVersionConventionTests
{
    private const string Base = "@inherits DataStudio.Components.StudioDisplayComponent";

    private static readonly Regex SpellsATime = new(
        @"\bPreferences\.(FormatInSelectedTimeZone|ConvertToSelectedTimeZone|SelectedTimeZoneId)\b|\bSinceMarkTime\.When\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>The layout is where the version is made, and draws no time of its own.</summary>
    private static readonly string[] Exempt = ["src/DataStudio/Components/Layout/StudioLayout.razor"];

    [Fact]
    public void Every_component_that_spells_a_time_takes_the_display_version()
    {
        string[] components = [.. SourceScanner.ShippedSourceFiles().Where(static x => x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))];
        components.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] spelling = [.. components.Where(static x => SpellsATime.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))];
        spelling.Length.Should().BeGreaterThan(30, "the scan finds the studio's pages that spell a time; fewer means the pattern stopped matching");

        string[] offenders = [.. spelling
            .Where(static x => !Exempt.Contains(SourceScanner.Relative(x), StringComparer.Ordinal))
            .Where(static x => !File.ReadAllText(x).Contains(Base, StringComparison.Ordinal))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)];

        offenders.Should().BeEmpty(
            "a component that spells a time in the visitor's zone must be drawn again when the zone changes: @inherits DataStudio.Components.StudioDisplayComponent. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_finds_a_component_that_spells_a_time_and_ignores_one_that_only_says_so_in_a_comment()
    {
        SpellsATime.IsMatch(SourceScanner.StripCommentsAndStrings("<td>@Preferences.FormatInSelectedTimeZone(at)</td>")).Should().BeTrue();
        SpellsATime.IsMatch(SourceScanner.StripCommentsAndStrings("@(SinceMarkTime.When(Preferences, at))")).Should().BeTrue();
        SpellsATime.IsMatch(SourceScanner.StripCommentsAndStrings("// Preferences.FormatInSelectedTimeZone is what pages call")).Should().BeFalse();
    }
}
