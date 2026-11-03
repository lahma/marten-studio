using System.Globalization;
using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The two 1.1 contrast sections (1.1-UX-FIX-B for the dark theme, 1.1-UX-FIX-B2 for the light one): the values they
/// pin, where they are allowed to declare them, and that the ratios they claim are true.
/// </summary>
/// <remarks>
/// Where a custom property is declared matters as much as its value. A later bare <c>:root</c> would beat an earlier
/// system-dark one and bring the old grey back in dark mode without any value being wrong, so the tests read the
/// declarations by the section that owns them.
/// </remarks>
public partial class StylesheetTests
{
    private const string DarkMuted = "#8796ac";
    private const string DarkBadge = "#93c5fd";
    private const string LightMuted = "#5e6d83";
    private const string AdviceSelectorDarkSystem = ".ms-studio:not([data-theme=\"light\"]) .ms-conn-advice";
    private const string AdviceSelectorDarkTheme = ".ms-studio[data-theme=\"dark\"] .ms-conn-advice";
    private const string AdviceSelectorLightTheme = ".ms-studio[data-theme=\"light\"] .ms-conn-advice";
    private const string AdviceSelectorLightSystem = ".ms-studio:not([data-theme=\"dark\"]) .ms-conn-advice";

    /// <summary>
    /// The dark values are pinned, in both spellings of "dark", and the section declares nothing for the light theme.
    /// </summary>
    [Fact]
    public void The_UX_FIX_B_section_pins_the_dark_values_and_declares_nothing_for_light()
    {
        string section = Section("1.1-UX-FIX-B Skip link");

        string systemDark = AtRuleBlock("@media (prefers-color-scheme: dark)", DarkMuted);
        Declaration(":root", "--ms-color-text-muted", systemDark).Should().Be(DarkMuted);
        Declaration(AdviceSelectorDarkSystem, "color", systemDark).Should().Be(DarkBadge);

        Declaration(".ms-studio[data-theme=\"dark\"]", "--ms-color-text-muted", section).Should().Be(DarkMuted);
        Declaration(AdviceSelectorDarkTheme, "color", section).Should().Be(DarkBadge);

        Matches(":root", section).Should().ContainSingle(
            "the only :root in the section is the one inside the system-dark media block; a bare one would declare for light");
        systemDark.Should().Contain(":root");

        AllSelectors(section).Where(DeclaresFor("light")).Should().BeEmpty(
            "the light theme is the next section's, and a light selector here would be the dark section declaring for light");
    }

    /// <summary>
    /// The light values are declared for a light system and for the explicit light theme only, so dark stays as it was.
    /// </summary>
    [Fact]
    public void The_UX_FIX_B2_section_declares_the_light_values_for_light_only()
    {
        string section = Section("1.1-UX-FIX-B2 light-theme contrast");

        string lightSystem = AtRuleBlock("@media not (prefers-color-scheme: dark)", ":root");
        Declaration(":root", "--ms-color-text-muted", lightSystem).Should().Be(LightMuted);
        Declaration(AdviceSelectorLightSystem, "color", lightSystem).Should().Be("var(--ms-color-primary-hover)");

        Declaration(".ms-studio[data-theme=\"light\"]", "--ms-color-text-muted", section).Should().Be(LightMuted);
        Declaration(AdviceSelectorLightTheme, "color", section).Should().Be("var(--ms-color-primary-hover)");

        Matches(":root", section).Should().ContainSingle(
            "a bare :root here, later in the file than the system-dark one, would bring the old grey back in dark mode");
        lightSystem.Should().Contain(":root", "and the one it has is inside the not-dark media block");

        AllSelectors(section).Where(DeclaresFor("dark")).Should().BeEmpty(
            "nothing in the light section may declare for the dark theme");
        section.Should().NotContain(DarkMuted).And.NotContain(DarkBadge);
    }

    /// <summary>The ratios the two sections' comments claim are the ratios of the values they declare.</summary>
    [Theory]
    [InlineData(LightMuted, "#ffffff", 5.26)]
    [InlineData(LightMuted, "#f8fafc", 5.03)]
    [InlineData(LightMuted, "#f1f5f9", 4.80)]
    [InlineData("#1d4ed8", "#dbeafe", 5.49)]
    [InlineData(DarkMuted, "#1e293b", 4.87)]
    [InlineData(DarkMuted, "#0f172a", 5.94)]
    [InlineData(DarkBadge, "#1e3a5f", 6.38)]
    public void The_contrast_the_sections_claim_is_the_contrast_the_values_have(string foreground, string background, double claimed)
    {
        double ratio = ContrastRatio(foreground, background);

        Math.Round(ratio, 2).Should().Be(claimed);
        ratio.Should().BeGreaterThanOrEqualTo(4.5, "WCAG AA for normal text");
    }

    /// <summary>The raw stylesheet from the section's banner to the next banner, comments removed.</summary>
    private static string Section(string heading)
    {
        string raw = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

        int marker = raw.IndexOf("===== " + heading, StringComparison.Ordinal);
        marker.Should().BeGreaterThan(-1, "the section '{0}' must exist in {1}", heading, StylesheetPath);

        // The banner that holds the heading is the first one after its own opening; the next banner ends the section.
        int end = raw.IndexOf("\n/* =====", marker, StringComparison.Ordinal);
        int closing = raw.IndexOf("*/", marker, StringComparison.Ordinal);
        string body = raw[(closing + 2)..(end < 0 ? raw.Length : end)];

        return CommentExpression().Replace(body, string.Empty);
    }

    private static IEnumerable<string> AllSelectors(string source)
    {
        foreach (Match match in RuleExpression().Matches(source))
        {
            foreach (string candidate in match.Groups["selectors"].Value.Split(','))
            {
                yield return candidate.Trim();
            }
        }
    }

    /// <summary>A selector that names the theme as a requirement rather than excluding it with <c>:not(...)</c>.</summary>
    private static Func<string, bool> DeclaresFor(string theme) => selector =>
        selector.Replace($":not([data-theme=\"{theme}\"])", string.Empty, StringComparison.Ordinal)
            .Contains($"[data-theme=\"{theme}\"]", StringComparison.Ordinal);

    private static double ContrastRatio(string foreground, string background)
    {
        double a = Luminance(foreground);
        double b = Luminance(background);

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            double value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d;

            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
    }
}
