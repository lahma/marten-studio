using System.Text;

using DataStudio.Kafka.Core.Wire;
using DataStudio.Kafka.Tests.Contract;
using DataStudio.Kafka.Tests.Support.FakeKafka;

using DataStudio.Postgres.Tests.Support;

using Microsoft.Extensions.Logging;

namespace DataStudio.Kafka.Tests.Diagnostics;

/// <summary>
/// D50: the diagnostics API returns what the engine reads about a cluster, a topic and a group, with every refusal as a value and a sentence, and
/// tells where a member stands in its group.
/// </summary>
public class KafkaDiagnosticsTests
{
    private const string Cluster = EngineHarness.ClusterKey;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig()
        {
            Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 19, 6, 0, 0, TimeSpan.Zero));
            Fake = new FakeKafkaCluster(Clock);
            Engine = EngineHarness.ForWire(new FakeKafkaWire(Fake, Cluster), Clock);
        }

        public ManualTimeProvider Clock { get; }

        public FakeKafkaCluster Fake { get; }

        public EngineHarness Engine { get; }

        public IKafkaDiagnostics Diagnostics => Engine.Diagnostics;

        public void Produce(string topic, int partition, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Fake.Produce(topic, partition, Encoding.UTF8.GetBytes("k" + i), Encoding.UTF8.GetBytes("v" + i), null, 1_700_000_000_000 + i);
            }
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static WireGroupMember Member(string clientId, params WirePartition[] assignment) =>
        new(clientId + "-member", clientId, "/10.0.0.1", assignment);

    [Fact]
    public async Task The_registered_clusters_are_listed_by_key()
    {
        await using Rig rig = new();

        rig.Diagnostics.Clusters.Should().Equal(Cluster);
    }

    [Fact]
    public async Task An_unknown_cluster_key_is_a_programming_error_and_throws()
    {
        await using Rig rig = new();

        Func<Task> read = () => rig.Diagnostics.GetClusterAsync("nope", cancellationToken: Token);

        await read.Should().ThrowAsync<ArgumentException>().WithMessage("*'nope'*");
    }

    [Fact]
    public async Task The_cluster_status_counts_the_brokers_and_says_which_topics_the_metadata_lists()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);

        KafkaClusterStatus status = await rig.Diagnostics.GetClusterAsync(Cluster, ["orders", "absent"], Token);

        status.Problem.Should().BeNull();
        status.BrokerCount.Should().Be(1);
        status.ReadAt.Should().Be(rig.Clock.GetUtcNow());
        status.Topics.Select(static x => (x.Topic, x.Listed)).Should().Equal(("orders", true), ("absent", false));
    }

    [Fact]
    public async Task A_cluster_that_does_not_answer_is_a_problem_on_the_status_and_never_an_exception()
    {
        await using Rig rig = new();
        rig.Fake.FailNext(WireCall.Metadata, new WireError.Unavailable("Local_Transport", Retriable: true));

        KafkaClusterStatus status = await rig.Diagnostics.GetClusterAsync(Cluster, ["orders"], Token);

        status.Problem.Should().BeEquivalentTo(new KafkaReadProblem
        {
            Outcome = KafkaReadOutcome.Unavailable,
            Code = "Local_Transport",
            Sentence = "Not available: the cluster did not answer (Local_Transport)",
        });
        status.BrokerCount.Should().Be(0);
        status.Topics.Should().ContainSingle().Which.Listed.Should().BeFalse();
    }

    [Fact]
    public async Task The_topic_status_reads_each_partition_s_first_and_end_offset_and_the_retention()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 2, new Dictionary<string, string> { ["retention.ms"] = "3600000", ["cleanup.policy"] = "compact,delete" });
        rig.Produce("orders", 0, 3);
        rig.Fake.DeleteRecords("orders", 0, 1);

        KafkaTopicStatus status = await rig.Diagnostics.GetTopicAsync(Cluster, "orders", Token);

        status.Problem.Should().BeNull();
        status.OffsetsProblem.Should().BeNull();
        status.Partitions.Select(static x => (x.Partition, x.FirstOffset, x.EndOffset)).Should().Equal((0, 1L, 3L), (1, 0L, 0L));
        status.OffsetSpan.Should().Be(2);
        status.RetentionMs.Should().Be(3_600_000);
        status.CleanupPolicy.Should().Be("compact,delete");
        status.ConfigProblem.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_configuration_says_what_to_grant_and_leaves_the_offsets_in_place()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 2);
        rig.Fake.Deny(KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic, "orders");

        KafkaTopicStatus status = await rig.Diagnostics.GetTopicAsync(Cluster, "orders", Token);

        status.ConfigProblem!.Outcome.Should().Be(KafkaReadOutcome.NotAuthorized);
        status.ConfigProblem.Sentence.Should().Be("Not available: needs `DESCRIBE_CONFIGS` on topic `orders`");
        status.ConfigProblem.Topic.Should().Be("orders");
        status.RetentionMs.Should().BeNull();
        status.Partitions.Should().ContainSingle().Which.EndOffset.Should().Be(2);
    }

    [Fact]
    public async Task A_topic_the_metadata_does_not_list_is_not_found()
    {
        await using Rig rig = new();

        KafkaTopicStatus status = await rig.Diagnostics.GetTopicAsync(Cluster, "absent", Token);

        status.Problem!.Outcome.Should().Be(KafkaReadOutcome.NotFound);
        status.Problem.Sentence.Should().Be("Not found: topic `absent`");
        status.Problem.Topic.Should().Be("absent");
        status.Partitions.Should().BeEmpty();
    }

    [Fact]
    public async Task The_group_status_lists_the_members_their_partitions_and_the_lag_of_each()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 2);
        rig.Produce("orders", 0, 5);
        rig.Produce("orders", 1, 2);
        rig.Fake.SetGroupMembers(
            "billing", "Stable", Member("billing-a", new WirePartition("orders", 0)), Member("billing-b", new WirePartition("orders", 1)));
        rig.Fake.Commit("billing", "orders", 0, 3);

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);

        status.Problem.Should().BeNull();
        status.OffsetsProblem.Should().BeNull();
        status.State.Should().Be("Stable");
        status.Members.Select(static x => x.ClientId).Should().Equal("billing-a", "billing-b");
        status.Members[0].Assignment.Should().Equal(new KafkaTopicPartition { Topic = "orders", Partition = 0 });
        status.Partitions.Select(static x => (x.Partition, x.CommittedOffset, x.EndOffset, x.Lag, x.MemberClientId)).Should().Equal(
            (0, 3L, 5L, 2L, "billing-a"),
            (1, (long?) null, 2L, (long?) null, "billing-b"));
        status.TotalLag.Should().Be(2);
    }

    [Fact]
    public async Task A_group_with_no_member_shows_its_committed_positions_on_the_topics_asked_about()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 2);
        rig.Produce("orders", 1, 4);
        rig.Fake.SetGroup("billing", "Empty", 0);
        rig.Fake.Commit("billing", "orders", 1, 1);

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);

        status.State.Should().Be("Empty");
        status.Members.Should().BeEmpty();
        status.Partitions.Should().ContainSingle().Which.Should().BeEquivalentTo(new KafkaGroupPartition
        {
            Topic = "orders",
            Partition = 1,
            CommittedOffset = 1,
            EndOffset = 4,
            FirstOffset = 0,
            Lag = 3,
        });
    }

    [Fact]
    public async Task A_group_Kafka_does_not_know_is_dead_with_no_member_and_no_problem()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Fake.FailNext(WireCall.DescribeGroup, new WireError.NotFound(KafkaResourceType.Group, "ghost"));

        KafkaGroupStatus notFound = await rig.Diagnostics.GetGroupAsync(Cluster, "ghost", ["orders"], Token);
        KafkaGroupStatus dead = await rig.Diagnostics.GetGroupAsync(Cluster, "never-seen", ["orders"], Token);

        notFound.State.Should().Be("Dead");
        notFound.Problem.Should().BeNull();
        dead.State.Should().Be("Dead");
        dead.Members.Should().BeEmpty();
        dead.Partitions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_group_says_what_to_grant()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Fake.Deny(KafkaAclOperation.Describe, KafkaResourceType.Group, "billing");

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);

        status.Problem!.Outcome.Should().Be(KafkaReadOutcome.NotAuthorized);
        status.Problem.Code.Should().Be("NotAuthorized");
        status.Problem.Sentence.Should().Be("Not available: needs `DESCRIBE` on group `billing`");
        status.OffsetsProblem!.Outcome.Should().Be(KafkaReadOutcome.NotAuthorized);
        status.OffsetsProblem.Topic.Should().Be("orders", "a support message names the topic whose offsets were withheld");
        status.Problem.Topic.Should().BeNull("the description is about the group, not a topic");
        status.State.Should().BeEmpty();
    }

    [Fact]
    public async Task The_end_offset_of_an_assigned_topic_is_read_even_when_it_was_not_asked_about()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 3);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", [], Token);

        status.Partitions.Should().ContainSingle().Which.EndOffset.Should().Be(3);
    }

    [Theory]
    [InlineData(null, 5L, KafkaMemberState.Behind, 5L)]
    [InlineData(3L, 5L, KafkaMemberState.Behind, 2L)]
    [InlineData(5L, 5L, KafkaMemberState.CaughtUp, 0L)]
    public async Task A_member_with_partitions_is_behind_until_its_committed_offsets_reach_the_end(
        long? committed, long produced, KafkaMemberState expected, long waiting)
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, (int) produced);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));

        if (committed is { } offset)
        {
            rig.Fake.Commit("billing", "orders", 0, offset);
        }

        KafkaMemberPlace place = (await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token)).PlaceOf("billing-a");

        place.State.Should().Be(expected);
        place.RecordsWaiting.Should().Be(waiting);
        place.HasNothingToRead.Should().Be(expected == KafkaMemberState.CaughtUp);
    }

    /// <summary>
    /// v249: a partition with nothing committed is counted from the first record it still holds, never from offset 0. A quiet member
    /// whose topic's records have all expired has nothing to read, and one with some left has those waiting.
    /// </summary>
    [Theory]
    [InlineData(5, 5L, KafkaMemberState.CaughtUp, 0L)]
    [InlineData(5, 3L, KafkaMemberState.Behind, 2L)]
    public async Task A_member_that_has_committed_nothing_is_counted_from_the_partitions_first_offset(
        int produced, long firstOffset, KafkaMemberState expected, long waiting)
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, produced);
        rig.Fake.DeleteRecords("orders", 0, firstOffset);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);
        KafkaMemberPlace place = status.PlaceOf("billing-a");

        status.Partitions.Should().ContainSingle().Which.FirstOffset.Should().Be(firstOffset);
        place.State.Should().Be(expected, "records that have expired are not waiting");
        place.RecordsWaiting.Should().Be(waiting);
    }

    /// <summary>
    /// REVIEW-B3-LAST F2: a committed offset that retention has passed counts from the first record the partition still holds, as a
    /// partition with nothing committed does. What expired is not waiting.
    /// </summary>
    [Fact]
    public async Task A_committed_offset_that_retention_has_passed_is_counted_from_the_partitions_first_offset()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 5);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));
        rig.Fake.Commit("billing", "orders", 0, 1);
        rig.Fake.DeleteRecords("orders", 0, 5);

        KafkaMemberPlace place = (await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token)).PlaceOf("billing-a");

        place.State.Should().Be(KafkaMemberState.CaughtUp, "the four records after the commit have expired, and nothing is left to read");
        place.RecordsWaiting.Should().Be(0);
    }

    /// <summary>
    /// mxxh: the first offset matters only where nothing is committed. When that read alone fails, a member with a commit is judged
    /// as it was read, one with none is unknown, and a partition nobody owns and nothing is committed to is not listed for it.
    /// </summary>
    [Fact]
    public async Task A_first_offset_that_could_not_be_read_matters_only_where_nothing_is_committed_and_lists_no_row_of_its_own()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 3);
        rig.Produce("orders", 0, 4);
        rig.Produce("orders", 1, 4);
        rig.Fake.SetGroupMembers(
            "billing",
            "Stable",
            Member("billing-a", new WirePartition("orders", 0)),
            Member("billing-b", new WirePartition("orders", 1)));
        rig.Fake.Commit("billing", "orders", 0, 4);
        rig.Fake.FailNextOffsets(WireOffsetSpec.Earliest, new WireError.Unavailable("RequestTimedOut", Retriable: true));

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);

        status.OffsetsProblem.Should().BeNull("the committed and end offsets were read");
        status.Partitions.Select(static x => x.Partition).Should().Equal([0, 1], "partition 2 has no owner and no commit, and a first offset alone lists no row");
        status.Partitions[0].Problem.Should().BeNull("a partition with a commit does not need its first offset");
        status.Partitions[1].Problem.Should().NotBeNull();
        status.PlaceOf("billing-a").State.Should().Be(KafkaMemberState.CaughtUp);
        status.PlaceOf("billing-b").State.Should().Be(KafkaMemberState.Unknown);
    }

    /// <summary>
    /// v249: only the member's own partitions are judged. A topic the host named that cannot be read is a problem on the status, and
    /// says nothing about a member that owns no partition of it.
    /// </summary>
    [Fact]
    public async Task A_topic_that_could_not_be_read_does_not_make_a_member_of_another_topic_unknown()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 4);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));
        rig.Fake.Commit("billing", "orders", 0, 4);

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders", "not-a-topic"], Token);

        status.OffsetsProblem.Should().NotBeNull("the topic that is not listed is still reported");
        status.OffsetsProblem!.Topic.Should().Be("not-a-topic");
        status.PlaceOf("billing-a").State.Should().Be(KafkaMemberState.CaughtUp);
    }

    /// <summary>
    /// v249: a topic whose offsets were withheld as a whole is a problem on each partition the member owns of it, so the member is
    /// unknown and a committed offset that was not read is never taken for "none committed".
    /// </summary>
    [Fact]
    public async Task A_member_whose_own_topic_could_not_be_read_is_unknown_and_never_behind()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Produce("orders", 0, 4);
        rig.Fake.SetGroupMembers("billing", "Stable", Member("billing-a", new WirePartition("orders", 0)));
        rig.Fake.Commit("billing", "orders", 0, 4);
        rig.Fake.FailNext(WireCall.ListOffsets, new WireError.Unavailable("RequestTimedOut", Retriable: true));

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);
        KafkaMemberPlace place = status.PlaceOf("billing-a");

        status.Partitions.Should().ContainSingle().Which.Problem.Should().NotBeNull("the topic's problem is on the partition the member owns");
        place.State.Should().Be(KafkaMemberState.Unknown);
        place.Detail.Should().StartWith("the offsets of orders partition 0 could not be read:");
    }

    [Fact]
    public async Task A_member_with_no_partition_stands_by_and_a_client_the_group_lacks_is_not_a_member()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Fake.SetGroupMembers(
            "billing", "Stable", Member("billing-a", new WirePartition("orders", 0)), Member("billing-b"));

        KafkaGroupStatus status = await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token);

        status.PlaceOf("billing-b").Should().BeEquivalentTo(new KafkaMemberPlace { State = KafkaMemberState.Standby });
        status.PlaceOf("billing-b").HasNothingToRead.Should().BeTrue();
        KafkaMemberPlace missing = status.PlaceOf("billing-c");
        missing.State.Should().Be(KafkaMemberState.NotAMember);
        missing.Detail.Should().Be("not a member of consumer group billing (Stable, 2 member(s))");
    }

    [Fact]
    public async Task A_member_of_a_group_that_could_not_be_read_is_unknown()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);
        rig.Fake.FailNext(WireCall.DescribeGroup, new WireError.Unavailable("NotCoordinatorForGroup", Retriable: true));

        KafkaMemberPlace place = (await rig.Diagnostics.GetGroupAsync(Cluster, "billing", ["orders"], Token)).PlaceOf("billing-a");

        place.State.Should().Be(KafkaMemberState.Unknown);
        place.Detail.Should().Be("consumer group billing could not be read: Not available: the cluster did not answer (NotCoordinatorForGroup)");
        place.HasNothingToRead.Should().BeFalse();
    }

    [Fact]
    public async Task Each_read_logs_event_9681_at_debug_with_its_outcome_code_only()
    {
        await using Rig rig = new();
        rig.Fake.CreateTopic("orders", 1);

        await rig.Diagnostics.GetClusterAsync(Cluster, ["orders"], Token);
        await rig.Diagnostics.GetTopicAsync(Cluster, "absent", Token);

        rig.Engine.Logs.Entries.Where(static x => x.EventId.Id == 9681).Select(static x => (x.Level, x.Message)).Should().Equal(
            (LogLevel.Debug, "Data Studio's diagnostics API read the cluster of cluster test: Read"),
            (LogLevel.Debug, "Data Studio's diagnostics API read the topic of cluster test: NotFound"));
    }
}
