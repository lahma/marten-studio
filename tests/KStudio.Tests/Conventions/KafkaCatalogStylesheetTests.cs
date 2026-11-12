using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D26 for the catalog pages: the <c>KS-UI-A</c> section is appended to the one stylesheet after the shell's, holds only <c>ms-k-</c> rules, and
/// carries a rule for each class the catalog pages' markup is built on.
/// </summary>
public partial class KafkaCatalogStylesheetTests
{
    private const string Header = "/* ===== KS-UI-A: the Kafka catalog (topics, topic overview, cluster settings) ===== */";

    private const string ShellHeader = "/* ===== KS-SHELL: the module seams (cluster selector, the Kafka capability group, the trademark line) ===== */";

    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    /// <summary>The section: from its header to the next titled section, or the end of the file.</summary>
    private static string Section()
    {
        string css = Css;
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is read by its header");

        int next = css.IndexOf("/* ===== ", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_follows_the_shells_in_the_one_stylesheet()
    {
        string css = Css;

        css.Should().Contain(Header);
        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf(ShellHeader, StringComparison.Ordinal), "sections are appended in cascade order (D26)");
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
    [InlineData("ms-k-cluster-strip")]
    [InlineData("ms-k-topic-group")]
    [InlineData("ms-k-span")]
    [InlineData("ms-k-note")]
    [InlineData("ms-k-exposure")]
    [InlineData("ms-k-partitions")]
    public void The_section_has_a_rule_for_each_class_the_design_names(string className)
    {
        Section().Should().Contain("." + className);
    }

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
