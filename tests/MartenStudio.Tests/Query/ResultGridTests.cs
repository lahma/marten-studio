using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Tests.Components;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The SQL console's grid, on its own.
/// </summary>
public class ResultGridTests
{
    /// <summary>
    /// UX-6, U7: the grid put <c>ms-query-cell-{kind}</c> on each <c>&lt;td&gt;</c>, which for a text cell
    /// spells <c>ms-query-cell-text</c> - the class of the inline-block span inside the cell. A cell that is an
    /// inline-block is not a table cell, and the console's text columns fell out of the grid. The kind is on
    /// the cell under a class of its own now, and the span keeps its class.
    /// </summary>
    [Fact]
    public void A_cell_carries_its_kind_under_a_class_of_its_own_and_never_the_span_s()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, [("name", "text"), ("total", "numeric"), ("note", "text")])
            .Add(x => x.Rows, [[("alpha", "text"), ("42", "number"), (string.Empty, ResultGrid.NullKind)]]));

        List<IElement> cells = [.. grid.FindAll("tbody td.ms-query-cell")];
        cells.Should().HaveCount(3);

        cells[0].ClassList.Should().Contain("ms-query-kind-text").And.NotContain("ms-query-cell-text");
        cells[1].ClassList.Should().Contain("ms-query-kind-number");
        cells[2].ClassList.Should().Contain("ms-query-kind-null");

        grid.FindAll("td.ms-query-cell-text, td[class*='ms-query-cell-number']").Should().BeEmpty(
            "no td may take a class the stylesheet gives to the span inside it");
        cells[0].QuerySelector("span.ms-query-cell-text")!.TextContent.Should().Be("alpha", "the span keeps its own class");
        cells[2].QuerySelector(".ms-query-null")!.TextContent.Should().Be("NULL");
    }
}
