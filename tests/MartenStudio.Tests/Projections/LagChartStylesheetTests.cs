using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// D26 for M2's section: <c>M2</c> is appended after <c>W5-API</c>, so nothing above it changes; it carries the rules the
/// lag chart and the thresholds list use; and every colour in it is a design token (hard rule 3), never a literal - so the
/// chart follows the theme in both modes with nothing declared three times.
/// </summary>
public partial class LagChartStylesheetTests
{
    private static readonly Lazy<string> Stylesheet = new(() =>
        File.ReadAllText(RepositoryRoot.Combine("src/PostgresStudio/wwwroot/css/studio.css")).Replace("\r\n", "\n"));

    private const string Header = "M2. The projections screen's lag chart";

    /// <summary>The section's text: from its header to the end of the file, or to the next section's banner.</summary>
    private static string Section()
    {
        int start = Stylesheet.Value.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BePositive("the section is in studio.css");

        int next = Stylesheet.Value.IndexOf("/* ====", start, StringComparison.Ordinal);

        return next < 0 ? Stylesheet.Value[start..] : Stylesheet.Value[start..next];
    }

    [Fact]
    public void M2_is_appended_after_W5_API()
    {
        int earlier = Stylesheet.Value.IndexOf("W5-API. The Health page's Snapshot menu", StringComparison.Ordinal);
        int section = Stylesheet.Value.IndexOf(Header, StringComparison.Ordinal);

        earlier.Should().BePositive();
        section.Should().BeGreaterThan(earlier, "a later section is appended after the one before it (D26)");
    }

    [Theory]
    [InlineData(".ms-shard-history-cell {")]
    [InlineData(".ms-lag-chart {")]
    [InlineData(".ms-lag-chart-svg {")]
    [InlineData(".ms-lag-chart-high-water {")]
    [InlineData(".ms-lag-chart-position {")]
    [InlineData(".ms-lag-chart-stalled {")]
    [InlineData(".ms-lag-chart-threshold {")]
    [InlineData(".ms-lag-chart-caption {")]
    [InlineData(".ms-lag-chart-is-stalled .ms-lag-chart-stall {")]
    [InlineData(".ms-projection-thresholds {")]
    public void M2_carries_the_rules_the_chart_uses(string rule)
    {
        Section().Should().Contain(rule, "{0} belongs to M2", rule);
        Stylesheet.Value.IndexOf(rule, StringComparison.Ordinal).Should().Be(
            Stylesheet.Value.LastIndexOf(rule, StringComparison.Ordinal), "{0} is M2's own selector, declared once", rule);
    }

    [Fact]
    public void Every_colour_in_M2_is_a_design_token()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("a colour literal would not follow the theme");
        RgbColour().IsMatch(section).Should().BeFalse();
        Regex.Matches(section, @"(?:^|\s)(?:stroke|color|border-left):[^;]*;", RegexOptions.Multiline)
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Value.Contains("var(--ms-color-", StringComparison.Ordinal));
    }

    /// <summary>The caption's words are the secondary colour, which UX-6 measured at 4.5:1 or better; the muted grey is not.</summary>
    [Fact]
    public void The_caption_does_not_use_the_muted_grey()
    {
        Section().Should().NotContain("--ms-color-text-muted");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"rgba?\(")]
    private static partial Regex RgbColour();
}
