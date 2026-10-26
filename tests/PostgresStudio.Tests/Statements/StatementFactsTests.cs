using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Statements;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The Statements page's facts (W5-Q1, issue #8 G6), with no Postgres: the ranking and the share arithmetic over hand-built
/// windows, the window bounded by the statistics window, and T02's text rules applied at read - the statement withheld, the
/// comment masked.
/// </summary>
public class StatementFactsTests
{
    [Fact]
    public void Before_two_samples_every_statement_is_its_totals_since_its_counters_began_and_its_share_of_the_extensions_total()
    {
        StatementFacts facts = Facts(Read(
            1000,
            Statement(1, "select * from public.orders where id = $1", 100, 300, [Orders]),
            Statement(2, "select 1", 400, 600)));

        facts.State.Should().Be(StatementsState.Read);
        facts.Watched.Should().BeFalse();
        facts.From.Should().Be(Now.AddDays(-1), "since pg_stat_statements_info.stats_reset");
        facts.TotalExecMs.Should().Be(1000);

        facts.Statements.Select(static x => x.QueryId).Should().Equal([2, 1], "the most execution time first");
        facts.Statements[0].Window.Should().Be(facts.Statements[0].SinceBegan);
        facts.Statements[0].Share.Should().Be(0.6);
        facts.Statements[1].Share.Should().Be(0.3);
        facts.Statements[1].Window!.MeanMs.Should().Be(3);
    }

    [Fact]
    public void Over_a_watched_window_each_statement_is_its_change_and_its_share_is_of_the_change_in_the_total()
    {
        HealthHistoryView history = History(
            Sample(TimeSpan.FromMinutes(30), 10_000, (1, 100, 1_000), (2, 1_000, 5_000)),
            Sample(TimeSpan.FromMinutes(15), 12_000, (1, 150, 1_600), (2, 1_200, 5_800)),
            Sample(TimeSpan.Zero, 14_000, (1, 200, 2_000), (2, 1_400, 6_500)));

        StatementFacts facts = Facts(
            Read(14_000, Statement(1, "select * from public.orders", 200, 2_000, [Orders]), Statement(2, "select 1", 1_400, 6_500)),
            history);

        facts.Watched.Should().BeTrue();
        (facts.From, facts.To).Should().Be((Now.AddMinutes(-30), Now));
        facts.Span.Should().Be(TimeSpan.FromMinutes(30));
        facts.TotalExecMs.Should().Be(4_000, "the database's total changed by 4 s in the window");

        StatementFact first = facts.Statements[0];
        first.QueryId.Should().Be(2, "1.5 s in the window ranks above 1 s, whatever their totals since they began");
        first.Window!.ExecMs.Should().Be(1_500);
        first.Window.Calls.Should().Be(400);
        first.Share.Should().Be(1_500d / 4_000);
        first.WatchedFrom.Should().BeNull("it was watched for the whole window");

        StatementFact second = facts.Statements[1];
        second.Window!.ExecMs.Should().Be(1_000);
        second.Window.Calls.Should().Be(100);
        second.Window.SharedBlocksRead.Should().Be(100);
        second.Share.Should().Be(0.25);
        second.SinceBegan.ExecMs.Should().Be(2_000, "its totals since its counters began are kept beside the window");
    }

    [Fact]
    public void A_statement_new_to_the_kept_ones_is_not_ranked_on_a_lifetimes_total_inside_a_window()
    {
        HealthHistoryView history = History(
            Sample(TimeSpan.FromMinutes(30), 10_000, (1, 100, 1_000)),
            Sample(TimeSpan.Zero, 12_000, (1, 200, 2_000), (3, 50, 90_000)));

        StatementFacts facts = Facts(
            Read(12_000, Statement(1, "select 1", 200, 2_000), Statement(3, "select 2", 50, 90_000, since: Now.AddDays(-3))),
            history);

        StatementFact unwatched = facts.Statements.Single(static x => x.QueryId == 3);
        unwatched.Window.Should().BeNull("its counters before the window are unknown, and its 90 s since it began are not the window's");
        unwatched.Share.Should().BeNull();
        facts.Statements.Select(static x => x.QueryId).Should().Equal([1, 3], "a statement not watched over the window ranks after every watched one");
    }

    [Fact]
    public void A_statement_whose_counters_began_inside_the_window_is_all_in_it()
    {
        HealthHistoryView history = History(
            Sample(TimeSpan.FromMinutes(30), 10_000, (1, 100, 1_000)),
            Sample(TimeSpan.Zero, 12_000, (1, 200, 2_000)));

        StatementFacts facts = Facts(
            Read(12_000, Statement(1, "select 1", 200, 2_000), Statement(4, "select 3", 10, 500, since: Now.AddMinutes(-10))),
            history);

        StatementFact fresh = facts.Statements.Single(static x => x.QueryId == 4);
        fresh.Window!.ExecMs.Should().Be(500, "its own stats_since (17+) is inside the window, so everything it counts is");
        fresh.WatchedFrom.Should().Be(Now.AddMinutes(-10));
        fresh.Share.Should().Be(0.25);
    }

    [Fact]
    public void A_failover_inside_the_window_starts_the_window_again_after_it()
    {
        HealthHistoryView history = History(
            Sample(TimeSpan.FromMinutes(30), 50_000, (1, 900, 9_000)),
            Sample(TimeSpan.FromMinutes(20), 51_000, (1, 950, 9_500)),
            Sample(TimeSpan.Zero, 1_000, (1, 10, 100)));

        StatisticsWindow failover = new()
        {
            MeasuredAt = Now,
            Since = Now.AddMinutes(-5),
            Basis = StatisticsBasis.ServerStart,
            Reason = StatisticsResetReason.Failover,
            LooksReset = true,
            Signal = "the timeline went from 1 to 2",
        };

        StatementFacts facts = Facts(Read(1_000, Statement(1, "select 1", 10, 100)), history, failover);

        facts.Watched.Should().BeFalse("the samples before the failover are another server's counters");
        facts.Statistics.Should().Be(failover);
        StatementFacts.RestartsTheCounters(StatisticsResetReason.ManualReset).Should().BeFalse("pg_stat_reset() does not reset pg_stat_statements");
        StatementFacts.RestartsTheCounters(StatisticsResetReason.Crash).Should().BeTrue();
    }

    [Fact]
    public void A_text_that_keeps_a_literal_is_withheld_at_read_and_a_comment_is_masked()
    {
        StatementFacts facts = Facts(Read(
            null,
            Statement(1, "copy (select * from public.orders where note = 'alice@example.com') to stdout", 1, 100),
            Statement(2, "/* user='alice@example.com' */ select * from public.orders where id = $1", 10, 50, [Orders])));

        StatementFact copy = facts.Statements.Single(static x => x.QueryId == 1);
        copy.Text.Should().BeNull();
        copy.TextWithheld.Should().Be(StatementReads.WithheldUtility);
        copy.TextRead.Should().BeTrue();

        StatementFact select = facts.Statements.Single(static x => x.QueryId == 2);
        select.Text.Should().Be(StatementReads.CommentMask + " select * from public.orders where id = $1");
        select.TextWithheld.Should().BeNull();
        select.Group!.Label.Should().Be("select · public.orders");
    }

    [Fact]
    public void A_statement_whose_text_was_not_returned_has_no_text_and_no_group()
    {
        StatementFact fact = Facts(Read(null, Statement(9, null, 1, 1))).Statements.Single();

        (fact.Text, fact.TextWithheld, fact.TextRead, fact.Group).Should().Be((null, null, false, null));
    }

    [Fact]
    public void A_read_that_did_not_read_says_which_state_stopped_it_and_carries_the_platform()
    {
        StatementReadOutcome notInstalled = new(StatementReadState.NotInstalled, new StatementExtension(170011, null, null), null)
        {
            Platform = new PostgresPlatform(PostgresStudio.PostgresPlatformKind.AmazonAurora, "aurora_version()"),
        };

        StatementFacts facts = StatementFacts.Build("default", "default", Now, notInstalled, null, HealthHistoryView.Empty, null, Tables, null);

        facts.State.Should().Be(StatementsState.NotInstalled);
        facts.Platform!.Kind.Should().Be(PostgresStudio.PostgresPlatformKind.AmazonAurora);
        facts.Statements.Should().BeEmpty();

        StatementFacts failed = StatementFacts.Build("default", "default", Now, null, "T02 timed out.", HealthHistoryView.Empty, null, Tables, null);
        (failed.State, failed.Failure).Should().Be((StatementsState.Failed, "T02 timed out."));
    }
}
