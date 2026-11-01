using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One session of the blocking graph, as C12 read it.</summary>
/// <param name="Node">The session, as the lock table read had it.</param>
/// <param name="Target">What it waits for, labelled the way C06 labels it; <see langword="null" /> for one that waits for nothing.</param>
internal sealed record ChainSession(LockGraphNode Node, LockTarget? Target);

/// <summary>What C12 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="Sessions">Every session of the blocking graph: the waiting ones and the ones blocking them.</param>
/// <param name="Platform">Who runs the server, for where the server-setting remedy changes.</param>
internal sealed record BlockingChainFacts(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    IReadOnlyList<ChainSession> Sessions,
    PostgresPlatform Platform);

/// <summary>
/// C12: the head of a blocking chain - a session idle in its transaction with somebody waiting behind it
/// (<c>idle-head</c>), or a chain whose oldest wait has gone on for minutes whatever its head is doing (<c>long-chain</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is its own check beside C04 and C06.</b> C04 says a session sits idle in a transaction, C06 that sessions wait
/// for locks; neither says which session the others are waiting on, and that is the one somebody has to deal with. C12
/// draws the blocking graph as chains (<see cref="BlockingForest" />) and judges each chain's head: idle in its transaction,
/// it will hold everybody up until the application that opened it ends it, so it is raised after ten seconds of anybody's
/// wait; doing anything else, it is raised once the chain's oldest wait reaches two minutes. Both are Critical at five, C06's
/// own line. A chain whose sessions wait on each other - a deadlock forming, which Postgres' detector ends - or on sessions
/// the read did not list has no head to name, and is not judged.
/// </para>
/// <para>
/// <b>An activity check</b> (D29): every finding names its heads - process id, role, application, client - and hands over
/// the statement that would end each, so the whole check is behind <c>ViewDatabaseActivity</c>. The statements are text for
/// whoever may run them (<see cref="SessionHandOver" />): the studio never cancels or ends a session (K2). The remedy of an
/// <c>idle-head</c> finding is the server's backstop, <c>idle_in_transaction_session_timeout</c>; of a <c>long-chain</c>
/// finding, the head's statements as a repair to run by hand.
/// </para>
/// <para>
/// <b>Read once with the others.</b> It reads nothing of its own: the blocking graph is the sweep's one read of the lock table
/// (<see cref="LockTableReads" />, X3), and the platform the sweep's server read.
/// </para>
/// </remarks>
[HealthCheck("C12", HealthArea.Connections, HealthCostTier.Catalog, "Head of a blocking chain",
    Gate = HealthGate.Activity, Variants = [IdleHead, LongChain])]
internal sealed class BlockingChainCheck : HealthCheck<BlockingChainFacts>
{
    /// <summary>The variant for a chain whose head sits idle in its transaction.</summary>
    public const string IdleHead = "idle-head";

    /// <summary>The variant for a chain whose oldest wait has gone on for minutes, whatever its head does.</summary>
    public const string LongChain = "long-chain";

    /// <summary>The setting an <c>idle-head</c> finding's remedy names.</summary>
    internal const string IdleTimeoutSetting = "idle_in_transaction_session_timeout";

    /// <summary>The value it suggests: long past any request's idle moment, short against a transaction somebody forgot.</summary>
    internal const string IdleTimeoutValue = "'5min'";

    /// <inheritdoc />
    public override async Task<BlockingChainFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        SessionLockLabels labels = context.Services.GetRequiredService<ISessionLockLabeler>().For(context.Source, LockWaitCheck.Classifier(context));
        LockTableRead locks = await LockTableReads.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return FactsOf(locks, labels, server.Platform, context.Now);
    }

    /// <summary>C12's facts from a read of the lock table: every session of the graph, each waiter's target labelled.</summary>
    internal static BlockingChainFacts FactsOf(LockTableRead locks, SessionLockLabels labels, PostgresPlatform platform, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(platform);

        return new BlockingChainFacts(
            readAt,
            locks.Visibility,
            [
                .. locks.Nodes.Select(x => new ChainSession(
                    x,
                    x.Wanted is { } wanted ? labels.Describe(wanted.LockType, wanted.Relation, wanted.AdvisoryKey, wanted.TupleRelation) : null)),
            ],
            platform);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(BlockingChainFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        Dictionary<int, ChainSession> byPid = [];
        foreach (ChainSession session in facts.Sessions)
        {
            byPid.TryAdd(session.Node.ProcessId, session);
        }

        List<JudgedChain> judged = [];

        foreach (BlockingChain<ChainSession> chain in BlockingForest.Build(
                     facts.Sessions, static x => x.Node.ProcessId, static x => x.Node.BlockedBy, static x => x.Node.Wanted?.Waited))
        {
            LockGraphNode head = chain.Root.Node.Node;

            // A chain with no session that waits on nobody has no head to name: a deadlock forming, or waits on sessions the
            // read did not list. A head that waits for a lock the read did not list is not one either.
            if (chain.HeadWaits || head.WaitsForALock || !head.Known || chain.Blocked.Count == 0)
            {
                continue;
            }

            List<ChainSession> behind = [.. chain.Blocked.Where(byPid.ContainsKey).Select(x => byPid[x])];
            List<ChainSession> waiting = [.. behind.Where(static x => x.Node.Wanted is not null)];

            if (waiting.Count == 0)
            {
                continue;
            }

            ChainSession oldest = waiting.OrderByDescending(static x => x.Node.Wanted!.Waited).ThenBy(static x => x.Node.ProcessId).First();
            TimeSpan age = SnapshotAge.At(oldest.Node.Wanted!.Waited, facts.ReadAt, evaluation);
            bool idle = head.SameDatabase && head.IsIdleInTransaction;

            if (idle ? age >= ConnectionThresholds.BlockingIdleHeadWarning : age >= ConnectionThresholds.BlockingChainWarning)
            {
                judged.Add(new JudgedChain(chain.Root.Node, behind, waiting, oldest, age, idle));
            }
        }

        List<HealthFinding> findings = [];

        if (judged.Where(static x => x.IdleHead).ToList() is { Count: > 0 } idleHeads)
        {
            findings.Add(Finding(idleHeads, IdleHead, facts, evaluation));
        }

        if (judged.Where(static x => !x.IdleHead).ToList() is { Count: > 0 } longChains)
        {
            findings.Add(Finding(longChains, LongChain, facts, evaluation));
        }

        return findings.Count == 0
            ? HealthVerdict.Quiet(
                "No blocking chain's head has sat idle in its transaction while a session waited "
                + SessionText.Duration(ConnectionThresholds.BlockingIdleHeadWarning) + " or more behind it, and no chain has held a session up for "
                + SessionText.Duration(ConnectionThresholds.BlockingChainWarning) + " or more.")
            : new HealthVerdict(findings);
    }

    /// <summary>One finding for every chain of one variant, the longest wait first.</summary>
    private static HealthFinding Finding(List<JudgedChain> chains, string variant, BlockingChainFacts facts, HealthEvaluation evaluation)
    {
        chains.Sort(static (x, y) => y.Age.CompareTo(x.Age) is var byAge and not 0 ? byAge : x.Head.Node.ProcessId.CompareTo(y.Head.Node.ProcessId));

        bool idleHead = variant == IdleHead;
        TimeSpan longest = chains[0].Age;
        int held = chains.SelectMany(static x => x.Behind).Select(static x => x.Node.ProcessId).Distinct().Count();
        bool exact = chains.SelectMany(static x => x.Waiting).All(static x => x.Node.Wanted!.ExactStart);

        string waitingFor = string.Join("; ", chains
            .SelectMany(static x => x.Waiting)
            .DistinctBy(static x => x.Node.ProcessId)
            .GroupBy(static x => (Label: x.Target?.Label ?? "a lock", x.Node.Wanted!.Mode))
            .OrderByDescending(static x => x.Count())
            .ThenBy(static x => x.Key.Label, StringComparer.Ordinal)
            .Select(static x => x.Key.Label + " in " + x.Key.Mode + (x.Count() > 1 ? " (" + SessionText.Number(x.Count()) + " sessions)" : string.Empty)));

        List<HealthEvidence> evidence =
        [
            new("Chains", SessionText.Number(chains.Count)),
            new("Sessions held up", SessionText.Number(held)),
            new("Longest wait", SessionText.Duration(longest) + (exact ? string.Empty : " (at most: measured from the session's last state change)")),
            new("Waiting for", waitingFor),
        ];

        for (int i = 0; i < Math.Min(chains.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            JudgedChain chain = chains[i];
            LockGraphNode head = chain.Head.Node;
            string number = (i + 1).ToString(CultureInfo.InvariantCulture);

            evidence.Add(SessionEvidence.Session(
                SessionEvidence.Label("Head", i, "longest wait"),
                head.ProcessId,
                head.User,
                head.Application,
                head.ClientAddress,
                head.BackendStart,
                HeadWords(chain, facts, evaluation)));

            evidence.Add(new HealthEvidence(
                "Hand over " + number,
                idleHead
                    ? SessionHandOver.Terminate(head.ProcessId) + " - ends the session and rolls its transaction back; pg_cancel_backend does nothing to a session idle in its transaction"
                    : SessionHandOver.Cancel(head.ProcessId) + " - cancels its statement; if its transaction stays open, holding its locks, "
                      + SessionHandOver.Terminate(head.ProcessId) + " ends the session",
                HealthGate.Activity));
        }

        LockGraphNode first = chains[0].Head.Node;
        string count = SessionText.Count(held, "session");

        return new HealthFinding
        {
            Code = "C12",
            Variant = variant,
            Severity = longest >= ConnectionThresholds.BlockingChainCritical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = idleHead
                ? chains.Count == 1
                    ? "A session idle in its transaction has held up " + count + " for " + SessionText.Duration(longest)
                    : SessionText.Number(chains.Count) + " sessions idle in their transactions are holding up " + count + ", the longest wait " + SessionText.Duration(longest)
                : chains.Count == 1
                    ? "A blocking chain has held up " + count + " for " + SessionText.Duration(longest)
                    : SessionText.Number(chains.Count) + " blocking chains are holding up " + count + ", the longest wait " + SessionText.Duration(longest),
            WhyItMatters = idleHead
                ? "The session at the head of the chain has finished its last statement and sits inside a transaction it has not ended, "
                  + "holding every lock it took. The sessions behind it wait until the application that opened it commits or rolls back - "
                  + "which, for a transaction a code path forgot, is never - and every request that needs the same rows or table queues "
                  + "behind them. Ending it rolls back whatever it wrote. The studio never ends a session: the statement under each "
                  + "\"Hand over\" is for whoever may."
                : "The session at the head of the chain holds a lock the others have waited for " + SessionText.Duration(longest)
                  + ", and everything that needs the same lock queues behind them. "
                  + (VacuumHead(chains) is { } vacuum ? LockWaitCheck.VacuumHeadAdvice(vacuum, evaluation.Text) : string.Empty)
                  + "Cancelling the head's statement, or ending its session, lets the queue move - and loses what it was doing. "
                  + "The studio never does either: the statements under each \"Hand over\" are for whoever may.",
            Evidence = evidence,
            Caveat = SessionEvidence.Caveat(
                facts.Visibility,
                facts.ReadAt,
                (exact
                    ? "Waits are measured by pg_locks.waitstart."
                    : "On PostgreSQL before 14 there is no pg_locks.waitstart, so a wait is measured from the session's last state change - an upper bound.")
                + " A waiting session is counted once in each chain it waits in. The whole tree, with each session's lock and relation, "
                + "is on Database › Sessions › Blocking."),
            Link = BlockingLink,
            Remedy = idleHead
                ? ServerSettingRemedy.Build(
                    facts.Platform,
                    IdleTimeoutSetting,
                    IdleTimeoutValue,
                    evaluation.Text,
                    context: null,
                    ":",
                    where => "Ends every session that sits idle inside a transaction this long, rolling the transaction back and "
                        + "releasing its locks - the server's backstop for a transaction an application left open; the fix is still "
                        + "in the application, which should end what it begins. Set it in " + where
                        + ", or for the application's role or database alone." + evaluation.Text.IdleTimeoutLeaderClause)
                : HealthRemedy.ForRepair(
                    "-- The head of the longest chain, pid " + first.ProcessId.ToString(CultureInfo.InvariantCulture)
                    + (first.State is { } state ? ", " + state + (first.StateAge is { } inState ? " for " + SessionText.Duration(SnapshotAge.At(inState, facts.ReadAt, evaluation)) : string.Empty) : string.Empty)
                    + ". Cancel its statement:\n"
                    + SessionHandOver.Cancel(first.ProcessId) + "\n"
                    + "-- If its transaction stays open, holding its locks, end its session instead:\n"
                    + SessionHandOver.Terminate(first.ProcessId),
                    "pg_cancel_backend cancels the statement the head is running; inside an explicit transaction that leaves the "
                    + "transaction aborted, still holding its locks until the application rolls it back, and pg_terminate_backend ends "
                    + "the session, rolling its transaction back. Either needs a superuser, a member of the head's own role, or a member "
                    + "of pg_signal_backend, which cannot signal a superuser's session."),
        };
    }

    /// <summary>What a head is doing and what is behind it, after its identity: "idle in transaction for 4 min, …".</summary>
    private static string HeadWords(JudgedChain chain, BlockingChainFacts facts, HealthEvaluation evaluation)
    {
        LockGraphNode head = chain.Head.Node;
        List<string> parts = [];

        if (head.VacuumLabel is { } vacuum)
        {
            parts.Add(vacuum);
        }

        parts.Add((head.SameDatabase ? head.State ?? "state not readable" : "in another database")
            + (head.SameDatabase && head.State is not null && head.StateAge is { } inState
                ? " for " + SessionText.Duration(SnapshotAge.At(inState, facts.ReadAt, evaluation))
                : string.Empty));

        if (head.TransactionAge is { } open)
        {
            parts.Add("transaction open " + SessionText.Duration(SnapshotAge.At(open, facts.ReadAt, evaluation)));
        }

        if (head.HasTransactionId)
        {
            parts.Add("has written");
        }

        if (head.HeldModes.Count > 0)
        {
            parts.Add("holds " + string.Join(" and ", head.HeldModes));
        }

        parts.Add("holds up " + SessionText.Count(chain.Behind.Count, "session") + ", the longest waiting "
            + SessionText.Duration(chain.Age) + " for " + (chain.Oldest.Target?.Label ?? "a lock") + " in " + chain.Oldest.Node.Wanted!.Mode);

        return string.Join(", ", parts);
    }

    /// <summary>The first head that is a <c>VACUUM</c>, as C06's advice takes it.</summary>
    private static LockBlocker? VacuumHead(IEnumerable<JudgedChain> chains) =>
        chains.Select(static x => x.Head.Node).Where(static x => x.VacuumLabel is not null).Select(static x => new LockBlocker(
            x.ProcessId, x.SameDatabase, x.State, x.User, x.Application, x.ClientAddress, x.TransactionAge, x.HasTransactionId, x.WaitsForALock)
        {
            Vacuum = x.Vacuum,
            Autovacuum = x.Autovacuum,
            PreventsWraparound = x.PreventsWraparound,
        }).FirstOrDefault();

    /// <summary>The finding's link: Database › Sessions, on its Blocking tab.</summary>
    internal static HealthLink BlockingLink { get; } = new(
        "Open Database › Sessions › Blocking",
        SessionLinks.Route,
        [new KeyValuePair<string, string?>(SessionLinks.ViewParameter, SessionLinks.BlockingView)]);

    /// <summary>One chain past its line.</summary>
    private sealed record JudgedChain(
        ChainSession Head,
        IReadOnlyList<ChainSession> Behind,
        IReadOnlyList<ChainSession> Waiting,
        ChainSession Oldest,
        TimeSpan Age,
        bool IdleHead);
}
