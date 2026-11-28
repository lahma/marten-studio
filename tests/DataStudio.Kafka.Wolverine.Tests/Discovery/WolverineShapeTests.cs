using Confluent.Kafka;

using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Internal;
using DataStudio.Kafka.Wolverine.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using Wolverine;
using Wolverine.Kafka.Internals;

namespace DataStudio.Kafka.Wolverine.Tests.Discovery;

/// <summary>
/// Wolverine 7 keeps the Kafka transport's configuration in Wolverine's own option types, not Confluent's, and this build reads Confluent's (#103). The check that
/// tells them apart is one internal method that takes the transport <see cref="Type" />, so a type of the 7.x shape is handed to it here, and the real 6.61 transport
/// is what the rest of the suite runs on.
/// </summary>
public class WolverineShapeTests
{
    private const string Sentence =
        "Wolverine 7.0.0 keeps its Kafka configuration in a form Data Studio 2.0.2 cannot read, so no cluster was listed: Wolverine 7 and later need Data Studio 3.0 or later.";

    [Fact]
    public void The_real_transport_has_the_shape_this_build_reads()
    {
        WolverineClusterSource.HasConfluentShape(typeof(KafkaTransport)).Should().BeTrue();
        WolverineClusterSource.ShapeRefusal(typeof(KafkaTransport), "6.61.0", "2.0.2").Should().BeNull();
    }

    [Fact]
    public void A_transport_of_the_Wolverine_7_shape_is_refused_with_one_note()
    {
        WolverineClusterSource.HasConfluentShape(typeof(Wolverine7Transport)).Should().BeFalse();

        KafkaSourceNote note = WolverineClusterSource.ShapeRefusal(typeof(Wolverine7Transport), "7.0.0", "2.0.2")!;

        note.What.Should().Be("clusters");
        note.Reason.Should().Be("this Wolverine version needs Data Studio 3.0 or later");
        note.Text.Should().Be(Sentence);
    }

    [Theory]
    [InlineData(typeof(OnlyConsumerAndProducer))]
    [InlineData(typeof(OneOfThreeChanged))]
    [InlineData(typeof(NotPublic))]
    [InlineData(typeof(NoMembers))]
    public void A_transport_missing_one_member_or_with_one_of_another_type_is_refused(Type transport)
    {
        WolverineClusterSource.HasConfluentShape(transport).Should().BeFalse();
        WolverineClusterSource.ShapeRefusal(transport, "7.0.0", "2.0.2").Should().NotBeNull();
    }

    [Fact]
    public void A_wolverine_on_the_real_6_61_host_still_lists_its_clusters_and_no_shape_note()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options, "kafka-1:9092");

        KafkaClusterContribution contribution = WolverineRig.Contribute(options);

        contribution.Clusters.Should().ContainSingle().Which.ConsumerConfig.BootstrapServers.Should().Be("kafka-1:9092");
        contribution.Notes.Should().NotContain(static note => note.Reason.Contains("Data Studio 3.0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_over_a_Wolverine_of_the_7_shape_lists_no_cluster_and_says_why_and_never_throws()
    {
        WolverineOptions options = WolverineRig.Options();
        WolverineRig.Default(options, "kafka-1:9092");

        ServiceCollection services = new();
        services.AddSingleton(options);

        KafkaClusterContribution contribution = new WolverineClusterSource(
                services.BuildServiceProvider(),
                WolverineSourceSettings.Take(new KafkaWolverineOptions()))
            { TransportType = typeof(Wolverine7Transport) }
            .Contribute();

        contribution.Clusters.Should().BeEmpty("nothing of the transport was read");

        KafkaSourceNote note = contribution.Notes.Should().ContainSingle().Which;

        note.What.Should().Be("clusters");
        note.Reason.Should().Be("this Wolverine version needs Data Studio 3.0 or later");
        note.Text.Should().StartWith("Wolverine ").And.EndWith("so no cluster was listed: Wolverine 7 and later need Data Studio 3.0 or later.");
    }

    [Fact]
    public void The_note_names_the_loaded_Wolverines_version_and_this_adapters_own()
    {
        string wolverine = KafkaWolverineVersionGuard.ReleaseVersion(KafkaWolverineVersionGuard.InformationalVersion(typeof(WolverineOptions).Assembly));
        string own = KafkaWolverineVersionGuard.ReleaseVersion(KafkaWolverineVersionGuard.InformationalVersion(typeof(WolverineClusterSource).Assembly));

        wolverine.Should().NotBe("?").And.NotContain("+");
        own.Should().NotBe("?").And.NotContain("+");
        WolverineClusterSource.ShapeRefusal(typeof(Wolverine7Transport), wolverine, own)!.Text
            .Should().StartWith($"Wolverine {wolverine} keeps its Kafka configuration in a form Data Studio {own} cannot read");
    }

    // The shape of Wolverine 7's transport: its own option types where 6.x has Confluent's.
    private sealed class Wolverine7Transport
    {
        public Wolverine7Options ConsumerConfig { get; } = new();

        public Wolverine7Options ProducerConfig { get; } = new();

        public Wolverine7Options AdminClientConfig { get; } = new();
    }

    private sealed class Wolverine7Options
    {
        public string? BootstrapServers { get; set; }
    }

    private sealed class OnlyConsumerAndProducer
    {
        public ConsumerConfig ConsumerConfig { get; } = new();

        public ProducerConfig ProducerConfig { get; } = new();
    }

    private sealed class OneOfThreeChanged
    {
        public ConsumerConfig ConsumerConfig { get; } = new();

        public ProducerConfig ProducerConfig { get; } = new();

        public Wolverine7Options AdminClientConfig { get; } = new();
    }

    private sealed class NotPublic
    {
        internal ConsumerConfig ConsumerConfig { get; } = new();

        internal ProducerConfig ProducerConfig { get; } = new();

        internal AdminClientConfig AdminClientConfig { get; } = new();
    }

    private sealed class NoMembers;
}
