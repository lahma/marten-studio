using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.10-B's stylesheet section (issue #28 phase 2, D26): the pin bar, Compare, the settings what-if, the timeout field and the
/// What if? panel's in-place note, appended last, every selector its own and none an earlier section's, no literal colour (the
/// tokens carry the dark theme), and every class the new markup draws either styled by it or one an earlier section styles.
/// </summary>
public partial class PlanCompareStylesheetTests
{
    private const string Header = "0.10-B. Compare plans";

    private static string Css() => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_is_appended_after_every_earlier_one_and_is_the_last()
    {
        string css = Css();

        css.Split(Header).Should().HaveCount(2, "the header is the section's, once");
        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("0.10-A. Facts beside the plan", StringComparison.Ordinal), "D26: appended in cascade order");
        css.IndexOf("/* ====", css.IndexOf(Header, StringComparison.Ordinal), StringComparison.Ordinal).Should().Be(-1, "nothing follows it yet");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own_and_no_earlier_selector_is_redeclared()
    {
        string css = Css();
        string before = css[..css.IndexOf(Header, StringComparison.Ordinal)];

        IReadOnlyList<string> selectors =
        [
            .. Section().Split('\n')
                .Select(static x => x.Trim())
                .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
                .SelectMany(static x => x.TrimEnd('{').Split(','))
                .Select(static x => x.Trim())
                .Where(static x => x.Length > 0),
        ];

        selectors.Should().NotBeEmpty();

        foreach (string selector in selectors)
        {
            before.Should().NotContain("\n" + selector + " {", "{0} is not an earlier section's to redeclare", selector);
        }

        selectors.Should().OnlyContain(
            static x => x.StartsWith(".ms-plan-pin", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-params-compare", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-timeout", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-compare", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-diff-", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-whatif-setting", StringComparison.Ordinal)
                || x == ".ms-whatif-in-place");
    }

    [Fact]
    public void No_literal_colour_appears_in_the_section()
    {
        HexColour().IsMatch(Section()).Should().BeFalse("colours come from the tokens, which have the dark theme's values (hard rule 3)");
        Section().Should().Contain("var(--ms-color-");
    }

    [Theory]
    [InlineData("PlanCompare.razor")]
    [InlineData("PlanSettingsPanel.razor")]
    public void Every_class_the_new_components_draw_is_styled_here_or_by_an_earlier_section(string component)
    {
        string markup = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Query", component));
        string css = Css();

        IReadOnlyList<string> classes =
        [
            .. ClassAttribute().Matches(markup).SelectMany(static x => x.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(static x => x.StartsWith("ms-plan-", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        classes.Should().NotBeEmpty();

        // Hooks only: a test finds them, nothing needs them to look any way.
        string[] hooks = ["ms-plan-compare-insights", "ms-plan-compare-totals", "ms-plan-whatif-settings-message", "ms-plan-whatif-settings-run", "ms-plan-compare-row", "ms-plan-compare-table"];

        foreach (string name in classes.Except(hooks))
        {
            css.Should().Contain("." + name, "{0} draws {1}", component, name);
        }
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();

    [GeneratedRegex("class=\"([^\"@]+)\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClassAttribute();
}
