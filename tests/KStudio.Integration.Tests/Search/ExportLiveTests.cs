using System.Text;

using KStudio.Core.Export;
using KStudio.Core.Scanning;
using KStudio.Integration.Tests.Support;
using KStudio.Services;

namespace KStudio.Integration.Tests.Search;

/// <summary>
/// Export over a real broker (AGENTS.md D45, D-M5 decision 2): the records a real search found are saved as JSON Lines and as CSV through the
/// real export service, composed by the real registrations over the real policy, ring and log. What the fast suite proves on the fake broker is proven
/// here on both images: the file holds one row per record, the deny-listed header and the rule-masked header are masked in both formats and the secret is
/// in no byte, the audit holds the plan's text, and a <c>Production</c> cluster never exports.
/// </summary>
public abstract class ExportLiveTests(KafkaFixture fixture)
{
    private const string Sentinel = "S3NT1NEL-export-live-8e5b";
    private const int Records = 6;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static KafkaSearchInput Input(LiveSearchHost host, string topic) =>
        new(host.Entry, [topic], LiveScanEngine.T0, DateTimeOffset.UtcNow.AddDays(1), "header.correlation-id ~ 'c-'", IncludeUncommitted: false);

    private static async Task<string> SeedAsync(LiveContractCluster admin)
    {
        string topic = admin.NewTopic("export-live");
        await admin.CreateTopicAsync(topic, 1);
        await admin.ProduceAsync(
            topic,
            0,
            [.. Enumerable.Range(0, Records).Select(static i => LiveRecords.At(i, "k" + i, "{\"n\":" + i + "}", ("correlation-id", "c-" + i), ("authorization", "Bearer " + Sentinel + i), ("customer-email", Sentinel + "-mail-" + i)))]);
        return topic;
    }

    private static async Task<(KafkaSearchPlanned Planned, List<KafkaMatch> Matches, KafkaScanResult Result)> SearchAsync(LiveSearchHost host, string topic)
    {
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await host.Search.PlanAsync(Input(host, topic), Token)).Value;
        planned.Plan.IsValid.Should().BeTrue(string.Join("; ", planned.Plan.Errors.Select(static x => x.Message)));

        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await host.Search.StartAsync(planned, confirmed: true, Token)).Run;
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
        return (planned, matches, result);
    }

    private sealed class Saved
    {
        public string? FileName { get; private set; }

        public byte[] Bytes { get; private set; } = [];

        public Task<bool> DeliverAsync(string fileName, string mime, Stream content, CancellationToken cancellationToken)
        {
            FileName = fileName;
            using MemoryStream copy = new();
            content.CopyTo(copy);
            Bytes = copy.ToArray();
            return Task.FromResult(true);
        }
    }

    [KafkaFact]
    public async Task A_real_searchs_records_are_saved_as_both_formats_with_the_masks_in_place_and_audited()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin);
        await using LiveSearchHost host = LiveSearchHost.Create(
            fixture,
            "reader",
            options =>
            {
                options.Capabilities.ExportRecords = true;
                options.Topic(topic, t => t.Redact(r => r.Header("customer-email").Searchable()));
            });
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(host, topic);
        matches.Should().HaveCount(Records);

        foreach (KafkaExportFormat format in new[] { KafkaExportFormat.JsonLines, KafkaExportFormat.Csv })
        {
            Saved saved = new();

            KafkaExportOutcome outcome = await host.Export.ExportAsync(
                new KafkaExportInput(host.Entry, [topic], KafkaExportSource.Search, planned.Input.From, planned.Input.To, planned.Plan.AuditText, result.Records, result.Bytes, matches, format),
                saved.DeliverAsync,
                Token);

            outcome.Delivered.Should().BeTrue();
            outcome.Written.Should().Be(Records);
            string text = Encoding.UTF8.GetString(saved.Bytes);
            text.Should().NotContain(Sentinel, "the deny-listed header and the rule-masked header are masked in the file");
            text.Should().Contain("redacted by the built-in header deny-list").And.Contain("redacted by header customer-email");
            saved.FileName.Should().StartWith("kstudio-live-").And.EndWith(format == KafkaExportFormat.Csv ? "Z.csv" : "Z.jsonl");

            // One row per record: a JSON line each, or a CSV header and a row each.
            int rows = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Length;
            rows.Should().Be(format == KafkaExportFormat.Csv ? Records + 1 : Records);
        }

        host.Ring.Where(static x => x.Action == "ExportRecords").Should().HaveCount(2);
        host.Ring.Where(static x => x.Action == "ExportRecords").Should().OnlyContain(static x => x.Succeeded && !x.Message!.Contains(Sentinel, StringComparison.Ordinal));
        host.Logs.Entries.Where(static x => x.EventId.Id == 9606).Should().HaveCount(2);
    }

    [KafkaFact]
    public async Task A_production_cluster_never_exports_even_when_the_capability_is_granted()
    {
        await using LiveContractCluster admin = LiveContractCluster.Open(fixture);
        string topic = await SeedAsync(admin);
        await using LiveSearchHost host = LiveSearchHost.Create(fixture, "reader", options => options.Capabilities.ExportRecords = true, KafkaProfile.Production);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(host, topic);
        Saved saved = new();

        KafkaExportOutcome outcome = await host.Export.ExportAsync(
            new KafkaExportInput(host.Entry, [topic], KafkaExportSource.Search, planned.Input.From, planned.Input.To, planned.Plan.AuditText, result.Records, result.Bytes, matches, KafkaExportFormat.JsonLines),
            saved.DeliverAsync,
            Token);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Be("This cluster's profile does not allow exporting records.", "the visitor's text is neutral and names no option (#74)").And.NotContain("KafkaClusterBuilder");
        saved.FileName.Should().BeNull();
        host.Ring.Where(static x => x.Action == "ExportRecords").Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        host.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9609);
        host.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9606);
    }
}

[Collection(KafkaCollections.V39)]
public sealed class ExportLiveTests39(Kafka39Fixture fixture) : ExportLiveTests(fixture);

[Collection(KafkaCollections.V4)]
public sealed class ExportLiveTests4(Kafka4Fixture fixture) : ExportLiveTests(fixture);
