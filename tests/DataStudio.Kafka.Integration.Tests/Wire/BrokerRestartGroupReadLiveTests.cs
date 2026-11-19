using System.Diagnostics;
using System.Globalization;
using System.Text;

using Confluent.Kafka;

using DataStudio.Kafka.Core.Wire;
using DataStudio.Kafka.Integration.Tests.Support;
using DataStudio.Kafka.Tests.Contract;

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
/// class's load would blur. The variants are the host's ways of reading: back to back, and at a gap, so that no read is in flight when the broker
/// returns.
/// </para>
/// <para>
/// <b>Not covered, and open:</b> a caller that gives up on a read (a host that allows eight seconds, below the engine's ten-second request timeout)
/// leaves the call running on its client, and the next read, a second later, runs beside it on the same client. The client the first call timed
/// out on is replaced at once, but it is disposed only when the second call ends, which can be after the broker is back, and then the process
/// died in two runs of five (Windows, Confluent.Kafka 2.14.0). The variant is left out because it is not deterministic, not because it is safe.
/// </para>
/// </remarks>
[Collection(KafkaCollections.Restart)]
public abstract class BrokerRestartGroupReadLiveTests(string image)
{
    /// <summary>How long after the broker answers metadata again both reads must have answered: a rebuild and one refresh, with margin.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long the broker stays stopped. This is scenario time, the outage the issue describes, not a wait for anything: every wait that
    /// is for something is on a condition, with a bound.
    /// </summary>
    private static readonly TimeSpan Outage = TimeSpan.FromSeconds(15);

    /// <summary>How long a reader keeps reading before it gives up, so that a run that never recovers fails instead of waiting.</summary>
    private static readonly TimeSpan MeasureFor = TimeSpan.FromSeconds(240);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The group reads recover after a restart, for each way a host reads.</summary>
    /// <param name="gapSeconds">The pause between one read and the next: the host's own cadence, scenario time as <see cref="Outage" /> is.</param>
    [KafkaTheory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task Both_group_reads_answer_again_soon_after_the_broker_answers_metadata_following_a_restart(int gapSeconds)
    {
        await using RestartableKafkaFixture broker = new(image);
        await broker.InitializeAsync();
        await using LiveContractCluster cluster = LiveContractCluster.Open(broker);

        string topic = cluster.NewTopic("restart");
        string group = "data-studio-restart-" + broker.RunId;
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, [new ContractRecord("k", "v", 1_700_000_000_000)]);
        await cluster.CommitGroupOffsetAsync(group, topic, 0, 1);
        await using IAsyncDisposable member = await cluster.JoinGroupAsync(group, "restart-member", topic);

        WirePartition partition = new(topic, 0);
        StringBuilder log = new();

        // Before the outage both calls answer a value, so the clients the outage will leave behind are ones that have worked.
        (await cluster.Wire.DescribeGroupAsync(group, Token)).IsOk.Should().BeTrue("the group is described before the outage");
        (await cluster.Wire.ListGroupOffsetsAsync(group, [partition], Token)).IsOk.Should().BeTrue("the group's offsets are read before the outage");

        // The host reads through the outage, as a page that refreshes does.
        Stopwatch clock = Stopwatch.StartNew();
        await DockerCli.StopAsync(broker.ContainerId);
        Line(log, "broker stopped", clock);

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        TimeSpan gap = TimeSpan.FromSeconds(gapSeconds);

        Task<TimeSpan?> describing = ReadUntilValueAsync("DescribeGroup", clock, log, gap, token => cluster.Wire.DescribeGroupAsync(group, token), stop.Token);
        Task<TimeSpan?> committing = ReadUntilValueAsync("GroupOffsets", clock, log, gap, token => cluster.Wire.ListGroupOffsetsAsync(group, [partition], token), stop.Token);

        await Task.Delay(Outage, Token);
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
        (describedAt ?? TimeSpan.MaxValue).Should().BeLessThan(Deadline, summary);
        (committedAt ?? TimeSpan.MaxValue).Should().BeLessThan(Deadline, summary);
    }

    /// <summary>
    /// Reads one wire call over and over, one at a time, until it answers a value or <see cref="MeasureFor" /> has passed, and returns when
    /// the first value came on the test's clock. The broker is stopped until the test starts it, so the first value is the first read after it
    /// came back.
    /// </summary>
    private static async Task<TimeSpan?> ReadUntilValueAsync<T>(
        string name,
        Stopwatch clock,
        StringBuilder log,
        TimeSpan gap,
        Func<CancellationToken, Task<WireResult<T>>> read,
        CancellationToken token)
    {
        while (clock.Elapsed < MeasureFor && !token.IsCancellationRequested)
        {
            TimeSpan begun = clock.Elapsed;
            WireResult<T> result = await read(token);
            bool ok = result.IsOk;
            string outcome = ok ? "value" : result.Error!.Kind + ":" + result.Error.Code;

            TimeSpan ended = clock.Elapsed;

            lock (log)
            {
                log.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{name} +{begun.TotalSeconds:F1}s took {(ended - begun).TotalSeconds:F1}s: {outcome}"));
            }

            if (ok)
            {
                return ended;
            }

            // The host's own cadence between two reads: scenario time, as the outage is.
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
