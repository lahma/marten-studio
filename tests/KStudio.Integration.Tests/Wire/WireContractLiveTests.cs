using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Wire;

/// <summary>
/// <c>WireContract</c> on a real broker (decision 3.5): the same tests the fast suite runs on the fake, here on <c>apache/kafka</c>, so a fast
/// test may rely on a Kafka behaviour only because this proves it. Runs as <c>admin</c>, who could create a topic if the engine asked for one.
/// </summary>
public abstract class WireContractLiveTests(KafkaFixture fixture) : WireContract
{
    internal override Task<IContractCluster> OpenAsync() => Task.FromResult<IContractCluster>(LiveContractCluster.Open(fixture));
}

/// <summary><see cref="WireContractLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class WireContractLiveTests39(Kafka39Fixture fixture) : WireContractLiveTests(fixture);

/// <summary><see cref="WireContractLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class WireContractLiveTests4(Kafka4Fixture fixture) : WireContractLiveTests(fixture);
