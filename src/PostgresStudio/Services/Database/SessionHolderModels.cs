using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Database;

// -------------------------------------------------------------------------------------------------------
// Database › Sessions › Holders (issue #20): who holds the whole server's connections
// -------------------------------------------------------------------------------------------------------

/// <summary>
/// One group of the server's client sessions: the sessions of one database, application, role and client address - or, for
/// the sessions a role without <c>pg_read_all_stats</c> may not see into, of one database, application and role whose client
/// address is not readable.
/// </summary>
/// <remarks>
/// A detail in every respect but its counts: the database, the application, the role and the address name other people's
/// sessions (D29), and a visitor short of <c>ViewDatabaseActivity</c> is shown <see cref="SessionHolderCounts" /> in their
/// place, per database the source policy admits and for the rest together.
/// </remarks>
/// <param name="Database">The database the sessions are connected to; <c>""</c> when Postgres named none.</param>
/// <param name="Application">The <c>application_name</c>, <c>""</c> for sessions that set none.</param>
/// <param name="Role">The role, <c>""</c> when Postgres named none.</param>
/// <param name="ClientAddress">The client's address, or <see langword="null" /> for a Unix socket - or for <paramref name="Hidden" /> sessions.</param>
/// <param name="Hidden">Whether the role may not read these sessions' state, timings and client (no <c>pg_read_all_stats</c>).</param>
/// <param name="Total">How many sessions.</param>
/// <param name="Active">Running a statement.</param>
/// <param name="Idle">Idle, outside a transaction.</param>
/// <param name="IdleInTransaction">Idle in a transaction, aborted ones included.</param>
/// <param name="StateUnknown">Whose state this role may not read.</param>
/// <param name="OldestIdle">How long the longest-idle one has been idle, or <see langword="null" /> when none is.</param>
/// <param name="OldestIdleInTransaction">How long the longest has been idle inside its transaction, or <see langword="null" />.</param>
/// <param name="ThisDatabase">Whether the database is the one the studio's connection is on.</param>
internal sealed record SessionHolderGroup(
    string Database,
    string Application,
    string Role,
    string? ClientAddress,
    bool Hidden,
    long Total,
    long Active,
    long Idle,
    long IdleInTransaction,
    long StateUnknown,
    TimeSpan? OldestIdle,
    TimeSpan? OldestIdleInTransaction,
    bool ThisDatabase = false)
{
    /// <summary>How the page says <see cref="ClientAddress" />: the address, <c>local socket</c>, or <c>not readable</c>.</summary>
    public string ClientText => Hidden ? "not readable" : ClientAddress ?? "local socket";

    /// <summary>How the page says <see cref="Application" />: its name, or <c>(none set)</c>.</summary>
    public string ApplicationText => Application.Length == 0 ? "(none set)" : Application;
}

/// <summary>
/// The server's client sessions as Postgres had them: the connection settings and the groups, the most sessions first.
/// </summary>
/// <param name="CanReadAllStats">Whether the role reads every session's state (<c>pg_read_all_stats</c>).</param>
/// <param name="MaxConnections"><c>max_connections</c>.</param>
/// <param name="SuperuserReserved"><c>superuser_reserved_connections</c>.</param>
/// <param name="Reserved"><c>reserved_connections</c> (PostgreSQL 16+), zero before.</param>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
/// <param name="ServerConnections">Client sessions on the whole server, the studio's own reading session included - C03's number.</param>
/// <param name="GroupCount">How many groups there are, past the read's cap.</param>
/// <param name="Groups">The groups, at most <c>HealthConnectionQueries.MaxHolderGroups</c>.</param>
internal sealed record SessionServerHolders(
    bool CanReadAllStats,
    int MaxConnections,
    int SuperuserReserved,
    int Reserved,
    int ServerVersion,
    long ServerConnections,
    long GroupCount,
    IReadOnlyList<SessionHolderGroup> Groups)
{
    /// <summary>
    /// The server's connection budget, through the one formula C03 judges with (<see cref="ServerConnectionLimits.Available" />):
    /// <c>max_connections</c> less both reservations. Its per-database and per-role members are not read here.
    /// </summary>
    public ServerConnectionLimits Limits => new(ServerConnections, 0, MaxConnections, SuperuserReserved, Reserved, ServerVersion);

    /// <summary>Whether the groups are the most sessions first of more groups than the read lists.</summary>
    public bool Truncated => GroupCount > Groups.Count;

    /// <summary>Whether every session's state was read: the role reads every state, or nobody it may not read into was connected.</summary>
    public bool AllSessionsVisible => CanReadAllStats || Groups.All(static x => x.StateUnknown == 0);

    /// <summary>Nothing read: no group and a budget of nothing, which a reader that did not read it hands back.</summary>
    public static SessionServerHolders None { get; } = new(true, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// The sessions of one database - or of every database a visitor may not be told the name of, together - by state: numbers
/// that name no application, role or address.
/// </summary>
/// <param name="Database">The database's name, or <see langword="null" /> for the databases together.</param>
/// <param name="ThisDatabase">Whether it is the database the studio's connection is on.</param>
/// <param name="Total">Every session.</param>
/// <param name="Active">Running a statement.</param>
/// <param name="Idle">Idle, outside a transaction.</param>
/// <param name="IdleInTransaction">Idle in a transaction, aborted ones included.</param>
/// <param name="StateUnknown">Whose state the role may not read.</param>
/// <param name="OldestIdle">The longest any has been idle, or <see langword="null" />.</param>
/// <param name="OldestIdleInTransaction">The longest any has been idle inside its transaction, or <see langword="null" />.</param>
internal sealed record SessionHolderCounts(
    string? Database,
    bool ThisDatabase,
    long Total,
    long Active,
    long Idle,
    long IdleInTransaction,
    long StateUnknown,
    TimeSpan? OldestIdle,
    TimeSpan? OldestIdleInTransaction)
{
    /// <summary>The counts of <paramref name="groups" />, summed - the oldest of each age taken.</summary>
    /// <param name="database">The name to give them, or <see langword="null" /> for the databases together.</param>
    /// <param name="thisDatabase">Whether they are the studio's connection's own database.</param>
    /// <param name="groups">The groups; none gives zeros.</param>
    public static SessionHolderCounts Of(string? database, bool thisDatabase, IEnumerable<SessionHolderGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        long total = 0;
        long active = 0;
        long idle = 0;
        long idleInTransaction = 0;
        long unknown = 0;
        TimeSpan? oldestIdle = null;
        TimeSpan? oldestIdleInTransaction = null;

        foreach (SessionHolderGroup group in groups)
        {
            total += group.Total;
            active += group.Active;
            idle += group.Idle;
            idleInTransaction += group.IdleInTransaction;
            unknown += group.StateUnknown;
            oldestIdle = Longer(oldestIdle, group.OldestIdle);
            oldestIdleInTransaction = Longer(oldestIdleInTransaction, group.OldestIdleInTransaction);
        }

        return new SessionHolderCounts(database, thisDatabase, total, active, idle, idleInTransaction, unknown, oldestIdle, oldestIdleInTransaction);
    }

    private static TimeSpan? Longer(TimeSpan? left, TimeSpan? right) =>
        left is null ? right : right is null ? left : left >= right ? left : right;
}

/// <summary>
/// What Database › Sessions › Holders shows a visitor the source policy passes for the database as a whole: the server's
/// connections per database, with no application, role or address named.
/// </summary>
/// <param name="ServerConnections">Client sessions on the whole server (C03's number).</param>
/// <param name="Available">What an ordinary role may open (<see cref="ServerConnectionLimits.Available" />).</param>
/// <param name="AvailableText">"97 (max_connections 100 - 3 superuser_reserved_connections - 0 reserved_connections)".</param>
/// <param name="AllSessionsVisible">Whether every session's state was read.</param>
/// <param name="Databases">
/// The databases this visitor may be told the name of - the studio's connection's own, and those a registered source's policy
/// admits (<c>SessionHolderNames</c>) - the most sessions first.
/// </param>
/// <param name="Others">Every other database's sessions together, or <see langword="null" /> when there are none.</param>
/// <param name="GroupCount">How many groups of sessions there are, past the read's cap.</param>
/// <param name="ListedGroups">How many of them the read listed, which the counts above are made from.</param>
/// <param name="Truncated">Whether the read listed fewer groups than there are, so the counts above are lower bounds.</param>
internal sealed record SessionHoldersSummary(
    long ServerConnections,
    int Available,
    string AvailableText,
    bool AllSessionsVisible,
    IReadOnlyList<SessionHolderCounts> Databases,
    SessionHolderCounts? Others,
    long GroupCount,
    long ListedGroups,
    bool Truncated)
{
    /// <summary>The share of what an ordinary role may open that is in use.</summary>
    public double Share => (double) ServerConnections / Math.Max(1, Available);
}

/// <summary>
/// What Database › Sessions › Holders shows past the <c>ViewDatabaseActivity</c> gate: every group, by database, application,
/// role and client address, and every database by name.
/// </summary>
/// <param name="Groups">The groups, the most sessions first.</param>
/// <param name="Databases">Every database with sessions, by name, the most first.</param>
/// <param name="GroupCount">How many groups there are, past the read's cap.</param>
internal sealed record SessionHoldersDetails(
    IReadOnlyList<SessionHolderGroup> Groups,
    IReadOnlyList<SessionHolderCounts> Databases,
    long GroupCount)
{
    /// <summary>No groups - a visitor the gate refused, or a reader that did not read them.</summary>
    public static SessionHoldersDetails None { get; } = new([], [], 0);

    /// <summary>Whether the read listed fewer groups than there are.</summary>
    public bool Truncated => GroupCount > Groups.Count;
}

/// <summary>Folds the server's groups into what each audience is shown.</summary>
internal static class SessionHolders
{
    /// <summary>
    /// The per-database counts, by name, the most sessions first, ties by name: every database in <paramref name="groups" />.
    /// </summary>
    /// <param name="groups">The groups.</param>
    public static IReadOnlyList<SessionHolderCounts> Databases(IEnumerable<SessionHolderGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        return
        [
            .. groups
                .GroupBy(static x => x.Database, StringComparer.Ordinal)
                .Select(static x => SessionHolderCounts.Of(x.Key, x.Any(static y => y.ThisDatabase), x))
                .OrderByDescending(static x => x.Total)
                .ThenBy(static x => x.Database, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// What a visitor short of <c>ViewDatabaseActivity</c> is shown: the databases whose names they may be told - the studio's
    /// own connection's database, and <paramref name="admitted" /> - each by name, and every other database's sessions together
    /// with no name.
    /// </summary>
    /// <param name="holders">What was read.</param>
    /// <param name="admitted">The names of the other databases the source policy admits this visitor to.</param>
    public static SessionHoldersSummary Summarize(SessionServerHolders holders, IReadOnlySet<string> admitted)
    {
        ArgumentNullException.ThrowIfNull(holders);
        ArgumentNullException.ThrowIfNull(admitted);

        IReadOnlyList<SessionHolderCounts> all = Databases(holders.Groups);
        List<SessionHolderCounts> named = [.. all.Where(x => x.ThisDatabase || admitted.Contains(x.Database!))];
        SessionHolderCounts[] rest = [.. all.Where(x => !x.ThisDatabase && !admitted.Contains(x.Database!))];

        ServerConnectionLimits limits = holders.Limits;

        return new SessionHoldersSummary(
            holders.ServerConnections,
            limits.Available,
            limits.AvailableText,
            holders.AllSessionsVisible,
            named,
            rest.Length == 0
                ? null
                : new SessionHolderCounts(
                    null,
                    false,
                    rest.Sum(static x => x.Total),
                    rest.Sum(static x => x.Active),
                    rest.Sum(static x => x.Idle),
                    rest.Sum(static x => x.IdleInTransaction),
                    rest.Sum(static x => x.StateUnknown),
                    rest.Max(static x => x.OldestIdle),
                    rest.Max(static x => x.OldestIdleInTransaction)),
            holders.GroupCount,
            holders.Groups.Count,
            holders.Truncated);
    }

    /// <summary>What a visitor past the gate is shown: every group, and every database by name.</summary>
    /// <param name="holders">What was read.</param>
    public static SessionHoldersDetails Detail(SessionServerHolders holders)
    {
        ArgumentNullException.ThrowIfNull(holders);

        return new SessionHoldersDetails(holders.Groups, Databases(holders.Groups), holders.GroupCount);
    }
}
