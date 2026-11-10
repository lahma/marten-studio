using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Windows;

/// <summary>
/// <c>WindowContract</c> on a real broker (AGENTS.md D38): the engine's own resolver over <c>apache/kafka</c>, out-of-order <c>CreateTime</c>
/// stamps with the margin, <c>LogAppendTime</c> with none, a retention gap with its wording, an empty window, the last stable offset and
/// uncommitted records.
/// </summary>
public abstract class WindowResolutionLiveTests(KafkaFixture fixture) : WindowContract
{
    internal override Task<IContractCluster> OpenAsync() => Task.FromResult<IContractCluster>(LiveContractCluster.Open(fixture));
}

/// <summary><see cref="WindowResolutionLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class WindowResolutionLiveTests39(Kafka39Fixture fixture) : WindowResolutionLiveTests(fixture);

/// <summary><see cref="WindowResolutionLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class WindowResolutionLiveTests4(Kafka4Fixture fixture) : WindowResolutionLiveTests(fixture);
