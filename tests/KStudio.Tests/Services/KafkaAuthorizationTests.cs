using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Services;

/// <summary>
/// D35: every question KStudio's policies are asked is a <see cref="KafkaTopicResource" />, once per topic, with the capability named when one is
/// being used, and the host studio's policies are never asked. The recording authorization service answers for both resources, so a host
/// question that leaked in would be seen.
/// </summary>
public class KafkaAuthorizationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_three_topic_call_asks_the_cluster_policy_once_per_topic()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");

        IReadOnlyList<string> allowed = await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["orders.v1", "payments.v1", "audit.v1"], cancellationToken: Ct);

        allowed.Should().Equal("orders.v1", "payments.v1", "audit.v1");
        harness.Authorization.KafkaCalls.Should().Equal(
            ("kafka-topics", new KafkaTopicResource("primary", null, "orders.v1", null)),
            ("kafka-topics", new KafkaTopicResource("primary", null, "payments.v1", null)),
            ("kafka-topics", new KafkaTopicResource("primary", null, "audit.v1", null)));
    }

    [Fact]
    public async Task The_capability_policy_falls_back_to_the_cluster_policy()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");

        (await harness.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct)).Should().BeTrue();

        // D-SHELL 5.2: the cluster policy is always asked first with no capability named, then the capability's policy (here the same one).
        harness.Authorization.KafkaCalls.Should().Equal(
            ("kafka-topics", new KafkaTopicResource("primary", null, "orders.v1", null)),
            ("kafka-topics", new KafkaTopicResource("primary", null, "orders.v1", "SearchRecords")));
    }

    [Fact]
    public async Task Both_policies_are_asked_when_both_are_set_and_the_capability_one_alone_when_only_it_is()
    {
        using KafkaStudioHarness both = new(static o =>
        {
            o.ClusterAuthorizationPolicy = "kafka-topics";
            o.CapabilityAuthorizationPolicy = "kafka-search";
        });

        await both.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct);

        both.Authorization.KafkaCalls.Should().Equal(
            ("kafka-topics", new KafkaTopicResource("primary", null, "orders.v1", null)),
            ("kafka-search", new KafkaTopicResource("primary", null, "orders.v1", "SearchRecords")));

        using KafkaStudioHarness only = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");

        await only.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct);
        await only.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", cancellationToken: Ct);

        only.Authorization.KafkaCalls.Should().Equal(
            [("kafka-search", new KafkaTopicResource("primary", null, "orders.v1", "SearchRecords"))],
            "a read that needs no capability asks the cluster policy, which is not set");
    }

    [Fact]
    public async Task A_refusal_by_the_cluster_policy_stops_a_capability_use_before_the_capability_policy_is_asked()
    {
        using KafkaStudioHarness harness = new(static o =>
        {
            o.ClusterAuthorizationPolicy = "kafka-topics";
            o.CapabilityAuthorizationPolicy = "kafka-search";
        });
        harness.Authorization.AllowKafka(static r => r.Topic != "secrets.v1");

        (await harness.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "secrets.v1", KafkaCapability.SearchRecords, cancellationToken: Ct)).Should().BeFalse();

        harness.Authorization.KafkaCalls.Select(static x => x.Policy).Should().Equal("kafka-topics");
    }

    [Fact]
    public async Task No_policy_asks_nothing()
    {
        using KafkaStudioHarness harness = new();

        (await harness.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct)).Should().BeTrue();
        (await harness.KafkaAuthorization.FilterClustersAsync([KafkaStudioHarness.Primary, KafkaStudioHarness.Secondary], cancellationToken: Ct)).Should().HaveCount(2);
        (await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["a", "b"], cancellationToken: Ct)).Should().HaveCount(2);

        harness.Authorization.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_hosts_store_policy_is_never_asked()
    {
        // The host sets every policy it has; Kafka sets one of its own. The host's handler (which records any resource it sees) is only ever shown
        // its own resource: PostgresStudioResource is asked about the host's scope, never about a cluster or a topic.
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Context.Options.SourceAuthorizationPolicy = "p";
        harness.Context.Options.WriteAuthorizationPolicy = "w";

        await harness.KafkaAuthorization.FilterClustersAsync([KafkaStudioHarness.Primary], cancellationToken: Ct);
        await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["orders.v1"], cancellationToken: Ct);
        await harness.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct);

        harness.Authorization.HostCalls.Should().BeEmpty("the host studio's policies are written for its own resources");
        harness.Authorization.Calls.Select(static x => x.Policy).Distinct().Should().Equal("kafka-topics");
    }

    [Fact]
    public async Task Every_kafka_question_is_a_KafkaTopicResource()
    {
        using KafkaStudioHarness harness = new(static o =>
        {
            o.ClusterAuthorizationPolicy = "kafka-topics";
            o.CapabilityAuthorizationPolicy = "kafka-search";
        });

        await harness.KafkaAuthorization.FilterClustersAsync([KafkaStudioHarness.Primary], cancellationToken: Ct);
        await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["a"], cancellationToken: Ct);
        await harness.KafkaAuthorization.IsAuthorizedAsync(KafkaStudioHarness.Primary, null, KafkaCapability.SearchRecords, cancellationToken: Ct);
        await harness.KafkaAuthorization.AreAllAuthorizedAsync(KafkaStudioHarness.Primary, ["a", "b"], KafkaCapability.SearchRecords, cancellationToken: Ct);

        harness.Authorization.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource is KafkaTopicResource);
    }

    [Fact]
    public async Task A_cluster_level_question_has_no_topic_and_a_tenant_comes_from_the_entry()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        KafkaClusterEntry tenant = new("primary", "tenant-a", "Primary", null);

        await harness.KafkaAuthorization.IsAuthorizedAsync(tenant, null, cancellationToken: Ct);

        harness.Authorization.KafkaCalls.Should().Equal(("kafka-topics", new KafkaTopicResource("primary", "tenant-a", null, null)));
    }

    [Fact]
    public async Task A_scan_naming_a_refused_topic_is_refused_whole_and_names_nothing()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Authorization.AllowKafka(static r => r.Topic != "secrets.v1");

        (await harness.KafkaAuthorization.AreAllAuthorizedAsync(KafkaStudioHarness.Primary, ["orders.v1", "secrets.v1"], KafkaCapability.SearchRecords, cancellationToken: Ct))
            .Should().BeFalse("narrowing the scan to the allowed topics would turn a search into a false negative");
        (await harness.KafkaAuthorization.AreAllAuthorizedAsync(KafkaStudioHarness.Primary, ["orders.v1", "payments.v1"], KafkaCapability.SearchRecords, cancellationToken: Ct))
            .Should().BeTrue();
        (await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["orders.v1", "secrets.v1"], cancellationToken: Ct))
            .Should().Equal("orders.v1");

        KafkaStudioText.RequestRefused.Should().NotContain("secrets");
    }

    [Fact]
    public async Task The_principal_is_fetched_once_per_sweep()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        int before = harness.Visitor.Reads;

        await harness.KafkaAuthorization.FilterTopicsAsync(KafkaStudioHarness.Primary, ["a", "b", "c", "d"], cancellationToken: Ct);

        (harness.Visitor.Reads - before).Should().Be(1);
    }

    [Fact]
    public async Task A_whole_request_fetches_the_principal_once()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        int before = harness.Visitor.Reads;

        await harness.KafkaAuthorization.AreAllAuthorizedAsync(KafkaStudioHarness.Primary, ["a", "b", "c"], KafkaCapability.SearchRecords, cancellationToken: Ct);

        (harness.Visitor.Reads - before).Should().Be(1, "the cluster question and each topic's share one principal");
    }

    [Fact]
    public async Task A_tenant_entry_is_asked_about_as_the_cluster_and_its_tenant_and_never_by_its_entry_key()
    {
        // D43, decision 6: the entry key is {cluster}/{tenant}, the engine's; a policy sees Cluster = the base and TenantId = the tenant, so it never parses a key.
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        KafkaClusterEntry tenant = new("wolverine/acme", "acme", "Wolverine", null);

        tenant.Cluster.Should().Be("wolverine");

        await harness.KafkaAuthorization.IsAuthorizedAsync(tenant, "orders.v1", KafkaCapability.SearchRecords, cancellationToken: Ct);
        await harness.KafkaAuthorization.FilterClustersAsync([tenant], cancellationToken: Ct);
        await harness.KafkaAuthorization.FilterTopicsAsync(tenant, ["orders.v1"], cancellationToken: Ct);
        await harness.KafkaAuthorization.AreAllAuthorizedAsync(tenant, ["orders.v1", "payments.v1"], KafkaCapability.SearchRecords, cancellationToken: Ct);

        harness.Authorization.KafkaCalls.Should().NotBeEmpty().And.OnlyContain(
            static x => x.Resource.Cluster == "wolverine" && x.Resource.TenantId == "acme",
            "no policy is ever handed the composite key");
        harness.Authorization.KafkaCalls.Select(static x => x.Resource).Should().Contain(new KafkaTopicResource("wolverine", "acme", "orders.v1", "SearchRecords"));
    }

    [Fact]
    public async Task A_policy_can_refuse_one_tenant_of_a_cluster_and_not_another()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Authorization.AllowKafka(static r => r.TenantId != "globex");
        KafkaClusterEntry acme = new("wolverine/acme", "acme", "Wolverine", null);
        KafkaClusterEntry globex = new("wolverine/globex", "globex", "Wolverine", null);

        IReadOnlyList<KafkaClusterEntry> visible = await harness.KafkaAuthorization.FilterClustersAsync([acme, globex], cancellationToken: Ct);

        visible.Select(static x => x.Key).Should().Equal("wolverine/acme");
    }
}
