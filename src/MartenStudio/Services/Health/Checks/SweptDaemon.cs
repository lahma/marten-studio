using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// The daemon a sweep's checks look at, found once per sweep: D02 to D05 (through <c>daemon.in-process</c>), C09
/// and D08 all ask, and every one of them asks about the same database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why once.</b> With a projection coordinator hosted in this process, <see cref="DaemonAccessor.ForScopeAsync" />
/// asks Marten's coordinator for its daemons (<c>AllDaemonsAsync()</c>), and Marten answers that by enumerating
/// the store (<c>Storage.AllDatabases()</c>) - on a master-table or sharded tenancy a read of its master table, as
/// the coordinator's own leadership loop makes every cycle. Asked by each of the three places that ask, that was
/// three enumerations a sweep; asked through the sweep's memo it is one (H8-fix2).
/// </para>
/// <para>
/// The lookup reads nothing through <see cref="HealthCheckContext.Sql" />, so it adds no statement to anybody's
/// "Show SQL"; it is handed the sweep's budget rather than the first asking check's, like every memoised read.
/// </para>
/// </remarks>
internal static class SweptDaemon
{
    /// <summary>The memo key: one daemon lookup per sweep.</summary>
    internal const string Key = "daemon.hosting";

    /// <summary>The daemon hosting for the swept database, looked up at most once per sweep.</summary>
    /// <param name="context">The sweep's context, whose memo holds the answer.</param>
    /// <param name="accessor">The sweep scope's daemon accessor.</param>
    public static Task<DaemonHosting> HostingAsync(HealthCheckContext context, DaemonAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(accessor);

        return context.Shared(Key, token => accessor.ForScopeAsync(context.Scope, token).AsTask());
    }
}
