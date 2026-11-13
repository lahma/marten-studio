using KStudio.Core.Scanning;
using KStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// What the live tail page says (D-M5 decision 3.7), word for word: every stop reason has a sentence or a pause, every pause cause names its setting or its
/// cause, and the allowance that ran out is the one named.
/// </summary>
public class KafkaTailTextTests
{
    private static readonly KafkaScanLimits Limits = new()
    {
        TailPauseAfter = TimeSpan.FromSeconds(10),
        MaxBytesPerScan = 64L * 1024 * 1024,
        MaxRecordsPerScan = 1_000_000,
    };

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(185, "3:05")]
    [InlineData(3_723, "1:02:03")]
    public void An_elapsed_time_reads_as_minutes_and_seconds_and_from_the_hour_as_hours(int seconds, string expected)
    {
        KafkaTailText.Elapsed(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void Every_stop_reason_is_either_a_pause_the_tail_goes_on_from_or_the_end_of_the_tail()
    {
        Dictionary<KafkaScanStopReason, KafkaTailPause?> expected = new()
        {
            [KafkaScanStopReason.Paused] = KafkaTailPause.You,
            [KafkaScanStopReason.Deadline] = KafkaTailPause.Time,
            [KafkaScanStopReason.ByteBudget] = KafkaTailPause.Bytes,
            [KafkaScanStopReason.RecordBudget] = KafkaTailPause.Records,
            [KafkaScanStopReason.OwnerBudget] = KafkaTailPause.Allowance,
            [KafkaScanStopReason.Unavailable] = KafkaTailPause.Unavailable,
            [KafkaScanStopReason.Completed] = null,
            [KafkaScanStopReason.MatchLimit] = null,
            [KafkaScanStopReason.Cancelled] = null,
            [KafkaScanStopReason.CallerGone] = null,
            [KafkaScanStopReason.Shutdown] = null,
            [KafkaScanStopReason.NotAuthorized] = null,
            [KafkaScanStopReason.Failed] = null,
        };

        Enum.GetValues<KafkaScanStopReason>().Order().Should().Equal(expected.Keys.Order(), "a new stop reason needs a decision here: a pause, or the end");

        foreach ((KafkaScanStopReason reason, KafkaTailPause? pause) in expected)
        {
            KafkaTailText.PauseOf(reason, requested: null).Should().Be(pause, reason.ToString());

            if (pause is null)
            {
                KafkaTailText.Ended(reason).Should().NotBeNullOrWhiteSpace(reason.ToString());
            }
        }
    }

    [Fact]
    public void A_pause_the_page_asked_for_keeps_its_cause_and_one_it_did_not_is_the_visitors()
    {
        KafkaTailText.PauseOf(KafkaScanStopReason.Paused, KafkaTailPause.TabHidden).Should().Be(KafkaTailPause.TabHidden);
        KafkaTailText.PauseOf(KafkaScanStopReason.Paused, KafkaTailPause.ConnectionLost).Should().Be(KafkaTailPause.ConnectionLost);
        KafkaTailText.PauseOf(KafkaScanStopReason.Paused, requested: null).Should().Be(KafkaTailPause.You);
        KafkaTailText.PauseOf(KafkaScanStopReason.Deadline, KafkaTailPause.TabHidden).Should().Be(KafkaTailPause.Time, "the clock ended it: the tab is not why");
    }

    [Fact]
    public void Each_pause_has_its_sentence_and_the_budgets_name_their_settings()
    {
        KafkaTailText.PausedBecause(KafkaTailPause.You, Limits, null).Should().Be("you paused it");
        KafkaTailText.PausedBecause(KafkaTailPause.TabHidden, Limits, null).Should().Be("the tab was hidden; it resumes when you come back");
        KafkaTailText.PausedBecause(KafkaTailPause.Time, Limits, null).Should().Be("it read for 10 seconds");
        KafkaTailText.PausedBecause(KafkaTailPause.Bytes, Limits, null).Should().Be("it read KafkaStudioOptions.Limits.MaxBytesPerScan (64 MiB) in one stretch");
        KafkaTailText.PausedBecause(KafkaTailPause.Records, Limits, null).Should().Be("it read KafkaStudioOptions.Limits.MaxRecordsPerScan (1,000,000 records) in one stretch");
        KafkaTailText.PausedBecause(KafkaTailPause.Unavailable, Limits, null).Should().Be("the cluster stopped answering");
        KafkaTailText.PausedBecause(KafkaTailPause.ConnectionLost, Limits, null).Should().Be("the connection to this page was lost");
        KafkaTailText.PausedLine(KafkaTailPause.ConnectionLost, Limits, null, 5).Should().Be("Paused: the connection to this page was lost. 5 records shown.");

        foreach (KafkaTailPause pause in Enum.GetValues<KafkaTailPause>())
        {
            KafkaTailText.PausedLine(pause, Limits, null, 12).Should().StartWith("Paused: ").And.EndWith(". 12 records shown.", pause.ToString());
        }
    }

    [Fact]
    public void The_allowance_that_ran_out_is_the_one_named()
    {
        KafkaTailText.PausedBecause(KafkaTailPause.Allowance, Limits, new KafkaSpentBudget(KafkaBudgetScope.Process, 2L * 1024 * 1024 * 1024))
            .Should().Be("your hourly allowance of 2 GiB is spent");
        KafkaTailText.PausedBecause(KafkaTailPause.Allowance, Limits, new KafkaSpentBudget(KafkaBudgetScope.Cluster, 512L * 1024 * 1024))
            .Should().Be("your hourly allowance on this cluster of 512 MiB is spent");
        KafkaTailText.PausedBecause(KafkaTailPause.Allowance, Limits, null).Should().Be("your hourly allowance is spent", "no number when the engine did not say which");
    }

    [Fact]
    public void A_tail_that_ended_says_so_and_a_busy_owner_is_told_a_tail_counts()
    {
        KafkaTailText.Ended(KafkaScanStopReason.Cancelled).Should().Be("Stopped by you.");
        KafkaTailText.Ended(KafkaScanStopReason.NotAuthorized).Should().Be("Nothing was read: every partition refused READ.");
        KafkaTailText.Ended(KafkaScanStopReason.Failed).Should().Be(KafkaStudioText.TailFailed);
        KafkaTailText.Refusal(KafkaScanRefusal.OwnerBusy, null).Should().Contain("search or live tail");
        KafkaTailText.Refusal(KafkaScanRefusal.ProfileForbids, null).Should().Contain("KafkaClusterBuilder.Profile");
        KafkaStudioText.Refusal(KafkaScanRefusal.OwnerBusy, spent: null).Should().Contain("Pause the tail to search.", "a search refused for a tail says what holds its place");
    }

    [Fact]
    public void The_live_line_names_the_topics_what_was_read_and_when_the_tail_pauses_itself()
    {
        KafkaTailText.Running(["orders.v1", "payments.v1"], fromEnd: true, 1_204, 2_048, 3, TimeSpan.FromSeconds(75), TimeSpan.FromMinutes(5))
            .Should().Be("Live: reading orders.v1, payments.v1 from the end · 1,204 records, 2 KiB read · 3 matches · 1:15, pauses itself after 5 minutes");
        KafkaTailText.Running(["orders.v1"], fromEnd: false, 1, 10, 1, TimeSpan.Zero, TimeSpan.FromSeconds(10)).Should().Contain("on from where it paused");
        KafkaTailText.LiveChip(3).Should().Be("3 live");
        KafkaTailText.BehindChip("orders.v1", 2).Should().Be("orders.v1/2 behind");
        KafkaTailText.Faster(50, 200).Should().Be("50 matches arrived faster than they could be shown; the newest 200 are kept.");
        KafkaTailText.Jumped(1).Should().Be("Skipped 1 offset produced while paused.");
        KafkaTailText.Jumped(120).Should().Be("Skipped 120 offsets produced while paused.");
    }
}
