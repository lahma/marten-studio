using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Storage;

/// <summary>
/// The statistics window over hand-built histories (W5-CAP): each "sweep" computes the window against the samples before it
/// and appends its own, as <c>HealthSweeper</c> does - so a reset is seen once, its window carried after, and a clean restart,
/// a crash on 14 and on 17, a failover, a restore, a counter drop and the reset functions are each told apart.
/// </summary>
public class StatisticsWindowTests
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>A ring of samples, filled one sweep at a time.</summary>
    private sealed class Ring
    {
        private readonly List<HealthSample> samples = [];

        public HealthHistoryView View => new([.. samples]);

        /// <summary>One sweep: the window against the samples so far, then this sweep's sample appended.</summary>
        public StatisticsWindow Sweep(StatisticsReading reading)
        {
            StatisticsWindow window = StatisticsWindow.Compute(reading, View);
            samples.Add(new HealthSample(reading.ReadAt, window.Sample(reading).ToDictionary(static x => x.Series, static x => x.Value, StringComparer.Ordinal)));
            return window;
        }
    }

    /// <summary>A PostgreSQL 17 server that started at <see cref="Boot" /> and discarded its statistics a second later, as a first start does.</summary>
    private static StatisticsReading Seventeen(DateTimeOffset at, long commits = 1_000, long reads = 5_000) => new()
    {
        ReadAt = at,
        ServerVersion = 170_011,
        DatabaseReset = null,
        SharedReset = Boot.AddSeconds(1),
        ArchiverReset = Boot.AddSeconds(1),
        ServerStartedAt = Boot,
        Timeline = 1,
        SystemIdentifier = 7_698_659_551_654_174_754,
        Commits = commits,
        BlocksRead = reads,
        Tables = 40,
        UnanalysedTables = 2,
    };

    /// <summary>The same on 14, whose collector stamps the database's own <c>stats_reset</c>.</summary>
    private static StatisticsReading Fourteen(DateTimeOffset at, long commits = 1_000) => Seventeen(at, commits) with
    {
        ServerVersion = 140_024,
        DatabaseReset = Boot.AddSeconds(2),
    };

    [Fact]
    public void First_sight_of_a_server_that_kept_its_statistics_since_its_start_says_so()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddDays(3)));

        window.Since.Should().Be(Boot.AddSeconds(1));
        window.Basis.Should().Be(StatisticsBasis.ServerReset);
        window.Reason.Should().Be(StatisticsResetReason.ServerStarted);
        window.LooksReset.Should().BeFalse("there is nothing earlier to compare with");
        window.Length.Should().BeCloseTo(TimeSpan.FromDays(3), TimeSpan.FromSeconds(2));
        window.CoversADay.Should().BeTrue();
        window.BasisText.Should().Be("pg_stat_checkpointer.stats_reset");
    }

    [Fact]
    public void Below_17_the_shared_view_is_the_bgwriters()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddDays(3)) with { ServerVersion = 160_015 });

        window.BasisText.Should().Be("pg_stat_bgwriter.stats_reset");
    }

    [Fact]
    public void A_start_with_most_tables_never_analysed_is_still_only_the_servers_start_on_one_read()
    {
        // A Marten store with many collections nobody has written yet looks exactly like a cluster pg_upgrade just made.
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddHours(2)) with { Tables = 40, UnanalysedTables = 39 });

        window.Reason.Should().Be(StatisticsResetReason.ServerStarted);
    }

    [Fact]
    public void One_shared_views_reset_after_the_counters_began_does_not_move_the_window()
    {
        // initdb thirty days ago; pg_stat_reset_shared('archiver') two days ago, to clear a failure count; first sight now.
        DateTimeOffset now = Boot.AddDays(30);
        StatisticsWindow window = new Ring().Sweep(Seventeen(now) with { ArchiverReset = now.AddDays(-2) });

        window.Since.Should().Be(Boot.AddSeconds(1), "the earlier of the two shared views: the archiver's reset touched no database counter");
        window.Basis.Should().Be(StatisticsBasis.ServerReset);
        window.Length.Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_bgwriter_reset_is_the_same_and_the_archivers_is_the_basis_when_it_is_the_earlier()
    {
        DateTimeOffset now = Boot.AddDays(30);

        new Ring().Sweep(Seventeen(now) with { ServerVersion = 160_015, SharedReset = now.AddDays(-1) }).Since.Should().Be(Boot.AddSeconds(1));

        StatisticsWindow archiverFirst = new Ring().Sweep(Seventeen(now) with { ArchiverReset = Boot, SharedReset = now.AddDays(-1) });
        archiverFirst.Since.Should().Be(Boot);
        archiverFirst.Basis.Should().Be(StatisticsBasis.ArchiverReset);
    }

    [Fact]
    public void The_databases_own_reset_wins_whenever_it_is_later_than_the_server_statistics()
    {
        DateTimeOffset now = Boot.AddDays(30);
        StatisticsWindow window = new Ring().Sweep(Seventeen(now) with { DatabaseReset = now.AddDays(-3), ArchiverReset = now.AddDays(-2) });

        window.Since.Should().Be(now.AddDays(-3), "pg_stat_reset() reset this database's counters; the archiver's reset came after and touched none");
        window.Basis.Should().Be(StatisticsBasis.DatabaseReset);
    }

    [Fact]
    public void A_standby_that_follows_a_promotion_upstream_without_restarting_keeps_its_window()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        StatisticsWindow first = ring.Sweep(Seventeen(before) with { InRecovery = true });

        StatisticsWindow after = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 1_010) with { InRecovery = true, Timeline = 2 });

        after.LooksReset.Should().BeFalse("the standby's own counters went on counting");
        after.Since.Should().Be(first.Since);
    }

    [Fact]
    public void The_watched_standbys_own_promotion_keeps_its_window_whenever_the_new_timeline_shows()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        StatisticsWindow first = ring.Sweep(Seventeen(before) with { InRecovery = true });

        // Promoted between two sweeps; on 14 and 17 pg_control_checkpoint() shows timeline 2 only from the first checkpoint
        // after the promotion, so the sweep that sees it may already be out of recovery with the old timeline once.
        StatisticsWindow promoted = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 1_010) with { InRecovery = false });
        StatisticsWindow checkpointed = ring.Sweep(Seventeen(before.AddMinutes(2), commits: 1_020) with { InRecovery = false, Timeline = 2 });

        promoted.LooksReset.Should().BeFalse();
        checkpointed.LooksReset.Should().BeFalse("its counters went on through its promotion (measured on 14.24, 17.11 and 18.6)");
        checkpointed.Since.Should().Be(first.Since);
    }

    [Fact]
    public void A_new_timeline_out_of_recovery_with_the_counters_down_is_another_server_though_it_started_the_same_second()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        StatisticsWindow window = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 40) with { Timeline = 2 });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.Failover);
    }

    [Fact]
    public void A_read_that_writes_no_history_anchors_a_counter_drop_on_the_last_sample_and_stops_calling_it_a_reset_after_a_day()
    {
        Ring ring = new();
        DateTimeOffset sampled = Boot.AddDays(20);
        ring.Sweep(Seventeen(sampled, commits: 900_000));

        // No sweep runs again; the Storage page reads every interval against the same last sample.
        StatisticsWindow soon = StatisticsWindow.Compute(Seventeen(sampled.AddHours(1), commits: 10), ring.View, recorded: false);
        StatisticsWindow later = StatisticsWindow.Compute(Seventeen(sampled.AddDays(2), commits: 99_999), ring.View, recorded: false);

        soon.Since.Should().Be(sampled, "the last sample's time, the same on every read - never the read's, which would never reach a day");
        soon.Basis.Should().Be(StatisticsBasis.CounterDrop);
        soon.LooksReset.Should().BeTrue();

        later.Since.Should().Be(sampled);
        later.LooksReset.Should().BeFalse("a reset seen against a sample a day old happened at least a day ago");
        later.CoversADay.Should().BeTrue();
        later.Reason.Should().Be(StatisticsResetReason.Unexplained, "what it was is still said");
    }

    // ---- the control signals, read apart (CAP-fix2) ------------------------------------------------------------------------

    [Theory]
    [InlineData("0A000")]
    [InlineData("XX000")]
    [InlineData("42883")]
    [InlineData("42501")]
    [InlineData("57014")]
    public async Task A_control_read_that_fails_with_any_sqlstate_is_unknown_and_is_told_once(string sqlState)
    {
        List<Exception> told = [];

        StatisticsControl control = await StatisticsWindow.ControlOrUnknownAsync(
            _ => throw new Npgsql.PostgresException("pg_control is not available here", "ERROR", "ERROR", sqlState),
            told.Add,
            TestContext.Current.CancellationToken);

        control.Should().Be(StatisticsControl.Unknown);
        told.Should().ContainSingle().Which.Should().BeOfType<Npgsql.PostgresException>().Which.SqlState.Should().Be(sqlState);
    }

    [Fact]
    public async Task A_control_read_that_was_cancelled_still_cancels()
    {
        Func<Task> read = () => StatisticsWindow.ControlOrUnknownAsync(
            static _ => throw new OperationCanceledException(),
            static _ => throw new InvalidOperationException("a cancellation is not a platform's state"),
            TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_control_read_that_answers_is_passed_through()
    {
        StatisticsControl control = await StatisticsWindow.ControlOrUnknownAsync(
            static _ => Task.FromResult(new StatisticsControl(3, 42)),
            static _ => throw new InvalidOperationException("nothing failed"),
            TestContext.Current.CancellationToken);

        control.Should().Be(new StatisticsControl(3, 42));
    }

    [Fact]
    public void A_timeline_not_read_on_either_side_of_a_promotion_elsewhere_says_no_failover()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before));

        // The control read failed for one sweep, then answered with another timeline: nothing to compare the new one with.
        StatisticsWindow unread = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 1_010) with { Timeline = null, SystemIdentifier = null });
        StatisticsWindow answered = ring.Sweep(Seventeen(before.AddMinutes(2), commits: 1_020) with { Timeline = 2 });

        unread.LooksReset.Should().BeFalse("a signal that was not read is unknown, not changed");
        answered.LooksReset.Should().BeFalse("the last check read no timeline to compare with");
        answered.Since.Should().Be(unread.Since);
    }

    [Fact]
    public void A_system_identifier_not_read_says_no_new_cluster()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before));

        StatisticsWindow unread = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 1_010) with { SystemIdentifier = null });
        StatisticsWindow answered = ring.Sweep(Seventeen(before.AddMinutes(2), commits: 1_020) with { SystemIdentifier = 42 });

        unread.LooksReset.Should().BeFalse();
        answered.LooksReset.Should().BeFalse("the last check read no system identifier to compare with");
        answered.Reason.Should().NotBe(StatisticsResetReason.FreshCluster);
    }

    [Fact]
    public void A_different_server_started_within_half_a_second_whose_shared_times_moved_is_a_reset()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        // Started 400 ms from the server watched, counters higher, but its own shared views' reset times and a new timeline:
        // neither a promotion nor following one moves a reset time, so this is another server.
        StatisticsWindow window = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 950_000) with
        {
            ServerStartedAt = Boot.AddMilliseconds(400),
            SharedReset = Boot.AddDays(3),
            ArchiverReset = Boot.AddDays(3),
            Timeline = 2,
        });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.Failover);
        window.Since.Should().Be(Boot.AddDays(3));
    }

    [Fact]
    public void A_sweep_keeps_the_later_bound_for_a_counter_drop()
    {
        Ring ring = new();
        DateTimeOffset sampled = Boot.AddDays(20);
        ring.Sweep(Seventeen(sampled, commits: 900_000));

        StatisticsWindow.Compute(Seventeen(sampled.AddHours(1), commits: 10), ring.View).Since.Should().Be(sampled.AddHours(1));
    }

    [Fact]
    public void Statistics_older_than_the_servers_start_were_kept_through_a_clean_restart()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddDays(10)) with { ServerStartedAt = Boot.AddDays(9) });

        window.Since.Should().Be(Boot.AddSeconds(1));
        window.Reason.Should().Be(StatisticsResetReason.None);
    }

    [Fact]
    public void A_reset_time_well_after_the_start_is_a_manual_reset()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddDays(10)) with { DatabaseReset = Boot.AddDays(5) });

        window.Since.Should().Be(Boot.AddDays(5));
        window.Basis.Should().Be(StatisticsBasis.DatabaseReset);
        window.Reason.Should().Be(StatisticsResetReason.ManualReset);
    }

    [Fact]
    public void A_crash_on_17_moves_the_shared_views_and_zeroes_the_counters_but_not_the_databases_reset()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(2);
        ring.Sweep(Seventeen(before, commits: 90_000));

        DateTimeOffset restarted = before.AddMinutes(10);
        StatisticsWindow window = ring.Sweep(Seventeen(restarted.AddMinutes(1), commits: 12) with
        {
            ServerStartedAt = restarted,
            SharedReset = restarted.AddSeconds(1),
            ArchiverReset = restarted.AddSeconds(1),
            DatabaseReset = null,
        });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.Crash);
        window.Since.Should().Be(restarted.AddSeconds(1));
        window.Basis.Should().Be(StatisticsBasis.ServerReset);
        window.Signal.Should().Contain("restarted").And.Contain("without its statistics");
        window.CoversADay.Should().BeFalse("a reset is never a day's window");
    }

    [Fact]
    public void A_crash_on_14_moves_the_databases_reset_too()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(2);
        ring.Sweep(Fourteen(before, commits: 90_000));

        DateTimeOffset restarted = before.AddMinutes(10);
        StatisticsWindow window = ring.Sweep(Fourteen(restarted.AddMinutes(1), commits: 0) with
        {
            ServerStartedAt = restarted,
            SharedReset = restarted.AddSeconds(1),
            ArchiverReset = restarted.AddSeconds(1),
            DatabaseReset = restarted.AddSeconds(3),
        });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.Crash);
        window.Since.Should().Be(restarted.AddSeconds(3), "the database's own reset is later than the shared views'");
        window.Basis.Should().Be(StatisticsBasis.DatabaseReset);
    }

    [Theory]
    [InlineData(170_011)]
    [InlineData(140_024)]
    public void A_clean_restart_keeps_the_statistics_on_14_and_on_15_and_later(int version)
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(2);
        StatisticsReading reading = (version >= 150_000 ? Seventeen(before, commits: 90_000) : Fourteen(before, commits: 90_000)) with { ServerVersion = version };
        StatisticsWindow first = ring.Sweep(reading);

        StatisticsWindow after = ring.Sweep(reading with
        {
            ReadAt = before.AddMinutes(20),
            ServerStartedAt = before.AddMinutes(10),
            Commits = 90_500,
            BlocksRead = 6_000,
        });

        after.LooksReset.Should().BeFalse("a clean shutdown keeps every counter and every reset time (B0 G4)");
        after.Since.Should().Be(first.Since);
        after.Length.Should().BeGreaterThan(first.Length);
    }

    [Fact]
    public void A_failover_to_a_standby_already_running_starts_the_window_at_its_reset_times_not_its_start()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        // The standby was made from a base backup ten days in, kept its statistics through a clean restart in recovery three
        // days later - as 15 to 18 do (measured on 17.11 and 18.6) - and was promoted just now.
        DateTimeOffset standbyStart = Boot.AddDays(13);
        StatisticsWindow window = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 40) with
        {
            ServerStartedAt = standbyStart,
            SharedReset = Boot.AddDays(10),
            ArchiverReset = Boot.AddDays(10),
            Timeline = 2,
        });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.Failover);
        window.Since.Should().Be(Boot.AddDays(10), "its counters began at its first start, and went on through the restart");
        window.Basis.Should().Be(StatisticsBasis.ServerReset);
        window.Signal.Should().Contain("timeline went from 1 to 2");
    }

    [Fact]
    public void A_failover_to_a_standby_on_14_starts_at_its_last_start_because_its_reset_times_say_so()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Fourteen(before, commits: 900_000));

        // 14 discards a standby's statistics at every start, and stamps every reset time then (measured on 14.24).
        DateTimeOffset standbyStart = Boot.AddDays(13);
        StatisticsWindow window = ring.Sweep(Fourteen(before.AddMinutes(1), commits: 40) with
        {
            ServerStartedAt = standbyStart,
            SharedReset = standbyStart.AddSeconds(1),
            ArchiverReset = standbyStart.AddSeconds(1),
            DatabaseReset = standbyStart.AddSeconds(2),
            Timeline = 2,
        });

        window.Reason.Should().Be(StatisticsResetReason.Failover);
        window.Since.Should().Be(standbyStart.AddSeconds(2));
        window.Basis.Should().Be(StatisticsBasis.DatabaseReset);
    }

    [Fact]
    public void A_timeline_that_went_up_on_a_server_that_started_after_the_last_check_is_a_restore()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        DateTimeOffset restored = before.AddHours(3);
        StatisticsWindow window = ring.Sweep(Seventeen(restored.AddMinutes(5), commits: 3) with
        {
            ServerStartedAt = restored,
            SharedReset = restored.AddSeconds(1),
            ArchiverReset = restored.AddSeconds(1),
            Timeline = 3,
        });

        window.Reason.Should().Be(StatisticsResetReason.Restore);
        window.Since.Should().Be(restored.AddSeconds(1));
    }

    [Fact]
    public void A_counter_that_went_down_with_nothing_moved_starts_the_window_at_that_check_and_the_next_one_carries_it()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        DateTimeOffset seen = before.AddMinutes(1);
        StatisticsWindow dropped = ring.Sweep(Seventeen(seen, commits: 10));

        dropped.LooksReset.Should().BeTrue();
        dropped.Reason.Should().Be(StatisticsResetReason.Unexplained);
        dropped.Since.Should().Be(seen, "the later bound: no reset time said when");
        dropped.Basis.Should().Be(StatisticsBasis.CounterDrop);
        dropped.Signal.Should().Be("xact_commit went down with no reset time moving");

        StatisticsWindow next = ring.Sweep(Seventeen(seen.AddMinutes(1), commits: 25));

        next.LooksReset.Should().BeFalse("the reset was seen once");
        next.Since.Should().Be(seen, "and the shortened window is carried, though the server's reset times never moved");
        next.Basis.Should().Be(StatisticsBasis.CounterDrop);
        next.Reason.Should().Be(StatisticsResetReason.Unexplained);
    }

    [Fact]
    public void A_pg_stat_reset_moves_the_databases_reset_time()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        DateTimeOffset reset = before.AddSeconds(30);
        StatisticsWindow window = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 4) with { DatabaseReset = reset });

        window.LooksReset.Should().BeTrue();
        window.Reason.Should().Be(StatisticsResetReason.ManualReset);
        window.Since.Should().Be(reset);
        window.Basis.Should().Be(StatisticsBasis.DatabaseReset);
    }

    [Fact]
    public void A_pg_stat_reset_shared_of_one_view_with_the_counters_still_climbing_keeps_the_window()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        StatisticsWindow first = ring.Sweep(Seventeen(before, commits: 900_000));

        StatisticsWindow after = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 900_100) with { SharedReset = before.AddSeconds(20) });
        StatisticsWindow later = ring.Sweep(Seventeen(before.AddMinutes(2), commits: 900_200) with { SharedReset = before.AddSeconds(20) });

        after.LooksReset.Should().BeFalse("this database's counters went on climbing");
        after.Since.Should().Be(first.Since);
        later.Since.Should().Be(first.Since, "carried, though the shared view's time is now later");
    }

    [Fact]
    public void A_backend_crash_resets_every_shared_view_at_once_while_the_server_stays_up()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Seventeen(before, commits: 900_000));

        DateTimeOffset reinitialised = before.AddSeconds(30);
        StatisticsWindow window = ring.Sweep(Seventeen(before.AddMinutes(1), commits: 2) with
        {
            SharedReset = reinitialised,
            ArchiverReset = reinitialised.AddMilliseconds(400),
        });

        window.Reason.Should().Be(StatisticsResetReason.Crash);
        window.Signal.Should().Contain("every shared statistic was reset");
    }

    [Fact]
    public void On_14_a_databases_reset_time_that_vanished_after_a_backend_crash_is_a_reset_not_a_move_back()
    {
        Ring ring = new();
        DateTimeOffset before = Boot.AddDays(20);
        ring.Sweep(Fourteen(before, commits: 900_000));

        // The collector restarted with no stats file: the shared views stamped anew, this database's entry not yet re-created.
        DateTimeOffset reinitialised = before.AddSeconds(30);
        StatisticsWindow window = ring.Sweep(Fourteen(before.AddMinutes(1), commits: 0) with
        {
            DatabaseReset = null,
            SharedReset = reinitialised,
            ArchiverReset = reinitialised,
        });

        window.Reason.Should().Be(StatisticsResetReason.Crash);
        window.Since.Should().Be(reinitialised);
    }

    [Fact]
    public void A_new_major_version_or_system_identifier_is_a_new_cluster()
    {
        Ring upgraded = new();
        upgraded.Sweep(Seventeen(Boot.AddDays(20)) with { ServerVersion = 160_015 });
        StatisticsWindow major = upgraded.Sweep(Seventeen(Boot.AddDays(20).AddHours(1), commits: 0));

        major.Reason.Should().Be(StatisticsResetReason.FreshCluster);
        major.Signal.Should().Be("the server's major version went from 16 to 17");

        Ring replaced = new();
        replaced.Sweep(Seventeen(Boot.AddDays(20)));
        StatisticsWindow system = replaced.Sweep(Seventeen(Boot.AddDays(20).AddHours(1)) with { SystemIdentifier = 42 });

        system.Reason.Should().Be(StatisticsResetReason.FreshCluster);
        system.LooksReset.Should().BeTrue();
    }

    [Fact]
    public void Another_instance_answering_is_a_reset_only_where_both_reads_named_one()
    {
        Ring aurora = new();
        aurora.Sweep(Seventeen(Boot.AddDays(20)) with { Instance = "writer-1" });
        StatisticsWindow other = aurora.Sweep(Seventeen(Boot.AddDays(20).AddMinutes(1)) with { Instance = "reader-2" });

        other.Reason.Should().Be(StatisticsResetReason.OtherInstance);
        other.LooksReset.Should().BeTrue();

        Ring vanilla = new();
        vanilla.Sweep(Seventeen(Boot.AddDays(20)));
        vanilla.Sweep(Seventeen(Boot.AddDays(20).AddMinutes(1)) with { Instance = "reader-2" }).LooksReset.Should().BeFalse();
    }

    [Fact]
    public void A_window_under_a_day_is_watched_and_says_how_long_so_far()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddHours(5)));

        window.CoversADay.Should().BeFalse();
        window.PerDay(1_000).Should().BeNull();
        window.Watching("the cache hit ratio", StatisticsWindow.Day).Should()
            .StartWith("Watching: 4 h of statistics so far (since 2026-10-01 08:00 UTC, pg_stat_checkpointer.stats_reset)")
            .And.EndWith("the cache hit ratio is judged once they have counted for a day.");
    }

    [Fact]
    public void A_week_is_covered_only_by_a_week()
    {
        StatisticsWindow window = new Ring().Sweep(Seventeen(Boot.AddDays(6)));

        window.Covers(TimeSpan.FromDays(7)).Should().BeFalse();
        window.CoversADay.Should().BeTrue();
        window.PerDay(6_000).Should().BeApproximately(1_000, 1);
    }

    [Fact]
    public void Every_series_is_written_at_the_database_wide_gate()
    {
        StatisticsReading reading = Seventeen(Boot.AddDays(1)) with { Instance = "writer-1" };
        StatisticsWindow window = StatisticsWindow.Compute(reading, HealthHistoryView.Empty);

        HealthSamplePart[] parts = [.. window.Sample(reading)];

        parts.Should().OnlyContain(static x => x.Gate == HealthGate.Database && x.Series.StartsWith("stats.", StringComparison.Ordinal));
        parts.Select(static x => x.Series).Should().OnlyHaveUniqueItems().And.Contain(
        [
            StatisticsWindow.SinceSeries, StatisticsWindow.TimelineSeries, StatisticsWindow.ServerStartSeries,
            StatisticsWindow.CommitsSeries, StatisticsWindow.BlocksReadSeries, StatisticsWindow.InstanceSeries,
        ]);
    }

    [Fact]
    public void No_reset_time_and_no_start_is_an_empty_window()
    {
        StatisticsWindow window = new Ring().Sweep(new StatisticsReading { ReadAt = Boot });

        window.Basis.Should().Be(StatisticsBasis.Unknown);
        window.Length.Should().Be(TimeSpan.Zero);
        window.CoversADay.Should().BeFalse();
    }

    [Fact]
    public void A_sample_taken_at_or_after_the_read_is_not_the_previous_one()
    {
        Ring ring = new();
        ring.Sweep(Seventeen(Boot.AddDays(2), commits: 900_000));

        // A read stamped no later than the sample already in the ring compares with nothing.
        StatisticsWindow window = StatisticsWindow.Compute(Seventeen(Boot.AddDays(2), commits: 3), ring.View);

        window.LooksReset.Should().BeFalse();
    }
}
