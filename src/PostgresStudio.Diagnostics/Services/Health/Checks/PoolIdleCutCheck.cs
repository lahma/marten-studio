using System.Globalization;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C16: something ends an idle connection - the server's <c>idle_session_timeout</c>, or an Amazon RDS Proxy's idle client
/// timeout - before the data source's pool lets go of it, and the pool is not keeping it alive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it fails late.</b> Npgsql's pool keeps an idle connection for <c>Connection Idle Lifetime</c> (300 s by default),
/// and always keeps <c>Minimum Pool Size</c> of them open however long they idle. It does not check a connection when it
/// hands it out of the pool, so when the server (or a proxy) has closed one in the meantime, the application finds out with
/// the next command on it, not when the connection was cut. <c>Keepalive</c> sends a message on an idle connection every
/// <c>Keepalive</c> seconds (<c>NpgsqlConnector.PerformKeepAlive</c>, Npgsql 9.0.4 and 10.0.3), which counts as activity to a
/// server's idle timer and to a proxy's.
/// </para>
/// <para>
/// <b>Two variants, two causes.</b> <c>idle-session-timeout</c>: <c>idle_session_timeout</c> (PostgreSQL 14) is above zero as a
/// new session of the login starts with it (<see cref="ConnectionObserved.IdleSessionTimeoutMs" />: <c>reset_val</c>, so a
/// setting made with <c>ALTER ROLE … SET</c> counts and the studio's own transaction settings do not). <c>proxy-idle-timeout</c>:
/// the data source's host is an Amazon RDS Proxy, which ends a client connection that has been idle for its
/// <c>IdleClientTimeout</c>, 1800 s by default (<i>RDS Proxy connection considerations</i>, Amazon RDS User Guide); the studio
/// cannot read the proxy's setting, so it judges the default and says so.
/// </para>
/// <para>
/// <b>The pool outlasts the cut</b> when it holds connections that nothing retires first: <c>Minimum Pool Size</c> above zero,
/// or a <c>Connection Idle Lifetime</c> not shorter than the cut. And <c>Keepalive</c> covers it when it is above zero and
/// shorter than the cut.
/// </para>
/// <para>
/// <b>A pooler in front changes whose connection it is.</b> Behind a PgBouncer-family pooler the server's
/// <c>idle_session_timeout</c> ends the pooler's own server connection, which the pooler replaces; the client's connection to
/// the pooler is the pooler's to end (<c>client_idle_timeout</c>). So the <c>idle-session-timeout</c> variant is not judged
/// where a PgBouncer-family sign is present. An RDS Proxy is not PgBouncer: its sign gives the <c>proxy-idle-timeout</c> variant.
/// </para>
/// <para>
/// <b>Behind an RDS Proxy the server's cut reaches the client only on a pinned connection.</b> The proxy multiplexes, so the
/// server's <c>idle_session_timeout</c> ends a database connection the proxy holds for the client only while it is pinned to that
/// client (<i>Avoiding pinning an RDS Proxy</i>, <c>rds-proxy-pinning</c>). Npgsql's reset when a connection returns to its pool
/// pins it by default (<see cref="ConnectionHints" />' RESET hint); <c>No Reset On Close=true</c>, which that hint advises behind an
/// RDS Proxy, stops the reset and with it the pinning, so with an RDS Proxy sign and <c>No Reset On Close=true</c> the
/// <c>idle-session-timeout</c> variant is not judged and the quiet note says why. The <c>proxy-idle-timeout</c> variant is not
/// affected: the proxy's own idle client timeout applies either way.
/// </para>
/// <para>
/// <b>Severity is Warning</b> for both; the remedy is the data source's settings. K08 judges the same server timeout against
/// the HotCold daemon's leadership in Marten Studio (<see cref="Settings.StudioText.PoolIdleTimeoutLeaderClause" />).
/// </para>
/// </remarks>
[HealthCheck(
    "C16",
    HealthArea.Connections,
    HealthCostTier.Catalog,
    "Server or proxy ends idle pooled connections first",
    Variants = [IdleSessionTimeoutVariant, ProxyIdleTimeoutVariant])]
internal sealed class PoolIdleCutCheck : HealthCheck<ConnectionFacts>
{
    /// <summary>The variant of a finding about the server's <c>idle_session_timeout</c>.</summary>
    public const string IdleSessionTimeoutVariant = "idle-session-timeout";

    /// <summary>The variant of a finding about an Amazon RDS Proxy's idle client timeout.</summary>
    public const string ProxyIdleTimeoutVariant = "proxy-idle-timeout";

    /// <inheritdoc />
    public override async Task<ConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Pooling off is the connection string's to say, so it stands the check down whether or not the server answered.
        return await ConnectionCheckSupport.ReadAsync(context, NotApplicableReason).ConfigureAwait(false);
    }

    /// <summary>Why this check does not apply to the data source, or <see langword="null" /> when it does.</summary>
    /// <param name="facts">The connection facts.</param>
    public static string? NotApplicableReason(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Settings.Values.Pooling
            ? null
            : "Pooling is off in the data source's connection string, so no connection waits idle in a pool for the server or a proxy to end it. C17 judges having no pool.";
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ConnectionValues v = facts.Settings.Values;
        bool pgBouncerFamily = facts.Settings.PoolerSigns.Any(static x => x.IsPgBouncerFamily);
        bool rdsProxy = facts.Settings.PoolerSigns.Any(static x => x.Kind == PoolerSignKind.RdsProxy);

        List<HealthFinding> findings = [];
        List<string> notes = [];

        // The server's idle_session_timeout.
        if (facts.Observed is null)
        {
            notes.Add("what the server was started with could not be read (" + (facts.ObservedNotRead ?? "no reason given") + "), so idle_session_timeout is not judged");
        }
        else if (facts.Observed.IdleSessionTimeoutMs is null)
        {
            notes.Add("the server has no idle_session_timeout (PostgreSQL 14 added it)");
        }
        else if (facts.Observed.IdleSessionTimeoutSeconds is not { } serverCut)
        {
            notes.Add("idle_session_timeout is off (0) for this login");
        }
        else if (pgBouncerFamily)
        {
            notes.Add("the data source reaches a connection pooler, which holds the server connection that idle_session_timeout ends, so it is not judged against this pool");
        }
        else if (rdsProxy && v.Bool("No Reset On Close") == true)
        {
            notes.Add(
                "the data source reaches an Amazon RDS Proxy with No Reset On Close=true: the server's idle_session_timeout reaches the client only on a connection the proxy has pinned, "
                + "Npgsql's reset is what pins its connections by default, and without it they are not pinned, so idle_session_timeout is not judged against this pool");
        }
        else if (Outlasts(v, serverCut))
        {
            findings.Add(IdleSessionFinding(facts, evaluation, serverCut));
        }
        else
        {
            notes.Add("the pool retires or keeps alive its idle connections before idle_session_timeout (" + ConnectionCheckSupport.Seconds(serverCut) + ") ends them");
        }

        // An Amazon RDS Proxy's idle client timeout, by its default.
        if (rdsProxy)
        {
            int proxyCut = PoolerSigns.RdsProxyIdleClientTimeoutSeconds;

            if (Outlasts(v, proxyCut))
            {
                findings.Add(ProxyFinding(facts, evaluation, proxyCut));
            }
            else
            {
                notes.Add("the pool retires or keeps alive its idle connections before an Amazon RDS Proxy's default idle client timeout (" + ConnectionCheckSupport.Seconds(proxyCut) + ")");
            }
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(Capitalize(string.Join("; ", notes)) + ".");
    }

    /// <summary>
    /// Whether the pool holds idle connections that nothing retires or keeps alive before something ends them after
    /// <paramref name="cut" /> seconds: <c>Minimum Pool Size</c> above zero, or a <c>Connection Idle Lifetime</c> not shorter
    /// than the cut - and no <c>Keepalive</c> shorter than the cut.
    /// </summary>
    /// <param name="values">The data source's settings.</param>
    /// <param name="cut">The shortest idle time after which the connection is ended, in seconds.</param>
    public static bool Outlasts(ConnectionValues values, int cut)
    {
        ArgumentNullException.ThrowIfNull(values);

        bool keptAlive = values.Keepalive > 0 && values.Keepalive < cut;

        return !keptAlive && (values.MinPoolSize > 0 || values.ConnectionIdleLifetime >= cut);
    }

    private static HealthFinding IdleSessionFinding(ConnectionFacts facts, HealthEvaluation evaluation, int cut) => Finding(
        facts,
        evaluation,
        IdleSessionTimeoutVariant,
        cut,
        "The server ends idle connections after " + ConnectionCheckSupport.Seconds(cut) + ", before the pool lets go of them",
        "The server's idle_session_timeout closes a session that has sat idle (outside a transaction) for that long. Npgsql's pool "
        + "keeps an idle connection for Connection Idle Lifetime, and always keeps Minimum Pool Size of them, and it does not check a "
        + "connection when it hands it out - so the pool can hold a connection the server has already closed, and the next command "
        + "on it fails (SQLSTATE 57P05 or an I/O error) instead of the pool opening a fresh one.",
        [new("idle_session_timeout", ConnectionCheckSupport.Seconds(cut) + " (what a new session of this login starts with)")],
        "idle_session_timeout is read as a new session of this login starts with it, so a value set for the role or the database "
        + "(ALTER ROLE … SET) counts and one the application sets for itself with SET is not seen. The server counts idle time "
        + "between commands outside a transaction."
        + evaluation.Text.PoolIdleTimeoutLeaderClause);

    private static HealthFinding ProxyFinding(ConnectionFacts facts, HealthEvaluation evaluation, int cut) => Finding(
        facts,
        evaluation,
        ProxyIdleTimeoutVariant,
        cut,
        "An Amazon RDS Proxy ends idle connections after " + ConnectionCheckSupport.Seconds(cut) + ", before the pool lets go of them",
        "An Amazon RDS Proxy closes a client connection that has been idle for its idle client timeout (IdleClientTimeout, 1800 s "
        + "by default; Amazon RDS User Guide, RDS Proxy connection considerations). Npgsql's pool keeps an idle connection for "
        + "Connection Idle Lifetime, and always keeps Minimum Pool Size of them, and it does not check a connection when it hands "
        + "it out - so the pool can hold a connection the proxy has already closed, and the next command on it fails.",
        [new("Idle client timeout of the proxy", ConnectionCheckSupport.Seconds(cut) + " (the proxy's default)")],
        "The studio cannot read the proxy's own setting, so it judges RDS Proxy's default of 1800 s; a proxy configured with a "
        + "shorter idle client timeout needs a correspondingly lower Keepalive or Connection Idle Lifetime.");

    private static HealthFinding Finding(
        ConnectionFacts facts,
        HealthEvaluation evaluation,
        string variant,
        int cut,
        string title,
        string why,
        HealthEvidence[] cause,
        string caveat)
    {
        ConnectionValues v = facts.Settings.Values;
        string half = Math.Max(cut / 2, 1).ToString(CultureInfo.InvariantCulture);

        List<HealthEvidence> evidence = [.. cause];
        evidence.Add(new("Minimum Pool Size", v.MinPoolSize.ToString(CultureInfo.InvariantCulture)));
        evidence.Add(new("Connection Idle Lifetime", ConnectionCheckSupport.Seconds(v.ConnectionIdleLifetime, zeroMeans: null)));
        evidence.Add(new("Keepalive", ConnectionCheckSupport.Seconds(v.Keepalive)));
        evidence.Add(new(
            "Why the pool outlasts the cut",
            v.MinPoolSize > 0
                ? "Minimum Pool Size keeps " + v.MinPoolSize.ToString(CultureInfo.InvariantCulture) + " connection(s) open however long they idle"
                : "Connection Idle Lifetime is not shorter than the cut, so the pool still holds the connection when it is ended"));

        string remedy = ConnectionCheckSupport.Block(
            evaluation.Text,
            "Keep idle connections alive, in the connection string the data source is built from:",
            "Keepalive=" + half,
            "b.ConnectionStringBuilder.KeepAlive = " + half + ";");

        // Npgsql refuses a pool whose idle lifetime is shorter than its pruning interval, so retiring is offered only where it can be set.
        bool canRetire = Math.Max(cut / 2, 1) >= (v.Int("Connection Pruning Interval") ?? 10);

        if (canRetire)
        {
            remedy += "\n\n" + ConnectionCheckSupport.Block(
                evaluation.Text,
                "Or retire them before the cut (Minimum Pool Size must be 0 for this to hold):",
                "Connection Idle Lifetime=" + half + ";Minimum Pool Size=0",
                "b.ConnectionStringBuilder.ConnectionIdleLifetime = " + half + ";",
                "b.ConnectionStringBuilder.MinPoolSize = 0;");
        }

        return new HealthFinding
        {
            Code = "C16",
            Variant = variant,
            Severity = HealthSeverity.Warning,
            Title = title,
            WhyItMatters = why,
            Evidence = evidence,
            Caveat = caveat,
            Remedy = new HealthRemedy(
                remedy,
                "Keepalive sends a message on an idle connection every " + half + " s, which counts as activity and keeps it open "
                + "(Npgsql recommends it over the TCP variants)."
                + (canRetire
                    ? " Retiring idle connections instead means a Connection Idle Lifetime below the cut with no Minimum Pool Size, so the "
                      + "pool closes them first and opens a fresh one when it is next asked."
                    : " The cut is too short to retire connections before it: Npgsql refuses a Connection Idle Lifetime shorter than the "
                      + "pruning interval (Connection Pruning Interval, 10 s by default).")),
            Link = SessionEvidence.SessionsLink,
        };
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
}
