using System.Globalization;
using System.Text;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Connection;

/// <summary>What <see cref="ConnectionPageBuilder.Build" /> is handed: the facts, the visitor's report, and the studio's contributions.</summary>
internal sealed record ConnectionPageInputs
{
    /// <summary>The redacted settings and what the server saw.</summary>
    public required ConnectionFacts Facts { get; init; }

    /// <summary>The visitor's Health report, or <see langword="null" /> when it could not be read.</summary>
    public HealthReport? Report { get; init; }

    /// <summary>Why <see cref="Report" /> is <see langword="null" />.</summary>
    public string? ReportNote { get; init; }

    /// <summary>The pool in this process, while somebody holds a lease on the observer.</summary>
    public SessionPoolSummary? Pool { get; init; }

    /// <summary>The pool's busiest moment over the connection history, for the visitor who may see applications.</summary>
    public ConnectionPoolPeak? PoolPeak { get; init; }

    /// <summary>What the studio's contributors added.</summary>
    public IReadOnlyList<ConnectionContribution> Contributions { get; init; } = [];

    /// <summary>The activity gate for "This application's sessions".</summary>
    public required SessionDetailsAccess Activity { get; init; }

    /// <summary>When the facts were read.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }
}

/// <summary>
/// The Connection page's model, built from the facts and the visitor's report (NP3). Pure: every sentence a visitor reads
/// on the page that is not a hint's or a check's own is in this file, and the unit tests hold each to its facts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here touches a connection string.</b> The facts hold <see cref="ConnectionSettings" />, which is redacted
/// (NP-DESIGN §2), and the observed values; this builder only arranges them.
/// </para>
/// <para>
/// <b>Findings and hints are two voices on one page.</b> The findings are the advisor's (C13 to C18, S05, S11 to S13, K04, K08),
/// gated and counted as the Health page gates and counts them; the hints are the page's own, informational and never counted by
/// the advisor. A hint is left out when a finding of the check that says the same is in the visitor's report, shown, accepted or
/// muted - the finding is the louder of the two - and a hint with no fix to make is a note, not "a thing worth a look".
/// </para>
/// </remarks>
internal static class ConnectionPageBuilder
{
    /// <summary>The codes of the checks that are about how the application connects, in no order.</summary>
    public static IReadOnlyList<string> FindingCodes { get; } =
        ["C13", "C14", "C15", "C16", "C17", "C18", "S05", "S11", "S12", "S13", "K04", "K08"];

    /// <summary>The settings a finding is about beyond the ones the catalogue links it to.</summary>
    private static readonly Dictionary<string, string[]> ExtraKeywords = new(StringComparer.Ordinal)
    {
        ["S05"] = ["SSL Mode"],
        ["K04"] = ["Host", "Port"],
        ["K08"] = ["Keepalive", "Connection Idle Lifetime"],
        ["C18"] = ["Maximum Pool Size"],
    };

    /// <summary>Hint ids a finding of a check says louder, so the hint is left out while the finding is in the report.</summary>
    private static readonly Dictionary<string, string[]> HintSpokenBy = new(StringComparer.Ordinal)
    {
        [ConnectionHintIds.Tls] = ["S11", "S05"],
        [ConnectionHintIds.Keepalive] = ["C16"],
    };

    /// <summary>The rows that say what the application connects with: they stay when the table shows only what differs.</summary>
    private static readonly HashSet<string> AlwaysShown = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Port", "Database", "Username", "Password", "SSL Mode", "Maximum Pool Size",
    };

    /// <summary>Builds the visitor's page.</summary>
    /// <param name="inputs">The facts, the report and the contributions.</param>
    public static ConnectionPageView Build(ConnectionPageInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        ConnectionFacts facts = inputs.Facts;
        ConnectionSettings settings = facts.Settings;
        ConnectionObserved? observed = facts.Observed;

        long? averageRow = inputs.Contributions.Select(static x => x.AverageRowBytes).FirstOrDefault(static x => x is not null);
        HealthReport? report = inputs.Report is { Refusal: HealthRefusal.None } ? inputs.Report : null;

        // POOLSIZE's caution (never advise lowering) turns on C03 reporting tight headroom.
        bool tight = report?.Findings.Any(static x => x.Code == "C03" && !x.Accepted) == true;

        IReadOnlyList<ConnectionHint> hints = ConnectionHints.Evaluate(settings, new ConnectionReadContext
        {
            Platform = facts.Platform.Kind,
            Observed = observed,
            PoolPeak = inputs.PoolPeak,
            TightHeadroom = tight,
            AverageRowBytes = averageRow,
        });

        // The findings the visitor is shown for the connection's codes, worst first. Accepted ones are counted apart.
        List<HealthReportFinding> findings =
        [
            .. (report?.Findings ?? [])
                .Where(static x => FindingCodes.Contains(x.Code, StringComparer.Ordinal))
                .OrderBy(static x => x.Severity)
                .ThenBy(static x => x.Code, StringComparer.Ordinal),
        ];
        HashSet<string> spoken = [.. findings.Select(static x => x.Code)];

        List<ConnectionCard> cards = [];
        List<ConnectionCard> notes = [];

        foreach (HealthReportFinding finding in findings.Where(static x => !x.Accepted))
        {
            cards.Add(FindingCard(finding, settings));
        }

        foreach (ConnectionHint hint in hints)
        {
            if (HintSpokenBy.TryGetValue(hint.Id, out string[]? codes) && codes.Any(spoken.Contains))
            {
                continue;
            }

            ConnectionCard card = HintCard(hint, settings, tight);

            (card.Counts ? cards : notes).Add(card);
        }

        foreach (ConnectionContribution contribution in inputs.Contributions)
        {
            cards.AddRange(contribution.Cards);
        }

        IReadOnlyList<ConnectionCard> everyCard = [.. cards, .. notes, .. inputs.Contributions.SelectMany(static x => x.Sections).SelectMany(static x => x.Cards)];
        List<ConnectionSettingView> rows = [.. settings.Rows.Select(row => SettingView(row, everyCard))];

        ConnectionTls tls = TlsOf(facts);
        string? withheldReason = report is { WithheldCount: > 0 } ? report.WithheldReason : null;

        return new ConnectionPageView
        {
            MeasuredAt = inputs.MeasuredAt,
            Header = Header(facts, tls, inputs),
            Tls = tls,
            PoolMax = settings.Readable ? settings.Values.Pooling ? settings.Values.MaxPoolSize : null : null,
            Readable = settings.Readable,
            UnreadableReason = settings.UnreadableReason,
            NpgsqlVersion = settings.NpgsqlVersion,
            Cards = cards,
            Notes = notes,
            AcceptedFindings = findings.Count(static x => x.Accepted),
            FindingsWithheld = report?.WithheldCount ?? 0,
            FindingsWithheldReason = withheldReason,
            FindingsNote = inputs.Report is { Refusal: not HealthRefusal.None } refused ? refused.Reason : inputs.ReportNote,
            Settings = rows,
            Observed = ObservedOf(facts),
            SettingsNotes = settings.Notes,
            NotVisible = NotVisible(),
            Contributed = [.. inputs.Contributions.SelectMany(static x => x.Sections)],
            Activity = inputs.Activity,
            Statements = [ObservedStatement()],
        };
    }

    // -------------------------------------------------------------------------------------------------
    // Header
    // -------------------------------------------------------------------------------------------------

    private static List<ConnectionFact> Header(ConnectionFacts facts, ConnectionTls tls, ConnectionPageInputs inputs)
    {
        ConnectionSettings settings = facts.Settings;
        ConnectionObserved? observed = facts.Observed;
        List<ConnectionFact> header = [];

        if (settings.Readable)
        {
            header.Add(new("Server", Endpoints(settings), RowAnchor("Host")));
            header.Add(new("Database", observed?.Database ?? settings.Values.Text("Database") ?? "not set", RowAnchor("Database")));
            header.Add(new("Role", observed?.SessionUser ?? settings.Values.Text("Username") ?? "not set", RowAnchor("Username")));
        }
        else
        {
            header.Add(new("Server", "not readable", "conn-settings"));
        }

        header.Add(new("TLS", tls.Text, RowAnchor("SSL Mode"), TlsTone(tls.State)));

        if (settings.Readable)
        {
            header.Add(new("Pool", PoolText(settings, inputs.Pool, inputs.PoolPeak), RowAnchor("Maximum Pool Size")));
        }

        header.Add(new("Npgsql", settings.NpgsqlVersion, "conn-settings"));
        header.Add(new("Platform", PlatformText(facts), RowAnchor("Host")));
        return header;
    }

    private static string Endpoints(ConnectionSettings settings)
    {
        if (settings.Endpoints.Count == 0)
        {
            return "not set";
        }

        return string.Join(", ", settings.Endpoints.Select(static x => ConnectionHosts.IsSocket(x.Host)
            ? "Unix socket " + x.Host
            : x.Host + ":" + x.Port.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>"max 100 · 3 in use, 2 idle · peak 41 in the last 7 days" - or why there is no pool to speak of.</summary>
    internal static string PoolText(ConnectionSettings settings, SessionPoolSummary? pool, ConnectionPoolPeak? peak)
    {
        ConnectionValues v = settings.Values;

        if (!v.Pooling)
        {
            return "no pool (Pooling=false)";
        }

        List<string> parts = ["max " + v.MaxPoolSize.ToString(CultureInfo.InvariantCulture)];

        if (pool is { Matched: true, Used: { } used, Idle: { } idle })
        {
            parts.Add(used.ToString(CultureInfo.InvariantCulture) + " in use, " + idle.ToString(CultureInfo.InvariantCulture) + " idle");
        }
        else if (pool is { Ambiguous: true })
        {
            parts.Add("live numbers not told apart: more than one pool in this process uses this connection string");
        }

        if (peak is not null)
        {
            parts.Add("peak " + peak.Peak.ToString(CultureInfo.InvariantCulture) + " in the last " + peak.Window);
        }

        return string.Join(" · ", parts);
    }

    private static string PlatformText(ConnectionFacts facts)
    {
        List<string> parts = [];

        if (facts.Platform.IsManaged)
        {
            parts.Add(facts.Platform.Name);
        }

        foreach (PoolerSign sign in facts.Settings.PoolerSigns)
        {
            string name = PoolerName(sign.Kind);

            if (!parts.Contains(name))
            {
                parts.Add(name);
            }
        }

        return parts.Count == 0 ? "no managed platform or pooler detected" : string.Join(" · ", parts);
    }

    /// <summary>The pooler a sign points at, in words.</summary>
    internal static string PoolerName(PoolerSignKind kind) => kind switch
    {
        PoolerSignKind.NeonPooler => "Neon pooled endpoint",
        PoolerSignKind.SupabaseTransaction => "Supabase transaction-mode pooler",
        PoolerSignKind.AzurePgBouncer => "Azure built-in PgBouncer",
        PoolerSignKind.RdsProxy => "Amazon RDS Proxy",
        _ => "a PgBouncer-style pooler (port 6432)",
    };

    private static string TlsTone(ConnectionTlsState state) => state switch
    {
        ConnectionTlsState.Verified or ConnectionTlsState.EncryptedByProxy or ConnectionTlsState.EncryptedGss or ConnectionTlsState.NotEncryptedLocal => "ok",
        ConnectionTlsState.EncryptedNotVerified or ConnectionTlsState.NotEncrypted => "warn",
        _ => "none",
    };

    /// <summary>
    /// What the TLS fact says: from the server's own view of the studio's backend (<c>pg_stat_ssl</c>) and the settings. "Not
    /// visible" is never "not encrypted" (a narrowed role cannot see its own row), and a Cloud SQL Auth Proxy is encrypted onward.
    /// </summary>
    /// <param name="facts">The connection facts.</param>
    internal static ConnectionTls TlsOf(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        ConnectionObserved? observed = facts.Observed;
        ConnectionSettings settings = facts.Settings;

        if (observed is null)
        {
            return new(ConnectionTlsState.NotRead, "not read", facts.ObservedNotRead is { } why
                ? "The server was not asked: " + why + "."
                : "The server was not asked.");
        }

        string mode = settings.Readable ? settings.Values.SslMode : "Prefer";

        if (observed.Ssl is true)
        {
            string protocol = observed.SslVersion is { Length: > 0 } version ? version : "TLS";

            return settings.Readable && !settings.Values.CertificateNotVerified
                ? new(ConnectionTlsState.Verified, "verified (" + mode + ")", protocol + ", and the server's certificate is checked (SSL Mode=" + mode + ").")
                : new(
                    ConnectionTlsState.EncryptedNotVerified,
                    "encrypted, not verified",
                    protocol + ", but the server's certificate is not checked (SSL Mode=" + mode + "), so anything between here and the server can pretend to be it.");
        }

        if (observed.Ssl is false)
        {
            if (observed.GssEncrypted is true)
            {
                return new(ConnectionTlsState.EncryptedGss, "encrypted (GSS)", "Not TLS, but the connection is encrypted with GSSAPI.");
            }

            if (facts.IsCloudSqlProxyCase)
            {
                return new(
                    ConnectionTlsState.EncryptedByProxy,
                    "encrypted onward by the Cloud SQL Auth Proxy",
                    "The server sees no TLS because the Cloud SQL Auth Proxy wraps its leg to the instance in TLS itself; the leg from the application to the proxy is not encrypted.");
            }

            if (settings.Readable && settings.Endpoints.Count > 0 && settings.RemoteEndpoints.Count == 0)
            {
                return new(ConnectionTlsState.NotEncryptedLocal, "not encrypted (local)", "The host is a socket or loopback, so the connection never leaves this machine.");
            }

            return new(ConnectionTlsState.NotEncrypted, "not encrypted", "The server reports no TLS on this connection (SSL Mode=" + mode + ").");
        }

        return new(
            ConnectionTlsState.NotVisible,
            "not visible to this role",
            "The server shows the reading role no pg_stat_ssl row for its own connection, so the studio cannot tell whether it is encrypted.");
    }

    // -------------------------------------------------------------------------------------------------
    // Cards
    // -------------------------------------------------------------------------------------------------

    private static ConnectionCard FindingCard(HealthReportFinding finding, ConnectionSettings settings)
    {
        HealthRemedy? remedy = finding.RemedyAvailable ? finding.Finding.Remedy : null;

        return new ConnectionCard(
            "conn-card-" + finding.Anchor,
            ConnectionCardKind.Finding,
            finding.Finding.Title,
            finding.Finding.WhyItMatters,
            [finding.Code],
            KeywordsFor(finding.Code, settings),
            remedy is null ? [] : [new ConnectionFix(remedy.Heading, remedy.Code)])
        {
            Finding = finding,
            Code = finding.Code,
        };
    }

    private static ConnectionCard HintCard(ConnectionHint hint, ConnectionSettings settings, bool tight)
    {
        List<ConnectionFix> fixes = [];

        if (hint.ConnectionStringFix is { Length: > 0 } connectionString)
        {
            fixes.Add(new("Connection string", connectionString));
        }

        if (hint.BuilderFix is { Length: > 0 } builder)
        {
            fixes.Add(new("NpgsqlDataSourceBuilder", builder));
        }

        // A fix that is only a sentence ("Remove X from the connection string.") is not a thing to copy.
        if (hint.Id == ConnectionHintIds.Obsolete)
        {
            fixes.RemoveAll(static x => x.Code.StartsWith("Remove ", StringComparison.Ordinal));
        }

        // Context is not advice: where the connection is a socket, which timeout cancels first, the ambient-transaction check,
        // auto-preparation left off, and a pool peak that gives no reason to change the size. They are shown, and not counted.
        bool contextOnly = hint.Id is ConnectionHintIds.Local or ConnectionHintIds.CommandTimeout or ConnectionHintIds.Enlist
            || (hint.Id == ConnectionHintIds.Prepare && settings.Values.MaxAutoPrepare == 0)
            || (hint.Id == ConnectionHintIds.PoolSize && !tight);

        return new ConnectionCard(
            "conn-card-hint-" + Slug(hint.Id + (hint.Id == ConnectionHintIds.Obsolete ? "-" + hint.Keyword : string.Empty)),
            contextOnly || hint.AllSettingsOnly ? ConnectionCardKind.Note : ConnectionCardKind.Hint,
            HintHeading(hint, settings),
            hint.Text,
            [hint.Id],
            hint.Id == ConnectionHintIds.Obsolete || hint.Id == ConnectionHintIds.Auth
                ? hint.Keyword is { } only ? [only] : []
                : KeywordsFor(hint.Id, settings, hint.Keyword),
            fixes)
        {
            Code = hint.Id,
            AllSettingsOnly = hint.AllSettingsOnly,
        };
    }

    /// <summary>One sentence for a hint's card: what is the matter, before the hint's own longer text.</summary>
    internal static string HintHeading(ConnectionHint hint, ConnectionSettings settings) => hint.Id switch
    {
        ConnectionHintIds.Tls => settings.Values.Is("SSL Mode", "Disable")
            ? "This connection is never encrypted"
            : "The server's certificate is not verified",
        ConnectionHintIds.Lifetime => "Connections never recycle",
        ConnectionHintIds.MultiHost => "Writes can land on a standby",
        ConnectionHintIds.Keepalive => "Idle connections can be dropped without the pool noticing",
        ConnectionHintIds.PoolSize => "How busy the pool gets, against its size",
        ConnectionHintIds.Buffer => "Rows are larger than the read buffer",
        ConnectionHintIds.Prepare => settings.Values.MaxAutoPrepare > 0
            ? "Auto-prepare can break behind this pooler"
            : "Auto-preparation is off",
        ConnectionHintIds.Reset => "Npgsql resets connections the pooler does not want reset",
        ConnectionHintIds.Enlist => "Every open checks for an ambient transaction",
        ConnectionHintIds.CommandTimeout => "Which timeout cancels a long command first",
        ConnectionHintIds.ColdStart => "The connect timeout may be shorter than a cold start",
        ConnectionHintIds.Auth => "Weaker authentication settings",
        ConnectionHintIds.Multiplexing => "Multiplexing is experimental",
        ConnectionHintIds.Obsolete => (hint.Keyword ?? "A setting") + " is obsolete",
        ConnectionHintIds.Local => "Connected over a Unix socket",
        _ => hint.Id,
    };

    /// <summary>The keywords a card is about: the ones the catalogue links to its id, the finding's extras, and the hint's own.</summary>
    private static IReadOnlyList<string> KeywordsFor(string id, ConnectionSettings settings, string? own = null)
    {
        List<string> keywords = [];

        if (own is not null)
        {
            keywords.Add(own);
        }

        keywords.AddRange(settings.Rows.Where(x => x.HintIds.Contains(id, StringComparer.Ordinal)).Select(static x => x.Keyword));

        if (ExtraKeywords.TryGetValue(id, out string[]? extra))
        {
            keywords.AddRange(extra);
        }

        // Keywords the table has: an anchor into a row that is not there would go nowhere.
        return [.. keywords.Distinct(StringComparer.OrdinalIgnoreCase).Where(k => settings.Rows.Any(r => string.Equals(r.Keyword, k, StringComparison.OrdinalIgnoreCase)))];
    }

    // -------------------------------------------------------------------------------------------------
    // The settings table
    // -------------------------------------------------------------------------------------------------

    private static ConnectionSettingView SettingView(ConnectionSettingRow row, IReadOnlyList<ConnectionCard> cards) => new(
        row,
        RowAnchor(row.Keyword),
        FromText(row),
        cards.FirstOrDefault(card => card.Keywords.Contains(row.Keyword, StringComparer.OrdinalIgnoreCase)),
        AlwaysShown.Contains(row.Keyword));

    /// <summary>The text of a row's "From" badge.</summary>
    internal static string FromText(ConnectionSettingRow row) => row.Origin switch
    {
        ConnectionSettingOrigin.ConnectionString => "connection string",
        ConnectionSettingOrigin.Environment => row.EnvironmentVariable ?? "environment",
        ConnectionSettingOrigin.NotVisible => "not visible",
        ConnectionSettingOrigin.Withheld => "withheld",
        ConnectionSettingOrigin.Unreadable => "unreadable",
        _ => "default",
    };

    /// <summary>The element id of a setting's row.</summary>
    /// <param name="keyword">A keyword.</param>
    internal static string RowAnchor(string keyword) => "conn-row-" + Slug(keyword);

    /// <summary>Lower-case letters and digits, anything else a dash: a keyword or a category as an element id.</summary>
    /// <param name="text">The text.</param>
    internal static string Slug(string text)
    {
        StringBuilder slug = new(text.Length);

        foreach (char c in text)
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return slug.ToString().Trim('-');
    }

    // -------------------------------------------------------------------------------------------------
    // What the server saw
    // -------------------------------------------------------------------------------------------------

    /// <summary>The parameters the server reports at start-up that the page shows, in order.</summary>
    private static readonly string[] ReportedToShow =
    [
        "server_version", "server_encoding", "in_hot_standby", "default_transaction_read_only", "is_superuser",
        "integer_datetimes", "standard_conforming_strings", "IntervalStyle", "scram_iterations",
    ];

    private static ConnectionObservedView ObservedOf(ConnectionFacts facts)
    {
        if (facts.Observed is not { } o)
        {
            return new([], facts.ObservedNotRead ?? "The server was not asked.");
        }

        List<ConnectionObservedLine> lines =
        [
            new("Logged in as", o.SessionUser, "session_user: the role the application's connections log in as."),
        ];

        if (!string.Equals(o.CurrentUser, o.SessionUser, StringComparison.Ordinal))
        {
            lines.Add(new("Reads run as", o.CurrentUser, "The studio's reads run as SqlConsoleRole; the application's own statements do not."));
        }

        lines.Add(new("Database", o.Database));
        lines.Add(new("Server", o.ServerAddress is { } address
            ? address + (o.ServerPort is { } port ? ":" + port.ToString(CultureInfo.InvariantCulture) : string.Empty)
            : "Unix-domain socket"));
        lines.Add(new("Role of the server", o.InRecovery ? "standby (in recovery)" : "primary"));

        lines.Add(new("Encryption", o.Ssl switch
        {
            true => (o.SslVersion ?? "TLS") + (o.SslCipher is { } cipher ? ", " + cipher : string.Empty) + (o.SslBits is { } bits ? ", " + bits.ToString(CultureInfo.InvariantCulture) + " bits" : string.Empty),
            false => o.GssEncrypted is true ? "GSSAPI" : "none",
            null => "not visible to this role",
        }));

        if (o.GssAuthenticated is { } gss)
        {
            lines.Add(new("GSSAPI authentication", gss ? "yes" : "no"));
        }

        AddIfPresent(lines, "Time zone", o.TimeZone);
        AddIfPresent(lines, "Client encoding", o.ClientEncoding);
        AddIfPresent(lines, "Search path", o.SearchPath);
        lines.Add(new("idle_session_timeout", Timeout(o.IdleSessionTimeoutMs, "before PostgreSQL 14"), "Ends a session that sits idle this long."));
        lines.Add(new("statement_timeout", Timeout(o.StatementTimeoutMs, null), "The server cancels a statement that runs this long."));
        lines.Add(new("idle_in_transaction_session_timeout", Timeout(o.IdleInTransactionTimeoutMs, null), "Ends a session that sits idle inside a transaction this long."));
        lines.Add(new("transaction_timeout", Timeout(o.TransactionTimeoutMs, "before PostgreSQL 17"), "Ends a transaction that runs this long."));
        AddIfPresent(lines, "password_encryption", o.PasswordEncryption);

        foreach (string name in ReportedToShow)
        {
            if (o.ReportedParameters.TryGetValue(name, out string? value))
            {
                lines.Add(new("Reported at start-up: " + name, value));
            }
        }

        return new(lines, null);
    }

    private static void AddIfPresent(List<ConnectionObservedLine> lines, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            lines.Add(new(label, value));
        }
    }

    /// <summary>A server timeout in milliseconds as a person reads it: "off", "30 s", "1 h 30 min", or why there is none to read.</summary>
    internal static string Timeout(long? milliseconds, string? unavailable) => milliseconds switch
    {
        null => "not available" + (unavailable is null ? string.Empty : " " + unavailable),
        0 => "off",
        { } ms when ms % 1000 != 0 => ms.ToString(CultureInfo.InvariantCulture) + " ms",
        { } ms => SessionText.Duration(TimeSpan.FromMilliseconds(ms)),
    };

    /// <summary>What the page cannot see, because it is made in code and not in the connection string.</summary>
    private static List<ConnectionObservedLine> NotVisible() =>
    [
        new(
            "Passwords supplied by code",
            "A password provider (UsePasswordProvider, UsePeriodicPasswordProvider on the NpgsqlDataSourceBuilder - how Microsoft Entra ID and cloud IAM tokens reach Npgsql) leaves no trace in the connection string. The Password row says what the connection string holds, which for such an application is nothing."),
        new(
            "Certificates and how they are checked",
            "Client certificates, a root certificate and validation callbacks (UseClientCertificate, UseRootCertificate, UseUserCertificateValidationCallback, UseSslClientAuthenticationOptionsCallback) are set in code. They can change how the server's certificate is checked beyond what SSL Mode says, and the studio cannot tell whether one is registered."),
        new(
            "Logging, tracing and metrics",
            "UseLoggerFactory, EnableParameterLogging and ConfigureTracing decide what the driver writes down and exports; none is part of the connection string."),
        new(
            "Type mappings and plugins",
            "MapEnum, MapComposite, ConfigureTypeLoading, EnableDynamicJson and the plugin packages (NodaTime, NetTopologySuite) change what the driver sends and reads, and are registered in code."),
        new(
            "A script run on each new connection",
            "UsePhysicalConnectionInitializer runs SQL on every new physical connection, so a session the application uses may have settings this page's start-up values do not show."),
        new(
            "The default password file",
            "When no password is configured Npgsql may read "
            + (OperatingSystem.IsWindows() ? @"%APPDATA%\postgresql\pgpass.conf" : "~/.pgpass")
            + ", and the default certificate files in the same place. The studio never looks for them."),
    ];

    /// <summary>The statement the observed read runs, for "Show SQL" - the builder's own text and parameter names, never a value.</summary>
    private static HealthStatement ObservedStatement()
    {
        // A var, as the convention fence asks: the query is the builder's, and only the builder's folder names its type.
        var query = ConnectionObservedQueries.Observed([]);
        return new HealthStatement(query.Shown ?? query.Sql, query.ParameterNames);
    }
}
