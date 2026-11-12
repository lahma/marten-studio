using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// The search service's gates and audit (D-UI decision 11, hard rule 5): the capability, the bounds and both policies are asked again at the start; a
/// request naming a refused topic is refused whole and names nothing; and the visitor's typed text reaches neither the ring, nor the log, nor a toast.
/// </summary>
public class KafkaSearchServiceTests
{
    private const string Sentinel = "S3NT1NEL-7c1d9e";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string All(StudioActionLogEntry entry) =>
        string.Join('\n', entry.User, entry.Action, entry.Target, entry.Message, entry.Capability, entry.ScopeLabel, entry.ModuleScope?.ToString());

    [Fact]
    public async Task StartAsync_asks_the_capability_and_both_policies_again()
    {
        await using KafkaSearchRig rig = new(studio: o => o.CapabilityAuthorizationPolicy = "kafka-search");
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", "c-1"));
        KafkaSearchService search = rig.Search();

        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k1'"), Ct);
        rig.Studio.Authorization.Calls.Clear();

        KafkaSearchStartResult start = await search.StartAsync(plan.Value, confirmed: false, Ct);

        await using KafkaSearchRun run = start.Should().BeOfType<KafkaSearchStartResult.Running>().Subject.Run;
        (_, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
        matches.Should().ContainSingle().Which.Offset.Should().Be(0);

        var asked = rig.Studio.Authorization.KafkaCalls.Select(static x => (x.Policy, x.Resource.Topic, x.Resource.Capability)).ToArray();
        asked.Should().Contain(("kafka-topics", null, null), "the cluster policy is asked about the cluster");
        asked.Should().Contain(("kafka-topics", "orders", null), "and about the topic");
        asked.Should().Contain(("kafka-search", "orders", "SearchRecords"), "and the capability policy about the topic with the capability named");

        // The capability switched off while the dialog waited: the start is refused where the operation is.
        rig.Studio.Options.Capabilities.SearchRecords = false;
        KafkaSearchStartResult refused = await search.StartAsync(plan.Value, confirmed: false, Ct);

        refused.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords");
        rig.Ring.Should().Contain(static x => !x.Succeeded && x.Capability == "SearchRecords" && x.Action == "SearchRecords");
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9602);
    }

    [Fact]
    public async Task A_policy_that_changed_while_the_dialog_waited_refuses_the_start()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct);

        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "orders");
        KafkaSearchStartResult start = await search.StartAsync(plan.Value, confirmed: true, Ct);

        start.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchNamesRefusedTopic);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task A_request_naming_a_refused_topic_is_refused_whole_and_names_nothing()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret.v1");
        rig.Topic("orders");
        rig.Topic("secret.v1");
        int callsBefore = rig.BrokerCalls;
        KafkaSearchService search = rig.Search();

        KafkaSearchPlanResult plan = await search.PlanAsync(rig.Input(["orders", "secret.v1"]), Ct);

        plan.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchNamesRefusedTopic).And.NotContain("secret");
        rig.BrokerCalls.Should().Be(callsBefore, "a refused request is not narrowed to the allowed topics and read: nothing is read at all");

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(KafkaStudioText.RefusedEntry, "the entry says nothing about why");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task Twenty_one_topics_are_refused()
    {
        await using KafkaSearchRig rig = new();
        int callsBefore = rig.BrokerCalls;
        string[] topics = [.. Enumerable.Range(0, 21).Select(static i => "t" + i)];

        KafkaSearchPlanResult plan = await rig.Search().PlanAsync(rig.Input(topics), Ct);

        plan.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be("A search reads 1 to 20 topics.");
        rig.BrokerCalls.Should().Be(callsBefore);
        rig.Ring.Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9601);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused_with_the_engines_sentence()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        KafkaSearchInput input = rig.Input(["orders"]) with { From = KafkaSearchRig.T0.AddHours(2) };

        KafkaSearchPlanResult plan = await rig.Search().PlanAsync(input, Ct);

        plan.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be("The window must start before it ends.");
    }

    [Fact]
    public async Task The_typed_text_never_reaches_the_ring_or_the_log()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email").Searchable())));
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("customer-email", Sentinel));
        rig.Produce("orders", 0, 20, "k2", "v2", ("customer-email", "someone-else"));
        KafkaSearchService search = rig.Search();

        // A valid search: a literal on a redacted (but searchable) header.
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], $"header.customer-email = '{Sentinel}'"), Ct);
        plan.Value.Plan.AuditText.Should().Contain("<redacted>").And.NotContain(Sentinel);

        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(plan.Value, confirmed: false, Ct)).Run;
        (KafkaScanResult result, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
        matches.Should().ContainSingle().Which.Headers.Single(static x => x.Name == "customer-email").Value.IsRedacted.Should().BeTrue();
        result.Matches.Should().Be(1);

        // A search that does not parse: contains on a redacted field is refused, and the audit records only the text's length.
        KafkaSearchPlanResult.Planned invalid = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], $"header.customer-email ~ '{Sentinel}'"), Ct);
        invalid.Value.Plan.IsValid.Should().BeFalse();
        invalid.Value.Plan.Errors.Select(static x => x.Message).Should().NotContain(static m => m.Contains(Sentinel, StringComparison.Ordinal));

        await run.DisposeAsync();

        rig.Ring.Should().NotBeEmpty();
        rig.Ring.Select(All).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal), "the ring holds the audit text, never the typed text");
        rig.Ring.Should().Contain(static x => x.Succeeded && x.Message!.Contains("predicate: header.customer-email = <redacted>", StringComparison.Ordinal));
        rig.Ring.Should().Contain(static x => !x.Succeeded && x.Message == KafkaStudioText.PredicateInvalid);
        rig.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal), "nor does a log line");
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9600 && x.Message.Contains("<redacted>", StringComparison.Ordinal));
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9601 && x.Message.Contains("did not parse", StringComparison.Ordinal));
        rig.Toasts.Messages.Select(static x => x.Message).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal), "nor a toast");
    }

    [Fact]
    public async Task A_scan_is_recorded_once_whichever_comes_first()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct);

        KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(plan.Value, confirmed: false, Ct)).Run;
        await KafkaSearchRig.DrainAsync(run);
        run.IsRecorded.Should().BeTrue();
        await run.DisposeAsync();
        await run.DisposeAsync();

        rig.Logs.Entries.Where(static x => x.EventId.Id == 9600).Should().ContainSingle();
        rig.Ring.Count(static x => x.Action == "SearchRecords" && x.Succeeded).Should().Be(1);
    }

    [Fact]
    public async Task A_page_that_is_disposed_mid_scan_still_leaves_its_entry()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct);

        KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(plan.Value, confirmed: false, Ct)).Run;
        await run.DisposeAsync();

        rig.Logs.Entries.Where(static x => x.EventId.Id is 9600 or 9601).Should().ContainSingle("the scan is recorded exactly once, by the disposal");
        rig.Ring.Count(static x => x.Action == "SearchRecords").Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_confirmation_records_not_confirmed()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct);

        await search.NotConfirmedAsync(plan.Value);

        rig.Ring.Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(static x => !x.Succeeded && x.Message == "not confirmed");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9601 && x.Message.Contains("not confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_owner_is_the_user_or_the_circuit()
    {
        await using KafkaSearchRig rig = new();

        KafkaScanOwner anonymous = await new KafkaOwnerKey(rig.Studio.Visitor).OwnerAsync();
        KafkaScanOwner anonymousAgain = await new KafkaOwnerKey(rig.Studio.Visitor).OwnerAsync();
        anonymous.Key.Should().StartWith("circuit:");
        anonymousAgain.Key.Should().StartWith("circuit:").And.NotBe(anonymous.Key, "a principal with no name is an owner of its own circuit");

        rig.Studio.Visitor.SignIn("ada");
        KafkaOwnerKey key = new(rig.Studio.Visitor);
        KafkaScanOwner first = await key.OwnerAsync();
        KafkaScanOwner other = await new KafkaOwnerKey(rig.Studio.Visitor).OwnerAsync();

        first.Key.Should().Be("user:ada");
        other.Key.Should().Be("user:ada", "two tabs of one user share the per-owner slot and the hourly budget");
    }

    [Fact]
    public async Task Every_stop_sentence_reads_as_the_design_has_it()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanResult.Planned plan = (KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'nope'"), Ct);
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(plan.Value, confirmed: false, Ct)).Run;

        (KafkaScanResult result, _) = await KafkaSearchRig.DrainAsync(run);

        KafkaSearchText.Stop(result, plan.Value.Plan.Limits).Should().Be("No match in the 1 record read from 1 partition.");
    }
}
