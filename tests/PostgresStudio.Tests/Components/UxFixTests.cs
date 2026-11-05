using System.Text.RegularExpressions;

using Bunit;

using MartenStudio.Tests.Conventions;

using PostgresStudio.Components.Shared;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-UX-FIX: the name helper the Maintenance table uses (a break opportunity after an underscore or a dot and nowhere else), and
/// the sweep's stylesheet section.
/// </summary>
public partial class UxFixTests
{
    private const string Stylesheet = "src/PostgresStudio/wwwroot/css/studio.css";

    private const string Header = "/* ===== 1.2-UX: the sweep's fixes ===== */";

    [Theory]
    [InlineData("sweep_child", "sweep_|child")]
    [InlineData("public.sweep_child", "public.|sweep_|child")]
    [InlineData("a_b.c", "a_|b.|c")]
    [InlineData("nothing", "nothing")]
    [InlineData("trailing_", "trailing_")]
    [InlineData("_leading", "_|leading")]
    [InlineData("", "")]
    public void A_name_is_cut_after_each_underscore_and_dot_and_the_parts_join_back_into_the_name(string name, string expected)
    {
        BreakableName.Parts(name).Should().Equal(expected.Length == 0 ? [] : expected.Split('|'));
        string.Concat(BreakableName.Parts(name)).Should().Be(name);
    }

    [Fact]
    public void The_rendered_name_reads_exactly_as_written_with_a_wbr_after_each_part_but_the_last_has_none_to_add_a_character()
    {
        using BunitContext context = new();

        IRenderedComponent<BreakableName> name = context.Render<BreakableName>(parameters => parameters.Add(x => x.Value, "quartz.sweep_child"));

        name.Markup.Should().Be("quartz.<wbr>sweep_<wbr>child<wbr>");
        name.Nodes.Select(static x => x.TextContent).Aggregate(string.Concat).Should().Be("quartz.sweep_child", "<wbr> adds no character: it copies as written");
    }

    [Fact]
    public void A_missing_name_renders_nothing()
    {
        using BunitContext context = new();

        context.Render<BreakableName>().Markup.Should().BeEmpty();
    }

    private static string Section()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine([.. Stylesheet.Split('/')]));
        int start = css.IndexOf(Header, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the sweep's section is in studio.css (D26)");

        int next = css.IndexOf("/* ====", start + Header.Length, StringComparison.Ordinal);
        return next < 0 ? css[start..] : css[start..next];
    }

    [Fact]
    public void The_section_uses_the_design_tokens_and_no_literal_colour_and_no_name_may_break_anywhere()
    {
        string section = Section();

        HexColour().IsMatch(section).Should().BeFalse("colours come from the tokens (hard rule 3)");
        section.Should().Contain("var(--ms-space-");

        // The Triggers name breaks at its <wbr>s: `anywhere` is what broke it in the middle of an identifier.
        Regex.Match(section, @"\.ms-maint-name\s*\{[^}]*\}").Value.Should().Contain("overflow-wrap: break-word").And.NotContain("anywhere");
    }

    [Fact]
    public void The_projections_actions_column_is_narrowed_by_stacking_the_shard_buttons()
    {
        string section = Section();

        Regex.Match(section, @"\.ms-projections-table \.ms-shard-start,\s*\.ms-projections-table \.ms-shard-stop\s*\{[^}]*\}").Value
            .Should().Contain("display: block");
        Regex.Match(section, @"\.ms-projections-table \.ms-projection-type\s*\{[^}]*\}").Value.Should().Contain("max-width: 10rem");
    }

    [GeneratedRegex("#[0-9a-fA-F]{3,8}\\b")]
    private static partial Regex HexColour();
}
