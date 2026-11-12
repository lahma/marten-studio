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
