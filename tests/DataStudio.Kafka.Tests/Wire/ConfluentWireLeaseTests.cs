using System.Reflection;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Core.Logging;
using DataStudio.Kafka.Core.Wire;
using DataStudio.Kafka.Core.Wire.Confluent;

using DataStudio.Marten.Tests.Support;

namespace DataStudio.Kafka.Tests.Wire;

/// <summary>
/// Issue #97 (review F9): <c>ConfluentWire</c> hands the lease and the result of <em>its own</em> call, with the call's own name, to
/// <c>Release</c>, which decides whether the client is replaced. Every other test calls <c>Release</c> itself, so replacing the call a
/// method passes with a constant would pass them all. These run the real <c>ConfluentWire</c> over an admin client whose every call ends
/// <c>Local_TimedOut</c>, with no broker: a group description replaces the client it ran on, and a metadata read does not.
/// </summary>
public class ConfluentWireLeaseTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    private sealed class Rig
    {
        public Rig()
        {
            KafkaEngineOptions options = new() { RequestTimeout = TimeSpan.FromSeconds(1) };
            options.AddCluster("test", new ClientConfig { BootstrapServers = "broker:9092" });
            KafkaClusterRegistry registry = new(options);
            KafkaClusterDefinition cluster = registry.Get("test");
            CapturingLoggerProvider logs = new();
            KafkaEngineLoggers loggers = new(logs.CreateFactory());
            ConfluentClientEvents events = new(loggers, new KafkaLogOnce(TimeProvider.System), TimeProvider.System);

            Cluster = cluster;
            Events = events;
            Admin = new KafkaAdminPool<IAdminClient>(
                _ =>
                {
                    IAdminClient client = DispatchProxy.Create<IAdminClient, TimedOutAdminClient>();
                    Created.Add(client);
                    return client;
                },
                "admin",
                TimeProvider.System,
                Idle,
                logs.CreateFactory().CreateLogger("test"));

            KafkaAdminPool<ConfluentOffsetsReader> offsets = new(
                _ => throw new InvalidOperationException("no offsets reader is built here"),
                "offsets",
                TimeProvider.System,
                Idle,
                logs.CreateFactory().CreateLogger("test"));

            Wire = new ConfluentWire(cluster, Admin, offsets, registry.Settings, events, loggers);
        }

        public KafkaClusterDefinition Cluster { get; }

        public KafkaAdminPool<IAdminClient> Admin { get; }

        public ConfluentClientEvents Events { get; }

        public List<IAdminClient> Created { get; } = [];

        public ConfluentWire Wire { get; }

        /// <summary>The next call's client, to see whether it is the one before.</summary>
        public void TakeNextClient()
        {
            using (Admin.Rent(Cluster))
            {
            }
        }
    }

    /// <summary>An admin client whose every call ends <c>Local_TimedOut</c>, the way a broker that does not answer ends it.</summary>
    public class TimedOutAdminClient : DispatchProxy
    {
        /// <inheritdoc />
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            KafkaException timedOut = new(new Error(ErrorCode.Local_TimedOut));

            return targetMethod.Name switch
            {
                "Dispose" => null,
                "GetMetadata" => throw timedOut,
                "DescribeConsumerGroupsAsync" => Task.FromException<DescribeConsumerGroupsResult>(timedOut),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
        }
    }

    [Fact]
    public async Task A_group_description_that_timed_out_replaces_the_client_it_ran_on()
    {
        Rig rig = new();

        WireResult<WireGroupDescription> result = await rig.Wire.DescribeGroupAsync("orders", TestContext.Current.CancellationToken);

        result.Error.Should().Be(new WireError.Unavailable("Local_TimedOut", Retriable: true));

        rig.TakeNextClient();
        rig.Created.Should().HaveCount(2, "the group call is the one that retires");
    }

    /// <summary>
    /// Review F6: a timeout within 30 seconds of an authentication failure is reported as that failure, and the client it ran on holds the
    /// same waiting group request as any other that timed out. The retire reads the call's own error, so it is replaced all the same.
    /// </summary>
    [Fact]
    public async Task A_group_description_that_timed_out_after_an_authentication_failure_is_reported_as_that_failure_and_still_replaces_its_client()
    {
        Rig rig = new();
        rig.Events.OnError(rig.Cluster.Key, "admin", new Error(ErrorCode.SaslAuthenticationFailed));

        WireResult<WireGroupDescription> result = await rig.Wire.DescribeGroupAsync("orders", TestContext.Current.CancellationToken);

        result.Error.Should().Be(new WireError.AuthenticationFailed("SaslAuthenticationFailed"));

        rig.TakeNextClient();
        rig.Created.Should().HaveCount(2, "the call itself ended Local_TimedOut, whatever it is reported as");
    }

    [Fact]
    public async Task A_metadata_read_that_timed_out_keeps_its_client()
    {
        Rig rig = new();

        WireResult<WireMetadata> result = await rig.Wire.GetMetadataAsync(TestContext.Current.CancellationToken);

        result.Error.Should().Be(new WireError.Unavailable("Local_TimedOut", Retriable: true));

        rig.TakeNextClient();
        rig.Created.Should().ContainSingle("a metadata read is not a group call");
    }
}
