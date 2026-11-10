using System.Text;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

namespace KStudio.Tests.Wire;

/// <summary>
/// How the engine uses a scan reader (decision 3.1, AGENTS.md D37): every assign carries explicit offsets, <c>Close</c> commits nothing and is
/// idempotent, and a reader is used from one thread. The real reader is built against no broker here (a consumer connects in the
/// background and nothing is read); what needs a broker is proved by the live contract.
/// </summary>
public class ConfluentScanReaderTests
{
    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static ConfluentScanReader NewReader()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("main", new ClientConfig { BootstrapServers = "localhost:1" });
        KafkaClusterDefinition cluster = new KafkaClusterRegistry(options).Get("main");
        CapturingLoggerProvider logs = new();
        KStudioLoggers loggers = new(logs.CreateFactory());
        ConfluentClientEvents events = new(loggers, new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

        return new ConfluentScanReader(cluster, options, new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()), events, loggers);
    }

    [Fact]
    public void An_assign_with_a_negative_offset_is_refused_before_it_reaches_the_consumer()
    {
        using ConfluentScanReader reader = NewReader();

        // -1001 is Offset.Stored, which stalls without a group ACL (SPIKE S1); -2 is Beginning and -1 End: none is explicit.
        foreach (long offset in new long[] { -1001, -2, -1 })
        {
            Action assign = () => reader.Assign([new WirePartitionOffset("orders", 0, offset)]);
            assign.Should().Throw<ArgumentOutOfRangeException>();
        }

        Action reposition = () => reader.Reposition(new WirePartitionOffset("orders", 0, -1));
        reposition.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_reader_with_nothing_assigned_reads_nothing_and_closes_twice_without_error()
    {
        using ConfluentScanReader reader = NewReader();

        reader.Read(TimeSpan.FromMilliseconds(50)).Should().BeSameAs(WireReadOutcome.Idle);

        reader.Close();
        Action again = reader.Close;
        again.Should().NotThrow();
    }

    [Fact]
    public void A_reader_disposed_without_being_closed_does_not_throw()
    {
        ConfluentScanReader reader = NewReader();

        Action dispose = reader.Dispose;

        dispose.Should().NotThrow();
        dispose.Should().NotThrow();
    }

    [Fact]
    public void The_fake_records_every_assign_the_one_close_and_the_thread_of_each_call()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        cluster.CreateTopic("orders", 2);
        FakeKafkaWire wire = new(cluster, "main");

        using IWireScanReader reader = wire.OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));
        reader.Assign([new WirePartitionOffset("orders", 0, 0), new WirePartitionOffset("orders", 1, 0)]);
        reader.Read(TimeSpan.Zero);
        reader.Pause([new WirePartition("orders", 0)]);
        reader.Close();

        FakeScanReader fake = wire.Readers.Single();
        fake.Assigns.Should().ContainSingle().Which.Should().OnlyContain(static x => x.Offset >= 0);
        fake.CloseCount.Should().Be(1);
        fake.ThreadIds.Distinct().Should().ContainSingle("every call came from one thread");
    }

    [Fact]
    public void A_fake_reader_refuses_a_negative_assign_as_the_real_one_does()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        using IWireScanReader reader = new FakeKafkaWire(cluster, "main").OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));

        Action assign = () => reader.Assign([new WirePartitionOffset("orders", 0, -1)]);

        assign.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Without_a_group_acl_one_refusal_arrives_and_the_records_flow_on()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System) { RefuseGroupOnFetch = true };
        cluster.CreateTopic("orders", 1);
        cluster.Produce("orders", 0, null, Encoding.UTF8.GetBytes("v"), null, T0);
        using IWireScanReader reader = new FakeKafkaWire(cluster, "main").OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));
        reader.Assign([new WirePartitionOffset("orders", 0, 0)]);

        List<WireReadOutcome> outcomes = [reader.Read(TimeSpan.Zero), reader.Read(TimeSpan.Zero), reader.Read(TimeSpan.Zero)];

        outcomes[0].Should().BeOfType<WireReadOutcome.Failure>().Which.Error.Should().BeOfType<WireError.NotAuthorized>()
            .Which.ResourceType.Should().Be(KafkaResourceType.Group);
        outcomes[1].Should().BeOfType<WireReadOutcome.Record>();
        outcomes[2].Should().BeOfType<WireReadOutcome.PartitionEnd>();
    }

    [Fact]
    public void A_topic_that_does_not_exist_reports_nothing_and_a_refused_read_is_reported_once()
    {
        FakeKafkaCluster cluster = new(TimeProvider.System);
        cluster.CreateTopic("closed", 1);
        cluster.Deny(KafkaAclOperation.Read, KafkaResourceType.Topic, "closed");
        using IWireScanReader reader = new FakeKafkaWire(cluster, "main").OpenReader(new WireReaderOptions(Guid.NewGuid(), WireIsolation.ReadCommitted, new KafkaScanLimits()));
        reader.Assign([new WirePartitionOffset("ghost", 0, 0), new WirePartitionOffset("closed", 0, 0)]);

        List<WireReadOutcome> outcomes = [reader.Read(TimeSpan.Zero), reader.Read(TimeSpan.Zero), reader.Read(TimeSpan.Zero)];

        outcomes[0].Should().BeOfType<WireReadOutcome.Failure>().Which.Error.Should().Be(
            new WireError.NotAuthorized(KafkaAclOperation.Read, KafkaResourceType.Topic, "closed"));
        outcomes.Skip(1).Should().OnlyContain(static x => x == WireReadOutcome.Idle, "a missing topic is silent (SPIKE S1.6) and a refusal is reported once");
    }
}
