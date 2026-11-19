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

    /// <summary>
    /// A time spelt in the visitor's zone, whatever the preferences are called where they are injected (<c>Preferences</c>, <c>Prefs</c>): the
    /// member is what is matched, not its receiver.
    /// </summary>
    private static readonly Regex SpellsATime = new(
        @"\.(FormatInSelectedTimeZone|ConvertToSelectedTimeZone|SelectedTimeZoneId)\b|\bSinceMarkTime\.When\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex DerivesInCode = new(@":\s*(DataStudio\.Components\.)?StudioDisplayComponent\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    /// <summary>The layout is where the version is made, and draws no time of its own.</summary>
    private static readonly string[] Exempt = ["src/DataStudio/Components/Layout/StudioLayout.razor"];

    /// <summary>
    /// The code that spells a time outside a component, each drawn again by a component that takes the version: the rail's badge titles (the
    /// layout draws the rail) and the since-mark helper (its callers are components, which the scan holds by <c>SinceMarkTime.When</c>).
    /// </summary>
    private static readonly string[] CodeThatSpellsATime =
    [
        "src/DataStudio.Postgres/Services/Live/DatabaseModuleRail.cs",
        "src/DataStudio.Postgres/Services/Live/SinceMarkTime.cs",
    ];

    private static bool SpellsATimeIn(string text) => SpellsATime.IsMatch(SourceScanner.StripCommentsAndStrings(text));

    /// <summary>Whether a component's markup takes the version: the <c>@inherits</c> line is read with comments removed, so one in a comment does not count.</summary>
    private static bool TakesTheVersion(string razor) => SourceScanner.StripCommentsAndStrings(razor).Contains(Base, StringComparison.Ordinal);

    [Fact]
    public void Every_component_that_spells_a_time_takes_the_display_version()
    {
        string[] components = [.. SourceScanner.ShippedSourceFiles().Where(static x => x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))];
        components.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] spelling = [.. components.Where(static x => SpellsATimeIn(File.ReadAllText(x)))];
        spelling.Length.Should().BeGreaterThan(30, "the scan finds the studio's pages that spell a time; fewer means the pattern stopped matching");

        string[] offenders = [.. spelling
            .Where(static x => !Exempt.Contains(SourceScanner.Relative(x), StringComparer.Ordinal))
            .Where(static x => !TakesTheVersion(File.ReadAllText(x)))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)];

        offenders.Should().BeEmpty(
            "a component that spells a time in the visitor's zone must be drawn again when the zone changes: @inherits DataStudio.Components.StudioDisplayComponent. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// A time spelt in a <c>.cs</c> file is out of the markup scan's sight: a code-behind, a component written in code, or text formatted
    /// outside a component and handed to one. Each is held here: a code-behind by its component's markup, a class by deriving from the base,
    /// and anything else by the short list above, which a new entry joins only with the component that draws it again named beside it.
    /// </summary>
    [Fact]
    public void Code_that_spells_a_time_is_a_component_that_takes_the_version_or_is_listed()
    {
        string[] code = [.. SourceScanner.ShippedSourceFiles().Where(static x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))];
        code.Should().NotBeEmpty();

        List<string> offenders = [];
        List<string> found = [];

        foreach (string file in code)
        {
            string text = File.ReadAllText(file);
            if (!SpellsATimeIn(text))
            {
                continue;
            }

            string relative = SourceScanner.Relative(file);
            found.Add(relative);

            if (CodeThatSpellsATime.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            bool codeBehind = file.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase)
                && File.Exists(file[..^3])
                && TakesTheVersion(File.ReadAllText(file[..^3]));

            if (!codeBehind && !DerivesInCode.IsMatch(SourceScanner.StripCommentsAndStrings(text)))
            {
                offenders.Add(relative);
            }
        }

        found.Should().Contain(CodeThatSpellsATime, "each listed file still spells a time: an entry that no longer does is removed");
        offenders.Should().BeEmpty(
            "a time spelt in code is drawn by a component that takes the display version, or the file is listed with the reason. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_finds_a_component_that_spells_a_time_and_ignores_one_that_only_says_so_in_a_comment()
    {
        SpellsATimeIn("<td>@Preferences.FormatInSelectedTimeZone(at)</td>").Should().BeTrue();
        SpellsATimeIn("@(SinceMarkTime.When(Preferences, at))").Should().BeTrue();
        SpellsATimeIn("<td>@Prefs.ConvertToSelectedTimeZone(at)</td>").Should().BeTrue("the preferences injected under another name");
        SpellsATimeIn("// Preferences.FormatInSelectedTimeZone is what pages call").Should().BeFalse();
        SpellsATimeIn("public string FormatInSelectedTimeZone(DateTimeOffset moment) => string.Empty;").Should().BeFalse("the declaration is not a use");
    }

    [Fact]
    public void An_inherits_line_in_a_comment_does_not_count()
    {
        TakesTheVersion(Base + "\n<td>@Preferences.FormatInSelectedTimeZone(at)</td>").Should().BeTrue();
        TakesTheVersion("@* " + Base + " *@\n<td>@Preferences.FormatInSelectedTimeZone(at)</td>").Should().BeFalse();
        DerivesInCode.IsMatch("internal sealed class Clock : StudioDisplayComponent").Should().BeTrue();
        DerivesInCode.IsMatch("internal sealed class Clock : ComponentBase").Should().BeFalse();
    }
}
