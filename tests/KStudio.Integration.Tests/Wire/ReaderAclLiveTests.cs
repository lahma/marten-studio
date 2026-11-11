using KStudio.Integration.Tests.Support;
using KStudio.Tests.Contract;

namespace KStudio.Integration.Tests.Wire;

/// <summary>
/// <c>ReaderAclContract</c> on a real broker (REVIEW-ENG-A 5b and 5c): the reader as <c>nogroup</c> (READ on the topics, no group ACL) and as
/// <c>describeonly</c> (DESCRIBE on the topics, no READ), so the behaviours the fake models are the ones a broker shows.
/// </summary>
public abstract class ReaderAclLiveTests(KafkaFixture fixture) : ReaderAclContract
{
    internal override Task<IContractCluster> OpenAsync(ContractPrincipal principal) =>
        Task.FromResult<IContractCluster>(LiveContractCluster.Open(fixture, principal == ContractPrincipal.NoGroup ? "nogroup" : "describeonly"));
}

/// <summary><see cref="ReaderAclLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class ReaderAclLiveTests39(Kafka39Fixture fixture) : ReaderAclLiveTests(fixture);

/// <summary><see cref="ReaderAclLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class ReaderAclLiveTests4(Kafka4Fixture fixture) : ReaderAclLiveTests(fixture);
