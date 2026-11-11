using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Core.Windows;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The scan engine on the fake broker (decisions 9.2 and 9.5): every stop reason and every partition end state has a scenario that
/// produces it, a reflection guard fails when a value is added without one, the cursor of a stopped scan continues to exactly the offsets
/// one scan would have read, and "no match" is said only of what was read.
/// </summary>
public class ScanEngineTests
{
    private static string Hundred(int i) => $"k{i % 10}";

    private static readonly string Pad = new('x', 98);

    private static async Task<(ScanFixture Fixture, string Topic)> Seeded(int records, int partitions = 1, Action<KafkaEngineOptions>? configure = null)
    {
        ScanFixture fixture = ScanFixture.Create(configure);
        string topic = fixture.Topic("scan", partitions);

        for (int i = 0; i < records; i++)
        {
            fixture.Produce(topic, i % partitions, i, Hundred(i), Pad);
        }

        await Task.CompletedTask;
        return (fixture, topic);
    }

    private static readonly IReadOnlyDictionary<KafkaScanStopReason, Func<Task<KafkaScanResult>>> StopScenarios =
        new Dictionary<KafkaScanStopReason, Func<Task<KafkaScanResult>>>
        {
            [KafkaScanStopReason.Completed] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10);
                await using ScanFixture guard = fixture;
                return (await fixture.RunAsync(ScanFixture.Request([topic]))).Result;
            },
            [KafkaScanStopReason.MatchLimit] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10);
                await using ScanFixture guard = fixture;
                return (await fixture.RunAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxMatches = 3 }))).Result;
            },
            [KafkaScanStopReason.ByteBudget] = async () =>
            {
                // The lowest byte budget the engine accepts is 1 MiB: ten records of 400 KiB, of which the third passes it.
                ScanFixture fixture = ScanFixture.Create();
                await using ScanFixture guard = fixture;
                string topic = fixture.Topic("bytes");
                string big = new('x', 400 * 1024);

                for (int i = 0; i < 10; i++)
                {
                    fixture.Produce(topic, 0, i, "k", big);
                }

                return (await fixture.RunAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxBytesPerScan = 1024 * 1024 }))).Result;
            },
            [KafkaScanStopReason.RecordBudget] = async () =>
            {
                // The lowest record budget the engine accepts is 1,000; no record matches, so the match limit does not come first.
                (ScanFixture fixture, string topic) = await Seeded(1_200);
                await using ScanFixture guard = fixture;
                return (await fixture.RunAsync(ScanFixture.Request([topic], "key = 'absent'", lower: new KafkaScanLowering { MaxRecordsPerScan = 1_000 }))).Result;
            },
            [KafkaScanStopReason.OwnerBudget] = async () =>
            {
                ScanFixture fixture = ScanFixture.Create(static o => o.Limits.OwnerBytesPerHour = 1024 * 1024);
                await using ScanFixture guard = fixture;
                string topic = fixture.Topic("owner");
                string big = new('x', 100 * 1024);

                for (int i = 0; i < 20; i++)
                {
                    fixture.Produce(topic, 0, i, "k", big);
                }

                // Planning samples with the planner's own owner, so the reading owner's hour is untouched.
                KafkaScanPlan plan = await fixture.Scanner.PlanAsync(ScanFixture.Request([topic]), new KafkaScanOwner("planner"), CancellationToken.None);
                await using KafkaScanHandle handle = fixture.Start(plan, confirmed: true, owner: new KafkaScanOwner("reader")).Handle!;
                return await handle.Result.Bounded();
            },
            [KafkaScanStopReason.Deadline] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync(static o => o.Limits.MaxScanDuration = TimeSpan.FromSeconds(5));
                await using ScriptedScan guard = scan;
                ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");
                scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(6));
                return await scan.Handle.Result.Bounded();
            },
            [KafkaScanStopReason.Cancelled] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;
                ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");
                scan.Handle.Stop();
                return await scan.Handle.Result.Bounded();
            },
            [KafkaScanStopReason.CallerGone] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");
                await scan.Handle.DisposeAsync();
                KafkaScanResult result = await scan.Handle.Result.Bounded();
                await scan.Fixture.DisposeAsync();
                return result;
            },
            [KafkaScanStopReason.Shutdown] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");
                await scan.Fixture.DisposeAsync();
                return await scan.Handle.Result.Bounded();
            },
            [KafkaScanStopReason.NotAuthorized] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10);
                await using ScanFixture guard = fixture;
                fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);
                return (await fixture.RunAsync(ScanFixture.Request([topic]))).Result;
            },
            [KafkaScanStopReason.Unavailable] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;

                // The clock moves on the scan's own thread between the two failures, so the second finds nothing has progressed for 16 s.
                scan.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Unavailable("Local_Transport", Retriable: true), null));
                scan.Reader.EnqueueAction(() => scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(16)));
                scan.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Unavailable("Local_Transport", Retriable: true), null));
                return await scan.Handle.Result.Bounded();
            },
            [KafkaScanStopReason.Failed] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;
                scan.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Failed("Local_Fatal", IsFatal: true), null));
                return await scan.Handle.Result.Bounded();
            },
        };

    private static readonly IReadOnlyDictionary<PartitionEndState, Func<Task<KafkaScanResult>>> EndScenarios =
        new Dictionary<PartitionEndState, Func<Task<KafkaScanResult>>>
        {
            [PartitionEndState.EndOfWindow] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10);
                await using ScanFixture guard = fixture;
                return (await fixture.RunAsync(ScanFixture.Request([topic]))).Result;
            },
            [PartitionEndState.EndOfLog] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;

                // The range is [0, 100) but the log ends at 10: the end comes before the window's end.
                for (int i = 0; i < 10; i++)
                {
                    scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, i, i)));
                }

                scan.Reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(scan.Topic, 0), 10));
                return await scan.Handle.Result.Bounded();
            },
            [PartitionEndState.StoppedAtLso] = async () =>
            {
                ScanFixture fixture = ScanFixture.Create();
                await using ScanFixture guard = fixture;
                string topic = fixture.Topic("lso");

                for (int i = 0; i < 5; i++)
                {
                    fixture.Produce(topic, 0, i, "k" + i, "v");
                }

                long transaction = fixture.Cluster.BeginTransaction();

                for (int i = 5; i < 10; i++)
                {
                    fixture.Cluster.Produce(topic, 0, [1], [1], [], ScanFixture.T0.ToUnixTimeMilliseconds() + (i * 1000L), transaction);
                }

                (KafkaScanPlan plan, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([topic]));
                plan.Notes.OfType<WindowNote.StoppedAtLso>().Should().ContainSingle();
                return result;
            },
            [PartitionEndState.Empty] = async () =>
            {
                ScanFixture fixture = ScanFixture.Create();
                await using ScanFixture guard = fixture;
                string topic = fixture.Topic("empty");
                fixture.Produce(topic, 0, 100, "k", "v");
                return (await fixture.RunAsync(ScanFixture.Request([topic], fromSeconds: 0, toSeconds: 10))).Result;
            },
            [PartitionEndState.NotAuthorized] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10);
                await using ScanFixture guard = fixture;
                fixture.Cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, topic);
                return (await fixture.RunAsync(ScanFixture.Request([topic]))).Result;
            },
            [PartitionEndState.OffsetGap] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;
                scan.Reader.RepositionFails = static _ => true;
                scan.Fixture.Cluster.DeleteRecords(scan.Topic, 0, 50);
                scan.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.OffsetOutOfRange(new WirePartition(scan.Topic, 0), null), new WirePartition(scan.Topic, 0)));
                return await scan.Handle.Result.Bounded();
            },
            [PartitionEndState.Stalled] = async () =>
            {
                ScriptedScan scan = await ScriptedScan.StartAsync();
                await using ScriptedScan guard = scan;
                ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan reads");
                scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(16));
                return await scan.Handle.Result.Bounded();
            },
            [PartitionEndState.NotReached] = async () =>
            {
                (ScanFixture fixture, string topic) = await Seeded(10, partitions: 2);
                await using ScanFixture guard = fixture;
                return (await fixture.RunAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxMatches = 1 }))).Result;
            },
        };

    public static TheoryData<string> StopReasons() => [.. Enum.GetNames<KafkaScanStopReason>()];

    public static TheoryData<string> EndStates() => [.. Enum.GetNames<PartitionEndState>()];

    [Fact]
    public void A_scenario_exists_for_every_stop_reason_and_every_end_state()
    {
        // The reflection guard: adding a value to either enum without a scenario fails here, by name.
        StopScenarios.Keys.Order().Should().Equal(Enum.GetValues<KafkaScanStopReason>().Order(), "every KafkaScanStopReason needs a scenario that produces it");
        EndScenarios.Keys.Order().Should().Equal(Enum.GetValues<PartitionEndState>().Order(), "every PartitionEndState needs a scenario that produces it");
    }

    [Theory]
    [MemberData(nameof(StopReasons))]
    public async Task Each_stop_reason_is_produced_by_its_scenario(string name)
    {
        KafkaScanStopReason reason = Enum.Parse<KafkaScanStopReason>(name);
        KafkaScanResult result = await StopScenarios[reason]();

        result.StopReason.Should().Be(reason);
    }

    [Theory]
    [MemberData(nameof(EndStates))]
    public async Task Each_partition_end_state_is_produced_by_its_scenario(string name)
    {
        PartitionEndState state = Enum.Parse<PartitionEndState>(name);
        KafkaScanResult result = await EndScenarios[state]();

        result.Partitions.Should().Contain(x => x.State == state);
    }

    [Fact]
    public async Task A_completed_scan_reports_what_it_read_per_partition_and_keeps_the_matches_it_found()
    {
        (ScanFixture fixture, string topic) = await Seeded(10);
        await using ScanFixture guard = fixture;

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "key = 'k3' or key = 'k7'"));

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Records.Should().Be(10);
        result.Bytes.Should().Be(1_000);
        result.Matches.Should().Be(2);
        result.Undecidable.Should().Be(0);
        result.Cursor.IsEmpty.Should().BeTrue("everything was read");

        KafkaPartitionResult partition = result.Partitions.Single();
        (partition.Start, partition.End, partition.Next, partition.State, partition.Records, partition.Matches).Should().Be((0L, 10L, 10L, PartitionEndState.EndOfWindow, 10L, 2L));
        matches.Select(static x => x.Offset).Should().Equal(3L, 7L);
        matches.Select(static x => x.Key.Text).Should().Equal("k3", "k7");
        plan.AuditText.Should().Be("key = 'k3' or key = 'k7'");
    }

    [Fact]
    public async Task Records_outside_the_window_by_their_own_timestamp_are_read_counted_and_never_matched()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("ooo");

        // Out of order: the first record is stamped after the window, the second inside it. With a margin the range reads both.
        fixture.Produce(topic, 0, 20, "late", "v");
        fixture.Produce(topic, 0, 5, "inside", "v");
        fixture.Produce(topic, 0, 7, "inside2", "v");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(
            ScanFixture.Request([topic], "key ^= ''", fromSeconds: 0, toSeconds: 10, margin: TimeSpan.FromMinutes(1)));

        result.Records.Should().Be(3);
        result.OutsideWindow.Should().Be(1, "read, and outside the window");
        matches.Select(static x => x.Key.Text).Should().Equal("inside", "inside2");
    }

    [Fact]
    public async Task A_record_without_a_timestamp_is_counted_and_never_matches()
    {
        ScriptedScan scan = await ScriptedScan.StartAsync();
        await using ScriptedScan guard = scan;

        scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, 0, 1, type: WireTimestampType.NotAvailable)));
        scan.Reader.Enqueue(new WireReadOutcome.Record(ScanFixture.Rec(scan.Topic, 0, 1, 2)));
        scan.Reader.Enqueue(new WireReadOutcome.PartitionEnd(new WirePartition(scan.Topic, 0), 2));

        KafkaScanResult result = await scan.Handle.Result.Bounded();

        result.NoTimestamp.Should().Be(1);
        result.Matches.Should().Be(1);
    }

    [Fact]
    public async Task Records_the_search_cannot_decide_are_counted_per_reason_and_said_beside_the_result()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("events");
        fixture.Declare(topic, static t => t.Value(KafkaDecoders.CloudEvents));
        fixture.Produce(topic, 0, 1, "a", """{"specversion":"1.0","type":"x"}""");
        fixture.Produce(topic, 0, 2, "b", "{ broken");
        fixture.Produce(topic, 0, 3, "c", "{ broken too");
        fixture.Produce(topic, 0, 4, "d", """{"hello":"world"}""");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await fixture.RunAsync(ScanFixture.Request([topic], "ce.type = 'x'"));

        matches.Should().ContainSingle();
        result.Undecidable.Should().Be(3);
        result.UndecidableByReason[KafkaUnknownReason.InvalidJson].Should().Be(2);
        result.UndecidableByReason[KafkaUnknownReason.NotACloudEvent].Should().Be(1);
        result.UndecidableSentences.Should().Contain("2 records could not be decoded as JSON");
        result.Decodes.CloudEventStructured.Should().Be(4, "the search parsed every value");
        result.MatchDecodes.CloudEventStructured.Should().Be(1, "building the one match read its attributes again, counted apart");
    }

    [Fact]
    public async Task A_group_refusal_is_counted_logged_once_at_debug_and_never_stops_the_scan()
    {
        (ScanFixture fixture, string topic) = await Seeded(10);
        await using ScanFixture guard = fixture;
        fixture.Cluster.RefuseGroupOnFetch = true;

        (_, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([topic]));

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.GroupRefusals.Should().Be(1);
        result.Records.Should().Be(10);
        fixture.Logs.Entries.Where(static x => x.EventId.Id == 9671).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        fixture.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task The_cursor_of_a_stopped_scan_continues_to_exactly_the_offsets_one_scan_would_have_read()
    {
        (ScanFixture fixture, string topic) = await Seeded(40, partitions: 3);
        await using ScanFixture guard = fixture;

        (_, KafkaScanResult full, IReadOnlyList<KafkaMatch> all) = await fixture.RunAsync(ScanFixture.Request([topic], "key in ('k1', 'k4', 'k8')"));
        full.StopReason.Should().Be(KafkaScanStopReason.Completed);

        (_, KafkaScanResult first, IReadOnlyList<KafkaMatch> firstMatches) = await fixture.RunAsync(
            ScanFixture.Request([topic], "key in ('k1', 'k4', 'k8')", lower: new KafkaScanLowering { MaxMatches = 5 }));
        first.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        first.Cursor.IsEmpty.Should().BeFalse();

        // Records arrive after the first scan: the continuation must not read them, because the cursor keeps the first scan's ends.
        for (int i = 0; i < 6; i++)
        {
            fixture.Produce(topic, 0, 500 + i, "k1", Pad);
        }

        (_, KafkaScanResult second, IReadOnlyList<KafkaMatch> secondMatches) = await fixture.RunAsync(
            ScanFixture.Request([topic], "key in ('k1', 'k4', 'k8')", cursor: first.Cursor, toSeconds: 100_000));

        second.StopReason.Should().Be(KafkaScanStopReason.Completed);
        (long, long, long)[] union = [.. firstMatches.Concat(secondMatches).Select(static x => (x.Offset, (long) x.Partition, 0L)).Order()];
        (long, long, long)[] expected = [.. all.Select(static x => (x.Offset, (long) x.Partition, 0L)).Order()];

        union.Should().Equal(expected, "the two scans together read exactly the offsets one scan would have");
        firstMatches.Select(static x => (x.Partition, x.Offset)).Intersect(secondMatches.Select(static x => (x.Partition, x.Offset))).Should().BeEmpty("no offset is read twice");
        (first.Records + second.Records).Should().Be(full.Records, "nor is any offset read twice or skipped");
    }

    [Fact]
    public void A_cursor_has_a_text_form_that_round_trips_and_is_bounded_at_4_KB()
    {
        KafkaScanCursor cursor = new([new KafkaCursorPosition("orders.v1", 0, 12, 100), new KafkaCursorPosition("orders.v1", 7, 0, 5)]);

        cursor.TryFormat(out string text).Should().BeTrue();
        text.Should().Be("v1;orders.v1:0:12:100,orders.v1:7:0:5");

        KafkaScanCursor.TryParse(text, out KafkaScanCursor back).Should().BeTrue();
        back.Positions.Should().Equal(cursor.Positions);

        KafkaScanCursor huge = new([.. Enumerable.Range(0, 600).Select(static i => new KafkaCursorPosition("topic.with.a.long.name", i, 1_000_000, 2_000_000))]);
        huge.TryFormat(out _).Should().BeFalse("the text form is for an address and is bounded at 4 KB");

        foreach (string bad in new[] { "", "v2;a:0:1:2", "v1;a:0:5:2", "v1;a:0:1", "v1;a:x:1:2", "v1;a:0:1:2,a:0:3:4", "v1;bad topic:0:1:2", "v1;a:-1:1:2", "no" })
        {
            KafkaScanCursor.TryParse(bad, out _).Should().BeFalse("'" + bad + "' is not a cursor");
        }

        KafkaScanCursor.TryParse(null, out _).Should().BeFalse();
        KafkaScanCursor.TryParse("v1;" + new string('a', 5_000), out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_cursor_ignores_partitions_it_does_not_name_and_a_finished_partition_is_not_in_it()
    {
        (ScanFixture fixture, string topic) = await Seeded(20, partitions: 2);
        await using ScanFixture guard = fixture;
        KafkaScanCursor onlyZero = new([new KafkaCursorPosition(topic, 0, 4, 10)]);

        (KafkaScanPlan plan, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([topic], cursor: onlyZero));

        plan.Ranges.Should().ContainSingle().Which.Should().Match<PartitionRange>(static x => x.Partition == 0 && x.Start == 4 && x.End == 10);
        result.Records.Should().Be(6);
    }

    [Fact]
    public async Task A_deletion_during_the_scan_is_counted_as_a_gap_and_the_scan_goes_on_from_the_new_log_start()
    {
        BlockingObserver observer = new();
        ScanFixture fixture = ScanFixture.Create(observer: observer);
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("gap");

        for (int i = 0; i < 10; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v");
        }

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        await using KafkaScanHandle handle = fixture.Start(plan, confirmed: true).Handle!;

        await observer.Reached.Task.Bounded();
        fixture.Cluster.DeleteRecords(topic, 0, 5);
        observer.Proceed.Set();

        KafkaScanResult result = await handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        KafkaScanGap gap = result.Gaps.Should().ContainSingle().Subject;
        (gap.FromOffset, gap.ToOffset, gap.Lost).Should().Be((1L, 4L, 4L));
        handle.Matches.Select(static x => x.Offset).Should().Equal(0L, 5L, 6L, 7L, 8L, 9L);
        fixture.Logs.Entries.Where(static x => x.EventId.Id == 9669).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
        fixture.Readers[^1].Assigns.Should().HaveCount(1, "the partition was repositioned, not reassigned wholesale");
    }

    [Fact]
    public async Task A_deletion_of_the_whole_remainder_ends_the_partition_with_an_offset_gap_and_says_what_was_lost()
    {
        BlockingObserver observer = new();
        ScanFixture fixture = ScanFixture.Create(observer: observer);
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("gone");

        for (int i = 0; i < 10; i++)
        {
            fixture.Produce(topic, 0, i, "k" + i, "v");
        }

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));
        await using KafkaScanHandle handle = fixture.Start(plan, confirmed: true).Handle!;

        await observer.Reached.Task.Bounded();
        fixture.Cluster.DeleteRecords(topic, 0, 10);
        observer.Proceed.Set();

        KafkaScanResult result = await handle.Result.Bounded();

        result.Partitions.Single().State.Should().Be(PartitionEndState.OffsetGap);
        result.Gaps.Single().Lost.Should().Be(9);
        result.Matches.Should().Be(1);
    }

    [Fact]
    public async Task The_match_store_holds_at_most_max_matches_and_is_read_by_index_range()
    {
        KafkaMatchStore store = new(2);
        (ScanFixture f, string topic) = await Seeded(5);
        await using ScanFixture guard = f;
        (_, _, IReadOnlyList<KafkaMatch> matches) = await f.RunAsync(ScanFixture.Request([topic]));

        store.TryAdd(matches[0]).Should().BeTrue();
        store.TryAdd(matches[1]).Should().BeTrue();
        store.TryAdd(matches[2]).Should().BeFalse("the store is full");
        store.IsFull.Should().BeTrue();
        store.Count.Should().Be(2);
        store.From(0).Should().HaveCount(2);
        store.From(1).Should().ContainSingle().Which.Should().BeSameAs(matches[1]);
        store.From(5).Should().BeEmpty();
    }

    [Fact]
    public async Task A_window_with_nothing_to_read_starts_no_consumer_takes_no_slot_and_completes_at_once()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("quiet");
        fixture.Produce(topic, 0, 100, "k", "v");

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], toSeconds: 10));
        KafkaScanStart started = fixture.Start(plan);

        started.Started.Should().BeTrue();
        await using KafkaScanHandle handle = started.Handle!;
        handle.IsRunning.Should().BeFalse();
        (await handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Completed);
        fixture.Readers.Should().BeEmpty("no consumer was built");
        fixture.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0);
    }

    [Fact]
    public async Task An_invalid_search_is_a_plan_with_errors_and_start_refuses_it()
    {
        (ScanFixture fixture, string topic) = await Seeded(3);
        await using ScanFixture guard = fixture;

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], "key ="));

        plan.IsValid.Should().BeFalse();
        plan.Errors.Single().Message.Should().Be("Unexpected the end at position 6: expected a value.");
        plan.Window.Should().BeNull("nothing was resolved for a text that does not parse");
        fixture.Start(plan, confirmed: true).Refusal.Should().Be(KafkaScanRefusal.PredicateInvalid);
        fixture.Cluster.Calls.Should().BeEmpty("a text with errors reads nothing, not even offsets");
    }

    [Fact]
    public async Task A_plan_without_the_envelope_grant_or_topics_is_a_caller_error()
    {
        (ScanFixture fixture, string topic) = await Seeded(3);
        await using ScanFixture guard = fixture;

        Func<Task> noGrant = () => fixture.PlanAsync(ScanFixture.Request([topic], grant: KafkaReadGrant.None));
        Func<Task> noTopics = () => fixture.PlanAsync(ScanFixture.Request([]));

        await noGrant.Should().ThrowAsync<ArgumentException>();
        await noTopics.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void A_lowering_can_only_lower_a_limit()
    {
        KafkaScanLimits host = new();

        KafkaScanLowering lower = new() { MaxMatches = 10, MaxBytesPerScan = 2L * 1024 * 1024 };
        KafkaScanLimits applied = lower.Apply(host.Copy());
        (applied.MaxMatches, applied.MaxBytesPerScan, applied.MaxRecordsPerScan).Should().Be((10, 2L * 1024 * 1024, host.MaxRecordsPerScan));

        Action raise = () => new KafkaScanLowering { MaxMatches = host.MaxMatches + 1 }.Apply(host.Copy());
        raise.Should().Throw<ArgumentException>().WithMessage("MaxMatches cannot be raised*");
        Action raiseTime = () => new KafkaScanLowering { MaxScanDuration = host.MaxScanDuration + TimeSpan.FromSeconds(1) }.Apply(host.Copy());
        raiseTime.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task A_visitor_who_lowers_the_search_scope_sees_the_lower_limits_in_the_plan_and_the_host_options_are_untouched()
    {
        (ScanFixture fixture, string topic) = await Seeded(3);
        await using ScanFixture guard = fixture;

        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], lower: new KafkaScanLowering { MaxMatches = 7 }));

        plan.Limits.MaxMatches.Should().Be(7);
        fixture.Options.Limits.MaxMatches.Should().Be(500);
    }

    /// <summary>A scan whose reader a test controls, over a topic with 100 records so that its range is [0, 100).</summary>
    internal sealed class ScriptedScan : IAsyncDisposable
    {
        private ScriptedScan(ScanFixture fixture, ScriptedReader reader, KafkaScanHandle handle, string topic)
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

        public static async Task<ScriptedScan> StartAsync(Action<KafkaEngineOptions>? configure = null, string? predicate = null)
        {
            ScriptedReader reader = new();
            int opened = 0;

            // The first consumer a plan opens is the size sampler's, and reads the fake; the scan's is the second, and reads the script.
            ScanFixture fixture = ScanFixture.Create(configure, wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? wire.OpenReader(options) : reader));
            string topic = fixture.Topic("scripted");

            for (int i = 0; i < 100; i++)
            {
                fixture.Produce(topic, 0, i, "k", "v");
            }

            KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic], predicate));
            KafkaScanStart started = fixture.Start(plan, confirmed: true);
            started.Refusal.Should().BeNull();
            return new ScriptedScan(fixture, reader, started.Handle!, topic);
        }

        public async ValueTask DisposeAsync()
        {
            await Handle.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }

    private sealed class BlockingObserver : IScanObserver
    {
        private int once;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Proceed { get; } = new(false);

        public void AfterRecord(WirePartition partition, long offset)
        {
            if (Interlocked.Exchange(ref once, 1) == 0)
            {
                Reached.SetResult();
                Proceed.Wait(TestBounds.Gate);
            }
        }
    }
}
