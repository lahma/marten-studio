using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Wire.Confluent;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Clusters;

/// <summary>Decision 4.1: one admin client per cluster, lazy, idle-disposed on a manual clock, safe under a lease, gone at shutdown.</summary>
public class AdminPoolTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetiredWait = TimeSpan.FromSeconds(31);

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
                Logs.CreateFactory().CreateLogger("test"),
                RetiredWait);
        }

        public ManualTimeProvider Clock { get; } = new();

        public CapturingLoggerProvider Logs { get; }

        public List<StubClient> Created { get; } = [];

        public KafkaAdminPool<StubClient> Pool { get; }
    }

    private sealed class StubClient(string cluster) : IDisposable
    {
        private readonly int owner = Environment.CurrentManagedThreadId;

        public string Cluster { get; } = cluster;

        public bool Disposed { get; private set; }

        /// <summary>Whether the dispose ran on the thread that created the client (the test's), which a retired client's never does.</summary>
        public bool DisposedOnCallerThread { get; private set; }

        public bool ThrowOnDispose { get; set; }

        public void Dispose()
        {
            DisposedOnCallerThread = Environment.CurrentManagedThreadId == owner;
            Disposed = true;

            if (ThrowOnDispose)
            {
                throw new InvalidOperationException("librdkafka refused to tear down");
            }
        }
    }

    [Fact]
    public void A_synchronous_dispose_of_the_pool_disposes_every_client_and_refuses_new_leases()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();

        rig.Pool.Dispose();

        rig.Created.Should().OnlyContain(static x => x.Disposed);
        Action rent = () => rig.Pool.Rent(Cluster("a"));
        rent.Should().Throw<ObjectDisposedException>();
        rig.Pool.Dispose();
    }

    [Fact]
    public void A_client_that_throws_on_dispose_does_not_stop_the_others_and_is_logged_at_debug()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();
        rig.Pool.Rent(Cluster("b")).Dispose();
        rig.Created.Single(static x => x.Cluster == "a").ThrowOnDispose = true;

        Action dispose = rig.Pool.Dispose;

        dispose.Should().NotThrow();
        rig.Created.Should().OnlyContain(static x => x.Disposed, "the failing client did not stop the other");
        CapturedLogEntry entry = rig.Logs.Entries.Single(static x => x.EventId.Id == 9664);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Contain("cluster a");
    }

    [Fact]
    public void The_reaper_survives_a_client_that_throws_on_dispose_and_goes_on_reaping()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();
        rig.Pool.Rent(Cluster("b")).Dispose();
        rig.Created.Single(static x => x.Cluster == "a").ThrowOnDispose = true;

        Action advance = () => rig.Clock.Advance(Idle);

        advance.Should().NotThrow("a timer callback that throws ends the process");
        rig.Created.Should().OnlyContain(static x => x.Disposed);
        rig.Pool.ClientCount.Should().Be(0);
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9664).Should().ContainSingle();
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9651).Should().ContainSingle("only the client that disposed cleanly is reported as disposed");
    }

    [Fact]
    public void The_registered_pool_is_disposed_by_a_synchronous_service_provider_dispose()
    {
        EngineHarness harness = EngineHarness.ForWire(new FakeKafkaWire(new FakeKafkaCluster(TimeProvider.System), EngineHarness.ClusterKey));
        _ = harness.Provider.GetRequiredService<KafkaAdminPool<IAdminClient>>();

        Action dispose = harness.Provider.Dispose;

        dispose.Should().NotThrow("a singleton that is only IAsyncDisposable makes ServiceProvider.Dispose() throw");
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
    public async Task Shutdown_disposes_every_idle_client_and_refuses_new_leases()
    {
        Rig rig = new();
        rig.Pool.Rent(Cluster("a")).Dispose();
        rig.Pool.Rent(Cluster("b")).Dispose();

        await rig.Pool.DisposeAsync();

        rig.Created.Should().OnlyContain(static x => x.Disposed);
        Action rent = () => rig.Pool.Rent(Cluster("a"));
        rent.Should().Throw<ObjectDisposedException>();
        await rig.Pool.DisposeAsync();
    }

    [Fact]
    public void A_client_a_call_still_holds_is_not_destroyed_by_the_pool_s_dispose_and_is_disposed_when_the_last_lease_is_released()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease first = rig.Pool.Rent(Cluster("a"));
        KafkaAdminPool<StubClient>.Lease second = rig.Pool.Rent(Cluster("a"));
        rig.Pool.Rent(Cluster("b")).Dispose();

        rig.Pool.Dispose();

        rig.Created.Single(static x => x.Cluster == "b").Disposed.Should().BeTrue("nobody held it");
        StubClient held = rig.Created.Single(static x => x.Cluster == "a");
        held.Disposed.Should().BeFalse("a call that is blocked in the client would touch freed native memory");
        Action rent = () => rig.Pool.Rent(Cluster("a"));
        rent.Should().Throw<ObjectDisposedException>("a rent after the dispose is refused as before");

        first.Dispose();
        held.Disposed.Should().BeFalse("one lease is still held");

        second.Dispose();
        SpinWait.SpinUntil(() => held.Disposed, TimeSpan.FromSeconds(10)).Should().BeTrue("the last lease disposes the retired client, on a pool thread");
        held.DisposedOnCallerThread.Should().BeFalse("disposing blocks while librdkafka tears down, so the release never does it inline");
    }

    [Fact]
    public async Task The_asynchronous_dispose_waits_for_a_retired_client_and_returns_when_its_last_lease_is_released()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease lease = rig.Pool.Rent(Cluster("a"));

        ValueTask dispose = rig.Pool.DisposeAsync();
        dispose.IsCompleted.Should().BeFalse("a lease is held");
        rig.Created[0].Disposed.Should().BeFalse();

        lease.Dispose();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        rig.Created[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task The_asynchronous_dispose_gives_up_after_the_retired_wait_and_leaves_the_client_to_its_last_lease()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease lease = rig.Pool.Rent(Cluster("a"));

        ValueTask dispose = rig.Pool.DisposeAsync();
        rig.Clock.Advance(RetiredWait);
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        rig.Created[0].Disposed.Should().BeFalse("a call is still in flight");

        lease.Dispose();
        SpinWait.SpinUntil(() => rig.Created[0].Disposed, TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    [Fact]
    public void A_retired_client_that_throws_on_dispose_is_logged_and_does_not_escape_the_release()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease lease = rig.Pool.Rent(Cluster("a"));
        rig.Created[0].ThrowOnDispose = true;
        rig.Pool.Dispose();

        Action release = lease.Dispose;

        release.Should().NotThrow("the dispose runs on a pool thread, where an exception would end the process");
        SpinWait.SpinUntil(() => rig.Logs.Entries.Any(static x => x.EventId.Id == 9664), TimeSpan.FromSeconds(10)).Should().BeTrue();
    }

    [Fact]
    public void The_reaper_takes_only_clients_without_a_lease_and_never_a_retired_one()
    {
        Rig rig = new();
        KafkaAdminPool<StubClient>.Lease held = rig.Pool.Rent(Cluster("a"));
        rig.Pool.Rent(Cluster("b")).Dispose();

        rig.Clock.Advance(Idle * 3);

        rig.Created.Single(static x => x.Cluster == "a").Disposed.Should().BeFalse("it is leased");
        rig.Created.Single(static x => x.Cluster == "b").Disposed.Should().BeTrue("it was idle");

        rig.Pool.Dispose();
        rig.Clock.Advance(Idle * 3);
        rig.Created.Single(static x => x.Cluster == "a").Disposed.Should().BeFalse("retired clients wait for their lease, not for the reaper");

        held.Dispose();
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
