using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Scanning;

/// <summary>
/// The scan's lifecycle (decisions 4.2 and 4.3): one consumer per scan, every call of it on one dedicated thread, closed once; a disposed
/// handle stops its scan; a host that stops cancels every scan with <c>Shutdown</c>; nothing connects, starts or hosts at registration.
/// </summary>
public class ScanLifecycleTests
{
    [Fact]
    public async Task Every_call_of_a_scans_consumer_comes_from_one_thread_that_is_not_the_callers_and_close_is_called_once()
    {
        ScanFixture fixture = ScanFixture.Create();
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("thread", partitions: 2);

        for (int i = 0; i < 20; i++)
        {
            fixture.Produce(topic, i % 2, i, "k", "v");
        }

        (_, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([topic]));
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);

        // The sampler's consumer is the first, the scan's the second.
        Support.FakeKafka.FakeScanReader scan = fixture.Readers[^1];
        scan.ThreadIds.Distinct().Should().ContainSingle("the consumer is built, assigned, read, paused and closed on the scan's thread only");
        scan.ThreadIds.Should().NotContain(Environment.CurrentManagedThreadId);
        scan.CloseCount.Should().Be(1);
        scan.Assigns.Should().ContainSingle("one explicit assign");
        scan.Assigns[0].Should().OnlyContain(static x => x.Offset >= 0, "an assign carries explicit offsets, never a stored or committed position");
        scan.Options.Isolation.Should().Be(WireIsolation.ReadCommitted);
        scan.GroupId.Should().StartWith("kstudio-");
    }

    [Fact]
    public async Task The_scans_thread_is_a_dedicated_one_and_the_consumer_is_closed_even_when_the_scan_fails()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        await using ScanEngineTests.ScriptedScan guard = scan;

        scan.Reader.Enqueue(new WireReadOutcome.Failure(new WireError.Failed("Local_Fatal", IsFatal: true), null));
        KafkaScanResult result = await scan.Handle.Result.Bounded();

        result.StopReason.Should().Be(KafkaScanStopReason.Failed);
        scan.Reader.ReadOnPoolThread.Should().BeFalse("a scan blocks for as long as it runs, so it never takes a thread-pool thread");
        scan.Reader.CloseCount.Should().Be(1);
    }

    [Fact]
    public async Task An_exception_in_the_reader_is_a_failed_scan_logged_once_by_kind_and_never_an_unhandled_one()
    {
        ThrowingReader reader = new();
        int opened = 0;
        ScanFixture fixture = ScanFixture.Create(wrap: wire => new ScriptedWire(wire, options => Interlocked.Increment(ref opened) == 1 ? wire.OpenReader(options) : reader));
        await using ScanFixture guard = fixture;
        string topic = fixture.Topic("boom");

        for (int i = 0; i < 5; i++)
        {
            fixture.Produce(topic, 0, i, "k", "v");
        }

        (_, KafkaScanResult result, _) = await fixture.RunAsync(ScanFixture.Request([topic]));

        result.StopReason.Should().Be(KafkaScanStopReason.Failed);
        reader.Closed.Should().Be(1);
        CapturedLogEntry failed = fixture.Logs.Entries.Where(static x => x.EventId.Id == 9668).Should().ContainSingle().Subject;
        failed.Level.Should().Be(LogLevel.Warning);
        failed.Message.Should().Contain("InvalidOperationException").And.NotContain("s3cr3t");
    }

    [Fact]
    public async Task Disposing_a_handle_stops_a_running_scan_with_caller_gone_and_waits_for_it()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan is reading");

        await scan.Handle.DisposeAsync();

        scan.Handle.IsRunning.Should().BeFalse("dispose waited for the scan to end");
        (await scan.Handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.CallerGone);
        scan.Reader.CloseCount.Should().Be(1);
        await scan.Handle.DisposeAsync();
        await scan.Fixture.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_the_host_cancels_every_scan_with_shutdown_and_logs_how_many()
    {
        ScanEngineTests.ScriptedScan first = await ScanEngineTests.ScriptedScan.StartAsync();
        ScanFixture.Until(() => first.Reader.Reads > 0, "the scan is reading");

        await first.Fixture.DisposeAsync();

        (await first.Handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Shutdown);
        first.Fixture.Logs.Entries.Where(static x => x.EventId.Id == 9662).Should().ContainSingle().Which.Message.Should().Be("KStudio is stopping 1 scans");
        first.Fixture.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9663);
    }

    [Fact]
    public async Task A_scan_that_outlives_the_ten_seconds_of_shutdown_is_reported_as_a_warning()
    {
        ScanEngineTests.ScriptedScan scan = await ScanEngineTests.ScriptedScan.StartAsync();
        scan.Reader.BlockForever = true;
        ScanFixture.Until(() => scan.Reader.Reads > 0, "the scan is inside its blocked read");

        KafkaScanCoordinator coordinator = scan.Fixture.Provider.GetRequiredService<KafkaScanCoordinator>();
        Task shutdown = scan.Fixture.DisposeAsync().AsTask();
        ScanFixture.Until(() => coordinator.IsShuttingDown, "the host is stopping");

        // The wait for the scans is a timer on the manual clock; move the clock until it has been set and has fired.
        ScanFixture.Until(
            () =>
            {
                scan.Fixture.Clock.Advance(TimeSpan.FromSeconds(11));
                return shutdown.IsCompleted;
            },
            "shutdown gives up after ten seconds");
        await shutdown.Bounded();

        CapturedLogEntry warning = scan.Fixture.Logs.Entries.Where(static x => x.EventId.Id == 9663).Should().ContainSingle().Subject;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Be("1 KStudio scans did not stop within 10 s of shutdown");

        scan.Reader.Unblock();
        (await scan.Handle.Result.Bounded()).StopReason.Should().Be(KafkaScanStopReason.Shutdown);
    }

    [Fact]
    public async Task A_start_after_the_host_began_stopping_is_refused_as_shutting_down()
    {
        ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("late");
        fixture.Produce(topic, 0, 1, "k", "v");
        KafkaScanPlan plan = await fixture.PlanAsync(ScanFixture.Request([topic]));

        IKafkaScanner scanner = fixture.Scanner;
        await fixture.DisposeAsync();

        scanner.Start(plan, fixture.Owner, confirmed: true).Refusal.Should().Be(KafkaScanRefusal.ShuttingDown);
    }

    [Fact]
    public async Task Registering_the_engine_adds_no_hosted_service_and_resolving_it_connects_to_nothing()
    {
        await using ScanFixture fixture = ScanFixture.Create();

        _ = fixture.Provider.GetRequiredService<IKafkaScanner>();
        _ = fixture.Provider.GetRequiredService<KafkaScanCoordinator>();
        _ = fixture.Provider.GetRequiredService<KafkaByteRateGovernor>();

        fixture.Provider.GetServices<IHostedService>().Should().BeEmpty();
        fixture.Cluster.Calls.Should().BeEmpty("nothing connects at registration");
        fixture.Readers.Should().BeEmpty("no consumer is built until a scan starts");
        fixture.Provider.GetRequiredService<KafkaScanCoordinator>().Count.Should().Be(0);
    }

    [Fact]
    public async Task Planning_starts_no_scan_and_the_only_consumer_it_opens_is_the_samplers()
    {
        await using ScanFixture fixture = ScanFixture.Create();
        string topic = fixture.Topic("plan");

        for (int i = 0; i < 5; i++)
        {
            fixture.Produce(topic, 0, i, "k", "v");
        }

        await fixture.PlanAsync(ScanFixture.Request([topic]));

        fixture.Provider.GetRequiredService<KafkaScanLimiter>().Running.Should().Be(0);
        fixture.Provider.GetRequiredService<KafkaScanCoordinator>().Count.Should().Be(0);
        fixture.Readers.Should().HaveCount(1, "the size sampler's");
    }

    private sealed class ThrowingReader : IWireScanReader
    {
        public int Closed { get; private set; }

        public void Assign(IReadOnlyList<WirePartitionOffset> start)
        {
        }

        public void Pause(IReadOnlyList<WirePartition> partitions)
        {
        }

        public void Resume(IReadOnlyList<WirePartition> partitions)
        {
        }

        public void Reposition(WirePartitionOffset position)
        {
        }

        public WireReadOutcome Read(TimeSpan timeout) => throw new InvalidOperationException("payload: s3cr3t");

        public void Close() => Closed++;

        public void Dispose()
        {
        }
    }
}
