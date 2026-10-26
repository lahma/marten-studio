using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// W5-Q1's stylesheet section: every rule its own (<c>ms-stmts-</c>), tokens only, and the controls one column on a phone -
/// read through one path the orchestrator points at <c>studio.css</c> when it folds the part in (W5-index §4, D26).
/// </summary>
public partial class StatementsStylesheetTests
{
    /// <summary>Where the section is: the part until it is folded, then <c>src/PostgresStudio/wwwroot/css/studio.css</c>.</summary>
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "W5-Q1. Statements";

    private static string Section()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine([.. Stylesheet.Split('/')]));
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
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
            static x => x.StartsWith(".ms-stmts-", StringComparison.Ordinal),
            "W5-Q1 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
    }

    [Fact]
    public void On_a_phone_the_controls_are_one_column_and_the_statement_wraps()
    {
        string section = Section();
        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);

        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-stmts-controls {").And.Contain("flex-direction: column;");
        section.Should().Contain("overflow-wrap: anywhere;", "a 4,096-character statement wraps rather than widening the table");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();
}
