using System.Text;

using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Services;

namespace KStudio.Integration.Tests.Search;

/// <summary>
/// UI-B's search service over a real broker (D-UI decision 17): the engine is the real Confluent one as the restricted principal <c>reader</c>,
/// and the authorization, capability guard, owner and audit are KStudio's real services, composed by the real registrations over the studio's real
/// action log and the application's real <c>IAuthorizationService</c> (<see cref="LiveSearchHost" />). What the fast suite proves
/// on the fake broker is proven here on both images: a header search finds the seeded record and is audited, a request naming a refused topic reads
/// nothing, "Continue" reads exactly the rest, and a masked header never reaches the audit.
/// </summary>
public abstract class KafkaSearchServiceLiveTests(KafkaFixture fixture)
{
    private const string Sentinel = "S3NT1NEL-live-4d2a";
    private const int Records = 10;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private LiveSearchHost Host(Action<KafkaStudioOptions>? configure = null) => LiveSearchHost.Create(fixture, "reader", configure);

    private static KafkaSearchInput Input(LiveSearchHost host, string[] topics, string? predicate, KafkaScanCursor? cursor = null) =>
        new(host.Entry, topics, LiveScanEngine.T0, DateTimeOffset.UtcNow.AddDays(1), predicate, IncludeUncommitted: false, cursor);

    private static async Task<(KafkaScanResult Result, List<KafkaMatch> Matches)> RunAsync(LiveSearchHost host, KafkaSearchInput input)
    {
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await host.Search.PlanAsync(input, Token);
        plan.Value.Plan.IsValid.Should().BeTrue(string.Join("; ", plan.Value.Plan.Errors.Select(static x => x.Message)));

        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await host.Search.StartAsync(plan.Value, confirmed: true, Token)).Run;

        List<KafkaMatch> matches = [];
        KafkaScanResult? result = null;

        await foreach (KafkaScanBatch batch in run.ReadBatchesAsync(Token))
        {
            matches.AddRange(batch.NewMatches);

            if (batch.Final is { } final)
            {
                result = final;
                run.Complete(final);
            }
        }

        result.Should().NotBeNull("the last batch carries the result");
        return (result, matches);
    }

    private static async Task<string> SeedAsync(LiveContractCluster admin, string label)
    {
        string topic = admin.NewTopic(label);
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(
            topic,
            0,
            [.. Enumerable.Range(0, Records).Select(static i => LiveRecords.At(i, "k" + i, "payload-" + i, ("correlation-id", i % 2 == 0 ? "even" : "odd-" + i), ("customer-email", Sentinel + i)))]);
        return topic;
    }

    [KafkaFact]
    public async Task A_header_search_finds_the_seeded_record_and_audits_it()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "ui-b-find");
        await using LiveSearchHost host = Host();

        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(host, Input(host, [topic], "header.correlation-id = 'odd-7'"));

        KafkaMatch found = matches.Should().ContainSingle().Subject;
        (found.Topic, found.Partition, found.Offset).Should().Be((topic, 0, 7L));
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);

        PostgresStudio.Services.StudioActionLogEntry entry = host.Ring.Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Contain("stopped by Completed").And.Contain("predicate: header.correlation-id = 'odd-7'");
        entry.Target.Should().StartWith(topic);
        host.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9600);
    }

    [KafkaFact]
    public async Task A_value_search_finds_the_record_and_audits_ReadPayloads()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("m3-value");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(
            topic,
            0,
            [.. Enumerable.Range(0, Records).Select(static i => LiveRecords.At(i, "k" + i, "{\"n\":" + i + ",\"customer\":{\"id\":\"customer-" + i + "\"}}"))]);
        await using LiveSearchHost host = Host(options => options.Capabilities.ReadPayloads = true);

        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(host, Input(host, [topic], "$.customer.id = 'customer-7'"));

        KafkaMatch found = matches.Should().ContainSingle().Subject;
        found.Offset.Should().Be(7);
        found.ValueText.Should().Be("{\"n\":7,\"customer\":{\"id\":\"customer-7\"}}", "the match holds the value because the search had the payload grant");
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);

        PostgresStudio.Services.StudioActionLogEntry entry = host.Ring.Should().ContainSingle().Subject;
        entry.Capability.Should().Be("ReadPayloads");
        entry.Message.Should().Contain("predicate: $.customer.id = 'customer-7'").And.EndWith("; values read");
        host.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9600);

        // The same search without the capability is refused where the operation is, before anything is read.
        await using LiveSearchHost without = Host();
        KafkaSearchPlanResult refused = await without.Search.PlanAsync(Input(without, [topic], "$.customer.id = 'customer-7'"), Token);
        refused.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be("Reading values is not available here.");
        without.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9602);
    }

    [KafkaFact]
    public async Task A_pruned_search_reads_one_partition()
    {
        const int Partitions = 3;
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("m3-pruned");
        await admin.CreateTopicAsync(topic, Partitions);

        // The records go where a murmur2 producer puts them (the parity of the engine's function with librdkafka's is PartitionerParityLiveTests').
        for (int partition = 0; partition < Partitions; partition++)
        {
            int mine = partition;
            await admin.ProduceAsync(
                topic,
                mine,
                [.. Enumerable.Range(0, 600)
                    .Where(i => KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes("k-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), Partitions) == mine)
                    .Select(static i => LiveRecords.At(i, "k-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), "v"))]);
        }

        await using LiveSearchHost host = Host(options => options.Topic(topic, t => t.Partitioner(KafkaPartitioner.Murmur2)));

        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(host, Input(host, [topic], "key = 'k-0042'"));

        int expected = KafkaPartitioner.Murmur2.Partition(Encoding.UTF8.GetBytes("k-0042"), Partitions);
        result.Partitions.Should().ContainSingle("only the partition the key maps to is read").Which.Partition.Should().Be(expected);
        matches.Should().ContainSingle().Which.Partition.Should().Be(expected);
        result.PruningSentences.Should().ContainSingle().Which.Should().StartWith($"This search read only partition {expected} of {Partitions} of {topic} (murmur2, verified on ");
        host.Ring.Should().ContainSingle().Which.Message.Should().Contain($"; pruned to partition {expected} of {topic}");

        // "Search all partitions" is a new scan that reads every partition and finds the same record.
        (KafkaScanResult all, List<KafkaMatch> allMatches) = await RunAsync(host, Input(host, [topic], "key = 'k-0042'") with { SearchAllPartitions = true });
        all.Partitions.Should().HaveCount(Partitions);
        allMatches.Should().ContainSingle().Which.Partition.Should().Be(expected);
    }

    [KafkaFact]
    public async Task A_search_naming_a_refused_topic_reads_nothing()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string open = await SeedAsync(admin, "ui-b-open");
        string refused = await SeedAsync(admin, "ui-b-refused");
        await using LiveSearchHost host = Host();
        host.Refuse(refused);

        KafkaSearchPlanResult plan = await host.Search.PlanAsync(Input(host, [open, refused], null), Token);

        plan.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchNamesRefusedTopic).And.NotContain(refused);
        host.Ring.Should().ContainSingle().Which.Message.Should().Be(KafkaStudioText.RefusedEntry);
        host.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
        host.Logs.Entries.Select(static x => x.EventId.Id).Should().NotContain([9600, 9601], "nothing was planned, so nothing was read");
    }

    [KafkaFact]
    public async Task Continue_reads_exactly_the_rest()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "ui-b-continue");
        await using LiveSearchHost host = Host(options => options.Limits.MaxMatches = 4);

        (KafkaScanResult first, List<KafkaMatch> firstMatches) = await RunAsync(host, Input(host, [topic], "key ^= 'k'"));
        first.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        first.Cursor.IsEmpty.Should().BeFalse();

        List<long> union = [.. firstMatches.Select(static x => x.Offset)];
        KafkaScanCursor? cursor = first.Cursor;

        while (cursor is { IsEmpty: false })
        {
            (KafkaScanResult next, List<KafkaMatch> nextMatches) = await RunAsync(host, Input(host, [topic], "key ^= 'k'", cursor));
            union.AddRange(nextMatches.Select(static x => x.Offset));
            cursor = next.StopReason == KafkaScanStopReason.MatchLimit ? next.Cursor : null;
        }

        await using LiveSearchHost unbounded = Host();
        (_, List<KafkaMatch> all) = await RunAsync(unbounded, Input(unbounded, [topic], "key ^= 'k'"));

        union.Should().Equal(all.Select(static x => x.Offset), "the scans together read exactly what one unbounded scan reads");
        union.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The sample's shape (UI-VERIFY): six partitions, a match limit that stops the first scan inside partition 0, and an estimate above the confirmation
    /// threshold. Every step through the service completes within a bound; the continued plan needs its confirmation (a start without it is refused at once,
    /// never left waiting); and the continued scan reads the next records of partition 0, exactly where the first stopped.
    /// </summary>
    [KafkaFact]
    public async Task Continue_over_six_partitions_plans_and_starts_within_a_bound_and_reads_the_next_records()
    {
        TimeSpan bound = TimeSpan.FromSeconds(60);
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("ui-b-six");
        await admin.CreateTopicAsync(topic, 6);

        for (int partition = 0; partition < 6; partition++)
        {
            await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(0, 30).Select(static i => LiveRecords.At(i, "k" + i, "payload-" + i, ("correlation-id", "c-" + i)))]);
        }

        await using LiveSearchHost host = Host(options =>
        {
            options.Limits.MaxMatches = 5;
            options.Limits.ConfirmAboveRecords = 1;
        });

        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await host.Search.PlanAsync(Input(host, [topic], "key ^= 'k'"), Token).WaitAsync(bound, Token);
        plan.Value.Plan.Estimate.NeedsConfirmation.Should().BeTrue();
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await host.Search.StartAsync(plan.Value, confirmed: true, Token).WaitAsync(bound, Token)).Run;
        (KafkaScanResult first, List<KafkaMatch> firstMatches) = await Drain(run).WaitAsync(bound, Token);

        first.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        firstMatches.Select(static x => (x.Partition, x.Offset)).Should().Equal((0, 0L), (0, 1L), (0, 2L), (0, 3L), (0, 4L));

        // "All six not reached" after a match-limit stop is the engine's NotReached for every partition the scan stopped inside or never started: partition 0
        // was read to offset 5 and the others not at all, and the cursor says exactly that.
        first.Partitions.Select(static x => x.State).Should().OnlyContain(static x => x == PartitionEndState.NotReached);
        first.Partitions.Single(static x => x.Partition == 0).Should().Match<KafkaPartitionResult>(static x => x.Next == 5 && x.Records == 5);
        first.Partitions.Where(static x => x.Partition != 0).Should().OnlyContain(static x => x.Records == 0 && x.Next == x.Start);
        first.Cursor.Positions.Should().HaveCount(6);
        first.Cursor.Positions.Single(static x => x.Partition == 0).Next.Should().Be(5);

        // The continued plan: bounded, above the threshold, and a start without the confirmation is a refusal, never a wait.
        KafkaSearchPlanResult.Planned next = (KafkaSearchPlanResult.Planned) await host.Search.PlanAsync(Input(host, [topic], "key ^= 'k'", first.Cursor), Token).WaitAsync(bound, Token);
        next.Value.Plan.Estimate.NeedsConfirmation.Should().BeTrue();
        next.Value.Plan.Estimate.Offsets.Should().Be(6 * 30 - 5, "the continued scan reads what the first left");

        KafkaSearchStartResult unconfirmed = await host.Search.StartAsync(next.Value, confirmed: false, Token).WaitAsync(bound, Token);
        unconfirmed.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.NeedsConfirmation);

        await using KafkaSearchRun continued = ((KafkaSearchStartResult.Running) await host.Search.StartAsync(next.Value, confirmed: true, Token).WaitAsync(bound, Token)).Run;
        (KafkaScanResult second, List<KafkaMatch> secondMatches) = await Drain(continued).WaitAsync(bound, Token);

        second.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        secondMatches.Select(static x => (x.Partition, x.Offset)).Should().Equal((0, 5L), (0, 6L), (0, 7L), (0, 8L), (0, 9L));
        host.Ring.Count(static x => x.Action == "SearchRecords" && x.Succeeded).Should().Be(2, "the continued scan is a new audit entry");
    }

    private static async Task<(KafkaScanResult Result, List<KafkaMatch> Matches)> Drain(KafkaSearchRun run)
    {
        List<KafkaMatch> matches = [];
        KafkaScanResult? result = null;

        await foreach (KafkaScanBatch batch in run.ReadBatchesAsync(Token))
        {
            matches.AddRange(batch.NewMatches);

            if (batch.Final is { } final)
            {
                result = final;
                run.Complete(final);
            }
        }

        return (result!, matches);
    }

    [KafkaFact]
    public async Task A_masked_header_never_reaches_the_audit()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "ui-b-masked");
        await using LiveSearchHost host = Host(options => options.Topic(topic, t => t.Redact(r => r.Header("customer-email").Searchable())));

        (_, List<KafkaMatch> matches) = await RunAsync(host, Input(host, [topic], $"header.customer-email = '{Sentinel}3'"));

        KafkaMatch found = matches.Should().ContainSingle().Subject;
        found.Headers.Single(static x => x.Name == "customer-email").Value.IsRedacted.Should().BeTrue();
        found.Offset.Should().Be(3);

        host.Ring.Select(static x => x.Message + x.Target).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
        host.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
        host.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9600 && x.Message.Contains("<redacted>", StringComparison.Ordinal));
    }
}

/// <summary><see cref="KafkaSearchServiceLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class KafkaSearchServiceLiveTests39(Kafka39Fixture fixture) : KafkaSearchServiceLiveTests(fixture);

/// <summary><see cref="KafkaSearchServiceLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class KafkaSearchServiceLiveTests4(Kafka4Fixture fixture) : KafkaSearchServiceLiveTests(fixture);
