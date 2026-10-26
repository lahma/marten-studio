using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Storage;

/// <summary>
/// W5-CAP's stylesheet section: every rule its own (<c>ms-storage-</c>), tokens only, notes in the secondary text colour, and
/// one column of tiles on a phone - read through one path the orchestrator points at <c>studio.css</c> when it folds the part
/// in (W5-index §4, D26).
/// </summary>
public partial class StorageStylesheetTests
{
    /// <summary>Where the section is: the part until it is folded, then <c>src/PostgresStudio/wwwroot/css/studio.css</c>.</summary>
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/parts/cap.css";

    private const string Header = "W5-CAP. Storage: the database's size, its growth, and the largest tables";

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
            .Where(static x => (x.EndsWith('{') || x.EndsWith(',')) && !x.StartsWith('@') && x.StartsWith('.'))
            .SelectMany(static x => x.TrimEnd('{', ',').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-storage-", StringComparison.Ordinal),
            "W5-CAP re-declares nothing an earlier section owns");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
    }

    [Fact]
    public void Its_notes_use_the_secondary_text_colour_and_never_the_muted_one()
    {
        string section = Section();

        section.Should().Contain("color: var(--ms-color-text-secondary);");
        section.Should().NotContain("--ms-color-text-muted", "UX-6 measured the muted grey below 4.5:1 on the surface");
    }

    [Fact]
    public void The_share_bar_is_drawn_from_the_custom_property_the_page_sets()
    {
        string section = Section();
        int bar = section.IndexOf(".ms-storage-bar {", StringComparison.Ordinal);

        bar.Should().BePositive();
        section[bar..section.IndexOf('}', bar)].Should().Contain("var(--ms-storage-share, 0%)");
    }

    [Fact]
    public void On_a_phone_the_tiles_are_one_column()
    {
        string section = Section();
        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);

        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-storage-tiles {").And.Contain("grid-template-columns: minmax(0, 1fr);");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
