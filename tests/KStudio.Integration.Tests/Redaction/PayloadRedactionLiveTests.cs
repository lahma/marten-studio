using System.Text.Json;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Windows;
using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Redaction;

/// <summary>
/// Payload redaction on a real broker (decision 3.6 of D-PAYLOAD): the same record produced to Kafka is scanned and read back, and neither
/// the matches, the plan text, the logs nor the audit text of the search hold a value a rule masks; a searchable rule's exact match finds the
/// record; a value a rule cannot be applied to is withheld; and CloudEvents data is the value on the wire as it was in the fake.
/// </summary>
public abstract class PayloadRedactionLiveTests(KafkaFixture fixture)
{
    private const string Email = "SENTINEL-7f3c2a@example.com";
    private const string Phone = "+358 40 555 0199";
    private const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;

    private static readonly string Document =
        $$"""{"customer":{"email":"{{Email}}","name":"Ann"},"note":"call {{Phone}} now","n":1}""";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Declare(KafkaEngineOptions options, string topic) =>
        options.Topic(topic, t => t.Redact(r =>
        {
            r.JsonPath("$.customer.email").Searchable();
            r.Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone");
        }));

    private static void NoSentinel(params object?[] sinks)
    {
        string serialized = string.Concat(sinks.Select(static x => x is string text ? text : JsonSerializer.Serialize(x)));

        serialized.Should().NotBeEmpty();
        serialized.Should().NotContain("SENTINEL").And.NotContain("555 0199");
    }

    private static async Task<string> SeedAsync(LiveContractCluster admin, string label)
    {
        string topic = admin.NewTopic(label);
        await admin.CreateTopicAsync(topic, 2);

        for (int partition = 0; partition < 2; partition++)
        {
            await admin.ProduceAsync(topic, partition, [.. Enumerable.Range(0, 20).Select(i => LiveRecords.At(i, $"p{partition}-k{i}", Document))]);
        }

        return topic;
    }

    [Fact]
    public async Task A_scan_and_a_record_read_return_no_masked_value_and_the_logs_hold_none()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "payload-redaction");
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) =
            await engine.RunAsync(LiveScanEngine.Request([topic], "$.customer.name = 'Ann'", grant: Payload), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Should().HaveCount(40);
        matches.Should().OnlyContain(static x => x.ValueText!.Contains("••• (redacted by $.customer.email)") && x.ValueText.Contains("••• (redacted by pattern phone)"));
        NoSentinel(matches, plan.AuditText, plan.Verdicts, result.UndecidableSentences);

        KafkaRecordRead read = await engine.Scanner.ReadRecordAsync(
            new KafkaRecordRequest(EngineHarness.ClusterKey, new KafkaTopicName(topic), 1, 7, 3, Payload, KafkaIsolation.Committed, 1024 * 1024),
            engine.Owner,
            Token);

        read.Status.Should().Be(KafkaRecordReadStatus.Found, read.Text);
        read.Record!.ValueText.Should().Contain("••• (redacted by $.customer.email)");
        read.Record.MaskedPaths.Single().Text.Should().Be("$.customer.email");
        NoSentinel(read);
        NoSentinel(string.Join('\n', engine.Logs.Entries.Select(static x => x.Message)));
    }

    [Fact]
    public async Task The_exact_match_on_the_searchable_leaf_finds_the_record_and_its_audit_text_holds_redacted()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin, "payload-exact");
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan plan, KafkaScanResult _, IReadOnlyList<KafkaMatch> matches) =
            await engine.RunAsync(LiveScanEngine.Request([topic], $"$.customer.email = '{Email}'", grant: Payload), Token);

        matches.Should().HaveCount(40, "the raw value is compared exactly");
        plan.AuditText.Should().Be("$.customer.email = <redacted>");
        NoSentinel(matches, plan.AuditText, plan.Verdicts);

        KafkaScanPlan refused = await engine.PlanAsync(LiveScanEngine.Request([topic], $"$.customer.email ~ '{Email}'", grant: Payload), Token);

        refused.IsValid.Should().BeFalse();
        NoSentinel(refused.AuditText, refused.Errors);

        KafkaScanPlan masked = await engine.PlanAsync(LiveScanEngine.Request([topic], "value ~ 'SENTINEL'", grant: Payload), Token);
        KafkaScanStart started = engine.Scanner.Start(masked, engine.Owner, confirmed: true);
        await using KafkaScanHandle handle = started.Handle!;
        KafkaScanResult none = await handle.Result.WaitAsync(TimeSpan.FromMinutes(3), Token);

        handle.Matches.Should().BeEmpty("value clauses compare what the visitor would see");
        none.Undecidable.Should().Be(0);
    }

    [Fact]
    public async Task A_value_that_is_not_json_under_a_path_rule_is_withheld_on_the_wire()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("payload-withheld");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(i => LiveRecords.At(i, "k" + i, $"call {Phone} now"))]);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan _, KafkaScanResult _, IReadOnlyList<KafkaMatch> matches) = await engine.RunAsync(LiveScanEngine.Request([topic], "key ^= 'k'", grant: Payload), Token);

        matches.Should().HaveCount(5);
        matches.Should().OnlyContain(static x => x.ValueText == null && x.ValueWithheldBy == "$.customer.email");
        NoSentinel(matches, string.Join('\n', engine.Logs.Entries.Select(static x => x.Message)));
        engine.Logs.Entries.Count(static x => x.EventId.Id == 9675).Should().Be(1);
    }

    [Fact]
    public async Task A_structured_cloudevents_data_is_the_value_on_the_wire_and_masked_like_one()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = admin.NewTopic("payload-cloudevent");
        await admin.CreateTopicAsync(topic, 1);
        string envelope = "{\"specversion\":\"1.0\",\"type\":\"order.placed\",\"id\":\"e-1\",\"data\":" + Document + "}";
        await admin.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(i => LiveRecords.At(i, "k" + i, envelope, ("content-type", "application/cloudevents+json")))]);
        await using LiveScanEngine engine = LiveScanEngine.Create(fixture, "reader", o => Declare(o, topic));

        (KafkaScanPlan plan, KafkaScanResult result, IReadOnlyList<KafkaMatch> matches) =
            await engine.RunAsync(LiveScanEngine.Request([topic], "ce.type = 'order.placed' and $.customer.name = 'Ann'", grant: Payload), Token);

        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        matches.Should().HaveCount(5);
        matches.Should().OnlyContain(static x => x.ValueIsCloudEventData && x.ValueFormat == "CloudEvents (structured), data as JSON");
        NoSentinel(matches, plan.AuditText);
    }
}

/// <summary><see cref="PayloadRedactionLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class PayloadRedactionLiveTests39(Kafka39Fixture fixture) : PayloadRedactionLiveTests(fixture);

/// <summary><see cref="PayloadRedactionLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class PayloadRedactionLiveTests4(Kafka4Fixture fixture) : PayloadRedactionLiveTests(fixture);
