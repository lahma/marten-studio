using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// X1's stylesheet section: appended last (D26), every rule its own (<c>ms-plan-</c>), tokens only, the share bar driven by the
/// custom property the tree writes, and on a phone the drill-in's header one column and the tree's indent narrower.
/// </summary>
public partial class PlanStylesheetTests
{
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "X1. The plan view";

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
    public void The_section_is_appended_after_every_earlier_one()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("W5-API. The Health page's Snapshot menu", StringComparison.Ordinal),
            "D26: a section is appended in cascade order, after 0.5's last");
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
            static x => x.StartsWith(".ms-plan-", StringComparison.Ordinal) || x == ".ms-plan",
            "X1 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
        section.Should().Contain("width: var(--ms-plan-share, 0%);", "the bar is the share the tree writes on it");
    }

    [Fact]
    public void The_tree_keeps_its_natural_width_inside_its_region_and_a_node_fits_a_phone()
    {
        string section = Section();

        section.Should().Contain(".ms-plan-tree {\n    min-width: max-content;", "a deep plan scrolls sideways in its region (D25) rather than squeezing");

        // 0.6's UI sweep measured 85vw at 390 px: a viewport unit knows nothing of the icon rail and the cards round the plan,
        // and the root node ran 88 px past a 252 px region. The bound is the plan's own width, less the tree's padding.
        section.Should().Contain("    container-type: inline-size;\n", "the plan is the container the node's bound is measured against");
        section.Should().Contain("max-width: min(72ch, 100cqi - 2 * var(--ms-space-sm));");
        section.Should().NotContain("vw)", "a node's bound is the plan's width, never the viewport's");

        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);
        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-plan-drill-header {").And.Contain("flex-direction: column;").And.Contain(".ms-plan-children {");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();
}
