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

    /// <summary>The page, with the scope carried (D9) under the studio's own source parameter.</summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio's descriptor: its source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    public static string ToSessions(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        return StudioScopeQuery.Append(StudioLink.To(settings, Route), scope, descriptor.SourceQueryParameter);
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
    IReadOnlyList<HealthStatement> Statements);

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

    /// <summary>How sure: the role's visibility, the caps.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];

    /// <summary>Whether this visitor may see the details, and what it would take.</summary>
    public SessionDetailsAccess Details { get; init; } = new(false, false, null, DatabaseRefusal.CapabilityOff, null);

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
    /// Whether the statements are withheld outright, because the visitor's withheld schemas could not be read.
    /// </summary>
    public bool StatementsWithheld { get; init; }

    /// <summary>Details that say why there are none.</summary>
    public static SessionActivityDetails Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}
