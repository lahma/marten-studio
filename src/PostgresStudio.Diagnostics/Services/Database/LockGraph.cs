using PostgresStudio.Internal.Sql;

using Npgsql;

namespace PostgresStudio.Services.Database;

/// <summary>What a waiting session waits for, as the lock table read had it.</summary>
/// <param name="LockType"><c>pg_locks.locktype</c>: <c>relation</c>, <c>tuple</c>, <c>transactionid</c>, <c>advisory</c>…</param>
/// <param name="Mode">The mode it asks for.</param>
/// <param name="Relation">
/// For a <c>relation</c> or <c>tuple</c> lock, the table the locked relation belongs to and which part of it - never the
/// index's, partition's or TOAST table's own name.
/// </param>
/// <param name="TupleRelation">
/// The owner of the row the session holds the tuple lock on, which is what a wait on another transaction is for.
/// </param>
/// <param name="AdvisoryKey">The key, for a one-key advisory lock.</param>
/// <param name="ExactStart">Whether the wait is measured from <c>pg_locks.waitstart</c> (PostgreSQL 14+).</param>
/// <param name="Waited">How long it had waited when read.</param>
internal sealed record LockWanted(
    string LockType,
    string Mode,
    LockedRelation? Relation,
    LockedRelation? TupleRelation,
    long? AdvisoryKey,
    bool ExactStart,
    TimeSpan Waited);

/// <summary>
/// One session of the blocking graph (X3): a client session of this database that waits for a lock, or a session that
/// blocks one - as <see cref="HealthConnectionQueries.LockTable" /> read it.
/// </summary>
/// <remarks>
/// <b>Database-wide facts and details, side by side.</b> Its state, ages, wait class and lock modes are numbers about the
/// database, which C06 already shows at its database-wide gate; its process id, role, application, client and connection
/// time are details, behind <c>ViewDatabaseActivity</c> - which is why Sessions › Blocking draws the tree only through that
/// gate, and why C12, which names the head, is an activity check (D29).
/// </remarks>
internal sealed record LockGraphNode
{
    /// <summary>Its backend's process id. A detail.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Whether it is a session of this database; a blocker can be another database's, on a shared catalog.</summary>
    public bool SameDatabase { get; init; } = true;

    /// <summary>Whether <c>pg_stat_activity</c> had a row for it when the read ran; one that ended in between has none.</summary>
    public bool Known { get; init; } = true;

    /// <summary>Its state, or <see langword="null" /> when the role may not read it or it is another database's.</summary>
    public string? State { get; init; }

    /// <summary>Its role. A detail.</summary>
    public string? User { get; init; }

    /// <summary>Its <c>application_name</c>. A detail.</summary>
    public string? Application { get; init; }

    /// <summary>Its client's address, or <see langword="null" /> for a Unix socket. A detail.</summary>
    public string? ClientAddress { get; init; }

    /// <summary>When its backend started. A detail.</summary>
    public DateTimeOffset? BackendStart { get; init; }

    /// <summary>The class of what it waits for: <c>Lock</c>, <c>Client</c>, <c>IO</c>…</summary>
    public string? WaitEventType { get; init; }

    /// <summary>What it waits for, exactly (<c>relation</c>, <c>transactionid</c>, <c>ClientRead</c>…).</summary>
    public string? WaitEvent { get; init; }

    /// <summary>How long its transaction had been open when read.</summary>
    public TimeSpan? TransactionAge { get; init; }

    /// <summary>How long it had been in its state when read.</summary>
    public TimeSpan? StateAge { get; init; }

    /// <summary>Whether it has written in its transaction (it has a transaction id).</summary>
    public bool HasTransactionId { get; init; }

    /// <summary>Whether it is running a <c>VACUUM</c>: a row in <c>pg_stat_progress_vacuum</c>, which every role may read.</summary>
    public bool Vacuum { get; init; }

    /// <summary>Whether it is an autovacuum worker - known only to a role with <c>pg_read_all_stats</c>.</summary>
    public bool Autovacuum { get; init; }

    /// <summary>Whether it is autovacuum running to prevent wraparound - known only to a role with <c>pg_read_all_stats</c>.</summary>
    public bool PreventsWraparound { get; init; }

    /// <summary>What it waits for, when the read listed it among the waiting sessions.</summary>
    public LockWanted? Wanted { get; init; }

    /// <summary>The process ids blocking it (<c>pg_blocking_pids</c>), for a waiting session. A detail.</summary>
    public IReadOnlyList<int> BlockedBy { get; init; } = [];

    /// <summary>The modes it holds on what the sessions it blocks wait for - empty for one ahead of them in the queue.</summary>
    public IReadOnlyList<string> HeldModes { get; init; } = [];

    /// <summary>Whether it waits for a lock itself: a link in a chain rather than its head.</summary>
    public bool WaitsForALock => Wanted is not null || string.Equals(WaitEventType, "Lock", StringComparison.Ordinal);

    /// <summary>Whether it is idle inside a transaction, aborted or not.</summary>
    public bool IsIdleInTransaction => State is "idle in transaction" or "idle in transaction (aborted)";

    /// <summary>What it is when it is a <c>VACUUM</c>: "an autovacuum to prevent wraparound", "an autovacuum", "a VACUUM".</summary>
    public string? VacuumLabel => PreventsWraparound
        ? "an autovacuum to prevent wraparound"
        : Autovacuum ? "an autovacuum" : Vacuum ? "a VACUUM" : null;
}

/// <summary>
/// One read of the lock table (<see cref="HealthConnectionQueries.LockTable" />): what a regular sweep's checks take from
/// it, and the blocking graph Sessions › Blocking and C12 are drawn from.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
internal sealed record LockTableRead(DateTimeOffset ReadAt, SessionVisibility Visibility, int ServerVersion)
{
    /// <summary>A read of an empty lock table, for a test.</summary>
    public static LockTableRead Empty(DateTimeOffset readAt) => new(readAt, SessionVisibility.Everything, 170000);

    /// <summary>The relations of this database somebody holds or awaits <c>ACCESS EXCLUSIVE</c> on.</summary>
    public IReadOnlyList<uint> ExclusiveRelations { get; init; } = [];

    /// <summary>
    /// The relations of this database somebody but a running <c>VACUUM</c> or <c>ANALYZE</c> - or a prepared transaction -
    /// holds or awaits a lock on that conflicts with autovacuum's <c>SHARE UPDATE EXCLUSIVE</c>.
    /// </summary>
    public IReadOnlyList<uint> StrongRelations { get; init; } = [];

    /// <summary>How many relation locks each session of this database holds, by process id; one that holds none is absent.</summary>
    public IReadOnlyDictionary<int, long> RelationLocks { get; init; } = new Dictionary<int, long>();

    /// <summary>Every one-key advisory lock a session of this database holds: its process id and the key.</summary>
    public IReadOnlyList<(int ProcessId, long Key)> AdvisoryLocks { get; init; } = [];

    /// <summary>How many sessions wait, beyond the ones <see cref="Nodes" /> lists when the read was capped.</summary>
    public long WaitingTotal { get; init; }

    /// <summary>Every waiting session the read listed, the longest wait first, and every session blocking one of them.</summary>
    public IReadOnlyList<LockGraphNode> Nodes { get; init; } = [];

    /// <summary>The sessions that wait, as listed.</summary>
    public IEnumerable<LockGraphNode> Waiting => Nodes.Where(static x => x.Wanted is not null);

    /// <summary>The sessions holding one of <paramref name="keys" /> - the source's daemon's leadership, say - by process id.</summary>
    public IReadOnlyList<int> HoldersOf(IReadOnlyCollection<long> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return [];
        }

        HashSet<long> wanted = [.. keys];
        return [.. AdvisoryLocks.Where(x => wanted.Contains(x.Key)).Select(static x => x.ProcessId).Distinct().Order()];
    }

    /// <summary>
    /// The read's rows folded into one: the first row (<c>kind = 0</c>) and one per session of the graph (<c>kind = 1</c>).
    /// </summary>
    /// <param name="rows">What <see cref="Map" /> made of every row.</param>
    /// <param name="readAt">When it was read.</param>
    /// <exception cref="InvalidOperationException">The read had no first row, which the statement always returns.</exception>
    internal static LockTableRead Fold(IReadOnlyList<LockTableRow> rows, DateTimeOffset readAt)
    {
        ArgumentNullException.ThrowIfNull(rows);

        LockTableRow header = rows.FirstOrDefault(static x => x.Node is null)
            ?? throw new InvalidOperationException("The lock table read returned no summary row.");

        Dictionary<int, long> relationLocks = [];
        for (int i = 0; i < Math.Min(header.RelationLockPids.Count, header.RelationLockCounts.Count); i++)
        {
            relationLocks[header.RelationLockPids[i]] = header.RelationLockCounts[i];
        }

        List<(int, long)> advisory = [];
        for (int i = 0; i < Math.Min(header.AdvisoryPids.Count, header.AdvisoryKeys.Count); i++)
        {
            advisory.Add((header.AdvisoryPids[i], header.AdvisoryKeys[i]));
        }

        return new LockTableRead(readAt, header.Visibility, header.ServerVersion)
        {
            ExclusiveRelations = header.ExclusiveRelations,
            StrongRelations = header.StrongRelations,
            RelationLocks = relationLocks,
            AdvisoryLocks = advisory,
            WaitingTotal = header.WaitingTotal,
            Nodes = [.. rows.Where(static x => x.Node is not null).Select(static x => x.Node!)],
        };
    }

    /// <summary>One row of <see cref="HealthConnectionQueries.LockTable" />.</summary>
    internal static LockTableRow Map(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.GetInt32(0) == 0)
        {
            return new LockTableRow
            {
                Visibility = new SessionVisibility(reader.GetBoolean(1), reader.GetInt64(2)),
                ServerVersion = reader.GetInt32(3),
                ExclusiveRelations = Array<uint>(reader, 4),
                StrongRelations = Array<uint>(reader, 5),
                RelationLockPids = Array<int>(reader, 6),
                RelationLockCounts = Array<long>(reader, 7),
                AdvisoryPids = Array<int>(reader, 8),
                AdvisoryKeys = Array<long>(reader, 9),
                WaitingTotal = reader.GetInt64(10),
            };
        }

        bool waiter = !reader.IsDBNull(27) && reader.GetBoolean(27);
        string? backendType = SessionReads.Text(reader, 25);

        return new LockTableRow
        {
            Node = new LockGraphNode
            {
                ProcessId = reader.GetInt32(11),
                SameDatabase = !reader.IsDBNull(12) && reader.GetBoolean(12),
                Known = !reader.IsDBNull(13) && reader.GetBoolean(13),
                State = SessionReads.Text(reader, 14),
                User = SessionReads.Text(reader, 15),
                Application = SessionReads.Text(reader, 16),
                ClientAddress = SessionReads.Text(reader, 17),
                BackendStart = SessionReads.Value<DateTimeOffset>(reader, 18),
                WaitEventType = SessionReads.Text(reader, 19),
                WaitEvent = SessionReads.Text(reader, 20),
                TransactionAge = SessionReads.Seconds(reader, 21),
                StateAge = SessionReads.Seconds(reader, 22),
                HasTransactionId = !reader.IsDBNull(23) && reader.GetBoolean(23),
                Vacuum = !reader.IsDBNull(24) && reader.GetBoolean(24),
                Autovacuum = string.Equals(backendType, "autovacuum worker", StringComparison.Ordinal),
                PreventsWraparound = !reader.IsDBNull(26) && reader.GetBoolean(26),
                Wanted = waiter
                    ? new LockWanted(
                        reader.GetString(28),
                        reader.GetString(29),
                        SessionReads.Locked(reader, 30),
                        SessionReads.Locked(reader, 34),
                        SessionReads.Value<long>(reader, 38),
                        !reader.IsDBNull(39) && reader.GetBoolean(39),
                        SessionReads.Seconds(reader, 40) ?? TimeSpan.Zero)
                    : null,
                BlockedBy = SessionReads.Pids(reader, 41),
                HeldModes = reader.IsDBNull(42) ? [] : reader.GetFieldValue<string[]>(42),
            },
        };
    }

    private static T[] Array<T>(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<T[]>(ordinal);
}

/// <summary>One row of <see cref="HealthConnectionQueries.LockTable" />: the first row's facts, or one session of the graph.</summary>
internal sealed record LockTableRow
{
    /// <summary>The session, for a row of the graph; <see langword="null" /> for the first row.</summary>
    public LockGraphNode? Node { get; init; }

    /// <summary>How much of <c>pg_stat_activity</c> the role could see.</summary>
    public SessionVisibility Visibility { get; init; } = SessionVisibility.Everything;

    /// <summary><c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; }

    /// <summary>See <see cref="LockTableRead.ExclusiveRelations" />.</summary>
    public IReadOnlyList<uint> ExclusiveRelations { get; init; } = [];

    /// <summary>See <see cref="LockTableRead.StrongRelations" />.</summary>
    public IReadOnlyList<uint> StrongRelations { get; init; } = [];

    /// <summary>The process ids of <see cref="LockTableRead.RelationLocks" />, in order.</summary>
    public IReadOnlyList<int> RelationLockPids { get; init; } = [];

    /// <summary>Their counts, in the same order.</summary>
    public IReadOnlyList<long> RelationLockCounts { get; init; } = [];

    /// <summary>The process ids of <see cref="LockTableRead.AdvisoryLocks" />, in order.</summary>
    public IReadOnlyList<int> AdvisoryPids { get; init; } = [];

    /// <summary>Their keys, in the same order.</summary>
    public IReadOnlyList<long> AdvisoryKeys { get; init; } = [];

    /// <summary>See <see cref="LockTableRead.WaitingTotal" />.</summary>
    public long WaitingTotal { get; init; }
}

/// <summary>Why a branch of a blocking tree is not drawn out under one of its parents.</summary>
internal enum BlockingRepeat
{
    /// <summary>It is drawn out here.</summary>
    None,

    /// <summary>
    /// It waits on more than one session, and its own waiters are drawn under the first of them - a waiter blocked by two
    /// appears under both, its subtree once.
    /// </summary>
    ShownElsewhere,

    /// <summary>
    /// It is already on the path from the root: the sessions wait on each other, a deadlock Postgres' detector ends after
    /// <c>deadlock_timeout</c>.
    /// </summary>
    Cycle,

    /// <summary>The tree is deeper than <see cref="BlockingForest.MaxDepth" />; the rest is not drawn.</summary>
    TooDeep,
}

/// <summary>One session in a blocking tree, with the sessions waiting on it.</summary>
/// <typeparam name="T">What a session is.</typeparam>
/// <param name="Node">The session.</param>
/// <param name="Waiters">Who waits on it, the longest wait first - empty when <paramref name="Repeat" /> says why.</param>
/// <param name="Repeat">Why its waiters are not drawn here, when they are not.</param>
/// <param name="Depth">How far from the root: the root is 0.</param>
internal sealed record BlockingBranch<T>(T Node, IReadOnlyList<BlockingBranch<T>> Waiters, BlockingRepeat Repeat, int Depth);

/// <summary>One chain: the session at its head and every session waiting on it, however far down.</summary>
/// <typeparam name="T">What a session is.</typeparam>
/// <param name="Root">The head and its tree.</param>
/// <param name="HeadWaits">
/// Whether the root waits for a lock itself - its sessions wait on each other, or on a session the read did not list - so
/// it is a place to start drawing rather than a session that holds the others up.
/// </param>
/// <param name="Blocked">The process ids of every session in the chain but the root, each once.</param>
internal sealed record BlockingChain<T>(BlockingBranch<T> Root, bool HeadWaits, IReadOnlySet<int> Blocked);

/// <summary>
/// The blocking graph as trees (X3): each head blocker with its waiters, nested - pure, so every shape is unit-tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>A head</b> is a session that blocks somebody and waits for no lock itself. A waiter blocked by two sessions is drawn
/// under both, and its own waiters only the first time (<see cref="BlockingRepeat.ShownElsewhere" />); two heads that block
/// one waiter are two chains, each counting it.
/// </para>
/// <para>
/// <b>Cycles are impossible for long, and guarded all the same.</b> A deadlock is sessions waiting on each other, and
/// Postgres' detector ends one of them after <c>deadlock_timeout</c>; a read can still catch one forming, and
/// <c>pg_blocking_pids</c> also names a session ahead in the queue, which can point back. A session already on the path from
/// its root is drawn once more, marked <see cref="BlockingRepeat.Cycle" />, and not followed. Waiting sessions no head
/// reaches are drawn as chains of their own, from the lowest process id, with <see cref="BlockingChain{T}.HeadWaits" /> set.
/// </para>
/// </remarks>
internal static class BlockingForest
{
    /// <summary>The deepest a tree is drawn.</summary>
    public const int MaxDepth = 64;

    /// <summary>The chains among <paramref name="nodes" />, the most sessions held up first.</summary>
    /// <typeparam name="T">What a session is.</typeparam>
    /// <param name="nodes">Every session of the graph.</param>
    /// <param name="processId">A session's process id.</param>
    /// <param name="blockedBy">The process ids blocking it; empty for one that waits on nobody.</param>
    /// <param name="waited">How long it has waited, for one that waits; orders waiters and chains.</param>
    public static IReadOnlyList<BlockingChain<T>> Build<T>(
        IEnumerable<T> nodes,
        Func<T, int> processId,
        Func<T, IReadOnlyList<int>> blockedBy,
        Func<T, TimeSpan?> waited)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(processId);
        ArgumentNullException.ThrowIfNull(blockedBy);
        ArgumentNullException.ThrowIfNull(waited);

        Dictionary<int, T> byPid = [];
        foreach (T node in nodes)
        {
            byPid.TryAdd(processId(node), node);
        }

        // Who waits on whom: each waiter under every listed session it waits on, the longest wait first.
        Dictionary<int, List<T>> waitersOf = [];
        foreach (T node in byPid.Values)
        {
            int pid = processId(node);

            foreach (int blocker in blockedBy(node).Distinct())
            {
                if (blocker == pid || !byPid.ContainsKey(blocker))
                {
                    continue;
                }

                if (!waitersOf.TryGetValue(blocker, out List<T>? list))
                {
                    waitersOf[blocker] = list = [];
                }

                list.Add(node);
            }
        }

        foreach (List<T> list in waitersOf.Values)
        {
            list.Sort((x, y) => Order(x, y, processId, waited));
        }

        // A head waits on no listed session; a session that waits only on sessions the read did not list is not a head, but
        // the start of a chain of its own, said so.
        bool Waits(T node) => blockedBy(node).Any(x => x != processId(node));

        HashSet<int> expanded = [];
        List<BlockingChain<T>> chains = [];

        IEnumerable<T> heads = byPid.Values
            .Where(x => waitersOf.ContainsKey(processId(x)) && !Waits(x))
            .OrderBy(processId);

        foreach (T head in heads)
        {
            chains.Add(Chain(head, headWaits: false));
        }

        // Whatever no head reached: sessions waiting on each other, or on sessions the read did not list. Each pass draws at least
        // the session it starts from, so the loop ends.
        while (true)
        {
            List<T> rest = [.. byPid.Values.Where(x => Waits(x) && !expanded.Contains(processId(x))).OrderBy(processId)];

            if (rest.Count == 0)
            {
                break;
            }

            chains.Add(Chain(rest[0], headWaits: true));
        }

        return
        [
            .. chains
                .OrderBy(static x => x.HeadWaits)
                .ThenByDescending(static x => x.Blocked.Count)
                .ThenByDescending(x => Longest(x.Root, waited))
                .ThenBy(x => processId(x.Root.Node)),
        ];

        BlockingChain<T> Chain(T root, bool headWaits)
        {
            HashSet<int> blocked = [];
            BlockingBranch<T> tree = Branch(root, [], 0, blocked);
            blocked.Remove(processId(root));
            return new BlockingChain<T>(tree, headWaits, blocked);
        }

        BlockingBranch<T> Branch(T node, HashSet<int> path, int depth, HashSet<int> blocked)
        {
            int pid = processId(node);
            blocked.Add(pid);

            if (path.Contains(pid))
            {
                return new BlockingBranch<T>(node, [], BlockingRepeat.Cycle, depth);
            }

            if (!waitersOf.TryGetValue(pid, out List<T>? waiters) || waiters.Count == 0)
            {
                expanded.Add(pid);
                return new BlockingBranch<T>(node, [], BlockingRepeat.None, depth);
            }

            if (!expanded.Add(pid))
            {
                // Its waiters are drawn under another of the sessions it waits on; they still count in this chain.
                CountBelow(pid, blocked, [pid]);
                return new BlockingBranch<T>(node, [], BlockingRepeat.ShownElsewhere, depth);
            }

            if (depth >= MaxDepth)
            {
                CountBelow(pid, blocked, [pid]);
                return new BlockingBranch<T>(node, [], BlockingRepeat.TooDeep, depth);
            }

            path.Add(pid);
            List<BlockingBranch<T>> children = [.. waiters.Select(x => Branch(x, path, depth + 1, blocked))];
            path.Remove(pid);

            return new BlockingBranch<T>(node, children, BlockingRepeat.None, depth);
        }

        // Every session under one already drawn elsewhere, so a chain counts all it holds up whichever branch drew them.
        void CountBelow(int pid, HashSet<int> blocked, HashSet<int> seen)
        {
            if (!waitersOf.TryGetValue(pid, out List<T>? waiters))
            {
                return;
            }

            foreach (T waiter in waiters)
            {
                int child = processId(waiter);
                blocked.Add(child);

                if (seen.Add(child))
                {
                    CountBelow(child, blocked, seen);
                }
            }
        }
    }

    /// <summary>The longest wait anywhere in a tree.</summary>
    public static TimeSpan Longest<T>(BlockingBranch<T> branch, Func<T, TimeSpan?> waited)
    {
        ArgumentNullException.ThrowIfNull(branch);
        ArgumentNullException.ThrowIfNull(waited);

        TimeSpan longest = waited(branch.Node) ?? TimeSpan.Zero;

        foreach (BlockingBranch<T> child in branch.Waiters)
        {
            TimeSpan below = Longest(child, waited);
            if (below > longest)
            {
                longest = below;
            }
        }

        return longest;
    }

    /// <summary>Every branch of a tree, the root first, depth first.</summary>
    public static IEnumerable<BlockingBranch<T>> Flatten<T>(BlockingBranch<T> branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        yield return branch;

        foreach (BlockingBranch<T> child in branch.Waiters)
        {
            foreach (BlockingBranch<T> below in Flatten(child))
            {
                yield return below;
            }
        }
    }

    private static int Order<T>(T x, T y, Func<T, int> processId, Func<T, TimeSpan?> waited)
    {
        int byWait = (waited(y) ?? TimeSpan.Zero).CompareTo(waited(x) ?? TimeSpan.Zero);
        return byWait != 0 ? byWait : processId(x).CompareTo(processId(y));
    }
}
