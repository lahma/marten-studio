using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.9-A's stylesheet section (X3, issue #28): appended last (D26), every rule its own (<c>ms-plan-</c>), the heat scale four
/// steps defined once as tokens with the dark theme's own - on <c>:root</c>, in the dark media query and under both explicit theme
/// attributes - and no literal colour anywhere else; and the studio.js helper the pasted plan travels by.
/// </summary>
public partial class PlanAnalyzerStylesheetTests
{
    private const string Header = "0.9-A. Query › Plan";

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
    public void The_section_is_after_every_earlier_one()
    {
        string css = Css();

        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("UI-0.8. The rail's credit line", StringComparison.Ordinal), "D26: appended in cascade order");
    }

    [Fact]
    public void Every_selector_in_the_section_is_its_own_or_a_theme_block_for_its_tokens()
    {
        IEnumerable<string> selectors = Section()
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-plan", StringComparison.Ordinal) || x == ":root" || x.StartsWith(".ms-studio[data-theme=", StringComparison.Ordinal),
            "0.9-A re-declares nothing an earlier section owns but .ms-plan-node, which it says so about");

        Section().Should().Contain("re-declares only\n   `.ms-plan-node`");
    }

    [Fact]
    public void The_heat_scale_is_four_steps_on_root_with_the_dark_themes_own_in_every_place_a_theme_is_set()
    {
        string section = Section();

        foreach (string block in new[] { "\n:root {", "\n    :root {", "\n.ms-studio[data-theme=\"light\"] {", "\n.ms-studio[data-theme=\"dark\"] {" })
        {
            int at = section.IndexOf(block, StringComparison.Ordinal);
            at.Should().BeGreaterThanOrEqualTo(0, "{0} defines the scale", block.Trim());

            string declared = section[at..section.IndexOf('}', at)];

            foreach (string step in new[] { "--ms-plan-heat-1:", "--ms-plan-heat-2:", "--ms-plan-heat-3:", "--ms-plan-heat-4:" })
            {
                declared.Should().Contain(step);
            }
        }

        section.Should().Contain("@media (prefers-color-scheme: dark)");
    }

    [Fact]
    public void A_literal_colour_appears_only_in_the_four_token_blocks()
    {
        string section = Section();

        // The part from the first rule that is not a token block: rules use tokens.
        string rules = section[section.IndexOf("\n.ms-plan-page {", StringComparison.Ordinal)..];

        HexColour().IsMatch(rules).Should().BeFalse("colours come from the tokens (hard rule 3)");
        rules.Should().Contain("var(--ms-plan-heat-1)").And.Contain("var(--ms-plan-heat-4)").And.Contain("var(--ms-color-");

        // The scale never leaves its own four names.
        Regex.Matches(section, @"--ms-plan-heat-(\d+)", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(static x => x.Groups[1].Value).Distinct().Order().Should().Equal("1", "2", "3", "4");
    }

    [Fact]
    public void The_sort_arrows_and_the_blank_dash_are_drawn_by_the_stylesheet_never_by_the_text()
    {
        string section = Section();

        section.Should().Contain("th[aria-sort=\"ascending\"] .ms-plan-sort::after").And.Contain("th[aria-sort=\"descending\"] .ms-plan-sort::after");
        section.Should().Contain(".ms-plan-strip-item dd:empty::before", "a blank item's text stays empty and the stylesheet puts a dash there");
    }

    [Fact]
    public void The_scripts_chunked_hand_over_matches_the_component_that_receives_it()
    {
        string script = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "js", "studio.js"));
        string component = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Components", "Pages", "Query", "PlanPaste.razor"));

        script.Should().Contain("sendInChunks: async function (textarea, dotNetRef, chunkCharacters, maxCharacters)")
            .And.Contain("invokeMethodAsync(\"BeginPaste\")").And.Contain("invokeMethodAsync(\"PasteChunk\"");
        component.Should().Contain("public Task BeginPaste()").And.Contain("public Task PasteChunk(string chunk)").And.Contain("martenStudio.query.sendInChunks");
        component.Should().Contain("[JSInvokable]");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();
}
