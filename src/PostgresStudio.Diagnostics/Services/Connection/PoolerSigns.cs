using System.Globalization;
using System.Text.RegularExpressions;

namespace PostgresStudio.Services.Connection;

/// <summary>What a pooler sign looks like in a host or a port.</summary>
internal enum PoolerSignKind
{
    /// <summary>A <c>-pooler.</c> host: Neon's pooled endpoint, PgBouncer in transaction mode.</summary>
    NeonPooler,

    /// <summary>Port 6543, where Supabase's poolers take transaction-mode connections.</summary>
    SupabaseTransaction,

    /// <summary>Port 6432 of an Azure server: its built-in PgBouncer, pooling by transaction by default.</summary>
    AzurePgBouncer,

    /// <summary>Port 6432: PgBouncer's default <c>listen_port</c>.</summary>
    PgBouncerPort,

    /// <summary>A <c>*.proxy-*.&lt;region&gt;.rds.amazonaws.com</c> host: an Amazon RDS Proxy endpoint.</summary>
    RdsProxy,
}

/// <summary>One host and port that looks like a pooler, with the provider document that says so.</summary>
/// <param name="Host">The host as the connection string wrote it.</param>
/// <param name="Port">Its port.</param>
/// <param name="Kind">Which sign.</param>
/// <param name="Text">
/// The sign in words, <c>host:port - why</c>: the text K04's evidence carries. It names the host, so it is evidence
/// behind <c>HealthGate.Database</c> and never prose.
/// </param>
internal sealed record PoolerSign(string Host, int Port, PoolerSignKind Kind, string Text)
{
    /// <summary>
    /// Whether the sign is a PgBouncer, or a pooler behind the same advice: Neon's, Azure's built-in and the default port
    /// are PgBouncer, and Supabase's 6543 is a transaction-mode pooler Npgsql's compatibility advice applies to. An RDS
    /// Proxy is not: it multiplexes and pins by its own rules.
    /// </summary>
    public bool IsPgBouncerFamily => Kind != PoolerSignKind.RdsProxy;

    /// <summary>Whether the sign is a pooler that works by transaction and holds a session-scoped advisory lock where it does not pin (K04).</summary>
    public bool IsTransactionMode => Kind != PoolerSignKind.RdsProxy;
}

/// <summary>
/// What, in a data source's hosts and ports, looks like a connection pooler - moved out of K04 (<c>PooledDaemonLockCheck.SignsOf</c>)
/// so the Connection page and the connection checks read the same signs, and extended with Amazon RDS Proxy.
/// </summary>
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
/// <item>A host of the shape <c>&lt;name&gt;.proxy-&lt;id&gt;.&lt;region&gt;.rds.amazonaws.com</c>: an Amazon RDS Proxy endpoint
/// (<i>Connecting to a database through RDS Proxy</i>, Amazon RDS User Guide, <c>rds-proxy-connecting</c>, whose example is
/// <c>the-proxy.proxy-demo.us-east-1.rds.amazonaws.com</c>; a user-defined endpoint has the same <c>proxy-</c> label,
/// with the endpoint's name before it). The China regions end <c>.rds.amazonaws.com.cn</c>. RDS Proxy pins a session on <c>SET</c>,
/// on <c>PREPARE</c>, <c>DISCARD</c>, <c>DEALLOCATE</c> and <c>EXECUTE</c>, on temporary tables, sequences and views, on
/// <c>LISTEN</c>, and on session-level advisory locks (<c>pg_advisory_lock</c>, <c>pg_try_advisory_lock</c>), and ends an
/// idle client connection after 1800 s by default (<i>Avoiding pinning an RDS Proxy</i>, <c>rds-proxy-pinning</c>; <i>RDS
/// Proxy connection considerations</i>, <c>rds-proxy-connections</c>, <c>IdleClientTimeout</c>; both read 2026-11-03).</item>
/// </list>
/// <para>
/// <b>K04 does not take the RDS Proxy sign.</b> K04 is about a session-scoped advisory lock being handed to another client
/// by a transaction-mode pooler. RDS Proxy pins on exactly that lock, which keeps the lock with the leader, so the sign is
/// not evidence of K04's condition (<see cref="TransactionModeTexts" /> is K04's list, <see cref="Find" /> is every sign).
/// </para>
/// </remarks>
internal static partial class PoolerSigns
{
    /// <summary>The idle client connection timeout RDS Proxy applies by default, in seconds (<c>IdleClientTimeout</c>).</summary>
    public const int RdsProxyIdleClientTimeoutSeconds = 1800;

    // <name>.proxy-<id>.<region>.rds.amazonaws.com[.cn]; the name may itself contain dots for a user-defined endpoint.
    [GeneratedRegex(@"\.proxy-[a-z0-9]+\.[a-z0-9-]+\.rds\.amazonaws\.com(\.cn)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex RdsProxyHost();

    /// <summary>Every pooler sign of these endpoints, each with its kind. Pure: the unit tests hold each to it.</summary>
    /// <param name="endpoints">The data source's hosts, each with its port.</param>
    public static IReadOnlyList<PoolerSign> Find(IEnumerable<(string Host, int Port)> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        List<PoolerSign> signs = [];

        foreach ((string host, int port) in endpoints)
        {
            string name = host.ToLowerInvariant();
            string where = host + ":" + port.ToString(CultureInfo.InvariantCulture);

            if (name.Contains("-pooler.", StringComparison.Ordinal))
            {
                signs.Add(new(host, port, PoolerSignKind.NeonPooler, where + " - the -pooler suffix of Neon's pooled endpoint, PgBouncer in transaction mode"));
            }
            else if (IsRdsProxyHost(name))
            {
                signs.Add(new(host, port, PoolerSignKind.RdsProxy, where + " - the proxy- label of an Amazon RDS Proxy endpoint, which pins a session on SET and PREPARE and ends idle client connections after 1800 s by default"));
            }
            else if (port == 6543)
            {
                signs.Add(new(host, port, PoolerSignKind.SupabaseTransaction, where + " - port 6543, where Supabase's poolers take transaction-mode connections"));
            }
            else if (port == 6432 && name.EndsWith(".postgres.database.azure.com", StringComparison.Ordinal))
            {
                signs.Add(new(host, port, PoolerSignKind.AzurePgBouncer, where + " - port 6432 of an Azure server, its built-in PgBouncer, which pools by transaction by default"));
            }
            else if (port == 6432)
            {
                signs.Add(new(host, port, PoolerSignKind.PgBouncerPort, where + " - port 6432, PgBouncer's default port"));
            }
        }

        return signs;
    }

    /// <summary>
    /// The signs K04 and C10 read: <see cref="Find" /> without the RDS Proxy (see the type's remarks), as the words K04's
    /// evidence carries. What <c>PooledDaemonLockCheck.SignsOf</c> returned before the move, byte for byte.
    /// </summary>
    /// <param name="endpoints">The data source's hosts, each with its port.</param>
    public static IReadOnlyList<string> TransactionModeTexts(IEnumerable<(string Host, int Port)> endpoints) =>
        [.. Find(endpoints).Where(static x => x.IsTransactionMode).Select(static x => x.Text)];

    /// <summary>Whether <paramref name="host" /> is an Amazon RDS Proxy endpoint.</summary>
    /// <param name="host">A host name, any case, with or without a trailing dot.</param>
    public static bool IsRdsProxyHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        string name = host.Trim().TrimEnd('.');

        try
        {
            return RdsProxyHost().IsMatch(name);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
