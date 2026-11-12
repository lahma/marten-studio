using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D26 for the search pages: the <c>KS-UI-B</c> section is appended to the one stylesheet after UI-A's, holds only <c>ms-k-</c> rules, and carries a rule
/// for each class the design names.
/// </summary>
public partial class KafkaSearchStylesheetTests
{
    private const string Header = "/* ===== KS-UI-B: search, results, the record detail ===== */";

    private const string PreviousHeader = "/* ===== KS-UI-A: the Kafka catalog (topics, topic overview, cluster settings) ===== */";

    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css;
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is read by its header");

        int next = css.IndexOf("/* ===== ", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_follows_UI_As_in_the_one_stylesheet()
    {
        string css = Css;

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf(PreviousHeader, StringComparison.Ordinal), "sections are appended in cascade order (D26)");
        Regex.Matches(css, Regex.Escape(Header), RegexOptions.None, TimeSpan.FromSeconds(5)).Should().HaveCount(1);
    }

    [Fact]
    public void The_section_holds_only_ms_k_classes()
    {
        string[] selectors = [.. ClassName().Matches(Regex.Replace(Section(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
            .Select(static m => m.Groups[1].Value)
            .Where(static x => !char.IsDigit(x[0]))
            .Distinct()];

        selectors.Should().NotBeEmpty();
        selectors.Where(static x => !x.StartsWith("ms-k-", StringComparison.Ordinal)).Should().BeEmpty("the module re-declares no host class (D26, D34)");
    }

    [Theory]
    [InlineData("ms-k-search-form")]
    [InlineData("ms-k-predicate")]
    [InlineData("ms-k-predicate-marks")]
    [InlineData("ms-k-verdicts")]
    [InlineData("ms-k-result-banner")]
    [InlineData("ms-k-caveats")]
    [InlineData("ms-k-chip")]
    [InlineData("ms-k-match-detail")]
    [InlineData("ms-k-redacted")]
    [InlineData("ms-k-null")]
    [InlineData("ms-k-neighbours")]
    public void The_section_has_a_rule_for_each_class_the_design_names(string className)
    {
        Section().Should().Contain("." + className);
    }

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
