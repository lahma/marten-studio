using System.Globalization;

namespace PostgresStudio.Services.Connection;

/// <summary>Where a setting's shown value came from, or why none is shown.</summary>
internal enum ConnectionSettingOrigin
{
    /// <summary>The data source's connection string says so.</summary>
    ConnectionString,

    /// <summary>The connection string does not, and the environment variable the loaded Npgsql falls back to does.</summary>
    Environment,

    /// <summary>Neither: Npgsql's default.</summary>
    Default,

    /// <summary>The value cannot be seen: Npgsql removes the password from the data source's connection string.</summary>
    NotVisible,

    /// <summary>The value is withheld by rule (a keyword the studio does not know).</summary>
    Withheld,

    /// <summary>The loaded Npgsql's getter threw; only the exception's type is kept.</summary>
    Unreadable,
}

/// <summary>One keyword of the connection string, already redacted: nothing here can be a secret.</summary>
/// <param name="Keyword">The keyword's canonical spelling.</param>
/// <param name="Category">Npgsql's <c>[Category]</c> for it (<c>Connection</c>, <c>Security</c>, <c>Pooling</c>, …).</param>
/// <param name="Origin">Where <paramref name="Value" /> came from.</param>
/// <param name="Value">What the page shows: the value, <c>set</c> / <c>not set</c> for a secret, or the reason none is shown.</param>
/// <param name="Exposure">How the catalogue lets the value be shown; <see langword="null" /> for a keyword it does not know.</param>
/// <param name="EnvironmentVariable">The variable the value came from, or the one Npgsql would fall back to when it is not set.</param>
/// <param name="Note">A sentence about this row, or <see langword="null" />.</param>
/// <param name="Obsolete">Npgsql's <c>[Obsolete]</c> message, or <see langword="null" />.</param>
/// <param name="IsExplicit">Whether the connection string sets this keyword.</param>
/// <param name="HintIds">The hints and checks that are about this keyword.</param>
/// <param name="DefaultValue">Npgsql's own default for the keyword, formatted as <paramref name="Value" /> is (<c>not set</c> for none); <see langword="null" /> for a keyword the studio does not know.</param>
/// <param name="BuilderProperty">The loaded <c>NpgsqlConnectionStringBuilder</c> property that carries the keyword, <c>MaxPoolSize</c>.</param>
internal sealed record ConnectionSettingRow(
    string Keyword,
    string Category,
    ConnectionSettingOrigin Origin,
    string Value,
    NpgsqlSettingExposure? Exposure,
    string? EnvironmentVariable,
    string? Note,
    string? Obsolete,
    bool IsExplicit,
    IReadOnlyList<string> HintIds,
    string? DefaultValue = null,
    string? BuilderProperty = null);

/// <summary>
/// The effective, non-secret values of the settings the hints and checks read: the connection string over the
/// environment over Npgsql's default. Holds no keyword the catalogue marks <see cref="NpgsqlSettingExposure.Secret" /> or
/// <see cref="NpgsqlSettingExposure.Options" />.
/// </summary>
internal sealed class ConnectionValues
{
    private readonly IReadOnlyDictionary<string, object?> values;

    /// <summary>Wraps effective values, keyed by canonical keyword (any case).</summary>
    /// <param name="values">Keyword to value.</param>
    public ConnectionValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        this.values = new Dictionary<string, object?>(values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>No values: what an unreadable connection string has.</summary>
    public static ConnectionValues Empty { get; } = new(new Dictionary<string, object?>(0));

    /// <summary>Every effective value, by canonical keyword: for the page, the snapshot and the sentinel tests.</summary>
    public IReadOnlyDictionary<string, object?> All => values;

    /// <summary>The value of <paramref name="keyword" /> as text (an enum by its name), or <see langword="null" /> when it has none.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    public string? Text(string keyword) => values.GetValueOrDefault(keyword) switch
    {
        null => null,
        string { Length: 0 } => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        object other => other.ToString(),
    };

    /// <summary>The value of <paramref name="keyword" /> as an integer, or <see langword="null" />.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    public int? Int(string keyword) => values.GetValueOrDefault(keyword) switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int) number,
        _ => null,
    };

    /// <summary>The value of <paramref name="keyword" /> as a flag, or <see langword="null" />.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    public bool? Bool(string keyword) => values.GetValueOrDefault(keyword) as bool?;

    /// <summary>Whether the effective value of <paramref name="keyword" /> is <paramref name="expected" />, ignoring case.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    /// <param name="expected">The text to compare to.</param>
    public bool Is(string keyword, string expected) => string.Equals(Text(keyword), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>Pooling</c> (Npgsql's default: on).</summary>
    public bool Pooling => Bool("Pooling") ?? true;

    /// <summary><c>Minimum Pool Size</c> (default 0).</summary>
    public int MinPoolSize => Int("Minimum Pool Size") ?? 0;

    /// <summary><c>Maximum Pool Size</c> (default 100).</summary>
    public int MaxPoolSize => Int("Maximum Pool Size") ?? 100;

    /// <summary><c>Connection Idle Lifetime</c>, seconds (default 300).</summary>
    public int ConnectionIdleLifetime => Int("Connection Idle Lifetime") ?? 300;

    /// <summary><c>Connection Lifetime</c>, seconds (default 3600; 0 is never).</summary>
    public int ConnectionLifetime => Int("Connection Lifetime") ?? 3600;

    /// <summary><c>Keepalive</c>, seconds (default 0: off).</summary>
    public int Keepalive => Int("Keepalive") ?? 0;

    /// <summary><c>Timeout</c>, seconds (default 15).</summary>
    public int Timeout => Int("Timeout") ?? 15;

    /// <summary><c>Command Timeout</c>, seconds (default 30; 0 is none).</summary>
    public int CommandTimeout => Int("Command Timeout") ?? 30;

    /// <summary><c>Read Buffer Size</c>, bytes (default 8192).</summary>
    public int ReadBufferSize => Int("Read Buffer Size") ?? 8192;

    /// <summary><c>Max Auto Prepare</c> (default 0: off).</summary>
    public int MaxAutoPrepare => Int("Max Auto Prepare") ?? 0;

    /// <summary><c>SSL Mode</c>, by name (Npgsql's default: <c>Prefer</c>).</summary>
    public string SslMode => Text("SSL Mode") ?? "Prefer";

    /// <summary>Whether <c>SSL Mode</c> is below <c>VerifyCA</c>: the server's certificate is not checked.</summary>
    public bool CertificateNotVerified => SslMode.ToLowerInvariant() is "disable" or "allow" or "prefer" or "require";
}

/// <summary>One server setting named in <c>Options</c>.</summary>
/// <param name="Name">The setting's name, as written (a dash in it read as an underscore).</param>
/// <param name="Value">The value, or <see langword="null" /> when it is withheld.</param>
/// <param name="WithheldBecause">Why the value is withheld, or <see langword="null" /> when it is shown.</param>
internal sealed record ConnectionOption(string Name, string? Value, string? WithheldBecause);

/// <summary>What a connection string's (or <c>PGOPTIONS</c>'s) <c>Options</c> holds, with values shown only for server settings.</summary>
/// <param name="Items">Each setting named, in order.</param>
/// <param name="OtherWithheld">Whether text remained that did not parse as <c>-c name=value</c> or <c>--name=value</c>.</param>
internal sealed record ConnectionOptions(IReadOnlyList<ConnectionOption> Items, bool OtherWithheld)
{
    /// <summary>The names, for the observed read's existence check.</summary>
    public IReadOnlyList<string> Names => [.. Items.Select(static x => x.Name)];

    /// <summary>The options as the row shows them: <c>-c name=value -c other=(value withheld) (other options withheld)</c>.</summary>
    public string Render()
    {
        List<string> parts = [.. Items.Select(static x => "-c " + x.Name + "=" + (x.Value ?? "(value withheld)"))];

        if (OtherWithheld)
        {
            parts.Add("(other options withheld)");
        }

        return parts.Count == 0 ? "not set" : string.Join(' ', parts);
    }
}

/// <summary>
/// A sentence of advice about the settings (NP-DESIGN §6): informational, never a finding. The page shows it; the checks
/// of NP-DESIGN §7 are the ones that rank and count.
/// </summary>
/// <param name="Id">The hint's id (<see cref="ConnectionHintIds" />).</param>
/// <param name="Keyword">The keyword it is about, or <see langword="null" />.</param>
/// <param name="Text">One or two sentences.</param>
/// <param name="ConnectionStringFix">The change as a connection-string fragment, or <see langword="null" />.</param>
/// <param name="BuilderFix">The change in code, or <see langword="null" />.</param>
/// <param name="AllSettingsOnly">Whether the page shows the hint only in its "all settings" view.</param>
internal sealed record ConnectionHint(
    string Id,
    string? Keyword,
    string Text,
    string? ConnectionStringFix,
    string? BuilderFix,
    bool AllSettingsOnly = false);

/// <summary>The ids of the hints (NP-DESIGN §6).</summary>
internal static class ConnectionHintIds
{
    /// <summary>The server's certificate is not checked.</summary>
    public const string Tls = "TLS";

    /// <summary>Connections never recycle.</summary>
    public const string Lifetime = "LIFETIME";

    /// <summary>Several hosts without a target session attribute.</summary>
    public const string MultiHost = "MULTIHOST";

    /// <summary>No keepalive where something cuts idle connections.</summary>
    public const string Keepalive = "KEEPALIVE";

    /// <summary>What the pool's peak says about its size.</summary>
    public const string PoolSize = "POOLSIZE";

    /// <summary>A row larger than the read buffer.</summary>
    public const string Buffer = "BUFFER";

    /// <summary>Auto-preparation and the pooler in front of it.</summary>
    public const string Prepare = "PREPARE";

    /// <summary>Npgsql's reset behind PgBouncer.</summary>
    public const string Reset = "RESET";

    /// <summary>The ambient-transaction check on every open.</summary>
    public const string Enlist = "ENLIST";

    /// <summary>Which timeout cancels first.</summary>
    public const string CommandTimeout = "CMDTIMEOUT";

    /// <summary>A serverless compute waking up.</summary>
    public const string ColdStart = "COLDSTART";

    /// <summary>Weak authentication settings.</summary>
    public const string Auth = "AUTH";

    /// <summary>Multiplexing.</summary>
    public const string Multiplexing = "MUX";

    /// <summary>A keyword Npgsql has marked obsolete.</summary>
    public const string Obsolete = "OBSOLETE";

    /// <summary>A Unix-domain socket.</summary>
    public const string Local = "LOCAL";
}

/// <summary>
/// The most sessions the connection history counted under this application's <c>Application Name</c> on the server (C15's history),
/// for the POOLSIZE hint. <b>Not this pool's peak:</b> the history counts sessions per server by <c>application_name</c>, so it
/// includes every process, replica and database that uses the name.
/// </summary>
/// <param name="Peak">The most sessions named <paramref name="Application" /> at once.</param>
/// <param name="Max">This pool's <c>Maximum Pool Size</c>.</param>
/// <param name="Window">The window the peak is over, in words ("7 days").</param>
/// <param name="Application">The <c>Application Name</c> the sessions were counted under.</param>
internal sealed record ConnectionPoolPeak(int Peak, int Max, string Window, string Application);

/// <summary>What the reader is given besides the connection string: the environment, the loaded Npgsql, and the facts the hints need.</summary>
internal sealed record ConnectionReadContext
{
    /// <summary>The process environment; the real one by default.</summary>
    public IProcessEnvironment Environment { get; init; } = ProcessEnvironment.Instance;

    /// <summary>The loaded Npgsql's major version; the loaded assembly's by default.</summary>
    public int? NpgsqlMajor { get; init; }

    /// <summary>
    /// The names among the options that exist in <c>pg_settings</c>, from the observed read; <see langword="null" /> when
    /// the server was not asked, which withholds every option's value.
    /// </summary>
    public IReadOnlySet<string>? ServerSettingNames { get; init; }

    /// <summary>The platform, for the hints' wording.</summary>
    public PostgresPlatformKind Platform { get; init; } = PostgresPlatformKind.SelfHosted;

    /// <summary>What the server saw, or <see langword="null" /> when it was not read.</summary>
    public ConnectionObserved? Observed { get; init; }

    /// <summary>The pool's history peak, when known (POOLSIZE).</summary>
    public ConnectionPoolPeak? PoolPeak { get; init; }

    /// <summary>Whether the server's connection headroom is tight (C03): the only case POOLSIZE speaks of lowering.</summary>
    public bool TightHeadroom { get; init; }

    /// <summary>The average row or document size in bytes, from T09 or E04 (BUFFER), when known.</summary>
    public long? AverageRowBytes { get; init; }
}

/// <summary>
/// The connection settings of one data source, already redacted (NP-DESIGN §2): every row, the effective values the
/// checks read, the pooler signs and the hints. Nothing in it can be the connection string or any part of a secret.
/// </summary>
internal sealed record ConnectionSettings
{
    /// <summary>The loaded Npgsql's version, <c>9.0.4</c>.</summary>
    public required string NpgsqlVersion { get; init; }

    /// <summary>The loaded Npgsql's major version.</summary>
    public required int NpgsqlMajor { get; init; }

    /// <summary>The loaded Npgsql is newer than the studio's tables: its environment fallbacks may be incomplete.</summary>
    public bool NewerThanStudioKnows { get; init; }

    /// <summary>Whether the connection string parsed; when not, <see cref="Rows" /> is empty and <see cref="UnreadableReason" /> says why.</summary>
    public bool Readable { get; init; } = true;

    /// <summary>The exception type that stopped the parse (never its message, which can quote the value), or <see langword="null" />.</summary>
    public string? UnreadableReason { get; init; }

    /// <summary>Every keyword, in the page's order.</summary>
    public IReadOnlyList<ConnectionSettingRow> Rows { get; init; } = [];

    /// <summary>The effective non-secret values.</summary>
    public ConnectionValues Values { get; init; } = ConnectionValues.Empty;

    /// <summary>The hosts with their ports.</summary>
    public IReadOnlyList<(string Host, int Port)> Endpoints { get; init; } = [];

    /// <summary>What in the hosts and ports looks like a pooler.</summary>
    public IReadOnlyList<PoolerSign> PoolerSigns { get; init; } = [];

    /// <summary>Whether <c>Persist Security Info</c> is on, so the data source's connection string keeps the password.</summary>
    public bool PersistSecurityInfo { get; init; }

    /// <summary>Whether <c>SSL Password</c> is set (the connection string keeps it whatever <c>Persist Security Info</c> says).</summary>
    public bool SslPasswordSet { get; init; }

    /// <summary>Whether <c>PGPASSWORD</c> is set in the process environment.</summary>
    public bool PasswordEnvironmentSet { get; init; }

    /// <summary>The parsed <c>Options</c>, or <see langword="null" /> when there are none.</summary>
    public ConnectionOptions? Options { get; init; }

    /// <summary>Sentences about the page as a whole: the default password file, a newer Npgsql.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The hints (NP-DESIGN §6).</summary>
    public IReadOnlyList<ConnectionHint> Hints { get; init; } = [];

    /// <summary>The hosts that are neither a socket nor loopback.</summary>
    public IReadOnlyList<(string Host, int Port)> RemoteEndpoints => [.. Endpoints.Where(static x => !ConnectionHosts.IsLocal(x.Host))];

    /// <summary>Whether the data source names more than one host.</summary>
    public bool IsMultiHost => Endpoints.Count > 1;
}

/// <summary>What a host name says about where it is.</summary>
internal static class ConnectionHosts
{
    /// <summary>Whether <paramref name="host" /> is a Unix-domain socket directory (a rooted path, or an abstract <c>@</c> name).</summary>
    /// <param name="host">A host as the connection string writes it.</param>
    public static bool IsSocket(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.StartsWith('/') || host.StartsWith('@');
    }

    /// <summary>Whether <paramref name="host" /> is <c>localhost</c> or a loopback address.</summary>
    /// <param name="host">A host as the connection string writes it.</param>
    public static bool IsLoopback(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        string name = host.Trim().Trim('[', ']').TrimEnd('.');

        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(name, out System.Net.IPAddress? address)
                && System.Net.IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
    }

    /// <summary>Whether a connection to <paramref name="host" /> never leaves the machine: a socket or loopback.</summary>
    /// <param name="host">A host as the connection string writes it.</param>
    public static bool IsLocal(string host) => IsSocket(host) || IsLoopback(host);
}
