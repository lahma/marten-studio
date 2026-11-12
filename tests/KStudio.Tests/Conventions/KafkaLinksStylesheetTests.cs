using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D26 for links and discovered facts: the <c>KS-UI-E</c> section is appended to the one stylesheet after UI-D's, holds only <c>ms-k-</c> rules, carries a
/// rule for each class the pages draw, and keeps them inside a 390 px screen: unbroken text breaks, and the two-column lists stack.
/// </summary>
public partial class KafkaLinksStylesheetTests
{
    private const string Header = "/* ===== KS-UI-E: links to Marten records, external links, and what Wolverine told KStudio ===== */";

    private const string PreviousHeader = "/* ===== KS-UI-D: related records, the time strip and possible duplicates ===== */";

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

    [Fact]
    public void The_section_follows_UI_Ds_in_the_one_stylesheet()
    {
        string css = Css;

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf(PreviousHeader, StringComparison.Ordinal), "sections are appended in cascade order (D26)");
        Regex.Matches(css, Regex.Escape(Header), RegexOptions.None, TimeSpan.FromSeconds(5)).Should().HaveCount(1);
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
    [InlineData("ms-k-record-links")]
    [InlineData("ms-k-record-links-compact")]
    [InlineData("ms-k-link-list")]
    [InlineData("ms-k-marten-link")]
    [InlineData("ms-k-marten-nolink")]
    [InlineData("ms-k-external-links")]
    [InlineData("ms-k-external-link")]
    [InlineData("ms-k-envelope-id")]
    [InlineData("ms-k-sent-at")]
    [InlineData("ms-k-partition-key")]
    [InlineData("ms-k-discovered")]
    [InlineData("ms-k-discovered-facts")]
    [InlineData("ms-k-discovered-types")]
    [InlineData("ms-k-discovery")]
    [InlineData("ms-k-discovery-listed")]
    [InlineData("ms-k-discovery-notes")]
    public void The_section_has_a_rule_for_each_class_the_pages_draw(string className)
    {
        Section().Should().Contain("." + className);
    }

    [Fact]
    public void The_narrow_screen_stacks_the_facts_and_long_text_breaks()
    {
        Section().Should().Contain("@media (max-width: 600px)");
        Section().Should().Contain("grid-template-columns: 1fr");
        WithoutComments().Should().Contain("overflow-wrap: anywhere");
    }

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
