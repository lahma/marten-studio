using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// What the Statements page draws of a view (W5-Q1): the top N by execution time or - on Aurora - by storage reads, then sorted
/// by a column; the group view's sums; and a statement not watched over the window ranked after every watched one.
/// </summary>
public class StatementsViewTests
{
    private static StatementRowView Row(long id, string group, double exec, double calls, double? storage = null, double temp = 0, bool watched = true)
    {
        StatementCounts counts = new(calls, exec, calls * 2, calls * 10, calls, temp, 0, storage);
        return new StatementRowView(id, "select " + id, null, group, group, watched ? counts : null, counts, watched ? exec / 1_000 : null, null, null);
    }

    private static StatementsView View(bool storageReads = false) => new()
    {
        StorageReads = storageReads,
        Rows =
        [
            Row(1, "select · public.orders", exec: 300, calls: 10, storage: 5),
            Row(2, "select · public.orders", exec: 200, calls: 1_000, storage: 900, temp: 7),
            Row(3, "insert · public.audit", exec: 100, calls: 50, storage: 50),
            Row(4, "select · other", exec: 900_000, calls: 1, watched: false),
        ],
    };

    [Fact]
    public void The_top_N_are_the_most_execution_time_over_the_window_and_an_unwatched_statement_comes_last()
    {
        View().Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 2, 3, 4);
        View().Top(2, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 2);
    }

    [Theory]
    [InlineData(nameof(StatementSort.Calls), new long[] { 2, 3, 1 })]
    [InlineData(nameof(StatementSort.Mean), new long[] { 1, 3, 2 })]
    [InlineData(nameof(StatementSort.Temp), new long[] { 2, 1, 3 })]
    public void The_top_statements_sort_by_a_column_among_themselves(string sort, long[] order)
    {
        View().Top(3, StatementRanking.TotalTime, Enum.Parse<StatementSort>(sort)).Select(static x => x.QueryId).Should().Equal(order);
    }

    [Fact]
    public void On_Aurora_the_top_N_can_be_the_most_storage_reads_and_elsewhere_that_ranking_is_total_time()
    {
        View(storageReads: true).Top(2, StatementRanking.StorageReads, StatementSort.StorageReads).Select(static x => x.QueryId).Should().Equal(2, 3);
        View(storageReads: false).Top(2, StatementRanking.StorageReads, StatementSort.TotalTime).Select(static x => x.QueryId)
            .Should().Equal([1, 2], "a ranking by storage reads is offered only where they are read");
    }

    [Fact]
    public void The_group_view_sums_the_top_statements_by_group()
    {
        IReadOnlyList<StatementGroupRowView> groups = View().Groups(10, StatementRanking.TotalTime);

        groups.Select(static x => x.Label).Should().Equal(["select · public.orders", "insert · public.audit"], "an unwatched statement has no window to sum");

        StatementGroupRowView orders = groups[0];
        orders.Statements.Should().Be(2);
        orders.Counts.ExecMs.Should().Be(500);
        orders.Counts.Calls.Should().Be(1_010);
        orders.Counts.TempBlocksWritten.Should().Be(7);
        orders.Share.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void The_page_offers_the_top_10_to_100()
    {
        StatementsView.TopChoices.Should().Equal(10, 25, 50, 100);
    }
}
