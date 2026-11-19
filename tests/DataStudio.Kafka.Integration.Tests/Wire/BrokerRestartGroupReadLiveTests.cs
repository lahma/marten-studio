using System.Diagnostics;
using System.Globalization;
using System.Text;

using Confluent.Kafka;

using DataStudio.Kafka.Core.Catalog;
using DataStudio.Kafka.Core.Wire;
using DataStudio.Kafka.Integration.Tests.Support;
using DataStudio.Kafka.Tests.Contract;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Integration.Tests.Wire;

/// <summary>
/// The group reads across a broker restart (issue #97, D36, D50). A host that reads a group while its broker is away gets
/// <c>Local_TimedOut</c> from both group calls, <c>DescribeConsumerGroups</c> on the admin client and <c>ListConsumerGroupOffsets</c> on the
/// offsets reader. A client whose group request timed out while the broker was away <b>crashes the whole process</b> (<c>0xC0000005</c>, an
/// access violation in librdkafka, four to nine seconds after the broker is back) or, where it does not, keeps timing out until its own metadata
/// refresh runs five minutes later. The engine replaces the client a group call timed out on, every time, so that the reads answer a value again
/// soon after the broker answers metadata, and the host survives.
/// </summary>
/// <remarks>
/// <para>
/// <b>Without the fix this test ends the test host</b>, as <see cref="FreshBrokerLagReadLiveTests" /> does for #5040: a native crash does not fail an
/// assertion, it ends the process (exit code <c>-1073741819</c> on Windows), and that is how a failure shows. Measured on Windows at Confluent.Kafka
/// 2.14.0, the floor, each call alone and both together; a control that read nothing while the broker was away did not crash.
/// </para>
/// <para>
/// The broker is the test's own, never reused, bound to a host port chosen up front (Docker gives an unpinned port a new number at every start, and
/// the advertised address would no longer be the engine's bootstrap). The collection runs alone: the measurement is of time, which another
/// class's load would blur. The variants are the host's ways of reading: back to back, at a gap, so that no read is in flight when the broker
/// returns, and back to back with a caller that gives up.
/// </para>
/// <para>
/// The third variant is the host that allows a read eight seconds, below the engine's ten-second request timeout, and reads again a second later:
/// the caller gives up and the call goes on holding its client. It passes because the snapshot cache shares the read that is still running with the
/// next caller of the key (one read per key at a time); reads of different keys on one client still overlap, which this test does not cover (D36).
/// </para>
/// </remarks>
[Collection(KafkaCollections.Restart)]
public abstract class BrokerRestartGroupReadLiveTests(string image)
{
    /// <summary>How long after the broker answers metadata again both reads must have answered: a rebuild and one refresh, with margin.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the test waits for both readers to have recorded a read that failed <c>Unavailable</c> while the broker was away. A group request
    /// that times out takes the engine's ten-second request timeout, so this is the outage's minimum length, as a condition and not a delay.
    /// </summary>
    private static readonly TimeSpan FirstFailureBound = TimeSpan.FromSeconds(90);

    /// <summary>The shell's default <c>RefreshInterval</c>, which is how long the engine reuses a completed read.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a reader keeps reading before it gives up, so that a run that never recovers fails instead of waiting.</summary>
    private static readonly TimeSpan MeasureFor = TimeSpan.FromSeconds(240);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The group reads recover after a restart, for each way a host reads.</summary>
    /// <param name="gapSeconds">The pause between one read and the next: the host's own cadence, which is scenario time.</param>
    /// <param name="callerSeconds">How long the caller waits for a read before it gives up and reads again; <c>0</c> waits for the engine's own timeout.</param>
    [KafkaTheory]
    [InlineData(1, 0)]
    [InlineData(20, 0)]
    [InlineData(1, 8)]
    public async Task Both_group_reads_answer_again_soon_after_the_broker_answers_metadata_following_a_restart(int gapSeconds, int callerSeconds)
    {
        await using RestartableKafkaFixture broker = new(image);
        await broker.InitializeAsync();
        await using LiveContractCluster cluster = LiveContractCluster.Open(broker, catalogTimeToLive: RefreshInterval);

        string topic = cluster.NewTopic("restart");
        string group = "data-studio-restart-" + broker.RunId;
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, [new ContractRecord("k", "v", 1_700_000_000_000)]);
        await cluster.CommitGroupOffsetAsync(group, topic, 0, 1);
        await using IAsyncDisposable member = await cluster.JoinGroupAsync(group, "restart-member", topic);

        WirePartition partition = new(topic, 0);
        StringBuilder log = new();

        // The reads are the engine's own, through the single-flight cache the pages and the diagnostics API share (D50), which is what a host runs.
        KafkaWireReads reads = cluster.Engine.Provider.GetRequiredService<KafkaWireReads>();
        string key = EngineHarness.ClusterKey;

        // Before the outage both calls answer a value, so the clients the outage will leave behind are ones that have worked.
        (await reads.GroupAsync(key, group, Token)).IsOk.Should().BeTrue("the group is described before the outage");
        (await reads.GroupOffsetsAsync(key, group, topic, [partition], Token)).IsOk.Should().BeTrue("the group's offsets are read before the outage");

        // The host reads through the outage, as a page that refreshes does.
        Stopwatch clock = Stopwatch.StartNew();
        await DockerCli.StopAsync(broker.ContainerId);
        Line(log, "broker stopped", clock);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        TimeSpan gap = TimeSpan.FromSeconds(gapSeconds);
        TimeSpan? caller = callerSeconds > 0 ? TimeSpan.FromSeconds(callerSeconds) : null;
        Reader describer = new("DescribeGroup");
        Reader committer = new("GroupOffsets");

        Task<TimeSpan?> describing = ReadUntilValueAsync(describer, clock, log, gap, caller, token => reads.GroupAsync(key, group, token), stop.Token);
        Task<TimeSpan?> committing = ReadUntilValueAsync(committer, clock, log, gap, caller, token => reads.GroupOffsetsAsync(key, group, topic, [partition], token), stop.Token);

        // The scenario's precondition: each call has read through the outage and been answered "unavailable" (a group request that timed out
        // while the broker was away). Reads from before the outage, still in the cache, do not count, and a reader does not stop on one.
        bool read = await Task.Run(() => SpinWait.SpinUntil(() => describer.Failures > 0 && committer.Failures > 0, FirstFailureBound), Token);
        read.Should().BeTrue($"each read must have failed Unavailable during the outage before the broker is started; DescribeGroup {describer.Failures}, GroupOffsets {committer.Failures}\n{log}");

        await DockerCli.StartAsync(broker.ContainerId);
        Line(log, "broker started", clock);

        // The broker has answered a fresh client's metadata request: a wait on a condition, bounded at 120 s.
        await WaitForMetadataAsync(broker, TimeSpan.FromSeconds(120));
        TimeSpan answered = clock.Elapsed;
        Line(log, "metadata answered", clock);

        // Each read is judged from when metadata answered: the time of its first value after that, or none within the bound.
        TimeSpan? describedAt = SinceOrZero(await describing, answered);
        TimeSpan? committedAt = SinceOrZero(await committing, answered);

        string summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{log}DescribeGroup answered {Format(describedAt)} after metadata, GroupOffsets {Format(committedAt)}; the bound is {Deadline.TotalSeconds:F0}s");

        TestContext.Current.SendDiagnosticMessage(summary);
        describer.FailuresBeforeValue.Should().BeGreaterThan(0, summary);
        committer.FailuresBeforeValue.Should().BeGreaterThan(0, summary);
        (describedAt ?? TimeSpan.MaxValue).Should().BeLessThan(Deadline, summary);
        (committedAt ?? TimeSpan.MaxValue).Should().BeLessThan(Deadline, summary);
    }

    /// <summary>What one reader saw: how many of its reads failed <c>Unavailable</c>, and how many had by the time it got its first value.</summary>
    private sealed class Reader(string name)
    {
        private int failures;

        public string Name { get; } = name;

        public int Failures => Volatile.Read(ref failures);

        public int FailuresBeforeValue { get; set; }

        public void Failed() => Interlocked.Increment(ref failures);
    }

    /// <summary>
    /// Reads one wire call over and over, one at a time, until it answers a value <b>after it has failed Unavailable at least once</b> or
    /// <see cref="MeasureFor" /> has passed, and returns when that value came on the test's clock. A value before any failure (one the cache still
    /// held from before the outage) is not the recovery and is read past.
    /// </summary>
    private static async Task<TimeSpan?> ReadUntilValueAsync<T>(
        Reader reader,
        Stopwatch clock,
        StringBuilder log,
        TimeSpan gap,
        TimeSpan? caller,
        Func<CancellationToken, Task<WireResult<T>>> read,
        CancellationToken token)
    {
        while (clock.Elapsed < MeasureFor && !token.IsCancellationRequested)
        {
            TimeSpan begun = clock.Elapsed;
            bool ok = false;
            string outcome;

            using (CancellationTokenSource given = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (caller is { } limit)
                {
                    given.CancelAfter(limit);
                }

                try
                {
                    WireResult<T> result = await read(given.Token);
                    ok = result.IsOk;
                    outcome = ok ? "value" : result.Error!.Kind + ":" + result.Error.Code;

                    if (result.Error is WireError.Unavailable)
                    {
                        reader.Failed();
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    outcome = "the caller gave up";
                }
            }

            TimeSpan ended = clock.Elapsed;

            lock (log)
            {
                log.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{reader.Name} +{begun.TotalSeconds:F1}s took {(ended - begun).TotalSeconds:F1}s: {outcome}"));
            }

            if (ok && reader.Failures > 0)
            {
                reader.FailuresBeforeValue = reader.Failures;
                return ended;
            }

            // The host's own cadence between two reads: scenario time.
            await Task.Delay(gap, CancellationToken.None);
        }

        return null;
    }

    private static async Task WaitForMetadataAsync(KafkaFixture broker, TimeSpan bound)
    {
        DateTime deadline = DateTime.UtcNow + bound;

        while (true)
        {
            try
            {
                using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig(broker.Client("admin")) { AllowAutoCreateTopics = false }).Build();
                admin.GetMetadata(TimeSpan.FromSeconds(5));
                return;
            }
            catch (KafkaException) when (DateTime.UtcNow < deadline)
            {
                // A poll of a condition: the call waits up to five seconds on its own.
                await Task.Yield();
            }
        }
    }

    private static void Line(StringBuilder log, string what, Stopwatch clock)
    {
        lock (log)
        {
            log.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{what} at +{clock.Elapsed.TotalSeconds:F1}s"));
        }
    }

    private static TimeSpan? SinceOrZero(TimeSpan? at, TimeSpan since) =>
        at is { } value ? (value < since ? TimeSpan.Zero : value - since) : null;

    private static string Format(TimeSpan? at) =>
        at is { } value ? string.Create(CultureInfo.InvariantCulture, $"+{value.TotalSeconds:F1}s") : "never";
}

/// <summary><see cref="BrokerRestartGroupReadLiveTests" /> on Kafka 4.4.0.</summary>
public sealed class BrokerRestartGroupReadLiveTests4() : BrokerRestartGroupReadLiveTests(KafkaFixture.Image4);
