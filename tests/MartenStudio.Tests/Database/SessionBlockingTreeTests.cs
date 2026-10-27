using PostgresStudio.Services.Database;
using PostgresStudio.Services.Postgres;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What Sessions › Blocking is made of for one visitor (X3), with no database: the counts every visitor the summary admits
/// may see, and the trees past the gate - each waiter's target in the studio's words, the relation masked per D27, and the
/// statements to hand over only to a head that holds the others up.
/// </summary>
public class SessionBlockingTreeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly SessionLockLabels Labels =
        SessionLockLabels.RelationsOnly("default", new DatabaseObjectClassifier([]), PostgresSessionLockLabeler.Wording);

    private static LockGraphNode Head(int pid = 5151, string state = "idle in transaction") => new()
    {
        ProcessId = pid,
        User = "app_user",
        State = state,
        HeldModes = ["AccessExclusiveLock"],
    };

    private static LockGraphNode Waiter(int pid, string schema, string table, TimeSpan waited, params int[] blockedBy) => new()
    {
        ProcessId = pid,
        State = "active",
        WaitEventType = "Lock",
        Wanted = new LockWanted("relation", "AccessShareLock", new LockedRelation(schema, table, "r"), null, null, true, waited),
        BlockedBy = blockedBy,
    };

    private static LockTableRead Read(params LockGraphNode[] nodes) => LockTableRead.Empty(Now) with { Nodes = nodes };

    [Fact]
    public void A_relation_in_a_schema_the_visitor_may_see_is_named_and_one_in_a_withheld_schema_is_the_token_alone()
    {
        IReadOnlyList<BlockingChainView> tree = SessionActivityService.TreeOf(
            Read(Head(), Waiter(4242, "public", "orders", TimeSpan.FromMinutes(1), 5151), Waiter(4343, "payroll", "salaries", TimeSpan.FromSeconds(30), 5151)),
            Labels,
            new HashSet<string>(StringComparer.Ordinal) { "payroll" });

        BlockingChainView chain = tree.Should().ContainSingle().Subject;
        chain.Head.ProcessId.Should().Be(5151);
        chain.Held.Should().Be(2);
        chain.LongestWait.Should().Be(TimeSpan.FromMinutes(1));

        chain.Head.Waiters.Select(static x => (x.ProcessId, x.Relation, x.Target)).Should().Equal(
            (4242, "public.orders", "a relation"),
            (4343, WithheldNames.Token, "a relation"));
        chain.Head.Waiters.Should().OnlyContain(static x => !(x.Target ?? string.Empty).Contains("payroll", StringComparison.Ordinal)
            && !(x.Relation ?? string.Empty).Contains("salaries", StringComparison.Ordinal), "a withheld schema's relation is not named at all");
    }

    [Fact]
    public void When_the_withheld_schemas_cannot_be_read_no_relation_is_named()
    {
        IReadOnlyList<BlockingChainView> tree = SessionActivityService.TreeOf(
            Read(Head(), Waiter(4242, "public", "orders", TimeSpan.FromMinutes(1), 5151)), Labels, withheld: null);

        tree.Single().Head.Waiters.Single().Relation.Should().BeNull();
    }

    [Fact]
    public void A_relation_named_like_Martens_is_labelled_and_never_named_in_Postgres_Studio()
    {
        IReadOnlyList<BlockingChainView> tree = SessionActivityService.TreeOf(
            Read(Head(), Waiter(4242, "public", "mt_doc_order", TimeSpan.FromMinutes(1), 5151)), Labels, new HashSet<string>(StringComparer.Ordinal));

        BlockingNodeView waiter = tree.Single().Head.Waiters.Single();
        waiter.Target.Should().Be("a relation named like Marten's");
        waiter.Relation.Should().BeNull("its name can carry a store's alias or a tenant, and Marten Studio is where it is read");
    }

    [Fact]
    public void A_head_idle_in_its_transaction_is_handed_terminate_alone_and_a_running_one_cancel_too()
    {
        BlockingChainView idle = SessionActivityService.TreeOf(
            Read(Head(), Waiter(4242, "public", "orders", TimeSpan.FromMinutes(1), 5151)), Labels, new HashSet<string>()).Single();

        idle.IdleHead.Should().BeTrue();
        idle.Terminate.Should().Be("select pg_terminate_backend(5151);");
        idle.Cancel.Should().BeNull("pg_cancel_backend does nothing to a session idle in its transaction");

        BlockingChainView running = SessionActivityService.TreeOf(
            Read(Head(state: "active"), Waiter(4242, "public", "orders", TimeSpan.FromMinutes(1), 5151)), Labels, new HashSet<string>()).Single();

        running.IdleHead.Should().BeFalse();
        running.Cancel.Should().Be("select pg_cancel_backend(5151);");
        running.Terminate.Should().Be("select pg_terminate_backend(5151);");
    }

    [Fact]
    public void A_chain_with_no_head_is_drawn_and_handed_nothing()
    {
        BlockingChainView chain = SessionActivityService.TreeOf(
            Read(Waiter(7, "public", "a", TimeSpan.FromSeconds(4), 8), Waiter(8, "public", "b", TimeSpan.FromSeconds(3), 7)), Labels, new HashSet<string>())
            .Single();

        chain.HeadWaits.Should().BeTrue();
        chain.Terminate.Should().BeNull();
        chain.Cancel.Should().BeNull();
        chain.Head.Waiters.Single().Waiters.Single().Repeat.Should().Be(BlockingRepeat.Cycle);
    }

    [Fact]
    public void The_counts_name_nobody_and_count_a_waiter_blocked_by_two_once()
    {
        SessionBlockingCounts counts = SessionActivityService.CountsOf(Read(
            Head(5151),
            Head(6161, "active"),
            Waiter(4242, "public", "orders", TimeSpan.FromMinutes(3), 5151, 6161),
            Waiter(4343, "public", "orders", TimeSpan.FromSeconds(20), 5151),
            Waiter(7, "public", "a", TimeSpan.FromSeconds(4), 8),
            Waiter(8, "public", "b", TimeSpan.FromSeconds(3), 7)));

        counts.Should().Be(new SessionBlockingCounts(2, 4, TimeSpan.FromMinutes(3), 1, 1));
        SessionActivityService.CountsOf(null).Should().Be(SessionBlockingCounts.None);
        SessionActivityService.CountsOf(Read()).Should().Be(SessionBlockingCounts.None);
    }
}
