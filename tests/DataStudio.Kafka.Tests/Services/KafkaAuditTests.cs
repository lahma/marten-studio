using Bunit;

using DataStudio.Components.Pages;
using DataStudio.Services;
using DataStudio.Kafka.Services;
using DataStudio.Kafka.Tests.Support;

using DataStudio.Postgres.Services;

namespace DataStudio.Kafka.Tests.Services;

/// <summary>
/// D34: a Kafka audit entry is written under the module's own scope and read only through the module's gate - never the host's store policy -
/// and the Activity screen shows the cluster in its Scope column.
/// </summary>
public class KafkaAuditTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ValueTask<List<StudioActionLogEntry>> Visible(KafkaHarness harness) =>
        harness.Context.ActivityRing.GetVisibleAsync(cancellationToken: Ct);

    private static void Record(KafkaHarness harness, string[] topics, KafkaCapability? capability = null, KafkaClusterEntry? cluster = null) =>
        harness.ActionLog.Record(cluster ?? KafkaHarness.Primary, topics, "SearchRecords", string.Join(", ", topics), succeeded: true, "done", capability);

    [Fact]
    public async Task An_entry_is_written_under_the_module_scope_with_the_clusters_label()
    {
        using KafkaHarness harness = new();

        Record(harness, ["orders.v1"], cluster: new KafkaClusterEntry("primary", "tenant-a", "Primary", null));

        StudioActionLogEntry entry = (await Visible(harness)).Should().ContainSingle().Which;
        entry.Scope.Kind.Should().Be("kafka");
        entry.Scope.Label.Should().Equal("Primary · tenant-a");
        entry.Scope.Title.Should().Be("Primary · tenant-a");
        KafkaAuditScope scope = entry.Scope.Value.Should().BeOfType<KafkaAuditScope>().Which;
        (scope.Cluster, scope.TenantId, scope.Topics).Should().Be(("primary", "tenant-a", scope.Topics));
        scope.Topics.Should().Equal("orders.v1");
        entry.DatabaseScope().Should().BeNull("a module entry names no store, so no host policy can recognise it");
    }

    [Fact]
    public async Task A_module_entry_is_never_judged_by_the_hosts_store_policy()
    {
        using KafkaHarness harness = new();
        harness.Context.Options.SourceAuthorizationPolicy = "host-source";
        harness.Authorization.AllowHost(static _ => false);

        Record(harness, ["orders.v1"]);

        (await Visible(harness)).Should().ContainSingle("the host's policy refuses every source, and says nothing about a Kafka entry");
        harness.Authorization.HostCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_naming_a_refused_topic_is_hidden()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Authorization.AllowKafka(static r => r.Topic != "secrets.v1");

        Record(harness, ["orders.v1"]);
        Record(harness, ["orders.v1", "secrets.v1"]);

        (await Visible(harness)).Should().ContainSingle().Which.Target.Should().Be("orders.v1");
    }

    [Fact]
    public async Task A_refused_cluster_hides_its_entries()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Authorization.AllowKafka(static r => r.Cluster != "secondary");

        Record(harness, ["a"], cluster: KafkaHarness.Secondary);

        (await Visible(harness)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_SearchRecords_entry_needs_the_capability_on_and_its_policy()
    {
        using KafkaHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");

        Record(harness, ["orders.v1"], KafkaCapability.SearchRecords);

        (await Visible(harness)).Should().BeEmpty("the capability is off, so reading the entry would reveal a read the visitor may not make");

        harness.Options.Capabilities.SearchRecords = true;
        (await Visible(harness)).Should().ContainSingle();

        harness.Authorization.AllowKafka(static r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses");

        harness.Authorization.AllowKafka(static _ => true);
        harness.Context.ShellOptions.ReadOnly = true;
        (await Visible(harness)).Should().BeEmpty("the host's master switch turns the capability off");
    }

    [Fact]
    public async Task A_ReadPayloads_entry_needs_ReadPayloads_on_and_its_policy()
    {
        using KafkaHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");

        Record(harness, ["orders.v1"], KafkaCapability.ReadPayloads);

        (await Visible(harness)).Should().BeEmpty("the capability is off, so reading the entry would reveal a value read the visitor may not make");

        harness.Options.Capabilities.ReadPayloads = true;
        (await Visible(harness)).Should().BeEmpty("ReadPayloads is judged with its prerequisite, and SearchRecords is off");

        harness.Options.Capabilities.SearchRecords = true;
        (await Visible(harness)).Should().ContainSingle();

        harness.Authorization.AllowKafka(static r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses SearchRecords for the topic, the prerequisite of a value read");
        harness.Authorization.KafkaCalls.Should().Contain(static x => x.Resource.Capability == "SearchRecords" && x.Resource.Topic == "orders.v1");

        harness.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses ReadPayloads for the topic");
        harness.Authorization.KafkaCalls.Should().Contain(static x => x.Resource.Capability == "ReadPayloads" && x.Resource.Topic == "orders.v1");

        harness.Authorization.AllowKafka(static _ => true);
        harness.Context.ShellOptions.ReadOnly = true;
        (await Visible(harness)).Should().BeEmpty("the host's master switch turns the capability off");
    }

    [Fact]
    public async Task Answers_are_kept_for_one_sweep_only()
    {
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");

        Record(harness, ["orders.v1"]);
        Record(harness, ["orders.v1"]);
        Record(harness, ["orders.v1"]);

        (await Visible(harness)).Should().HaveCount(3);
        harness.Authorization.KafkaCalls.Should().HaveCount(2, "one cluster question and one topic question, however many entries");

        (await Visible(harness)).Should().HaveCount(3);
        harness.Authorization.KafkaCalls.Should().HaveCount(4, "the next sweep asks again: a policy's answer may have changed");
    }

    [Fact]
    public async Task An_entry_of_a_kind_no_gate_answers_for_is_hidden()
    {
        using KafkaHarness harness = new();
        harness.Context.ActivityRing.Record(
            new StudioAuditScope("removed-module", ["somewhere"], "somewhere", new object()), "Anything", "target", succeeded: true);

        (await Visible(harness)).Should().BeEmpty("fail closed");
    }

    [Fact]
    public async Task A_host_entry_is_judged_as_before()
    {
        using KafkaHarness harness = new();
        harness.Context.ActionLog.Record("OpenDocument", "orders/1", succeeded: true, "done");

        (await Visible(harness)).Should().ContainSingle().Which.DatabaseScope().Should().NotBeNull("the studio's own entry carries the database's scope, under the database module's key");
    }

    [Fact]
    public async Task The_activity_footer_names_the_modules_events_beside_the_studios()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();

        IRenderedComponent<Activity> page = harness.Context.Render<Activity>();

        string footer = string.Join(' ', page.Find("p.ms-muted").TextContent.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
        footer.Should().EndWith("under event ids 9200-9211 and Kafka's under 9600-9609.");
    }

    [Fact]
    public async Task The_scope_column_shows_the_cluster_label()
    {
        using KafkaHarness harness = new();
        await harness.Context.ReadyAsync();
        Record(harness, ["orders.v1"], cluster: new KafkaClusterEntry("primary", "tenant-a", "Primary", null));

        IRenderedComponent<Activity> page = harness.Context.Render<Activity>();

        page.WaitForAssertion(() => page.Find(".ms-activity-scope").TextContent.Trim().Should().Be("Primary · tenant-a"));
        page.Find(".ms-activity-scope").GetAttribute("title").Should().Be("Primary · tenant-a");
    }

    [Fact]
    public async Task A_tenant_entry_is_recorded_and_judged_by_its_cluster_and_tenant_not_its_entry_key()
    {
        // D43, decision 6: the audit scope carries the cluster's own key and the tenant, so the gate asks the question the entry's pages asked.
        using KafkaHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics");
        harness.Authorization.AllowKafka(static r => r is { Cluster: "wolverine", TenantId: "acme" });
        KafkaClusterEntry acme = new("wolverine/acme", "acme", "Wolverine", null);
        KafkaClusterEntry globex = new("wolverine/globex", "globex", "Wolverine", null);

        Record(harness, ["orders.v1"], cluster: acme);
        Record(harness, ["orders.v1"], cluster: globex);

        List<StudioActionLogEntry> visible = await Visible(harness);

        StudioActionLogEntry entry = visible.Should().ContainSingle().Which;
        entry.Scope.Label.Should().Equal("Wolverine · acme");
        KafkaAuditScope scope = entry.Scope.Value.Should().BeOfType<KafkaAuditScope>().Which;
        (scope.Cluster, scope.TenantId).Should().Be(("wolverine", "acme"));
        harness.Authorization.KafkaCalls.Should().OnlyContain(static x => x.Resource.Cluster == "wolverine");
    }
}
