using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// 0.8-B's stylesheet section, appended to <c>studio.css</c> in D26 order after UI-0.6.1: every selector its own
/// (<c>ms-holders-</c>), tokens only, notes in the secondary text colour, and the tab's tag that drops under its name on a phone
/// rather than widening the row.
/// </summary>
public partial class HoldersStylesheetTests
{
    private const string Header = "0.8-B. Database > Sessions > Holders: who holds the whole server's connections";

    private static readonly string[] Owned = [".ms-holders-"];

    private static string Css() => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    private static string Section()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_is_appended_after_the_last_earlier_one_in_D26_order()
    {
        string css = Css();
        int ui = css.IndexOf("UI-0.6.1. Sessions: a statement cell is at most six lines tall", StringComparison.Ordinal);
        int holders = css.IndexOf(Header, StringComparison.Ordinal);

        ui.Should().BeGreaterThan(0);
        holders.Should().BeGreaterThan(ui, "later wins on purpose (D26), and this section re-declares nothing earlier");
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
            static x => Owned.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)),
            "0.8-B re-declares nothing an earlier section owns (D26)");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour_and_only_tokens_that_exist()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
        section.Should().Contain("color: var(--ms-color-text-secondary);").And.NotContain("--ms-color-text-muted",
            "UX-6 measured the muted grey below 4.5:1 on the surface");

        string css = Css();

        foreach (Match token in TokenUse().Matches(section))
        {
            css.Should().Contain(token.Groups["name"].Value + ":", token.Groups["name"].Value + " is a token the stylesheet declares");
        }
    }

    [Fact]
    public void The_tag_drops_under_its_name_on_a_phone()
    {
        string section = Section();

        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);
        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-holders-tag").And.Contain("display: inline-block;");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();

    [GeneratedRegex(@"var\((?<name>--ms-[a-z0-9-]+)\)")]
    private static partial Regex TokenUse();
}
