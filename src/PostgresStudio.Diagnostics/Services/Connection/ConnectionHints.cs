using System.Globalization;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// Evaluates the hints of NP-DESIGN §6 from the settings the reader redacted and the facts it was handed: informational
/// sentences for the Connection page, each with its connection-string fix and its builder fix. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hints are not findings.</b> They are never counted, ranked or exported; the checks of NP-DESIGN §7 (C16, C17, C18, S11,
/// S12, S13) are the ones that are. A hint may restate a check's condition in a sentence the page shows beside the setting.
/// </para>
/// <para>
/// <b>Platform and pooler facts</b> are the ones NP-DESIGN §5 cites: Amazon RDS User Guide (<i>Using SSL with a PostgreSQL DB
/// instance</i>; <i>Avoiding pinning an RDS Proxy</i>, <c>rds-proxy-pinning</c>; <i>RDS Proxy connection considerations</i>,
/// <c>rds-proxy-connections</c>), Microsoft Learn (<i>security-tls</i>), the Neon docs (<i>Connection pooling</i>; <i>Connection
/// latency and timeouts</i>), the Supabase docs and supabase/supavisor#239, and npgsql.org/doc/compatibility.html and
/// /doc/performance.html.
/// </para>
/// <para>
/// <b>Two departures from NP-DESIGN §6, both because the cited page says less than the design did</b> (read 2026-11-03).
/// PREPARE does not name an RDS Proxy: the Amazon RDS pinning page lists <c>PREPARE</c>, <c>DEALLOCATE</c> and <c>EXECUTE</c> for
/// PostgreSQL, and "whether the prepared statement uses SQL text or the binary protocol" under MySQL and MariaDB only, so
/// there is no source that protocol-level preparation (what <c>Max Auto Prepare</c> uses) pins on PostgreSQL. RESET asks for
/// <c>Pooling</c> on too, since a data source with no pool has nothing to reset.
/// </para>
/// </remarks>
internal static class ConnectionHints
{
    /// <summary>The hints that apply to <paramref name="settings" /> given <paramref name="context" />, in the order NP-DESIGN §6 lists them.</summary>
    /// <param name="settings">The redacted settings.</param>
    /// <param name="context">The facts the hints need beside the settings.</param>
    public static IReadOnlyList<ConnectionHint> Evaluate(ConnectionSettings settings, ConnectionReadContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(context);

        if (!settings.Readable)
        {
            return [];
        }

        List<ConnectionHint> hints = [];
        ConnectionValues v = settings.Values;
        PostgresPlatformKind platform = context.Platform;
        ConnectionObserved? observed = context.Observed;

        Tls(settings, platform, hints);
        Lifetime(settings, platform, hints);
        MultiHost(settings, hints);
        KeepAlive(settings, platform, observed, hints);
        PoolSize(v, context, hints);
        Buffer(v, context, hints);
        Prepare(settings, hints);
        Reset(settings, hints);
        Enlist(v, hints);
        CommandTimeout(v, observed, hints);
        ColdStart(v, platform, hints);
        Auth(v, observed, hints);

        if (v.Bool("Multiplexing") == true)
        {
            hints.Add(new(
                ConnectionHintIds.Multiplexing,
                "Multiplexing",
                "Multiplexing is experimental and changes connection semantics: two commands of one NpgsqlConnection can reach two server processes, so session state, session-level advisory locks and temporary tables do not hold (the notes on K04 and C10 apply). Npgsql's development branch has removed it.",
                "Multiplexing=false",
                "builder.ConnectionStringBuilder.Multiplexing = false;"));
        }

        foreach (ConnectionSettingRow row in settings.Rows.Where(static x => x.Obsolete is not null && x.IsExplicit))
        {
            hints.Add(new(
                ConnectionHintIds.Obsolete,
                row.Keyword,
                row.Keyword + " is obsolete: " + row.Obsolete,
                "Remove " + row.Keyword + " from the connection string.",
                null));
        }

        if (settings.Endpoints.Any(static x => ConnectionHosts.IsSocket(x.Host)))
        {
            hints.Add(new(
                ConnectionHintIds.Local,
                "Host",
                "Unix socket. Npgsql's performance guide notes that a socket is slightly faster than TCP for a server on the same machine.",
                null,
                null));
        }

        return hints;
    }

    private static void Tls(ConnectionSettings settings, PostgresPlatformKind platform, List<ConnectionHint> hints)
    {
        ConnectionValues v = settings.Values;

        if (!v.CertificateNotVerified || settings.RemoteEndpoints.Count == 0)
        {
            return;
        }

        string mode = v.SslMode;
        string text = string.Equals(mode, "Disable", StringComparison.OrdinalIgnoreCase)
            ? "SSL Mode=Disable: the connection is never encrypted, so anything between here and the server can read it and change it."
            : "The connection is encrypted but the server's certificate is not checked, so anything between here and the server can pretend to be it."
              + (mode.ToLowerInvariant() switch
              {
                  "prefer" => " `Prefer` also falls back to plaintext silently.",
                  "allow" => " `Allow` tries plaintext first.",
                  _ => string.Empty,
              });

        string? provider = ProviderCertificates(platform);

        hints.Add(new(
            ConnectionHintIds.Tls,
            "SSL Mode",
            provider is null ? text : text + " " + provider,
            "SSL Mode=VerifyFull;Root Certificate=<the provider's CA bundle>",
            "builder.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;\nbuilder.ConnectionStringBuilder.RootCertificate = \"<path to the provider's CA bundle>\";"));
    }

    /// <summary>The CA a provider's server certificates chain to, for the TLS hint, or <see langword="null" /> when the studio has no source for the platform.</summary>
    /// <param name="platform">The platform.</param>
    internal static string? ProviderCertificates(PostgresPlatformKind platform) => platform switch
    {
        PostgresPlatformKind.AmazonRds =>
            "Amazon RDS: VerifyFull needs the AWS CA bundle (global-bundle.pem) as Root Certificate (Amazon RDS User Guide, Using SSL with a PostgreSQL DB instance).",
        PostgresPlatformKind.Azure =>
            "Azure Database for PostgreSQL: VerifyFull needs DigiCert Global Root G2, Microsoft RSA Root CA 2017 and, for older servers, DigiCert Global Root CA in the trust bundle (Microsoft Learn, security-tls).",
        _ => null,
    };

    private static void Lifetime(ConnectionSettings settings, PostgresPlatformKind platform, List<ConnectionHint> hints)
    {
        ConnectionValues v = settings.Values;

        if (v.ConnectionLifetime != 0
            || !(settings.IsMultiHost || v.Bool("Load Balance Hosts") == true || platform == PostgresPlatformKind.AmazonAurora))
        {
            return;
        }

        hints.Add(new(
            ConnectionHintIds.Lifetime,
            "Connection Lifetime",
            "Connection Lifetime=0 means connections never recycle, so they do not rebalance after a failover or a scale-out. Npgsql's default is 3600.",
            "Connection Lifetime=3600",
            "builder.ConnectionStringBuilder.ConnectionLifetime = 3600;"));
    }

    private static void MultiHost(ConnectionSettings settings, List<ConnectionHint> hints)
    {
        if (!settings.IsMultiHost)
        {
            return;
        }

        string? attributes = settings.Values.Text("Target Session Attributes");

        if (attributes is not null && !string.Equals(attributes, "Any", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        hints.Add(new(
            ConnectionHintIds.MultiHost,
            "Target Session Attributes",
            "This data source names " + settings.Endpoints.Count.ToString(CultureInfo.InvariantCulture)
            + " hosts and accepts any of them, so a write can land on a standby.",
            "Target Session Attributes=primary",
            "builder.ConnectionStringBuilder.TargetSessionAttributes = \"primary\";"));
    }

    private static void KeepAlive(ConnectionSettings settings, PostgresPlatformKind platform, ConnectionObserved? observed, List<ConnectionHint> hints)
    {
        if (settings.Values.Keepalive != 0 || !(platform != PostgresPlatformKind.SelfHosted || settings.PoolerSigns.Count > 0))
        {
            return;
        }

        int? cut = ShortestIdleCut(settings, observed);
        string fix = "Keepalive=" + (cut is { } seconds ? Math.Max(seconds / 2, 1) : 30).ToString(CultureInfo.InvariantCulture);

        hints.Add(new(
            ConnectionHintIds.Keepalive,
            "Keepalive",
            "Idle connections can be dropped by the network silently. A Keepalive shorter than the shortest idle cut (an RDS Proxy's 1800 s, the server's idle_session_timeout) keeps them alive. Npgsql recommends Keepalive over the TCP variants.",
            fix,
            "builder.ConnectionStringBuilder.KeepAlive = " + fix["Keepalive=".Length..] + ";"));
    }

    /// <summary>The shortest idle cut in seconds that the settings and the server show: an RDS Proxy's 1800, the server's <c>idle_session_timeout</c>.</summary>
    /// <param name="settings">The redacted settings.</param>
    /// <param name="observed">What the server saw, or <see langword="null" />.</param>
    internal static int? ShortestIdleCut(ConnectionSettings settings, ConnectionObserved? observed)
    {
        int? cut = observed?.IdleSessionTimeoutSeconds;

        if (settings.PoolerSigns.Any(static x => x.Kind == PoolerSignKind.RdsProxy))
        {
            cut = cut is { } known ? Math.Min(known, PoolerSigns.RdsProxyIdleClientTimeoutSeconds) : PoolerSigns.RdsProxyIdleClientTimeoutSeconds;
        }

        return cut;
    }

    private static void PoolSize(ConnectionValues v, ConnectionReadContext context, List<ConnectionHint> hints)
    {
        if (context.PoolPeak is not { } peak)
        {
            return;
        }

        string text = "The pool peaked at " + peak.Peak.ToString(CultureInfo.InvariantCulture) + " of " + peak.Max.ToString(CultureInfo.InvariantCulture)
            + " connections in the last " + peak.Window + ".";

        if (context.TightHeadroom)
        {
            text += " The server's connection headroom is tight (C03), so a lower Maximum Pool Size is worth weighing against the application's peak.";
        }

        hints.Add(new(ConnectionHintIds.PoolSize, "Maximum Pool Size", text, null, null));
    }

    private static void Buffer(ConnectionValues v, ConnectionReadContext context, List<ConnectionHint> hints)
    {
        if (context.AverageRowBytes is not { } average || average <= v.ReadBufferSize)
        {
            return;
        }

        long suggested = Math.Max((long) Math.Round(average * 1.1 / 1000d) * 1000, v.ReadBufferSize + 1000);

        hints.Add(new(
            ConnectionHintIds.Buffer,
            "Read Buffer Size",
            "The average row or document read here is about " + average.ToString(CultureInfo.InvariantCulture)
            + " bytes, larger than Read Buffer Size (" + v.ReadBufferSize.ToString(CultureInfo.InvariantCulture)
            + "). Npgsql's performance guide gives Read Buffer Size slightly above the typical row (its example: 18000 for 16 KB rows).",
            "Read Buffer Size=" + suggested.ToString(CultureInfo.InvariantCulture),
            "builder.ConnectionStringBuilder.ReadBufferSize = " + suggested.ToString(CultureInfo.InvariantCulture) + ";"));
    }

    private static void Prepare(ConnectionSettings settings, List<ConnectionHint> hints)
    {
        ConnectionValues v = settings.Values;

        if (v.MaxAutoPrepare > 0 && settings.PoolerSigns.Any(static x => x.Kind == PoolerSignKind.SupabaseTransaction))
        {
            hints.Add(new(
                ConnectionHintIds.Prepare,
                "Max Auto Prepare",
                "Port 6543 is Supabase's transaction-mode pooler. Supavisor does not support prepared statements (supabase/supavisor#239; the dedicated PgBouncer pooler can), so Max Auto Prepare can break there.",
                "Max Auto Prepare=0",
                "builder.ConnectionStringBuilder.MaxAutoPrepare = 0;"));
        }
        else if (v.MaxAutoPrepare == 0 && settings.PoolerSigns.Count == 0)
        {
            hints.Add(new(
                ConnectionHintIds.Prepare,
                "Max Auto Prepare",
                "Max Auto Prepare=0 leaves auto-preparation off. Turning it on can remove repeated planning of the statements an application runs often (the Statements page shows plan time when track_planning is on).",
                "Max Auto Prepare=20",
                "builder.ConnectionStringBuilder.MaxAutoPrepare = 20;"));
        }
    }

    private static void Reset(ConnectionSettings settings, List<ConnectionHint> hints)
    {
        ConnectionValues v = settings.Values;

        if (!v.Pooling || v.Bool("No Reset On Close") != false || !settings.PoolerSigns.Any(static x => x.IsPgBouncerFamily))
        {
            return;
        }

        hints.Add(new(
            ConnectionHintIds.Reset,
            "No Reset On Close",
            "Npgsql's compatibility page, for a connection through PgBouncer: specify No Reset On Close=true, because the DISCARD ALL Npgsql sends when a connection returns to its pool makes no sense in a pooler's transaction or statement mode (it also advises turning Npgsql's own pool off with Pooling=false).",
            "No Reset On Close=true",
            "builder.ConnectionStringBuilder.NoResetOnClose = true;"));
    }

    private static void Enlist(ConnectionValues v, List<ConnectionHint> hints)
    {
        if (v.Bool("Enlist") != true)
        {
            return;
        }

        hints.Add(new(
            ConnectionHintIds.Enlist,
            "Enlist",
            "Enlist=true (the default) makes Npgsql look for an ambient TransactionScope each time a connection is opened. Enlist=false skips that check (Npgsql's performance guide); keep it true only where System.Transactions is used.",
            "Enlist=false",
            "builder.ConnectionStringBuilder.Enlist = false;",
            AllSettingsOnly: true));
    }

    private static void CommandTimeout(ConnectionValues v, ConnectionObserved? observed, List<ConnectionHint> hints)
    {
        if (observed is null)
        {
            return;
        }

        List<string> server = [];

        if (observed.StatementTimeoutMs is > 0)
        {
            server.Add("statement_timeout " + Seconds(observed.StatementTimeoutMs.Value));
        }

        if (observed.TransactionTimeoutMs is > 0)
        {
            server.Add("transaction_timeout " + Seconds(observed.TransactionTimeoutMs.Value));
        }

        if (observed.IdleInTransactionTimeoutMs is > 0)
        {
            server.Add("idle_in_transaction_session_timeout " + Seconds(observed.IdleInTransactionTimeoutMs.Value));
        }

        if (server.Count == 0)
        {
            return;
        }

        int client = v.CommandTimeout;
        string clientText = client == 0 ? "Command Timeout=0 (Npgsql never cancels a command itself)" : "Command Timeout=" + client.ToString(CultureInfo.InvariantCulture) + " s";

        string winner = observed.StatementTimeoutMs is > 0 && (client == 0 || observed.StatementTimeoutMs.Value < client * 1000L)
            ? "the server's statement_timeout cancels a long command first"
            : client > 0
                ? "Npgsql's Command Timeout cancels a long command first"
                : "a long command is cut by the server's settings";

        hints.Add(new(
            ConnectionHintIds.CommandTimeout,
            "Command Timeout",
            clientText + "; the server starts a session with " + string.Join(", ", server) + ". Of the two, " + winner + ".",
            null,
            null));

        static string Seconds(long ms) => ms % 1000 == 0 ? (ms / 1000).ToString(CultureInfo.InvariantCulture) + " s" : ms.ToString(CultureInfo.InvariantCulture) + " ms";
    }

    private static void ColdStart(ConnectionValues v, PostgresPlatformKind platform, List<ConnectionHint> hints)
    {
        if (platform != PostgresPlatformKind.Neon || v.Timeout >= 5)
        {
            return;
        }

        hints.Add(new(
            ConnectionHintIds.ColdStart,
            "Timeout",
            "Neon scales a compute to zero after 5 minutes without activity by default, and waking it takes about half a second to a few seconds (Neon docs, Connection latency and timeouts). A Timeout of "
            + v.Timeout.ToString(CultureInfo.InvariantCulture) + " s can expire before the compute is awake.",
            "Timeout=15",
            "builder.ConnectionStringBuilder.Timeout = 15;"));
    }

    private static void Auth(ConnectionValues v, ConnectionObserved? observed, List<ConnectionHint> hints)
    {
        bool md5 = string.Equals(observed?.PasswordEncryption, "md5", StringComparison.OrdinalIgnoreCase);
        bool noBinding = v.Is("Channel Binding", "Disable");

        if (!md5 && !noBinding)
        {
            return;
        }

        List<string> parts = [];

        if (md5)
        {
            parts.Add("the server's password_encryption is md5, so new passwords are stored as MD5 hashes (scram-sha-256 is the stronger default since PostgreSQL 14)");
        }

        if (noBinding)
        {
            parts.Add("Channel Binding=Disable switches off SCRAM channel binding, which protects a TLS connection against a man in the middle");
        }

        hints.Add(new(
            ConnectionHintIds.Auth,
            noBinding ? "Channel Binding" : null,
            "Authentication: " + string.Join("; ", parts) + ". This is a hint only: pg_hba.conf, which decides the method actually used, cannot be read without superuser.",
            noBinding ? "Channel Binding=Prefer" : null,
            noBinding ? "builder.ConnectionStringBuilder.ChannelBinding = ChannelBinding.Prefer;" : null));
    }
}
