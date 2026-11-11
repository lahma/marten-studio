using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Scanning;

/// <summary>
/// The scan engine on real records (decisions 9.2 and 9.5): the stop reasons that need a broker's behaviour to be believed - completed,
/// match limit, byte budget, deadline, cancelled - and the cursor union. The fake proves the rest; this proves the fake.
/// </summary>
public abstract class ScanLiveTests(KafkaFixture fixture)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(LiveContractCluster Admin, string Topic)> SeedAsync(KafkaFixture fixture, string label, int partitions, int perPartition, Func<int, int, ContractRecord> record)
    {
        LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic(label);
        await admin.CreateTopicAsync(topic, partitions);

        for (int p = 0; p < partitions; p++)
        {
            await admin.ProduceAsync(topic, p, [.. Enumerable.Range(0, perPartition).Select(i => record(p, i))]);
        }

        return (admin, topic);
    }

    [Fact]
    public async Task A_scan_reads_every_record_of_its_window_and_completes()
    {
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "complete", 3, 60, static (p, i) => LiveRecords.At(i, $"k{i % 10}", "v" + i, ("seq", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'k3'"), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Records.Should().Be(180);
        matches.Should().HaveCount(18, "k3 is every tenth record of 180");
        matches.Should().OnlyContain(x => x.Key.Text == "k3");
        result.Partitions.Should().OnlyContain(static x => x.State == PartitionEndState.EndOfWindow);
        result.Cursor.IsEmpty.Should().BeTrue();
        plan.Estimate.Offsets.Should().Be(180);
    }

    [Fact]
    public async Task The_cheap_clause_decides_first_on_a_real_broker_so_a_record_it_rejects_is_never_decoded()
    {
        // Decision 12.4: the short circuit, counted by DecodeCounters on real records. The clause on the key is bytes; the clause on the
        // header's text (`~`) is a decode, and runs only on the records the key clause let through.
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "short-circuit", 2, 50, static (p, i) => LiveRecords.At(i, $"k{i % 10}", "v", ("x", "value")));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (_, KafkaScanResult none, IReadOnlyList<KafkaMatch> noMatches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], "header.x ~ 'val' and key = 'absent'"), Token);

        noMatches.Should().BeEmpty();
        none.Records.Should().Be(100);
        none.Decodes.Total.Should().Be(0, "the key clause is false for every record, so the header is never decoded, whatever order the text was typed in");

        (_, KafkaScanResult some, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], "header.x ~ 'val' and key = 'k3'"), Token);

        matches.Should().HaveCount(10);
        some.Decodes.HeaderText.Should().Be(10, "the header was decoded for the records whose key matched, and for no other");
        some.Decodes.KeyText.Should().Be(0);
    }

    [Fact]
    public async Task A_scan_stops_at_its_match_limit_with_a_cursor_that_continues_to_exactly_the_rest()
    {
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "cursor", 2, 100, static (p, i) => LiveRecords.At(i, $"k{i % 5}", "v"));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (_, KafkaScanResult full, IReadOnlyList<KafkaMatch> all) = await engine.RunAsync(LiveScanEngine.Request([topic], "key in ('k1', 'k4')"), Token);
        full.StopReason.Should().Be(KafkaScanStopReason.Completed);

        (_, KafkaScanResult first, IReadOnlyList<KafkaMatch> firstMatches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], "key in ('k1', 'k4')", lower: new KafkaScanLowering { MaxMatches = 25 }), Token);
        first.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        firstMatches.Should().HaveCount(25);
        first.Cursor.IsEmpty.Should().BeFalse();

        // New records after the first scan: the cursor keeps the first scan's ends, so the continuation must not read them.
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 10).Select(static i => LiveRecords.At(500 + i, "k1", "late"))]);

        (_, KafkaScanResult second, IReadOnlyList<KafkaMatch> secondMatches) = await engine.RunAsync(
            LiveScanEngine.Request([topic], "key in ('k1', 'k4')", cursor: first.Cursor), Token);

        second.StopReason.Should().Be(KafkaScanStopReason.Completed);
        (int, long)[] union = [.. firstMatches.Concat(secondMatches).Select(static x => (x.Partition, x.Offset)).Order()];
        (int, long)[] expected = [.. all.Select(static x => (x.Partition, x.Offset)).Order()];

        union.Should().Equal(expected, "the two scans together read exactly the offsets one scan would have");
        union.Should().OnlyHaveUniqueItems();
        (first.Records + second.Records).Should().Be(full.Records);
    }

    [Fact]
    public async Task A_scan_stops_at_its_byte_budget()
    {
        // The lowest byte budget the engine accepts is 1 MiB: 1,000 records of 2 KB are about 2 MB, so the scan stops about halfway; no record matches, so the match limit does not come first.
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "bytes", 1, 1_000, static (p, i) => LiveRecords.At(i, "k", new string('x', 2_000)));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (_, KafkaScanResult result, _) = await engine.RunAsync(LiveScanEngine.Request([topic], "key = 'absent'", lower: new KafkaScanLowering { MaxBytesPerScan = 1024 * 1024 }), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.ByteBudget);
        result.Bytes.Should().BeInRange(1024 * 1024, (1024 * 1024) + 2_100);
        result.Records.Should().BeInRange(450, 550);
        result.Partitions.Single().State.Should().Be(PartitionEndState.NotReached);
    }

    [Fact]
    public async Task A_scan_that_cannot_finish_in_time_stops_at_its_deadline()
    {
        // 2,000 records of 8 KiB (16 MiB) at the slowest ceiling the engine allows (64 KiB/s) cannot finish in the 5 seconds it is given.
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "deadline", 1, 2_000, static (p, i) => LiveRecords.At(i, "k", new string('x', 8 * 1024)));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(
            fixture,
            "reader",
            static o =>
            {
                o.Limits.BytesPerSecond = 64 * 1024;
                o.Limits.MaxScanDuration = TimeSpan.FromSeconds(5);
            });

        (_, KafkaScanResult result, _) = await engine.RunAsync(LiveScanEngine.Request([topic]), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Deadline);
        result.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4.5)).And.BeLessThan(TimeSpan.FromSeconds(8));
        result.Records.Should().BeLessThan(200, "at 64 KiB/s a scan reads about 8 records a second");
        result.Cursor.IsEmpty.Should().BeFalse("a continuation can pick up where the deadline stopped it");
    }

    [Fact]
    public async Task A_scan_the_caller_stops_keeps_what_it_read_and_says_cancelled()
    {
        (LiveContractCluster admin, string topic) = await SeedAsync(fixture, "cancel", 1, 2_000, static (p, i) => LiveRecords.At(i, "k", new string('x', 8 * 1024)));
        await using LiveContractCluster adminGuard = admin;
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", static o => o.Limits.BytesPerSecond = 64 * 1024);

        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic]), Token);
        await using KafkaScanHandle handle = engine.Scanner.Start(plan, engine.Owner, confirmed: true).Handle!;

        SpinWait.SpinUntil(() => handle.Progress.Records > 3, TimeSpan.FromSeconds(60)).Should().BeTrue("the scan reads something before it is stopped");
        handle.Stop();
        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Cancelled);
        result.Records.Should().BeGreaterThan(3);
        result.Matches.Should().Be(handle.Matches.Count);
    }

    [Fact]
    public async Task A_scan_over_a_topic_the_principal_cannot_read_ends_not_authorized_and_creates_nothing()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("denied").Replace("ks.", "other.", StringComparison.Ordinal);
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [LiveRecords.At(1, "k", "v")]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        KafkaScanPlan plan = await engine.PlanAsync(LiveScanEngine.Request([topic]), Token);

        // `reader` cannot even describe `other.*`: the window has no ranges, so nothing is read, and the scan says why: every topic was
        // refused, which is NotAuthorized and not "completed" (a completed scan with no match reads as "nothing found").
        plan.Ranges.Should().BeEmpty();
        plan.Window!.Topics.Single().Refusal.Should().NotBeNull();
        await using KafkaScanHandle handle = engine.Scanner.Start(plan, engine.Owner, confirmed: true).Handle!;
        KafkaScanResult result = await handle.Result.WaitAsync(TimeSpan.FromSeconds(30), Token);
        result.StopReason.Should().Be(KafkaScanStopReason.NotAuthorized);
        result.TopicRefusals.Should().ContainSingle();
    }
}

/// <summary><see cref="ScanLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class ScanLiveTests39(Kafka39Fixture fixture) : ScanLiveTests(fixture);

/// <summary><see cref="ScanLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class ScanLiveTests4(Kafka4Fixture fixture) : ScanLiveTests(fixture);
