using System.Globalization;

using PostgresStudio.Components;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Database;

/// <summary>Database › Sessions' address.</summary>
internal static class SessionLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "database/sessions";

    /// <summary>The query parameter that names the page's tab (D9: a link someone pastes reopens the same tab).</summary>
    public const string ViewParameter = "view";

    /// <summary>The Blocking tab's value of <see cref="ViewParameter" /> (X3).</summary>
    public const string BlockingView = "blocking";

    /// <summary>The Holders tab's value of <see cref="ViewParameter" />: who holds the whole server's connections (issue #20).</summary>
    public const string HoldersView = "holders";

    /// <summary>The page, with the scope carried (D9) under the studio's own source parameter.</summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio's descriptor: its source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="view">The tab - <see cref="BlockingView" /> or <see cref="HoldersView" /> - or <see langword="null" /> for the sessions themselves.</param>
    public static string ToSessions(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope, string? view = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);

        string route = view is { Length: > 0 }
            ? Route + "?" + ViewParameter + "=" + Uri.EscapeDataString(view)
            : Route;

        return StudioScopeQuery.Append(StudioLink.To(settings, route), scope, descriptor.SourceQueryParameter);
    }
}

/// <summary>How much of <c>pg_stat_activity</c> the studio's Postgres role can see.</summary>
/// <param name="CanReadAllStats">
/// Whether the role is a member of <c>pg_read_all_stats</c> (a superuser is): then every session's state,
/// timings, client and statement are readable.
/// </param>
/// <param name="HiddenSessions">
/// How many of this database's client sessions this role may not read into - other roles' sessions, whose
/// backend type, state, timings, client and statement Postgres withholds from it. They are counted, and they
/// cannot be judged.
/// </param>
internal sealed record SessionVisibility(bool CanReadAllStats, long HiddenSessions)
{
    /// <summary>A role that sees everything.</summary>
    public static SessionVisibility Everything { get; } = new(true, 0);

    /// <summary>Whether something exists that this role cannot see into.</summary>
    public bool IsPartial => !CanReadAllStats && HiddenSessions > 0;

    /// <summary>
    /// What a finding or the page says about it: nothing when the role sees everything, and "cannot see",
    /// never "none", when it does not (plan §8). An expected state - the host's choice of role - so a sentence
    /// and never a log line above Debug.
    /// </summary>
    public string? Caveat => CanReadAllStats
        ? null
        : HiddenSessions > 0
            ? "The studio's Postgres role is not a member of pg_read_all_stats, so it cannot see into "
              + SessionText.Count(HiddenSessions, "session") + " of other roles in this database: they are counted, "
              + "and their state, timings and statements are not read - this says what it can see, not that there is nothing else."
            : "The studio's Postgres role is not a member of pg_read_all_stats; at this reading, every other session in this "
              + "database was one it may read.";
}

/// <summary>Durations and counts, worded the same way by every check and by the page.</summary>
internal static class SessionText
{
    /// <summary>"45 s", "12 min", "3 h 5 min", "2 d 4 h".</summary>
    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value < TimeSpan.FromMinutes(1))
        {
            return ((int) value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
        }

        if (value < TimeSpan.FromHours(1))
        {
            return ((int) value.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
        }

        if (value < TimeSpan.FromDays(1))
        {
            return ((int) value.TotalHours).ToString(CultureInfo.InvariantCulture) + " h "
                + value.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
        }

        return ((int) value.TotalDays).ToString(CultureInfo.InvariantCulture) + " d "
            + value.Hours.ToString(CultureInfo.InvariantCulture) + " h";
    }

    /// <summary>"1 session", "3 sessions".</summary>
    public static string Count(long count, string noun) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    /// <summary>A whole number, grouped.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A share, as "83%" - without the space the invariant culture's percent pattern puts in.</summary>
    public static string Percent(double share) =>
        Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
}

// -------------------------------------------------------------------------------------------------------
// The snapshot: database-wide, the same for every visitor, never shown as it is
// -------------------------------------------------------------------------------------------------------

/// <summary>One client session of the database, as <c>pg_stat_activity</c> had it.</summary>
/// <param name="ProcessId">Its backend's process id. A detail.</param>
/// <param name="User">Its role. A detail.</param>
/// <param name="Application">Its <c>application_name</c>. A detail.</param>
/// <param name="ClientAddress">Its client's address, or <see langword="null" /> for a Unix socket. A detail.</param>
/// <param name="BackendStart">When the backend started. A detail.</param>
/// <param name="TransactionStart">When its transaction began, or <see langword="null" /> outside one.</param>
/// <param name="QueryStart">When its last statement began.</param>
/// <param name="State">Its state, or <see langword="null" /> when the role may not read it.</param>
/// <param name="WaitEventType">The class of what it waits for (<c>Lock</c>, <c>IO</c>, <c>Client</c>…).</param>
/// <param name="WaitEvent">What it waits for, exactly. A detail.</param>
/// <param name="HasTransactionId">Whether it has written in its current transaction.</param>
/// <param name="HasSnapshot">Whether it holds a snapshot, which pins vacuum.</param>
/// <param name="TransactionAge">How long its transaction has been open, as of the read.</param>
/// <param name="QueryAge">How long since its last statement began.</param>
/// <param name="StateAge">How long it has been in its state.</param>
/// <param name="BlockedBy">The process ids blocking it, for a session that waits for a lock. A detail.</param>
/// <param name="Query">Its statement, cut on the server. A detail, masked before it is shown.</param>
/// <param name="QueryTruncated">Whether Postgres or the read cut the statement.</param>
internal sealed record SessionRow(
    int ProcessId,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? BackendStart,
    DateTimeOffset? TransactionStart,
    DateTimeOffset? QueryStart,
    string? State,
    string? WaitEventType,
    string? WaitEvent,
    bool HasTransactionId,
    bool HasSnapshot,
    TimeSpan? TransactionAge,
    TimeSpan? QueryAge,
    TimeSpan? StateAge,
    IReadOnlyList<int> BlockedBy,
    string? Query,
    bool QueryTruncated)
{
    /// <summary>Whether it is idle inside a transaction.</summary>
    public bool IsIdleInTransaction => State is "idle in transaction" or "idle in transaction (aborted)";
}

/// <summary>One lock a session of the database holds or waits for, as <c>pg_locks</c> had it.</summary>
/// <param name="ProcessId">The session's process id. A detail.</param>
/// <param name="LockType"><c>relation</c>, <c>tuple</c>, <c>transactionid</c>, <c>advisory</c>…</param>
/// <param name="Mode"><c>AccessShareLock</c>, <c>RowExclusiveLock</c>, <c>ExclusiveLock</c>…</param>
/// <param name="Granted">Whether it is held rather than awaited.</param>
/// <param name="Relation">
/// For a relation or tuple lock, the table the locked relation belongs to and which part of it is locked - never
/// the name of the index, partition or TOAST table itself.
/// </param>
/// <param name="AdvisoryKey">The advisory key, for a one-key advisory lock.</param>
/// <param name="Waited">How long it has been awaited, on PostgreSQL 14 and later.</param>
internal sealed record LockRow(
    int ProcessId,
    string LockType,
    string Mode,
    bool Granted,
    LockedRelation? Relation,
    long? AdvisoryKey,
    TimeSpan? Waited);

/// <summary>One prepared transaction of the database, as <c>pg_prepared_xacts</c> had it.</summary>
/// <param name="Gid">Its global identifier. A detail.</param>
/// <param name="Owner">The role that prepared it. A detail.</param>
/// <param name="Prepared">When it was prepared.</param>
/// <param name="Age">How long ago, as of the read.</param>
/// <param name="TransactionId">Its transaction id. A detail.</param>
internal sealed record PreparedRow(string Gid, string Owner, DateTimeOffset Prepared, TimeSpan Age, string TransactionId);

/// <summary>
/// One read of the database's sessions, locks and prepared transactions - shared by every visitor of the
/// database for a few seconds, and never shown as it is: what a visitor sees of it is
/// <see cref="SessionActivitySummary" /> or, past the activity gate, <see cref="SessionActivityDetails" />.
/// </summary>
/// <remarks>
/// <b>Where per-application totals belong (issue #8, G9).</b> <see cref="Sessions" /> is capped and ordered by
/// transaction age, so grouping it by <c>application_name</c> would undercount exactly the busy applications such a
/// view is for. Connections by application - total, active, idle, idle in transaction, waiting, oldest transaction -
/// are an aggregate of their own over every session the role may see, read in this same snapshot as one more
/// statement and carried as one more member here: the snapshot stays the one read every visitor shares, the summary
/// still names no application, and the details gate (<c>ViewDatabaseActivity</c>) is what would show them, because an
/// application name is a detail. The reader takes a <c>ResolvedSource</c>, so the same read serves any source's
/// database - which is what a diagnostics API over it (G10) needs.
/// </remarks>
/// <param name="ReadAt">When it was read, by the studio's clock.</param>
/// <param name="Visibility">How much of it the role could see.</param>
/// <param name="TotalSessions">How many client sessions the database has, beyond <see cref="Sessions" /> when that was capped.</param>
/// <param name="Sessions">The sessions, oldest transaction first, at most <c>MaxSessions</c>.</param>
/// <param name="TotalLocks">How many locks, likewise.</param>
/// <param name="Locks">The locks, waiters first, at most <c>MaxLocks</c>.</param>
/// <param name="MaxPreparedTransactions"><c>max_prepared_transactions</c>: zero means none can exist.</param>
/// <param name="Prepared">The prepared transactions, oldest first.</param>
/// <param name="Statements">What was run, for Show SQL.</param>
internal sealed record SessionSnapshot(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    long TotalSessions,
    IReadOnlyList<SessionRow> Sessions,
    long TotalLocks,
    IReadOnlyList<LockRow> Locks,
    int MaxPreparedTransactions,
    IReadOnlyList<PreparedRow> Prepared,
    IReadOnlyList<HealthStatement> Statements)
{
    /// <summary>
    /// Connections by application (issue #8 G9, W5-API) and the server's connection budget, read in the same snapshot - or
    /// <see langword="null" /> from a reader that did not read them (a test's).
    /// </summary>
    public SessionConnections? Connections { get; init; }

    /// <summary>
    /// The lock table's blocking graph (X3): every waiting session and every session blocking one, read in the same snapshot
    /// with the statement a sweep reads the lock table with - or <see langword="null" /> from a reader that did not read it.
    /// </summary>
    public LockTableRead? Blocking { get; init; }

    /// <summary>
    /// PostgreSQL 19's <c>pg_stat_lock</c>, by lock type - or <see langword="null" /> below 19, where the view does not exist,
    /// and from a reader that did not read it.
    /// </summary>
    public IReadOnlyList<LockStatisticsRow>? LockStatistics { get; init; }

    /// <summary>
    /// The whole server's client sessions by database, application, role and client address (issue #20) - the one part of this
    /// snapshot that is not about this database - read in the same snapshot, or <see langword="null" /> from a reader that did
    /// not read it (a test's).
    /// </summary>
    public SessionServerHolders? Holders { get; init; }
}

/// <summary>One row of PostgreSQL 19's <c>pg_stat_lock</c>: the whole server's counters for one lock type, since their reset.</summary>
/// <param name="LockType"><c>relation</c>, <c>transactionid</c>, <c>advisory</c>…</param>
/// <param name="Waits">How many lock requests of the type had to wait.</param>
/// <param name="WaitTimeMilliseconds">How long they waited in all, in milliseconds.</param>
/// <param name="FastPathExceeded">How often a backend's fast-path lock slots were full, so the lock went to the shared table.</param>
/// <param name="StatsReset">When the counters were last reset.</param>
internal sealed record LockStatisticsRow(string LockType, long Waits, double WaitTimeMilliseconds, long FastPathExceeded, DateTimeOffset? StatsReset);

/// <summary>
/// One application's client sessions of the database, by state - or, with no <see cref="Application" />, the database's own
/// total. Every role sees every session's application; a state the role may not read is counted in <see cref="StateUnknown" />.
/// </summary>
/// <param name="Application">The <c>application_name</c>, <c>""</c> for sessions that set none; <see langword="null" /> for the total.</param>
/// <param name="Total">Every session.</param>
/// <param name="Active">Running a statement.</param>
/// <param name="Idle">Idle, outside a transaction.</param>
/// <param name="IdleInTransaction">Idle in a transaction, aborted ones included.</param>
/// <param name="Waiting">Waiting on a lock.</param>
/// <param name="StateUnknown">Another role's, whose state this role may not read (no <c>pg_read_all_stats</c>).</param>
/// <param name="OldestTransaction">The longest open transaction it may see, or <see langword="null" />.</param>
internal sealed record SessionApplicationCounts(
    string? Application,
    long Total,
    long Active,
    long Idle,
    long IdleInTransaction,
    long Waiting,
    long StateUnknown,
    TimeSpan? OldestTransaction);

/// <summary>
/// What <see cref="PostgresStudio.Internal.Sql.ConnectionsByApplicationQueries.Read" /> read: the database's sessions as a
/// whole and by application, and the server's connection budget. Shown at two gates: the totals and the budget at
/// <c>Database</c>, the applications - their names - at <c>Activity</c>.
/// </summary>
/// <param name="CanReadAllStats">Whether the role reads every session's state (<c>pg_read_all_stats</c>).</param>
/// <param name="MaxConnections"><c>max_connections</c>.</param>
/// <param name="Reserved"><c>superuser_reserved_connections</c>, plus <c>reserved_connections</c> from PostgreSQL 16.</param>
/// <param name="ServerConnections">Client sessions on the whole server.</param>
/// <param name="Database">This database's total.</param>
/// <param name="Applications">The applications, the most sessions first, at most <c>MaxApplications</c>.</param>
/// <param name="ApplicationCount">How many applications there are, past the cap included.</param>
internal sealed record SessionConnections(
    bool CanReadAllStats,
    int MaxConnections,
    int Reserved,
    long ServerConnections,
    SessionApplicationCounts Database,
    IReadOnlyList<SessionApplicationCounts> Applications,
    long ApplicationCount)
{
    /// <summary>Whether every session's state was read: the role reads every state, or no other role's session was connected.</summary>
    public bool AllSessionsVisible => CanReadAllStats || Database.StateUnknown == 0;

    /// <summary>The budget alone, which names no application: what the database-wide gate shows.</summary>
    public SessionConnectionBudget ToBudget() => new(MaxConnections, Reserved, ServerConnections, AllSessionsVisible);
}

/// <summary>The server's connection budget and how much of it is spent: database-wide numbers, naming nothing.</summary>
/// <param name="MaxConnections"><c>max_connections</c>.</param>
/// <param name="Reserved"><c>superuser_reserved_connections</c>, plus <c>reserved_connections</c> from PostgreSQL 16.</param>
/// <param name="ServerConnections">Client sessions on the whole server.</param>
/// <param name="AllSessionsVisible">Whether every session's state was read.</param>
internal sealed record SessionConnectionBudget(int MaxConnections, int Reserved, long ServerConnections, bool AllSessionsVisible);

// -------------------------------------------------------------------------------------------------------
// What a visitor is shown
// -------------------------------------------------------------------------------------------------------

/// <summary>Whether this visitor may see session details, and the sentence when not.</summary>
/// <param name="CapabilityEnabled">Whether <c>ViewDatabaseActivity</c> is on and <c>ReadOnly</c> is off.</param>
/// <param name="ReadOnly">Whether <c>MartenStudioOptions.ReadOnly</c> is the reason.</param>
/// <param name="Authorized">The write policy's answer, or <see langword="null" /> when nobody was asked.</param>
/// <param name="Refusal">Which gate is shut, or <see cref="DatabaseRefusal.None" />.</param>
/// <param name="Denial">What it would take, naming the option or the policy.</param>
internal sealed record SessionDetailsAccess(
    bool CapabilityEnabled,
    bool ReadOnly,
    bool? Authorized,
    DatabaseRefusal Refusal,
    string? Denial)
{
    /// <summary>Whether details may be read.</summary>
    public bool IsOpen => Refusal == DatabaseRefusal.None && CapabilityEnabled && Authorized != false;
}

/// <summary>One session as a visitor without details sees it: no process id, role, client, application or statement.</summary>
/// <param name="State">Its state, or <see langword="null" /> when the role may not read it.</param>
/// <param name="WaitEventType">The class of what it waits for.</param>
/// <param name="TransactionAge">How long its transaction has been open.</param>
/// <param name="QueryAge">How long since its last statement began.</param>
/// <param name="StateAge">How long it has been in its state.</param>
/// <param name="BlockedByCount">How many sessions block it.</param>
/// <param name="HasTransactionId">Whether it has written.</param>
internal sealed record SessionSummaryRow(
    string? State,
    string? WaitEventType,
    TimeSpan? TransactionAge,
    TimeSpan? QueryAge,
    TimeSpan? StateAge,
    int BlockedByCount,
    bool HasTransactionId);

/// <summary>The locks on one thing in one mode, counted.</summary>
/// <param name="Target">What they are on, in Marten's terms.</param>
/// <param name="Mode">The mode.</param>
/// <param name="Granted">Whether held or awaited.</param>
/// <param name="Count">How many.</param>
/// <param name="LongestWait">The longest wait among them, when awaited.</param>
internal sealed record LockSummaryRow(string Target, string Mode, bool Granted, int Count, TimeSpan? LongestWait);

/// <summary>The in-process pool of the database's data source.</summary>
/// <param name="Matched">Whether the pool was found among the process's pools.</param>
/// <param name="Used">Connections lent out.</param>
/// <param name="Idle">Connections open and idle.</param>
/// <param name="Max">Its ceiling.</param>
/// <param name="Pooling">Whether the data source pools at all.</param>
/// <param name="Ambiguous">
/// Whether more than one pool in the process reports under this database's connection string, so none of them is
/// shown as this database's.
/// </param>
internal sealed record SessionPoolSummary(bool Matched, int? Used, int? Idle, int? Max, bool Pooling, bool Ambiguous = false);

/// <summary>Everything Database › Sessions shows a visitor the store policy passes for the database as a whole.</summary>
internal sealed record SessionActivitySummary
{
    /// <summary>Why there is no summary, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or a failed read.</summary>
    public string? Reason { get; init; }

    /// <summary>When the snapshot behind it was read.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>How many client sessions the database has, the studio's own read excluded.</summary>
    public long SessionCount { get; init; }

    /// <summary>The sessions by state, most first. A null state is one the role cannot see.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> ByState { get; init; } = [];

    /// <summary>Every session, without anything that identifies it.</summary>
    public IReadOnlyList<SessionSummaryRow> Sessions { get; init; } = [];

    /// <summary>How many sessions wait for a lock.</summary>
    public int Waiting { get; init; }

    /// <summary>The longest open transaction.</summary>
    public TimeSpan? LongestTransaction { get; init; }

    /// <summary>The locks, counted by what, mode and whether granted.</summary>
    public IReadOnlyList<LockSummaryRow> Locks { get; init; } = [];

    /// <summary>How many locks there were in all.</summary>
    public long LockCount { get; init; }

    /// <summary>How many prepared transactions the database has.</summary>
    public int PreparedCount { get; init; }

    /// <summary>The oldest prepared transaction's age.</summary>
    public TimeSpan? OldestPrepared { get; init; }

    /// <summary><c>max_prepared_transactions</c>.</summary>
    public int MaxPreparedTransactions { get; init; }

    /// <summary>The database's pool in this process, while the page holds a lease on the observer.</summary>
    public SessionPoolSummary? Pool { get; init; }

    /// <summary>
    /// The database's client sessions by state, as one total row, and the server's connection budget (issue #8 G9) - numbers
    /// about the database as a whole, with no application named - or <see langword="null" /> when they were not read.
    /// </summary>
    public SessionApplicationCounts? Connections { get; init; }

    /// <summary>The server's connection budget beside <see cref="Connections" />, or <see langword="null" />.</summary>
    public SessionConnectionBudget? Budget { get; init; }

    /// <summary>How many applications are connected: a count, which names none.</summary>
    public long ApplicationCount { get; init; }

    /// <summary>How sure: the role's visibility, the caps.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    /// <summary>
    /// Sessions › Blocking's counts (X3): how many chains, how many sessions they hold up, the longest wait and how many heads
    /// sit idle in their transactions - numbers about the database, which name nobody. The tree is the details'.
    /// </summary>
    public SessionBlockingCounts Blocking { get; init; } = SessionBlockingCounts.None;

    /// <summary>
    /// PostgreSQL 19's <c>pg_stat_lock</c>, by lock type - the whole server's counters, which name nobody - or
    /// <see langword="null" /> below 19, where the section is absent.
    /// </summary>
    public IReadOnlyList<LockStatisticsRow>? LockStatistics { get; init; }

    /// <summary>Whether this visitor may see the details, and what it would take.</summary>
    public SessionDetailsAccess Details { get; init; } = new(false, false, null, DatabaseRefusal.CapabilityOff, null);

    /// <summary>
    /// Sessions › Holders' counts (issue #20): the whole server's connections per database the source policy lets this visitor
    /// be told the name of, and every other database together - naming no application, role or address - or <see langword="null" />
    /// when they were not read.
    /// </summary>
    public SessionHoldersSummary? Holders { get; init; }

    /// <summary>What was run, for Show SQL.</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A summary that says why there is none.</summary>
    public static SessionActivitySummary Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>One session, past the activity gate.</summary>
/// <param name="ProcessId">Its process id.</param>
/// <param name="User">Its role.</param>
/// <param name="Application">Its application name.</param>
/// <param name="ClientAddress">Its client's address.</param>
/// <param name="BackendStart">When its backend started.</param>
/// <param name="State">Its state.</param>
/// <param name="WaitEventType">The class of what it waits for.</param>
/// <param name="WaitEvent">What it waits for.</param>
/// <param name="TransactionAge">How long its transaction has been open.</param>
/// <param name="QueryAge">How long since its last statement began.</param>
/// <param name="StateAge">How long it has been in its state.</param>
/// <param name="BlockedBy">Who blocks it.</param>
/// <param name="Statement">Its statement, masked for this visitor and capped.</param>
internal sealed record SessionDetailRow(
    int ProcessId,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? BackendStart,
    string? State,
    string? WaitEventType,
    string? WaitEvent,
    TimeSpan? TransactionAge,
    TimeSpan? QueryAge,
    TimeSpan? StateAge,
    IReadOnlyList<int> BlockedBy,
    SessionStatementText Statement);

/// <summary>One lock, past the activity gate.</summary>
/// <param name="ProcessId">The session holding or awaiting it.</param>
/// <param name="Target">What it is on, in Marten's terms.</param>
/// <param name="Relation">
/// The relation it is on, qualified - with a withheld schema's name masked - or <see langword="null" /> where the
/// relation is one nobody may be told about, or one in a schema this visitor may not see.
/// </param>
/// <param name="Mode">The mode.</param>
/// <param name="Granted">Whether held or awaited.</param>
/// <param name="Waited">How long it has been awaited.</param>
internal sealed record LockDetailRow(int ProcessId, string Target, string? Relation, string Mode, bool Granted, TimeSpan? Waited);

/// <summary>One prepared transaction, past the activity gate.</summary>
/// <param name="Gid">Its global identifier.</param>
/// <param name="Owner">Its owner.</param>
/// <param name="Prepared">When it was prepared.</param>
/// <param name="Age">How long ago.</param>
internal sealed record PreparedDetailRow(string Gid, string Owner, DateTimeOffset Prepared, TimeSpan Age);

/// <summary>
/// What Database › Sessions shows a visitor through the <c>ViewDatabaseActivity</c> gate: who, from where,
/// running what - or the refusal, as a value.
/// </summary>
internal sealed record SessionActivityDetails
{
    /// <summary>Which gate refused, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence, naming the option or the policy.</summary>
    public string? Reason { get; init; }

    /// <summary>When the snapshot behind it was read.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>The sessions.</summary>
    public IReadOnlyList<SessionDetailRow> Sessions { get; init; } = [];

    /// <summary>The locks.</summary>
    public IReadOnlyList<LockDetailRow> Locks { get; init; } = [];

    /// <summary>The prepared transactions.</summary>
    public IReadOnlyList<PreparedDetailRow> Prepared { get; init; } = [];

    /// <summary>
    /// Sessions › Blocking's tree (X3): each chain's head and the sessions waiting on it, nested, the most held up first -
    /// every session named, so behind the same gate as the rest of the details.
    /// </summary>
    public IReadOnlyList<BlockingChainView> Blocking { get; init; } = [];

    /// <summary>
    /// This database's sessions by <c>application_name</c>, the most first (issue #8 G9): an application's name is another
    /// program's, so it is a detail.
    /// </summary>
    public IReadOnlyList<SessionApplicationCounts> Applications { get; init; } = [];

    /// <summary>How many applications there are, past the read's cap included.</summary>
    public long ApplicationCount { get; init; }

    /// <summary>
    /// Sessions › Holders' groups (issue #20): the whole server's client sessions by database, application, role and client
    /// address, every database by name - other databases' applications, roles and addresses as much as this one's, so behind the
    /// same gate.
    /// </summary>
    public SessionHoldersDetails Holders { get; init; } = SessionHoldersDetails.None;

    /// <summary>
    /// Whether the statements are withheld outright, because the visitor's withheld schemas could not be read.
    /// </summary>
    public bool StatementsWithheld { get; init; }

    /// <summary>Details that say why there are none.</summary>
    public static SessionActivityDetails Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>Sessions › Blocking's numbers (X3): database-wide, naming nobody.</summary>
/// <param name="Chains">How many chains have a head - a session that blocks others and waits for no lock itself.</param>
/// <param name="Held">How many sessions they hold up, each once.</param>
/// <param name="LongestWait">The longest wait among them, or <see langword="null" /> when nothing waits.</param>
/// <param name="IdleHeads">How many of the heads sit idle in their transactions - C12's <c>idle-head</c>.</param>
/// <param name="Unrooted">How many chains have no head: sessions waiting on each other, or on sessions the read did not list.</param>
internal sealed record SessionBlockingCounts(int Chains, int Held, TimeSpan? LongestWait, int IdleHeads, int Unrooted)
{
    /// <summary>Nothing waits.</summary>
    public static SessionBlockingCounts None { get; } = new(0, 0, null, 0, 0);
}

/// <summary>One session of Sessions › Blocking's tree, past the activity gate (X3).</summary>
/// <remarks>
/// The relation is the details' and masked for this visitor (D27): qualified for a schema they may see, <c>‹withheld›</c> -
/// and no name - for one they may not, and nothing for a relation nobody is told about (a hidden document type's, another
/// store's, one only the <c>mt_</c> rule calls Marten's in Postgres Studio). <see cref="Target" /> says what it is in the
/// studio's words either way.
/// </remarks>
internal sealed record BlockingNodeView
{
    /// <summary>Its process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Its role.</summary>
    public string? User { get; init; }

    /// <summary>Its application name.</summary>
    public string? Application { get; init; }

    /// <summary>Its client's address, or <see langword="null" /> for a Unix socket.</summary>
    public string? ClientAddress { get; init; }

    /// <summary>Its state, or <see langword="null" /> when the role may not read it or it is another database's.</summary>
    public string? State { get; init; }

    /// <summary>The class of what it waits for.</summary>
    public string? WaitEventType { get; init; }

    /// <summary>What it waits for, exactly.</summary>
    public string? WaitEvent { get; init; }

    /// <summary>How long its transaction has been open.</summary>
    public TimeSpan? TransactionAge { get; init; }

    /// <summary>How long it has been in its state.</summary>
    public TimeSpan? StateAge { get; init; }

    /// <summary>How long it has waited for its lock, for one that waits.</summary>
    public TimeSpan? Waited { get; init; }

    /// <summary>Whether <see cref="Waited" /> is measured from <c>pg_locks.waitstart</c> rather than its last state change.</summary>
    public bool ExactWait { get; init; } = true;

    /// <summary>The mode it waits for, for one that waits.</summary>
    public string? WantedMode { get; init; }

    /// <summary>The modes it holds on what the sessions below it wait for.</summary>
    public IReadOnlyList<string> HeldModes { get; init; } = [];

    /// <summary>What it waits for, in the studio's words, for one that waits - masked for this visitor.</summary>
    public string? Target { get; init; }

    /// <summary>The relation it waits for, qualified, <c>‹withheld›</c>, or <see langword="null" />.</summary>
    public string? Relation { get; init; }

    /// <summary>Whether it is a session of this database.</summary>
    public bool SameDatabase { get; init; } = true;

    /// <summary>Whether <c>pg_stat_activity</c> still had it when read.</summary>
    public bool Known { get; init; } = true;

    /// <summary>What it is when it is a <c>VACUUM</c>.</summary>
    public string? VacuumLabel { get; init; }

    /// <summary>Who blocks it.</summary>
    public IReadOnlyList<int> BlockedBy { get; init; } = [];

    /// <summary>Why its waiters are not drawn here, when they are not.</summary>
    public BlockingRepeat Repeat { get; init; }

    /// <summary>Who waits on it, the longest wait first.</summary>
    public IReadOnlyList<BlockingNodeView> Waiters { get; init; } = [];
}

/// <summary>One chain of Sessions › Blocking, past the activity gate (X3): its head, everybody below it, and what to hand over.</summary>
/// <param name="Head">The head and its tree.</param>
/// <param name="HeadWaits">Whether the head waits for a lock itself: the chain has no session that holds the others up alone.</param>
/// <param name="Held">How many sessions it holds up, each once.</param>
/// <param name="LongestWait">The longest wait in it.</param>
internal sealed record BlockingChainView(BlockingNodeView Head, bool HeadWaits, int Held, TimeSpan? LongestWait)
{
    /// <summary>
    /// The statement that would end the head's session, for a person to run - <see langword="null" /> for a chain with no head,
    /// or a head that has gone. The studio never runs it.
    /// </summary>
    public string? Terminate => HeadWaits || !Head.Known ? null : Internal.Sql.SessionHandOver.Terminate(Head.ProcessId);

    /// <summary>The statement that would cancel the head's running statement, likewise - and only for a head that runs one.</summary>
    public string? Cancel => HeadWaits || !Head.Known || Head.State is not "active" ? null : Internal.Sql.SessionHandOver.Cancel(Head.ProcessId);

    /// <summary>Whether the head sits idle in its transaction - which only ending its session ends.</summary>
    public bool IdleHead => !HeadWaits && Head.State is "idle in transaction" or "idle in transaction (aborted)";
}
