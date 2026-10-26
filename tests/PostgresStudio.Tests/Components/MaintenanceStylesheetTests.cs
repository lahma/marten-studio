using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// W5-V1's stylesheet section: every rule its own (<c>ms-maint-</c>), tokens only, and one column on a phone - read through
/// one path the orchestrator points at <c>studio.css</c> when it folds the part in (W5-index §4, D26).
/// </summary>
public partial class MaintenanceStylesheetTests
{
    /// <summary>Where the section is: the part until it is folded, then <c>src/PostgresStudio/wwwroot/css/studio.css</c>.</summary>
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "W5-V1 - the Maintenance page";

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
            static x => x.StartsWith(".ms-maint-", StringComparison.Ordinal),
            "W5-R2 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
