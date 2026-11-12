using System.Text;

using KStudio.Core.Export;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// The export service's gates and audit (AGENTS.md D45, D-M5 decision 2.7, hard rule 5): the capability (and the capability it needs), the cluster's
/// profile, the bounds, both policies for every topic and the payload grant are asked again at the click; any refusal refuses the whole export and
/// writes nothing; the audit is written once per call, on failure as well as success; and the typed text of a search never reaches it.
/// </summary>
public class KafkaExportServiceTests
{
    private const string Sentinel = "S3NT1NEL-export-9a41";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KafkaSearchRig Rig(
        bool payloads = false,
        bool searchOn = true,
        KafkaProfile? profile = null,
        Action<KafkaStudioOptions>? studio = null,
        Action<KafkaEngineOptions>? engine = null) =>
        new(
            engine,
            options =>
            {
                options.Capabilities.ExportRecords = true;
                studio?.Invoke(options);
            },
            searchOn,
            payloads: payloads,
            profile: profile);

    private static void Seed(KafkaSearchRig rig, string topic = "orders", int count = 3, string plain = "visible")
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, 10 + i, "k" + i, "{\"n\":" + i + "}", ("x-plain", plain), ("authorization", "Bearer " + Sentinel));
        }
    }

    /// <summary>Runs a search over <paramref name="topics" /> and returns what the page would hold: the plan, the matches and the result.</summary>
    private static async Task<(KafkaSearchPlanned Planned, List<KafkaMatch> Matches, KafkaScanResult Result)> SearchAsync(KafkaSearchRig rig, string[] topics, string? predicate = null)
    {
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(topics, predicate), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: true, Ct)).Run;
        (KafkaScanResult result, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
        return (planned, matches, result);
    }

    private static KafkaExportInput InputOf(
        KafkaSearchRig rig,
        KafkaSearchPlanned planned,
        List<KafkaMatch> matches,
        KafkaScanResult result,
        KafkaExportFormat format = KafkaExportFormat.JsonLines,
        string[]? topics = null) =>
        new(
            rig.Entry,
            topics ?? planned.Input.Topics,
            KafkaExportSource.Search,
            planned.Input.From,
            planned.Input.To,
            planned.Plan.AuditText,
            result.Records,
            result.Bytes,
            matches,
            format);

    /// <summary>A delivery that keeps what it was given.</summary>
    private sealed class Capture
    {
        public int Calls { get; private set; }

        public string? FileName { get; private set; }

        public string? Mime { get; private set; }

        public byte[] Bytes { get; private set; } = [];

        public bool Result { get; set; } = true;

        public Task<bool> DeliverAsync(string fileName, string mime, Stream content, CancellationToken cancellationToken)
        {
            Calls++;
            FileName = fileName;
            Mime = mime;
            using MemoryStream copy = new();
            content.CopyTo(copy);
            Bytes = copy.ToArray();
            return Task.FromResult(Result);
        }
    }

    private static StudioActionLogEntry[] Exports(KafkaSearchRig rig) => [.. rig.Ring.Where(static x => x.Action == "ExportRecords")];

    // ---- the file, and the one audit entry

    [Fact]
    public async Task An_export_delivers_the_file_and_audits_it_once()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"], "header.x-plain = 'visible'");
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeTrue();
        outcome.Written.Should().Be(3);
        capture.Calls.Should().Be(1);
        capture.Mime.Should().Be("application/x-ndjson");
        capture.FileName.Should().StartWith("kstudio-test-orders-").And.EndWith("Z.jsonl");
        Encoding.UTF8.GetString(capture.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(3);

        StudioActionLogEntry entry = Exports(rig).Should().ContainSingle("one entry per call, whatever the file holds").Which;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be("ExportRecords");
        entry.Target.Should().StartWith("export of orders (search ");
        entry.Message.Should().Contain("3 records").And.Contain("saved as JSON Lines").And.Contain("predicate: ");
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9606).Should().ContainSingle();
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9609);
    }

    [Fact]
    public async Task A_csv_export_is_the_same_gate_and_one_entry()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result, KafkaExportFormat.Csv), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeTrue();
        capture.FileName.Should().EndWith("Z.csv");
        capture.Bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Exports(rig).Should().ContainSingle().Which.Message.Should().Contain("saved as CSV");
    }

    [Fact]
    public async Task A_file_stops_at_the_clusters_limit_with_whole_rows_and_the_page_and_the_audit_say_how_many_were_left_out()
    {
        const long OneMiB = 1_048_576;
        await using KafkaSearchRig rig = Rig(payloads: true, engine: static o => o.Limits.MaxExportBytes = OneMiB);
        rig.Topic("big");
        string value = "\"" + new string('x', 100_000) + "\"";

        for (int i = 0; i < 20; i++)
        {
            rig.Produce("big", 0, 10 + i, "k" + i, value);
        }

        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["big"]);
        matches.Should().HaveCount(20);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeTrue();
        outcome.LeftOut.Should().BeGreaterThan(0);
        (outcome.Written + outcome.LeftOut).Should().Be(20);
        outcome.Text.Should().Be(KafkaStudioText.ExportLimited(outcome.Written, 20, OneMiB))
            .And.Contain("KafkaStudioOptions.Limits.MaxExportBytes");
        capture.Bytes.Length.Should().BeLessThanOrEqualTo((int) OneMiB);
        Encoding.UTF8.GetString(capture.Bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(outcome.Written,"a file never holds a cut row");
        Exports(rig).Single().Message.Should().Contain($"{outcome.LeftOut} left out at the size limit");
    }

    // ---- the gates

    [Fact]
    public async Task A_capability_that_is_off_refuses_before_anything_is_written()
    {
        await using KafkaSearchRig rig = Rig(studio: static o => o.Capabilities.ExportRecords = false);
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Contain("KafkaStudioOptions.Capabilities.ExportRecords");
        capture.Calls.Should().Be(0);
        Exports(rig).Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9602);
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9606);
    }

    [Fact]
    public async Task Export_needs_SearchRecords_and_the_refusal_names_it_first()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        rig.Studio.Options.Capabilities.SearchRecords = false;
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Text.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords").And.NotContain("ExportRecords");
        capture.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ReadOnly_refuses_an_export_naming_the_hosts_option()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        rig.Studio.Context.Options.ReadOnly = true;
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Text.Should().Contain("PostgresStudioOptions.ReadOnly");
        capture.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_production_cluster_never_exports_even_when_the_capability_is_granted()
    {
        await using KafkaSearchRig rig = Rig(profile: KafkaProfile.Production);
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();
        rig.Studio.Authorization.Calls.Clear();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Be(KafkaStudioText.ProfileForbids(KafkaStudioText.ExportingRecords, rig.Entry))
            .And.Contain("KafkaClusterBuilder.Profile").And.Contain("even when they are granted");
        capture.Calls.Should().Be(0, "nothing was written, so nothing was delivered");
        rig.Studio.Authorization.KafkaCalls.Should().BeEmpty("the profile is judged before a policy is asked");
        Exports(rig).Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9609).Should().ContainSingle();
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9606);
        rig.Export().ForbiddenByProfile(rig.Entry).Should().BeTrue();
    }

    [Fact]
    public async Task A_cluster_without_a_profile_and_a_development_one_may_export()
    {
        foreach (KafkaProfile? profile in new KafkaProfile?[] { null, KafkaProfile.Development, KafkaProfile.Test })
        {
            await using KafkaSearchRig rig = Rig(profile: profile);
            Seed(rig);
            (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);

            KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), new Capture().DeliverAsync, Ct);

            outcome.Delivered.Should().BeTrue($"{profile?.ToString() ?? "no profile"} forbids nothing");
        }
    }

    [Fact]
    public async Task A_cluster_policy_that_refuses_a_topic_refuses_the_whole_export()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig, "orders");
        Seed(rig, "secret.v1");
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders", "secret.v1"]);
        rig.Studio.Authorization.AllowKafka(static r => r.Topic != "secret.v1");
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Be(KafkaStudioText.RequestRefused).And.NotContain("secret");
        capture.Calls.Should().Be(0, "a request naming a refused topic is refused whole, never narrowed to the allowed one");
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9603);
        Exports(rig).Should().ContainSingle().Which.Message.Should().Be(KafkaStudioText.RefusedEntry);
    }

    [Theory]
    [InlineData("SearchRecords")]
    [InlineData("ExportRecords")]
    public async Task A_capability_policy_that_refuses_either_capability_on_a_topic_refuses_the_export(string refused)
    {
        await using KafkaSearchRig rig = Rig(studio: static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        rig.Studio.Authorization.AllowKafka(r => r.Capability != refused);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Text.Should().Be(KafkaStudioText.RequestRefused);
        capture.Calls.Should().Be(0);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task The_policies_are_asked_with_the_capability_named_for_every_topic()
    {
        await using KafkaSearchRig rig = Rig(studio: static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        rig.Studio.Authorization.Calls.Clear();

        await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), new Capture().DeliverAsync, Ct);

        var asked = rig.Studio.Authorization.KafkaCalls.Select(static x => (x.Policy, x.Resource.Topic, x.Resource.Capability)).ToArray();
        asked.Should().Contain(("kafka-topics", null, null));
        asked.Should().Contain(("kafka-topics", "orders", null));
        asked.Should().Contain(("kafka-search", "orders", "SearchRecords"));
        asked.Should().Contain(("kafka-search", "orders", "ExportRecords"));
    }

    [Fact]
    public async Task Records_that_are_not_the_sources_topics_are_refused_and_nothing_is_written()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig, "orders");
        Seed(rig, "payments");
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders", "payments"]);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result, topics: ["orders"]), capture.DeliverAsync, Ct);

        outcome.Text.Should().Be(KafkaStudioText.ExportRecordsNotFromTopics);
        capture.Calls.Should().Be(0);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9609);
    }

    [Fact]
    public async Task A_value_is_saved_from_one_record_of_the_record_page_only()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();

        KafkaExportOutcome several = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result, KafkaExportFormat.Value), capture.DeliverAsync, Ct);
        KafkaExportOutcome fromSearch = await rig.Export().ExportAsync(InputOf(rig, planned, [matches[0]], result, KafkaExportFormat.Value), capture.DeliverAsync, Ct);

        several.Text.Should().Be(KafkaStudioText.ExportValueFromOneRecord);
        fromSearch.Text.Should().Be(KafkaStudioText.ExportValueFromOneRecord, "the value alone is the record page's");
        capture.Calls.Should().Be(0);

        KafkaExportInput record = new(
            rig.Entry,
            ["orders"],
            KafkaExportSource.Record,
            null,
            null,
            KafkaStudioText.RecordExportAuditText,
            1,
            matches[0].ValueLength,
            [matches[0]],
            KafkaExportFormat.Value);
        KafkaExportOutcome saved = await rig.Export().ExportAsync(record, capture.DeliverAsync, Ct);

        saved.Delivered.Should().BeTrue();
        capture.FileName.Should().Contain("-orders-0-0-").And.EndWith("Z.json");
        capture.Mime.Should().Be("application/json");
        Encoding.UTF8.GetString(capture.Bytes).Should().Contain("\"n\"");
        Exports(rig).Should().Contain(static x => x.Target == "export of orders/0/0", "a record is audited by its place");
    }

    [Fact]
    public async Task An_export_of_no_records_is_refused()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, _, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, [], result), capture.DeliverAsync, Ct);

        outcome.Text.Should().Be(KafkaStudioText.ExportNothing);
        capture.Calls.Should().Be(0);
    }

    // ---- values

    [Fact]
    public async Task A_grant_that_went_down_since_the_records_were_read_refuses_the_export_whole_and_never_writes_them_as_envelope_rows()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        matches.Should().OnlyContain(static x => x.ReadWithPayload, "the search ran with the payload grant");
        rig.Studio.Options.Capabilities.ReadPayloads = false;
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Be(KafkaStudioText.ExportGrantChanged);
        capture.Calls.Should().Be(0);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9609);
    }

    [Fact]
    public async Task A_payload_policy_that_now_refuses_a_topic_refuses_the_export_whole()
    {
        await using KafkaSearchRig rig = Rig(payloads: true, studio: static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        Capture capture = new();

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Text.Should().Be(KafkaStudioText.ExportGrantChanged);
        capture.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_file_of_values_says_so_in_the_audit()
    {
        await using KafkaSearchRig rig = Rig(payloads: true);
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);

        await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), new Capture().DeliverAsync, Ct);

        Exports(rig).Single().Message.Should().Contain("values included");
        rig.Logs.Entries.Single(static x => x.EventId.Id == 9606).Message.Should().Contain("; values included");
    }

    // ---- delivery

    [Fact]
    public async Task A_file_that_did_not_reach_the_browser_is_recorded_as_not_delivered()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new() { Result = false };

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        outcome.Delivered.Should().BeFalse();
        outcome.Text.Should().Be(KafkaStudioText.ExportNotDelivered);
        StudioActionLogEntry entry = Exports(rig).Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(KafkaStudioText.ExportNotDelivered);
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9606);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9609);
    }

    [Fact]
    public async Task A_circuit_that_went_away_while_the_file_was_sent_is_a_file_that_did_not_arrive()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(
            InputOf(rig, planned, matches, result),
            static (_, _, _, _) => throw new Microsoft.JSInterop.JSDisconnectedException("gone"),
            Ct);

        outcome.Delivered.Should().BeFalse();
        Exports(rig).Should().ContainSingle().Which.Message.Should().Be(KafkaStudioText.ExportNotDelivered);
    }

    [Fact]
    public async Task A_delivery_that_fails_is_recorded_once_and_the_export_says_so()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);

        KafkaExportOutcome outcome = await rig.Export().ExportAsync(
            InputOf(rig, planned, matches, result),
            static (_, _, _, _) => throw new FormatException("boom"),
            Ct);

        outcome.Text.Should().Be(KafkaStudioText.ExportFailed);
        Exports(rig).Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    // ---- two at a time

    [Fact]
    public async Task A_third_export_at_once_is_refused_and_not_queued()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        KafkaExportGate gate = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        TaskCompletionSource twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<bool> Hold(string name, string mime, Stream content, CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                twoStarted.TrySetResult();
            }

            await release.Task.WaitAsync(token);
            return true;
        }

        Task<KafkaExportOutcome> first = rig.Export(gate).ExportAsync(InputOf(rig, planned, matches, result), Hold, Ct);
        Task<KafkaExportOutcome> second = rig.Export(gate).ExportAsync(InputOf(rig, planned, matches, result), Hold, Ct);
        await twoStarted.Task.WaitAsync(Ct);

        Capture capture = new();
        KafkaExportOutcome third = await rig.Export(gate).ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        third.Delivered.Should().BeFalse();
        third.Text.Should().Be(KafkaStudioText.ExportsBusy);
        capture.Calls.Should().Be(0);

        release.SetResult();
        (await first).Delivered.Should().BeTrue();
        (await second).Delivered.Should().BeTrue();

        KafkaExportOutcome after = await rig.Export(gate).ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);
        after.Delivered.Should().BeTrue("the places were given back");
    }

    // ---- what the audit never holds

    [Fact]
    public async Task The_audit_and_the_log_carry_the_plans_text_with_a_redacted_literal_and_never_the_typed_one()
    {
        await using KafkaSearchRig rig = Rig(engine: static o => o.Topic("orders", static t => t.Redact(static r => r.Header("x-plain").Searchable())));
        Seed(rig, plain: Sentinel);
        string typed = "header.x-plain = '" + Sentinel + "'";
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"], typed);
        planned.Plan.AuditText.Should().Contain("<redacted>").And.NotContain(Sentinel);

        await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), new Capture().DeliverAsync, Ct);

        StudioActionLogEntry entry = Exports(rig).Single();
        string all = string.Join('\n', entry.Target, entry.Message, entry.ScopeLabel);
        all.Should().Contain("<redacted>").And.NotContain(Sentinel);
        rig.Logs.Entries.Single(static x => x.EventId.Id == 9606).Message.Should().Contain("<redacted>").And.NotContain(Sentinel);
    }

    [Fact]
    public async Task The_file_holds_no_secret_a_header_rule_masked_and_the_deny_list_header_is_masked_on_the_row()
    {
        await using KafkaSearchRig rig = Rig();
        Seed(rig);
        (KafkaSearchPlanned planned, List<KafkaMatch> matches, KafkaScanResult result) = await SearchAsync(rig, ["orders"]);
        Capture capture = new();

        await rig.Export().ExportAsync(InputOf(rig, planned, matches, result), capture.DeliverAsync, Ct);

        string text = Encoding.UTF8.GetString(capture.Bytes);
        text.Should().NotContain(Sentinel, "the authorization header is on the built-in deny-list");
        text.Should().Contain("redacted by the built-in header deny-list");
    }
}
