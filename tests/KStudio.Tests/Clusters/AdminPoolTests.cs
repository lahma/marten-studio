using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Wire.Confluent;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Clusters;

/// <summary>Decision 4.1: one admin client per cluster, lazy, idle-disposed on a manual clock, safe under a lease, gone at shutdown.</summary>
public class AdminPoolTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    private static KafkaClusterDefinition Cluster(string key)
    {
        KafkaEngineOptions options = new();
        options.AddCluster(key, new ClientConfig { BootstrapServers = "broker:9092" });
        return new KafkaClusterRegistry(options).Get(key);
    }

    private sealed class Rig
    {
        public Rig()
        {
            Logs = new CapturingLoggerProvider();
            Pool = new KafkaAdminPool<StubClient>(
                cluster =>
                {
                    StubClient client = new(cluster.Key);
                    Created.Add(client);
                    return client;
                },
                Clock,
                Idle,
                Logs.CreateFactory().CreateLogger("test"));
        }

        public ManualTimeProvider Clock { get; } = new();

        public CapturingLoggerProvider Logs { get; }

        public List<StubClient> Created { get; } = [];

        public KafkaAdminPool<StubClient> Pool { get; }
    }

    private sealed class StubClient(string cluster) : IDisposable
    {
        public string Cluster { get; } = cluster;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Nothing_is_created_until_a_read()
    {
        Rig rig = new();

        rig.Created.Should().BeEmpty();
        rig.Pool.ClientCount.Should().Be(0);
        rig.Logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Leases_of_one_cluster_share_one_client()
    {
        Rig rig = new();

        using KafkaAdminPool<StubClient>.Lease first = rig.Pool.Rent(Cluster("a"));
        using KafkaAdminPool<StubClient>.Lease second = rig.Pool.Rent(Cluster("a"));

        first.Client.Should().BeSameAs(second.Client);
        rig.Created.Should().ContainSingle();
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9650).Should().ContainSingle().Which.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public void Each_cluster_has_a_client_of_its_own()
    {
        Rig rig = new();

        using KafkaAdminPool<StubClient>.Lease a = rig.Pool.Rent(Cluster("a"));
        using KafkaAdminPool<StubClient>.Lease b = rig.Pool.Rent(Cluster("b"));

        a.Client.Should().NotBeSameAs(b.Client);
        (a.Client.Cluster, b.Client.Cluster).Should().Be(("a", "b"));
        rig.Pool.ClientCount.Should().Be(2);
    }

    [Fact]
    public void An_idle_client_is_disposed_by_the_reaper_and_a_busy_one_is_not()
    {
        Rig rig = new();
        KafkaClusterDefinition a = Cluster("a");
        KafkaClusterDefinition b = Cluster("b");

        rig.Pool.Rent(a).Dispose();
        KafkaAdminPool<StubClient>.Lease held = rig.Pool.Rent(b);

        rig.Clock.Advance(Idle);

        rig.Created.Single(static x => x.Cluster == "a").Disposed.Should().BeTrue("idle for the whole timeout");
        rig.Created.Single(static x => x.Cluster == "b").Disposed.Should().BeFalse("a lease blocks disposal");
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9651).Should().ContainSingle().Which.Message.Should().Contain("cluster a").And.Contain("300 s idle");

        rig.Clock.Advance(Idle * 2);
        rig.Created.Single(static x => x.Cluster == "b").Disposed.Should().BeFalse("still leased");

        held.Dispose();
        rig.Clock.Advance(Idle);

        rig.Created.Single(static x => x.Cluster == "b").Disposed.Should().BeTrue("disposed once the lease is released and the timeout passed");
        rig.Pool.ClientCount.Should().Be(0);
    }

    [Fact]
    public void A_client_is_not_disposed_before_it_has_been_idle_for_the_timeout()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();

        rig.Clock.Advance(Idle / 2);
        rig.Created[0].Disposed.Should().BeFalse();

        rig.Pool.Rent(Cluster("a")).Dispose();
        rig.Clock.Advance(Idle / 2);
        rig.Created[0].Disposed.Should().BeFalse("the use in between restarted the idle time");

        rig.Clock.Advance(Idle / 2);
        rig.Created[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public void After_the_reaper_stopped_a_new_read_creates_a_new_client_and_a_new_reaper()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();
        rig.Clock.Advance(Idle);
        rig.Pool.ClientCount.Should().Be(0);

        using KafkaAdminPool<StubClient>.Lease again = rig.Pool.Rent(Cluster("a"));
        rig.Created.Should().HaveCount(2);
        again.Client.Disposed.Should().BeFalse();

        again.Dispose();
        rig.Clock.Advance(Idle);
        again.Client.Disposed.Should().BeTrue("the new reaper works too");
    }

    [Fact]
    public async Task Shutdown_disposes_every_client_and_refuses_new_leases()
    {
        Rig rig = new();
        using KafkaAdminPool<StubClient>.Lease a = rig.Pool.Rent(Cluster("a"));
        rig.Pool.Rent(Cluster("b")).Dispose();

        await rig.Pool.DisposeAsync();

        rig.Created.Should().OnlyContain(static x => x.Disposed);
        Action rent = () => rig.Pool.Rent(Cluster("a"));
        rent.Should().Throw<ObjectDisposedException>();
        await rig.Pool.DisposeAsync();
    }

    [Fact]
    public void A_lease_disposed_twice_releases_once()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease first = rig.Pool.Rent(Cluster("a"));
        using KafkaAdminPool<StubClient>.Lease second = rig.Pool.Rent(Cluster("a"));

        first.Dispose();
        first.Dispose();
        rig.Clock.Advance(Idle * 2);

        rig.Created[0].Disposed.Should().BeFalse("the second lease is still held");
    }
}
