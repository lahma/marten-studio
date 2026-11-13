using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Scanning;

/// <summary>
/// A live tail's run (AGENTS.md D46, D-M5 decisions 3.3 to 3.5), on the fake broker and on a scripted reader, with a clock a test moves by hand: it
/// matches what arrives after its start whatever the timestamp, never finishes a partition at its end, never stops for a match limit, keeps a ring,
/// stops for exactly one reason, and resumes from an exact cursor.
/// </summary>
public class TailRunTests
{
    private static void Seed(ScanFixture fixture, string topic, int partition, int count)
    {
        for (int i = 0; i < count; i++)
        {
            fixture.Produce(topic, partition, i, "k" + i, "v" + i);
        }
    }

    private static long[] Offsets(IEnumerable<KafkaMatch> matches) => [.. matches.Select(static x => x.Offset)];

    private static WireReadOutcome.PartitionEnd End(string topic, int partition, long offset) => new(new WirePartition(topic, partition), offset);

    private static WireReadOutcome.Record Record(string topic, int partition, long offset, int seconds = 1) => new(ScanFixture.Rec(topic, partition, offset, seconds));

    private static WireReadOutcome.Failure Quiet() => new(new WireError.Failed("Local_Other", IsFatal: false), null);

    /// <summary>
    /// Moves the clock on the scan's own thread, hands it a harmless outcome and waits until the loop has looked at the clocks after it: the last
    /// action runs inside the read that follows that outcome's accounting and its stall check.
    /// </summary>
    private static void PassStallCheck(ScriptedTail tail, TimeSpan by)
    {
        int passed = 0;
        tail.Reader.EnqueueAction(() => tail.Fixture.Clock.Advance(by));
        tail.Reader.Enqueue(Quiet());
        tail.Reader.EnqueueAction(() => Volatile.Write(ref passed, 1));
        ScanFixture.Until(() => Volatile.Read(ref passed) == 1, "the loop passed the stall check");
    }

    [Fact]
    public async Task Records_produced_after_the_start_are_matched_and_the_older_ones_are_not_read()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("after");
        Seed(fixture, topic, 0, 5);

        await using KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        handle.UntilCaughtUp();
        handle.Progress.Records.Should().Be(0, "a tail starts at the end: the five records already there are not read");

        for (int i = 5; i < 8; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }

        handle.UntilRecords(3);
        handle.Stop();
        KafkaScanResult result = await handle.Result.Bounded();

        Offsets(handle.Matches).Should().Equal(5, 6, 7);
        result.Records.Should().Be(3);
        result.Matches.Should().Be(3);
        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        result.Cursor.Positions.Should().ContainSingle().Which.Should().Be(new KafkaCursorPosition(topic, 0, 8, KafkaScanCursor.OpenEnd));
        fixture.Readers.Single().Assigns.Should().ContainSingle().Which.Should().ContainSingle().Which.Offset.Should().Be(5);
    }

    [Fact]
    public async Task The_search_judges_what_arrives_and_a_record_whose_timestamp_is_outside_any_window_still_matches()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("search");
        Seed(fixture, topic, 0, 2);

        await using KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic], "key = \"wanted\""));
        handle.UntilCaughtUp();

        // Stamped long before and long after anything a window could hold.
        fixture.Produce(topic, 0, -86_400, "wanted", "old");
        fixture.Produce(topic, 0, 31_536_000, "wanted", "future");
        fixture.Produce(topic, 0, 3, "other", "skipped");
        handle.UntilRecords(3);
        handle.Stop();
        KafkaScanResult result = await handle.Result.Bounded();

        Offsets(handle.Matches).Should().Equal(2, 3);
        result.Records.Should().Be(3);
        result.OutsideWindow.Should().Be(0, "a tail has no window to be outside of");
    }

    [Fact]
    public async Task A_record_with_no_timestamp_is_still_evaluated_and_counted()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        tail.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(tail.Topic, 0, 10, 1, type: WireTimestampType.NotAvailable)));
        tail.Reader.Enqueue(Record(tail.Topic, 0, 11));
        tail.Handle.UntilRecords(2);
        tail.Handle.Stop();
        KafkaScanResult result = await tail.Handle.Result.Bounded();

        result.NoTimestamp.Should().Be(1);
        result.Matches.Should().Be(2, "the record without a timestamp matched, as an empty search matches everything");
        result.OutsideWindow.Should().Be(0);
    }

    [Fact]
    public async Task A_partition_end_makes_a_partition_live_and_never_finishes_it_and_the_next_record_makes_it_behind_again()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        tail.Reader.Enqueue(End(tail.Topic, 0, 10));
        tail.Handle.UntilCaughtUp();

        KafkaPartitionProgress live = tail.Handle.Progress.Partitions.Single();
        live.State.Should().BeNull("a tail's partition never ends at its end");
        live.CaughtUp.Should().BeTrue();
        live.Percent.Should().Be(100);
        tail.Handle.IsRunning.Should().BeTrue();

        tail.Reader.Enqueue(Record(tail.Topic, 0, 10));
        tail.Handle.UntilRecords(1);

        KafkaPartitionProgress behind = tail.Handle.Progress.Partitions.Single();
        behind.CaughtUp.Should().BeFalse("a record arrived since the end was reported");
        behind.Position.Should().Be(11);
        behind.Percent.Should().Be(0);

        tail.Reader.Enqueue(End(tail.Topic, 0, 11));
        tail.Handle.UntilCaughtUp();
        tail.Handle.Progress.Partitions.Single().Position.Should().Be(11);
        tail.Handle.IsRunning.Should().BeTrue();
        tail.Reader.Paused.Should().BeEmpty("a caught-up partition is not paused: it is waiting for the next record");
    }

    [Fact]
    public async Task A_tail_never_stops_for_the_match_limit_keeps_the_newest_in_its_ring_and_counts_what_it_evicted_exactly()
    {
        // A scan of these limits would stop at 20 matches; the tail's ring keeps 10.
        await using ScanFixture fixture = ScanFixture.Create(static o =>
        {
            o.Limits.MaxMatches = 20;
            o.Limits.MaxTailMatches = 10;
        });
        string topic = fixture.Topic("ring");

        await using KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        List<KafkaScanBatch> batches = TailRig.Collect(handle);
        handle.UntilCaughtUp();

        for (int i = 0; i < 60; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }

        ScanFixture.Until(() => handle.Progress.Matches == 60, "all 60 matches were found");
        handle.IsRunning.Should().BeTrue("three times MaxTailMatches matches did not stop the tail");

        // One tick after all 60 arrived: the page was shown the newest 10 and told it missed 50.
        fixture.Clock.Advance(KafkaScanHandle.BatchInterval);
        ScanFixture.Until(() => TailRig.Snapshot(batches).Count >= 1, "the first batch");
        KafkaScanBatch first = TailRig.Snapshot(batches)[0];
        Offsets(first.NewMatches).Should().Equal(Enumerable.Range(50, 10).Select(static x => (long) x));
        first.Skipped.Should().Be(50);
        first.Final.Should().BeNull();

        handle.Stop();
        KafkaScanResult result = await handle.Result.Bounded();
        fixture.Clock.Advance(KafkaScanHandle.BatchInterval);
        ScanFixture.Until(() => TailRig.Snapshot(batches).Any(static x => x.Final is not null), "the last batch");

        List<KafkaScanBatch> seen = TailRig.Snapshot(batches);
        (seen.Sum(static x => (long) x.NewMatches.Count) + seen.Sum(static x => x.Skipped)).Should().Be(60, "every match was shown or counted as missed, exactly once");
        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        result.Matches.Should().Be(60);
        handle.Matches.Should().HaveCount(10);
    }

    [Fact]
    public async Task The_batcher_takes_matches_only_when_the_reader_has_room_so_a_tail_holds_the_ring_plus_one_batch_and_no_more()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxTailMatches = 10);
        string topic = fixture.Topic("bounded");

        await using KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        handle.UntilCaughtUp();

        // Nobody reads the channel yet. The first tick takes five matches and fills the channel.
        for (int i = 0; i < 5; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v");
        }

        handle.UntilRecords(5);
        fixture.Clock.Advance(KafkaScanHandle.BatchInterval);
        ScanFixture.Until(() => handle.Passes == 1, "the first batch was taken");

        // Five more, and a second tick: the channel is full, so the batcher takes nothing. A bounded wait for something that must not happen.
        for (int i = 5; i < 10; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v");
        }

        handle.UntilRecords(10);
        fixture.Clock.Advance(KafkaScanHandle.BatchInterval);
        SpinWait.SpinUntil(() => handle.Passes > 1, TimeSpan.FromMilliseconds(500)).Should().BeFalse("the batcher waits for room before it takes matches");

        for (int i = 10; i < 30; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v");
        }

        handle.UntilRecords(30);

        // The reader arrives: the first batch is the five, and the second is taken only now, from the ring as it is now.
        List<KafkaScanBatch> batches = TailRig.Collect(handle);
        ScanFixture.Until(() => TailRig.Snapshot(batches).Count >= 2, "both batches");
        List<KafkaScanBatch> seen = TailRig.Snapshot(batches);

        seen[0].NewMatches.Should().HaveCount(5);
        seen[0].Skipped.Should().Be(0);
        seen[1].NewMatches.Should().HaveCount(10, "the ring");
        seen[1].Skipped.Should().Be(15, "thirty arrived, five were shown, ten are kept: fifteen were never held by a batch");
        Offsets(seen[1].NewMatches).Should().Equal(Enumerable.Range(20, 10).Select(static x => (long) x));
    }

    [Fact]
    public async Task A_caught_up_partition_is_never_reported_stalled_however_long_it_is_quiet()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync(partitions: 2);
        await using ScriptedTail guard = tail;

        tail.Reader.Enqueue(End(tail.Topic, 0, 10));
        tail.Reader.Enqueue(End(tail.Topic, 1, 10));
        tail.Handle.UntilCaughtUp();

        // An hour of silence on the scan's own thread, then a harmless outcome so the loop looks at the clocks once more.
        PassStallCheck(tail, TimeSpan.FromMinutes(4));

        tail.Handle.IsRunning.Should().BeTrue();
        tail.Handle.Progress.Partitions.Should().OnlyContain(static x => x.State == null && x.CaughtUp, "idle is not stalled");
        tail.Handle.Stop();
        KafkaScanResult result = await tail.Handle.Result.Bounded();
        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled, "silence never ends a tail with Unavailable");
    }

    [Fact]
    public async Task A_partition_that_is_behind_and_makes_no_progress_is_stalled_and_the_tail_goes_on_with_the_rest()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync(partitions: 2);
        await using ScriptedTail guard = tail;

        // Partition 1 is live; partition 0 never reported its end and never delivered a record.
        tail.Reader.Enqueue(End(tail.Topic, 1, 10));
        tail.Handle.Progress.Partitions.Should().NotBeEmpty();
        PassStallCheck(tail, TimeSpan.FromSeconds(16));

        tail.Handle.IsRunning.Should().BeTrue("one partition is still live");
        tail.Handle.Progress.Partitions.Single(static x => x.Partition == 0).State.Should().Be(PartitionEndState.Stalled);
        KafkaPartitionProgress live = tail.Handle.Progress.Partitions.Single(static x => x.Partition == 1);
        live.State.Should().BeNull();
        live.CaughtUp.Should().BeTrue();

        tail.Handle.Stop();
        KafkaScanResult result = await tail.Handle.Result.Bounded();
        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        result.Partitions.Single(static x => x.Partition == 0).State.Should().Be(PartitionEndState.Stalled);
        result.Partitions.Single(static x => x.Partition == 1).State.Should().Be(PartitionEndState.NotReached);
    }

    [Fact]
    public async Task A_tail_whose_every_partition_stalled_stops_unavailable()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        tail.Reader.EnqueueAction(() => tail.Fixture.Clock.Advance(TimeSpan.FromSeconds(16)));
        tail.Reader.Enqueue(Quiet());

        KafkaScanResult result = await tail.Handle.Result.Bounded();
        result.StopReason.Should().Be(KafkaScanStopReason.Unavailable);
        result.Partitions.Single().State.Should().Be(PartitionEndState.Stalled);
    }

    [Fact]
    public async Task The_wires_unavailable_failures_end_a_tail_that_made_no_progress_for_the_stall_timeout_as_they_end_a_scan()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        tail.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Unavailable("Local_Transport", Retriable: true), null));
        tail.Reader.EnqueueAction(() => tail.Fixture.Clock.Advance(TimeSpan.FromSeconds(16)));
        tail.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Unavailable("Local_Transport", Retriable: true), null));

        (await tail.Handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Unavailable);
    }

    [Fact]
    public async Task Paused_ends_a_segment_on_request_with_an_exact_cursor_and_releases_the_slot()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("pause");
        Seed(fixture, topic, 0, 3);

        KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        await using KafkaScanHandle guard = handle;
        handle.UntilCaughtUp();

        // The segment holds a slot: another read by the same owner is refused, in words that say why.
        KafkaScanPlan scan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        fixture.Start(scan, confirmed: true).Refusal.Should().Be(KafkaScanRefusal.OwnerBusy);

        handle.Stop(KafkaScanStopReason.Paused);
        KafkaScanResult result = await handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.Paused);
        result.Cursor.Positions.Should().ContainSingle().Which.Should().Be(new KafkaCursorPosition(topic, 0, 3, KafkaScanCursor.OpenEnd));

        KafkaScanStart after = fixture.Start(scan, confirmed: true);
        after.Refusal.Should().BeNull("a pause releases the slot");
        await using KafkaScanHandle scanHandle = after.Handle!;
        await scanHandle.Result.Bounded();
    }

    [Fact]
    public async Task A_second_tail_of_the_same_owner_is_owner_busy_and_a_tail_counts_against_the_process_slots_too()
    {
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.MaxConcurrentScans = 1);
        string topic = fixture.Topic("slots");
        Seed(fixture, topic, 0, 2);

        await using KafkaScanHandle first = await fixture.TailAsync(TailRig.Request([topic]));
        KafkaScanPlan plan = await fixture.PlanTailAsync(TailRig.Request([topic]));

        fixture.StartTail(plan).Refusal.Should().Be(KafkaScanRefusal.OwnerBusy);
        fixture.StartTail(plan, new KafkaScanOwner("somebody-else")).Refusal.Should().Be(KafkaScanRefusal.ProcessBusy);

        first.Stop();
        await first.Result.Bounded();
        KafkaScanStart now = fixture.StartTail(plan);
        now.Refusal.Should().BeNull();
        await using KafkaScanHandle second = now.Handle!;
        second.Stop();
        await second.Result.Bounded();
    }

    [Fact]
    public async Task A_tail_pauses_itself_after_TailPauseAfter_and_not_after_MaxScanDuration()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync(static o => o.Limits.MaxScanDuration = TimeSpan.FromSeconds(5));
        await using ScriptedTail guard = tail;
        tail.Reader.Enqueue(End(tail.Topic, 0, 10));
        tail.Handle.UntilCaughtUp();

        // TailPauseAfter is five minutes by default; MaxScanDuration is five seconds and does not apply. Each step runs on the scan's own thread and
        // waits until its loop has looked at the deadline again, so "still running" is what the loop saw, not what a test thread saw first.
        PassStallCheck(tail, TimeSpan.FromSeconds(30));
        tail.Handle.IsRunning.Should().BeTrue("MaxScanDuration (5 s) does not apply to a tail");
        tail.Handle.Progress.StopReason.Should().BeNull();

        PassStallCheck(tail, TimeSpan.FromSeconds(269));
        tail.Handle.Progress.Elapsed.Should().Be(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        tail.Handle.IsRunning.Should().BeTrue("4 min 59 s is before TailPauseAfter");
        tail.Handle.Progress.StopReason.Should().BeNull();

        tail.Fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        KafkaScanResult result = await tail.Handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.Deadline);
        result.Cursor.Positions.Should().ContainSingle().Which.End.Should().Be(KafkaScanCursor.OpenEnd);
    }

    [Fact]
    public async Task The_byte_and_record_budgets_end_a_segment_and_the_owners_hour_ends_it_too()
    {
        await using ScanFixture bytes = ScanFixture.Create(static o =>
        {
            o.Limits.MaxBytesPerScan = 1024 * 1024;
            o.Limits.ConfirmAboveBytes = 1024 * 1024;
        });
        string topic = bytes.Topic("bytes");
        KafkaScanHandle byteTail = await bytes.TailAsync(TailRig.Request([topic]));
        await using KafkaScanHandle byteGuard = byteTail;
        byteTail.UntilCaughtUp();
        bytes.Produce(topic, 0, 1, "k", new string('x', 700_000));
        bytes.Produce(topic, 0, 2, "k", new string('x', 700_000));
        bytes.Produce(topic, 0, 3, "k", "never read");
        KafkaScanResult byteResult = await byteTail.Result.Bounded();
        byteResult.StopReason.Should().Be(KafkaScanStopReason.ByteBudget);
        byteResult.Records.Should().Be(2, "the record that crossed the budget was accounted for completely");
        byteResult.Cursor.Positions.Single().Next.Should().Be(2);

        await using ScanFixture records = ScanFixture.Create(static o =>
        {
            o.Limits.MaxRecordsPerScan = 1_000;
            o.Limits.ConfirmAboveRecords = 1_000;
        });
        string recordTopic = records.Topic("records");
        KafkaScanHandle recordTail = await records.TailAsync(TailRig.Request([recordTopic]));
        await using KafkaScanHandle recordGuard = recordTail;
        recordTail.UntilCaughtUp();

        for (int i = 0; i < 1_200; i++)
        {
            records.Produce(recordTopic, 0, i, "k", "v");
        }

        KafkaScanResult recordResult = await recordTail.Result.Bounded();
        recordResult.StopReason.Should().Be(KafkaScanStopReason.RecordBudget);
        recordResult.Records.Should().Be(1_000);
        recordResult.Cursor.Positions.Single().Next.Should().Be(1_000);

        await using ScanFixture hour = ScanFixture.Create(static o => o.Limits.OwnerBytesPerHour = 1024 * 1024);
        string hourTopic = hour.Topic("hour");
        KafkaScanHandle hourTail = await hour.TailAsync(TailRig.Request([hourTopic]));
        await using KafkaScanHandle hourGuard = hourTail;
        hourTail.UntilCaughtUp();
        hour.Produce(hourTopic, 0, 1, "k", new string('x', 700_000));
        hour.Produce(hourTopic, 0, 2, "k", new string('x', 700_000));
        (await hourTail.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.OwnerBudget);
    }

    [Fact]
    public async Task The_visitor_stopping_the_tab_going_and_the_host_stopping_are_three_more_reasons_and_a_refused_topic_is_another()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("reasons");
        Seed(fixture, topic, 0, 2);

        KafkaScanHandle stopped = await fixture.TailAsync(TailRig.Request([topic]));
        stopped.UntilCaughtUp();
        stopped.Stop();
        (await stopped.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        await stopped.DisposeAsync();

        KafkaScanHandle gone = await fixture.TailAsync(TailRig.Request([topic]));
        gone.UntilCaughtUp();
        await gone.DisposeAsync();
        (await gone.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.CallerGone);

        KafkaScanHandle shutdown = await fixture.TailAsync(TailRig.Request([topic]));
        shutdown.UntilCaughtUp();
        await fixture.DisposeAsync();
        (await shutdown.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Shutdown);

        await using ScanFixture refused = ScanFixture.Create();
        string denied = refused.Topic("denied");
        Seed(refused, denied, 0, 2);
        refused.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, denied);
        await using KafkaScanHandle handle = await refused.TailAsync(TailRig.Request([denied]));
        (await handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.NotAuthorized);
    }

    [Fact]
    public async Task A_pause_and_a_resume_read_every_offset_exactly_once_and_the_two_segments_sum_to_an_uninterrupted_one()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("resume");
        Seed(fixture, topic, 0, 5);

        KafkaScanHandle first = await fixture.TailAsync(TailRig.Request([topic]));
        await using KafkaScanHandle firstGuard = first;
        first.UntilCaughtUp();

        // The first segment reads twenty records and is paused at a place it is known to have reached, whatever the machine's pace.
        for (int i = 5; i < 25; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }

        first.UntilRecords(20);
        first.UntilCaughtUp();
        first.Stop(KafkaScanStopReason.Paused);
        KafkaScanResult one = await first.Result.Bounded();
        IReadOnlyList<KafkaMatch> firstMatches = first.Matches;

        one.StopReason.Should().Be(KafkaScanStopReason.Paused);
        one.Records.Should().Be(20);
        one.Cursor.Positions.Single().Next.Should().Be(25);

        // Twenty more arrive while it is paused; the resume reads exactly those.
        for (int i = 25; i < 45; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v" + i);
        }

        KafkaScanHandle second = await fixture.TailAsync(TailRig.Request([topic], resume: one.Cursor));
        await using KafkaScanHandle secondGuard = second;
        second.UntilRecords(20);
        second.UntilCaughtUp();
        second.Stop();
        KafkaScanResult two = await second.Result.Bounded();

        two.Records.Should().Be(20, "the twenty produced while paused, and nothing read twice");
        long[] all = [.. Offsets(firstMatches), .. Offsets(second.Matches)];
        all.Should().Equal(Enumerable.Range(5, 40).Select(static x => (long) x), "offsets 5 to 44, each exactly once, in order");
        two.Cursor.Positions.Single().Next.Should().Be(45);
    }

    [Fact]
    public async Task A_tail_paused_in_the_middle_of_a_backlog_resumes_at_the_next_offset_it_had_not_read()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        // Five records are in the log (offsets 10 to 14); the segment reads two of them and the third is never handed to it.
        for (int i = 10; i < 15; i++)
        {
            tail.Fixture.Produce(tail.Topic, 0, i, "k" + i, "v" + i);
        }

        tail.Reader.Enqueue(Record(tail.Topic, 0, 10));
        tail.Reader.Enqueue(Record(tail.Topic, 0, 11));
        tail.Reader.EnqueueAction(() => tail.Handle.Stop(KafkaScanStopReason.Paused));
        KafkaScanResult result = await tail.Handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.Paused);
        result.Records.Should().Be(2);
        KafkaCursorPosition position = result.Cursor.Positions.Single();
        position.Next.Should().Be(12);
        position.End.Should().Be(KafkaScanCursor.OpenEnd);

        KafkaScanPlan resumed = await tail.Fixture.PlanTailAsync(TailRig.Request([tail.Topic], resume: result.Cursor));
        resumed.Ranges.Should().ContainSingle().Which.Start.Should().Be(12, "the next offset the paused segment had not read");
        resumed.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task The_governor_paces_a_tail_within_ten_percent_of_the_byte_rate()
    {
        const long rate = 64 * 1024;
        await using ScanFixture fixture = ScanFixture.Create(static o => o.Limits.BytesPerSecond = rate);
        string topic = fixture.Topic("paced");
        string value = new('x', 1_000);
        long recordSize = ScanFixture.Rec(topic, 0, 0, 0, "k0", value).Size;

        for (int i = 0; i < 1_300; i++)
        {
            fixture.Produce(topic, 0, i, "k" + (i % 10), value);
        }

        // Read the whole backlog from offset 0, as a resumed tail does.
        KafkaScanCursor fromTheStart = new([new KafkaCursorPosition(topic, 0, 0, KafkaScanCursor.OpenEnd)]);
        await using KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic], resume: fromTheStart));
        KafkaByteRateGovernor governor = fixture.Provider.GetRequiredService<KafkaByteRateGovernor>();
        DateTimeOffset start = fixture.Clock.GetUtcNow();

        // Lock-step with the scan's thread: the bucket holds at most one second of rate, so a clock that runs ahead of a thread that is still
        // reading wastes the time it skipped. Each second, wait until the thread has read what the bucket allowed and is parked on its debt
        // (not calling Consume), look at how much it read, and only then move the clock on.
        long read = 0;
        double allowed = 0;
        System.Diagnostics.Stopwatch sinceNudge = System.Diagnostics.Stopwatch.StartNew();

        for (int second = 0; second <= 10; second++)
        {
            ScanFixture.Until(
                () =>
                {
                    double seconds = (fixture.Clock.GetUtcNow() - start).TotalSeconds;
                    read = handle.Progress.Bytes;
                    allowed = rate * (1 + seconds);

                    if (read > allowed && governor.Waiters == 1)
                    {
                        return true;
                    }

                    // At most once every 20 ms of real time, so a starved scan thread is never left more than a few virtual milliseconds behind
                    // (the bucket holds one second of rate and wastes the rest).
                    if (governor.Waiters == 1 && sinceNudge.ElapsedMilliseconds >= 20)
                    {
                        sinceNudge.Restart();
                        // Parked on a debt it has already been paid (its timer was armed a moment after the clock moved): the clock moves a
                        // millisecond, which is all the nudge a wake-up needs.
                        fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
                    }

                    return false;
                },
                $"the tail read what {second + 1} s of rate allowed and parked");

            // The record that overdrew the bucket is the most it can be over, and it is never under.
            read.Should().BeInRange((long) allowed, (long) allowed + recordSize);
            handle.Progress.Partitions.Single().State.Should().BeNull();

            if (second < 10)
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            }
        }

        double total = rate * (1 + (fixture.Clock.GetUtcNow() - start).TotalSeconds);
        read.Should().BeInRange((long) (total * 0.9), (long) (total * 1.1), "over ten seconds the tail read the rate within ten percent");
    }

    [Fact]
    public async Task The_tails_consumer_is_assigned_explicit_offsets_never_subscribed_and_closed_once_on_one_dedicated_thread()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("overrides", partitions: 2);
        Seed(fixture, topic, 0, 4);
        Seed(fixture, topic, 1, 7);

        KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        await using KafkaScanHandle guard = handle;
        handle.UntilCaughtUp();
        handle.Stop();
        await handle.Result.Bounded();

        Support.FakeKafka.FakeScanReader reader = fixture.Readers.Single();
        reader.Assigns.Should().ContainSingle("one explicit assign");
        reader.Assigns[0].Select(static x => (x.Key.Partition, x.Offset)).Should().BeEquivalentTo([(0, 4L), (1, 7L)]);
        reader.ThreadIds.Distinct().Should().ContainSingle();
        reader.CloseCount.Should().Be(1);
        reader.GroupId.Should().StartWith("kstudio-");
        reader.Options.Isolation.Should().Be(WireIsolation.ReadCommitted);
    }

    [Fact]
    public async Task A_tail_blocks_a_thread_of_its_own_and_never_a_pool_thread_and_closes_its_consumer_once()
    {
        ScriptedTail tail = await ScriptedTail.StartAsync();
        await using ScriptedTail guard = tail;

        tail.Reader.Enqueue(Record(tail.Topic, 0, 10));
        tail.Handle.UntilRecords(1);
        tail.Handle.Stop(KafkaScanStopReason.Paused);
        await tail.Handle.Result.Bounded();

        tail.Reader.ReadOnPoolThread.Should().BeFalse("a tail blocks for as long as it runs, so it never takes a thread-pool thread");
        tail.Reader.CloseCount.Should().Be(1);
        tail.Reader.Assigns.Should().ContainSingle().Which.Should().ContainSingle().Which.Offset.Should().Be(10);
    }

    [Fact]
    public async Task A_transaction_left_open_at_the_start_is_read_when_it_commits_under_committed_and_a_tail_reads_nothing_of_one_that_aborts()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("transaction");
        Seed(fixture, topic, 0, 2);
        long open = fixture.Cluster.BeginTransaction();
        long aborted = fixture.Cluster.BeginTransaction();
        fixture.Cluster.Produce(topic, 0, [1], [1], [], ScanFixture.T0.ToUnixTimeMilliseconds() + 2_000, open);
        fixture.Cluster.Produce(topic, 0, [1], [1], [], ScanFixture.T0.ToUnixTimeMilliseconds() + 3_000, aborted);

        // Planned with the high watermark at 4 and the last stable offset at 2: the tail starts at 2.
        KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic]));
        await using KafkaScanHandle guard = handle;
        handle.Progress.Partitions.Single().Position.Should().Be(2);

        fixture.Cluster.EndTransaction(topic, 0, aborted, commit: false);
        fixture.Cluster.EndTransaction(topic, 0, open, commit: true);
        handle.UntilRecords(1);
        handle.UntilCaughtUp();
        handle.Stop();
        await handle.Result.Bounded();

        Offsets(handle.Matches).Should().Equal(2);
    }

    /// <summary>A tail over a scripted reader: the topic has ten records, so the tail starts at offset 10.</summary>
    private sealed class ScriptedTail : IAsyncDisposable
    {
        private ScriptedTail(ScanFixture fixture, ScriptedReader reader, KafkaScanHandle handle, string topic)
        {
            Fixture = fixture;
            Reader = reader;
            Handle = handle;
            Topic = topic;
        }

        public ScanFixture Fixture { get; }

        public ScriptedReader Reader { get; }

        public KafkaScanHandle Handle { get; }

        public string Topic { get; }

        public static async Task<ScriptedTail> StartAsync(Action<KafkaEngineOptions>? configure = null, int partitions = 1, string? predicate = null)
        {
            ScriptedReader reader = new();

            // Planning a tail opens no consumer, so the first one the wire is asked for is the tail's.
            ScanFixture fixture = ScanFixture.Create(configure, wrap: wire => new ScriptedWire(wire, _ => reader));
            string topic = fixture.Topic("scripted", partitions);

            for (int partition = 0; partition < partitions; partition++)
            {
                Seed(fixture, topic, partition, 10);
            }

            KafkaScanHandle handle = await fixture.TailAsync(TailRig.Request([topic], predicate));
            ScanFixture.Until(() => reader.Reads > 0, "the tail reads");
            return new ScriptedTail(fixture, reader, handle, topic);
        }

        public async ValueTask DisposeAsync()
        {
            await Handle.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }
}
