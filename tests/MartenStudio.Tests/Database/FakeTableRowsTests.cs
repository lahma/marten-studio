using System.Text;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Database;

/// <summary>
/// The Rows tab's sample data agrees with itself, and with the shapes the real service returns - so a page
/// test built on it cannot pass on data no live page would ever see.
/// </summary>
public class FakeTableRowsTests
{
    [Fact]
    public void Every_row_has_one_cell_per_column_and_the_key_it_is_found_by()
    {
        TableRowPage page = FakeTableRows.Page(8);

        page.Rows.Should().HaveCount(8);
        page.Rows.Should().OnlyContain(row => row.Cells.Count == page.Columns.Count);
        page.Rows.Select(static x => string.Join('|', x.Key!.Values)).Should().OnlyHaveUniqueItems();
        page.Columns.Where(static x => x.KeyPosition is not null).Select(static x => x.Name).Should().Equal(FakeTableRows.Key.Columns);

        FakeTableRows.Detail(3).Cells.Should().Equal(page.Rows[3].Cells);
        FakeTableRows.Detail(3).Key.Should().BeEquivalentTo(page.Rows[3].Key);
    }

    [Fact]
    public void It_has_the_cells_the_grid_draws_differently()
    {
        TableRowPage page = FakeTableRows.Page(8);
        int next = Ordinal(page, "next_fire_time");

        page.Rows.Select(row => row.Cells[next].Kind).Should().Contain(SqlCellKind.Null).And.Contain(SqlCellKind.Number);
        page.Rows[1].Cells[next].Text.Should().Be((FakeTableRows.BaseTicks + TimeSpan.TicksPerHour).ToString(System.Globalization.CultureInfo.InvariantCulture));
        new DateTime(long.Parse(page.Rows[1].Cells[next].Text, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)
            .Should().Be(new DateTime(2026, 9, 29, 1, 0, 0, DateTimeKind.Utc), "Quartz stores .NET ticks");

        page.Rows[0].Cells[Ordinal(page, "job_data")].Kind.Should().Be(SqlCellKind.Binary);
        page.Rows[0].Cells[Ordinal(page, "trigger_meta")].Kind.Should().Be(SqlCellKind.Json);

        TableCellValue data = FakeTableRows.Cell("job_data");
        Encoding.UTF8.GetString(data.Bytes!).Should().StartWith("{\"reportId\"");

        FakeTableRows.Cell("calendar_name", 1).IsNull.Should().BeTrue();
    }

    [Fact]
    public void The_references_have_a_keyed_parent_and_a_count_past_the_cap()
    {
        TableRowReferences references = FakeTableRows.References(2);

        references.Outbound.Should().ContainSingle().Which.TargetsRowKey.Should().BeTrue();
        references.Inbound.Should().Contain(static x => x.More && x.Count == 1000);
    }

    [Fact]
    public async Task The_fake_answers_from_the_sample_and_records_what_it_was_asked()
    {
        var fake = new FakeTableRowService();
        var scope = new MartenStudio.Services.StudioScope("default", string.Empty, null);
        CancellationToken token = TestContext.Current.CancellationToken;

        (await fake.ListRowsAsync(scope, FakeTableRows.Schema, FakeTableRows.Table, new TableRowRequest { Filter = "x = 1" }, token))
            .Rows.Should().HaveCount(5);
        (await fake.GetCellAsync(scope, FakeTableRows.Schema, FakeTableRows.Table, FakeTableRows.KeyOf(0), "trigger_meta", token))
            .Text.Should().Contain("retries");

        fake.Requests.Should().ContainSingle().Which.Request.Filter.Should().Be("x = 1");
        fake.CellsAsked.Should().ContainSingle().Which.Column.Should().Be("trigger_meta");
        fake.Reads.Should().Be(2);

        fake.Failure = new InvalidOperationException("boom");
        await FluentActions.Awaiting(() => fake.CountExactAsync(scope, "s", "t", null, token))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    private static int Ordinal(TableRowPage page, string column) =>
        page.Columns.Select(static (x, i) => (x.Name, i)).Single(x => x.Name == column).i;
}
