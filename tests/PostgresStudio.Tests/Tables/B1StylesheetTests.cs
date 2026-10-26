using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// W5-B1's stylesheet section: every selector its own (<c>ms-db-fk-uncovered</c>, <c>ms-db-security-</c>,
/// <c>ms-db-headroom</c>), defined tokens only, notes in the secondary text colour, and the headroom left-aligned on a phone -
/// read through one path the orchestrator points at <c>studio.css</c> when it folds the part in (W5-index §4, D26).
/// </summary>
public partial class B1StylesheetTests
{
    /// <summary>Where the section is: the part until it is folded, then <c>src/PostgresStudio/wwwroot/css/studio.css</c>.</summary>
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/parts/b1.css";

    private const string Header = "W5-B1. Database browser lints";

    private static readonly string[] OwnPrefixes = [".ms-db-fk-uncovered", ".ms-db-security-", ".ms-db-headroom"];

    private static string Section()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine([.. Stylesheet.Split('/')]));
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
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
            static x => OwnPrefixes.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)),
            "W5-B1 re-declares nothing an earlier section owns: the badges stay ms-db-badge-warning badges");
    }

    [Fact]
    public void It_uses_only_tokens_studio_css_defines_and_no_literal_colour()
    {
        string section = Section();
        string studio = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        IReadOnlyList<string> tokens = [.. Token().Matches(section).Select(static x => x.Groups[1].Value).Distinct(StringComparer.Ordinal)];

        tokens.Should().NotBeEmpty();
        tokens.Should().OnlyContain(x => studio.Contains("--" + x + ":", StringComparison.Ordinal), "a token the stylesheet does not define renders as nothing");
    }

    [Fact]
    public void The_notes_are_in_the_secondary_text_colour_and_the_headroom_wraps_left_on_a_phone()
    {
        string section = Section();

        section.Should().Contain("color: var(--ms-color-text-secondary);", "UX-6 measured the muted grey below 4.5:1 on the surface");
        section.Should().NotContain("--ms-color-text-muted");

        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);
        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-db-headroom {").And.Contain("justify-content: flex-start;");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"var\(--([a-z0-9-]+)\)")]
    private static partial Regex Token();
}
