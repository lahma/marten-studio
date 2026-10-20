using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C04, C05, C06 and C07's judgement, over stated facts and no Postgres - and the rule every one of them keeps:
/// the count and the ages are the database's, and who the sessions are is evidence behind the activity gate.
/// </summary>
public class SessionChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Connected = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    /// <summary>The values of the identifying fields every stated session carries - none may reach an ungated line.</summary>
    private static readonly string[] Identifying = ["4242", "app_user", "billing-api", "10.1.2.3", "2026-10-15 08:00:00Z", "gid-orphan", "tx_owner"];

    private static HealthEvaluation At(DateTimeOffset? now = null) => HealthEvaluation.At(now ?? Now);

    private static IdleSession Idle(TimeSpan idle, bool written = true, bool snapshot = false, long relationLocks = 0, bool daemon = false, int pid = 4242) =>
        new(pid, "app_user", "billing-api", "10.1.2.3", Connected, "idle in transaction", idle, idle + TimeSpan.FromSeconds(5), written, snapshot, relationLocks, daemon);

    private static IdleInTransactionFacts IdleFacts(params IdleSession[] sessions) =>
        new(Now, SessionVisibility.Everything, sessions.Length, sessions);

    private static RunningStatement Running(TimeSpan running, string? wait = null) =>
        new(4242, "app_user", "billing-api", "10.1.2.3", Connected, wait, running);

    private static LockWaiter Waiter(TimeSpan waited, LockTarget target, bool exact = true, int pid = 4242, IReadOnlyList<int>? blockedBy = null) =>
        new(pid, "app_user", "billing-api", "10.1.2.3", "ShareLock", target, exact, waited, blockedBy ?? [5151]);

    private static LockBlocker Blocker(int pid = 5151, string state = "idle in transaction", bool waiting = false) =>
        new(pid, true, state, "app_user", "billing-api", "10.1.2.3", TimeSpan.FromMinutes(4), true, waiting);

    private static readonly LockTarget StreamRow = new(LockTargetKind.EventTable, "an mt_streams row (sample_events)", "sample_events", "mt_streams", Row: true);

    private static readonly LockTarget ApplyLock = new(LockTargetKind.ApplyChangesLock, "Marten's apply-changes lock (ApplyChangesLockId 4004)");

    private static void AssertGated(HealthFinding finding)
    {
        foreach (HealthEvidence evidence in finding.Evidence)
        {
            if (evidence.Gate == HealthGate.Activity)
            {
                continue;
            }

            foreach (string value in Identifying)
            {
                evidence.Value.Should().NotContain(value, "{0} is shown to anybody the store policy passes, and names no session", evidence.Label);
            }
        }

        foreach (string value in Identifying)
        {
            finding.Title.Should().NotContain(value);
            finding.WhyItMatters.Should().NotContain(value);
            (finding.Caveat ?? string.Empty).Should().NotContain(value);
        }

        finding.Evidence.Should().Contain(static x => x.Gate == HealthGate.Activity, "who the sessions are is still there, for a visitor through the gate");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    // ----------------------------------------------------------------------------------------------
    // C04
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C04_is_quiet_under_a_minute()
    {
        HealthVerdict verdict = new IdleInTransactionCheck().Evaluate(IdleFacts(Idle(TimeSpan.FromSeconds(59))), At());

        verdict.Findings.Should().BeEmpty();
    }

    [Fact]
    public void C04_warns_at_a_minute_and_is_critical_at_ten()
    {
        HealthFinding warning = new IdleInTransactionCheck().Evaluate(IdleFacts(Idle(TimeSpan.FromMinutes(1))), At()).Findings.Single();
        warning.Severity.Should().Be(HealthSeverity.Warning);
        warning.Code.Should().Be("C04");
        AssertGated(warning);

        HealthFinding critical = new IdleInTransactionCheck().Evaluate(
            IdleFacts(Idle(TimeSpan.FromMinutes(10)), Idle(TimeSpan.FromMinutes(2), pid: 4343)), At()).Findings.Single();
        critical.Severity.Should().Be(HealthSeverity.Critical);
        critical.Evidence.Should().Contain(new HealthEvidence("Sessions idle in a transaction", "2"));
        critical.Evidence.Should().Contain(new HealthEvidence("Longest idle", "10 min"));
        critical.Evidence.Single(static x => x.Label == "Session 1 (longest)").Value.Should().StartWith("pid 4242, role app_user, application 'billing-api', from 10.1.2.3");
        critical.Evidence.Single(static x => x.Label == "Session 1 (longest)").Gate.Should().Be(HealthGate.Activity);
    }

    [Fact]
    public void C04_does_not_raise_a_session_that_pins_nothing_such_as_Martens_own_daemon_leader()
    {
        // Marten 9.39's HotCold leader: idle in a transaction for as long as it leads, holding only an advisory lock.
        HealthVerdict verdict = new IdleInTransactionCheck().Evaluate(
            IdleFacts(Idle(TimeSpan.FromMinutes(30), written: false, snapshot: false, relationLocks: 0, daemon: true)), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Marten's own daemon leadership").And.Contain("pins nothing");

        foreach (string value in Identifying)
        {
            verdict.Note.Should().NotContain(value, "a status row is shown at the check's own gate, the database's, and names no session");
        }
    }

    /// <summary>
    /// A daemon-lock holder that has written or locked a relation is not the leader's shape, so it is raised even inside the
    /// leader's bound - two minutes idle here. One holding only its lock and a snapshot is the leader, inside the bound
    /// (<see cref="C04_does_not_raise_Martens_leader_inside_the_keepalive_bound_and_raises_it_past_it" />).
    /// </summary>
    [Theory]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    public void C04_raises_a_daemon_lock_holder_that_has_written_or_locked_a_relation_inside_the_leaders_bound(bool written, bool snapshot, long relationLocks)
    {
        new IdleInTransactionCheck().Evaluate(
            IdleFacts(Idle(TimeSpan.FromMinutes(2), written, snapshot, relationLocks, daemon: true)), At()).Findings.Should().ContainSingle();
    }

    /// <summary>
    /// Marten's own HotCold leader with a transaction-scoped lock at Weasel 9.45: idle in its leadership transaction, with a
    /// snapshot, for about 61 s between DistributedLock's keepalives. Recognised by the daemon lock it holds; quiet inside the
    /// bound, raised past it.
    /// </summary>
    [Fact]
    public void C04_does_not_raise_Martens_leader_inside_the_keepalive_bound_and_raises_it_past_it()
    {
        ConnectionThresholds.DaemonLeaderIdleBound.Should().Be(TimeSpan.FromMinutes(3));

        IdleInTransactionFacts leader = IdleFacts(Idle(TimeSpan.FromSeconds(61), written: false, snapshot: true, daemon: true));

        HealthVerdict inside = new IdleInTransactionCheck().Evaluate(leader, At());
        inside.Findings.Should().BeEmpty("an idle stretch between keepalives is the leader's expected state");
        inside.Note.Should().Contain("holding Marten's own daemon leadership lock").And.Contain("T14 judges the snapshot it holds");

        foreach (string value in Identifying)
        {
            inside.Note.Should().NotContain(value, "a status row names no session");
        }

        new IdleInTransactionCheck().Evaluate(leader, At(Now.AddMinutes(2))).Findings.Should().ContainSingle(
            "61 s idle when read and two minutes since is past the bound: a leader whose keepalive has stopped holds back vacuum");
    }

    [Fact]
    public void C04_still_raises_a_session_idle_in_its_transaction_that_holds_no_daemon_lock()
    {
        new IdleInTransactionCheck().Evaluate(IdleFacts(Idle(TimeSpan.FromSeconds(61), written: false, snapshot: true, daemon: false)), At())
            .Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    [Fact]
    public void C04_still_raises_the_other_sessions_beside_a_leader_inside_the_bound()
    {
        HealthFinding finding = new IdleInTransactionCheck().Evaluate(
            IdleFacts(
                Idle(TimeSpan.FromSeconds(61), written: false, snapshot: true, daemon: true, pid: 1),
                Idle(TimeSpan.FromMinutes(2), pid: 2)),
            At()).Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Sessions idle in a transaction", "1"));
        finding.WhyItMatters.Should().Contain("holding Marten's own daemon leadership lock");
    }

    [Fact]
    public void C04_judges_a_snapshot_at_the_evaluations_time()
    {
        IdleInTransactionFacts facts = IdleFacts(Idle(TimeSpan.FromSeconds(2)));

        new IdleInTransactionCheck().Evaluate(facts, At()).Findings.Should().BeEmpty();
        new IdleInTransactionCheck().Evaluate(facts, At(Now.AddSeconds(59))).Findings.Should().ContainSingle(
            "two seconds idle when read and fifty-nine since is a minute and a second, had nothing changed");
    }

    [Fact]
    public void C04_says_it_cannot_see_into_other_roles_sessions_rather_than_that_there_are_none()
    {
        IdleInTransactionFacts facts = new(Now, new SessionVisibility(false, 3), 1, [Idle(TimeSpan.FromMinutes(2))]);

        HealthFinding finding = new IdleInTransactionCheck().Evaluate(facts, At()).Findings.Single();

        finding.Caveat.Should().Contain("cannot see into 3 sessions").And.Contain("pg_read_all_stats");
        finding.Caveat.Should().Contain("Database › Sessions", "the statement is on the page, behind the gate, and never in the finding");
    }

    // ----------------------------------------------------------------------------------------------
    // C05
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C05_warns_at_five_minutes_and_carries_no_statement_text()
    {
        new LongRunningStatementCheck().Evaluate(new LongRunningFacts(Now, SessionVisibility.Everything, 1, [Running(TimeSpan.FromMinutes(4.9))]), At())
            .Findings.Should().BeEmpty();

        HealthFinding finding = new LongRunningStatementCheck().Evaluate(
            new LongRunningFacts(Now, SessionVisibility.Everything, 2, [Running(TimeSpan.FromMinutes(7), "Lock"), Running(TimeSpan.FromMinutes(5))]),
            At()).Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Longest", "7 min"));
        finding.Evidence.Should().Contain(new HealthEvidence("Waiting rather than working", "1 (Lock 1)"));
        AssertGated(finding);
    }

    // ----------------------------------------------------------------------------------------------
    // C06
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C06_is_quiet_under_thirty_seconds_warns_at_thirty_and_is_critical_at_five_minutes()
    {
        LockWaitFacts Facts(TimeSpan waited) => new(Now, SessionVisibility.Everything, 1, [Waiter(waited, StreamRow)], [Blocker()]);

        new LockWaitCheck().Evaluate(Facts(TimeSpan.FromSeconds(29)), At()).Findings.Should().BeEmpty();
        new LockWaitCheck().Evaluate(Facts(TimeSpan.FromSeconds(30)), At()).Findings.Single().Severity.Should().Be(HealthSeverity.Warning);
        new LockWaitCheck().Evaluate(Facts(TimeSpan.FromMinutes(5)), At()).Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void C06_says_what_is_waited_for_in_Martens_terms_and_what_holds_it_up()
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(
            new LockWaitFacts(
                Now,
                SessionVisibility.Everything,
                2,
                [Waiter(TimeSpan.FromSeconds(45), StreamRow), Waiter(TimeSpan.FromSeconds(40), StreamRow, pid: 4343)],
                [Blocker()]),
            At()).Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Waiting for", "an mt_streams row (sample_events) in ShareLock (2 sessions)"));
        finding.Evidence.Should().Contain(new HealthEvidence("Held up by", "1 session idle in transaction, the oldest transaction open 4 min"));
        finding.WhyItMatters.Should().Contain("mt_streams row").And.Contain("never cancels");
        finding.Remedy.Should().BeNull("there is no StoreOptions line for a transaction somebody left open");
        AssertGated(finding);
    }

    [Fact]
    public void C06_on_the_apply_changes_lock_names_it_and_offers_the_deploy_time_apply()
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(
            new LockWaitFacts(Now, SessionVisibility.Everything, 1, [Waiter(TimeSpan.FromMinutes(1), ApplyLock)], [Blocker(state: "active")]),
            At()).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Waiting for").Value.Should().Contain("ApplyChangesLockId 4004");
        finding.Remedy!.Code.Should().Contain("AutoCreate.None");
    }

    [Fact]
    public void C06_finds_the_head_of_a_chain_rather_than_a_link()
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(
            new LockWaitFacts(
                Now,
                SessionVisibility.Everything,
                2,
                [Waiter(TimeSpan.FromMinutes(1), StreamRow, pid: 1, blockedBy: [2]), Waiter(TimeSpan.FromMinutes(1), StreamRow, pid: 2, blockedBy: [3])],
                [Blocker(pid: 2, state: "active", waiting: true), Blocker(pid: 3)]),
            At()).Findings.Single();

        finding.Evidence.Single(static x => x.Label == "Blocker 1 (oldest transaction)").Value.Should().StartWith("pid 3,");
        finding.Evidence.Should().NotContain(static x => x.Label == "Blocker 2", "a blocker that waits itself is a link, not a head");
    }

    [Fact]
    public void C06_says_a_wait_measured_without_waitstart_is_an_upper_bound()
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(
            new LockWaitFacts(Now, SessionVisibility.Everything, 1, [Waiter(TimeSpan.FromMinutes(1), StreamRow, exact: false)], [Blocker()]),
            At()).Findings.Single();

        finding.Caveat.Should().Contain("before 14");
        finding.Evidence.Single(static x => x.Label == "Longest wait").Value.Should().Contain("at most");
    }

    // ----------------------------------------------------------------------------------------------
    // C07
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C07_is_quiet_where_none_can_exist_and_for_a_young_one()
    {
        new PreparedTransactionCheck().Evaluate(new PreparedTransactionFacts(Now, 0, []), At()).Note.Should().Contain("max_prepared_transactions is 0");

        new PreparedTransactionCheck().Evaluate(
            new PreparedTransactionFacts(Now, 10, [new PreparedRow("gid-orphan", "tx_owner", Now.AddMinutes(-4), TimeSpan.FromMinutes(4), "901")]),
            At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void C07_is_critical_at_five_minutes_with_its_gid_and_owner_behind_the_gate()
    {
        PreparedTransactionFacts facts = new(
            Now, 10, [new PreparedRow("gid-orphan", "tx_owner", Now.AddSeconds(-2), TimeSpan.FromSeconds(2), "901")]);

        new PreparedTransactionCheck().Evaluate(facts, At()).Findings.Should().BeEmpty();

        HealthFinding finding = new PreparedTransactionCheck().Evaluate(facts, At(Now.AddMinutes(5))).Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Evidence.Single(static x => x.Label == "Prepared transaction 1 (oldest)").Gate.Should().Be(HealthGate.Activity);
        finding.Evidence.Single(static x => x.Label == "Prepared transaction 1 (oldest)").Value.Should().Contain("gid-orphan").And.Contain("tx_owner");
        AssertGated(finding with { Evidence = [.. finding.Evidence.Where(static x => !x.Label.StartsWith("Oldest", StringComparison.Ordinal))] });
    }
}
