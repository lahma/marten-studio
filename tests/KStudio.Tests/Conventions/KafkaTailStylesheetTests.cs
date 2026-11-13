using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D26 for the live tail page: the <c>KS-UI-TAIL</c> section is appended to the one stylesheet after UI-EXPORT's, holds only <c>ms-k-</c> rules, carries a rule for
/// each class the page adds to Search's, and keeps the 390 px rules (a long topic name or a sentence must not push the shell sideways).
/// </summary>
public partial class KafkaTailStylesheetTests
{
    private const string Header = "/* ===== KS-UI-TAIL: the live tail ===== */";

    private const string PreviousHeader = "/* ===== KS-UI-EXPORT: the Export menu and the cluster's profile chip ===== */";

    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css;
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is read by its header");

        int next = css.IndexOf("/* ===== ", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    private static string WithoutComments(string text) => Regex.Replace(text, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>Every declaration block, in the section, of a rule whose selector list names <paramref name="selector" />.</summary>
    private static string[] Declarations(string selector) =>
        [.. Block().Matches(WithoutComments(Section()))
            .Where(m => m.Groups[1].Value.Split(',').Any(s => s.Trim() == selector))
            .Select(static m => m.Groups[2].Value)];

    [Fact]
    public void The_section_follows_UI_EXPORTs_in_the_one_stylesheet()
    {
        string css = Css;

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf(PreviousHeader, StringComparison.Ordinal), "sections are appended in cascade order (D26)");
        Regex.Matches(css, Regex.Escape(Header), RegexOptions.None, TimeSpan.FromSeconds(5)).Should().HaveCount(1);
        css.TrimEnd().Should().EndWith("}", "the section is the last in the file, so a later one is appended after it");
    }

    [Fact]
    public void The_section_holds_only_ms_k_classes()
    {
        string[] selectors = [.. ClassName().Matches(WithoutComments(Section())).Select(static m => m.Groups[1].Value).Where(static x => !char.IsDigit(x[0])).Distinct()];

        selectors.Should().NotBeEmpty();
        selectors.Where(static x => !x.StartsWith("ms-k-", StringComparison.Ordinal)).Should().BeEmpty("the module re-declares no host class (D26, D34)");
    }

    [Theory]
    [InlineData("ms-k-tail")]
    [InlineData("ms-k-tail-status")]
    [InlineData("ms-k-tail-line")]
    [InlineData("ms-k-tail-chips")]
    [InlineData("ms-k-tail-chip")]
    [InlineData("ms-k-tail-chip-live")]
    [InlineData("ms-k-tail-paused")]
    [InlineData("ms-k-tail-actions")]
    [InlineData("ms-k-tail-matches")]
    public void The_section_has_a_rule_for_each_class_the_page_adds(string className)
    {
        Section().Should().Contain("." + className);
    }

    [Theory]
    [InlineData(".ms-k-tail", "min-width: 0")]
    [InlineData(".ms-k-tail-status", "min-width: 0")]
    [InlineData(".ms-k-tail-paused", "min-width: 0")]
    [InlineData(".ms-k-tail-line", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-tail-chip", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-tail-paused p", "overflow-wrap: anywhere")]
    [InlineData(".ms-k-tail-forbidden", "overflow-wrap: anywhere")]
    public void The_narrow_screen_rules_are_there(string selector, string declaration)
    {
        Declarations(selector).Should().Contain(x => x.Contains(declaration, StringComparison.Ordinal), selector + " has " + declaration + " (390 px)");
    }

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Block();

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
