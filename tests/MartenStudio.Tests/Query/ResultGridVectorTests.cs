using Bunit;

using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components.Web;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

using MartenQueryBody = MartenStudio.Components.Pages.Query.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// 1.2-VEC in the console's grid: a pgvector column is drawn the way the database browser's row grid draws one - its type and
/// first components, a chip that opens the whole value - in the shared grid, in the shared panel and on the Marten Query page's
/// SQL tab; every other column is exactly what it was.
/// </summary>
public class ResultGridVectorTests
{
    private const string Five = "[0.0123,-0.0441,0.2,0.7,0.5]";

    private static readonly PgVectorColumn Vector = PgVector.Column("vector", "vector", -1)!;

    private static IReadOnlyList<(string Name, string Type)> Columns { get; } =
        [("id", "integer"), ("embedding", "vector"), ("seen", "timestamp with time zone")];

    private static IReadOnlyList<IReadOnlyList<(string Text, string Kind)>> Rows(string vectorText) =>
        [[("1", "number"), (vectorText, "text"), ("2026-01-02T03:04:05+00:00", "text")]];

    [Fact]
    public void A_vector_column_s_cell_is_its_type_and_first_components_and_every_other_cell_is_unchanged()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(Five))
            .Add(x => x.Vectors, [null, Vector, null]));

        grid.Find("button.ms-vector-chip").TextContent.Should().Be("vector");
        grid.Find(".ms-vector-preview").TextContent.Should().Be("[0.0123, -0.0441, 0.2, …]");
        grid.Find(".ms-vector-preview").GetAttribute("title").Should().Be("vector: 5 components, the first 3 shown.");
        grid.Markup.Should().NotContain("0.7", "the fourth component is not drawn in the grid");

        List<AngleSharp.Dom.IElement> cells = [.. grid.FindAll("tbody td.ms-query-cell")];
        cells[0].QuerySelector("span.ms-query-cell-text")!.TextContent.Should().Be("1");
        cells[2].QuerySelector("span.ms-query-cell-text")!.TextContent.Should().Be("2026-01-02T03:04:05+00:00",
            "a column that is not a vector is not drawn in the row grid's rich mode: no time zone conversion, no hint");
        cells[2].QuerySelectorAll(".ms-row-time, .ms-row-notz").Should().BeEmpty();
    }

    [Fact]
    public void Without_vectors_the_grid_is_what_it_always_was()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(Five)));

        grid.FindAll(".ms-vector-chip, .ms-vector-preview").Should().BeEmpty();
        grid.FindAll("tbody td.ms-query-cell")[1].QuerySelector("span.ms-query-cell-text")!.TextContent.Should().Be(Five);
    }

    [Fact]
    public async Task The_chip_opens_the_whole_value_with_Copy_and_Close_closes_it()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(Five))
            .Add(x => x.Vectors, [null, Vector, null]));

        grid.FindAll(".ms-query-json-panel").Should().BeEmpty();

        await grid.Find("button.ms-vector-chip").ClickAsync(new MouseEventArgs());

        grid.Find(".ms-row-text-panel-body").TextContent.Should().Be(Five);
        grid.Find(".ms-row-text-panel").GetAttribute("aria-label").Should().Be("embedding (row 1)");
        grid.Find(".ms-vector-panel-actions .ms-copy-button").GetAttribute("aria-label").Should().Be("Copy the whole value of embedding (row 1)");
        grid.FindAll(".ms-query-json-panel .json-view, .ms-query-json-panel [class*='ms-json']").Should().BeEmpty("a vector is not opened in the JSON viewer");

        await grid.Find(".ms-query-json-panel-header button").ClickAsync(new MouseEventArgs());

        grid.FindAll(".ms-query-json-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task A_cut_value_is_collapsed_from_what_was_read_and_is_not_offered_for_copying()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows("[0.1,0.2,0.3,0.4…"))
            .Add(x => x.Vectors, [null, Vector, null])
            .Add(x => x.CutCells, new HashSet<(int Row, int Column)> { (0, 1) }));

        grid.Find(".ms-vector-preview").TextContent.Should().Be("[0.1, 0.2, 0.3, …]");
        grid.Find(".ms-vector-preview").GetAttribute("title").Should().Contain("more components than were read");

        await grid.Find("button.ms-vector-chip").ClickAsync(new MouseEventArgs());

        grid.Find(".ms-row-json-panel .ms-row-value-note").TextContent.Should().Contain("not offered for copying");
        grid.FindAll(".ms-row-json-panel .ms-copy-button").Should().BeEmpty();
    }

    [Fact]
    public void A_NULL_vector_has_no_chip()
    {
        using var context = new StudioComponentContext();

        var grid = context.Render<ResultGrid>(parameters => parameters
            .Add(x => x.Columns, Columns)
            .Add(x => x.Rows, Rows(string.Empty).Select(static row => (IReadOnlyList<(string Text, string Kind)>)
                [row[0], ("NULL", ResultGrid.NullKind), row[2]]).ToList())
            .Add(x => x.Vectors, [null, Vector, null]));

        grid.FindAll(".ms-vector-chip").Should().BeEmpty();
        grid.Find(".ms-query-null").TextContent.Should().Be("NULL");
    }

    private static SqlConsoleResult VectorResult(bool cut = false) =>
        new(
            [new SqlResultColumn("id", "integer"), new SqlResultColumn("embedding", "vector") { Vector = Vector }],
            [[new SqlCell("1", SqlCellKind.Number, false, 1), new SqlCell(cut ? "[0.1,0.2,0.3,0.4…" : Five, SqlCellKind.Text, cut, 99_999)]],
            Truncated: false,
            RowLimit: 500,
            Duration: TimeSpan.FromMilliseconds(7),
            Notices: [],
            Error: null,
            Rejection: null);

    [Fact]
    public async Task The_Marten_Query_page_s_SQL_tab_draws_the_collapsed_vector_from_the_console_s_result()
    {
        FakeQueryService service = new FakeQueryService().WithType("person");
        service.SqlResult = VectorResult();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<MartenQueryBody>();
        await page.Find("#ms-query-mode-sql").ClickAsync(new MouseEventArgs());
        page.WaitForElement("#ms-query-sql");
        await page.Find("#ms-query-sql").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "select id, embedding from items" });
        await page.Find(".ms-query-run").ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-vector-preview").TextContent.Should().Be("[0.0123, -0.0441, 0.2, …]"));
        page.Find("button.ms-vector-chip").TextContent.Should().Be("vector");
        page.TextOfAll(".ms-query-grid-type").Should().Equal("integer", "vector");

        await page.Find("button.ms-vector-chip").ClickAsync(new MouseEventArgs());

        page.Find(".ms-row-text-panel-body").TextContent.Should().Be(Five);
    }

    [Fact]
    public async Task The_panel_alone_marks_a_cut_vector_cell_from_the_cell_s_own_flag()
    {
        FakeQueryService service = new() { SqlResult = VectorResult(cut: true) };
        using StudioComponentContext context = CreateContext(service);

        var panel = context.Render<SqlConsolePanel>();
        panel.Find("#ms-query-sql").Change("select id, embedding from items");
        panel.Find(".ms-query-run").Click();

        panel.WaitForAssertion(() => panel.Find(".ms-vector-preview").TextContent.Should().Be("[0.1, 0.2, 0.3, …]"));

        await panel.Find("button.ms-vector-chip").ClickAsync(new MouseEventArgs());

        panel.Find(".ms-row-json-panel .ms-row-value-note").TextContent.Should().Contain("not offered for copying");
    }

    private static StudioComponentContext CreateContext(FakeQueryService service)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.Options.Capabilities.RunSql = true;
        context.Services.AddFallbackServiceProvider(new QueryServices(service));
        return context;
    }

    /// <summary>The fake, as the Query service and as the SQL console.</summary>
    private sealed class QueryServices(FakeQueryService instance) : IServiceProvider
    {
        public object? GetService(Type requested) =>
            requested == typeof(MartenStudio.Services.Query.IQueryService) || requested == typeof(ISqlConsoleService) ? instance : null;
    }
}
