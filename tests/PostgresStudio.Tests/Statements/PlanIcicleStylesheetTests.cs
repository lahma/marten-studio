using System.Globalization;
using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.0-P3B's stylesheet section (issue #28): appended last (D26), every rule its own (<c>ms-plan-icicle</c>), the ink of a heat
/// step a token with the dark theme's own - and the ink on every heat fill in every theme at WCAG AA's 4.5:1 - and the one
/// studio.js listener that stops the page scrolling under the frames' keys.
/// </summary>
public partial class PlanIcicleStylesheetTests
{
    private const string Header = "1.0-P3B. The icicle view";

    private static string Css() => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    /// <summary>The custom properties declared in the block that starts at <paramref name="selector" /> within <paramref name="text" />.</summary>
    private static Dictionary<string, string> Tokens(string text, string selector)
    {
        int at = text.IndexOf(selector, StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0, "{0} is declared", selector.Trim());

        string block = text[at..text.IndexOf('}', at)];
        return Declaration().Matches(block).ToDictionary(static x => x.Groups[1].Value, static x => x.Groups[2].Value);
    }

    [Fact]
    public void The_section_is_after_every_earlier_one()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("1.0-P3A. Text-format plans", StringComparison.Ordinal), "D26: appended in cascade order");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own_or_a_theme_block_for_its_tokens()
    {
        IEnumerable<string> selectors = Section()
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-plan-icicle", StringComparison.Ordinal) || x == ":root" || x.StartsWith(".ms-studio[data-theme=", StringComparison.Ordinal),
            "the section re-declares nothing an earlier one owns");
    }

    [Fact]
    public void The_ink_tokens_are_set_on_root_in_the_dark_media_query_and_under_both_explicit_themes()
    {
        string section = Section();

        Tokens(section, "\n:root {").Keys.Should().Contain(["--ms-icicle-ink-1", "--ms-icicle-ink-2", "--ms-icicle-ink-3", "--ms-icicle-ink-4"]);
        Tokens(section, "\n    :root {").Keys.Should().Contain(["--ms-icicle-ink-3", "--ms-icicle-ink-4"]);
        Tokens(section, "\n.ms-studio[data-theme=\"light\"] {").Keys.Should().Contain(["--ms-icicle-ink-3", "--ms-icicle-ink-4"]);
        Tokens(section, "\n.ms-studio[data-theme=\"dark\"] {").Keys.Should().Contain(["--ms-icicle-ink-3", "--ms-icicle-ink-4"]);
        section.Should().Contain("@media (prefers-color-scheme: dark)");
    }

    [Fact]
    public void A_literal_colour_appears_only_in_the_token_blocks()
    {
        string section = Section();
        string rules = section[section.IndexOf("\n.ms-plan-icicle {", StringComparison.Ordinal)..];

        HexColour().IsMatch(rules).Should().BeFalse("colours come from the tokens (hard rule 3)");
        rules.Should().Contain("var(--ms-plan-heat-1)").And.Contain("var(--ms-plan-heat-4)").And.Contain("var(--ms-icicle-ink-3)").And.Contain("var(--ms-color-");
    }

    /// <summary>The label on every heat fill reads at 4.5:1 or better, in the light theme, the dark theme and the system's dark default.</summary>
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("system-dark")]
    public void The_ink_on_each_heat_fill_meets_WCAG_AA_in_every_theme(string theme)
    {
        string plan = Css();
        string icicle = Section();

        // The heat scale is 0.9-A's; the ink is this section's. Each theme is the root's, then its own overrides.
        Dictionary<string, string> heat = Tokens(plan, "--ms-plan-heat-1: #ca8a04;\n    --ms-plan-heat-2: #ea580c;\n    --ms-plan-heat-3: #dc2626;\n    --ms-plan-heat-4: #991b1b;\n}\n\n@media");
        Dictionary<string, string> ink = Tokens(icicle, "\n:root {");

        switch (theme)
        {
            case "light":
                heat = Merge(heat, Tokens(plan, "\n.ms-studio[data-theme=\"light\"] {\n    --ms-plan-heat-1"));
                ink = Merge(ink, Tokens(icicle, "\n.ms-studio[data-theme=\"light\"] {"));
                break;
            case "dark":
                heat = Merge(heat, Tokens(plan, "\n.ms-studio[data-theme=\"dark\"] {\n    --ms-plan-heat-1"));
                ink = Merge(ink, Tokens(icicle, "\n.ms-studio[data-theme=\"dark\"] {"));
                break;
            default:
                heat = Merge(heat, Tokens(plan, "\n    :root {\n        --ms-plan-heat-1"));
                ink = Merge(ink, Tokens(icicle, "\n    :root {"));
                break;
        }

        for (int step = 1; step <= 4; step++)
        {
            string fill = heat["--ms-plan-heat-" + step];
            string text = ink["--ms-icicle-ink-" + step];

            Contrast(fill, text).Should().BeGreaterThanOrEqualTo(4.5, "heat {0} ({1}) under {2} text in the {3} theme", step, fill, text, theme);
        }
    }

    [Fact]
    public void The_arrows_and_Space_are_stopped_only_when_a_frame_itself_has_the_focus()
    {
        string script = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "js", "studio.js"));
        int at = script.IndexOf("window.martenStudio.icicleKeys = ", StringComparison.Ordinal);
        at.Should().BeGreaterThanOrEqualTo(0);

        string helper = script[at..script.IndexOf("return {};", at, StringComparison.Ordinal)];

        helper.Should().Contain("\"ArrowUp\", \"ArrowDown\", \"ArrowLeft\", \"ArrowRight\", \" \"")
            .And.Contain("target.matches(\".ms-plan-icicle-frame\")")
            .And.Contain("event.preventDefault()");
        helper.Should().NotContain("\"Tab\"").And.NotContain("\"Enter\"", "Tab keeps its way out of the view, and Enter has nothing to scroll");
    }

    private static Dictionary<string, string> Merge(Dictionary<string, string> root, Dictionary<string, string> over)
    {
        Dictionary<string, string> merged = new(root);

        foreach ((string key, string value) in over)
        {
            merged[key] = value;
        }

        return merged;
    }

    private static double Contrast(string a, string b)
    {
        double one = Luminance(a);
        double two = Luminance(b);
        return (Math.Max(one, two) + 0.05) / (Math.Min(one, two) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            double value = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        hex.Should().MatchRegex("^#[0-9a-f]{6}$");
        return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
    }

    [GeneratedRegex(@"(--[a-z0-9-]+):\s*([^;]+);", RegexOptions.None, 1000)]
    private static partial Regex Declaration();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b", RegexOptions.None, 1000)]
    private static partial Regex HexColour();
}
