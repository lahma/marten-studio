using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// The connection checks' thresholds: the plan's first values (§3.4), as constants, because a threshold is
/// made configurable only once its unit is honest.
/// </summary>
internal static class ConnectionThresholds
{
    /// <summary>C01: a pool more than this share in use is a Warning.</summary>
    public const double PoolUsedWarning = 0.8;

    /// <summary>C01: a pool fully in use on this many consecutive samples is Critical.</summary>
    public const int PoolSaturatedSamples = 2;

    /// <summary>C03: the server's connections more than this share in use is a Warning…</summary>
    public const double ServerUsedWarning = 0.8;

    /// <summary>C03: …and at least this share is Critical.</summary>
    public const double ServerUsedCritical = 0.95;

    /// <summary>C04: idle in a transaction this long is a Warning…</summary>
    public static readonly TimeSpan IdleInTransactionWarning = TimeSpan.FromMinutes(1);

    /// <summary>C04: …and this long is Critical.</summary>
    public static readonly TimeSpan IdleInTransactionCritical = TimeSpan.FromMinutes(10);

    /// <summary>
    /// C04: how long Marten's own HotCold leader - a session holding one of the store's daemon lock ids - may sit idle in its
    /// leadership transaction before it is raised. Three minutes: about three times the 61 s measured between DistributedLock's
    /// keepalives at Weasel 9.45 (Marten 9.50.2, CL3), so a keepalive a loaded host runs late is no finding, and short enough
    /// that a leader whose keepalive has stopped - monitoring off, a polling interval of minutes - is raised within minutes.
    /// </summary>
    public static readonly TimeSpan DaemonLeaderIdleBound = TimeSpan.FromMinutes(3);

    /// <summary>C05: a statement running this long is a Warning.</summary>
    public static readonly TimeSpan LongRunningWarning = TimeSpan.FromMinutes(5);

    /// <summary>C06: a lock awaited this long is a Warning…</summary>
    public static readonly TimeSpan LockWaitWarning = TimeSpan.FromSeconds(30);

    /// <summary>C06: …and this long is Critical.</summary>
    public static readonly TimeSpan LockWaitCritical = TimeSpan.FromMinutes(5);

    /// <summary>C07: a prepared transaction this old is Critical.</summary>
    public static readonly TimeSpan PreparedTransactionCritical = TimeSpan.FromMinutes(5);

    /// <summary>C08: rollbacks above this share of the window's transactions are a Warning…</summary>
    public const double RollbackShareWarning = 0.05;

    /// <summary>C08: …once the window holds at least this many transactions, the studio's own subtracted.</summary>
    public const long RollbackMinimumTransactions = 1_000;

    /// <summary>
    /// C09: the connections the async daemon holds beside its shards - the high-water detection's, and under
    /// HotCold the leadership lock's (both from the same data source, read at Marten V9.39.0).
    /// </summary>
    public const int DaemonOwnConnections = 1;

    /// <summary>
    /// The shortest age a gatherer reads at all, so a session that went idle a moment ago is not read. The
    /// thresholds are the evaluator's, which is what lets an evaluator be asked about a snapshot at a later
    /// time.
    /// </summary>
    public static readonly TimeSpan CandidateFloor = TimeSpan.FromSeconds(1);

    /// <summary>How many sessions a finding names in its gated evidence; the count says how many there are.</summary>
    public const int SessionsNamed = 5;
}

/// <summary>
/// A snapshot's age at the evaluation's time: how old it was when Postgres read it, plus however long has
/// passed since by the studio's clock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Skew-free.</b> Every age is computed by Postgres (<c>now() - state_change</c>), so the studio's clock and
/// the server's never meet in one subtraction: a host whose clock is a few minutes off Postgres' gets the same
/// answer as one that is not. In a sweep the evaluation's time is the gatherer's, so the second term is zero.
/// </para>
/// <para>
/// <b>And testable.</b> An evaluator asked about a snapshot later than it was read answers what it would say
/// had nothing changed since - which is how a live test turns a prepared transaction that is a second old into
/// one that is five minutes old without waiting five minutes for it.
/// </para>
/// </remarks>
internal static class SnapshotAge
{
    /// <summary><paramref name="ageAtRead" /> as of <paramref name="evaluation" />.</summary>
    public static TimeSpan At(TimeSpan ageAtRead, DateTimeOffset readAt, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        TimeSpan since = evaluation.Now - readAt;
        return ageAtRead + (since > TimeSpan.Zero ? since : TimeSpan.Zero);
    }
}

/// <summary>The server's connection settings and how much of them is spent - C02 and C03.</summary>
/// <param name="ClientBackends">Client connections on the whole server.</param>
/// <param name="ThisDatabase">Client connections to this database.</param>
/// <param name="MaxConnections"><c>max_connections</c>.</param>
/// <param name="SuperuserReserved"><c>superuser_reserved_connections</c>.</param>
/// <param name="Reserved"><c>reserved_connections</c> (PostgreSQL 16+), zero before.</param>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
internal sealed record ServerConnectionLimits(
    long ClientBackends,
    long ThisDatabase,
    int MaxConnections,
    int SuperuserReserved,
    int Reserved,
    int ServerVersion)
{
    /// <summary>What an ordinary role may open: <c>max_connections</c> less both reservations, never below one.</summary>
    public int Available => Math.Max(1, MaxConnections - SuperuserReserved - Reserved);

    /// <summary>"97 (max_connections 100 − 3 superuser_reserved_connections − 0 reserved_connections)".</summary>
    public string AvailableText =>
        Available.ToString("N0", CultureInfo.InvariantCulture)
        + " (max_connections " + MaxConnections.ToString(CultureInfo.InvariantCulture)
        + " − " + SuperuserReserved.ToString(CultureInfo.InvariantCulture) + " superuser_reserved_connections"
        + (ServerVersion >= 160000
            ? " − " + Reserved.ToString(CultureInfo.InvariantCulture) + " reserved_connections"
            : string.Empty)
        + ")";

    /// <summary>The sweep's one read of it, shared by C02 and C03 (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    public static Task<ServerConnectionLimits> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared("connections.server", token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    public static async Task<ServerConnectionLimits> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ServerConnectionLimits> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.ServerConnections(), Map, cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The server's connection settings came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
    }

    private static ServerConnectionLimits Map(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetInt64(1),
        reader.GetInt32(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetInt32(5));
}

/// <summary>Evidence about named sessions, worded once for every connection check.</summary>
internal static class SessionEvidence
{
    /// <summary>
    /// One session's identifying details, behind the activity gate: process id, role, application, client
    /// and when its backend started - the fields <c>ViewDatabaseActivity</c> exists to guard.
    /// </summary>
    /// <param name="label">The evidence label, unique within the finding ("Session 1 (oldest)").</param>
    /// <param name="processId">Its process id.</param>
    /// <param name="user">Its role.</param>
    /// <param name="application">Its application name.</param>
    /// <param name="client">Its client address; <see langword="null" /> is a local socket.</param>
    /// <param name="backendStart">When its backend started.</param>
    /// <param name="what">What it is doing, in the check's words ("idle in transaction for 12 min").</param>
    public static HealthEvidence Session(
        string label,
        int processId,
        string? user,
        string? application,
        string? client,
        DateTimeOffset? backendStart,
        string what) =>
        new(label, Describe(processId, user, application, client, backendStart, what), HealthGate.Activity);

    /// <summary>The value of a <see cref="Session" /> line.</summary>
    public static string Describe(int processId, string? user, string? application, string? client, DateTimeOffset? backendStart, string what) =>
        "pid " + processId.ToString(CultureInfo.InvariantCulture)
        + ", role " + (user ?? "(not readable)")
        + (string.IsNullOrEmpty(application) ? string.Empty : ", application '" + application + "'")
        + ", from " + (client ?? "a local socket")
        + (backendStart is { } started ? ", connected since " + started.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) : string.Empty)
        + ": " + what;

    /// <summary>The label of the <paramref name="index" />th named item (zero-based), unique within a finding.</summary>
    /// <param name="noun">What it is: "Session", "Waiter".</param>
    /// <param name="index">Its place, zero-based, in the order the finding sorted them.</param>
    /// <param name="first">What the first one is the most of: "longest", "oldest".</param>
    public static string Label(string noun, int index, string first = "longest") =>
        noun + " " + (index + 1).ToString(CultureInfo.InvariantCulture) + (index == 0 ? " (" + first + ")" : string.Empty);

    /// <summary>The caveat lines a session check shares: what the role can see, and when it was read.</summary>
    public static string Caveat(SessionVisibility visibility, DateTimeOffset readAt, string? extra = null)
    {
        ArgumentNullException.ThrowIfNull(visibility);

        List<string> parts =
        [
            "Read in pg_stat_activity, for this database only, at " + readAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + "; ages are measured by Postgres's own clock.",
        ];

        if (visibility.Caveat is { } hidden)
        {
            parts.Add(hidden);
        }

        if (extra is { Length: > 0 })
        {
            parts.Add(extra);
        }

        return string.Join(" ", parts);
    }

    /// <summary>The finding's link to Database › Sessions, where the sessions it counts are listed for the visitor.</summary>
    public static HealthLink SessionsLink { get; } = new("Open Database › Sessions", SessionLinks.Route);

    /// <summary>
    /// The sentence that points at the page with the statement text: findings never carry it, because what a
    /// visitor may read of another application's statement depends on which schemas they may see.
    /// </summary>
    public const string StatementsOnSessionsPage =
        "The statements themselves are on Database › Sessions, behind MartenStudioOptions.Capabilities.ViewDatabaseActivity, "
        + "masked for the schemas you may not see.";
}
