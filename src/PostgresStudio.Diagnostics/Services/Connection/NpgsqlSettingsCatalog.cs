namespace PostgresStudio.Services.Connection;

/// <summary>How a keyword's value may be shown: the catalogue decides, never the value (NP-DESIGN §2.1).</summary>
internal enum NpgsqlSettingExposure
{
    /// <summary>The value is shown as it is.</summary>
    Shown,

    /// <summary>A secret: reported as set or not set, never read out (<c>Password</c>, <c>SSL Password</c>).</summary>
    Secret,

    /// <summary>A file path: shown, and the file is never opened.</summary>
    Path,

    /// <summary><c>Options</c>: parsed, every name shown, a value only for a server setting (NP-DESIGN §2.4).</summary>
    Options,
}

/// <summary>One keyword of Npgsql's connection string, as the studio treats it.</summary>
/// <param name="Keyword">The keyword's canonical spelling, Npgsql's <c>[DisplayName]</c>.</param>
/// <param name="Exposure">How its value may be shown.</param>
/// <param name="EnvironmentVariable">The environment variable Npgsql falls back to at connect time, or <see langword="null" />.</param>
/// <param name="EnvironmentFromMajor">The first Npgsql major that reads <paramref name="EnvironmentVariable" />.</param>
/// <param name="MultiHostOnly">Whether the fallback applies only to a data source with more than one host (<c>PGTARGETSESSIONATTRS</c>).</param>
/// <param name="Hints">The ids of the hints (NP-DESIGN §6) and checks that are about this keyword.</param>
internal sealed record NpgsqlSettingEntry(
    string Keyword,
    NpgsqlSettingExposure Exposure,
    string? EnvironmentVariable,
    int EnvironmentFromMajor,
    bool MultiHostOnly,
    IReadOnlyList<string> Hints)
{
    /// <summary>The variable the given Npgsql major falls back to, or <see langword="null" /> when that major reads none.</summary>
    /// <param name="major">The loaded Npgsql's major version.</param>
    public string? EnvironmentFor(int major) => EnvironmentVariable is not null && major >= EnvironmentFromMajor ? EnvironmentVariable : null;
}

/// <summary>
/// The static table of Npgsql connection-string keywords the studio knows (NP-DESIGN §3): how each may be shown, which
/// environment variable the loaded Npgsql major falls back to for it, and which hints and checks are about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The catalogue decides what is shown.</b> A keyword the loaded Npgsql has and this table does not (a newer Npgsql)
/// is listed by name with its value withheld, so a future secret keyword cannot leak by being new
/// (<see cref="NpgsqlSettingsReader" />).
/// </para>
/// <para>
/// <b>Environment fallbacks</b> are Npgsql's <c>PostgresEnvironment</c> at the version named (read from the source at
/// <c>v9.0.4</c> and <c>v10.0.3</c>, 2026-11-03): 9.0.4 reads PGUSER, PGPASSWORD, PGPASSFILE, PGSSLCERT, PGSSLKEY,
/// PGSSLROOTCERT, PGCLIENTENCODING, PGTZ, PGOPTIONS, PGTARGETSESSIONATTRS and PGSSLNEGOTIATION; 10.0.3 adds PGGSSENCMODE,
/// PGREQUIREAUTH and PGAPPNAME. A connection string always wins over the environment (<c>Settings.X ?? PostgresEnvironment.X</c>
/// in <c>NpgsqlConnector</c>), and <c>PGTARGETSESSIONATTRS</c> is read only by the multi-host data source
/// (<c>NpgsqlMultiHostDataSource.GetTargetSessionAttributes</c>).
/// </para>
/// </remarks>
internal static class NpgsqlSettingsCatalog
{
    private static readonly NpgsqlSettingEntry[] EntriesInOrder =
    [
        Shown("Host", "MULTIHOST", "LOCAL"),
        Shown("Port"),
        Shown("Database"),
        ShownEnv("Username", "PGUSER"),
        new("Password", NpgsqlSettingExposure.Secret, "PGPASSWORD", 9, false, ["S12"]),
        new("Passfile", NpgsqlSettingExposure.Path, "PGPASSFILE", 9, false, []),
        ShownEnv("Application Name", "PGAPPNAME", 10, "C14"),
        Shown("Enlist", "ENLIST"),
        Shown("Search Path"),
        ShownEnv("Client Encoding", "PGCLIENTENCODING"),
        Shown("Encoding"),
        ShownEnv("Timezone", "PGTZ"),
        Shown("SSL Mode", "TLS", "S11"),
        ShownEnv("SSL Negotiation", "PGSSLNEGOTIATION"),
        ShownEnv("GSS Encryption Mode", "PGGSSENCMODE", 10),
        new("SSL Certificate", NpgsqlSettingExposure.Path, "PGSSLCERT", 9, false, []),
        new("SSL Key", NpgsqlSettingExposure.Path, "PGSSLKEY", 9, false, []),
        new("SSL Password", NpgsqlSettingExposure.Secret, null, 9, false, ["S12"]),
        new("Root Certificate", NpgsqlSettingExposure.Path, "PGSSLROOTCERT", 9, false, ["TLS"]),
        Shown("Check Certificate Revocation"),
        Shown("Kerberos Service Name"),
        Shown("Include Realm"),
        Shown("Persist Security Info", "S12"),
        Shown("Log Parameters", "S13"),
        Shown("Include Error Detail", "S13"),
        Shown("Include Failed Batched Command", "S13"),
        Shown("Channel Binding", "AUTH"),
        ShownEnv("Require Auth", "PGREQUIREAUTH", 10, "AUTH"),
        Shown("Pooling", "C17"),
        Shown("Minimum Pool Size", "C16", "C13"),
        Shown("Maximum Pool Size", "POOLSIZE", "C01", "C03", "C15"),
        Shown("Connection Idle Lifetime", "C16"),
        Shown("Connection Pruning Interval"),
        Shown("Connection Lifetime", "LIFETIME"),
        Shown("Timeout", "COLDSTART"),
        Shown("Command Timeout", "CMDTIMEOUT"),
        Shown("Cancellation Timeout"),
        new("Target Session Attributes", NpgsqlSettingExposure.Shown, "PGTARGETSESSIONATTRS", 9, true, ["MULTIHOST"]),
        Shown("Load Balance Hosts", "MULTIHOST"),
        Shown("Host Recheck Seconds"),
        Shown("Keepalive", "KEEPALIVE", "C16"),
        Shown("TCP Keepalive", "KEEPALIVE"),
        Shown("TCP Keepalive Time", "KEEPALIVE"),
        Shown("TCP Keepalive Interval", "KEEPALIVE"),
        Shown("Read Buffer Size", "BUFFER"),
        Shown("Write Buffer Size"),
        Shown("Socket Receive Buffer Size"),
        Shown("Socket Send Buffer Size"),
        Shown("Max Auto Prepare", "PREPARE"),
        Shown("Auto Prepare Min Usages"),
        Shown("No Reset On Close", "RESET"),
        Shown("Replication Mode"),
        new("Options", NpgsqlSettingExposure.Options, "PGOPTIONS", 9, false, []),
        Shown("Array Nullability Mode"),
        Shown("Multiplexing", "MUX"),
        Shown("Write Coalescing Buffer Threshold Bytes", "MUX"),
        Shown("Load Table Composites", "OBSOLETE"),
        Shown("Server Compatibility Mode", "OBSOLETE"),
        Shown("Trust Server Certificate", "OBSOLETE", "S11"),
        Shown("Internal Command Timeout", "OBSOLETE"),
    ];

    private static readonly Dictionary<string, NpgsqlSettingEntry> ByKeyword = EntriesInOrder.ToDictionary(static x => x.Keyword, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every keyword the studio knows, in the order the page lists them.</summary>
    public static IReadOnlyList<NpgsqlSettingEntry> Entries => EntriesInOrder;

    /// <summary>The entry of <paramref name="keyword" /> (any case), or <see langword="null" /> when the studio does not know it.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    public static NpgsqlSettingEntry? Find(string keyword)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        return ByKeyword.GetValueOrDefault(keyword);
    }

    /// <summary>The position of <paramref name="keyword" /> in the page's order, or <see cref="int.MaxValue" /> for a keyword the studio does not know.</summary>
    /// <param name="keyword">A canonical keyword.</param>
    public static int OrderOf(string keyword) => Array.FindIndex(EntriesInOrder, x => string.Equals(x.Keyword, keyword, StringComparison.OrdinalIgnoreCase)) is >= 0 and var index ? index : int.MaxValue;

    /// <summary>The environment variables the catalogue lists, for any Npgsql major - the only names the reader ever asks the environment for.</summary>
    public static IReadOnlyList<string> EnvironmentVariables { get; } =
        [.. EntriesInOrder.Where(static x => x.EnvironmentVariable is not null).Select(static x => x.EnvironmentVariable!).Distinct(StringComparer.Ordinal)];

    private static NpgsqlSettingEntry Shown(string keyword, params string[] hints) =>
        new(keyword, NpgsqlSettingExposure.Shown, null, 9, false, hints);

    private static NpgsqlSettingEntry ShownEnv(string keyword, string env, int fromMajor = 9, params string[] hints) =>
        new(keyword, NpgsqlSettingExposure.Shown, env, fromMajor, false, hints);
}
