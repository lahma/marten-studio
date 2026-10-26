using System.Globalization;

using JasperFx.Events.Daemon;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

using Npgsql;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What K08 reads: the daemon's leadership configuration, and the three server timeouts as the store's role sees them.</summary>
/// <param name="AsyncMode">The daemon mode this process was configured with.</param>
/// <param name="TransactionScopedLock"><c>Events.UseAdvisoryLockTransaction</c>.</param>
/// <param name="MonitoredLock"><c>Events.UseMonitoredAdvisoryLock</c>.</param>
/// <param name="LeadershipPolling"><c>LeadershipPollingTime</c>.</param>
/// <param name="KeepaliveMonitor">
/// Whether the lock monitor is DistributedLock's keepalive, which leaves the leader idle between statements (from Weasel 9.34,
/// Marten 9.39.1, with a connection string without Npgsql's <c>Keepalive</c> or <c>Multiplexing</c>), rather than the
/// <c>pg_sleep</c> monitor, which keeps it active (<see cref="LeaderLockMonitor" />).
/// </param>
/// <param name="IdleInTransaction"><c>idle_in_transaction_session_timeout</c>, or <see langword="null" /> when not read.</param>
/// <param name="TransactionTimeout"><c>transaction_timeout</c> (17+), or <see langword="null" /> where the server has none.</param>
/// <param name="IdleSession"><c>idle_session_timeout</c>, or <see langword="null" /> when not read.</param>
/// <param name="LockSetting">How <c>UseAdvisoryLockTransaction</c> reads against the default of the Marten this host runs.</param>
/// <param name="MonitorBasis">What said which monitor runs (<see cref="LeaderLockMonitor.Basis" />), for the evidence.</param>
internal sealed record DaemonTimeoutsFacts(
    DaemonMode AsyncMode,
    bool TransactionScopedLock,
    bool MonitoredLock,
    TimeSpan LeadershipPolling,
    bool KeepaliveMonitor,
    ServerSettingValue? IdleInTransaction,
    ServerSettingValue? TransactionTimeout,
    ServerSettingValue? IdleSession,
    string LockSetting,
    string MonitorBasis = "")
{
    /// <summary>
    /// How long the leader sits idle in its leadership transaction, or <see langword="null" /> for "for as long as it leads" -
    /// with its monitor off. Measured (<c>HotColdLeaderSessionLiveTests</c>, <c>DaemonTimeoutsLiveTests</c>): DistributedLock's
    /// keepalive about once a minute; 9.39's <c>pg_sleep</c> monitor keeps it active once its first leadership poll has passed,
    /// about a second after it.
    /// </summary>
    public TimeSpan? LeaderIdleInTransaction => !MonitoredLock
        ? null
        : KeepaliveMonitor ? DaemonTimeoutsCheck.KeepaliveCadence : LeadershipPolling + DaemonTimeoutsCheck.FirstPollSettle;

    /// <summary>How long a session-scoped leader's lock connection sits idle, or <see langword="null" /> for "for as long as it leads".</summary>
    public TimeSpan? LeaderIdleSession => MonitoredLock ? DaemonTimeoutsCheck.KeepaliveCadence : null;
}

/// <summary>
/// K08: a server timeout the store's role is subject to ends the HotCold daemon leader's session or transaction, and with it the
/// leadership - <c>idle_in_transaction_session_timeout</c> or <c>transaction_timeout</c> under a transaction-scoped lock,
/// <c>idle_session_timeout</c> under a session-scoped one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only for a process that hosts the daemon <c>HotCold</c></b>: its leadership is one advisory lock held on one connection,
/// and a timeout that ends that connection, or the transaction holding the lock, hands the leadership to whichever node asks
/// next - and every projection the leader ran restarts there. <c>Solo</c> takes no lock; an externally managed daemon's
/// leadership is not Marten's.
/// </para>
/// <para>
/// <b>The leader's idle stretch, from what the daemon is configured to do and the DistributedLock it runs</b> - measured, never
/// assumed (<c>HotColdLeaderSessionLiveTests</c>, <c>DaemonTimeoutsLiveTests</c>, at Marten 9.39.0, 9.45.0 and the latest
/// tested). With a transaction-scoped lock and its monitor on: from Weasel 9.34 (Marten 9.39.1), whose Patched
/// DistributedLock.Postgres 1.3.3 this reads off the loaded assembly (<see cref="LeaderLockMonitor" />), DistributedLock's
/// keepalive runs in the leadership transaction about once a minute, the session idle in between; with the upstream 1.3.0
/// (Weasel 9.33, Marten 9.39.0) - or where the connection string sets Npgsql's
/// <c>Keepalive</c> or <c>Multiplexing</c> - the monitor's <c>pg_sleep</c> keeps it active, idle only until its first
/// leadership poll has passed. With the monitor off it is idle for as long as it leads, and any timeout ends it. A
/// session-scoped lock's connection sits idle up to a minute at a time between keepalives (Marten's <c>async-daemon.md</c>).
/// A timeout is judged against <see cref="Margin" /> times that stretch - C04's margin for a keepalive a loaded host runs late
/// (<see cref="ConnectionThresholds.DaemonLeaderIdleBound" />).
/// </para>
/// <para>
/// <b>As the store's role sees them</b>, from the Server page's settings read: on a connection of the store's own data source,
/// so a value set for its role or its database counts, and <c>idle_in_transaction_session_timeout</c> - which the studio's own
/// read-only transaction sets for itself - read as a new session starts with it (<c>reset_val</c>).
/// </para>
/// </remarks>
[HealthCheck("K08", HealthArea.Configuration, HealthCostTier.Catalog, "Server timeouts end the daemon's leadership",
    Variants = ["idle-in-transaction", "transaction-timeout", "idle-session"])]
internal sealed class DaemonTimeoutsCheck : HealthCheck<DaemonTimeoutsFacts>
{
    /// <summary>
    /// DistributedLock's keepalive, and the session-scoped monitor's idle wait: about a minute between statements - 61 s
    /// measured from Weasel 9.34 (Marten 9.39.1; CL3 at 9.50.2, W5-S2 at 9.45.0 and 9.54.1), and "up to a minute at a time"
    /// in Marten's <c>async-daemon.md</c>.
    /// </summary>
    public static readonly TimeSpan KeepaliveCadence = TimeSpan.FromSeconds(61);

    /// <summary>How long after its first leadership poll 9.39's monitor starts its first <c>pg_sleep</c>: about 0.9 s, measured.</summary>
    public static readonly TimeSpan FirstPollSettle = TimeSpan.FromSeconds(1);

    /// <summary>How many of the leader's idle stretches a timeout must exceed not to be raised: room for a late keepalive.</summary>
    public const int Margin = 3;

    /// <inheritdoc />
    public override async Task<DaemonTimeoutsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DaemonMode mode = context.StoreOptions.Events.Daemon.AsyncMode;
        bool transactionScoped = context.StoreOptions.Events.UseAdvisoryLockTransaction;
        bool monitored = context.StoreOptions.Events.UseMonitoredAdvisoryLock;
        DaemonLockPooling pooling = DaemonLockPooling.Read(context);

        if (mode != DaemonMode.HotCold)
        {
            return new DaemonTimeoutsFacts(mode, transactionScoped, monitored, TimeSpan.Zero, false, null, null, null, string.Empty);
        }

        ServerSettingsRead settings = await ServerSettingsRead.SharedAsync(context).ConfigureAwait(false);

        LeaderLockMonitor monitor = LeaderLockMonitor.Current;
        bool keepalive = monitor.Keepalive && !SleepsInsteadOfKeepalive(context);

        return new DaemonTimeoutsFacts(
            mode,
            transactionScoped,
            monitored,
            ProjectionDataService.LeadershipPollingTimeOf(context.Store),
            keepalive,
            settings.Find("idle_in_transaction_session_timeout"),
            settings.Find("transaction_timeout"),
            settings.Find("idle_session_timeout"),
            pooling.DescribeLockSetting(MartenRuntime.Current),
            monitor.Basis);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DaemonTimeoutsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.AsyncMode != DaemonMode.HotCold)
        {
            return HealthVerdict.Quiet("This process runs the async daemon " + facts.AsyncMode + ", which holds no HotCold leadership a timeout could end.");
        }

        List<HealthFinding> findings = [];

        if (facts.TransactionScopedLock)
        {
            if (Positive(facts.IdleInTransaction) is { } idle && (facts.LeaderIdleInTransaction is not { } stretch || idle < stretch * Margin))
            {
                findings.Add(Finding(
                    facts,
                    "idle-in-transaction",
                    facts.IdleInTransaction!,
                    "The server ends idle transactions sooner than the daemon's leader may sit idle in its leadership transaction",
                    "With Events.UseAdvisoryLockTransaction the HotCold leader holds its lock in a transaction it keeps open for as long "
                    + "as it leads, and " + IdleStretch(facts) + " idle_in_transaction_session_timeout = " + Format(idle)
                    + " ends that session when it passes, the lock with it: another node takes the lead, and every projection the "
                    + "leader ran restarts there - over and over, as each new leader is ended in turn."));
            }

            if (Positive(facts.TransactionTimeout) is { } whole)
            {
                findings.Add(Finding(
                    facts,
                    "transaction-timeout",
                    facts.TransactionTimeout!,
                    "The server ends every transaction after " + Format(whole) + ", the daemon's leadership transaction included",
                    "With Events.UseAdvisoryLockTransaction the HotCold leader holds its lock in a transaction it keeps open for as long "
                    + "as it leads, and transaction_timeout = " + Format(whole) + " ends any transaction that has lasted that long, "
                    + "idle or not: the leadership changes hands at least that often, and every projection the leader ran restarts on "
                    + "the next node."));
            }
        }
        else if (Positive(facts.IdleSession) is { } session && (facts.LeaderIdleSession is not { } stretch || session < stretch * Margin))
        {
            findings.Add(Finding(
                facts,
                "idle-session",
                facts.IdleSession!,
                "The server ends idle sessions sooner than the daemon's leader may sit idle on its lock connection",
                "With a session-scoped leadership lock the HotCold leader holds its lock on a connection of its own, which "
                + (facts.MonitoredLock
                    ? "sits idle up to a minute at a time between its monitor's statements,"
                    : "sits idle for as long as it leads, with its lock monitor off,")
                + " and idle_session_timeout = " + Format(session) + " ends that session when it passes, the lock with it: another "
                + "node takes the lead, and every projection the leader ran restarts there."));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(facts.TransactionScopedLock
            ? "The leadership lock is transaction-scoped; idle_in_transaction_session_timeout is " + Describe(facts.IdleInTransaction)
              + " and transaction_timeout " + Describe(facts.TransactionTimeout) + ", neither of which ends the leader's transaction."
            : "The leadership lock is session-scoped; idle_session_timeout is " + Describe(facts.IdleSession) + ", which does not end the leader's session.");
    }

    private static HealthFinding Finding(DaemonTimeoutsFacts facts, string variant, ServerSettingValue setting, string title, string why) => new()
    {
        Code = "K08",
        Severity = HealthSeverity.Warning,
        Variant = variant,
        Subject = setting.Name,
        Title = title,
        WhyItMatters = why
            + " Whoever owns the store's role raises or unsets the timeout for it - ALTER ROLE … SET " + setting.Name + " = 0 - so the "
            + "application's other sessions keep the server's value; or the lock changes scope, below.",
        Evidence =
        [
            new("Setting", setting.Name + " = " + setting.Value + (setting.Unit is { } unit ? " " + unit : string.Empty)),
            new("Set by", DangerousSettingsCheck.SetBy(setting.Source)),
            new("Daemon", "HotCold, LeadershipPollingTime " + SessionText.Duration(facts.LeadershipPolling)
                + "; UseAdvisoryLockTransaction " + facts.LockSetting
                + "; UseMonitoredAdvisoryLock " + (facts.MonitoredLock ? "true" : "false")),
            new("Leader idle", IdleStretch(facts).TrimEnd(',', ' ')),
            new("Lock monitor", facts.MonitorBasis.Length > 0 ? facts.MonitorBasis : "not read"),
        ],
        Caveat =
            "Read in pg_settings on a connection from the store's own data source, as the store's role sees it - "
            + "idle_in_transaction_session_timeout as a new session starts with it, because the studio's own read sets its own. "
            + "Judged against " + Margin.ToString(CultureInfo.InvariantCulture) + " times the leader's idle stretch, room for a "
            + "keepalive a loaded host runs late.",
        Remedy = new HealthRemedy(
            variant == "idle-session"
                ? "// In AddMarten(...): hold the leadership lock in a transaction, which idle_session_timeout does not end.\nopts.Events.UseAdvisoryLockTransaction = true;"
                : "// In AddMarten(...): hold the leadership lock on its own session, which no transaction timeout ends.\nopts.Events.UseAdvisoryLockTransaction = false;",
            variant == "idle-session"
                ? "A transaction-scoped lock is then subject to idle_in_transaction_session_timeout and transaction_timeout instead, "
                  + "and holds a snapshot about a minute old at a time (T14)."
                : "A session-scoped lock is Marten's default from 9.41, and is then subject to idle_session_timeout instead - but behind a "
                  + "transaction-mode pooler it cannot work at all (K04), so there the timeout is what changes."),
    };

    /// <summary>
    /// Whether the data source's connection string sets Npgsql's <c>Keepalive</c> or <c>Multiplexing</c>, under which
    /// Patched.DistributedLock.Postgres 1.3.3 falls back to 9.39's <c>SAVEPOINT</c> and <c>pg_sleep</c> (decompiled, CL3).
    /// </summary>
    private static bool SleepsInsteadOfKeepalive(HealthCheckContext context)
    {
        if (context.Target.DataSource is not { } dataSource)
        {
            return false;
        }

        try
        {
            NpgsqlConnectionStringBuilder builder = new(dataSource.ConnectionString);
            return builder.KeepAlive > 0 || builder.Multiplexing;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string IdleStretch(DaemonTimeoutsFacts facts) =>
        facts.TransactionScopedLock
            ? facts.LeaderIdleInTransaction is { } stretch
                ? (facts.KeepaliveMonitor
                    ? "it sits idle there about " + SessionText.Duration(stretch) + " at a time, between DistributedLock's keepalives,"
                    : "it sits idle there about " + SessionText.Duration(stretch) + " after taking the lead, until its lock monitor's pg_sleep begins,")
                : "with its lock monitor off it sits idle there for as long as it leads,"
            : facts.LeaderIdleSession is { } idle
                ? "its lock connection sits idle about " + SessionText.Duration(idle) + " at a time,"
                : "its lock connection sits idle for as long as it leads,";

    private static TimeSpan? Positive(ServerSettingValue? setting) => setting?.Duration is { } duration && duration > TimeSpan.Zero ? duration : null;

    private static string Format(TimeSpan value) => SessionText.Duration(value);

    private static string Describe(ServerSettingValue? setting) => setting switch
    {
        null => "not shown or not on this server",
        _ when Positive(setting) is { } value => Format(value),
        _ => "off (0)",
    };
}
