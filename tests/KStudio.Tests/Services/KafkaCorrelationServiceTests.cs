using KStudio.Core.Correlation;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Services;
using KStudio.Tests.Support;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// The related-records service's gates and audit (D42, decision 3 of D-CORR, hard rule 5): the capability, the bounds and both policies for every topic
/// are asked again at the start; a chosen set naming a refused topic is refused whole; a default set leaves a refused topic out and names nothing; one
/// scan is one audit entry; and a value reaches the ring and the log only inside the plan's audit text, where a literal any topic redacts is hidden.
/// </summary>
public class KafkaCorrelationServiceTests
{
    private const string Sentinel = "S3NT1NEL-d41c93";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CorrelationKeySet Keys(string value, string kind = "correlation") => CorrelationKeySet.Seed(kind, value);

    private static string All(StudioActionLogEntry entry) =>
        string.Join('\n', entry.User, entry.Action, entry.Target, entry.Message, entry.Capability, entry.ScopeLabel, entry.ModuleScope?.ToString());

    private static async Task<(KafkaScanResult Result, List<KafkaMatch> Matches)> RunAsync(KafkaCorrelationService related, KafkaRelatedPlanned planned)
    {
        KafkaRelatedStartResult start = await related.StartAsync(planned, confirmed: true, Ct);
        await using KafkaSearchRun run = start.Should().BeOfType<KafkaRelatedStartResult.Running>().Subject.Run;
        return await KafkaSearchRig.DrainAsync(run);
    }

    // ------------------------------------------------------------------------------------------------
    // The anchor
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_anchors_keys_are_the_values_it_shows_and_it_is_opened_once_with_its_own_audit()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", "c-1"), ("id", "m-1"), ("tenant-id", "acme"));

        KafkaRelatedAnchorResult opened = await rig.Correlation().OpenAnchorAsync(rig.Entry, "orders", 0, 0, Ct);

        KafkaRelatedAnchorResult.Opened anchor = opened.Should().BeOfType<KafkaRelatedAnchorResult.Opened>().Subject;
        anchor.Keys.Of("correlation").Select(static x => x.Value).Should().Equal("c-1");
        anchor.Keys.Of("message").Select(static x => x.Value).Should().Equal("m-1");
        anchor.Keys.Values.Select(static x => x.Value).Should().NotContain("acme", "tenant-id is a scope, never a key");
        rig.Ring.Should().ContainSingle(static x => x.Action == "ViewRecord", "the anchor is the record page's read, with its own entry");
    }

    [Fact]
    public async Task A_masked_header_is_no_key_and_its_value_is_nowhere()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("correlation-id").Searchable())));
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", Sentinel), ("id", "m-1"));

        KafkaRelatedAnchorResult.Opened anchor = (KafkaRelatedAnchorResult.Opened) await rig.Correlation().OpenAnchorAsync(rig.Entry, "orders", 0, 0, Ct);

        anchor.Keys.Of("correlation").Should().BeEmpty();
        anchor.Keys.Values.Select(static x => x.Value).Should().NotContain(Sentinel);
        rig.Ring.Select(All).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_json_path_key_is_taken_only_from_a_value_whose_view_was_recorded()
    {
        const string Value = """{"customer":{"id":"cust-9"}}""";
        Action<KafkaEngineOptions> engine = o => o.Topic("orders", t => t.Correlation(c => c.JsonPath("$.customer.id").As("customer")));

        await using KafkaSearchRig on = new(engine: engine, payloads: true);
        on.Topic("orders");
        on.Produce("orders", 0, 10, "k1", Value);

        KafkaRelatedAnchorResult.Opened shown = (KafkaRelatedAnchorResult.Opened) await on.Correlation().OpenAnchorAsync(on.Entry, "orders", 0, 0, Ct);
        shown.Keys.Of("customer").Select(static x => x.Value).Should().Equal("cust-9");
        on.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9605, "the value was shown, so its view is recorded before a key is taken from it");

        await using KafkaSearchRig off = new(engine: engine, payloads: false);
        off.Topic("orders");
        off.Produce("orders", 0, 10, "k1", Value);

        KafkaRelatedAnchorResult.Opened hidden = (KafkaRelatedAnchorResult.Opened) await off.Correlation().OpenAnchorAsync(off.Entry, "orders", 0, 0, Ct);
        hidden.Keys.Of("customer").Should().BeEmpty("without ReadPayloads the value was not read");
        hidden.Fields.Should().NotContain(static x => x.Source == CorrelationSource.JsonPath);
    }

    [Fact]
    public async Task A_refused_anchor_topic_reads_like_a_missing_one()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "orders");
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", "c-1"));

        (await rig.Correlation().OpenAnchorAsync(rig.Entry, "orders", 0, 0, Ct)).Should().BeOfType<KafkaRelatedAnchorResult.NotAvailable>();
    }

    // ------------------------------------------------------------------------------------------------
    // The topics
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_set_is_the_declared_topics_the_visitor_may_read_the_anchors_first_and_a_refused_one_is_absent()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("payments", _ => { });
            o.Topic("orders", _ => { });
            o.Topic("secret.v1", _ => { });
        });
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret.v1");
        rig.Topic("orders");
        rig.Topic("payments");
        rig.Topic("secret.v1");
        rig.Topic("undeclared");

        IReadOnlyList<string>? topics = await rig.Correlation().ReadableTopicsAsync(rig.Entry, "payments", null, null, Ct);

        topics.Should().Equal("payments", "orders");
        rig.Ring.Should().BeEmpty("a list names nothing and records nothing");
        rig.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_seed_lists_only_the_topics_with_a_field_of_its_kind_and_store()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("orders", t => t.Correlation(c => c.Key().Document("Order")));
            o.Topic("main-only", t => t.Correlation(c => c.Key().Document("Order", "main")));
            o.Topic("other-store", t => t.Correlation(c => c.Key().Document("Order", "audit")));
            o.Topic("other-type", t => t.Correlation(c => c.Key().Document("Invoice")));
            o.Topic("plain", _ => { });
        });
        foreach (string name in new[] { "orders", "main-only", "other-store", "other-type", "plain" })
        {
            rig.Topic(name);
        }

        IReadOnlyList<string>? topics = await rig.Correlation().ReadableTopicsAsync(rig.Entry, null, "document:Order", "main", Ct);

        topics.Should().Equal("main-only", "orders");
    }

    // ------------------------------------------------------------------------------------------------
    // The gates
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_asks_the_capability_and_both_policies_again()
    {
        await using KafkaSearchRig rig = new(studio: o => o.CapabilityAuthorizationPolicy = "kafka-search");
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders"], Keys("c-1")), Ct)).Value;
        rig.Studio.Authorization.Calls.Clear();

        (_, List<KafkaMatch> matches) = await RunAsync(related, planned);
        matches.Should().ContainSingle().Which.Offset.Should().Be(0);

        var asked = rig.Studio.Authorization.KafkaCalls.Select(static x => (x.Policy, x.Resource.Topic, x.Resource.Capability)).ToArray();
        asked.Should().Contain(("kafka-topics", null, null), "the cluster policy is asked about the cluster");
        asked.Should().Contain(("kafka-topics", "orders", null), "and about the topic");
        asked.Should().Contain(("kafka-search", "orders", "SearchRecords"), "and the capability policy about the topic with the capability named");

        // The capability switched off while the dialog waited: the start is refused where the operation is.
        rig.Studio.Options.Capabilities.SearchRecords = false;
        KafkaRelatedStartResult refused = await related.StartAsync(planned, confirmed: true, Ct);

        refused.Should().BeOfType<KafkaRelatedStartResult.Refused>().Which.Text.Should().Be("Searching records is not available here.");
        rig.Ring.Should().Contain(static x => !x.Succeeded && x.Capability == "SearchRecords" && x.Action == "SearchRecords");
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9602);
    }

    [Fact]
    public async Task A_policy_that_changed_while_the_dialog_waited_refuses_the_start()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k1", "v1", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();
        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders"], Keys("c-1")), Ct)).Value;

        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "orders");
        KafkaRelatedStartResult start = await related.StartAsync(planned, confirmed: true, Ct);

        start.Should().BeOfType<KafkaRelatedStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchNamesRefusedTopic);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task A_chosen_set_naming_a_refused_topic_is_refused_whole_and_names_nothing()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret.v1");
        rig.Topic("orders");
        rig.Topic("secret.v1");
        int callsBefore = rig.BrokerCalls;

        KafkaRelatedPlanResult plan = await rig.Correlation().PlanAsync(rig.RelatedInput(["orders", "secret.v1"], Keys("c-1")), Ct);

        plan.Should().BeOfType<KafkaRelatedPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchNamesRefusedTopic).And.NotContain("secret");
        rig.BrokerCalls.Should().Be(callsBefore, "a refused request is not narrowed to the allowed topics and read: nothing is read at all");

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(KafkaStudioText.RefusedEntry);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task The_bounds_are_refused_before_any_policy_is_asked()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        KafkaCorrelationService related = rig.Correlation();
        string[] twentyOne = [.. Enumerable.Range(0, 21).Select(static i => "t" + i)];

        (await related.PrepareAsync(rig.RelatedInput(twentyOne, Keys("c-1")), Ct)).Should().BeOfType<KafkaRelatedPrepareResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchTopicCount);
        (await related.PrepareAsync(rig.RelatedInput(["orders"], CorrelationKeySet.Empty), Ct)).Should().BeOfType<KafkaRelatedPrepareResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RelatedNoKeys);
        (await related.PrepareAsync(rig.RelatedInput(["orders"], Keys(new string('x', 1_001))), Ct)).Should().BeOfType<KafkaRelatedPrepareResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RelatedValueTooLong);
        (await related.PrepareAsync(rig.RelatedInput(["orders"], Keys("c-1")) with { From = KafkaSearchRig.T0.AddHours(2) }, Ct)).Should().BeOfType<KafkaRelatedPrepareResult.Refused>().Which.Text.Should().Be("The window must start before it ends.");

        rig.Studio.Authorization.Calls.Should().BeEmpty("a bound is checked before any policy is asked");
        rig.Ring.Should().HaveCount(4).And.OnlyContain(static x => !x.Succeeded);
    }

    // ------------------------------------------------------------------------------------------------
    // The scan
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_topic_is_searched_by_its_own_fields_and_only_the_children_are_found()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("orders.in", _ => { });
            o.Topic("orders.out", t => t.Correlation(c => c.Header("x-flow").As("correlation")));
            o.Topic("unrelated", _ => { });
        });
        rig.Topic("orders.in");
        rig.Topic("orders.out");
        rig.Topic("unrelated");
        rig.Produce("orders.in", 0, 10, "k", "v", ("correlation-id", "c-1"));
        rig.Produce("orders.out", 0, 11, "k", "v", ("x-flow", "c-1"));
        rig.Produce("orders.out", 0, 12, "k", "v", ("x-flow", "c-2"));
        rig.Produce("unrelated", 0, 13, "k", "v", ("correlation-id", "c-2"));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders.in", "orders.out", "unrelated"], Keys("c-1"), "orders.in"), Ct)).Value;
        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(related, planned);

        matches.Select(static x => (x.Topic, x.Offset)).Should().BeEquivalentTo([("orders.in", 0L), ("orders.out", 0L)]);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        planned.Lanes.Select(static x => x.Query.PredicateText).Should().OnlyContain(static x => x != null);
    }

    [Fact]
    public async Task One_scan_is_one_ring_entry_and_one_9600_and_a_plan_alone_records_nothing()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Topic("payments");
        rig.Produce("orders", 0, 10, "k", "v", ("correlation-id", "c-1"));
        rig.Produce("payments", 0, 11, "k", "v", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders", "payments"], Keys("c-1")), Ct)).Value;
        rig.Ring.Should().BeEmpty("a plan is not a scan");

        await RunAsync(related, planned);

        StudioActionLogEntry entry = rig.Ring.Should().ContainSingle().Which;
        entry.Succeeded.Should().BeTrue();
        entry.Action.Should().Be("SearchRecords");
        entry.Target.Should().StartWith("records related to orders/0/0, 2 topics, ");
        entry.Message.Should().Contain("2 records").And.Contain("predicate: orders, payments: header.correlation-id = 'c-1'", "topics that share a search are grouped");
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9600);
    }

    [Fact]
    public async Task A_literal_redacted_on_one_topic_is_hidden_in_the_whole_audit_entry_and_the_lane_text()
    {
        await using KafkaSearchRig rig = new(engine: o =>
        {
            o.Topic("a", t => t.Redact(r => r.Header("correlation-id").Searchable()));
            o.Topic("b", _ => { });
        });
        rig.Topic("a");
        rig.Topic("b");
        rig.Produce("a", 0, 10, "k", "v", ("correlation-id", Sentinel));
        rig.Produce("b", 0, 11, "k", "v", ("correlation-id", Sentinel));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPrepared prepared = ((KafkaRelatedPrepareResult.Prepared) await related.PrepareAsync(rig.RelatedInput(["a", "b"], Keys(Sentinel), "b"), Ct)).Value;
        prepared.Lanes.Should().OnlyContain(static x => x.ShownText != null && x.ShownText.Contains("<redacted>") && !x.ShownText.Contains(Sentinel));

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["a", "b"], Keys(Sentinel), "b"), Ct)).Value;
        planned.Plan.AuditText.Should().Contain("<redacted>").And.NotContain(Sentinel);

        (_, List<KafkaMatch> matches) = await RunAsync(related, planned);
        matches.Should().HaveCount(2, "a searchable redacted field still finds its records");

        rig.Ring.Select(All).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
        rig.Logs.Entries.Select(static x => x.Message).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_topic_with_nothing_to_compare_is_a_lane_without_a_scan_and_the_rest_still_run()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("bare", t => t.Correlation(c => c.WithoutDefaults())));
        rig.Topic("orders");
        rig.Topic("bare");
        rig.Produce("orders", 0, 10, "k", "v", ("correlation-id", "c-1"));
        rig.Produce("bare", 0, 11, "k", "v", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders", "bare"], Keys("c-1")), Ct)).Value;

        planned.Lanes.Single(static x => x.Topic.Value == "bare").IsScanned.Should().BeFalse();
        planned.Plan.Request.Topics.Select(static x => x.Value).Should().Equal("orders");

        (_, List<KafkaMatch> matches) = await RunAsync(related, planned);
        matches.Should().ContainSingle().Which.Topic.Should().Be("orders");
    }

    [Fact]
    public async Task Nothing_to_compare_anywhere_is_refused_and_recorded_as_not_run()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("bare", t => t.Correlation(c => c.WithoutDefaults())));
        rig.Topic("bare");

        KafkaRelatedPlanResult plan = await rig.Correlation().PlanAsync(rig.RelatedInput(["bare"], Keys("c-1")), Ct);

        plan.Should().BeOfType<KafkaRelatedPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.RelatedNothingToSearch);
        rig.Ring.Should().ContainSingle(static x => !x.Succeeded);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9601);
    }

    [Fact]
    public async Task A_topic_the_broker_refuses_is_a_lane_with_the_engines_sentence_and_the_rest_are_read()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Topic("hidden");
        rig.Produce("orders", 0, 10, "k", "v", ("correlation-id", "c-1"));
        rig.Produce("hidden", 0, 11, "k", "v", ("correlation-id", "c-1"));
        rig.Cluster.Deny(KafkaAclOperation.Describe, KafkaResourceType.Topic, "hidden");
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders", "hidden"], Keys("c-1")), Ct)).Value;
        (KafkaScanResult result, List<KafkaMatch> matches) = await RunAsync(related, planned);

        matches.Should().ContainSingle().Which.Topic.Should().Be("orders");
        result.TopicRefusals.Should().ContainSingle(static x => x.Topic == "hidden");

        IReadOnlyList<KafkaTimelineLane> lanes = KafkaRelatedView.Lanes(planned.Lanes, matches, result, running: false, started: true, anchorTopic: "orders");
        lanes.Single(static x => x.Topic == "hidden").State.Should().StartWith("not readable: ");
        lanes.Select(static x => x.Topic).Should().Equal("orders", "hidden");
    }

    // ------------------------------------------------------------------------------------------------
    // The grant per topic
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_payload_grant_is_asked_per_topic_and_a_path_is_searched_only_where_it_is_granted()
    {
        Action<KafkaEngineOptions> engine = o =>
        {
            o.Topic("allowed", t => t.Correlation(c => c.JsonPath("$.customer.id").As("customer")));
            o.Topic("refused", t => t.Correlation(c => c.JsonPath("$.customer.id").As("customer")));
        };

        await using KafkaSearchRig rig = new(engine: engine, payloads: true, studio: o => o.CapabilityAuthorizationPolicy = "kafka-payloads");
        rig.Studio.Authorization.AllowKafka(static resource => !(resource.Topic == "refused" && resource.Capability == "ReadPayloads"));
        rig.Topic("allowed");
        rig.Topic("refused");
        rig.Produce("allowed", 0, 10, "k", """{"customer":{"id":"cust-9"}}""", ("correlation-id", "c-1"));
        rig.Produce("refused", 0, 11, "k", """{"customer":{"id":"cust-9"}}""", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();

        CorrelationKeySet keys = CorrelationKeySet.Seed("customer", "cust-9");
        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["allowed", "refused"], keys, "allowed"), Ct)).Value;

        KafkaRelatedLane allowed = planned.Lanes.Single(static x => x.Topic.Value == "allowed");
        KafkaRelatedLane refused = planned.Lanes.Single(static x => x.Topic.Value == "refused");

        allowed.Grant.Should().Be(KafkaReadGrant.Envelope | KafkaReadGrant.Payload);
        allowed.Query.ReadsValues.Should().BeTrue();
        refused.Grant.Should().Be(KafkaReadGrant.Envelope);
        refused.IsScanned.Should().BeFalse("its only field for this key is a path it may not read");
        refused.Query.Notes.Should().Contain(static x => x.Contains("needs `ReadPayloads`", StringComparison.Ordinal));
        planned.Plan.Request.Grant.Should().Be(KafkaReadGrant.Envelope | KafkaReadGrant.Payload);

        (_, List<KafkaMatch> matches) = await RunAsync(related, planned);
        matches.Should().ContainSingle().Which.Topic.Should().Be("allowed");
        rig.Ring.Should().ContainSingle().Which.Capability.Should().Be("ReadPayloads", "values were read by this scan");
    }

    [Fact]
    public async Task Without_the_capability_a_path_is_never_searched_and_nothing_reads_a_value()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Correlation(c => c.JsonPath("$.customer.id").As("customer"))), payloads: false);
        rig.Topic("orders");
        rig.Produce("orders", 0, 10, "k", """{"customer":{"id":"cust-9"}}""", ("correlation-id", "c-1"));
        KafkaCorrelationService related = rig.Correlation();

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders"], Keys("c-1")), Ct)).Value;

        planned.Lanes.Single().Grant.Should().Be(KafkaReadGrant.Envelope);
        planned.Plan.Request.Grant.Should().Be(KafkaReadGrant.Envelope);
        planned.Plan.ReadsValues.Should().BeFalse();

        (_, List<KafkaMatch> matches) = await RunAsync(related, planned);
        matches.Should().ContainSingle().Which.ReadWithPayload.Should().BeFalse();
        rig.Ring.Should().ContainSingle().Which.Capability.Should().Be("SearchRecords");
    }

    [Fact]
    public async Task Continue_keeps_each_topics_own_search()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Limits.MaxMatches = 1);
        rig.Topic("orders");
        rig.Topic("payments");
        for (int i = 0; i < 3; i++)
        {
            rig.Produce("orders", 0, 10 + i, "k", "v", ("correlation-id", "c-1"));
            rig.Produce("payments", 0, 20 + i, "k", "v", ("correlation-id", "c-1"));
        }

        KafkaCorrelationService related = rig.Correlation();
        KafkaRelatedPlanned first = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(rig.RelatedInput(["orders", "payments"], Keys("c-1")), Ct)).Value;
        (KafkaScanResult firstResult, List<KafkaMatch> firstMatches) = await RunAsync(related, first);

        firstResult.StopReason.Should().Be(KafkaScanStopReason.MatchLimit);
        firstResult.Cursor.IsEmpty.Should().BeFalse();

        KafkaRelatedPlanned next = ((KafkaRelatedPlanResult.Planned) await related.PlanAsync(first.Input with { ContinueFrom = firstResult.Cursor }, Ct)).Value;
        (_, List<KafkaMatch> nextMatches) = await RunAsync(related, next);

        next.Plan.Request.PerTopic.Should().NotBeNull().And.HaveCount(2);
        firstMatches.Concat(nextMatches).Select(static x => (x.Topic, x.Offset)).Distinct().Should().HaveCountGreaterThan(firstMatches.Count, "the continued scan read on, with the same searches");
    }

    [Fact]
    public async Task A_value_view_is_asked_only_when_the_anchors_topic_has_a_path_field()
    {
        // The first ReadPayloads question (the record page's grant) is allowed; any later one is refused. A view that nobody takes a key from must not ask.
        int asked = 0;
        Action<KafkaEngineOptions> withPath = o => o.Topic("orders", t => t.Correlation(c => c.JsonPath("$.customer.id").As("customer")));
        Action<KafkaEngineOptions> without = o => o.Topic("orders", _ => { });
        const string Value = """{"customer":{"id":"cust-9"}}""";

        await using KafkaSearchRig plain = new(engine: without, payloads: true, studio: o => o.CapabilityAuthorizationPolicy = "kafka-payloads");
        plain.Studio.Authorization.AllowKafka(resource => resource.Capability != "ReadPayloads" || Interlocked.Increment(ref asked) == 1);
        plain.Topic("orders");
        plain.Produce("orders", 0, 10, "k", Value, ("correlation-id", "c-1"));

        KafkaRelatedAnchorResult.Opened opened = (KafkaRelatedAnchorResult.Opened) await plain.Correlation().OpenAnchorAsync(plain.Entry, "orders", 0, 0, Ct);

        opened.Keys.Of("correlation").Should().ContainSingle();
        asked.Should().Be(1, "no field of the topic is a path, so nothing asks a second time");
        plain.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9603, "and nothing is refused or recorded as refused");

        asked = 0;
        await using KafkaSearchRig pathed = new(engine: withPath, payloads: true, studio: o => o.CapabilityAuthorizationPolicy = "kafka-payloads");
        pathed.Studio.Authorization.AllowKafka(resource => resource.Capability != "ReadPayloads" || Interlocked.Increment(ref asked) == 1);
        pathed.Topic("orders");
        pathed.Produce("orders", 0, 10, "k", Value, ("correlation-id", "c-1"));

        KafkaRelatedAnchorResult.Opened refused = (KafkaRelatedAnchorResult.Opened) await pathed.Correlation().OpenAnchorAsync(pathed.Entry, "orders", 0, 0, Ct);

        asked.Should().BeGreaterThan(1, "a path field makes the view a thing the service asks for");
        refused.Keys.Of("customer").Should().BeEmpty("the refused view takes no key from the value");
    }

    [Fact]
    public async Task A_seeds_store_reaches_the_query_of_each_topic()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Correlation(c =>
        {
            c.Key().Document("Order", "a");
            c.Header("x-b").Document("Order", "b");
        })));
        rig.Topic("orders");
        KafkaRelatedInput input = rig.RelatedInput(["orders"], Keys("o-1", "document:Order")) with { Subject = KafkaRelatedSubject.Seed("document", "b") };

        KafkaRelatedPrepared prepared = ((KafkaRelatedPrepareResult.Prepared) await rig.Correlation().PrepareAsync(input, Ct)).Value;

        prepared.Lanes.Single().Query.PredicateText.Should().Be("header.x-b = 'o-1'");
    }
}
