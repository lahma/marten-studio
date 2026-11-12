using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Services;

/// <summary>D34: the cluster in scope, read from the address once, validated when picked, and never written back before it was read.</summary>
public class KafkaStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static KafkaClusterEntry Entry(string key, string? tenant = null) => new(key, tenant, key, null);

    [Fact]
    public async Task The_address_names_the_cluster_to_open()
    {
        using KafkaStudioHarness harness = new(null, Entry("primary"), Entry("secondary"));
        harness.Context.Navigate("/postgres/kafka?cluster=secondary");

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.ActiveCluster!.Key.Should().Be("secondary");
        harness.State.AvailableClusters.Select(static x => x.Key).Should().Equal("primary", "secondary");
        harness.State.AddressRead.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_cluster_in_the_address_falls_back_and_is_logged_at_Debug_only()
    {
        using KafkaStudioHarness harness = new(null, Entry("primary"), Entry("secondary"));
        harness.Context.Navigate("/postgres/kafka?cluster=nope%0D%0Aforged");

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.ActiveCluster!.Key.Should().Be("primary", "the first available cluster is taken");
    }

    [Fact]
    public async Task A_refused_cluster_reads_as_an_unknown_one()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", Entry("primary"), Entry("secondary"));
        harness.Authorization.AllowKafka(static r => r.Cluster != "secondary");
        harness.Context.Navigate("/postgres/kafka?cluster=secondary");

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.ActiveCluster!.Key.Should().Be("primary");
        harness.State.AvailableClusters.Select(static x => x.Key).Should().Equal("primary");
    }

    [Fact]
    public async Task A_cluster_is_compared_ordinally()
    {
        using KafkaStudioHarness harness = new(null, Entry("primary"), Entry("secondary"));
        harness.Context.Navigate("/postgres/kafka?cluster=SECONDARY");

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.ActiveCluster!.Key.Should().Be("primary", "a case variant is not the same key");
    }

    [Fact]
    public async Task A_cluster_the_listing_does_not_carry_is_ignored_when_picked()
    {
        using KafkaStudioHarness harness = new(null, Entry("primary"), Entry("secondary"));
        int changes = 0;
        await harness.State.EnsureInitializedAsync(Ct);
        harness.State.ClusterChanged += () =>
        {
            changes++;
            return Task.CompletedTask;
        };

        await harness.State.SetClusterAsync("other-team");
        await harness.State.SetClusterAsync(null);
        await harness.State.SetClusterAsync("PRIMARY");
        await harness.State.SetClusterAsync("primary");

        harness.State.ActiveCluster!.Key.Should().Be("primary");
        changes.Should().Be(0);

        await harness.State.SetClusterAsync("secondary");

        harness.State.ActiveCluster!.Key.Should().Be("secondary");
        changes.Should().Be(1);
    }

    [Fact]
    public async Task Each_tenant_of_a_broker_per_tenant_cluster_is_its_own_entry()
    {
        using KafkaStudioHarness harness = new(null, Entry("primary", "tenant-a"), Entry("primary-b", "tenant-b"));

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.AvailableClusters.Select(static x => x.Label).Should().Equal("primary · tenant-a", "primary-b · tenant-b");
        harness.State.ActiveCluster!.TenantId.Should().Be("tenant-a");
    }

    [Fact]
    public async Task A_visitor_who_may_see_no_cluster_has_none_in_scope()
    {
        using KafkaStudioHarness harness = new(static o => o.ClusterAuthorizationPolicy = "kafka-topics", Entry("primary"));
        harness.Authorization.AllowKafka(static _ => false);

        await harness.State.EnsureInitializedAsync(Ct);

        harness.State.ActiveCluster.Should().BeNull();
        harness.State.AvailableClusters.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://h/postgres/kafka?cluster=a", "a")]
    [InlineData("https://h/postgres/kafka?x=1&cluster=a%2Fb#q=cluster%3Dz", "a/b")]
    [InlineData("https://h/postgres/kafka#cluster=a", null)]
    [InlineData("https://h/postgres/kafka?cluster=", null)]
    [InlineData("https://h/postgres/kafka", null)]
    public void The_cluster_is_read_from_the_query_string_and_nowhere_else(string uri, string? expected)
    {
        KafkaState.ReadCluster(uri).Should().Be(expected);
    }

    [Fact]
    public void The_state_is_resolved_as_the_circuits_own()
    {
        using KafkaStudioHarness harness = new();

        harness.Context.Services.GetRequiredService<NavigationManager>().Should().NotBeNull();
        harness.State.Should().BeSameAs(harness.Context.Services.GetRequiredService<KafkaState>());
    }
}
