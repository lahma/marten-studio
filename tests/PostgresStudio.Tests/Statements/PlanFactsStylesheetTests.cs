using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.10-A's stylesheet section (issue #28 phase 2, D26): the Facts panel's rules, appended last, every selector its own
/// (<c>ms-plan-dbfact-</c>, <c>ms-plan-dbfacts</c>, <c>ms-plan-table-facts</c>, <c>ms-plan-node-facts</c>) and none an earlier
/// section's, no literal colour (the tokens carry the dark theme), and every class the panel draws either styled by it or one an
/// earlier section already styles.
/// </summary>
public partial class PlanFactsStylesheetTests
{
    private const string Header = "0.10-A. Facts beside the plan";

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
    public void The_section_is_appended_after_every_earlier_one()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("0.9.1. Health: the linked finding", StringComparison.Ordinal), "D26: appended in cascade order");
        css.Split(Header).Should().HaveCount(2, "the header is the section's, once");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own_and_no_earlier_selector_is_redeclared()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        string before = css[..start];

        IReadOnlyList<string> selectors =
        [
            .. Section().Split('\n')
                .Select(static x => x.Trim())
                .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
                .SelectMany(static x => x.TrimEnd('{').Split(','))
                .Select(static x => x.Trim())
                .Where(static x => x.Length > 0),
        ];

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-plan-dbfact", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-table-facts", StringComparison.Ordinal)
                || x.StartsWith(".ms-plan-node-facts", StringComparison.Ordinal),
            "0.10-A's names are its own: ms-plan-facts and ms-plan-fact belong to the node's own facts line");

        foreach (string selector in selectors)
        {
            before.Should().NotContain("\n" + selector + " {", "{0} is not an earlier section's to redeclare", selector);
        }
    }

    [Fact]
    public void No_literal_colour_appears_in_the_section()
    {
        HexColour().IsMatch(Section()).Should().BeFalse("colours come from the tokens, which have the dark theme's values (hard rule 3)");
        Section().Should().Contain("var(--ms-color-");
    }

    [Fact]
    public void Every_class_the_panel_draws_is_one_the_section_styles_or_one_that_needs_no_rule()
    {
        string markup = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Query", "PlanFactsPanel.razor"));
        string css = Css();

        IReadOnlyList<string> classes =
        [
            .. ClassAttribute().Matches(markup).SelectMany(static x => x.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(static x => x.StartsWith("ms-plan-dbfact", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        classes.Should().NotBeEmpty();

        // A hook only: a test finds it, nothing needs it to look any way.
        string[] hooks = ["ms-plan-dbfacts-failure", "ms-plan-dbfact-not-read", "ms-plan-dbfact-unqualified", "ms-plan-dbfact-values-note"];

        foreach (string name in classes.Except(hooks))
        {
            css.Should().Contain("." + name, "the panel draws {0}", name);
        }
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();

    [GeneratedRegex("class=\"([^\"@]+)\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ClassAttribute();
}
