using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C12's judgement over stated blocking graphs and no Postgres (X3): a head idle in its transaction is <c>idle-head</c> from
/// ten seconds of anybody's wait, any other head <c>long-chain</c> from two minutes, both Critical at five; a chain with no
/// head is not judged; every head is named only in a finding that is itself behind the activity gate, with the statements
/// to hand over - and C06, which now takes its facts from the same read of the lock table.
/// </summary>
public class BlockingChainCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly LockTarget Orders = new(LockTargetKind.OtherRelation, "a relation", "public", "orders");

    private static HealthEvaluation At(DateTimeOffset? now = null) => HealthEvaluation.At(now ?? Now);

    private static ChainSession Head(int pid = 5151, string? state = "idle in transaction", bool sameDatabase = true, bool vacuum = false) => new(
        new LockGraphNode
        {
            ProcessId = pid,
            SameDatabase = sameDatabase,
            User = sameDatabase ? "app_user" : null,
            Application = sameDatabase ? "billing-api" : null,
            ClientAddress = sameDatabase ? "10.1.2.3" : null,
            State = sameDatabase ? state : null,
            StateAge = TimeSpan.FromMinutes(4),
            TransactionAge = TimeSpan.FromMinutes(5),
            HasTransactionId = true,
            Vacuum = vacuum,
            HeldModes = ["AccessExclusiveLock"],
        },
        null);

    private static ChainSession Waiter(int pid, TimeSpan waited, params int[] blockedBy) => new(
        new LockGraphNode
        {
            ProcessId = pid,
            User = "app_user",
            Application = "billing-api",
            State = "active",
            WaitEventType = "Lock",
            WaitEvent = "relation",
            Wanted = new LockWanted("relation", "AccessShareLock", new LockedRelation("public", "orders", "r"), null, null, true, waited),
            BlockedBy = blockedBy.Length == 0 ? [5151] : blockedBy,
        },
        Orders);

    private static BlockingChainFacts Facts(params ChainSession[] sessions) =>
        new(Now, SessionVisibility.Everything, sessions, PostgresPlatform.SelfHosted);

    private static HealthVerdict Judge(BlockingChainFacts facts, DateTimeOffset? now = null) => new BlockingChainCheck().Evaluate(facts, At(now));

    [Fact]
    public void It_is_an_activity_check_of_the_connections_area_with_two_variants()
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(BlockingChainCheck));

        info.Code.Should().Be("C12");
        info.Area.Should().Be(HealthArea.Connections);
        info.Tier.Should().Be(HealthCostTier.Catalog, "it runs in the regular sweep, from its one read of the lock table");
        info.Gate.Should().Be(HealthGate.Activity, "every finding names the sessions at the heads of the chains (D29)");
        info.Variants.Should().Equal(BlockingChainCheck.IdleHead, BlockingChainCheck.LongChain);
        info.DocsHeading.Should().Be("## C12 Head of a blocking chain");
    }

    [Fact]
    public void Nothing_waiting_is_quiet_and_says_its_lines()
    {
        HealthVerdict verdict = Judge(Facts());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("10 s").And.Contain("2 min");
    }

    [Fact]
    public void A_head_idle_in_its_transaction_is_quiet_under_ten_seconds_warns_at_ten_and_is_critical_at_five_minutes()
    {
        Judge(Facts(Head(), Waiter(4242, TimeSpan.FromSeconds(9)))).Findings.Should().BeEmpty();

        HealthFinding warning = Judge(Facts(Head(), Waiter(4242, TimeSpan.FromSeconds(10)))).Findings.Should().ContainSingle().Subject;
        warning.Variant.Should().Be(BlockingChainCheck.IdleHead);
        warning.Key.Should().Be("C12|idle-head");
        warning.Severity.Should().Be(HealthSeverity.Warning);
        warning.Title.Should().Be("A session idle in its transaction has held up 1 session for 10 s");

        Judge(Facts(Head(), Waiter(4242, TimeSpan.FromMinutes(5)))).Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
        Judge(Facts(Head(state: "idle in transaction (aborted)"), Waiter(4242, TimeSpan.FromSeconds(12)))).Findings.Single().Variant
            .Should().Be(BlockingChainCheck.IdleHead, "an aborted transaction still holds its locks until it is rolled back");
    }

    [Fact]
    public void Any_other_head_is_a_long_chain_from_two_minutes_and_critical_at_five()
    {
        Judge(Facts(Head(state: "active"), Waiter(4242, TimeSpan.FromSeconds(119)))).Findings.Should().BeEmpty();

        HealthFinding warning = Judge(Facts(Head(state: "active"), Waiter(4242, TimeSpan.FromMinutes(2)))).Findings.Should().ContainSingle().Subject;
        warning.Variant.Should().Be(BlockingChainCheck.LongChain);
        warning.Severity.Should().Be(HealthSeverity.Warning);
        warning.Title.Should().Be("A blocking chain has held up 1 session for 2 min");

        Judge(Facts(Head(state: "active"), Waiter(4242, TimeSpan.FromMinutes(5)))).Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void A_wait_read_a_while_ago_is_judged_as_of_now()
    {
        BlockingChainFacts facts = Facts(Head(state: "active"), Waiter(4242, TimeSpan.FromSeconds(30)));

        Judge(facts).Findings.Should().BeEmpty();
        Judge(facts, Now.AddMinutes(2)).Findings.Should().ContainSingle().Which.Title.Should().EndWith("2 min");
    }

    [Fact]
    public void Both_kinds_at_once_are_two_findings_and_a_waiter_blocked_by_two_heads_is_held_up_once()
    {
        HealthVerdict verdict = Judge(Facts(
            Head(5151, "idle in transaction"),
            Head(6161, "active"),
            Waiter(4242, TimeSpan.FromMinutes(3), 5151, 6161),
            Waiter(4343, TimeSpan.FromSeconds(20), 5151)));

        verdict.Findings.Select(static x => x.Key).Should().BeEquivalentTo(["C12|idle-head", "C12|long-chain"]);

        HealthFinding idle = verdict.Findings.Single(static x => x.Variant == BlockingChainCheck.IdleHead);
        idle.Evidence.Single(static x => x.Label == "Sessions held up").Value.Should().Be("2");
        idle.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().StartWith("pid 5151, role app_user")
            .And.Contain("idle in transaction for 4 min").And.Contain("holds AccessExclusiveLock")
            .And.Contain("holds up 2 sessions, the longest waiting 3 min for a relation in AccessShareLock");

        HealthFinding chain = verdict.Findings.Single(static x => x.Variant == BlockingChainCheck.LongChain);
        chain.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().StartWith("pid 6161");
        chain.Evidence.Single(static x => x.Label == "Sessions held up").Value.Should().Be("1");
    }

    [Fact]
    public void Several_idle_heads_are_one_finding_the_longest_wait_first()
    {
        HealthFinding finding = Judge(Facts(
            Head(5151), Waiter(4242, TimeSpan.FromSeconds(15), 5151),
            Head(6161), Waiter(4343, TimeSpan.FromMinutes(1), 6161))).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("2 sessions idle in their transactions are holding up 2 sessions, the longest wait 1 min");
        finding.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().StartWith("pid 6161");
        finding.Evidence.Single(static x => x.Label == "Head 2").Value.Should().StartWith("pid 5151");
        finding.Evidence.Single(static x => x.Label == "Chains").Value.Should().Be("2");
    }

    [Fact]
    public void An_idle_head_hands_over_the_terminate_statement_and_its_remedy_is_the_servers_idle_timeout()
    {
        HealthFinding finding = Judge(Facts(Head(), Waiter(4242, TimeSpan.FromSeconds(30)))).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Hand over 1").Value.Should().StartWith("select pg_terminate_backend(5151);")
            .And.Contain("pg_cancel_backend does nothing to a session idle in its transaction");

        HealthRemedy remedy = finding.Remedy!;
        remedy.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        remedy.Code.Should().EndWith("idle_in_transaction_session_timeout = '5min'");
        remedy.Explanation.Should().Contain("rolling the transaction back and releasing its locks").And.Contain("postgresql.conf");
        finding.WhyItMatters.Should().Contain("The studio never ends a session");
    }

    [Fact]
    public void A_long_chain_hands_over_cancel_then_terminate_as_a_repair_run_by_hand()
    {
        HealthFinding finding = Judge(Facts(Head(state: "active"), Waiter(4242, TimeSpan.FromMinutes(3)))).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Hand over 1").Value.Should().StartWith("select pg_cancel_backend(5151);")
            .And.Contain("select pg_terminate_backend(5151);");

        HealthRemedy remedy = finding.Remedy!;
        remedy.Kind.Should().Be(HealthRemedyKind.Repair);
        remedy.Heading.Should().Be(HealthRemedy.RepairHeading, "the studio never runs it");
        remedy.Code.Should().Contain("select pg_cancel_backend(5151);").And.Contain("select pg_terminate_backend(5151);")
            .And.Contain("pid 5151, active for 4 min");
        remedy.Explanation.Should().Contain("pg_signal_backend").And.Contain(HealthRemedy.RepairHandOverNote);
    }

    [Fact]
    public void A_VACUUM_at_the_head_gets_C06s_advice()
    {
        HealthFinding finding = Judge(Facts(Head(state: "active", vacuum: true), Waiter(4242, TimeSpan.FromMinutes(3)))).Findings.Single();

        finding.WhyItMatters.Should().Contain("The head of the chain is a VACUUM");
        finding.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().Contain("a VACUUM, active");
    }

    [Fact]
    public void A_head_in_another_database_is_judged_by_its_chain_alone_and_named_by_its_process_id()
    {
        HealthFinding finding = Judge(Facts(Head(state: null, sameDatabase: false), Waiter(4242, TimeSpan.FromMinutes(3)))).Findings.Single();

        finding.Variant.Should().Be(BlockingChainCheck.LongChain, "its state is another database's, so it is never called idle");
        finding.Evidence.Single(static x => x.Label == "Head 1 (longest wait)").Value.Should().Contain("in another database")
            .And.NotContain("app_user");
    }

    [Fact]
    public void Sessions_that_wait_on_each_other_have_no_head_and_are_not_judged()
    {
        ChainSession seven = Waiter(7, TimeSpan.FromMinutes(9), 8);
        ChainSession eight = Waiter(8, TimeSpan.FromMinutes(9), 7);

        HealthVerdict verdict = Judge(Facts(seven, eight));

        verdict.Findings.Should().BeEmpty("Postgres' deadlock detector ends a cycle; there is no session to hand over");
    }

    [Fact]
    public void A_head_that_waits_for_a_lock_the_read_did_not_list_is_no_head()
    {
        ChainSession waitingHead = new(Head(state: "active").Node with { WaitEventType = "Lock" }, null);

        Judge(Facts(waitingHead, Waiter(4242, TimeSpan.FromMinutes(9)))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Its_link_opens_Sessions_on_the_Blocking_tab_and_its_caveat_says_how_waits_are_measured()
    {
        HealthFinding finding = Judge(Facts(Head(), Waiter(4242, TimeSpan.FromSeconds(30)))).Findings.Single();

        finding.Link!.Route.Should().Be(SessionLinks.Route);
        finding.Link.Query.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string?>("view", "blocking"));
        finding.Caveat.Should().Contain("pg_locks.waitstart").And.Contain("Database › Sessions › Blocking");
    }

    [Fact]
    public void In_Postgres_Studios_words_the_remedy_names_no_store()
    {
        HealthFinding finding = new BlockingChainCheck().Evaluate(
            Facts(Head(), Waiter(4242, TimeSpan.FromSeconds(30))),
            HealthEvaluation.At(Now, text: PostgresStudioText.Instance)).Findings.Single();

        finding.Remedy!.Explanation.Should().NotContain("Marten").And.NotContain("HotCold");
        finding.Remedy.Code.Should().StartWith("# " + PostgresStudioText.Instance.ServerSettingComment);
    }

    [Fact]
    public void C06_takes_the_waiters_past_its_floor_and_their_blockers_from_the_same_read()
    {
        LockTableRead read = LockTableRead.Empty(Now) with
        {
            Nodes =
            [
                new LockGraphNode { ProcessId = 5151, State = "idle in transaction", User = "app_user", TransactionAge = TimeSpan.FromMinutes(5), HasTransactionId = true },
                new LockGraphNode { ProcessId = 6161, Known = false },
                Waiter(4242, TimeSpan.FromMinutes(2), 5151, 6161).Node,
                Waiter(4343, TimeSpan.FromMilliseconds(400), 5151).Node,
            ],
        };

        LockWaitFacts facts = LockWaitCheck.FactsOf(read, SessionLockLabels.RelationsOnly("default", new DatabaseObjectClassifier([]), PostgresSessionLockLabeler.Wording), Now);

        facts.Waiters.Should().ContainSingle("a wait shorter than the candidate floor is not read").Which.ProcessId.Should().Be(4242);
        facts.Waiters[0].Target.Label.Should().Be("a relation");
        facts.Waiters[0].BlockedBy.Should().Equal(5151, 6161);
        facts.Blockers.Should().ContainSingle("a blocker that ended as the read ran has no row, as 0.5's Blockers had none")
            .Which.Should().Match<LockBlocker>(static x => x.ProcessId == 5151 && x.State == "idle in transaction" && !x.Waiting);
        facts.Total.Should().Be(1);
    }
}
