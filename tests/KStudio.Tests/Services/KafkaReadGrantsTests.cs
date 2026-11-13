using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Services;

/// <summary>
/// What a visitor may read of a record's value (D-PAYLOAD decision 1.2): the envelope, or the payload as well when <c>ReadPayloads</c> is on and the
/// capability policy passes it for every topic. Only a search that reads values is refused for it, and it is refused whole.
/// </summary>
public class KafkaReadGrantsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Seed(KafkaSearchRig rig, params string[] topics)
    {
        foreach (string topic in topics)
        {
            rig.Topic(topic);
            rig.Produce(topic, 0, 10, "k1", "v1", ("correlation-id", "c-1"));
        }
    }

    private static KafkaReadGrants Grants(KafkaSearchRig rig) =>
        new(rig.Studio.Context.Services.GetRequiredService<KafkaCapabilityGuard>(), rig.Studio.KafkaAuthorization);

    private static KafkaTopicName[] Names(params string[] topics) => [.. topics.Select(static x => KafkaTopicName.TryCreate(x, out KafkaTopicName name) ? name : throw new InvalidOperationException(x))];

    [Fact]
    public async Task Payload_needs_the_capability_and_the_policy_for_every_topic()
    {
        await using KafkaSearchRig on = new(payloads: true);
        Seed(on, "orders", "payments");

        KafkaGrantDecision granted = await Grants(on).ForSearchAsync(on.Entry, Names("orders", "payments"), readsValues: false, Ct);

        granted.Grant.Should().Be(KafkaReadGrant.Envelope | KafkaReadGrant.Payload);
        granted.AccessFor("orders").Should().Be(KafkaValueAccess.Granted);

        await using KafkaSearchRig off = new(payloads: false);
        Seed(off, "orders");

        KafkaGrantDecision envelope = await Grants(off).ForSearchAsync(off.Entry, Names("orders"), readsValues: false, Ct);

        envelope.Grant.Should().Be(KafkaReadGrant.Envelope);
        envelope.AccessFor("orders").Should().Be(KafkaValueAccess.CapabilityOff);
        off.Studio.Authorization.KafkaCalls.Should().NotContain(static x => x.Resource.Capability == "ReadPayloads", "the policy is not asked about a capability that is off");
    }

    [Fact]
    public async Task A_value_search_naming_a_topic_that_refuses_values_is_refused_whole_and_names_nothing()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Studio.Authorization.AllowKafka(static r => !(r.Capability == "ReadPayloads" && r.Topic == "secret.v1"));
        Seed(rig, "orders", "secret.v1");
        int callsBefore = rig.BrokerCalls;

        KafkaSearchPlanResult plan = await rig.Search().PlanAsync(rig.Input(["orders", "secret.v1"], "value ~ 'v'"), Ct);

        plan.Should().BeOfType<KafkaSearchPlanResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchValuesRefusedTopic).And.NotContain("secret");
        rig.BrokerCalls.Should().Be(callsBefore, "a refused search reads nothing, not even a window");
        rig.Ring.Should().ContainSingle().Which.Should().Match<PostgresStudio.Services.StudioActionLogEntry>(
            static x => !x.Succeeded && x.Capability == "ReadPayloads" && x.Message == KafkaStudioText.RefusedEntry);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task A_key_search_runs_under_Envelope_when_a_topic_refuses_values_and_records_nothing()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Studio.Authorization.AllowKafka(static r => !(r.Capability == "ReadPayloads" && r.Topic == "secret.v1"));
        Seed(rig, "orders", "secret.v1");

        KafkaSearchPlanResult plan = await rig.Search().PlanAsync(rig.Input(["orders", "secret.v1"], "key = 'k1'"), Ct);

        KafkaSearchPlanned planned = plan.Should().BeOfType<KafkaSearchPlanResult.Planned>().Subject.Value;
        planned.Grant.Grant.Should().Be(KafkaReadGrant.Envelope);
        planned.Grant.TopicsWithoutValues.Should().Equal("secret.v1");
        planned.Grant.AccessFor("secret.v1").Should().Be(KafkaValueAccess.PolicyRefused);
        planned.Grant.AccessFor("orders").Should().Be(KafkaValueAccess.SearchWithoutValues);
        rig.Ring.Should().BeEmpty("asking whether values may be kept, and being told no, refuses nothing");
        rig.Logs.Entries.Select(static x => x.EventId.Id).Should().NotContain([9602, 9603]);
    }

    [Fact]
    public async Task ReadOnly_refuses_a_value_search_in_one_neutral_sentence_and_the_refusal_names_the_hosts_option()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        Seed(rig, "orders");
        rig.Studio.Context.Options.ReadOnly = true;

        KafkaGrantDecision decision = await Grants(rig).ForSearchAsync(rig.Entry, Names("orders"), readsValues: true, Ct);

        decision.Denied.Should().NotBeNull();
        decision.Refusal.Should().Be("Reading values is not available here.");
        decision.Denied!.Message.Should().Contain("ReadPayloads").And.Contain("PostgresStudioOptions.ReadOnly", "the audit and the log still name the option");
        decision.Grant.Should().Be(KafkaReadGrant.Envelope);

        KafkaGrantDecision quiet = await Grants(rig).ForSearchAsync(rig.Entry, Names("orders"), readsValues: false, Ct);
        quiet.Refusal.Should().BeNull("a search that reads no value is not refused for values");
        quiet.CapabilityOff.Should().BeTrue();
    }

    [Fact]
    public async Task The_capability_policy_is_asked_with_ReadPayloads_named()
    {
        await using KafkaSearchRig rig = new(payloads: true, studio: o => o.CapabilityAuthorizationPolicy = "kafka-search");
        Seed(rig, "orders", "payments");

        await Grants(rig).ForSearchAsync(rig.Entry, Names("orders", "payments"), readsValues: false, Ct);

        // The cluster policy is asked first for each topic with no capability named, and then the capability policy with ReadPayloads named.
        rig.Studio.Authorization.KafkaCalls
            .Where(static x => x.Resource.Capability == "ReadPayloads")
            .Select(static x => (x.Policy, x.Resource.Topic))
            .Should().BeEquivalentTo([("kafka-search", "orders"), ("kafka-search", "payments")]);
        rig.Studio.Authorization.KafkaCalls
            .Where(static x => x.Resource.Capability is null && x.Resource.Topic is not null)
            .Select(static x => x.Resource.Topic)
            .Should().Contain(["orders", "payments"]);
    }

    [Fact]
    public async Task A_lower_grant_at_start_refuses_the_start()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        Seed(rig, "orders");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "value ~ 'v'"), Ct)).Value;
        planned.Grant.HasPayload.Should().BeTrue();

        // The capability policy changed while the confirmation waited: the grant is lower than the one the visitor was planned under.
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        KafkaSearchStartResult start = await search.StartAsync(planned, confirmed: true, Ct);

        start.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.SearchValuesRefusedTopic, "a value search is refused whole at the gate");

        // A key search planned with values, whose values are then refused: the lower grant is a refusal of its own.
        rig.Studio.Authorization.AllowKafka(static _ => true);
        KafkaSearchPlanned keyPlan = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k1'"), Ct)).Value;
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");

        KafkaSearchStartResult lower = await search.StartAsync(keyPlan, confirmed: true, Ct);

        lower.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text.Should().Be(KafkaStudioText.GrantChanged);
        rig.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9601);
    }

    [Fact]
    public async Task A_higher_grant_at_start_starts_the_planned_one()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        Seed(rig, "orders");
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k1'"), Ct)).Value;
        planned.Grant.HasPayload.Should().BeFalse();

        rig.Studio.Authorization.AllowKafka(static _ => true);
        KafkaSearchStartResult start = await search.StartAsync(planned, confirmed: true, Ct);

        await using KafkaSearchRun run = start.Should().BeOfType<KafkaSearchStartResult.Running>().Subject.Run;
        (_, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
        matches.Should().ContainSingle().Which.ValueText.Should().BeNull("the planned (lower) grant is what runs");
    }
}
