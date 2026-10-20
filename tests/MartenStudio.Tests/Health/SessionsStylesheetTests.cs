using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// HEALTH-3, Database › Sessions' stylesheet section: appended after HEALTH-1 (D26), and every rule in it its own -
/// prefixed <c>ms-sessions-</c>, or scoped under the rail's Activity band - so nothing earlier changes beneath it.
/// </summary>
public class SessionsStylesheetTests
{
    private const string Header = "HEALTH-3. Database > Sessions";

    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    /// <summary>The section's own text: from its header to the next section's, or to the end.</summary>
    private static string Section()
    {
        string css = Css;
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BePositive("the section is there");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_sessions_section_comes_after_the_health_advisors()
    {
        string css = Css;

        css.IndexOf("HEALTH-1. The health advisor", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(css.IndexOf(Header, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_selector_in_the_sessions_section_is_its_own()
    {
        IEnumerable<string> selectors = Section()
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim());

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-sessions-", StringComparison.Ordinal) || x.StartsWith(".ms-db-rail-activity", StringComparison.Ordinal),
            "HEALTH-3 re-declares nothing an earlier section owns");
    }

    [Fact]
    public void A_statement_wraps_inside_its_column_rather_than_widening_the_table()
    {
        string section = Section();

        section.Should().Contain(".ms-sessions-statement code {");
        section.Should().Contain("overflow-wrap: anywhere;");
        section.Should().Contain("max-width: 60ch;");
    }
}
