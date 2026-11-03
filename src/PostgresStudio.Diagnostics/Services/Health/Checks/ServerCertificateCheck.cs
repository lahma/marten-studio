using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// S11: the connection is encrypted, but the data source does not check the server's certificate, so anything between the
/// application and the server can pretend to be it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What Npgsql does.</b> At <c>SSL Mode=Prefer</c> and <c>Require</c> Npgsql accepts any certificate the server presents
/// (<c>SslTrustServerValidation</c>, Npgsql 9.0.4 and 10.0.3); only <c>VerifyCA</c> and <c>VerifyFull</c> validate it. <c>Prefer</c>
/// also falls back to plaintext when the server offers no TLS, and <c>Allow</c> tries plaintext first. <c>Trust Server
/// Certificate</c> is <c>[Obsolete]</c> and does nothing at either version, so it is not a variant, and a <c>Root Certificate</c>
/// is read only at <c>VerifyCA</c> and <c>VerifyFull</c>: the evidence says when one is set and ignored.
/// </para>
/// <para>
/// <b>Judged only when TLS is in use.</b> The connection's own <c>pg_stat_ssl</c> row decides (<see cref="ConnectionObserved.Ssl" />):
/// a connection without TLS is S05's. When the studio's role cannot see its own backend's row (<c>pg_stat_ssl</c> filters on
/// <c>client_port IS NOT NULL</c>, which needs the privileges of the session's login role) the TLS state is null and the check
/// stands down as S05 does.
/// </para>
/// <para>
/// <b>Not applicable behind a pooler.</b> The server's <c>pg_stat_ssl</c> row behind a pooler describes the pooler's own connection to
/// the server, not the application's connection to the pooler, so it cannot say whether the application's leg uses TLS or checks a
/// certificate; any pooler sign (<see cref="ConnectionSettings.PoolerSigns" />) makes the check not applicable, and the Connection
/// page's TLS hint, which judges <c>SSL Mode</c> alone, is what speaks for that leg. The pooler stand-down runs first, before the
/// unread-server check and before an unreachable server's failure is thrown again, so a pooler data source whose connection could
/// not be read is not applicable rather than unavailable or failed. A port alone is no proof of a pooler (6432 and 6543 are also
/// where a plain server can listen), and the stand-down says the data source <i>may</i> reach one.
/// </para>
/// <para>
/// <b>The exception: <c>SSL Mode=Require</c> behind a pooler.</b> The setting alone says the application's connection is encrypted
/// (Npgsql fails the connection when the endpoint offers no TLS) and never verified, so the check fires the <c>require</c> variant
/// from the setting (<see cref="JudgedFromSslMode" />), without the server's row. <c>Prefer</c> and <c>Allow</c> behind a pooler stay
/// not applicable, because the setting alone cannot say whether TLS is used. The Amazon RDS Proxy remedy (<c>VerifyFull</c> with no
/// <c>Root Certificate</c>) is reached through this path.
/// </para>
/// <para>
/// <b>Stands down where nothing can sit between.</b> A host that is a Unix socket or loopback never leaves the machine, and a
/// Cloud SQL server reached through the Auth Proxy is a local endpoint whose TLS the proxy provides itself (per Google's Cloud
/// SQL Auth Proxy design: the proxy "wraps the connection in a SSL/TLS layer that's authorized for a Cloud SQL instance").
/// </para>
/// <para>
/// <b>Severity.</b> Warning on a detected cloud platform (<see cref="ConnectionFacts.Platform" />), Info anywhere else: the
/// studio can only judge that a certificate is not checked, not that the network is hostile. The remedy names the provider's
/// bundle where it has a source for one (<see cref="ConnectionHints.ProviderCertificates" />).
/// </para>
/// </remarks>
[HealthCheck("S11", HealthArea.Server, HealthCostTier.Catalog, "Server certificate not verified", Variants = [PreferVariant, RequireVariant])]
internal sealed class ServerCertificateCheck : HealthCheck<ConnectionFacts>
{
    /// <summary>The variant of a finding at <c>SSL Mode=Allow</c> or <c>Prefer</c>, which also fall back to plaintext.</summary>
    public const string PreferVariant = "prefer";

    /// <summary>The variant of a finding at <c>SSL Mode=Require</c>.</summary>
    public const string RequireVariant = "require";

    /// <inheritdoc />
    public override async Task<ConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A pooler stands the check down before anything else, the connection read's failure included: behind one, an unread or
        // unreachable connection says nothing about the application's leg either, so "not applicable" is the truer answer than
        // "unavailable" or "failed". What the server reported (a Unix socket, loopback, the TLS state) is only known once it answered.
        ConnectionFacts shared = await ConnectionFacts.SharedAsync(context).ConfigureAwait(false);

        if (JudgedFromSslMode(shared))
        {
            // Require behind a pooler: encrypted and never verified by the setting itself, so what the server saw is not needed. A server
            // that never answered is still Failed, never a finding: Reached counts a catalog check that did not fail as proof that the
            // database was read.
            shared.ObservedFailure?.Throw();

            return shared;
        }

        ConnectionFacts facts = await ConnectionCheckSupport.ReadAsync(context, PoolerReason).ConfigureAwait(false);

        if (UnavailableReason(facts) is { } unavailable)
        {
            throw new HealthCheckUnavailableException(unavailable);
        }

        if (NotApplicableReason(facts) is { } reason)
        {
            throw new HealthCheckNotApplicableException(reason);
        }

        return facts;
    }

    /// <summary>Why the connection's TLS state could not be read at all, or <see langword="null" />.</summary>
    /// <param name="facts">The connection facts.</param>
    public static string? UnavailableReason(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Observed is null
            ? "What the server saw of the connection could not be read (" + (facts.ObservedNotRead ?? "no reason given") + "), so whether it is encrypted is not known."
            : null;
    }

    /// <summary>Why a pooler in front of the data source makes this check not applicable, or <see langword="null" /> when no pooler sign is present.</summary>
    /// <param name="facts">The connection facts.</param>
    public static string? PoolerReason(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return JudgedFromSslMode(facts) ? null : PoolerSigns.TlsStandDownReason(facts.Settings.PoolerSigns);
    }

    /// <summary>
    /// Whether the finding is the <c>require</c> variant judged from <c>SSL Mode=Require</c> alone: the settings were read, a host is
    /// remote, a pooler sign is present and the mode is <c>Require</c>. Without a pooler sign the server's own row decides.
    /// </summary>
    /// <param name="facts">The connection facts.</param>
    public static bool JudgedFromSslMode(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Settings.Readable && facts.Settings.PoolerSigns.Count > 0 && facts.Settings.Values.SslRequired && facts.HasRemoteHost;
    }

    /// <summary>Why this check does not apply to the data source, or <see langword="null" /> when it does.</summary>
    /// <param name="facts">The connection facts.</param>
    public static string? NotApplicableReason(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (JudgedFromSslMode(facts))
        {
            return null;
        }

        if (PoolerReason(facts) is { } pooler)
        {
            return pooler;
        }

        if (facts.IsCloudSqlProxyCase)
        {
            return "The data source reaches Cloud SQL through the Auth Proxy (a local socket or loopback host). The proxy wraps the connection to the instance in TLS itself, "
                   + "per Google's Cloud SQL Auth Proxy design, so the server sees no TLS from the application and the application's SSL Mode is not what protects that leg.";
        }

        return facts.Observed?.Ssl is null
            ? "The studio's role cannot see its own connection's row in pg_stat_ssl (it needs the privileges of the login role, which a narrowed SqlConsoleRole lacks), so whether the connection uses TLS is not known."
            : null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ConnectionObserved? observed = facts.Observed;
        ConnectionValues v = facts.Settings.Values;

        if (!facts.HasRemoteHost)
        {
            return HealthVerdict.Quiet("The data source's hosts are on this machine (loopback or a Unix socket), so the connection never leaves it.");
        }

        bool fromSetting = JudgedFromSslMode(facts);

        if (!fromSetting && observed?.Ssl is not true)
        {
            return HealthVerdict.Quiet("The connection does not use TLS; S05 judges that.");
        }

        if (!v.CertificateNotVerified)
        {
            return HealthVerdict.Quiet("SSL Mode=" + v.SslMode + " checks the server's certificate.");
        }

        string mode = v.SslMode.ToLowerInvariant();
        string variant = mode == "require" ? RequireVariant : PreferVariant;
        string provider = ConnectionHints.ProviderCertificates(facts.Platform.Kind, facts.Settings.PoolerSigns) is { } bundle ? " " + bundle : string.Empty;
        bool rootCertificate = !ConnectionHints.IsRdsProxy(facts.Settings.PoolerSigns);
        bool cloud = facts.Platform.IsManaged;

        List<HealthEvidence> evidence =
        [
            new("SSL Mode", v.SslMode),
            fromSetting
                ? new("Connection", "encrypted by SSL Mode=Require, to the server or to a pooler in front of it (judged from the setting, not from the server)")
                : new("Connection", "encrypted" + (observed?.SslVersion is { Length: > 0 } version ? " (" + version + ")" : string.Empty)),
            new("Platform", facts.Platform.Name),
        ];

        if (!string.IsNullOrEmpty(v.Text("Root Certificate")))
        {
            evidence.Add(new("Root Certificate", "set, and not used: Npgsql reads it only at SSL Mode=VerifyCA and VerifyFull"));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S11",
            Variant = variant,
            Severity = cloud ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = "The connection is encrypted, but the server's certificate is not checked (SSL Mode=" + v.SslMode + ")",
            WhyItMatters =
                "Npgsql accepts whatever certificate the server presents at this SSL Mode, so the encryption stops anyone who only listens "
                + "but not anyone who stands in the path and answers as the server: they can read and change everything the connection carries, "
                + "the login included."
                + (variant == PreferVariant
                    ? " At Prefer and Allow the connection also falls back to plaintext without saying so when the server offers no TLS, which S05 would then flag."
                    : string.Empty),
            Evidence = evidence,
            Caveat =
                (fromSetting
                    ? "Judged from SSL Mode=Require in the data source's connection string, not from the server's pg_stat_ssl row: a connection pooler may sit in front of the server, "
                      + "and the server's row would then describe the pooler's own connection, not the application's. "
                    : "Read from this connection's own pg_stat_ssl row: the studio's connection uses the data source's settings, as the application's "
                      + "do. ")
                + (cloud
                    ? "Warning because " + facts.Platform.Name + " was detected: the path to a managed server crosses networks the application does not run."
                    : "Info because no managed platform was detected; the studio cannot tell whether the path to the server is trusted."),
            Remedy = new HealthRemedy(
                rootCertificate
                    ? ConnectionCheckSupport.Block(
                        evaluation.Text,
                        "In the connection string the data source is built from:",
                        "SSL Mode=VerifyFull;Root Certificate=<path to the provider's CA bundle>",
                        "b.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;",
                        "b.ConnectionStringBuilder.RootCertificate = \"<path to the provider's CA bundle>\";")
                    : ConnectionCheckSupport.Block(
                        evaluation.Text,
                        "In the connection string the data source is built from:",
                        "SSL Mode=VerifyFull",
                        "b.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;"),
                "VerifyFull checks that the server's certificate chains to a trusted authority and names the host; VerifyCA checks only the "
                + "chain. Without a Root Certificate Npgsql uses the machine's trusted certificate authorities, which suits a certificate from a public authority "
                + "and not a provider's own CA." + provider),
        });
    }
}
