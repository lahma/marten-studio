using System.Diagnostics;

using KStudio.Core.Wire;

namespace KStudio.Tests.Contract;

/// <summary>The principals the reader contract runs as.</summary>
public enum ContractPrincipal
{
    /// <summary><c>READ</c> and <c>DESCRIBE</c> on the topics and <b>no</b> ACL on any group.</summary>
    NoGroup,

    /// <summary><c>DESCRIBE</c> on the topics, no <c>READ</c>.</summary>
    DescribeOnly,
}

/// <summary>
/// What a scan reader must do for a principal short of the full set of ACLs (AGENTS.md D37, SPIKE S1, REVIEW-ENG-A 5b and 5c): the fake's
/// behaviours here were once its own, and each is now measured on <c>apache/kafka</c> 3.9.2 and 4.4.0. The live suite runs these as the
/// broker's <c>nogroup</c> and <c>describeonly</c> principals; the fast suite runs them on the fake.
/// </summary>
public abstract class ReaderAclContract
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    internal abstract Task<IContractCluster> OpenAsync(ContractPrincipal principal);

    [Fact]
    public async Task Without_a_group_acl_every_record_arrives_and_exactly_one_refusal_names_the_scan_group()
    {
        await using IContractCluster cluster = await OpenAsync(ContractPrincipal.NoGroup);
        string topic = cluster.NewTopic("nogroup");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 20).Select(i => new ContractRecord("k" + i, "v" + i, T0 + (i * 1000L)))]);
        Guid scanId = Guid.NewGuid();

        List<WireReadOutcome> outcomes = await OnReaderThread(
            cluster,
            scanId,
            reader =>
            {
                reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
                List<WireReadOutcome> seen = DrainUntil(
                    reader,
                    list => list.OfType<WireReadOutcome.PartitionEnd>().Any() && list.OfType<WireReadOutcome.Failure>().Any());

                // The refusal arrives once, within about half a second of the assign: nothing else follows it.
                seen.AddRange(ReadFor(reader, TimeSpan.FromSeconds(2)));
                return seen;
            });

        outcomes.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(Enumerable.Range(0, 20).Select(static x => (long) x));
        WireReadOutcome.Failure failure = outcomes.OfType<WireReadOutcome.Failure>().Should().ContainSingle().Subject;
        failure.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, "kstudio-" + scanId.ToString("N")));
        failure.Partition.Should().BeNull("a group refusal is about the scan, not a partition");
        outcomes.OfType<WireReadOutcome.PartitionEnd>().Should().ContainSingle().Which.Offset.Should().Be(20);
    }

    [Fact]
    public async Task Without_read_on_a_topic_the_refusal_is_reported_once_for_the_partition_and_no_record_arrives()
    {
        await using IContractCluster cluster = await OpenAsync(ContractPrincipal.DescribeOnly);
        string topic = cluster.NewTopic("describeonly");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, [.. Enumerable.Range(0, 5).Select(i => new ContractRecord("k" + i, "v" + i, T0 + (i * 1000L)))]);

        List<WireReadOutcome> outcomes = await OnReaderThread(
            cluster,
            Guid.NewGuid(),
            reader =>
            {
                reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
                return ReadFor(reader, TimeSpan.FromSeconds(4));
            });

        outcomes.OfType<WireReadOutcome.Record>().Should().BeEmpty();
        outcomes.OfType<WireReadOutcome.PartitionEnd>().Should().BeEmpty("a refused partition never reports an end");
        WireReadOutcome.Failure failure = outcomes.OfType<WireReadOutcome.Failure>().Should().ContainSingle("measured: once, then silence").Subject;
        failure.Error.Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Read, KafkaResourceType.Topic, topic));
        failure.Partition.Should().Be(new WirePartition(topic, 0));
    }

    private static Task<List<WireReadOutcome>> OnReaderThread(IContractCluster cluster, Guid scanId, Func<IWireScanReader, List<WireReadOutcome>> work) =>
        Task.Factory.StartNew(
            () =>
            {
                using IWireScanReader reader = cluster.Wire.OpenReader(new WireReaderOptions(scanId, WireIsolation.ReadCommitted, new KStudio.KafkaScanLimits()));

                try
                {
                    return work(reader);
                }
                finally
                {
                    reader.Close();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    private static List<WireReadOutcome> DrainUntil(IWireScanReader reader, Func<List<WireReadOutcome>, bool> done)
    {
        List<WireReadOutcome> outcomes = [];
        Stopwatch clock = Stopwatch.StartNew();

        while (!done(outcomes))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The reader did not reach the expected state within 30 s; it read " + outcomes.Count + " outcomes.");
            }

            WireReadOutcome outcome = reader.Read(TimeSpan.FromMilliseconds(100));

            if (outcome != WireReadOutcome.Idle)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    private static List<WireReadOutcome> ReadFor(IWireScanReader reader, TimeSpan duration)
    {
        List<WireReadOutcome> outcomes = [];
        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration)
        {
            WireReadOutcome outcome = reader.Read(TimeSpan.FromMilliseconds(100));

            if (outcome != WireReadOutcome.Idle)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }
}
