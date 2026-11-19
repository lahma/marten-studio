using Confluent.Kafka;
using Confluent.Kafka.Admin;

using DataStudio.Kafka.Core.Wire;
using DataStudio.Kafka.Integration.Tests.Support;
using DataStudio.Kafka.Tests.Contract;

namespace DataStudio.Kafka.Integration.Tests.Wire;

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
public sealed class WireContractLiveTests4(Kafka4Fixture fixture) : WireContractLiveTests(fixture)
{
    private readonly Kafka4Fixture fixture = fixture;

    /// <summary>
    /// D50, on Kafka 4's own consumer protocol (KIP-848): a group whose member joined with <c>group.protocol=consumer</c> is assigned by
    /// the broker and described through another request than a classic group. The engine's one describe call answers it the same way:
    /// the state, the member's client id and host, and its partitions. Live only: the fake has one kind of group.
    /// </summary>
    [Fact]
    public async Task A_group_on_the_consumer_protocol_is_described_with_its_member_and_its_partitions()
    {
        await using LiveContractCluster cluster = LiveContractCluster.Open(fixture);
        string topic = cluster.NewTopic("members848");
        await cluster.CreateTopicAsync(topic, 2);
        string group = "ks-members848-" + topic;

        await using (await cluster.JoinGroupAsync(group, "ks-member-848", topic, GroupProtocol.Consumer))
        {
            // The broker's own word that this group is on the new protocol: a member that fell back to the classic one would prove nothing.
            using (IAdminClient admin = new AdminClientBuilder(new AdminClientConfig(fixture.Client("admin")) { AllowAutoCreateTopics = false }).Build())
            {
                DescribeConsumerGroupsResult kind = await admin.DescribeConsumerGroupsAsync([group], new DescribeConsumerGroupsOptions { RequestTimeout = TimeSpan.FromSeconds(30) });
                kind.ConsumerGroupDescriptions.Should().ContainSingle().Which.GroupType.Should().Be(ConsumerGroupType.Consumer);
            }

            WireResult<WireGroupDescription> described = await cluster.Wire.DescribeGroupAsync(group, TestContext.Current.CancellationToken);

            described.Error.Should().BeNull();
            described.Value.State.Should().Be("Stable");
            described.Value.MemberCount.Should().Be(1);
            WireGroupMember member = described.Value.Members.Should().ContainSingle().Subject;
            member.ClientId.Should().Be("ks-member-848");
            member.MemberId.Should().NotBeNullOrEmpty();
            member.Host.Should().NotBeNullOrEmpty();
            member.Assignment.Should().BeEquivalentTo([new WirePartition(topic, 0), new WirePartition(topic, 1)]);
        }
    }
}
