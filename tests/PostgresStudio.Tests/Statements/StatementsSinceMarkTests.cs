using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Statements;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The Statements page counted from a mark (1.2-H-A, X6): each statement's change since the mark and its share of every
/// statement's, a falling counter meaning its entry began again, an entry's own <c>stats_since</c> telling a new incarnation from
/// one the mark saw, and the ways a mark stops holding.
/// </summary>
public class StatementsSinceMarkTests
{
    private static readonly DateTimeOffset MarkAt = Now.AddMinutes(-10);

    private static readonly DateTimeOffset Began = Now.AddDays(-1);

    private static StatementCounts Counts(double calls, double exec) => new(calls, exec, calls * 2, calls * 10, calls, 0, 0, null);

    private static StatementRowView Row(long id, double calls, double exec, DateTimeOffset? began = null) =>
        new(id, "select " + id, null, "select · public.orders", "select · public.orders", Counts(calls, exec), Counts(calls, exec), 0.123, null, null)
        {
            CountersSince = began ?? Began,
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

    private static StatisticsWindow Window(DateTimeOffset since, StatisticsResetReason reason, bool looksReset = false) => new()
    {
        MeasuredAt = Now,
        Since = since,
        Basis = StatisticsBasis.ServerReset,
        Reason = reason,
        LooksReset = looksReset,
        SharedView = "pg_stat_checkpointer",
    };

    private static string Text(SinceMarkEnd? ended) => ended!.Text(static at => at.UtcDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Each_statement_shows_its_change_since_the_mark_and_its_share_of_every_statements()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100), Row(2, calls: 5, exec: 300));

        (StatementsView counted, SinceMarkEnd? ended) = StatementsSinceMark.Apply(
            View(Now, 1_400, Row(1, calls: 30, exec: 300), Row(2, calls: 5, exec: 300)),
            mark);

        ended.Should().BeNull();
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

        counted.MarkedAt.Should().Be(MarkAt, "the page words the moment in its visitor's zone; the service returns the moment");
        counted.MarkSpan.Should().Be(TimeSpan.FromMinutes(10));
        counted.WindowText.Should().Be("the advisor's window", "the advisor's own line is left for the page to choose between");
        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 2);
    }

    [Fact]
    public void A_statement_whose_counters_fell_has_no_change_and_ranks_last()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 100, exec: 500), Row(2, calls: 5, exec: 50));

        (StatementsView counted, SinceMarkEnd? ended) = StatementsSinceMark.Apply(
            View(Now, 1_100, Row(1, calls: 3, exec: 20), Row(2, calls: 6, exec: 80)),
            mark);

        ended.Should().BeNull();

        StatementRowView evicted = counted.Rows.Single(static x => x.QueryId == 1);
        evicted.Window.Should().BeNull("an entry evicted and begun again has no change to show");
        evicted.Share.Should().BeNull();
        evicted.MarkNote.Should().StartWith("Reset since your mark");

        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(2, 1);
    }

    [Fact]
    public void An_entry_that_began_again_after_the_mark_counts_everything_it_has_even_with_higher_counters()
    {
        DateTimeOffset again = MarkAt.AddMinutes(3);
        StatementsMark mark = MarkOf(Row(1, calls: 100, exec: 500));

        // Evicted and seen again: its own start moved, and its counters are higher than the mark's - a plain difference would be wrong.
        (StatementsView counted, SinceMarkEnd? ended) = StatementsSinceMark.Apply(
            View(Now, 1_700, Row(1, calls: 400, exec: 900, began: again), Row(2, calls: 7, exec: 70, began: again.AddMinutes(1))),
            mark);

        ended.Should().BeNull();

        StatementRowView incarnation = counted.Rows.Single(static x => x.QueryId == 1);
        incarnation.Window!.Calls.Should().Be(400, "its counters began after the mark, so all of them are since");
        incarnation.WatchedFrom.Should().Be(again);

        StatementRowView brandNew = counted.Rows.Single(static x => x.QueryId == 2);
        brandNew.Window!.Calls.Should().Be(7);
        brandNew.WatchedFrom.Should().Be(again.AddMinutes(1));
        brandNew.MarkNote.Should().BeNull();
    }

    [Fact]
    public void A_statement_the_mark_lacked_counts_everything_only_when_its_entry_began_after_the_mark()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));

        (StatementsView counted, _) = StatementsSinceMark.Apply(
            View(Now, 1_300, Row(1, calls: 20, exec: 150), Row(2, calls: 99, exec: 900), Row(3, calls: 4, exec: 40, began: MarkAt)),
            mark);

        StatementRowView unknown = counted.Rows.Single(static x => x.QueryId == 2);
        unknown.Window.Should().BeNull("its counters before the mark are unknown, and a guess would be a made-up number");
        unknown.MarkNote.Should().Contain("Not among the statements kept at your mark");

        counted.Rows.Single(static x => x.QueryId == 3).Window!.Calls.Should().Be(4, "an entry that began at the mark's own moment counts all of it");
        counted.Top(10, StatementRanking.TotalTime, StatementSort.TotalTime).Select(static x => x.QueryId).Should().Equal(1, 3, 2);
    }

    [Fact]
    public void A_reset_of_pg_stat_statements_ends_the_mark_by_its_own_reset_time_and_the_view_is_left_as_it_was()
    {
        DateTimeOffset firstReset = MarkAt.AddDays(-3);
        StatementsMark mark = StatementsSinceMark.Take(View(MarkAt, 1_000, Row(1, calls: 10, exec: 100)) with { StatsReset = firstReset })!;
        mark.StatsReset.Should().Be(firstReset);

        StatementsView unchanged = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with { StatsReset = firstReset };
        StatementsSinceMark.Apply(unchanged, mark).Ended.Should().BeNull("the extension's reset time is the mark's own");

        // A reset later than the mark; and one the clock puts BEFORE the mark's moment - which the old rule (reset > mark) missed.
        foreach (DateTimeOffset reset in new[] { MarkAt.AddMinutes(2), MarkAt.AddMinutes(-1) })
        {
            StatementsView now = unchanged with { StatsReset = reset };

            (StatementsView view, SinceMarkEnd? ended) = StatementsSinceMark.Apply(now, mark);

            Text(ended).Should().StartWith("pg_stat_statements was reset at ");
            view.Should().BeSameAs(now, "a mark that no longer holds is not applied");
            view.MarkedAt.Should().BeNull();
        }
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

        SinceMarkEnd? ended = StatementsSinceMark.Apply(now, mark).Ended;

        if (ends)
        {
            Text(ended).Should().StartWith("the statistics began again at ").And.Contain("(");
        }
        else
        {
            ended.Should().BeNull("pg_stat_reset() does not reset pg_stat_statements");
        }

        StatementsSinceMark.Apply(now with { Statistics = Window(MarkAt.AddMinutes(-1), reason) }, mark).Ended
            .Should().BeNull("a window that began before the mark, and was where the mark saw it, is the mark's own");
    }

    [Theory]
    [InlineData(nameof(StatisticsResetReason.Failover), true)]
    [InlineData(nameof(StatisticsResetReason.OtherInstance), true)]
    [InlineData(nameof(StatisticsResetReason.ManualReset), false)]
    public void A_window_that_moved_earlier_ends_the_mark_for_a_failover_or_another_instance(string reasonName, bool ends)
    {
        StatisticsResetReason reason = Enum.Parse<StatisticsResetReason>(reasonName);
        DateTimeOffset since = MarkAt.AddDays(-2);
        StatementsMark mark = StatementsSinceMark.Take(View(MarkAt, 1_000, Row(1, calls: 10, exec: 100)) with { Statistics = Window(since, StatisticsResetReason.ServerStarted) })!;
        mark.WindowSince.Should().Be(since);

        StatementsView earlier = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with { Statistics = Window(since.AddDays(-5), reason) };

        SinceMarkEnd? ended = StatementsSinceMark.Apply(earlier, mark).Ended;

        if (ends)
        {
            Text(ended).Should().StartWith("the server's statistics now begin at ");
        }
        else
        {
            ended.Should().BeNull();
        }

        StatementsSinceMark.Apply(earlier with { Statistics = Window(since.AddSeconds(1), reason) }, mark).Ended
            .Should().BeNull("within the same event's slack the window is where the mark saw it");
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
    public void The_note_that_the_statistics_look_reset_is_kept_beside_a_mark()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView now = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with
        {
            Statistics = Window(MarkAt.AddDays(-1), StatisticsResetReason.ServerStarted, looksReset: true),
            Basis = "The server's counters look reset since the last check.",
        };

        (StatementsView counted, SinceMarkEnd? ended) = StatementsSinceMark.Apply(now, mark);

        ended.Should().BeNull();
        counted.Basis.Should().Be("The server's counters look reset since the last check.");
    }

    [Fact]
    public void The_eviction_sentence_says_it_is_over_the_advisors_window_while_a_mark_counts()
    {
        string plain = StatementsService.EvictedSentence(7, watched: true, marked: false);
        plain.Should().Contain("evicted 7 entries in the window");

        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView now = View(Now, 1_300, Row(1, calls: 20, exec: 150)) with
        {
            Evicted = 7,
            EvictedOverWatchedWindow = true,
            Caveat = "Read from pg_stat_statements." + plain,
        };

        StatementsSinceMark.Apply(now, mark).View.Caveat.Should()
            .Contain("evicted 7 entries in the advisor's window (not since your mark)")
            .And.NotContain("in the window (");

        StatementsView none = now with { Evicted = null, Caveat = "Read from pg_stat_statements." };
        StatementsSinceMark.Apply(none, mark).View.Caveat.Should().Be("Read from pg_stat_statements.", "nothing was evicted, so there is no sentence to rewrite");
    }

    [Fact]
    public void A_mark_holds_numbers_only_and_only_of_a_read()
    {
        DateTimeOffset since = MarkAt.AddDays(-2);
        StatementsMark mark = StatementsSinceMark.Take(View(MarkAt, 1_000, Row(1, calls: 10, exec: 100)) with { Statistics = Window(since, StatisticsResetReason.ServerStarted) })!;

        mark.At.Should().Be(MarkAt);
        mark.AllExecMs.Should().Be(1_000);
        mark.WindowSince.Should().Be(since);
        mark.Rows.Should().ContainKey(1);
        mark.Rows[1].SinceBegan.Calls.Should().Be(10);
        mark.Rows[1].CountersSince.Should().Be(Began);

        typeof(StatementsMark).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));
        typeof(StatementMarkRow).GetProperties().Select(static x => x.PropertyType).Should().NotContain(typeof(string));

        StatementsSinceMark.Take(StatementsView.Refused(DatabaseRefusal.CapabilityOff, "off")).Should().BeNull();
        StatementsSinceMark.Take(new StatementsView { State = StatementsState.NotInstalled, MeasuredAt = Now }).Should().BeNull();
        StatementsSinceMark.Take(new StatementsView { State = StatementsState.Read }).Should().BeNull("there is no moment to count from");
    }

    [Fact]
    public void A_view_that_did_not_read_or_was_refused_is_left_as_it_is_with_nothing_said()
    {
        StatementsMark mark = MarkOf(Row(1, calls: 10, exec: 100));
        StatementsView status = new() { State = StatementsState.NotRun, MeasuredAt = Now };
        // A refused view's State is the enum's default, Read: only its refusal says nothing was read.
        StatementsView refused = StatementsView.Refused(DatabaseRefusal.WritePolicy, "refused");

        refused.State.Should().Be(StatementsState.Read);

        foreach (StatementsView view in new[] { status, refused })
        {
            (StatementsView result, SinceMarkEnd? ended) = StatementsSinceMark.Apply(view, mark);

            result.Should().BeSameAs(view);
            result.MarkedAt.Should().BeNull();
            result.Watched.Should().BeFalse();
            ended.Should().BeNull();
        }
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
        view.Evicted.Should().Be(facts.Evicted);
        view.EvictedOverWatchedWindow.Should().Be(facts.Watched);
    }
}
