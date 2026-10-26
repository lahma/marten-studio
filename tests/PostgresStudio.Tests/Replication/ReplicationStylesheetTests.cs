using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// W5-R2's stylesheet section: every rule its own (<c>ms-repl-</c>), tokens only, and one column on a phone - read through
/// one path the orchestrator points at <c>studio.css</c> when it folds the part in (W5-index §4, D26).
/// </summary>
public partial class ReplicationStylesheetTests
{
    /// <summary>Where the section is: the part until it is folded, then <c>src/PostgresStudio/wwwroot/css/studio.css</c>.</summary>
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "W5-R2 - Operations › Replication";

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
            static x => x.StartsWith(".ms-repl-", StringComparison.Ordinal),
            "W5-R2 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
    }

    [Fact]
    public void On_a_phone_the_counts_and_the_facts_are_one_column()
    {
        string section = Section();
        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);

        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-repl-stats,").And.Contain(".ms-repl-facts {").And.Contain("grid-template-columns: minmax(0, 1fr);");
    }

    /// <summary>
    /// UI-fix: the tablespaces' "Size" header is <c>.ms-sr-only</c>, which is absolute, so each of the page's tables must be
    /// positioned - or that text is placed against the initial containing block, escapes the table's scroll region, and
    /// widened the whole document to 484 px at 390 px (D25), on both samples.
    /// </summary>
    [Fact]
    public void Every_table_is_the_containing_block_of_its_visually_hidden_text()
    {
        Match rule = Regex.Match(Section(), @"(?m)^\.ms-repl-table \{(?<body>[^}]*)\}");

        rule.Success.Should().BeTrue("the section positions .ms-repl-table itself, not one of its descendants");
        rule.Groups["body"].Value.Should().Contain("position: relative;");

        string page = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Replication", "Replication.razor"));

        foreach (Match table in Regex.Matches(page, "<table class=\"(?<classes>[^\"]*)\""))
        {
            table.Groups["classes"].Value.Split(' ').Should().Contain("ms-repl-table", "every table on the page takes the positioned rule: " + table.Value);
        }
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
