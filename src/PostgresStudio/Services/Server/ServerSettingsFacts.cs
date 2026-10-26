using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Server;

/// <summary>One <c>pg_settings</c> row as the source's own sessions run with it.</summary>
/// <param name="Name">The setting.</param>
/// <param name="Value">Its value, as <c>pg_settings.setting</c> spells it (the reset value, for one the studio's own read sets).</param>
/// <param name="Unit">Its unit (<c>8kB</c>, <c>ms</c>), or <see langword="null" />.</param>
/// <param name="Source">Who set it (<c>default</c>, <c>configuration file</c>, <c>user</c>, ...), or <see langword="null" /> when that cannot be told.</param>
/// <param name="PendingRestart">Whether a change to it waits for the server to restart.</param>
/// <param name="Context">Its <c>pg_settings.context</c>: <c>postmaster</c>, <c>sighup</c>, <c>user</c>, ...</param>
/// <param name="BootValue">Its built-in value (<c>boot_val</c>).</param>
internal sealed record ServerSettingValue(string Name, string Value, string? Unit, string? Source, bool PendingRestart, string? Context, string? BootValue)
{
    /// <summary>
    /// Whether the studio's own read-only transaction sets this setting for itself, so that <see cref="Value" /> is the value a
    /// new session starts with (<c>reset_val</c>) and <see cref="Source" /> is the read's best account of who set that
    /// (<see cref="ServerPostureQueries" />).
    /// </summary>
    public bool StudioSet { get; init; }

    /// <summary>Whether it reads <c>on</c>.</summary>
    public bool IsOn => string.Equals(Value, "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether it reads <c>off</c>.</summary>
    public bool IsOff => string.Equals(Value, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A time setting as a duration - <see cref="TimeSpan.Zero" /> for <c>0</c>, which every timeout reads as "off" - or
    /// <see langword="null" /> for a value that is not a number of its unit.
    /// </summary>
    public TimeSpan? Duration => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
        ? Unit switch
        {
            "us" => TimeSpan.FromMicroseconds(number),
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" => TimeSpan.FromSeconds(number),
            "min" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => null,
        }
        : null;
}

/// <summary>The curated settings as one read returned them: <see cref="ServerPostureQueries.Settings" />.</summary>
/// <param name="Rows">The rows, by name.</param>
internal sealed record ServerSettingsRead(IReadOnlyList<ServerSettingValue> Rows)
{
    /// <summary>The setting called <paramref name="name" />, or <see langword="null" />.</summary>
    public ServerSettingValue? Find(string name) => Rows.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    /// <summary>The sweep's one read of it - or the page's, through its own context - whichever asks first.</summary>
    public static Task<ServerSettingsRead> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ServerPostureQueries.SettingsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it through the context's runner.</summary>
    public static async Task<ServerSettingsRead> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ServerPostureQueries.Settings(ServerSettingsCatalog.Names, ServerSettingsCatalog.Patterns);
        IReadOnlyList<(ServerSettingValue Setting, string? EntryValue)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (
                    new ServerSettingValue(
                        reader.GetString(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        !reader.IsDBNull(4) && reader.GetBoolean(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6))
                    {
                        StudioSet = !reader.IsDBNull(7) && reader.GetBoolean(7),
                    },
                    reader.IsDBNull(8) ? null : reader.GetString(8)),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlySet<string> startup = ServerSettingSource.StartupOptions(context.Target.DataSource?.ConnectionString);

        return new ServerSettingsRead([.. rows.Select(x => x.Setting.StudioSet
            ? x.Setting with { Source = ServerSettingSource.Resolve(x.Setting, x.EntryValue, startup) }
            : x.Setting)]);
    }
}

/// <summary>
/// Who set a setting the studio's own read-only transaction sets for itself - whose <c>reset_val</c> the read takes - in
/// the order Postgres applies them: the connection's startup options over a role-and-database, role, database or global
/// entry, over the server's configuration and the default. Pure, for the unit tests.
/// </summary>
internal static class ServerSettingSource
{
    /// <summary>
    /// The source of <paramref name="setting" />'s <c>reset_val</c>: <c>client</c> when the connection string's
    /// <c>Options</c> set it; the role or database entry's own source when the entry Postgres would apply has the value;
    /// <c>default</c> with no entry and the built-in value; otherwise <see langword="null" />, not known. That covers an
    /// entry whose value is not the value with no startup option to explain it - a <c>PGOPTIONS</c> in the host's
    /// environment, a pooler's startup parameters or an entry altered since the session began would each read so, and
    /// calling any of them "client" would name a setter the read never saw - and the server's configuration, its command
    /// line or an environment variable, which the read cannot tell apart.
    /// </summary>
    /// <param name="setting">The row, its <see cref="ServerSettingValue.Value" /> the <c>reset_val</c> and its source the entry's.</param>
    /// <param name="entryValue">The applied entry's value as <c>ALTER … SET</c> stored it, or <see langword="null" /> with no entry.</param>
    /// <param name="startupOptions">The settings the connection string's <c>Options</c> name (<see cref="StartupOptions" />).</param>
    public static string? Resolve(ServerSettingValue setting, string? entryValue, IReadOnlySet<string> startupOptions)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(startupOptions);

        if (startupOptions.Contains(setting.Name))
        {
            return "client";
        }

        if (entryValue is not null)
        {
            return Same(entryValue, setting.Value, setting.Unit) switch
            {
                true => setting.Source,
                _ => null,
            };
        }

        return string.Equals(setting.Value, setting.BootValue, StringComparison.Ordinal) ? "default" : null;
    }

    /// <summary>
    /// The settings an Npgsql connection string's <c>Options</c> set at start-up - <c>-c name=value</c>, <c>-cname=value</c>
    /// or <c>--name=value</c>, a dash in a name read as an underscore - or none when it has no options or will not parse.
    /// </summary>
    public static IReadOnlySet<string> StartupOptions(string? connectionString)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return names;
        }

        string? options;

        try
        {
            options = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Options;
        }
        catch (ArgumentException)
        {
            return names;
        }

        if (string.IsNullOrWhiteSpace(options))
        {
            return names;
        }

        string[] tokens = options.Split((char[]) [' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            string? assignment = token switch
            {
                "-c" when i + 1 < tokens.Length => tokens[++i],
                _ when token.StartsWith("--", StringComparison.Ordinal) => token[2..],
                _ when token.StartsWith("-c", StringComparison.Ordinal) && token.Length > 2 => token[2..],
                _ => null,
            };

            if (assignment is not null && assignment.IndexOf('=', StringComparison.Ordinal) is > 0 and var equals)
            {
                names.Add(assignment[..equals].Replace('-', '_').ToLowerInvariant());
            }
        }

        return names;
    }

    /// <summary>
    /// Whether an entry's value as written (<c>30s</c>, <c>'1min'</c>, <c>30000</c>) is the value <c>pg_settings</c> shows
    /// in <paramref name="unit" /> - <see langword="null" /> when it cannot be told.
    /// </summary>
    internal static bool? Same(string written, string shown, string? unit)
    {
        string entry = written.Trim().Trim('\'', '"').Trim();

        if (unit is null)
        {
            return string.Equals(entry, shown.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (Quantity(entry, unit) is not { } left || Quantity(shown.Trim(), unit) is not { } right)
        {
            return null;
        }

        return Math.Abs(left - right) <= Math.Max(1e-9, Math.Abs(right) * 1e-9);
    }

    /// <summary>A value in base units - microseconds for time, bytes for memory - with a bare number taken in <paramref name="unit" />.</summary>
    private static double? Quantity(string text, string unit)
    {
        int end = 0;

        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+'))
        {
            end++;
        }

        if (!double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return null;
        }

        string suffix = text[end..].Trim();
        return Scale(suffix.Length == 0 ? unit : suffix) is { } scale ? number * scale : null;
    }

    private static double? Scale(string unit) => unit switch
    {
        "us" => 1,
        "ms" => 1_000,
        "s" => 1_000_000,
        "min" => 60_000_000,
        "h" => 3_600_000_000,
        "d" => 86_400_000_000,
        "B" => 1,
        "kB" => 1024,
        "8kB" => 8192,
        "16kB" => 16384,
        "MB" => 1024d * 1024,
        "GB" => 1024d * 1024 * 1024,
        "TB" => 1024d * 1024 * 1024 * 1024,
        _ => null,
    };
}

/// <summary>How the connection the read ran on reached the server.</summary>
internal enum ServerConnectionKind
{
    /// <summary>A Unix-domain socket: no client address at all.</summary>
    UnixSocket,

    /// <summary>TCP from the server's own machine (<c>127.0.0.0/8</c>, <c>::1</c>).</summary>
    Loopback,

    /// <summary>TCP from anywhere else.</summary>
    Network,
}

/// <summary>
/// The source's own connection to the server: who it logs in as, who the reads run as, whether it is encrypted, and the
/// server facts that come with it - <see cref="ServerPostureQueries.Posture" />'s one row.
/// </summary>
/// <param name="LoginRole">The login: <c>session_user</c>, which the application's connections are too.</param>
/// <param name="LoginSuperuser">Whether the login is a superuser (<c>rolsuper</c>).</param>
/// <param name="LoginMemberOf">The posture roles the login is a member of (<see cref="ServerPostureQueries.PostureRoles" />).</param>
/// <param name="ReadingRole">The role the reads run as: <c>current_user</c>, <c>SqlConsoleRole</c> when one is set.</param>
/// <param name="ReadingSuperuser">Whether the reading role is a superuser.</param>
/// <param name="ReadingMemberOf">The posture roles the reading role is a member of.</param>
/// <param name="Ssl">Whether the backend's connection is encrypted, or <see langword="null" /> when not read.</param>
/// <param name="SslVersion">The TLS version, when it is.</param>
/// <param name="ClientAddress">
/// The address the connection came from, as the server saw it, or <see langword="null" /> for a Unix-domain socket. A fact
/// for <see cref="Kind" /> alone: never shown, because a client address is another kind of thing than a setting (D29).
/// </param>
/// <param name="PasswordValidUntil">When the login's password expires (<c>rolvaliduntil</c>), or <see langword="null" /> for never.</param>
/// <param name="InRecovery">Whether the server is a standby.</param>
/// <param name="ServerStarted">When the server started.</param>
/// <param name="DataChecksums"><c>data_checksums</c>: <c>on</c> or <c>off</c>.</param>
/// <param name="StatementsLoaded">Whether <c>pg_stat_statements</c>' library was loaded when the server started.</param>
internal sealed record ServerConnectionPosture(
    string LoginRole,
    bool LoginSuperuser,
    IReadOnlyList<string> LoginMemberOf,
    string ReadingRole,
    bool ReadingSuperuser,
    IReadOnlyList<string> ReadingMemberOf,
    bool? Ssl,
    string? SslVersion,
    string? ClientAddress,
    DateTimeOffset? PasswordValidUntil,
    bool InRecovery,
    DateTimeOffset? ServerStarted,
    string? DataChecksums,
    bool StatementsLoaded)
{
    /// <summary>How the connection came: a socket, the loopback interface, or the network.</summary>
    public ServerConnectionKind Kind => KindOf(ClientAddress);

    /// <summary>Whether the reads run as another role than the login - <c>SqlConsoleRole</c>.</summary>
    public bool ReadsAsAnotherRole => !string.Equals(LoginRole, ReadingRole, StringComparison.Ordinal);

    /// <summary>The managed platforms' admin roles the login is a member of - for a login that is not a superuser.</summary>
    public IReadOnlyList<string> LoginProviderAdminRoles =>
        LoginSuperuser ? [] : [.. LoginMemberOf.Where(static x => PostgresGrants.ProviderAdminRoles.Contains(x, StringComparer.Ordinal))];

    /// <summary>The file-access roles the login is a member of - for a login that is not a superuser.</summary>
    public IReadOnlyList<string> LoginFileAccessRoles =>
        LoginSuperuser ? [] : [.. LoginMemberOf.Where(static x => ServerPostureQueries.FileAccessRoles.Contains(x, StringComparer.Ordinal))];

    /// <summary>The file-access roles the reading role is a member of - for a reading role that is not a superuser.</summary>
    public IReadOnlyList<string> ReadingFileAccessRoles =>
        ReadingSuperuser ? [] : [.. ReadingMemberOf.Where(static x => ServerPostureQueries.FileAccessRoles.Contains(x, StringComparer.Ordinal))];

    /// <summary>How an address came: none is a socket; <c>127.0.0.0/8</c>, <c>::1</c> and their IPv4-mapped forms are loopback.</summary>
    internal static ServerConnectionKind KindOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return ServerConnectionKind.UnixSocket;
        }

        if (IPAddress.TryParse(address.Trim(), out IPAddress? parsed))
        {
            IPAddress plain = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
            return IPAddress.IsLoopback(plain) ? ServerConnectionKind.Loopback : ServerConnectionKind.Network;
        }

        return ServerConnectionKind.Network;
    }

    /// <summary>The sweep's one read of it, whichever check asks first.</summary>
    public static Task<ServerConnectionPosture> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ServerPostureQueries.PostureKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it through the context's runner.</summary>
    public static async Task<ServerConnectionPosture> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ServerConnectionPosture> rows = await context.Sql
            .QueryAsync(
                ServerPostureQueries.Posture(),
                static reader => new ServerConnectionPosture(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.GetFieldValue<string[]>(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetFieldValue<string[]>(5),
                    reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.GetBoolean(13)),
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 1
            ? rows[0]
            : throw new InvalidOperationException("The connection posture read returned " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows; it returns one.");
    }
}

/// <summary>One extension: installed here, or one the page has a chip for.</summary>
/// <param name="Name">The extension.</param>
/// <param name="Named">Whether the page has a chip for it (<see cref="ServerExtensionsRead.Chips" />).</param>
/// <param name="InstalledVersion">The version installed in this database, or <see langword="null" />.</param>
/// <param name="DefaultVersion">The server's default version, or <see langword="null" /> when the server has no such extension.</param>
/// <param name="Trusted">Whether that version is trusted, or <see langword="null" /> when not known.</param>
internal sealed record ServerExtensionRow(string Name, bool Named, string? InstalledVersion, string? DefaultVersion, bool? Trusted)
{
    /// <summary>Whether an installed extension's version differs from the one the server would install now (S07).</summary>
    public bool Outdated => InstalledVersion is not null && DefaultVersion is not null
        && !string.Equals(InstalledVersion, DefaultVersion, StringComparison.Ordinal);
}

/// <summary>The extensions as one read returned them: <see cref="ServerPostureQueries.Extensions" />.</summary>
/// <param name="Rows">The named ones first, then every other installed one, by name.</param>
/// <param name="Total">How many there are, beyond the ones read.</param>
internal sealed record ServerExtensionsRead(IReadOnlyList<ServerExtensionRow> Rows, long Total)
{
    /// <summary>
    /// The extensions the page has a chip for (issue #8 G7): the statistics the advisor reads, the bloat and buffer
    /// measures a DBA reaches for, the two online-repack tools, and TimescaleDB.
    /// </summary>
    public static IReadOnlyList<string> Chips { get; } = ["pg_stat_statements", "pgstattuple", "pg_buffercache", "pg_repack", "pg_squeeze", "timescaledb"];

    /// <summary>The row of <paramref name="name" />, or <see langword="null" />.</summary>
    public ServerExtensionRow? Find(string name) => Rows.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    /// <summary>The sweep's one read of it, whichever check asks first.</summary>
    public static Task<ServerExtensionsRead> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ServerPostureQueries.ExtensionsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it through the context's runner.</summary>
    public static async Task<ServerExtensionsRead> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ServerPostureQueries.Extensions(Chips);
        IReadOnlyList<(ServerExtensionRow Row, long Total)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (
                    new ServerExtensionRow(
                        reader.GetString(0),
                        reader.GetBoolean(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        PostgresGrants.KnownExtensions.TryGetValue(reader.GetString(0), out bool trusted) ? trusted : null),
                    reader.GetInt64(4)),
                cancellationToken)
            .ConfigureAwait(false);

        return new ServerExtensionsRead([.. rows.Select(static x => x.Row)], rows.Count > 0 ? rows[0].Total : 0);
    }

    /// <summary>
    /// The chips' extensions that are not installed and whose trust the studio does not know
    /// (<see cref="PostgresGrants.KnownExtensions" />) - the only ones the page asks <c>pg_available_extension_versions</c> about.
    /// </summary>
    public IReadOnlyList<string> TrustUnknown =>
        [.. Rows.Where(static x => x.Named && x.InstalledVersion is null && x.DefaultVersion is not null && x.Trusted is null).Select(static x => x.Name)];

    /// <summary>These rows with what <c>pg_available_extension_versions</c> said of their trust.</summary>
    public ServerExtensionsRead WithTrust(IReadOnlyDictionary<string, bool> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        return this with { Rows = [.. Rows.Select(x => trusted.TryGetValue(x.Name, out bool value) ? x with { Trusted = value } : x)] };
    }

    /// <summary>The page's read of whether <paramref name="names" /> are trusted extensions - never the sweep's.</summary>
    public static async Task<IReadOnlyDictionary<string, bool>> ReadTrustAsync(HealthCheckContext context, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(names);

        if (names.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        var query = ServerPostureQueries.ExtensionTrust(names);
        IReadOnlyList<(string Name, bool Trusted)> rows = await context.Sql
            .QueryAsync(query, static reader => (reader.GetString(0), !reader.IsDBNull(1) && reader.GetBoolean(1)), cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(static x => x.Name, static x => x.Trusted, StringComparer.Ordinal);
    }
}

/// <summary>Whether a curated setting was read, and if not, why.</summary>
internal enum ServerSettingState
{
    /// <summary>Read.</summary>
    Read,

    /// <summary>Not shown to the reading role: only a superuser or <c>pg_read_all_settings</c> sees it.</summary>
    Hidden,

    /// <summary>Not on this server, although the role may read every setting - a platform that removed it.</summary>
    Absent,

    /// <summary>Not on this release: "needs PostgreSQL N".</summary>
    NeedsVersion,
}

/// <summary>One curated setting as the page shows it.</summary>
/// <param name="Name">The setting.</param>
/// <param name="Group">Its group (<see cref="ServerSettingsCatalog.Groups" />).</param>
/// <param name="State">Whether it was read.</param>
/// <param name="Setting">The row, when it was.</param>
/// <param name="Since">The feature whose release first has it, for a <see cref="ServerSettingState.NeedsVersion" /> row.</param>
internal sealed record ServerSettingRow(string Name, string Group, ServerSettingState State, ServerSettingValue? Setting, PostgresFeature? Since = null);

/// <summary>One chip of the page's extensions: whether the studio can use it, and what stands in the way, naming the grant (A7).</summary>
/// <param name="Name">The extension, or the setting for a setting chip (<c>track_io_timing</c>).</param>
/// <param name="InstalledVersion">The installed version, or <see langword="null" />.</param>
/// <param name="AvailableVersion">The server's default version, or <see langword="null" />.</param>
/// <param name="Usable">Whether the studio can use it: installed, and loaded or granted where it must be.</param>
/// <param name="Note">What stands in the way, or <see langword="null" /> when nothing does.</param>
/// <param name="IsSetting">Whether it is a setting's chip rather than an extension's.</param>
internal sealed record ServerExtensionStatus(string Name, string? InstalledVersion, string? AvailableVersion, bool Usable, string? Note, bool IsSetting = false);

/// <summary>One section of <see cref="ServerSettingsFacts" />: its value, or the sentence in its place.</summary>
/// <typeparam name="T">What the section reads.</typeparam>
/// <param name="Value">The value, or <see langword="null" /> when the read failed.</param>
/// <param name="Note">The studio's own words for a failure and its SQLSTATE - never Postgres' text.</param>
internal sealed record ServerRead<T>(T? Value, string? Note)
    where T : class
{
    /// <summary>Whether it was read.</summary>
    public bool Read => Value is not null;

    /// <summary>A value.</summary>
    public static ServerRead<T> Of(T value) => new(value, null);
}

/// <summary>
/// What the Server page shows, read once per database per <c>HealthInterval</c> for every visitor - gate-free, keyed by source
/// and database identity (W5-index §2.3, API design §6.2's S2 row) - and what <c>ServerPageService</c> then shows a visitor
/// through the database-wide gate.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Server">The server's version and platform (S01's read).</param>
/// <param name="Grants">What the reading role holds (VERS's read).</param>
/// <param name="Settings">The curated settings.</param>
/// <param name="Extensions">The extensions.</param>
/// <param name="Posture">How the source's connection reaches the server.</param>
/// <param name="Statements">Every statement the read ran, for "Show SQL".</param>
internal sealed record ServerSettingsFacts(
    DateTimeOffset ReadAt,
    PostgresServerFacts Server,
    ServerRead<PostgresGrants> Grants,
    ServerRead<ServerSettingsRead> Settings,
    ServerRead<ServerExtensionsRead> Extensions,
    ServerRead<ServerConnectionPosture> Posture,
    IReadOnlyList<HealthStatement> Statements)
{
    /// <summary>The sections that failed, by name, for a test and the matrix: empty when every one read.</summary>
    public IReadOnlyList<string> Failures =>
    [
        .. new (string Name, bool Read, string? Note)[]
            {
                ("grants", Grants.Read, Grants.Note),
                ("settings", Settings.Read, Settings.Note),
                ("extensions", Extensions.Read, Extensions.Note),
                ("connection", Posture.Read, Posture.Note),
            }
            .Where(static x => !x.Read)
            .Select(static x => x.Name + ": " + x.Note),
    ];

    /// <summary>
    /// Every curated setting, in <see cref="ServerSettingsCatalog" />'s order and then the families' members by name: read,
    /// hidden from the role, absent from the server, or not on this release.
    /// </summary>
    public IReadOnlyList<ServerSettingRow> CuratedSettings()
    {
        if (Settings.Value is not { } read)
        {
            return [];
        }

        bool seesEverySetting = Grants.Value?.Holds(PostgresGrant.ReadAllSettings) == true;
        List<ServerSettingRow> rows = [];

        foreach (CuratedSetting setting in ServerSettingsCatalog.Settings)
        {
            if (setting.Since is { } since && !PostgresFeatures.Has(since, Server.VersionNumber))
            {
                rows.Add(new ServerSettingRow(setting.Name, setting.Group, ServerSettingState.NeedsVersion, null, since));
            }
            else if (read.Find(setting.Name) is { } value)
            {
                rows.Add(new ServerSettingRow(setting.Name, setting.Group, ServerSettingState.Read, value));
            }
            else
            {
                rows.Add(new ServerSettingRow(setting.Name, setting.Group, seesEverySetting ? ServerSettingState.Absent : ServerSettingState.Hidden, null));
            }
        }

        foreach (CuratedSettingFamily family in ServerSettingsCatalog.Families)
        {
            rows.AddRange(read.Rows
                .Where(x => family.Contains(x.Name))
                .OrderBy(static x => x.Name, StringComparer.Ordinal)
                .Select(x => new ServerSettingRow(x.Name, family.Group, ServerSettingState.Read, x)));
        }

        return rows;
    }

    /// <summary>The extension chips, and <c>track_io_timing</c>'s, each worded through <see cref="PostgresGrants" /> (A7).</summary>
    public IReadOnlyList<ServerExtensionStatus> Chips()
    {
        List<ServerExtensionStatus> chips = [];
        PostgresPlatform platform = Server.Platform;

        if (Extensions.Value is { } extensions)
        {
            foreach (string name in ServerExtensionsRead.Chips)
            {
                ServerExtensionRow row = extensions.Find(name) ?? new ServerExtensionRow(name, true, null, null, null);
                chips.Add(ChipOf(row, Posture.Value, Grants.Value, platform));
            }
        }

        if (Settings.Value is { } settings)
        {
            chips.Add(TimingChip(settings.Find("track_io_timing"), Grants.Value, platform));
        }

        return chips;
    }

    /// <summary>One extension's chip. Pure: the unit tests hold every sentence to it.</summary>
    internal static ServerExtensionStatus ChipOf(ServerExtensionRow row, ServerConnectionPosture? posture, PostgresGrants? grants, PostgresPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(platform);

        if (row.InstalledVersion is null)
        {
            if (row.DefaultVersion is null)
            {
                return new ServerExtensionStatus(row.Name, null, null, false,
                    "Not installed, and this server has no files for it"
                    + (platform.IsManaged ? ": " + platform.Name + " may not offer it." : ": its package is not installed on the server."));
            }

            bool trusted = row.Trusted ?? (PostgresGrants.KnownExtensions.TryGetValue(row.Name, out bool known) && known);
            return new ServerExtensionStatus(row.Name, null, row.DefaultVersion, false, "Not installed in this database. " + WhoCreates(row.Name, trusted, platform));
        }

        string readsAs = grants?.ReadsAs ?? posture?.ReadingRole ?? "the studio's role";

        return row.Name switch
        {
            "pg_stat_statements" => posture switch
            {
                null => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, false,
                    "Installed in this database; whether its library is loaded could not be read."),
                { StatementsLoaded: false } => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, false,
                    "Installed in this database, but its library is not loaded: shared_preload_libraries must name it - set in "
                    + platform.WhereSettingsChange + " - and the server must restart. Until then the statement checks have nothing to read."),
                _ => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, true, null),
            },
            "pgstattuple" => Granted(row, grants, PostgresGrant.StatScanTables, platform, readsAs),
            "pg_buffercache" => Granted(row, grants, PostgresGrant.Monitor, platform, readsAs),
            _ => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, true, null),
        };
    }

    /// <summary><c>track_io_timing</c>'s chip: on, off (and where it changes), or not shown to the role.</summary>
    internal static ServerExtensionStatus TimingChip(ServerSettingValue? setting, PostgresGrants? grants, PostgresPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return setting switch
        {
            null => new ServerExtensionStatus("track_io_timing", null, null, false,
                "Not shown to role " + (grants?.ReadsAs ?? "the studio's role") + ".", IsSetting: true),
            { IsOn: true } => new ServerExtensionStatus("track_io_timing", "on", null, true, null, IsSetting: true),
            _ => new ServerExtensionStatus("track_io_timing", setting.Value, null, false,
                "Off: Postgres does not time its reads and writes, so pg_stat_statements and EXPLAIN (BUFFERS) carry no I/O time. "
                + "Timing costs little on most hardware (pg_test_timing measures it). It changes in "
                + ServerSettingRemedy.WhereToSet(platform, "track_io_timing") + ".", IsSetting: true),
        };
    }

    private static ServerExtensionStatus Granted(ServerExtensionRow row, PostgresGrants? grants, PostgresGrant needed, PostgresPlatform platform, string readsAs) =>
        grants switch
        {
            null => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, false,
                "Installed in this database; whether role " + readsAs + " holds " + needed.Described + " could not be read."),
            _ when grants.Holds(needed) => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, true, null),
            _ => new ServerExtensionStatus(row.Name, row.InstalledVersion, row.DefaultVersion, false,
                "Installed in this database. " + PostgresGrants.NotChecked(needed, platform, readsAs)),
        };

    /// <summary>
    /// Who can create an extension on <paramref name="platform" />: <see cref="PostgresGrants.ExtensionNotInstalled" />'s own
    /// sentence, without its "not checked" opening, which a chip has no check for.
    /// </summary>
    internal static string WhoCreates(string name, bool trusted, PostgresPlatform platform)
    {
        string sentence = PostgresGrants.ExtensionNotInstalled(name, trusted, platform);
        string opening = "Not checked: this needs the " + name + " extension, which this database does not have. ";

        return sentence.StartsWith(opening, StringComparison.Ordinal) ? sentence[opening.Length..] : sentence;
    }
}

/// <summary>Reads <see cref="ServerSettingsFacts" /> for one database - the page's read, through the checks' own statements.</summary>
/// <remarks>An interface so a component or service test can hand the page facts without Postgres (D12).</remarks>
internal interface IServerSettingsFactsReader
{
    /// <summary>
    /// Reads every section. Throws only when the server's version and platform cannot be read; a section's failure is its own
    /// value.
    /// </summary>
    /// <param name="resolved">The database, resolved with no tenant - by a visitor's resolver, or the unattended path.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ServerSettingsFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken);
}

/// <summary>
/// The real reader: every section through <see cref="HealthSqlRunner" /> - the advisor's read-only transaction under
/// <c>statement_timeout</c> and <c>lock_timeout</c>, <c>search_path</c> pinned, as <c>SqlConsoleRole</c> when one is set - on
/// one connection, with a memo of its own so each shared read runs once.
/// </summary>
/// <remarks>
/// <para>
/// <b>The checks' own reads.</b> Each section is the record S04-S10 and K08 gather through (<see cref="ServerSettingsRead" />,
/// <see cref="ServerConnectionPosture" />, <see cref="ServerExtensionsRead" />), with S01's <see cref="PostgresServerFacts" />
/// and VERS's <see cref="PostgresGrants" />, so what the page shows and what the advisor judges are the same statements.
/// </para>
/// <para>
/// <b>No gate inside.</b> The key is the database and nothing else; the caller resolves the scope first, and what a visitor
/// is shown is decided after (<c>ServerPageService</c>). A section that fails is that section's sentence and SQLSTATE; Debug
/// for a state the host chose (<c>42501</c>, a timeout), event 9440 at the throttle's level for anything else.
/// </para>
/// </remarks>
internal sealed class ServerSettingsFactsReader : IServerSettingsFactsReader
{
    private readonly IStudioSettings settings;
    private readonly IServiceProvider services;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<ServerSettingsFactsReader> logger;
    private readonly TimeProvider timeProvider;
    private readonly IStudioRuntime runtime;

    public ServerSettingsFactsReader(
        IStudioSettings settings,
        IServiceProvider services,
        StudioLogThrottle throttle,
        ILogger<ServerSettingsFactsReader> logger,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null)
    {
        this.settings = settings;
        this.services = services;
        this.throttle = throttle;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
    }

    /// <inheritdoc />
    public async Task<ServerSettingsFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        await using HealthConnection connection = new(resolved.Target);

        TimeSpan timeout = settings.QueryTimeout < HealthSweeper.MaxStatementTimeout ? settings.QueryTimeout : HealthSweeper.MaxStatementTimeout;
        HealthSqlRunner sql = new(connection, new ReadOnlySqlOptions { StatementTimeout = timeout, Role = settings.SqlConsoleRole }, observer: null);
        HealthCheckContext context = new(resolved, settings, runtime, services, sql, timeProvider.GetUtcNow(), new HealthSweepMemo(), cancellationToken);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        ServerRead<PostgresGrants> grants = await SectionAsync(context, "grants", PostgresGrants.SharedAsync, cancellationToken).ConfigureAwait(false);
        ServerRead<ServerSettingsRead> curated = await SectionAsync(context, "settings", ServerSettingsRead.SharedAsync, cancellationToken).ConfigureAwait(false);
        ServerRead<ServerExtensionsRead> extensions = await SectionAsync(context, "extensions", ServerExtensionsRead.SharedAsync, cancellationToken).ConfigureAwait(false);

        // Whether a chip's extension is trusted, for the few the studio cannot word from what it knows - the page's read
        // alone; the sweep's S07 never pays for pg_available_extension_versions.
        if (extensions.Value is { TrustUnknown.Count: > 0 } unknown)
        {
            ServerRead<IReadOnlyDictionary<string, bool>> trust = await SectionAsync(
                    context,
                    "extension trust",
                    x => ServerExtensionsRead.ReadTrustAsync(x, unknown.TrustUnknown, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (trust.Value is { } trusted)
            {
                extensions = ServerRead<ServerExtensionsRead>.Of(unknown.WithTrust(trusted));
            }
        }

        ServerRead<ServerConnectionPosture> posture = await SectionAsync(context, "connection", ServerConnectionPosture.SharedAsync, cancellationToken).ConfigureAwait(false);

        return new ServerSettingsFacts(context.Now, server, grants, curated, extensions, posture, context.Sql.Statements);
    }

    private async Task<ServerRead<T>> SectionAsync<T>(
        HealthCheckContext context,
        string section,
        Func<HealthCheckContext, Task<T>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return ServerRead<T>.Of(await read(context).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ServerRead<T>(null, Failure(exception, context, section));
        }
    }

    /// <summary>A section's sentence for a failure, and the log line: Debug for an expected state, 9440 at the throttle's level otherwise.</summary>
    private string Failure(Exception exception, HealthCheckContext context, string section)
    {
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        string? expected = postgres?.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => "The role the studio reads as may not read this (SQLSTATE 42501).",
            PostgresErrorCodes.QueryCanceled => "The read ran out of its statement_timeout; it is tried again at the next interval.",
            PostgresErrorCodes.LockNotAvailable => "The read waited too long for a lock; it is tried again at the next interval.",
            _ => PostgresFailure.IsTimeout(exception) ? "The read timed out; it is tried again at the next interval." : null,
        };

        if (expected is not null)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read the server {Section} of {DatabaseId}: {Reason}", settings.Text.ProductName, section, context.DatabaseId, expected);
            }

            return expected;
        }

        LogLevel level = throttle.WarningOrDebug("Server.Read", context.StoreKey, context.DatabaseId, StudioLogThrottle.KindOf(exception));

        if (settings.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioServerFactsReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }
        else
        {
            logger.ServerFactsReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, postgres?.SqlState);
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "This could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ").");
    }
}

/// <summary>
/// One <see cref="ServerSettingsFacts" /> per database per <c>HealthInterval</c>, however many visitors or clicks ask (D10) - a
/// single-flight <see cref="StudioSnapshotCache" /> keyed by source and database identity.
/// </summary>
/// <remarks>
/// <b>Authorization comes before this, never inside it</b>, as for the sweep: the key is the database and nothing else, so the
/// caller resolves the scope - the store policy for the database as a whole - first, and what a visitor is shown is decided
/// after.
/// </remarks>
internal sealed class ServerSettingsFactsCache
{
    private readonly IServerSettingsFactsReader reader;
    private readonly StudioSnapshotCache cache;
    private int reads;

    public ServerSettingsFactsCache(IServerSettingsFactsReader reader, IStudioSettings settings, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(settings);

        this.reader = reader;
        cache = new StudioSnapshotCache(timeProvider ?? TimeProvider.System, settings.EffectiveHealthInterval);
    }

    /// <summary>How many times the facts were actually read, for a test.</summary>
    internal int ReadCount => Volatile.Read(ref reads);

    /// <summary>The database's facts: a recent read, or a new one shared with everybody who asks while it runs.</summary>
    public Task<ServerSettingsFacts> GetAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        return cache.GetAsync(resolved.Source.Key + "|" + resolved.Target.Identity, token => ReadAsync(resolved, token), cancellationToken);
    }

    private Task<ServerSettingsFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref reads);
        return reader.ReadAsync(resolved, cancellationToken);
    }
}
