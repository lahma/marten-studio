using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Statements;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The Statements page counted from a mark (1.2-H-A, X6): each statement's change since the mark and its share of every
/// statement's, a falling counter meaning its entry began again, PostgreSQL 17's own <c>stats_since</c> telling a new entry from
/// one the mark saw, and the two ways a mark stops holding.
/// </summary>
public class StatementsSinceMarkTests
{
    private static readonly DateTimeOffset MarkAt = Now.AddMinutes(-10);

    private static StatementCounts Counts(double calls, double exec) => new(calls, exec, calls * 2, calls * 10, calls, 0, 0, null);

    private static StatementRowView Row(long id, double calls, double exec, DateTimeOffset? began = null) =>
        new(id, "select " + id, null, "select · public.orders", "select · public.orders", Counts(calls, exec), Counts(calls, exec), 0.123, null, null)
        {
            CountersSince = began ?? Now.AddDays(-1),
        };

    private static StatementsView View(DateTimeOffset measured, double all, params StatementRowView[] rows) => new()
    {
        State = StatementsState.Read,
        MeasuredAt = measured,
        AllExecMsSinceBegan = all,
        TotalExecMs = all,
        Watched = true,
        WindowText = "the advisor's window",
        Basis = "the advisor's basis",
        Rows = rows,
    };

    private static StatementsMark MarkOf(params StatementRowView[] rows) =>
        StatementsSinceMark.Take(View(MarkAt, 1_000, rows))!;

    private static StatisticsWindow Window(DateTimeOffset since, StatisticsResetReason reason) => new()
    {
        MeasuredAt = Now,
        Since = since,
        Basis = StatisticsBasis.ServerReset,
        Reason = reason,
        SharedView = "pg_stat_checkpointer",
    };

    [Fact]
    public void Each_statement_shows_its_change_since_the_mark_and_its_share_of_every_statements()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100), Row(2, calls: 5, exec: 300));

        (StatementsView counted, string? invalid) = StatementsSinceMark.Apply(
            View(Now, 1_400, Row(1, calls: 30, exec: 300), Row(2, calls: 5, exec: 300)),
            mark);

        invalid.Should().BeNull();
        counted.Watched.Should().BeTrue();
        counted.TotalExecMs.Should().Be(400, "every statement's execution time now less the mark's");

        StatementRowView first = counted.Rows.Single(static x => x.QueryId == 1);
        first.Window!.Calls.Should().Be(20);
        first.Window.ExecMs.Should().Be(200);
        first.Window.Rows.Should().Be(40);
        first.Share.Should().BeApproximately(0.5, 1e-9);
        first.MarkNote.Should().BeNull();

        StatementRowView quiet = counted.Rows.Single(static x => x.QueryId == 2);
        quiet.Window!.Calls.Should().Be(0);
        quiet.Share.Should().Be(0);

        counted.WindowText.Should().StartWith("Since your mark at " + TableTextOf(MarkAt)).And.Contain("10 min");
        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 2);
    }

    private static string TableTextOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

    [Fact]
    public void A_statement_whose_counters_fell_has_no_change_and_ranks_last()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 100, exec: 500), Row(2, calls: 5, exec: 50));

        (StatementsView counted, string? invalid) = StatementsSinceMark.Apply(
            View(Now, 1_100, Row(1, calls: 3, exec: 20), Row(2, calls: 6, exec: 80)),
            mark);

        invalid.Should().BeNull();

        StatementRowView evicted = counted.Rows.Single(static x => x.QueryId == 1);
        evicted.Window.Should().BeNull("an entry evicted and begun again has no change to show");
        evicted.Share.Should().BeNull();
        evicted.MarkNote.Should().StartWith("Reset since your mark");

        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(2, 1);
    }

    [Fact]
    public void On_PostgreSQL_17_an_entry_that_began_after_the_mark_counts_everything_since_then()
    {
        DateTimeOffset began = MarkAt.AddMinutes(3);
        StatementsMark mark = MarkOf(Row(1, calls: 100, exec: 500));

        (StatementsView counted, string? invalid) = StatementsSinceMark.Apply(
            View(Now, 1_200, Row(1, calls: 4, exec: 40, began: began), Row(2, calls: 7, exec: 70, began: began.AddMinutes(1))),
            mark);

        invalid.Should().BeNull();

        StatementRowView evictedAndSeenAgain = counted.Rows.Single(static x => x.QueryId == 1);
        evictedAndSeenAgain.Window!.Calls.Should().Be(4, "its counters began after the mark, so all of them are since");
        evictedAndSeenAgain.WatchedFrom.Should().Be(began);

        StatementRowView brandNew = counted.Rows.Single(static x => x.QueryId == 2);
        brandNew.Window!.Calls.Should().Be(7);
        brandNew.WatchedFrom.Should().Be(began.AddMinutes(1));
        brandNew.MarkNote.Should().BeNull();
    }

    [Fact]
    public void Before_PostgreSQL_17_a_statement_the_mark_lacked_has_no_known_change_and_ranks_last()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));

        (StatementsView counted, _) = StatementsSinceMark.Apply(
            View(Now, 1_300, Row(1, calls: 20, exec: 150), Row(2, calls: 99, exec: 900)),
            mark);

        StatementRowView unknown = counted.Rows.Single(static x => x.QueryId == 2);
        unknown.Window.Should().BeNull("its counters before the mark are unknown, and a guess would be a made-up number");
        unknown.MarkNote.Should().Contain("Not among the statements kept at your mark");
        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 2);
    }

    [Fact]
    public void A_reset_of_pg_stat_statements_after_the_mark_ends_it_and_the_view_is_left_as_it_was()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView now = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with { StatsReset = MarkAt.AddMinutes(2) };

        (StatementsView view, string? invalid) = StatementsSinceMark.Apply(now, mark);

        invalid.Should().StartWith("pg_stat_statements was reset at ");
        view.Should().BeSameAs(now, "a mark that no longer holds is not applied");
        view.WindowText.Should().Be("the advisor's window");

        (_, string? before) = StatementsSinceMark.Apply(now with { StatsReset = MarkAt.AddMinutes(-2) }, mark);
        before.Should().BeNull("a reset before the mark is in the mark's own counters");
    }

    [Theory]
    [InlineData(nameof(StatisticsResetReason.ServerStarted), true)]
    [InlineData(nameof(StatisticsResetReason.Crash), true)]
    [InlineData(nameof(StatisticsResetReason.Restore), true)]
    [InlineData(nameof(StatisticsResetReason.Failover), true)]
    [InlineData(nameof(StatisticsResetReason.OtherInstance), true)]
    [InlineData(nameof(StatisticsResetReason.ManualReset), false)]
    public void A_restart_that_began_the_counters_after_the_mark_ends_it_and_a_manual_reset_does_not(string reasonName, bool ends)
    {
        StatisticsResetReason reason = Enum.Parse<StatisticsResetReason>(reasonName);

        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView now = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with { Statistics = Window(MarkAt.AddMinutes(1), reason) };

        (_, string? invalid) = StatementsSinceMark.Apply(now, mark);

        if (ends)
        {
            invalid.Should().StartWith("the statistics began again at ").And.Contain("(");
        }
        else
        {
            invalid.Should().BeNull("pg_stat_reset() does not reset pg_stat_statements");
        }

        (_, string? earlier) = StatementsSinceMark.Apply(now with { Statistics = Window(MarkAt.AddMinutes(-1), reason) }, mark);
        earlier.Should().BeNull("a window that began before the mark is the mark's own");
    }

    [Fact]
    public void A_view_read_at_the_marks_own_moment_says_the_counters_are_still_the_marks()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));

        (StatementsView same, _) = StatementsSinceMark.Apply(View(MarkAt, 1_000, Row(1, calls: 10, exec: 100)), mark);
        (StatementsView later, _) = StatementsSinceMark.Apply(View(Now, 1_000, Row(1, calls: 10, exec: 100)), mark);

        same.Basis.Should().Contain("until then the counters are the mark's own");
        same.Rows[0].Window!.Calls.Should().Be(0);
        same.Rows[0].Share.Should().BeNull("there is no change in the total to take a share of");
        later.Basis.Should().BeNull("the advisor's statistics note is about a window this mark replaced");
    }

    [Fact]
    public void A_mark_holds_numbers_only_and_only_of_a_read()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));

        mark.At.Should().Be(MarkAt);
        mark.AllExecMs.Should().Be(1_000);
        mark.Rows.Should().ContainKey(1);
        mark.Rows[1].SinceBegan.Calls.Should().Be(10);

        typeof(StatementsMark).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));
        typeof(StatementMarkRow).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));

        StatementsSinceMark.Take(StatementsView.Refused(PostgresStudio.Services.Database.DatabaseRefusal.CapabilityOff, "off")).Should().BeNull();
        StatementsSinceMark.Take(new StatementsView { State = StatementsState.NotInstalled, MeasuredAt = Now }).Should().BeNull();
        StatementsSinceMark.Take(new StatementsView { State = StatementsState.Read }).Should().BeNull("there is no moment to count from");
    }

    [Fact]
    public void A_view_that_did_not_read_is_left_as_it_is_with_nothing_said()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView status = new() { State = StatementsState.NotRun, MeasuredAt = Now };

        (StatementsView view, string? invalid) = StatementsSinceMark.Apply(status, mark);

        view.Should().BeSameAs(status);
        invalid.Should().BeNull();
    }

    [Fact]
    public void The_view_carries_what_a_mark_needs_from_the_facts()
    {
        StatementFacts facts = Facts(Read(
                1_000,
                Statement(1, "select * from public.orders", 100, 600, [Orders], since: Now.AddHours(-5)),
                Statement(2, "select 1", 10, 100, since: Now.AddHours(-1))),
            statistics: Window(Now.AddDays(-3), StatisticsResetReason.ServerStarted));

        StatementsView view = new StatementsService(
                new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
                null!, null!, null!, null!, null!, new StudioLogThrottle(TimeProvider.System),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StatementsService>.Instance)
            .Show(facts, null);

        view.AllExecMsSinceBegan.Should().Be(1_000);
        view.StatsReset.Should().Be(facts.StatsReset);
        view.Statistics.Should().BeSameAs(facts.Statistics);
        view.Rows.Single(static x => x.QueryId == 1).CountersSince.Should().Be(Now.AddHours(-5));
        view.Rows.Single(static x => x.QueryId == 2).CountersSince.Should().Be(Now.AddHours(-1));
        view.Span.Should().Be(facts.Span);
        view.From.Should().Be(facts.From);
    }
}
