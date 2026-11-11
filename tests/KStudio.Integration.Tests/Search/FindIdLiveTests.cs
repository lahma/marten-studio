using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

using Microsoft.Extensions.Logging;

namespace KStudio.Integration.Tests.Search;

/// <summary>
/// <b>The M1 exit.</b> "Find the message with this id": as the restricted principal <c>reader</c> (READ on the topics, no configuration, no
/// group ACL beyond the prefix), <c>header.id = 'X'</c> across two topics in a window finds X with its partition and offset, and an id that is
/// not there returns <see cref="KafkaScanStopReason.Completed" /> with no match - and with every note that could make "not there" untrue: the
/// retention gap, the open transaction, the margin, and the configuration the principal could not read. Also as <c>nogroup</c> (SPIKE S1).
/// </summary>
public abstract class FindIdLiveTests(KafkaFixture fixture)
{
    private const string TargetId = "X-7f3a9c";
    private const int Partitions = 3;
    private const int PerPartition = 40;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Two topics of three partitions; topic B's partition 2 holds the target at a known offset; A has a retention gap and B an open transaction.</summary>
    private sealed record Seeded(string A, string B, long TargetOffset, IContractTransaction Open) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Open.DisposeAsync();
    }

    private static async Task<Seeded> SeedAsync(LiveContractCluster admin)
    {
        string a = admin.NewTopic("find-a");
        string b = admin.NewTopic("find-b");
        await admin.CreateTopicAsync(a, Partitions);
        await admin.CreateTopicAsync(b, Partitions);

        long targetOffset = -1;

        foreach (string topic in new[] { a, b })
        {
            for (int partition = 0; partition < Partitions; partition++)
            {
                List<ContractRecord> records = [];

                for (int i = 0; i < PerPartition; i++)
                {
                    string id = $"id-{topic[^1]}-{partition}-{i}";

                    // Topic B, partition 2, the 23rd record is the one being looked for.
                    if (topic == b && partition == 2 && i == 22)
                    {
                        id = TargetId;
                        targetOffset = i;
                    }

                    records.Add(LiveRecords.At(i + (partition * 10), "k" + i, "payload-" + i, ("id", id)));
                }

                await admin.ProduceAsync(topic, partition, records);
            }
        }

        // B's partition 1 has an open transaction inside the window: a committed scan stops at the last stable offset there.
        IContractTransaction open = await admin.BeginTransactionAsync(b, 1, [.. Enumerable.Range(0, 5).Select(static i => LiveRecords.At(50 + i, "tx" + i, "payload", ("id", "id-open-" + i)))]);

        // One record far after the window in every partition, so that the end of a window (with its margin) is a real offset.
        foreach (string topic in new[] { a, b })
        {
            for (int partition = 0; partition < Partitions; partition++)
            {
                await admin.ProduceAsync(topic, partition, [LiveRecords.At(1_000, "late", "payload", ("id", "id-late"))]);
            }
        }

        // A's partition 0 lost its head to retention, so a window from the beginning starts before the earliest retained record.
        await admin.DeleteRecordsAsync(a, 0, 5);

        return new Seeded(a, b, targetOffset, open);
    }

    [Fact]
    public async Task The_message_with_this_id_is_found_across_two_topics_with_its_partition_and_offset()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using Seeded seeded = await SeedAsync(admin);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([seeded.A, seeded.B], $"header.id = '{TargetId}'", toSeconds: 100, margin: TimeSpan.FromMinutes(2)), Token);

        KafkaMatch found = matches.Should().ContainSingle("exactly one record in the window has that id").Subject;
        (found.Topic, found.Partition, found.Offset).Should().Be((seeded.B, 2, seeded.TargetOffset));
        found.Headers.Single(static x => x.Name == "id").Value.Text.Should().Be(TargetId);
        found.Key.Text.Should().Be("k22");
        found.ValueText.Should().BeNull("a visitor who may search records does not, by that, read their values");
        found.ValueKind.Should().Be(KafkaValueKind.Text, "the value is plain text, and only its kind and length are kept");

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Records.Should().BeGreaterThan(0);
        plan.Verdicts.Should().OnlyContain(static x => x.Kind == ClauseVerdictKind.Scan, "a header equality is a byte comparison");
        plan.AuditText.Should().Be($"header.id = '{TargetId}'");
    }

    [Fact]
    public async Task An_id_that_is_not_there_is_a_completed_scan_with_no_match_and_every_note_that_could_make_that_untrue()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using Seeded seeded = await SeedAsync(admin);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([seeded.A, seeded.B], "header.id = 'no-such-id'", toSeconds: 100, margin: TimeSpan.FromMinutes(2)), Token);

        matches.Should().BeEmpty();
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        result.Matches.Should().Be(0);
        result.Records.Should().BeGreaterThan(200, "no match is said of what was read: both topics, every partition");

        string[] notes = [.. plan.Notes.Select(static x => x.Text)];

        notes.Should().Contain(static x => x.StartsWith("Records before ", StringComparison.Ordinal) && x.Contains("are gone (retention): the earliest retained record is offset 5", StringComparison.Ordinal),
            "topic A's partition 0 lost its head");
        notes.Should().Contain(static x => x.StartsWith("Stopped at offset ", StringComparison.Ordinal) && x.Contains("an open transaction holds offsets up to", StringComparison.Ordinal),
            "topic B's partition 1 has an open transaction");
        notes.Should().Contain(static x => x.StartsWith("A record stamped more than 2 min before a later one is not found after offset", StringComparison.Ordinal),
            "the margin of a CreateTime topic");
        notes.Should().Contain(static x => x.StartsWith("Not available: needs `DESCRIBE_CONFIGS` on topic", StringComparison.Ordinal),
            "the principal could not read the topic's configuration");

        result.Partitions.Single(x => x.Topic == seeded.B && x.Partition == 1).State.Should().Be(PartitionEndState.StoppedAtLso);

        await seeded.Open.AbortAsync();
    }

    [Fact]
    public async Task A_principal_with_no_group_acl_finds_the_same_message_and_the_one_refusal_is_not_a_stop()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using Seeded seeded = await SeedAsync(admin);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "nogroup");

        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([seeded.A, seeded.B], $"header.id = '{TargetId}'", toSeconds: 100, margin: TimeSpan.FromMinutes(2)), Token);

        matches.Should().ContainSingle().Which.Offset.Should().Be(seeded.TargetOffset);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);

        // SPIKE S1: one GroupAuthorizationFailed per consumer, about half a second after the assign; a scan that finishes first may not see it.
        result.GroupRefusals.Should().BeInRange(0, 1);
        engine.Logs.Entries.Count(static x => x.EventId.Id == 9671).Should().Be(result.GroupRefusals);
        engine.Logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_principal_that_cannot_read_a_topic_is_told_so_by_the_plan_and_finds_nothing_in_it()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        await using Seeded seeded = await SeedAsync(admin);
        string hidden = admin.NewTopic("find-hidden").Replace("ks.", "other.", StringComparison.Ordinal);
        await admin.CreateTopicAsync(hidden, 1);
        await admin.ProduceAsync(hidden, 0, [LiveRecords.At(1, "k", "v", ("id", TargetId))]);

        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader");
        (_, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(
            LiveScanEngine.Request([seeded.B, hidden], $"header.id = '{TargetId}'", toSeconds: 100, margin: TimeSpan.FromMinutes(2)), Token);

        matches.Should().ContainSingle().Which.Topic.Should().Be(seeded.B);
        result.TopicRefusals.Should().ContainSingle(x => x.Topic == hidden);

        await seeded.Open.AbortAsync();
    }
}

/// <summary><see cref="FindIdLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class FindIdLiveTests39(Kafka39Fixture fixture) : FindIdLiveTests(fixture);

/// <summary><see cref="FindIdLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class FindIdLiveTests4(Kafka4Fixture fixture) : FindIdLiveTests(fixture);
