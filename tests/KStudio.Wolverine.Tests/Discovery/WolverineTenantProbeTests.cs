using System.Collections;
using System.Reflection;

using KStudio.Core.Clusters;
using KStudio.Internal;

using KStudioWolverine.Tests.Support;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace KStudioWolverine.Tests.Discovery;

/// <summary>
/// D43, decisions 5.4 and 6: Wolverine 6.61 keeps its broker-per-tenant registrations internal, so a tenant's cluster is reached by a probe of three
/// members that fails closed. Against the real 6.61 types, and - for the shapes this version does not have - against fakes with the same member names.
/// </summary>
public class WolverineTenantProbeTests
{
    internal static KafkaTransport[] TenantTransports(KafkaTransport parent) =>
    [
        .. ((IEnumerable) typeof(KafkaTransport).GetProperty("Tenants", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(parent)!)
            .Cast<object>()
            .Select(static x => (KafkaTransport) x.GetType().GetProperty("Transport")!.GetValue(x)!),
    ];

    [Fact]
    public void The_real_6_61_types_have_the_shape_the_probe_reads()
    {
        PropertyInfo tenants = typeof(KafkaTransport).GetProperty(WolverineTenantProbe.TenantsMember, BindingFlags.Instance | BindingFlags.NonPublic)!;

        tenants.Should().NotBeNull("if Wolverine renames or exposes it, the probe is replaced by the public read");
        Type item = tenants.PropertyType.GetInterfaces().Single(static x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>)).GetGenericArguments()[0];
        item.FullName.Should().Be(WolverineTenantProbe.TenantTypeName);
        item.GetProperty(WolverineTenantProbe.TenantIdMember, BindingFlags.Instance | BindingFlags.Public)!.PropertyType.Should().Be<string>();
        item.GetProperty(WolverineTenantProbe.TransportMember, BindingFlags.Instance | BindingFlags.Public)!.PropertyType.Should().Be<KafkaTransport>();
        item.IsPublic.Should().BeFalse("KafkaTenant is internal in 6.61, which is why a probe is needed at all (F1)");
    }

    [Fact]
    public void A_tenant_added_before_Wolverine_connects_is_not_ready_and_the_parent_is_not_listed_in_its_place()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        WolverineRig.Listen(parent, "a.v1");
        options.UseKafka("parent:9092").AddTenant("acme", "tenant:9092");

        WolverineTenantProbeResult probe = WolverineTenantProbe.Probe(parent);

        probe.Tenants.Should().BeEmpty();
        probe.Notes.Should().ContainSingle().Which.Text.Should().Be(
            "Tenant acme's cluster is not listed: Wolverine had not connected it when KStudio read the configuration after the application started.");

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Should().ContainSingle().Which.TenantId.Should().BeNull();
        contribution.Notes.Should().ContainSingle(static x => x.Reason == "Wolverine had not connected it");
    }

    [Fact]
    public void After_Wolverine_connects_a_tenant_is_its_own_cluster_on_its_own_servers_with_the_parents_topics()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        WolverineRig.Listen(parent, "a.v1");
        options.UseKafka("parent:9092").AddTenant("acme", "tenant-a:9092").AddTenant("globex", "tenant-g:9092");
        WolverineRig.ConnectTenants(options, parent);

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Select(static x => (x.Key, x.TenantId)).Should().Equal(("wolverine", null), ("wolverine", "acme"), ("wolverine", "globex"));
        contribution.Clusters.Single(static x => x.TenantId == "acme").ConsumerConfig.BootstrapServers.Should().Be("tenant-a:9092");
        contribution.Clusters.Single(static x => x.TenantId == "globex").AdminConfig.BootstrapServers.Should().Be("tenant-g:9092");
        contribution.Clusters.Single(static x => x.TenantId == "acme").Topics.Select(static x => x.Name).Should().Equal("a.v1");
        contribution.Notes.Should().BeEmpty();

        KafkaClusterRegistry registry = WolverineRig.Registry(options);
        registry.Clusters.Select(static x => x.Key).Should().Equal("wolverine", "wolverine/acme", "wolverine/globex");
        registry.Get("wolverine/acme").Topics.Keys.Should().Equal("a.v1");
    }

    [Fact]
    public void A_tenant_configured_through_the_expression_is_ready_after_Wolverine_connects()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        options.UseKafka("parent:9092").AddTenant("acme", static expression => expression.ConfigureClient(static c => c.BootstrapServers = "via-configure:9092"));
        WolverineRig.ConnectTenants(options, parent);

        WolverineRig.Contribute(options).Clusters.Single(static x => x.TenantId == "acme").ConsumerConfig.BootstrapServers.Should().Be("via-configure:9092");
    }

    [Fact]
    public void IncludeTenants_false_lists_no_tenant_and_does_not_probe()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport parent = WolverineRig.Default(options, "parent:9092");
        options.UseKafka("parent:9092").AddTenant("acme", "tenant:9092");
        WolverineRig.ConnectTenants(options, parent);

        KafkaClusterContribution contribution = WolverineRig.Contribute(options, static o => o.IncludeTenants = false);

        contribution.Clusters.Should().ContainSingle();
        contribution.Notes.Should().BeEmpty();
    }

    [Fact]
    public void A_named_brokers_tenants_are_keyed_under_the_named_cluster()
    {
        WolverineOptions options = WolverineRig.Options();
        KafkaTransport named = WolverineRig.Named(options, "payments", "pay:9092");
        options.AddNamedKafkaBroker(new BrokerName("payments"), "pay:9092").AddTenant("acme", "pay-acme:9092");
        WolverineRig.ConnectTenants(options, named);

        WolverineRig.Registry(options).Clusters.Select(static x => x.Key).Should().Equal("wolverine-payments", "wolverine-payments/acme");
    }

    [Fact]
    public void An_item_of_another_type_name_fails_closed_with_one_note_and_no_tenant()
    {
        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(new object[] { new WrongName("acme", new KafkaTransport()) });

        probe.Tenants.Should().BeEmpty();
        probe.Notes.Should().ContainSingle().Which.Text.Should().Be(
            "Wolverine's broker-per-tenant clusters are not listed: this Wolverine version does not expose them as KStudio expects.");
    }

    [Theory]
    [MemberData(nameof(WrongShapes))]
    public void A_shape_that_is_not_the_one_read_fails_closed(object[] items)
    {
        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(items, WrongShapeTypeName(items));

        probe.Tenants.Should().BeEmpty();
        probe.Notes.Should().ContainSingle().Which.Reason.Should().Contain("does not expose them as KStudio expects");
    }

    public static TheoryData<object[]> WrongShapes() => new()
    {
        { [null!] },
        { [new MissingTransport("acme")] },
        { [new TransportOfAnotherType("acme", "not a transport")] },
        { [new ThrowingGetter()] },
    };

    [Fact]
    public void A_throwing_enumerator_fails_closed()
    {
        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(new ThrowingEnumerable());

        probe.Tenants.Should().BeEmpty();
        probe.Notes.Should().ContainSingle().Which.Reason.Should().Contain("does not expose");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("tab\there")]
    public void A_tenant_id_that_cannot_be_addressed_is_not_listed_and_the_note_does_not_contain_it(string? id)
    {
        KafkaTransport ready = new();
        ready.AdminClientConfig.BootstrapServers = "x:9092";

        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(new object[] { new FakeTenant(id, ready) }, typeof(FakeTenant).FullName!);

        probe.Tenants.Should().BeEmpty();
        KafkaSourceNote note = probe.Notes.Should().ContainSingle().Which;
        note.Text.Should().Be("A tenant's cluster was not listed: its id is one KStudio cannot address.");
    }

    [Fact]
    public void A_ready_tenant_of_the_expected_shape_is_listed_once_by_id()
    {
        KafkaTransport ready = new();
        ready.ConsumerConfig.BootstrapServers = "x:9092";

        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(
            new object[] { new FakeTenant("acme", ready), new FakeTenant("acme", ready), new FakeTenant("globex", ready) },
            typeof(FakeTenant).FullName!);

        probe.Tenants.Select(static x => x.TenantId).Should().Equal("acme", "globex");
        probe.Notes.Should().BeEmpty();
    }

    [Fact]
    public void More_than_the_most_tenants_are_cut_and_said_so()
    {
        KafkaTransport ready = new();
        ready.ConsumerConfig.BootstrapServers = "x:9092";
        object[] many = [.. Enumerable.Range(0, WolverineTenantProbe.MaxTenants + 5).Select(i => new FakeTenant("t" + i, ready))];

        WolverineTenantProbeResult probe = WolverineTenantProbe.Read(many, typeof(FakeTenant).FullName!);

        probe.Tenants.Should().HaveCount(WolverineTenantProbe.MaxTenants);
        probe.Notes.Should().ContainSingle().Which.Text.Should().Contain("5 more");
    }

    private static string WrongShapeTypeName(object[] items) => items.FirstOrDefault(static x => x is not null)?.GetType().FullName ?? "x";

    private sealed record WrongName(string TenantId, KafkaTransport Transport);

    private sealed record FakeTenant(string? TenantId, KafkaTransport Transport);

    private sealed record MissingTransport(string TenantId);

    private sealed record TransportOfAnotherType(string TenantId, object Transport);

    private sealed class ThrowingGetter
    {
        private readonly string reason = "boom";

        public string TenantId => throw new InvalidOperationException(reason);

        public KafkaTransport Transport => reason.Length > 0 ? new KafkaTransport() : throw new InvalidOperationException(reason);
    }

    private sealed class ThrowingEnumerable : IEnumerable
    {
        public IEnumerator GetEnumerator() => throw new InvalidOperationException("boom");
    }
}
