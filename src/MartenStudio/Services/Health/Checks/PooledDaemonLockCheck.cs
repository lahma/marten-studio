using System.Globalization;

using JasperFx.Events.Daemon;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// What K04 and C10 read of the store's daemon configuration: the mode, the kind of leadership lock, and where the
/// database's data source connects - configuration only, no connection.
/// </summary>
/// <param name="AsyncMode">The daemon mode this process was configured with (<c>Projections.AsyncMode</c>).</param>
/// <param name="TransactionScopedLock"><c>Events.UseAdvisoryLockTransaction</c>.</param>
/// <param name="Endpoints">Where the database's data source connects, or <see langword="null" /> when it cannot be read.</param>
internal sealed record DaemonLockPooling(DaemonMode AsyncMode, bool TransactionScopedLock, DataSourceEndpoints? Endpoints)
{
    /// <summary>The first Marten on which the HotCold leadership lock is session-scoped by default (#5526).</summary>
    public static readonly Version SessionLockDefaultSince = new(9, 41, 0);

    /// <summary>
    /// Whether this process runs a HotCold daemon whose leadership lock is session-scoped - the one configuration a
    /// transaction-mode pooler breaks.
    /// </summary>
    public bool SessionLockUnderHotCold => AsyncMode == DaemonMode.HotCold && !TransactionScopedLock;

    /// <summary>Reads it off the context's store and database.</summary>
    public static DaemonLockPooling Read(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new DaemonLockPooling(
            context.StoreOptions.Events.Daemon.AsyncMode,
            context.StoreOptions.Events.UseAdvisoryLockTransaction,
            DataSourceEndpoints.Of(context.Database));
    }

    /// <summary>
    /// Why neither K04 nor C10 has anything to say about this configuration, or <see langword="null" /> when it is the
    /// one they are about.
    /// </summary>
    public string? NotApplicable => AsyncMode switch
    {
        DaemonMode.Solo =>
            "This process runs the async daemon Solo, which takes no advisory lock to lead, so a pooler cannot hand its leadership to anybody.",
        DaemonMode.ExternallyManaged =>
            "An external system distributes this store's projections (DaemonMode.ExternallyManaged), and its leadership is not Marten's advisory lock.",
        DaemonMode.Disabled =>
            "This process runs no async daemon (DaemonMode.Disabled), so it takes no leadership lock here; a node that runs one is judged by its own studio.",
        DaemonMode.HotCold when TransactionScopedLock =>
            "The HotCold leadership lock is transaction-scoped (Events.UseAdvisoryLockTransaction), which a transaction-mode pooler keeps on one server connection for as long as the leader holds it.",
        DaemonMode.HotCold => null,
        _ => "This process's daemon mode, " + AsyncMode + ", takes no HotCold leadership lock.",
    };

    /// <summary>
    /// <c>UseAdvisoryLockTransaction</c>'s value, and whether it is Marten's default for the Marten this host runs: false
    /// is the default from 9.41, and on 9.39 and 9.40 true is.
    /// </summary>
    public string DescribeLockSetting(MartenRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        Version? marten = runtime.VersionOf(RuntimePackage.Marten);
        bool defaultValue = marten is not null && marten < SessionLockDefaultSince;
        string host = runtime.Describe(RuntimePackage.Marten);

        return TransactionScopedLock == defaultValue
            ? Bool(TransactionScopedLock) + " - the default on " + host
            : Bool(TransactionScopedLock) + " - set by the host; the default on " + host + " is " + Bool(defaultValue);

        static string Bool(bool value) => value ? "true" : "false";
    }

    /// <summary>
    /// What, in the data source's hosts and ports, looks like a transaction-mode pooler - each sign in words, with the
    /// provider document that says so.
    /// </summary>
    public IReadOnlyList<string> PoolerSigns() => Endpoints is null ? [] : SignsOf(Endpoints.Endpoints);

    /// <summary>The pooler signs of these endpoints. Pure: the unit tests hold each to it.</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A host with <c>-pooler.</c> in it: Neon's pooled endpoint is the direct one with <c>-pooler</c> after the
    /// endpoint id, and runs PgBouncer with <c>pool_mode=transaction</c> (<i>Connection pooling</i>, Neon docs).</item>
    /// <item>Port <c>6543</c>: "Port 6543 reaches PgBouncer for the dedicated pooler and Supavisor for shared transaction
    /// mode" (<i>Connect to your database</i>, Supabase docs). Supabase's shared pooler on 5432 is session mode, and is not
    /// a sign.</item>
    /// <item>Port <c>6432</c>: PgBouncer's default <c>listen_port</c> (pgbouncer.org, <i>Configuration</i>), and the port of
    /// Azure's built-in PgBouncer, whose <c>pgbouncer.pool_mode</c> defaults to <c>transaction</c> (<i>PgBouncer in Azure
    /// Database for PostgreSQL</i>, Microsoft Learn). A PgBouncer of one's own pools by session unless told otherwise, which
    /// is why this is "looks like" and C10 is the proof.</item>
    /// </list>
    /// </remarks>
    internal static IReadOnlyList<string> SignsOf(IEnumerable<(string Host, int Port)> endpoints)
    {
        List<string> signs = [];

        foreach ((string host, int port) in endpoints)
        {
            string name = host.ToLowerInvariant();
            string where = host + ":" + port.ToString(CultureInfo.InvariantCulture);

            if (name.Contains("-pooler.", StringComparison.Ordinal))
            {
                signs.Add(where + " - the -pooler suffix of Neon's pooled endpoint, PgBouncer in transaction mode");
            }
            else if (port == 6543)
            {
                signs.Add(where + " - port 6543, where Supabase's poolers take transaction-mode connections");
            }
            else if (port == 6432 && name.EndsWith(".postgres.database.azure.com", StringComparison.Ordinal))
            {
                signs.Add(where + " - port 6432 of an Azure server, its built-in PgBouncer, which pools by transaction by default");
            }
            else if (port == 6432)
            {
                signs.Add(where + " - port 6432, PgBouncer's default port");
            }
        }

        return signs;
    }

    /// <summary>The remedy K04 and C10 share: a lock the pooler cannot hand away, and a direct connection for the daemon.</summary>
    public static HealthRemedy Remedy { get; } = new(
        """
        // In AddMarten(...): a leadership lock a transaction-mode pooler cannot hand to another client.
        opts.Events.UseAdvisoryLockTransaction = true;

        // ...and, on the node that runs the daemon, a direct connection to Postgres rather than the pooler's:
        // opts.Connection(directConnectionString);
        """,
        "With the transaction-scoped lock the leader holds one transaction open for as long as it leads, which a transaction-mode "
        + "pooler keeps on one server connection - at the cost of that connection, and of an open transaction that holds back "
        + "vacuum while it lasts (C04, T14). A direct connection for the daemon's node avoids the pooler altogether: Neon's and "
        + "Supabase's documentation both send anything session-scoped to the direct connection string.");
}

/// <summary>
/// K04: the HotCold daemon's leadership lock is session-scoped, and this database's data source looks like it goes
/// through a transaction-mode pooler.
/// </summary>
/// <remarks>
/// <para>
/// HotCold leadership is a Postgres advisory lock. A session-scoped one (<c>pg_try_advisory_lock</c>) belongs to the server
/// process that took it - and a transaction-mode pooler (PgBouncer with <c>pool_mode = transaction</c>, Neon's pooler,
/// Supabase's Supavisor on 6543) hands that server process to another client as soon as the transaction that took the lock
/// ends. The lock then belongs to whoever is using that server connection next; the leader's own release lands on a
/// different one and fails; and lock monitoring, which watches its own connection, can report a lock that is somebody
/// else's. PgBouncer's own feature map says session-level advisory locks "never" work in transaction pooling.
/// </para>
/// <para>
/// <b>At the two ends of what the studio runs on.</b> Marten 9.41 made the session-scoped lock the default
/// (<c>UseAdvisoryLockTransaction = false</c>, #5526; <i>migration-guide.md</i> at V9.50.2, "Key Changes in 9.41.0": "The
/// one deployment that needs action is a daemon running behind PgBouncer in transaction pooling mode"). On 9.39 and 9.40
/// the lock is transaction-scoped by default, so this fires there only for a host that turned it off; from 9.41 every
/// HotCold host behind such a pooler that has not turned it on is the case.
/// </para>
/// <para>
/// <b>"Looks like."</b> Tier 0, no I/O: the host and port in the data source's connection string are all there is to go
/// on, so the finding is a Warning that says it is a guess. C10 tests the connection itself and is the proof. Never raised
/// for Solo (no lock), <c>ExternallyManaged</c> (not Marten's lock) or a process that runs no daemon.
/// </para>
/// <para>
/// <see cref="HealthGate.None" />, like K01: the finding, the daemon mode and the lock setting are on the Configuration
/// page's daemon card, which every visitor of the scope sees. The host and port that look pooled are evidence at
/// <see cref="HealthGate.Database" />, because the Configuration page lists a database's server and port only to a visitor
/// the store policy passes for the database as a whole.
/// </para>
/// </remarks>
[HealthCheck("K04", HealthArea.Configuration, HealthCostTier.InProcess, "HotCold daemon behind a transaction pooler", Gate = HealthGate.None)]
internal sealed class PooledDaemonLockCheck : HealthCheck<DaemonLockPooling>
{
    /// <inheritdoc />
    public override Task<DaemonLockPooling> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        Task.FromResult(DaemonLockPooling.Read(context));

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DaemonLockPooling facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.NotApplicable is { } reason)
        {
            return HealthVerdict.Quiet(reason);
        }

        if (facts.Endpoints is null)
        {
            return HealthVerdict.Quiet("This database's data source is not one the studio can read a host and port from, so it cannot tell whether a pooler is in front of it; C10 tests the connection itself.");
        }

        IReadOnlyList<string> signs = facts.PoolerSigns();

        if (signs.Count == 0)
        {
            // No host or port in the note: a status row is shown at the check's gate, None, and the Configuration page
            // shows a database's server and port only at the database-wide one.
            return HealthVerdict.Quiet(
                "Nothing in the host or port of this database's data source looks like a transaction-mode pooler; C10 tests the connection itself.");
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K04",
            Severity = HealthSeverity.Warning,
            Title = "The HotCold daemon's leadership lock is session-scoped, and this database's connection looks like it goes through a transaction-mode pooler",
            WhyItMatters =
                "HotCold leadership is a Postgres advisory lock, and a session-scoped one belongs to the server connection that took it. "
                + "A transaction-mode pooler hands that server connection to another client as soon as the transaction that took the lock "
                + "ends: the lock then belongs to whoever uses the connection next, the leader's release lands on another connection and "
                + "fails, and two nodes can end up running the same projection - or none. PgBouncer's own documentation says session-level "
                + "advisory locks never work in transaction pooling, and Marten's 9.41 migration guide names this as the one deployment "
                + "that has to act.",
            Evidence =
            [
                new HealthEvidence("Async mode", facts.AsyncMode.ToString()),
                new HealthEvidence("UseAdvisoryLockTransaction", facts.DescribeLockSetting(evaluation.Runtime)),
                // The host and port: the Configuration page lists a database's server and port only to a visitor the store
                // policy passes for the database as a whole, and so does this.
                new HealthEvidence("Looks pooled", string.Join("; ", signs), HealthGate.Database),
            ],
            Caveat =
                "Read from this process's configuration and the host and port of the database's connection string; nothing was asked of "
                + "the server, so this is a likeness, not a proof - C10 tests the connection itself. A pooler on a port of its own choosing "
                + "is not recognised, and a PgBouncer of one's own on 6432 pools by session unless it is told otherwise.",
            Remedy = DaemonLockPooling.Remedy,
        });
    }

}
