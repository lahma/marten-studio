namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The UX-8 section: a refused query's message and excerpt, which sat side by side in two narrow columns at 390 px -
/// the message wrapping every two or three words and the excerpt cut off after "System.Diagnos" - found by the 0.4 UI
/// sweep in a real browser, where no markup test can see it.
/// </summary>
public partial class StylesheetTests
{
    /// <summary>The query container the narrow rule asks about.</summary>
    private const string QueryStatusContainer = "ms-query-status";

    /// <summary>
    /// When the box is narrower than 40rem the excerpt goes under the message, as wide as the box: a column, stretched,
    /// in place of <c>.ms-alert</c>'s row of shrink-wrapped children.
    /// </summary>
    [Fact]
    public void A_refusal_puts_its_excerpt_under_the_message_when_the_box_is_narrow()
    {
        string block = ContainerBlock(QueryStatusContainer, "max-width: 40rem", ".ms-query-error");

        Declaration(".ms-alert.ms-query-error", "flex-direction", block).Should().Be("column");
        Declaration(".ms-alert.ms-query-error", "align-items", block).Should().Be("stretch",
            ".ms-alert's flex-start would leave the excerpt as wide as its line, past the box's edge");

        Declaration(".ms-alert.ms-query-error > .ms-query-caret", "margin", block).Should().Be("0",
            "the alert's gap spaces the two once they are stacked");
    }

    /// <summary>
    /// The excerpt scrolls inside itself rather than wrapping, and the caret stays under its character: one unwrapped
    /// line over the caret line, and a tab as wide as the one space the caret line has for it.
    /// </summary>
    [Fact]
    public void The_excerpt_scrolls_inside_itself_and_its_caret_keeps_its_column()
    {
        Declaration(".ms-query-caret", "white-space").Should().Be("pre", "a wrapped excerpt would move the caret off its character");
        Declaration(".ms-query-caret", "overflow-x").Should().Be("auto");

        Declaration(".ms-query-error > .ms-query-caret", "tab-size").Should().Be("1",
            "the caret line has one space for each character before the error, a tab included");
    }

    /// <summary>
    /// The JSON editor's parse error draws its caret line the same way - one space for each UTF-8 byte before the
    /// error, and a tab is one byte - so a tab in the document is one column wide there too.
    /// </summary>
    [Fact]
    public void The_JSON_editor_s_caret_keeps_its_column_past_a_tab()
    {
        Declaration(".ms-editor-error > .ms-editor-caret", "tab-size").Should().Be("1",
            "a tab drawn up to eight columns wide puts the caret up to seven columns left of the character it points at");
        Declaration(".ms-editor-caret", "white-space").Should().Be("pre");
    }

    /// <summary>
    /// The width that decides is the box's, so both places a refusal is drawn - the editor's status line, which every
    /// mode's <c>QueryEditor</c> renders, and LINQ mode's Order by status - are the named container the rule queries.
    /// </summary>
    [Fact]
    public void Both_places_a_refusal_is_drawn_are_the_container_the_rule_asks_about()
    {
        LastDeclaration(".ms-query-editor-status", "container").Should().Be(QueryStatusContainer + " / inline-size");
        LastDeclaration(".ms-query-linq-order-status", "container").Should().Be(QueryStatusContainer + " / inline-size");
    }

    /// <summary>
    /// Every component that draws a refusal box draws it inside one of those containers - otherwise the rule above is
    /// a rule for nothing. Read from the markup, where the class names are.
    /// </summary>
    [Fact]
    public void Every_component_that_draws_a_refusal_draws_it_inside_the_container()
    {
        // The class as a whole token: "ms-query-error" followed by the attribute's end or another class, never the
        // "-message" of the element inside it.
        List<(string Name, string Markup)> drawing = [.. Directory
            .EnumerateFiles(RepositoryRoot.Combine("src", "MartenStudio"), "*.razor", SearchOption.AllDirectories)
            .Select(static x => (Name: Path.GetFileName(x), Markup: File.ReadAllText(x)))
            .Where(static x => x.Markup.Contains("ms-query-error\"", StringComparison.Ordinal)
                || x.Markup.Contains("ms-query-error ", StringComparison.Ordinal))];

        drawing.Select(static x => x.Name).Should().Contain("QueryEditor.razor", "the editor draws the refusal for all three modes")
            .And.Contain("LinqQueryPanel.razor", "LINQ mode draws its Order by's own");

        foreach ((string name, string markup) in drawing)
        {
            (markup.Contains("ms-query-editor-status", StringComparison.Ordinal)
                    || markup.Contains("ms-query-linq-order-status", StringComparison.Ordinal))
                .Should().BeTrue($"{name} draws .ms-query-error, and the narrow rule only reaches one inside a query container");
        }
    }

    /// <summary>UX-8 is appended at the end, after PGV, as D26 has every section appended in cascade order.</summary>
    [Fact]
    public void The_refusal_section_comes_last()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

        int section = css.IndexOf("UX-8. A query refusal on a narrow screen", StringComparison.Ordinal);
        section.Should().BePositive();

        css.IndexOf("PGV. pgvector awareness", StringComparison.Ordinal).Should().BePositive().And.BeLessThan(section);
        css.IndexOf("@container " + QueryStatusContainer, StringComparison.Ordinal).Should().BeGreaterThan(section);
        css.IndexOf("container: " + QueryStatusContainer, StringComparison.Ordinal).Should().BeGreaterThan(section);
    }
}
