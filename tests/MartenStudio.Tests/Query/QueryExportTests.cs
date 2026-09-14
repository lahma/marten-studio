using System.Text.Json;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// Exporting the page of results that is on screen, and the SQL-console cell mapping that goes with it.
/// </summary>
public class QueryExportTests
{
    private static readonly SqlResultColumn[] Columns =
    [
        new("id", "int4"),
        new("doc", "jsonb"),
        new("note", "text"),
    ];

    private static IReadOnlyList<IReadOnlyList<SqlCell>> Rows() =>
    [
        [
            new SqlCell("1", SqlCellKind.Number, false, 1),
            new SqlCell("""{"name":"Alice"}""", SqlCellKind.Json, false, 16),
            SqlCell.Null,
        ],
        [
            new SqlCell("2", SqlCellKind.Number, false, 1),
            new SqlCell("""{"name":"Bob"}""", SqlCellKind.Json, false, 14),
            new SqlCell(string.Empty, SqlCellKind.Text, false, 0),
        ],
    ];

    /// <summary>
    /// NULL and the empty string stay different things, which is the distinction the grid exists to draw.
    /// </summary>
    [Fact]
    public void The_json_export_writes_null_as_null_and_the_empty_string_as_the_empty_string()
    {
        using var document = JsonDocument.Parse(QueryExport.ToJson(Columns, Rows()));

        JsonElement first = document.RootElement[0];
        JsonElement second = document.RootElement[1];

        first.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
        second.GetProperty("note").GetString().Should().BeEmpty();
    }

    [Fact]
    public void A_json_cell_is_exported_as_json_rather_than_as_a_quoted_string()
    {
        using var document = JsonDocument.Parse(QueryExport.ToJson(Columns, Rows()));

        JsonElement doc = document.RootElement[0].GetProperty("doc");

        doc.ValueKind.Should().Be(JsonValueKind.Object);
        doc.GetProperty("name").GetString().Should().Be("Alice");
    }

    [Fact]
    public void A_truncated_json_cell_that_will_not_parse_is_exported_as_a_string()
    {
        IReadOnlyList<IReadOnlyList<SqlCell>> rows = [[new SqlCell("{\"name\":\"Ali…", SqlCellKind.Json, true, 200)]];

        using var document = JsonDocument.Parse(QueryExport.ToJson([Columns[1]], rows));

        document.RootElement[0].GetProperty("doc").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void The_csv_has_a_header_row_and_quotes_only_what_it_has_to()
    {
        string csv = QueryExport.ToCsv(Columns, Rows());
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Be("id,doc,note");
        lines[1].Should().Be("1,\"{\"\"name\"\":\"\"Alice\"\"}\",");
        lines[2].Should().Be("2,\"{\"\"name\"\":\"\"Bob\"\"}\",");
    }

    [Fact]
    public void The_marten_rows_export_carries_the_id_and_the_document_as_marten_serialized_it()
    {
        IReadOnlyList<MartenQueryRow> rows =
        [
            new("42", """{"Name":"Alice"}"""),
            new("43", """{"Name":"Bob, of course"}"""),
        ];

        using var document = JsonDocument.Parse(QueryExport.ToJson(rows));

        document.RootElement[0].GetProperty("id").GetString().Should().Be("42");
        document.RootElement[0].GetProperty("document").GetProperty("Name").GetString().Should().Be("Alice");

        string[] csv = QueryExport.ToCsv(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        csv[0].Should().Be("id,document");
        csv[2].Should().Contain("\"\"Bob, of course\"\"", "a comma inside a field means the field is quoted");
    }
}
