using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The UI-0.6 section: what 0.6's UI sweep measured in a real browser at 390 px and in the dark theme, where no markup test
/// sees it - links drawn in the browser's own blue on the dark surface, the query editor's keyboard hint running 93 px past
/// its card, and time travel's slider pushing its Next button out of the card. The plan node's width, the fourth, is X1's
/// own rule, edited in place and pinned by <c>PlanStylesheetTests</c>.
/// </summary>
public partial class StylesheetTests
{
    private const string UiSweepHeader = "UI-0.6. What 0.6's UI sweep found";

    /// <summary>The section's rules, comments removed, read out of the file by its header (D26).</summary>
    private static string UiSweepSection()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));
        int start = css.IndexOf(UiSweepHeader, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        int end = css.IndexOf("*/", start, StringComparison.Ordinal);
        string section = next < 0 ? css[(end + 2)..] : css[(end + 2)..next];

        return CommentExpression().Replace(section, string.Empty);
    }

    [Fact]
    public void The_UI_sweep_section_is_appended_after_every_0_6_section_and_uses_only_the_tokens()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

        css.IndexOf(UiSweepHeader, StringComparison.Ordinal).Should().BeGreaterThan(
            css.IndexOf("M47. Orphaned progression rows", StringComparison.Ordinal),
            "D26: a section is appended, after 0.6's last packet section, so a later one wins by source order");

        string section = UiSweepSection();
        UiSweepHexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-primary)");
    }

    /// <summary>
    /// A link nothing else colours takes the link token, at no specificity: wrapped in <c>:where()</c>, so every rule that
    /// names a link still wins and only a link drawn in the browser's own colour changes - and inside <c>.ms-studio</c>, so a
    /// host's page is never reached. It sets the colour and nothing else, so no link loses its underline.
    /// </summary>
    [Fact]
    public void A_link_nothing_else_colours_takes_the_link_token_and_outweighs_no_rule()
    {
        string rule = Rule(":where(.ms-studio a)", UiSweepSection());

        Declaration(":where(.ms-studio a)", "color", UiSweepSection()).Should().Be("var(--ms-color-primary)");
        rule.Count(static c => c == ':').Should().Be(2,
            "one ':where' and one declaration: the rule sets the colour and nothing else");

        Selectors().Where(static x => x.Contains(":where(", StringComparison.Ordinal)).Should().Equal(
            [":where(.ms-studio a)"], "the whole selector is inside :where(), so it weighs nothing against any other rule");
    }

    /// <summary>
    /// Document detail's breadcrumb takes the database browser's breadcrumb style - the link token, underlined only on hover
    /// - so the two breadcrumbs read alike, as Database › Sessions does now by using the browser's class itself.
    /// </summary>
    [Fact]
    public void The_breadcrumbs_take_the_database_browsers_breadcrumb_style()
    {
        string section = UiSweepSection();

        Declaration(".ms-breadcrumb a", "color", section).Should().Be(Declaration(".ms-db-breadcrumb a", "color"));
        Declaration(".ms-breadcrumb a", "text-decoration", section).Should().Be(Declaration(".ms-db-breadcrumb a", "text-decoration"));
        Declaration(".ms-breadcrumb a:hover", "text-decoration", section).Should().Be(Declaration(".ms-db-breadcrumb a:hover", "text-decoration"));
    }

    /// <summary>
    /// The query editor's toolbar wraps, so its keyboard hint takes a line of its own when the buttons leave no room rather
    /// than running past the card - and past <c>.ms-content</c>, which then scrolled sideways (403 px in 334 at 390).
    /// </summary>
    [Fact]
    public void The_query_editors_toolbar_wraps_its_hint_rather_than_running_past_the_card()
    {
        Declaration(".ms-query-editor-toolbar", "flex-wrap", UiSweepSection()).Should().Be("wrap");
    }

    /// <summary>
    /// Time travel's version slider may shrink: a range input's automatic minimum is its default width, which kept the
    /// stepper row too wide for the card on a phone.
    /// </summary>
    [Fact]
    public void Time_travels_slider_gives_way_on_a_narrow_card()
    {
        Declaration(".ms-time-travel-stepper > input[type=\"range\"]", "min-width", UiSweepSection()).Should().Be("0");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UiSweepHexColour();
}
