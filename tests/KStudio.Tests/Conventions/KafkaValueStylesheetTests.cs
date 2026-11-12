using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>D26 for values, compare and pruning: the <c>KS-UI-C</c> section is appended to the one stylesheet after UI-B's, holds only <c>ms-k-</c> rules, and carries a rule for each class the design names.</summary>
public partial class KafkaValueStylesheetTests
{
    private const string Header = "/* ===== KS-UI-C: values, compare and pruning ===== */";

    private const string PreviousHeader = "/* ===== KS-UI-B: search, results, the record detail ===== */";

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
    public void The_section_follows_UI_Bs_in_the_one_stylesheet()
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
    [InlineData("ms-k-value")]
    [InlineData("ms-k-value-text")]
    [InlineData("ms-k-value-hex")]
    [InlineData("ms-k-masked-note")]
    [InlineData("ms-k-compare")]
    [InlineData("ms-k-compare-masked")]
    [InlineData("ms-k-pruned")]
    public void The_section_has_a_rule_for_each_class_the_design_names(string className)
    {
        Section().Should().Contain("." + className);
    }

    [Fact]
    public void A_value_scrolls_inside_its_own_box_and_never_widens_the_shell()
    {
        string section = Regex.Replace(Section(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        section.Should().Contain("min-width: 0");
        section.Should().Contain("overflow: auto");
        section.Should().Contain("overflow-wrap: anywhere");
        section.Should().Contain("white-space: pre;", "a hex dump keeps its columns and scrolls inside its box");
    }

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
