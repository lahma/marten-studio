using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M1's stylesheet section (D26): appended after the sections before it, every rule its own, tokens only, notes in the
/// secondary text colour, and a layout that stacks the step list above the step on a narrow screen.
/// </summary>
public partial class StreamStepperStylesheetTests
{
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "M1. The projection stepper";

    private static readonly string[] Owned =
        [".ms-stepper-", ".ms-time-travel-actions", ".ms-time-travel-step", ".ms-dead-letter-step", ".ms-dead-letters-event"];

    private static string Css() => File.ReadAllText(RepositoryRoot.Combine([.. Stylesheet.Split('/')]));

    private static string Section()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_comes_after_the_sections_before_it()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(
            css.IndexOf("W5-API. The Health page's Snapshot menu", StringComparison.Ordinal),
            "D26: a section is appended, so a later one wins by source order");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own()
    {
        IEnumerable<string> selectors = Section()
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => (x.EndsWith('{') || x.EndsWith(',')) && !x.StartsWith('@') && x.StartsWith('.'))
            .SelectMany(static x => x.TrimEnd('{', ',').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => Owned.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)),
            "M1 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
    }

    [Fact]
    public void Its_notes_use_the_secondary_text_colour_and_never_the_muted_one()
    {
        string section = Section();

        section.Should().Contain("color: var(--ms-color-text-secondary);");
        section.Should().NotContain("--ms-color-text-muted", "UX-6 measured the muted grey below 4.5:1 on the surface");
    }

    [Fact]
    public void On_a_narrow_screen_the_step_list_stacks_above_the_step()
    {
        string section = Section();
        int media = section.IndexOf("@media (max-width: 900px)", StringComparison.Ordinal);

        media.Should().BePositive();
        section[media..].Should().Contain("grid-template-columns: minmax(0, 1fr);");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
