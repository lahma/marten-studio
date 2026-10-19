using Bunit;

using MartenStudio.Components.Json;
using MartenStudio.Components.Pages.Database;
using MartenStudio.Components.Shared;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Schema;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;

namespace MartenStudio.Tests.Database;

/// <summary>
/// PGV in the viewers and on the schema screens: a pgvector value collapsed to its type and first components in the
/// row grid and row detail, its expand and its copy; the badge on a vector column; an HNSW or IVFFlat index's operator
/// classes and parameters; and an embedding inside a document's JSON drawn collapsed.
/// </summary>
public class PgVectorViewerTests
{
    private static readonly PgVectorColumn Vector5 = PgVector.Column("vector", "vector", 5)!;

    private const string Five = "[0.0123,-0.0441,0.2,0.7,0.5]";

    [Fact]
    public void A_grid_cell_of_a_vector_column_is_its_type_and_first_three_components_and_its_chip_opens_it()
    {
        using var context = new StudioComponentContext();
        int opened = 0;

        var cell = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Text, Five)
            .Add(x => x.Kind, "text")
            .Add(x => x.Type, "public.vector(5)")
            .Add(x => x.Rich, true)
            .Add(x => x.FullLength, Five.Length)
            .Add(x => x.Vector, Vector5)
            .Add(x => x.OnOpen, () => opened++));

        cell.Find("button.ms-vector-chip").TextContent.Should().Be("vector(5)");
        cell.Find(".ms-vector-preview").TextContent.Should().Be("[0.0123, -0.0441, 0.2, …]");
        cell.Find(".ms-vector-preview").GetAttribute("title").Should().Be("vector(5): 5 components, the first 3 shown.");
        cell.Markup.Should().NotContain("0.7", "the fourth component is not drawn in the grid");

        cell.Find("button.ms-vector-chip").Click();
        opened.Should().Be(1);
    }

    [Fact]
    public void A_vector_cell_the_server_cut_is_collapsed_from_what_it_sent_and_says_its_dimensions()
    {
        using var context = new StudioComponentContext();
        PgVectorColumn column = PgVector.Column("vector", "vector", 1536)!;

        var cell = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Text, "[0.1,0.2,0.3,0.4,0.5")
            .Add(x => x.Kind, "text")
            .Add(x => x.Rich, true)
            .Add(x => x.IsTruncated, true)
            .Add(x => x.FullLength, 18_000)
            .Add(x => x.Vector, column));

        cell.Find(".ms-vector-preview").TextContent.Should().Be("[0.1, 0.2, 0.3, …]");
        cell.Find(".ms-vector-preview").GetAttribute("title").Should().Contain("1,536 dimensions");
    }

    [Fact]
    public void A_vector_cell_in_the_SQL_console_and_a_NULL_one_are_drawn_as_they_always_were()
    {
        using var context = new StudioComponentContext();

        var console = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Text, Five)
            .Add(x => x.Kind, "text")
            .Add(x => x.Vector, Vector5));

        console.FindAll(".ms-vector-chip").Should().BeEmpty("the console's grid is not Rich");
        console.Find(".ms-query-cell-text").TextContent.Should().Be(Five);

        var none = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Kind, "null")
            .Add(x => x.Rich, true)
            .Add(x => x.Vector, Vector5));

        none.Find(".ms-query-null").TextContent.Should().Be("NULL");
    }

    [Fact]
    public void Text_that_is_not_pgvectors_output_is_shown_as_it_is()
    {
        using var context = new StudioComponentContext();

        var cell = context.Render<SqlCellView>(parameters => parameters
            .Add(x => x.Text, "(cannot read public.vector)")
            .Add(x => x.Kind, "text")
            .Add(x => x.Rich, true)
            .Add(x => x.Vector, Vector5));

        cell.FindAll(".ms-vector-chip").Should().BeEmpty();
        cell.Find(".ms-query-cell-text").TextContent.Should().Be("(cannot read public.vector)");
    }

    /// <summary>A page of one row: its key, and a vector(5) cell as the server sent it.</summary>
    private static TableRowPage VectorPage(MartenStudio.Internal.Sql.SqlCell cell) => new()
    {
        Schema = "legacy",
        Name = "items",
        Kind = DatabaseObjectKind.Table,
        RowKey = new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "items_pkey", ["id"]),
        Columns =
        [
            new TableRowColumn("id", 1, "integer", false, true, true, MartenStudio.Internal.Sql.SqlCellKind.Number, 1, [],
                MartenStudio.Services.Query.IndexVerdictLevel.Green, string.Empty, null),
            new TableRowColumn("embedding", 2, "public.vector(5)", true, false, true, MartenStudio.Internal.Sql.SqlCellKind.Text, null, [],
                MartenStudio.Services.Query.IndexVerdictLevel.Green, string.Empty, null, Vector5),
        ],
        Rows =
        [
            new TableRow(
                new Dictionary<string, string> { ["id"] = "1" },
                null,
                [new MartenStudio.Internal.Sql.SqlCell("1", MartenStudio.Internal.Sql.SqlCellKind.Number, false, 1), cell]),
        ],
    };

    private static IRenderedComponent<RowGrid> RenderGrid(StudioComponentContext context, MartenStudio.Internal.Sql.SqlCell cell) =>
        context.Render<RowGrid>(parameters => parameters
            .Add(x => x.Model, (object?) VectorPage(cell))
            .Add(x => x.Schema, "legacy")
            .Add(x => x.Name, "items"));

    [Fact]
    public async Task The_grid_panel_shows_a_whole_vector_with_Copy()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();

        var grid = RenderGrid(context, new MartenStudio.Internal.Sql.SqlCell(Five, MartenStudio.Internal.Sql.SqlCellKind.Text, false, Five.Length));

        grid.Find("button.ms-vector-chip").Click();

        grid.Find(".ms-row-text-panel-body").TextContent.Should().Be(Five);
        grid.Find(".ms-vector-panel-actions .ms-copy-button").GetAttribute("aria-label").Should().Be("Copy the whole value of embedding");
        context.TableRows.CellsAsked.Should().BeEmpty("a value the page has whole is not read again");
    }

    [Fact]
    public async Task The_grid_panel_says_a_value_cut_at_the_inline_cap_is_cut_and_offers_no_Copy()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();

        context.TableRows.Cell = new TableCellValue { Found = true, Text = "[0.1,0.2,0.3,0.4,0.5", Truncated = true, FullLength = 9_000_000, Cap = 1024 };

        var grid = RenderGrid(context, new MartenStudio.Internal.Sql.SqlCell("[0.1,0.2,0.3", MartenStudio.Internal.Sql.SqlCellKind.Text, true, 9_000_000));

        grid.Find("button.ms-vector-chip").Click();

        grid.WaitForAssertion(() => grid.Find(".ms-row-text-panel-body").TextContent.Should().Be("[0.1,0.2,0.3,0.4,0.5"));
        grid.Find(".ms-row-json-panel .ms-row-value-note").TextContent.Should().Contain("not offered for copying");
        grid.FindAll(".ms-row-json-panel .ms-copy-button").Should().BeEmpty();
        context.TableRows.CellsAsked.Should().ContainSingle().Which.Column.Should().Be("embedding");
    }

    [Fact]
    public async Task A_NULL_vector_has_no_chip_and_a_value_that_became_NULL_offers_no_Copy()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();

        RenderGrid(context, MartenStudio.Internal.Sql.SqlCell.Null).FindAll("button.ms-vector-chip").Should().BeEmpty();

        context.TableRows.Cell = new TableCellValue { Found = true, IsNull = true };

        var grid = RenderGrid(context, new MartenStudio.Internal.Sql.SqlCell("[0.1,0.2,0.3", MartenStudio.Internal.Sql.SqlCellKind.Text, true, 4_000));

        grid.Find("button.ms-vector-chip").Click();

        grid.WaitForAssertion(() => grid.Find(".ms-row-text-panel-body").TextContent.Should().Be("NULL"));
        grid.FindAll(".ms-row-json-panel .ms-copy-button").Should().BeEmpty("NULL is not a value to copy");
    }

    [Fact]
    public void Row_detail_collapses_a_whole_vector_expands_it_in_place_and_copies_all_of_it()
    {
        using var context = new StudioComponentContext();

        var value = context.Render<RowValue>(parameters => parameters
            .Add(x => x.Schema, "legacy")
            .Add(x => x.Name, "items")
            .Add(x => x.Column, "embedding")
            .Add(x => x.Kind, "text")
            .Add(x => x.Text, Five)
            .Add(x => x.FullLength, Five.Length)
            .Add(x => x.Vector, Vector5));

        value.Find(".ms-vector-type").TextContent.Should().Be("vector(5)");
        value.Find(".ms-vector-preview").TextContent.Should().Be("[0.0123, -0.0441, 0.2, …]");
        value.Find(".ms-copy-button").GetAttribute("aria-label").Should().Be("Copy the whole value of embedding");

        var toggle = value.Find("button.ms-vector-toggle");
        toggle.TextContent.Should().Be("Expand");
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        toggle.Click();

        value.Find(".ms-vector-whole").TextContent.Should().Be(Five);
        value.Find("button.ms-vector-toggle").TextContent.Should().Be("Collapse");
        value.Find("button.ms-vector-toggle").GetAttribute("aria-expanded").Should().Be("true");
        value.FindAll(".ms-vector-preview").Should().BeEmpty();

        value.Find(".ms-copy-button").Click();
        context.JSInterop.Invocations.Should().Contain(static x => x.Arguments.Contains(Five),
            "the copy puts the whole value on the clipboard, not the collapsed line");
    }

    [Fact]
    public void A_short_vector_is_shown_whole_with_nothing_to_expand()
    {
        using var context = new StudioComponentContext();

        var value = context.Render<RowValue>(parameters => parameters
            .Add(x => x.Column, "e")
            .Add(x => x.Kind, "text")
            .Add(x => x.Text, "[1,2,3]")
            .Add(x => x.Vector, PgVector.Column("vector", "vector", 3)!));

        value.Find(".ms-vector-preview").TextContent.Should().Be("[1, 2, 3]");
        value.FindAll("button.ms-vector-toggle").Should().BeEmpty();
        value.FindAll(".ms-copy-button").Should().ContainSingle();
    }

    [Fact]
    public async Task A_vector_the_page_cut_is_read_again_whole_before_it_is_expanded_or_offered_for_copying()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();

        const string whole = "[0.1,0.2,0.3,0.4,0.5,0.6,0.7]";
        context.TableRows.Cell = new TableCellValue { Found = true, Text = whole, FullLength = whole.Length, Cap = 1024 * 1024 };

        var value = context.Render<RowValue>(parameters => parameters
            .Add(x => x.Schema, "legacy")
            .Add(x => x.Name, "items")
            .Add(x => x.CellKey, new Dictionary<string, string> { ["id"] = "1" })
            .Add(x => x.Column, "embedding")
            .Add(x => x.Kind, "text")
            .Add(x => x.Text, "[0.1,0.2,0.3,0.4")
            .Add(x => x.IsTruncated, true)
            .Add(x => x.FullLength, whole.Length)
            .Add(x => x.Vector, PgVector.Column("vector", "vector", 7)!));

        value.Find(".ms-vector-preview").TextContent.Should().Be("[0.1, 0.2, 0.3, …]");
        value.FindAll(".ms-copy-button").Should().BeEmpty("what is here is not the value");

        value.Find("button.ms-vector-toggle").TextContent.Trim().Should().StartWith("Expand (");
        await value.Find("button.ms-vector-toggle").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        value.WaitForAssertion(() => value.Find(".ms-vector-whole").TextContent.Should().Be(whole));
        value.Find(".ms-copy-button").Should().NotBeNull();
        context.TableRows.CellsAsked.Should().ContainSingle().Which.Column.Should().Be("embedding");
    }

    [Fact]
    public async Task A_vector_cut_even_at_the_inline_cap_is_never_offered_for_copying()
    {
        using var context = new StudioComponentContext();
        await context.ReadyAsync();

        context.TableRows.Cell = new TableCellValue { Found = true, Text = "[0.1,0.2,0.3,0.4,0.5", Truncated = true, FullLength = 9_000_000, Cap = 1024 };

        var value = context.Render<RowValue>(parameters => parameters
            .Add(x => x.CellKey, new Dictionary<string, string> { ["id"] = "1" })
            .Add(x => x.Column, "embedding")
            .Add(x => x.Kind, "text")
            .Add(x => x.Text, "[0.1,0.2,0.3,0.4")
            .Add(x => x.IsTruncated, true)
            .Add(x => x.FullLength, 9_000_000)
            .Add(x => x.Vector, PgVector.Column("vector", "vector", 16000)!));

        await value.Find("button.ms-vector-toggle").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        value.WaitForAssertion(() => value.Find(".ms-vector-whole").TextContent.Should().Be("[0.1,0.2,0.3,0.4,0.5"));
        value.FindAll(".ms-copy-button").Should().BeEmpty();
        value.Find(".ms-row-value-note").TextContent.Should().Contain("MaxInlineDocumentBytes");
    }

    [Fact]
    public void An_HNSW_index_says_its_operator_class_its_distance_and_which_parameters_are_pgvectors_defaults()
    {
        using var context = new StudioComponentContext();
        PgVectorIndex index = PgVector.Index(true, "hnsw", ["embedding"], ["vector_cosine_ops"], ["m=24"])!;

        var summary = context.Render<VectorIndexSummary>(parameters => parameters.Add(x => x.Model, index));

        summary.Find(".ms-vector-index-method").TextContent.Should().Be("pgvector HNSW");
        summary.Find(".ms-vector-index-key").TextContent.Should().Be("embedding");
        summary.Find(".ms-vector-index-opclass").TextContent.Should().Be("vector_cosine_ops");
        summary.Find(".ms-vector-index-distance").TextContent.Should().Be("cosine distance, <=>");

        var parameters = summary.FindAll(".ms-vector-param");
        parameters.Select(static x => x.TextContent).Should().Equal("m = 24", "ef_construction = 64 (default)");
        parameters[1].GetAttribute("data-default").Should().Be("true");
        parameters[1].GetAttribute("title").Should().Contain("pgvector's default applies (pgvector 0.8.1)");
        parameters[0].GetAttribute("title").Should().Contain("Set on the index");
    }

    [Fact]
    public void A_vector_column_carries_a_badge_with_its_dimensions_on_the_Columns_tab()
    {
        using var context = new StudioComponentContext();

        DatabaseObjectDetail detail = new(
            null,
            [
                new DatabaseColumnInfo("id", 1, "integer", false, null, DatabaseIdentityKind.None, false, true, null, true),
                new DatabaseColumnInfo("embedding", 2, "public.vector(3)", true, null, DatabaseIdentityKind.None, false, false, null, true,
                    PgVector.Column("vector", "vector", 3)),
                new DatabaseColumnInfo("summary", 3, "‹withheld›.halfvec", true, null, DatabaseIdentityKind.None, false, false, null, true,
                    PgVector.Column("vector", "halfvec", -1)),
            ],
            null, [], [], [], [], [], [], DatabaseAccessState.Unknown, DatabaseRefusal.None, null);

        var tab = context.Render<ObjectColumnsTab>(parameters => parameters.Add(x => x.Detail, detail));

        var badges = tab.FindAll(".ms-vector-badge");
        badges.Select(static x => x.TextContent).Should().Equal("pgvector · 3 dimensions", "pgvector · any length");
        badges[0].GetAttribute("title").Should().Contain("no value is read");
        tab.FindAll(".ms-db-cell-type")[2].TextContent.Should().Contain("‹withheld›.halfvec", "the type is still printed - and masked - as Postgres spells it");
    }

    [Fact]
    public void The_Keys_tab_draws_an_IVFFlat_index_with_lists_and_leaves_a_btree_alone()
    {
        using var context = new StudioComponentContext();

        DatabaseObjectDetail detail = new(
            null,
            [],
            null,
            [],
            [
                new DatabaseIndexInfo("items_pkey", "CREATE UNIQUE INDEX items_pkey ON legacy.items USING btree (id)", true, true, true, false, ["id"]),
                new DatabaseIndexInfo("items_ivf", "CREATE INDEX items_ivf ON legacy.items USING ivfflat (embedding public.vector_l2_ops)", false, false, true, false, ["embedding"],
                    "ivfflat", PgVector.Index(true, "ivfflat", ["embedding"], ["vector_l2_ops"], null)),
            ],
            [], [], [], [], DatabaseAccessState.Unknown, DatabaseRefusal.None, null);

        var tab = context.Render<ObjectKeysTab>(parameters => parameters.Add(x => x.Detail, detail));

        tab.FindAll(".ms-vector-index").Should().ContainSingle();
        tab.Find(".ms-vector-index-method").TextContent.Should().Be("pgvector IVFFlat");
        tab.Find(".ms-vector-param").TextContent.Should().Be("lists = 100 (default)");
        tab.Find(".ms-vector-index-distance").TextContent.Should().Be("L2 distance, <->");
    }

    [Fact]
    public void Schema_Tables_names_each_vector_column_with_its_type_and_Indexes_describes_the_HNSW_index()
    {
        var fake = new FakeSchemaDataService();
        using var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");

        DatabaseObjectOwnership other = new(DatabaseObjectOwner.Other, null, null);

        fake.Tables = new SchemaTables(
            [
                new TableStats("studio", "embeddings", 81920, 8192, 65536, 3, 3, 0, 1, 1, null, null, null, null, other)
                {
                    VectorColumns =
                    [
                        new TableVectorColumn("embedding", PgVector.Column("vector", "vector", 3)!),
                        new TableVectorColumn("summary", PgVector.Column("vector", "halfvec", 3)!),
                    ],
                },
            ],
            ["studio"],
            1_000_000,
            null);

        fake.Indexes = new SchemaIndexes(
            [
                new IndexInfo("studio", "embeddings", "embeddings_hnsw", "CREATE INDEX embeddings_hnsw ON studio.embeddings USING hnsw (embedding public.vector_cosine_ops) WITH (m='16', ef_construction='64')",
                    8192, 0, 0, false, false, false, null, null, OnMartenTable: false)
                {
                    AccessMethod = "hnsw",
                    Vector = PgVector.Index(true, "hnsw", ["embedding"], ["vector_cosine_ops"], ["m=16", "ef_construction=64"]),
                },
            ],
            [],
            [],
            null);

        context.Navigate("/marten/schema?tab=tables");
        var tables = context.Render<SchemaPage>();

        tables.FindAll(".ms-schema-vectors .ms-vector-badge").Select(static x => x.TextContent)
            .Should().Equal("embedding vector(3)", "summary halfvec(3)");

        context.Navigate("/marten/schema?tab=indexes");
        var indexes = context.Render<SchemaPage>();

        indexes.Find(".ms-vector-index-method").TextContent.Should().Be("pgvector HNSW");
        indexes.FindAll(".ms-vector-param").Select(static x => x.TextContent).Should().Equal("m = 16", "ef_construction = 64");
    }

    /// <summary>130 numbers, 0.1 to 13 - past the 128 an embedding has at the least.</summary>
    private const int Dimensions = 130;

    /// <summary>A document with an embedding, three tags and 130 whole-number ids beside it.</summary>
    private static string EmbeddingDocument(int dimensions = Dimensions)
    {
        string embedding = string.Join(",", Enumerable.Range(1, dimensions).Select(static i => (i / 10.0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string ids = string.Join(",", Enumerable.Range(1, Dimensions));
        return "{\"name\":\"widget\",\"tags\":[1,2,3],\"ids\":[" + ids + "],\"embedding\":[" + embedding + "]}";
    }

    [Fact]
    public void An_embedding_inside_a_document_is_drawn_collapsed_and_opens_on_its_toggle()
    {
        using var context = new StudioComponentContext();

        var view = context.Render<JsonView>(parameters => parameters.Add(x => x.Json, EmbeddingDocument()).Add(x => x.ShowToolbar, false));

        var numbers = view.Find(".ms-json-numbers");
        numbers.QuerySelector(".ms-json-numbers-preview")!.TextContent.Should().Be("[0.1, 0.2, 0.3, …]");
        numbers.QuerySelector(".ms-json-count")!.TextContent.Should().Be("130 numbers");

        view.FindAll(".ms-json-numbers").Should().ContainSingle(
            "three numbers are not an embedding, and 130 whole numbers are ids - both open as they always did");
        view.FindAll("[role=treeitem]").Count.Should().Be(1 + 4 + 3 + Dimensions,
            "the root, its four members, the tags and the ids - the embedding's 130 are not rows");

        var row = view.FindAll("[role=treeitem]").Single(static x => x.QuerySelector(".ms-json-numbers") is not null);
        row.QuerySelector("button.ms-json-toggle")!.Click();

        view.FindAll(".ms-json-numbers").Should().BeEmpty();
        view.FindAll("[role=treeitem]").Count.Should().Be(1 + 4 + 3 + Dimensions + Dimensions);
    }

    [Fact]
    public void An_array_of_fewer_than_128_numbers_is_not_taken_for_an_embedding()
    {
        using var context = new StudioComponentContext();

        var view = context.Render<JsonView>(parameters => parameters
            .Add(x => x.Json, EmbeddingDocument(127))
            .Add(x => x.ShowToolbar, false)
            .Add(x => x.AutoExpandNodeBudget, 10_000));

        view.FindAll(".ms-json-numbers").Should().BeEmpty("a list of measurements or prices is read element by element");
        view.FindAll("[role=treeitem]").Count.Should().Be(1 + 4 + 3 + Dimensions + 127);
    }

    [Fact]
    public void The_threshold_is_the_viewers_own_option_and_zero_turns_the_collapse_off()
    {
        using var context = new StudioComponentContext();
        const string json = "{\"position\":[0.5,1.5,2.5,3.5,4.5]}";

        var low = context.Render<JsonView>(parameters => parameters
            .Add(x => x.Json, json)
            .Add(x => x.ShowToolbar, false)
            .Add(x => x.CollapsedNumberArray, 5));

        low.Find(".ms-json-numbers-preview").TextContent.Should().Be("[0.5, 1.5, 2.5, …]");

        var off = context.Render<JsonView>(parameters => parameters
            .Add(x => x.Json, EmbeddingDocument())
            .Add(x => x.ShowToolbar, false)
            .Add(x => x.CollapsedNumberArray, 0));

        off.FindAll(".ms-json-numbers").Should().BeEmpty();
    }

    [Fact]
    public void Expand_all_still_opens_a_collapsed_embedding_to_its_last_element()
    {
        using var context = new StudioComponentContext();

        var view = context.Render<JsonView>(parameters => parameters.Add(x => x.Json, EmbeddingDocument()));

        view.FindAll(".ms-json-numbers").Should().ContainSingle();

        view.FindAll("button").First(static x => x.TextContent.Trim() == "Expand all").Click();

        view.FindAll(".ms-json-numbers").Should().BeEmpty();
        view.FindAll("[role=treeitem]").Count.Should().Be(1 + 4 + 3 + Dimensions + Dimensions);
        view.FindAll("[role=treeitem] .ms-json-number").Select(static x => x.TextContent).Should().Contain("12.9", "the 129th component, which no id spells");
    }

    [Fact]
    public void A_search_reaches_inside_a_collapsed_embedding_and_opens_the_way_to_its_match()
    {
        using var context = new StudioComponentContext();

        var view = context.Render<JsonView>(parameters => parameters
            .Add(x => x.Json, EmbeddingDocument())
            .Add(x => x.SearchDebounceMilliseconds, 0));

        view.FindAll("mark.ms-json-hit").Should().BeEmpty();

        view.Find(".ms-json-search-input").Input("12.7");

        view.WaitForAssertion(() => view.Find(".ms-json-search-position").TextContent.Trim().Should().Be("1 / 1"));
        view.FindAll("mark.ms-json-hit").Select(static x => x.TextContent).Should().Equal("12.7");
        view.FindAll(".ms-json-numbers").Should().BeEmpty("the search opened the embedding to show its match");
    }
}
