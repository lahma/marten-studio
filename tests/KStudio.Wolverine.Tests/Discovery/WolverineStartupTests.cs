using Confluent.Kafka;

using KStudio.Core.Clusters;

using KStudioWolverine.Tests.Support;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace KStudioWolverine.Tests.Discovery;

/// <summary>
/// D43: tenants are read only once the host has started. A broker-per-tenant registration has no configuration until Wolverine's runtime has
/// connected it, and that is part of starting, so a registry first read before then lists the transports' own clusters and says the tenants come
/// later; the first read after it adds them, once, and changes nothing already listed.
/// </summary>
public class WolverineStartupTests
{
    private static (WolverineOptions Options, KafkaTransport Parent) Application()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        WolverineRig.Listen(parent, "a.v1");
        options.UseKafka("parent:9092").AddTenant("acme", "tenant-a:9092");
        return (options, parent);
    }

    [Fact]
    public void Built_before_the_host_started_it_lists_no_tenant_and_says_they_come_later()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        WolverineRig.ConnectTenants(options, parent);
        using FakeLifetime lifetime = new();

        KafkaClusterContribution contribution = WolverineRig.Contribute(options, lifetime: lifetime);

        contribution.Clusters.Select(static x => (x.Key, x.TenantId)).Should().Equal([("wolverine", (string?) null)]);
        contribution.Incomplete.Should().BeTrue();
        contribution.IncompleteNote.Should().Be("Tenant clusters are listed once the application has started.");
        contribution.CompleteWhen.Should().Be(lifetime.ApplicationStarted);
        contribution.Notes.Should().BeEmpty("the probe was not asked: nothing about the tenants is said until they are read");
    }

    [Fact]
    public void After_the_host_started_the_tenants_appear_the_hosts_clusters_stay_and_the_source_is_asked_twice()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        using FakeLifetime lifetime = new();
        int asked = 0;

        KafkaClusterRegistry registry = WolverineRig.Registry(
            options,
            static host => host.AddCluster("host", new ClientConfig { BootstrapServers = "host:9092" }),
            lifetime: lifetime,
            asked: () => asked++);

        registry.Clusters.Select(static x => x.Key).Should().Equal("host", "wolverine");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Be("Tenant clusters are listed once the application has started.");
        KafkaClusterDefinition hostCluster = registry.Get("host");
        KafkaClusterDefinition wolverine = registry.Get("wolverine");

        // What Wolverine's runtime does while the application starts.
        WolverineRig.ConnectTenants(options, parent);
        lifetime.Start();

        registry.Clusters.Select(static x => x.Key).Should().Equal("host", "wolverine", "wolverine/acme");
        registry.Get("host").Should().BeSameAs(hostCluster);
        registry.Get("wolverine").Should().BeSameAs(wolverine);
        registry.Get("wolverine/acme").TenantId.Should().Be("acme");
        registry.Notes.Should().BeEmpty();

        _ = registry.Clusters;
        asked.Should().Be(2);
    }

    [Fact]
    public void A_tenant_Wolverine_had_not_connected_by_then_is_noted_and_not_listed()
    {
        (WolverineOptions options, _) = Application();
        using FakeLifetime lifetime = new();

        KafkaClusterRegistry registry = WolverineRig.Registry(options, lifetime: lifetime);
        lifetime.Start();

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("Tenant acme's cluster is not listed");
    }

    [Fact]
    public void Built_after_the_host_started_it_lists_the_tenants_at_once_and_is_asked_once()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        WolverineRig.ConnectTenants(options, parent);
        using FakeLifetime lifetime = new();
        lifetime.Start();
        int asked = 0;

        KafkaClusterRegistry registry = WolverineRig.Registry(options, lifetime: lifetime, asked: () => asked++);

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine/acme");
        _ = registry.Clusters;
        asked.Should().Be(1);
    }

    [Fact]
    public void With_no_host_lifetime_registered_it_behaves_as_if_the_host_had_started()
    {
        (WolverineOptions options, KafkaTransport parent) = Application();
        WolverineRig.ConnectTenants(options, parent);
        int asked = 0;

        KafkaClusterRegistry registry = WolverineRig.Registry(options, asked: () => asked++);

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine/acme");
        registry.Notes.Should().BeEmpty();
        asked.Should().Be(1);
        WolverineRig.Contribute(options).Incomplete.Should().BeFalse();
    }

    [Fact]
    public void IncludeTenants_false_is_never_incomplete()
    {
        (WolverineOptions options, _) = Application();
        using FakeLifetime lifetime = new();

        WolverineRig.Contribute(options, static o => o.IncludeTenants = false, lifetime).Incomplete.Should().BeFalse();
    }

    [Fact]
    public void Two_named_brokers_whose_names_make_one_slug_list_only_the_first_and_its_tenants()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport first = WolverineRig.Named(options, "Payments EU", "first:9092");
        KafkaTransport second = WolverineRig.Named(options, "payments-eu", "second:9092");
        options.AddNamedKafkaBroker(new BrokerName("Payments EU"), "first:9092").AddTenant("acme", "first-acme:9092");
        options.AddNamedKafkaBroker(new BrokerName("payments-eu"), "second:9092").AddTenant("globex", "second-globex:9092");
        WolverineRig.ConnectTenants(options, first);
        WolverineRig.ConnectTenants(options, second);

        KafkaClusterRegistry registry = WolverineRig.Registry(options);

        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine-payments-eu", "wolverine-payments-eu/acme");
        registry.Get("wolverine-payments-eu").ConsumerSettings["bootstrap.servers"].Should().Be("first:9092");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("wolverine-payments-eu").And.Contain("another transport");
    }

    [Fact]
    public void A_Cluster_callback_that_is_cancelled_leaves_the_hosts_clusters_listed_with_a_note()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options);

        KafkaClusterRegistry registry = WolverineRig.Registry(
            options,
            static host => host.AddCluster("host", new ClientConfig { BootstrapServers = "host:9092" }),
            static o => o.Cluster(static (_, _) => throw new TaskCanceledException("cancelled")));

        registry.Clusters.Select(static x => x.Key).Should().Equal("host", "wolverine");
        registry.Notes.Should().ContainSingle().Which.Text.Should().Contain("TaskCanceledException");
    }
}
