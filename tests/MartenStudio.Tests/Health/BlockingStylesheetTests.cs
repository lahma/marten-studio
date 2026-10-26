using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// X3's stylesheet section, appended to <c>studio.css</c> in D26 order: every rule its own (<c>ms-blocking-</c>, and
/// <c>ms-sessions-tabs</c>), tokens only, notes in the secondary text colour, and a tree that narrows on a phone rather than
/// growing past it.
/// </summary>
public partial class BlockingStylesheetTests
{
    private const string Header = "X3. Database > Sessions > Blocking: who holds up whom, as trees";

    private static readonly string[] Owned = [".ms-blocking-", ".ms-sessions-tabs"];

    private static string Section()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));
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
            static x => Owned.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)),
            "X3 re-declares nothing an earlier section owns (D26)");
    }

    [Fact]
    public void It_uses_the_design_tokens_and_no_literal_colour()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-color-");
        section.Should().Contain("color: var(--ms-color-text-secondary);").And.NotContain("--ms-color-text-muted",
            "UX-6 measured the muted grey below 4.5:1 on the surface");
    }

    [Fact]
    public void The_tree_wraps_its_rows_and_narrows_its_nesting_on_a_phone()
    {
        string section = Section();

        int session = section.IndexOf(".ms-blocking-session {", StringComparison.Ordinal);
        section[session..section.IndexOf('}', session)].Should().Contain("flex-wrap: wrap;", "a session's fields wrap rather than widen the page (D25)");

        int phone = section.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal);
        phone.Should().BePositive();
        section[phone..].Should().Contain(".ms-blocking-waiters").And.Contain("padding-left: var(--ms-space-sm);");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
