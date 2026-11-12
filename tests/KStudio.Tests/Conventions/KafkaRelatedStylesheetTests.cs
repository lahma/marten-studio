using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D26 for related records: the <c>KS-UI-D</c> section is appended to the one stylesheet after UI-C's, holds only <c>ms-k-</c> rules, carries a rule for each
/// class the page draws, and keeps the page inside a 390 px screen: the strip hides, and the unbroken text breaks.
/// </summary>
public partial class KafkaRelatedStylesheetTests
{
    private const string Header = "/* ===== KS-UI-D: related records, the time strip and possible duplicates ===== */";

    private const string PreviousHeader = "/* ===== KS-UI-C: values, compare and pruning ===== */";

    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css;
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is read by its header");

        int next = css.IndexOf("/* ===== ", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    private static string WithoutComments() => Regex.Replace(Section(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>Every declaration block, in the section, of a rule whose selector list names <paramref name="selector" />.</summary>
    private static string[] Declarations(string selector) =>
        [.. Block().Matches(WithoutComments())
            .Where(m => m.Groups[1].Value.Split(',').Any(s => s.Trim() == selector))
            .Select(static m => m.Groups[2].Value)];

    [Fact]
    public void The_section_follows_UI_Cs_in_the_one_stylesheet()
    {
        string css = Css;

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf(PreviousHeader, StringComparison.Ordinal), "sections are appended in cascade order (D26)");
        Regex.Matches(css, Regex.Escape(Header), RegexOptions.None, TimeSpan.FromSeconds(5)).Should().HaveCount(1);
    }

    [Fact]
    public void The_strip_hides_at_600_px_and_below()
    {
        Section().Should().Contain("@media (max-width: 600px)");
        Declarations(".ms-k-strip-lanes").Should().Contain(static x => x.Contains("display: none", StringComparison.Ordinal), "the lanes and the table say the same on a phone");
    }

    [Theory]
    [InlineData(".ms-k-lane-head strong", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-keys code", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-lane-note", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-related-lead", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-strip-summary", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-lane", "min-width: 0")]
    [InlineData(".ms-k-lanes-section", "min-width: 0")]
    [InlineData(".ms-k-duplicates", "min-width: 0")]
    public void The_narrow_screen_rules_are_there(string selector, string declaration)
    {
        Declarations(selector).Should().Contain(x => x.Contains(declaration, StringComparison.Ordinal), selector + " has " + declaration + " (390 px)");
    }

    [Fact]
    public void The_section_holds_only_ms_k_classes()
    {
        string[] selectors = [.. ClassName().Matches(WithoutComments())
            .Select(static m => m.Groups[1].Value)
            .Where(static x => !char.IsDigit(x[0]))
            .Distinct()];

        selectors.Should().NotBeEmpty();
        selectors.Where(static x => !x.StartsWith("ms-k-", StringComparison.Ordinal)).Should().BeEmpty("the module re-declares no host class (D26, D34)");
    }

    [Theory]
    [InlineData("ms-k-keys")]
    [InlineData("ms-k-lanes")]
    [InlineData("ms-k-lane")]
    [InlineData("ms-k-lane-head")]
    [InlineData("ms-k-lane-query")]
    [InlineData("ms-k-strip")]
    [InlineData("ms-k-strip-track")]
    [InlineData("ms-k-strip-dot")]
    [InlineData("ms-k-strip-anchor")]
    [InlineData("ms-k-anchor-row")]
    [InlineData("ms-k-duplicates")]
    [InlineData("ms-k-related-link")]
    public void The_section_has_a_rule_for_each_class_the_page_draws(string className)
    {
        Section().Should().Contain("." + className);
    }

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Block();

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
