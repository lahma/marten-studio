using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// X3's tree builder over stated graphs and no Postgres: a head and its waiters nested, several heads, a waiter blocked by
/// two, cycles - impossible for long, and guarded all the same - a waiter whose blocker the read did not list, and a chain
/// deeper than the page draws.
/// </summary>
public class BlockingForestTests
{
    /// <summary>A session of a stated graph: its process id, who blocks it, and how long it has waited in seconds.</summary>
    private sealed record Node(int Pid, int[] By, double? Waited = null);

    private static IReadOnlyList<BlockingChain<Node>> Build(params Node[] nodes) =>
        BlockingForest.Build(nodes, static x => x.Pid, static x => x.By, static x => x.Waited is { } s ? TimeSpan.FromSeconds(s) : null);

    private static int[] Pids(BlockingBranch<Node> branch) => [.. branch.Waiters.Select(static x => x.Node.Pid)];

    [Fact]
    public void Nothing_waits_and_there_is_no_chain()
    {
        Build().Should().BeEmpty();
        Build(new Node(1, []), new Node(2, [])).Should().BeEmpty("sessions that block nobody and wait on nobody make no chain");
    }

    [Fact]
    public void A_head_has_its_waiters_below_it_the_longest_wait_first()
    {
        BlockingChain<Node> chain = Build(new Node(10, []), new Node(11, [10], 5), new Node(12, [10], 30)).Should().ContainSingle().Subject;

        chain.HeadWaits.Should().BeFalse();
        chain.Root.Node.Pid.Should().Be(10);
        chain.Root.Depth.Should().Be(0);
        Pids(chain.Root).Should().Equal(12, 11);
        chain.Blocked.Should().BeEquivalentTo([11, 12]);
        BlockingForest.Longest(chain.Root, static x => x.Waited is { } s ? TimeSpan.FromSeconds(s) : null).Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_waiter_that_blocks_others_is_drawn_with_its_own_waiters_nested()
    {
        BlockingChain<Node> chain = Build(new Node(10, []), new Node(11, [10], 50), new Node(12, [11], 20), new Node(13, [12], 1))
            .Should().ContainSingle().Subject;

        chain.Root.Node.Pid.Should().Be(10);
        BlockingBranch<Node> link = chain.Root.Waiters.Should().ContainSingle().Subject;
        link.Node.Pid.Should().Be(11);
        link.Depth.Should().Be(1);
        link.Waiters.Should().ContainSingle().Which.Waiters.Should().ContainSingle().Which.Node.Pid.Should().Be(13);
        chain.Blocked.Should().BeEquivalentTo([11, 12, 13]);
        BlockingForest.Flatten(chain.Root).Select(static x => x.Node.Pid).Should().Equal(10, 11, 12, 13);
    }

    [Fact]
    public void Several_heads_are_several_chains_the_most_held_up_first()
    {
        IReadOnlyList<BlockingChain<Node>> chains = Build(
            new Node(20, []), new Node(21, [20], 5),
            new Node(30, []), new Node(31, [30], 1), new Node(32, [30], 2));

        chains.Select(static x => x.Root.Node.Pid).Should().Equal(30, 20);
        chains.Should().OnlyContain(static x => !x.HeadWaits);
    }

    [Fact]
    public void A_waiter_blocked_by_two_is_drawn_under_both_its_own_waiters_once_and_counted_in_both_chains()
    {
        IReadOnlyList<BlockingChain<Node>> chains = Build(
            new Node(1, []),
            new Node(2, []),
            new Node(3, [1, 2], 40),
            new Node(4, [3], 10));

        chains.Should().HaveCount(2);

        BlockingChain<Node> first = chains.Single(static x => x.Root.Node.Pid == 1);
        BlockingChain<Node> second = chains.Single(static x => x.Root.Node.Pid == 2);

        BlockingBranch<Node> drawn = first.Root.Waiters.Should().ContainSingle().Subject;
        drawn.Repeat.Should().Be(BlockingRepeat.None);
        Pids(drawn).Should().Equal(4);

        BlockingBranch<Node> repeated = second.Root.Waiters.Should().ContainSingle().Subject;
        repeated.Node.Pid.Should().Be(3);
        repeated.Repeat.Should().Be(BlockingRepeat.ShownElsewhere);
        repeated.Waiters.Should().BeEmpty("its waiters are drawn once, under the first head");

        first.Blocked.Should().BeEquivalentTo([3, 4]);
        second.Blocked.Should().BeEquivalentTo([3, 4], "a chain counts everybody it holds up, whichever branch drew them");
    }

    [Fact]
    public void Sessions_waiting_on_each_other_are_a_chain_with_no_head_and_the_cycle_is_not_followed()
    {
        BlockingChain<Node> chain = Build(new Node(7, [8], 3), new Node(8, [7], 2)).Should().ContainSingle().Subject;

        chain.HeadWaits.Should().BeTrue("a deadlock forming has no session that holds the others up alone");
        chain.Root.Node.Pid.Should().Be(7, "it is drawn from the lowest process id");

        BlockingBranch<Node> eight = chain.Root.Waiters.Should().ContainSingle().Subject;
        eight.Node.Pid.Should().Be(8);
        BlockingBranch<Node> back = eight.Waiters.Should().ContainSingle().Subject;
        back.Node.Pid.Should().Be(7);
        back.Repeat.Should().Be(BlockingRepeat.Cycle);
        back.Waiters.Should().BeEmpty();
    }

    [Fact]
    public void A_cycle_behind_a_head_is_guarded_too()
    {
        // 2 waits on the head and on 3 (pg_blocking_pids names a session ahead in the queue too), and 3 waits on 2.
        BlockingChain<Node> chain = Build(new Node(1, []), new Node(2, [1, 3], 9), new Node(3, [2], 4)).Should().ContainSingle().Subject;

        chain.HeadWaits.Should().BeFalse();
        chain.Root.Node.Pid.Should().Be(1);
        BlockingForest.Flatten(chain.Root).Select(static x => (x.Node.Pid, x.Repeat)).Should().Equal(
            (1, BlockingRepeat.None), (2, BlockingRepeat.None), (3, BlockingRepeat.None), (2, BlockingRepeat.Cycle));
        chain.Blocked.Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void A_waiter_whose_blocker_the_read_did_not_list_is_a_chain_of_its_own_with_no_head()
    {
        BlockingChain<Node> chain = Build(new Node(5, [99], 12)).Should().ContainSingle().Subject;

        chain.HeadWaits.Should().BeTrue();
        chain.Root.Node.Pid.Should().Be(5);
        chain.Blocked.Should().BeEmpty();
    }

    [Fact]
    public void A_session_listed_as_its_own_blocker_is_not_its_own_waiter()
    {
        BlockingChain<Node> chain = Build(new Node(1, []), new Node(2, [2, 1], 3)).Should().ContainSingle().Subject;

        chain.Root.Node.Pid.Should().Be(1);
        Pids(chain.Root).Should().Equal(2);
    }

    [Fact]
    public void A_chain_deeper_than_the_page_draws_stops_and_the_rest_is_a_chain_of_its_own()
    {
        Node[] nodes = [new Node(1, []), .. Enumerable.Range(2, 100).Select(static x => new Node(x, [x - 1], 100 - x))];

        IReadOnlyList<BlockingChain<Node>> chains = Build(nodes);

        BlockingChain<Node> head = chains.Single(static x => !x.HeadWaits);
        List<BlockingBranch<Node>> drawn = [.. BlockingForest.Flatten(head.Root)];

        drawn.Should().HaveCount(BlockingForest.MaxDepth + 1);
        drawn[^1].Repeat.Should().Be(BlockingRepeat.TooDeep);
        head.Blocked.Should().HaveCount(100, "the chain still counts everybody it holds up");

        chains.SelectMany(static x => BlockingForest.Flatten(x.Root)).Select(static x => x.Node.Pid).Distinct().Should().HaveCount(101,
            "every session is drawn somewhere");
    }

    [Fact]
    public void The_lock_table_read_folds_its_first_row_and_finds_who_holds_a_key()
    {
        LockTableRead read = LockTableRead.Fold(
            [
                new LockTableRow
                {
                    Visibility = new SessionVisibility(false, 3),
                    ServerVersion = 170005,
                    ExclusiveRelations = [16385u],
                    StrongRelations = [16385u, 16390u],
                    RelationLockPids = [11, 12],
                    RelationLockCounts = [2, 7],
                    AdvisoryPids = [11, 12, 12],
                    AdvisoryKeys = [4444, 4444, 99],
                    WaitingTotal = 1,
                },
                new LockTableRow { Node = new LockGraphNode { ProcessId = 12, BlockedBy = [11] } },
            ],
            new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));

        read.Visibility.HiddenSessions.Should().Be(3);
        read.ServerVersion.Should().Be(170005);
        read.ExclusiveRelations.Should().Equal(16385u);
        read.StrongRelations.Should().Equal(16385u, 16390u);
        read.RelationLocks.Should().BeEquivalentTo(new Dictionary<int, long> { [11] = 2, [12] = 7 });
        read.HoldersOf([4444L]).Should().Equal(11, 12);
        read.HoldersOf([99L, 123L]).Should().Equal(12);
        read.HoldersOf([]).Should().BeEmpty();
        read.Nodes.Should().ContainSingle().Which.ProcessId.Should().Be(12);

        FluentActions.Invoking(() => LockTableRead.Fold([], DateTimeOffset.UnixEpoch)).Should().Throw<InvalidOperationException>(
            "the statement always returns its first row");
    }
}
