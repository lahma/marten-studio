using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C13 and C14 read: the budget C03 judges, and who holds the server's connections.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Limits">The server's connection budget - C03's own read, so both judge one number.</param>
/// <param name="Holders">The whole server's client sessions by database, application, role and client address.</param>
internal sealed record ConnectionHolderFacts(DateTimeOffset ReadAt, ServerConnectionLimits Limits, SessionServerHolders Holders)
{
    /// <summary>
    /// Whether every client session's state was read: the role reads every state, or nobody it may not read into was connected.
    /// </summary>
    public bool AllSessionsVisible => Holders.AllSessionsVisible;

    /// <summary>How many sessions' state the role could not read, among the groups listed.</summary>
    public long StateUnknown => Holders.Groups.Sum(static x => x.StateUnknown);

    /// <summary>The sentence that says what the role cannot see, or <see langword="null" /> when it sees every state.</summary>
    public string? VisibilityNote => AllSessionsVisible
        ? null
        : "The studio's Postgres role is not a member of pg_read_all_stats, so the state of "
          + SessionText.Count(StateUnknown, "session") + " of other roles is not readable: they are counted, and counted as neither idle nor "
          + "active - this says what it can see, not that there is nothing else.";

    /// <summary>The sentence that says the read listed fewer groups than there are, or <see langword="null" />.</summary>
    public string? TruncationNote => Holders.Truncated
        ? "Only the " + SessionText.Number(Holders.Groups.Count) + " largest of " + SessionText.Number(Holders.GroupCount)
          + " groups of sessions were read, so these counts can be lower than the server's."
        : null;
}

/// <summary>
/// The sweep's one read of the whole server's connections by holder (issue #20): C13 and C14 take it from here.
/// </summary>
/// <remarks>
/// <b>Once per sweep, with the lock table's and the settings' company.</b> The read is
/// <see cref="HealthConnectionQueries.ServerHolders" />, through the sweep's memo (<see cref="HealthCheckContext.Shared{T}" />) under
/// <see cref="Key" />, so whichever check asks first runs it and the other gets the same answer; its statement is in the "Show SQL"
/// of both. It is the one read of a sweep that groups other databases' sessions, so only an <c>Activity</c> check uses it
/// (D29). The budget the two judge it against is <see cref="ServerConnectionLimits.SharedAsync" />'s, C03's own read.
/// </remarks>
internal static class ServerConnectionHolders
{
    /// <summary>The memo key of the sweep's read.</summary>
    public const string Key = "connections.holders";

    /// <summary>The sweep's read, made once whichever check asks first.</summary>
    public static Task<SessionServerHolders> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    /// <summary>The facts C13 and C14 judge: the budget C03 reads and the holders.</summary>
    public static async Task<ConnectionHolderFacts> FactsAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerConnectionLimits limits = await ServerConnectionLimits.SharedAsync(context).ConfigureAwait(false);
        SessionServerHolders holders = await SharedAsync(context).ConfigureAwait(false);

        return new ConnectionHolderFacts(context.Now, limits, holders);
    }

    private static async Task<SessionServerHolders> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionActivityReader.HolderRow> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.ServerHolders(), SessionActivityReader.MapHolder, cancellationToken)
            .ConfigureAwait(false);

        return SessionActivityReader.Holders(rows) ?? SessionServerHolders.None;
    }

    /// <summary>The label an application goes by in a finding: its name in quotes, or "no application name".</summary>
    /// <param name="application">The <c>application_name</c>; empty for a session that set none.</param>
    public static string ApplicationLabel(string application) => application.Length == 0 ? "(no application name)" : "'" + application + "'";

    /// <summary><c>role@address</c> - a Unix socket as "local socket", an address the role may not read as "not readable".</summary>
    /// <param name="group">The group.</param>
    public static string Who(SessionHolderGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return (group.Role.Length == 0 ? "(unknown role)" : group.Role) + "@" + group.ClientText;
    }

    /// <summary><c>role@address/database (count)</c> - the form C14 names its largest holders in.</summary>
    /// <param name="group">The group.</param>
    /// <param name="count">The count to print.</param>
    public static string Holder(SessionHolderGroup group, long count)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Who(group) + "/" + (group.Database.Length == 0 ? "(no database)" : group.Database) + " (" + count.ToString("N0", CultureInfo.InvariantCulture) + ")";
    }
}
