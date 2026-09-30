using System.Text.Json;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Components.Shared;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The Rows tab's words and hints, without a page: what a byte prefix looks like, when a bigint is a time,
/// how a chip comes out of a filter, the paging note, and a row as JSON for the clipboard.
/// </summary>
public class RowPresentationTests
{
    // ----------------------------------------------------------------------------------------------
    // bytea
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_hex_cell_gives_back_its_bytes_and_ignores_what_follows_them()
    {
        SqlCellFormat.HexPrefix("\\x7b2261223a317d (7 bytes)").Should().Equal("{\"a\":1}"u8.ToArray());
        SqlCellFormat.HexPrefix("\\x1f8b08…  (4000 bytes)").Should().Equal([0x1f, 0x8b, 0x08]);
        SqlCellFormat.HexPrefix("not hex").Should().BeNull();
    }

    [Theory]
    [InlineData("{\"reportId\":1}", "Json")]
    [InlineData("  [1,2,3]", "Json")]
    [InlineData("hello, world\nsecond line", "Text")]
    public void Text_and_json_in_a_bytea_are_told_apart(string content, string expected)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);

        SqlCellFormat.Sniff(bytes, bytes.Length).Shape.ToString().Should().Be(expected);
    }

    [Fact]
    public void Gzip_png_and_anything_else_are_named_by_their_first_bytes_with_the_size()
    {
        SqlCellFormat.Sniff([0x1f, 0x8b, 0x08, 0x00], 3500).Label.Should().Be("gzip · 3.4 KB");
        SqlCellFormat.Sniff([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00], 12_288).Label.Should().Be("PNG image · 12 KB");
        SqlCellFormat.Sniff([0x00, 0x01, 0xff], 3).Shape.Should().Be(BinaryShape.Unknown);
        SqlCellFormat.Sniff("{\"a\":1}"u8, 7).Label.Should().Be("JSON · 7 B");
    }

    [Fact]
    public void A_prefix_cut_inside_a_character_is_still_text()
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("naïve café");

        SqlCellFormat.Sniff(bytes.AsSpan(0, bytes.Length - 1), bytes.Length).Shape.Should().Be(BinaryShape.Text,
            "the server cut the prefix in the middle of the last é");
    }

    // ----------------------------------------------------------------------------------------------
    // The date hint
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("next_fire_time", true)]
    [InlineData("created_at", true)]
    [InlineData("START_DATE", true)]
    [InlineData("retry_count", false)]
    [InlineData("format", false)]
    public void Only_a_name_that_suggests_a_time_is_read_as_one(string column, bool expected)
    {
        SqlCellFormat.NameSuggestsTime(column).Should().Be(expected);
    }

    [Fact]
    public void Every_value_that_is_not_a_sentinel_has_to_be_in_the_same_window()
    {
        string ticks = FakeTableRows.BaseTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

        SqlCellFormat.DateHintFor("next_fire_time", "bigint", [ticks, null, "-1", "0"]).Should().Be(DateHintUnit.Ticks,
            "Quartz writes -1 and 0 for 'none', and a NULL has nothing to say");
        SqlCellFormat.DateHintFor("sent_at", "bigint", ["1790000000000", "1790000360000"]).Should().Be(DateHintUnit.EpochMilliseconds);
        SqlCellFormat.DateHintFor("sent_at", "bigint", ["1790000000", "1790000360"]).Should().Be(DateHintUnit.EpochSeconds);

        SqlCellFormat.DateHintFor("sent_at", "bigint", [ticks, "1790000000000"]).Should().Be(DateHintUnit.None, "two units on one page are no unit");
        SqlCellFormat.DateHintFor("sent_at", "bigint", ["12345"]).Should().Be(DateHintUnit.None, "a small number is not a time");
        SqlCellFormat.DateHintFor("sent_at", "bigint", ["-1", null]).Should().Be(DateHintUnit.None, "nothing but sentinels is nothing to go on");
        SqlCellFormat.DateHintFor("sent_at", "integer", ["1790000000"]).Should().Be(DateHintUnit.None, "only a bigint");
        SqlCellFormat.DateHintFor("retry_count", "bigint", [ticks]).Should().Be(DateHintUnit.None);
    }

    [Fact]
    public void A_value_in_a_window_is_the_instant_it_counts_to()
    {
        SqlCellFormat.Instant((FakeTableRows.BaseTicks + TimeSpan.TicksPerHour).ToString(System.Globalization.CultureInfo.InvariantCulture), DateHintUnit.Ticks)
            .Should().Be(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero));
        SqlCellFormat.Instant("1790000000", DateHintUnit.EpochSeconds).Should().Be(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000));
        SqlCellFormat.Instant("-1", DateHintUnit.Ticks).Should().BeNull();
    }

    // ----------------------------------------------------------------------------------------------
    // The filter
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a = 1 b = 2 c = 3", 6, 5, "a = 1 c = 3")]
    [InlineData("a = 1 b = 2", 0, 5, "b = 2")]
    [InlineData("a = 1 b = \"two words\"", 6, 15, "a = 1")]
    [InlineData("a = 1", 0, 5, null)]
    public void Removing_a_term_cuts_its_span_and_leaves_the_rest_as_typed(string filter, int position, int length, string? expected)
    {
        RowPresentation.RemoveTerm(filter, position, length).Should().Be(expected);
    }

    [Fact]
    public void The_caret_is_under_the_characters_the_error_covers()
    {
        RowPresentation.Caret(3, 2).Should().Be("   ^^");
        RowPresentation.Caret(0, 0).Should().Be("^");
        RowPresentation.CaretText("a\tb").Should().Be("a b", "a tab would push the caret out of line");
    }

    [Fact]
    public void A_value_filter_reads_back_as_the_same_term()
    {
        RowPresentation.FilterFor("state", "WAITING").Should().Be("state = WAITING");
        RowPresentation.FilterFor("state", "two words").Should().Be("state = \"two words\"");
        RowPresentation.FilterFor("state", null).Should().Be("state is:null");
        RowPresentation.KeyFilter([new("a", "1"), new("b", "x y")]).Should().Be("a = 1 b = \"x y\"");
    }

    // ----------------------------------------------------------------------------------------------
    // Keys, the paging note, JSON
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_key_reads_in_key_order_and_a_long_value_is_cut()
    {
        IReadOnlyList<KeyValuePair<string, string>> pairs = RowPresentation.KeyPairs(
            new Dictionary<string, string> { ["b"] = "2", ["a"] = new string('x', 40) },
            new DatabaseRowKey(DatabaseRowKeySource.PrimaryKey, "pk", ["a", "b"]));

        pairs.Select(static x => x.Key).Should().Equal(["a", "b"]);
        RowPresentation.KeyTuple(pairs).Should().Be("(" + new string('x', 23) + "…, 2)");
    }

    [Fact]
    public void The_paging_note_names_each_walk()
    {
        TableRowPage keyset = FakeTableRows.Page();
        string.Join(" · ", RowPresentation.PagingNote(keyset)).Should().Be("~24 rows · keyset on (sched_name, trigger_name, trigger_group) · key order ↑");

        TableRowPage ctid = RowUiData.Keyless();
        string.Join(" · ", RowPresentation.PagingNote(ctid)).Should().Be("~2 rows · keyset on ctid, the row's physical position");

        TableRowPage view = keyset with
        {
            Kind = DatabaseObjectKind.View,
            EstimatedRows = null,
            Paging = new TableRowPaging(TableRowPagingMode.Offset, ["a", "b"], "b", SortDirection.Descending, 50, null),
        };
        string.Join(" · ", RowPresentation.PagingNote(view)).Should().Be("a view: every page runs its query · numbered pages, ordered by (a, b) · b ↓");
    }

    [Fact]
    public void A_row_as_json_keeps_numbers_json_and_nulls_what_they_are()
    {
        string json = RowPresentation.RowJson(FakeTableRows.Columns, FakeTableRows.Row(1).Cells);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("sched_name").GetString().Should().Be(FakeTableRows.Scheduler);
        root.GetProperty("next_fire_time").ValueKind.Should().Be(JsonValueKind.Number);
        root.GetProperty("calendar_name").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("trigger_meta").GetProperty("owner").GetString().Should().Be("ops");
        root.GetProperty("job_data").GetString().Should().StartWith("\\x7b");
    }

    [Fact]
    public void A_json_cell_cut_on_the_server_is_copied_as_the_text_it_is()
    {
        TableRowColumn column = RowUiData.Column("payload", 1, "jsonb");
        string json = RowPresentation.RowJson([column], [new SqlCell("{\"a\":[1,2,3…", SqlCellKind.Json, true, 90_000)]);

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("payload").GetString().Should().Be("{\"a\":[1,2,3…");
    }
}
