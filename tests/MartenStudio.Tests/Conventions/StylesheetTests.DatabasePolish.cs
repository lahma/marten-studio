namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The UX-6 section: what the 0.3.0 UI validation measured on the database browser and the Relationships
/// diagram, and fixed in the stylesheet where no markup test can see it.
/// </summary>
/// <remarks>
/// Several of these re-declare a selector an earlier section also declares, and win by source order (D26).
/// So they are asserted against the <em>last</em> rule for the selector, which is the one a browser applies
/// when the two have the same specificity.
/// </remarks>
public partial class StylesheetTests
{
    /// <summary>
    /// Scaled down to fit a desktop column, the diagram's arrow labels and the second line of every box came
    /// out at about seven pixels. It is drawn at its own size now, inside a labelled scroll region.
    /// </summary>
    [Fact]
    public void The_relationships_diagram_is_never_shrunk_to_fit()
    {
        LastDeclaration(".ms-graph-svg", "max-width").Should().Be("none",
            "the earlier max-width: 100% scaled the picture down until its text was unreadable");
    }

    /// <summary>
    /// The page heading's ring is drawn for a keyboard focus only - and "keyboard" is what the visitor last
    /// did, not Chromium's guess, which counts every script focus on a page with no pointer input yet.
    /// </summary>
    [Fact]
    public void The_page_heading_has_no_ring_unless_the_visitor_is_using_the_keyboard()
    {
        Declaration(".ms-studio h1:focus-visible:not(:root[data-ms-keyboard] *)", "outline").Should().Be("none");

        Declaration(".ms-studio h1:focus-visible", "outline").Should().Contain("var(--ms-color-primary)",
            "the keyboard ring itself is still the shell's, untouched");

        File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "wwwroot", "js", "marten-studio.js"))
            .Should().Contain("data-ms-keyboard", "the attribute the rule reads is the one the script writes");
    }

    /// <summary>
    /// The SQL console's cells carry their kind as <c>ms-query-kind-*</c>, because <c>ms-query-cell-text</c>
    /// is the inline-block span inside them. The old names are gone rather than left as rules for a class no
    /// markup uses.
    /// </summary>
    [Fact]
    public void The_console_s_cell_kinds_are_named_apart_from_the_span_inside_them()
    {
        Declaration(".ms-query-kind-number", "text-align").Should().Be("right");

        Matches(".ms-query-cell-number").Should().BeEmpty();
        Matches(".ms-query-cell-number .ms-query-cell-text").Should().BeEmpty();
    }

    /// <summary>On a phone the kind tabs are one row that scrolls sideways, not six tabs on three rows.</summary>
    [Fact]
    public void The_database_tabs_are_one_row_that_scrolls_on_a_narrow_screen()
    {
        string block = MediaBlock("max-width: 900px", ".ms-db-kind-tabs");

        Rule(".ms-db-kind-tabs", block).Should().Contain("flex-wrap: nowrap;").And.Contain("overflow-x: auto;");
    }

    /// <summary>
    /// WCAG AA wants 4.5:1. Muted text is 2.10:1 on the info tint in the light theme, and the schema prefix
    /// in muted was 3.07:1 on the dark surface.
    /// </summary>
    [Fact]
    public void The_notices_and_the_schema_prefix_are_not_drawn_in_muted_text()
    {
        LastDeclaration(".ms-capability-disabled .ms-muted", "color").Should().Be("var(--ms-color-text)");
        LastDeclaration(".ms-db-schema-prefix", "color").Should().Be("var(--ms-color-text-secondary)");
        LastDeclaration(".ms-graph-table-chip-schema", "color").Should().Be("var(--ms-color-text-secondary)");
    }

    /// <summary>The value of one declaration in the last rule for <paramref name="selector" /> - the one that wins.</summary>
    private static string LastDeclaration(string selector, string property)
    {
        List<string> blocks = Matches(selector);

        if (blocks.Count == 0)
        {
            throw new InvalidOperationException($"{StylesheetPath} has no rule for '{selector}'.");
        }

        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            blocks[^1],
            @"(?m)^\s*" + System.Text.RegularExpressions.Regex.Escape(property) + @"\s*:\s*(?<value>[^;]+);",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        if (!match.Success)
        {
            throw new InvalidOperationException($"The last rule for '{selector}' declares no '{property}'.");
        }

        return match.Groups["value"].Value.Trim();
    }
}
