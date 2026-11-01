using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// What 0.10's UI sweep found (D26): a selected plan node's most common values and histogram bounds are one long unbroken
/// text, which at 390 px pushed the content column sideways. The section is appended last and lets the text wrap anywhere.
/// </summary>
public class PlanUiSweepStylesheetTests
{
    private const string Header = "UI-0.10. What 0.10's UI sweep found";

    private static string Css() => File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

    [Fact]
    public void The_section_is_appended_after_0_10_B_and_lets_a_node_s_values_wrap_anywhere()
    {
        string css = Css();
        int start = css.IndexOf(Header, StringComparison.Ordinal);

        start.Should().BeGreaterThan(css.IndexOf("0.10-B. Compare plans", StringComparison.Ordinal), "D26: appended in cascade order");
        css.IndexOf("/* ====", start, StringComparison.Ordinal).Should().Be(-1, "it is the last section");

        string section = css[start..];
        section.Should().Contain(".ms-plan-node-facts .ms-plan-key-help {\n    overflow-wrap: anywhere;\n}");
        section.Should().Contain(".ms-plan-node-facts dd {\n    min-width: 0;\n}");
    }
}
