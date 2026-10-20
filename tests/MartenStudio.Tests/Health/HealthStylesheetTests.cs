using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// HEALTH-1, the advisor's stylesheet section: appended after UX-7 (D26), prefixed, and carrying the seven-tile
/// row the Overview's Health tile needs.
/// </summary>
public class HealthStylesheetTests
{
    private static string Css => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    [Fact]
    public void The_health_section_comes_after_the_last_0_3_section()
    {
        string css = Css;

        int health = css.IndexOf("HEALTH-1. The health advisor", StringComparison.Ordinal);
        health.Should().BePositive();
        css.IndexOf("UX-7. Final polish", StringComparison.Ordinal).Should().BePositive().And.BeLessThan(health);
    }

    /// <summary>Every rule HEALTH-1 adds is the advisor's own, so nothing earlier changes underneath it.</summary>
    [Fact]
    public void Every_selector_in_the_health_section_is_the_advisors_own()
    {
        string css = Css;
        string section = css[css.IndexOf("HEALTH-1. The health advisor", StringComparison.Ordinal)..];

        // To the next section's header, so the HEALTH-2, -3 and -4 sections later packets append are theirs.
        int next = section.IndexOf("\n/* ====", StringComparison.Ordinal);
        if (next >= 0)
        {
            section = section[..next];
        }

        IEnumerable<string> selectors = section
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim());

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-health", StringComparison.Ordinal)
                || x.StartsWith(".ms-overview-health", StringComparison.Ordinal)
                || x == ".ms-overview-tiles",
            "HEALTH-1 re-declares nothing an earlier section owns, but the Overview's tile row");
    }

    [Fact]
    public void Seven_tiles_fit_one_desktop_row_and_the_narrow_layout_is_left_alone()
    {
        string css = Css;
        int media = css.IndexOf("@media (min-width: 1025px) {\n    .ms-overview-tiles {", StringComparison.Ordinal);

        media.Should().BePositive("the override applies above the 1 024 px breakpoint only");
        css.IndexOf("grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));", media, StringComparison.Ordinal)
            .Should().BePositive("seven 140 px tracks and six gaps fit a 1 100 px content column");
    }
}
