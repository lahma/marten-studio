using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S05 reads.</summary>
/// <param name="Posture">
/// The source's connection: whether it is encrypted, and how it came; <see langword="null" /> when <paramref name="FromSslMode" />
/// decided it and the server was not asked.
/// </param>
/// <param name="ServerSsl">The server's <c>ssl</c> setting, or <see langword="null" /> when not read.</param>
/// <param name="EnvironmentName">The host's environment, or <see langword="null" />.</param>
/// <param name="PoolerSigns">The signs of a connection pooler in the data source's hosts and ports, or <see langword="null" /> when none were read.</param>
/// <param name="FromSslMode">
/// Whether the data source's <c>SSL Mode=Disable</c> and a remote host decided it, without the server's <c>pg_stat_ssl</c> row: the
/// application's own connection is then plaintext for certain, whatever sits in front of the server.
/// </param>
internal sealed record UnencryptedConnectionFacts(
    ServerConnectionPosture? Posture,
    string? ServerSsl,
    string? EnvironmentName,
    IReadOnlyList<PoolerSign>? PoolerSigns = null,
    bool FromSslMode = false)
{
    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => HostEnvironmentName.IsProduction(EnvironmentName);
}

/// <summary>
/// S05: the source's data source reaches the server over the network without TLS.
/// </summary>
/// <remarks>
/// <para>
/// <b>The source's own connection.</b> <c>pg_stat_ssl</c> for <c>pg_backend_pid()</c> on a connection of the source's data
/// source is what the application's connections negotiate too: the same connection string, the same <c>SSL Mode</c>. A Unix
/// -domain socket (no client address) never leaves the server's machine, and a loopback connection never leaves it either -
/// neither is a finding.
/// </para>
/// <para>
/// <b>Warning in Production, Info anywhere else.</b> The remedy is the Npgsql connection-string setting, never a server
/// setting: the client decides whether it encrypts and whether it checks the certificate.
/// </para>
/// <para>
/// <b>Not applicable behind a pooler, except for <c>SSL Mode=Disable</c>.</b> The server's <c>pg_stat_ssl</c> row behind a pooler
/// (PgBouncer, an Amazon RDS Proxy, Supavisor) describes the pooler's own connection to the server, not the application's
/// connection to the pooler, so it says nothing about the application's leg; any pooler sign in the data source's hosts and ports
/// (<see cref="PoolerSigns" />) makes the check not applicable, as it does S11. The Connection page's TLS hint judges <c>SSL Mode</c>
/// instead. A port alone is no proof of a pooler (6432 and 6543 are also where a plain server can listen), and <c>SSL Mode=Disable</c>
/// with a remote host needs no row: the application's own connection is plaintext for certain, to the server or to a pooler in front
/// of it, so behind a pooler sign the check fires from the setting instead of standing down (<see cref="JudgedFromSslMode" />).
/// </para>
/// </remarks>
[HealthCheck("S05", HealthArea.Server, HealthCostTier.Catalog, "Connection without TLS")]
internal sealed class UnencryptedConnectionCheck : HealthCheck<UnencryptedConnectionFacts>
{
    /// <inheritdoc />
    public override async Task<UnencryptedConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The signs come from the data source's hosts and ports, which the sweep's one connection read has already parsed.
        ConnectionFacts connection = await ConnectionFacts.SharedAsync(context).ConfigureAwait(false);

        if (JudgedFromSslMode(connection))
        {
            // Plaintext by the setting itself: the server is not asked for a row, and the finding says it was judged from
            // SSL Mode=Disable. A server that never answered is still Failed, never a finding: Reached counts a catalog check that
            // did not fail as proof that the database was read.
            connection.ObservedFailure?.Throw();

            return new UnencryptedConnectionFacts(null, null, HostEnvironmentName.Of(context), connection.Settings.PoolerSigns, FromSslMode: true);
        }

        if (NotApplicableReason(connection.Settings.PoolerSigns) is { } reason)
        {
            throw new HealthCheckNotApplicableException(reason);
        }

        ServerConnectionPosture posture = await ServerConnectionPosture.SharedAsync(context).ConfigureAwait(false);
        ServerSettingsRead settings = await ServerSettingsRead.SharedAsync(context).ConfigureAwait(false);

        return new UnencryptedConnectionFacts(posture, settings.Find("ssl")?.Value, HostEnvironmentName.Of(context), connection.Settings.PoolerSigns);
    }

    /// <summary>Why this check does not apply to the data source (it reaches a connection pooler), or <see langword="null" /> when it does.</summary>
    /// <param name="poolerSigns">The data source's pooler signs, or <see langword="null" /> when none were read.</param>
    public static string? NotApplicableReason(IReadOnlyList<PoolerSign>? poolerSigns) => PoolerSigns.TlsStandDownReason(poolerSigns);

    /// <summary>
    /// Whether a pooler sign would stand the check down, and <c>SSL Mode=Disable</c> with a remote host makes it fire from the setting
    /// instead: the settings were read, a host is remote, a pooler sign is present and the mode is <c>Disable</c>. Then the
    /// application's own connection is plaintext for certain, to the server or to a pooler in front of it. With no pooler sign the
    /// server's own row decides, as it did in 1.0.
    /// </summary>
    /// <param name="facts">The connection facts.</param>
    public static bool JudgedFromSslMode(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Settings.Readable && facts.Settings.PoolerSigns.Count > 0 && facts.Settings.Values.SslDisabled && facts.HasRemoteHost;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(UnencryptedConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.FromSslMode)
        {
            return FromSslModeVerdict(facts, evaluation);
        }

        ServerConnectionPosture posture = facts.Posture
            ?? throw new ArgumentException("A finding not judged from SSL Mode carries the server's posture.", nameof(facts));

        switch (posture.Kind)
        {
            case ServerConnectionKind.UnixSocket:
                return HealthVerdict.Quiet("The connection comes over a Unix-domain socket, which never leaves the server's machine.");
            case ServerConnectionKind.Loopback:
                return HealthVerdict.Quiet("The connection comes over the loopback interface, from the server's own machine.");
        }

        if (posture.Ssl is null)
        {
            return HealthVerdict.Quiet("pg_stat_ssl had no row for this connection, so whether it is encrypted is not known.");
        }

        if (posture.Ssl == true)
        {
            return HealthVerdict.Quiet("The connection is encrypted" + (posture.SslVersion is { Length: > 0 } version ? " (" + version + ")." : "."));
        }

        bool serverOff = string.Equals(facts.ServerSsl, "off", StringComparison.OrdinalIgnoreCase);

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S05",
            Severity = facts.IsProduction ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = "Connections from " + SourceWords.DataSource(evaluation.Text) + " reach Postgres unencrypted",
            WhyItMatters =
                "The connection crosses the network in plain text: every statement, " + evaluation.Text.ConnectionCarries + ", and "
                + "the login itself, where the server does not ask for SCRAM. Anybody on the path - a shared network, a compromised "
                + "host, a load balancer's mirror - can read it, and without a checked certificate can also stand in for the server.",
            Evidence =
            [
                new("Connection", "TCP from another machine, without TLS"),
                new("Server's ssl setting", facts.ServerSsl ?? "not read"),
                new("Environment", HostEnvironmentName.Describe(facts.EnvironmentName)),
            ],
            Caveat =
                "Read from pg_stat_ssl for the backend of a connection from " + SourceWords.DataSource(evaluation.Text) + ", which negotiates TLS as the "
                + "application's connections do. "
                + (facts.IsProduction
                    ? "This is Production."
                    : "Info rather than Warning: the environment is " + HostEnvironmentName.Describe(facts.EnvironmentName) + "."),
            Remedy = new HealthRemedy("SSL Mode=VerifyFull", RemedyText(serverOff, SourceWords.DataSource(evaluation.Text))),
        });
    }

    /// <summary>
    /// The finding for <c>SSL Mode=Disable</c> with a remote host, judged from the setting. The evidence and the caveat say so, and when a
    /// pooler sign is present that the connection is "to the server or to a pooler in front of it". No host is named.
    /// </summary>
    /// <param name="facts">The facts.</param>
    /// <param name="evaluation">The evaluation.</param>
    private static HealthVerdict FromSslModeVerdict(UnencryptedConnectionFacts facts, HealthEvaluation evaluation)
    {
        bool pooler = facts.PoolerSigns is { Count: > 0 };
        string reach = pooler ? "to the server or to a pooler in front of it" : "to the server";
        string dataSource = SourceWords.DataSource(evaluation.Text);

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S05",
            Severity = facts.IsProduction ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = "Connections from " + dataSource + " reach Postgres unencrypted",
            WhyItMatters =
                "SSL Mode=Disable means the connection is never encrypted, " + reach + ": every statement, " + evaluation.Text.ConnectionCarries + ", and "
                + "the login itself, where the server does not ask for SCRAM, cross the network in plain text. Anybody on the path - a shared network, a compromised "
                + "host, a load balancer's mirror - can read it, and can also stand in for the server.",
            Evidence =
            [
                new("Connection", "not encrypted (SSL Mode=Disable), " + reach),
                new("Judged from", "SSL Mode=Disable in the data source's connection string"),
                new("Environment", HostEnvironmentName.Describe(facts.EnvironmentName)),
            ],
            Caveat =
                "Judged from SSL Mode=Disable in the connection string " + dataSource + " is built from, not from the server's pg_stat_ssl row"
                + (pooler
                    ? ": a connection pooler may sit in front of the server, and the server's row would then describe the pooler's own connection, not the application's. "
                    : ". ")
                + (facts.IsProduction
                    ? "This is Production."
                    : "Info rather than Warning: the environment is " + HostEnvironmentName.Describe(facts.EnvironmentName) + "."),
            Remedy = new HealthRemedy("SSL Mode=VerifyFull", RemedyText(serverOff: false, dataSource)),
        });
    }

    /// <summary>
    /// What the remedy says around its one line. With the server's <c>ssl</c> off the line alone would be advice that cannot work yet,
    /// so there are two steps, the server's first, on lines of their own (a newline between them: the pages draw a remedy's explanation
    /// with <c>white-space: pre-line</c>); with it on, one. Either way <c>Prefer</c> and <c>Require</c> are said not to check.
    /// </summary>
    /// <param name="serverOff">Whether the server's <c>ssl</c> setting is <c>off</c>.</param>
    /// <param name="dataSource">The words for the data source whose connection string it is.</param>
    internal static string RemedyText(bool serverOff, string dataSource) =>
        (serverOff
            ? "1. Enable TLS on the server (ssl = on with a certificate; on a managed platform it is usually on already).\n"
              + "2. Then set SSL Mode=VerifyFull (and Root Certificate= if the CA is private) in the connection string " + dataSource + " is built from - a client setting. "
            : "In the connection string " + dataSource + " is built from - a client setting, not a server one. ")
        + "VerifyFull encrypts and checks that the server's certificate chains to a trusted authority and names the host. "
        + "SSL Mode=Prefer or Require encrypts but does not check the server's certificate.";
}
