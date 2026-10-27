using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// X2's stylesheet section: appended after X1 (D26), every rule its own (<c>ms-whatif-</c>), tokens only, and the two plans side by
/// side only where there is room for both - each able to shrink, so a wide plan scrolls in its own region and never widens the page.
/// </summary>
public partial class WhatIfStylesheetTests
{
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "X2. What if? A hypothetical index";

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
    public void The_section_is_appended_after_X1s()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("X1. The plan view", StringComparison.Ordinal),
            "D26: a section is appended in cascade order");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own()
    {
        IEnumerable<string> selectors = Section()
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-whatif-", StringComparison.Ordinal) || x == ".ms-whatif",
            "X2 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_the_two_plans_stack_on_a_narrow_screen()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
        section.Should().Contain("grid-template-columns: repeat(2, minmax(0, 1fr));", "side by side, each able to shrink below its content");
        section.Should().Contain(".ms-whatif-side {\n    display: flex;\n    flex-direction: column;\n    gap: var(--ms-space-xs);\n    min-width: 0;");

        int narrow = section.IndexOf("@media (max-width: 1100px)", StringComparison.Ordinal);
        narrow.Should().BePositive();
        section[narrow..].Should().Contain("grid-template-columns: minmax(0, 1fr);");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();
}
