using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-HEADER's stylesheet section: the environment badge's colours are the design tokens' (Production the danger token, Staging the
/// warning token, Development the success token), every rule is its own, and no colour is a literal (hard rule 3).
/// </summary>
public partial class HeaderStylesheetTests
{
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "/* ===== 1.2-HEADER ";

    private static string Section()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine([.. Stylesheet.Split('/')]));
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ===== ", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    private static string Rule(string section, string selector)
    {
        Match match = Regex.Match(section, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}", RegexOptions.None, TimeSpan.FromSeconds(5));
        match.Success.Should().BeTrue("the section has a rule for " + selector);
        return match.Groups["body"].Value;
    }

    [Theory]
    [InlineData(".ms-env-badge-production", "danger")]
    [InlineData(".ms-env-badge-staging", "warning")]
    [InlineData(".ms-env-badge-development", "success")]
    public void Each_kind_of_environment_is_drawn_in_its_tokens_colour(string selector, string token)
    {
        string section = Section();

        Rule(section, selector).Should().Contain("var(--ms-color-" + token + ")").And.Contain("var(--ms-color-" + token + "-bg)");
        Rule(section, selector + "::before").Should().Contain("var(--ms-color-" + token + ")");
    }

    [Fact]
    public void Any_other_name_is_neutral_and_the_badge_text_keeps_the_page_text_colour()
    {
        string section = Section();

        Rule(section, ".ms-env-badge").Should().Contain("color: var(--ms-color-text)").And.Contain("var(--ms-color-surface-alt)");
        section.Should().NotContain(".ms-env-badge-other", "an unnamed kind adds no colour of its own");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own_and_no_colour_is_a_literal()
    {
        string section = Section();

        IEnumerable<string> selectors = section
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-env-badge", StringComparison.Ordinal)
                || x.StartsWith(".ms-back-link", StringComparison.Ordinal)
                || x.StartsWith(".ms-copy-link", StringComparison.Ordinal)
                || x.StartsWith(".ms-chip-word", StringComparison.Ordinal)
                || x.StartsWith(".ms-header .ms-env-badge", StringComparison.Ordinal)
                || x.StartsWith(".ms-header .ms-header-right", StringComparison.Ordinal)
                || x.StartsWith(".ms-header .ms-scope-selector", StringComparison.Ordinal)
                || x == ".ms-header" || x == ".ms-header-left",
            "HEADER adds its own rules and re-declares only the header's own (the phone layout), on purpose (D26)");

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
