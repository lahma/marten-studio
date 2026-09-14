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

    // ------------------------------------------------------------------------------------------------
    // CSV formula injection. RFC 4180 says nothing about this, and it is the thing that gets people hurt.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A spreadsheet reads a cell beginning <c>=</c>, <c>@</c>, a tab or a carriage return as a formula,
    /// and <c>=cmd|' /C calc'!A0</c> is the canonical demonstration: a string somebody stored in a document
    /// becomes a program on the machine of whoever opened the export. The apostrophe is what stops it.
    /// </summary>
    [Theory]
    [InlineData("=cmd|' /C calc'!A0")]
    [InlineData("@SUM(1+9)*cmd|' /C calc'!A0")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    [InlineData("=1+1")]
    [InlineData("-2+3")]
    [InlineData("+1-1")]
    [InlineData("-cmd|' /C calc'!A0")]
    public void A_cell_that_a_spreadsheet_would_run_is_prefixed_with_an_apostrophe(string dangerous)
    {
        QueryExport.Neutralize(dangerous).Should().Be("'" + dangerous);
    }

    /// <summary>
    /// The deliberate exception, and the trade-off it buys: a cell that is <em>entirely</em> a plain number
    /// is left alone, so an export of negative numbers is still an export of numbers rather than of text.
    /// Anything else starting with a sign - which is every shape that is a formula rather than a number -
    /// is prefixed.
    /// </summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("-1.5")]
    [InlineData("+3.25")]
    [InlineData("-1.5e-10")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData("Alice")]
    [InlineData("""{"Name":"Alice"}""")]
    public void A_value_that_is_not_a_formula_is_left_exactly_as_it_is(string safe)
    {
        QueryExport.Neutralize(safe).Should().Be(safe);
    }

    /// <summary>
    /// Both paths, because a column name is as attacker-controlled as a value: <c>select 1 as "=cmd…"</c>
    /// is one statement away in the console the very same page runs.
    /// </summary>
    [Fact]
    public void Both_the_header_and_the_cells_are_neutralised()
    {
        SqlResultColumn[] columns = [new("=cmd|' /C calc'!A0", "text"), new("note", "text")];
        IReadOnlyList<IReadOnlyList<SqlCell>> rows =
        [
            [new SqlCell("=1+1", SqlCellKind.Text, false, 4), new SqlCell("-1", SqlCellKind.Number, false, 2)],
        ];

        string[] lines = QueryExport.ToCsv(columns, rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[0].Should().Be("'=cmd|' /C calc'!A0,note");
        lines[1].Should().Be("'=1+1,-1", "the number stays a number");
    }

    /// <summary>
    /// The apostrophe goes <em>inside</em> the quotes, because that is where the spreadsheet looks. Putting
    /// it in front of the opening quote would put it outside the value altogether.
    /// </summary>
    [Fact]
    public void A_dangerous_cell_that_also_needs_quoting_keeps_the_apostrophe_inside_the_quotes()
    {
        IReadOnlyList<IReadOnlyList<SqlCell>> rows =
        [
            [new SqlCell("=HYPERLINK(\"http://x\",\"a,b\")", SqlCellKind.Text, false, 28)],
        ];

        string[] lines = QueryExport
            .ToCsv([new SqlResultColumn("c", "text")], rows)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[1].Should().StartWith("\"'=HYPERLINK");
    }

    /// <summary>The Marten mode's two columns go through the same escape, id included.</summary>
    [Fact]
    public void The_marten_csv_neutralises_the_id_as_well_as_the_document()
    {
        IReadOnlyList<MartenQueryRow> rows = [new("=cmd|' /C calc'!A0", """{"Name":"Alice"}""")];

        string[] lines = QueryExport.ToCsv(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines[1].Should().StartWith("'=cmd|");
        lines[1].Should().Contain("""{""Name"":""Alice""}""", "a JSON object is not formula-shaped");
    }
}
