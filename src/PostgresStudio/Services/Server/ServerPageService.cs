using System.Globalization;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Server;

/// <summary>What the Server page reads: the database's server facts, through this visitor's gate.</summary>
/// <remarks>An interface so the page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IServerPageService
{
    /// <summary>The visitor's view of the scope's database. Never throws but for cancellation; a refusal is a value.</summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a server belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Why a visitor is shown nothing of the server.</summary>
internal enum ServerRefusal
{
    /// <summary>Shown.</summary>
    None,

    /// <summary>The store policy refused the database as a whole.</summary>
    StorePolicy,

    /// <summary>The source or the database could not be reached or resolved.</summary>
    Unavailable,
}

/// <summary>One line of "what the studio can see": a privilege of the reading role, and what holding it or not means.</summary>
/// <param name="Name">The privilege, in Postgres' words: <c>pg_read_all_stats</c>, <c>superuser</c>.</param>
/// <param name="Held">Whether the reading role holds it.</param>
/// <param name="Consequence">What it means for the studio, and who can grant it on this platform when it is not held.</param>
internal sealed record ServerGrantLine(string Name, bool Held, string Consequence);

/// <summary>One curated setting as a visitor is shown it.</summary>
/// <param name="Row">The setting.</param>
/// <param name="Value">Its value, formatted with its unit, or the sentence for one that was not read.</param>
/// <param name="SetBy">Who set it, in words, or <see langword="null" /> for one that was not read.</param>
/// <param name="WhereItChanges">Where it changes on this platform (<see cref="ServerSettingRemedy.WhereToSet" />), for the hover.</param>
internal sealed record ServerSettingLine(ServerSettingRow Row, string Value, string? SetBy, string WhereItChanges);

/// <summary>One group of curated settings.</summary>
/// <param name="Group">The group's name (<see cref="ServerSettingsCatalog.Groups" />).</param>
/// <param name="Title">Its heading.</param>
/// <param name="Settings">Its settings.</param>
internal sealed record ServerSettingGroup(string Group, string Title, IReadOnlyList<ServerSettingLine> Settings);

/// <summary>The Server page's view of one database, for one visitor.</summary>
internal sealed record ServerPageView
{
    /// <summary>Why nothing is shown, or <see cref="ServerRefusal.None" />.</summary>
    public ServerRefusal Refusal { get; init; }

    /// <summary>The refusal's sentence.</summary>
    public string? Reason { get; init; }

    /// <summary>When the facts were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The server's version and platform.</summary>
    public PostgresServerFacts? Server { get; init; }

    /// <summary>"on", "off", or "not read".</summary>
    public string DataChecksums { get; init; } = "not read";

    /// <summary>What the checksums' state means here.</summary>
    public string? DataChecksumsNote { get; init; }

    /// <summary>When the server started, or <see langword="null" /> when not read.</summary>
    public DateTimeOffset? ServerStarted { get; init; }

    /// <summary>How long it has been up, at <see cref="MeasuredAt" />.</summary>
    public TimeSpan? Uptime { get; init; }

    /// <summary>The role the data source logs in as - database-wide information (D29).</summary>
    public string? LoginRole { get; init; }

    /// <summary>The role the studio's reads run as - database-wide information (D29).</summary>
    public string? ReadsAs { get; init; }

    /// <summary>The sentence that says which role reads what: <c>SqlConsoleRole</c> or the login.</summary>
    public string? ReadingRoleNote { get; init; }

    /// <summary>What the reading role can see, privilege by privilege.</summary>
    public IReadOnlyList<ServerGrantLine> Grants { get; init; } = [];

    /// <summary>Why the grants are not shown, when they were not read.</summary>
    public string? GrantsNote { get; init; }

    /// <summary>The curated settings, by group.</summary>
    public IReadOnlyList<ServerSettingGroup> Settings { get; init; } = [];

    /// <summary>Why the settings are not shown, when they were not read.</summary>
    public string? SettingsNote { get; init; }

    /// <summary>The extension chips, and <c>track_io_timing</c>'s.</summary>
    public IReadOnlyList<ServerExtensionStatus> Extensions { get; init; } = [];

    /// <summary>Why the extensions are not shown, when they were not read.</summary>
    public string? ExtensionsNote { get; init; }

    /// <summary>Why the connection's posture is not shown, when it was not read.</summary>
    public string? PostureNote { get; init; }

    /// <summary>Every statement the facts read ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>A refusal.</summary>
    public static ServerPageView Refused(ServerRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>
/// The Server page's per-visitor half: the scope resolved for the database as a whole, and the shared facts shown through the
/// database-wide gate.
/// </summary>
/// <remarks>
/// <para>
/// <b>One gate</b> (W5-S2, AGENTS.md D29). Everything here is about the server and the database as a whole - its version, its
/// settings, its extensions, the roles the studio connects and reads as - so it is shown to a visitor the store policy passes
/// for the database with <c>TenantId = null</c>: the Health page's <see cref="HealthGate.Database" />. Nothing here needs
/// <c>ViewDatabaseActivity</c>: no session, no client address, no statement. The role names are the database-wide gate's
/// (D29); the client address the posture read keeps is never shown.
/// </para>
/// <para>
/// <b>About the store and database the scope resolved to, by their own names</b> (D29): the resolver's first question takes
/// the visitor's spelling, and the second, when either spelling differs, the registered key and the resolved identity.
/// </para>
/// <para>
/// <b>The page does not judge.</b> The security posture is the advisor's (S04-S10, K05-K08), shown through
/// <see cref="IHealthService" />'s own per-visitor report (<see cref="ServerPosture" />); this service never re-reads a
/// condition a check judges.
/// </para>
/// </remarks>
internal sealed class ServerPageService : IServerPageService
{
    private readonly IStudioSettings settings;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly ServerSettingsFactsCache facts;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<ServerPageService> logger;

    public ServerPageService(
        IStudioSettings settings,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        ServerSettingsFactsCache facts,
        StudioLogThrottle throttle,
        ILogger<ServerPageService> logger)
    {
        this.settings = settings;
        this.authorization = authorization;
        this.resolver = resolver;
        this.facts = facts;
        this.throttle = throttle;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope asked = scope with { TenantId = null };
        ResolvedSource resolved;

        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return ServerPageView.Refused(ServerRefusal.StorePolicy, StorePolicyDenial(settings.Text));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return ServerPageView.Refused(ServerRefusal.Unavailable, exception.Message);
        }

        StudioScope whole = new(resolved.Source.Key, resolved.Target.Identity, null);
        bool samePlace = string.Equals(asked.StoreKey, whole.StoreKey, StringComparison.Ordinal)
            && string.Equals(asked.DatabaseId, whole.DatabaseId, StringComparison.Ordinal);

        if (!samePlace && !await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return ServerPageView.Refused(ServerRefusal.StorePolicy, StorePolicyDenial(settings.Text));
        }

        ServerSettingsFacts read;

        try
        {
            read = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ServerPageView.Refused(ServerRefusal.Unavailable, Unreachable(exception, resolved));
        }

        return Build(read, settings.Text, settings.SqlConsoleRole is { Length: > 0 });
    }

    /// <summary>The visitor's view of <paramref name="read" />. Pure, for the unit tests of what is shown.</summary>
    /// <param name="read">The shared facts.</param>
    /// <param name="text">The studio's words.</param>
    /// <param name="consoleRoleSet">Whether <c>SqlConsoleRole</c> is set.</param>
    internal static ServerPageView Build(ServerSettingsFacts read, StudioText text, bool consoleRoleSet)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(text);

        PostgresPlatform platform = read.Server.Platform;
        PostgresGrants? grants = read.Grants.Value;
        ServerConnectionPosture? posture = read.Posture.Value;
        string? readsAs = grants?.ReadsAs ?? posture?.ReadingRole;

        return new ServerPageView
        {
            MeasuredAt = read.ReadAt,
            Server = read.Server,
            DataChecksums = posture?.DataChecksums ?? "not read",
            DataChecksumsNote = posture?.DataChecksums switch
            {
                "on" => "Postgres checks every page it reads from disk, and S03 counts the failures.",
                "off" => "Pages are not checksummed: a page changed behind Postgres' back is read as good. Checksums are chosen when a "
                    + "cluster is created - on by default only from PostgreSQL 18 - and turning them on later needs a stopped server "
                    + "(pg_checksums --enable).",
                _ => null,
            },
            ServerStarted = posture?.ServerStarted,
            Uptime = posture?.ServerStarted is { } started && read.ReadAt > started ? read.ReadAt - started : null,
            LoginRole = posture?.LoginRole ?? grants?.LoginRole,
            ReadsAs = readsAs,
            ReadingRoleNote = ReadingRoleNote(text, posture, grants, consoleRoleSet),
            Grants = grants is null ? [] : GrantLines(grants, posture, platform),
            GrantsNote = read.Grants.Note,
            Settings = read.Settings.Read ? Groups(read.CuratedSettings(), platform, readsAs) : [],
            SettingsNote = read.Settings.Note,
            Extensions = read.Chips(),
            ExtensionsNote = read.Extensions.Note,
            PostureNote = read.Posture.Note,
            Statements = read.Statements,
        };
    }

    /// <summary>What the reading role holds, one privilege a line, each with what it means for the studio.</summary>
    internal static IReadOnlyList<ServerGrantLine> GrantLines(PostgresGrants grants, ServerConnectionPosture? posture, PostgresPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(platform);

        List<ServerGrantLine> lines =
        [
            new("superuser", grants.Superuser, grants.Superuser
                ? "Reads everything on the server - more than the studio ever needs. S04 judges the role the data source logs in as."
                : "Not a superuser, which the studio never needs."),
        ];

        List<string> admin = [.. PostgresGrants.ProviderAdminRoles.Where(x => grants.Roles.Any(r => r.Member && string.Equals(r.Name, x, StringComparison.Ordinal)))];

        if (admin.Count > 0 || platform.IsManaged)
        {
            lines.Add(new ServerGrantLine(
                "provider admin role",
                admin.Count > 0,
                admin.Count > 0
                    ? "A provider admin role (" + string.Join(", ", admin) + "): the platform's administrator, not a superuser - it can grant "
                      + "itself every monitoring role."
                    : "Not a member of " + platform.Name + "'s admin role."));
        }

        lines.Add(Monitoring(grants, PostgresGrant.ReadAllStats, platform,
            "Every role's sessions, standby lag and autovacuum's progress are visible (Sessions, C04-C06, R03).",
            "Other roles' sessions show as counts only, and standby lag and autovacuum's progress are not read."));
        lines.Add(Monitoring(grants, PostgresGrant.StatScanTables, platform,
            "pgstattuple's exact bloat measure may read every table.",
            "pgstattuple's exact bloat measure is not available."));
        lines.Add(Monitoring(grants, PostgresGrant.ReadAllSettings, platform,
            "Every setting is shown, the ones only a superuser reads included.",
            "Settings only a superuser reads (shared_preload_libraries among them) are hidden from this page."));
        lines.Add(Monitoring(grants, PostgresGrant.Monitor, platform,
            "Holds all three of the above.",
            "pg_monitor would give all three of the above."));

        if (posture is not null)
        {
            IReadOnlyList<string> files = posture.ReadingFileAccessRoles;
            bool superuser = posture.ReadingSuperuser;

            lines.Add(new ServerGrantLine(
                "file access",
                superuser || files.Count > 0,
                superuser
                    ? "As a superuser it may read and write the server's files and run programs on it - the studio never does."
                    : files.Count > 0
                        ? "A member of " + string.Join(", ", files) + ": it may reach the server's files or programs, which the studio never does."
                        : "None of pg_read_server_files, pg_write_server_files or pg_execute_server_program - as it should be."));
        }

        return lines;
    }

    private static ServerGrantLine Monitoring(PostgresGrants grants, PostgresGrant grant, PostgresPlatform platform, string held, string missing) =>
        grants.Holds(grant)
            ? new ServerGrantLine(grant.Privilege, true, held)
            : new ServerGrantLine(grant.Privilege, false, missing + " " + PostgresGrants.Grantor(grant, platform));

    /// <summary>Which role reads what: the console's role for the catalog and the statements typed in, the login for the rest.</summary>
    private static string? ReadingRoleNote(StudioText text, ServerConnectionPosture? posture, PostgresGrants? grants, bool consoleRoleSet)
    {
        string? login = posture?.LoginRole ?? grants?.LoginRole;
        string? reading = grants?.ReadsAs ?? posture?.ReadingRole;

        if (login is null || reading is null)
        {
            return null;
        }

        return consoleRoleSet && !string.Equals(login, reading, StringComparison.Ordinal)
            ? "The studio's catalog reads, the database browser and the SQL console run as " + reading + " (" + text.OptionName("SqlConsoleRole")
              + "); the data source logs in as " + login + ", which the statement checks read pg_stat_statements as (D29)."
            : "No " + text.OptionName("SqlConsoleRole") + " is set, so every read runs as the role the data source logs in as, " + login + ".";
    }

    /// <summary>The curated rows by group, in <see cref="ServerSettingsCatalog.Groups" />' order.</summary>
    internal static IReadOnlyList<ServerSettingGroup> Groups(IReadOnlyList<ServerSettingRow> rows, PostgresPlatform platform, string? readsAs)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(platform);

        return
        [
            .. ServerSettingsCatalog.Groups.Select(group => new ServerSettingGroup(
                group,
                GroupTitle(group),
                [.. rows.Where(x => string.Equals(x.Group, group, StringComparison.Ordinal)).Select(x => Line(x, platform, readsAs))])),
        ];
    }

    /// <summary>One row's line: its formatted value, or the sentence for one that was not read.</summary>
    internal static ServerSettingLine Line(ServerSettingRow row, PostgresPlatform platform, string? readsAs)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(platform);

        string where = "Changes in " + ServerSettingRemedy.WhereToSet(platform, row.Name) + ".";

        return row.State switch
        {
            ServerSettingState.Read => new ServerSettingLine(
                row,
                ServerSettingText.Format(row.Setting!.Value, row.Setting.Unit),
                DangerousSettingsCheck.SetBy(row.Setting.Source)
                + (row.Setting.StudioSet ? " - the value a new session starts with; the studio's own read sets its own" : string.Empty),
                where),
            ServerSettingState.Hidden => new ServerSettingLine(
                row,
                HiddenSentence(readsAs),
                null,
                where),
            ServerSettingState.Absent => new ServerSettingLine(row, "Not on this server.", null, where),
            _ => new ServerSettingLine(row, "Not on this server: " + PostgresFeatures.Needs(row.Since!.Value) + ".", null, where),
        };
    }

    /// <summary>What a setting the reading role is not shown says.</summary>
    internal static string HiddenSentence(string? readsAs) =>
        "Hidden from role " + (readsAs ?? "the studio's role") + ": it needs " + PostgresGrant.ReadAllSettings.Described + ".";

    /// <summary>A group's heading.</summary>
    internal static string GroupTitle(string group) => group switch
    {
        "memory" => "Memory",
        "connections" => "Connections and sessions",
        "wal" => "WAL and durability",
        "vacuum" => "Vacuum",
        "planner" => "Planner",
        "io" => "I/O",
        "statistics" => "Statistics",
        _ => group,
    };

    private static string StorePolicyDenial(StudioText text)
    {
        string policy = text.Product == StudioProduct.PostgresStudio ? text.OptionName("SourceAuthorizationPolicy") : text.OptionName("StoreAuthorizationPolicy");

        return "Your account may not see this database's server. The policy (" + policy + ") refused this " + text.SourceNoun
            + " and database as a whole, with no tenant selected: the server's settings belong to no tenant.";
    }

    /// <summary>
    /// The sentence for facts that could not be read at all - the server's version and platform, which every section is worded
    /// by - and the log line: Debug for an expected state, event 9440 at the throttle's level otherwise.
    /// </summary>
    private string Unreachable(Exception exception, ResolvedSource resolved)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        bool expected = postgres?.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.LockNotAvailable
            || PostgresFailure.IsTimeout(exception);

        if (expected)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "{Product} did not read the server facts of {DatabaseId}", settings.Text.ProductName, databaseId);
            }
        }
        else
        {
            LogLevel level = throttle.WarningOrDebug("Server.Read", storeKey, databaseId, StudioLogThrottle.KindOf(exception));

            if (settings.Text.Product == StudioProduct.PostgresStudio)
            {
                logger.PostgresStudioServerFactsReadFailed(level, exception, "server", storeKey, databaseId, postgres?.SqlState);
            }
            else
            {
                logger.ServerFactsReadFailed(level, exception, "server", storeKey, databaseId, postgres?.SqlState);
            }
        }

        return PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The server's version and platform could not be read" + (postgres is null ? " (" + exception.GetType().Name + ")." : " (SQLSTATE " + postgres.SqlState + ").");
    }
}

/// <summary>
/// The security posture the Server page shows: the advisor's own findings and status rows for S04-S10 and K05-K08, from the
/// visitor's Health report - never judged again here.
/// </summary>
internal static class ServerPosture
{
    /// <summary>The checks the page's security-posture section shows, in the order it shows them.</summary>
    public static IReadOnlyList<string> Codes { get; } = ["S04", "S05", "S06", "S07", "S08", "S09", "S10", "K05", "K06", "K07", "K08"];

    /// <summary>The report's findings and status rows for <see cref="Codes" />, worst finding first.</summary>
    /// <param name="report">The visitor's report.</param>
    public static (IReadOnlyList<HealthReportFinding> Findings, IReadOnlyList<HealthCheckStatus> Checks) Of(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return (
            [.. report.Findings.Where(static x => Codes.Contains(x.Code, StringComparer.Ordinal)).OrderBy(static x => x.Severity).ThenBy(static x => x.Code, StringComparer.Ordinal)],
            [.. report.Checks.Where(static x => Codes.Contains(x.Code, StringComparer.Ordinal)).OrderBy(static x => IndexOf(x.Code))]);
    }

    private static int IndexOf(string code)
    {
        for (int i = 0; i < Codes.Count; i++)
        {
            if (string.Equals(Codes[i], code, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Codes.Count;
    }
}

/// <summary>A setting's value with its unit, as a person reads it: <c>128 MB</c>, <c>5 min</c>.</summary>
internal static class ServerSettingText
{
    /// <summary>
    /// <paramref name="value" /> in <paramref name="unit" />: a size in bytes' largest whole unit, a time in the largest unit it
    /// divides into, and anything else as Postgres spells it with its unit after it.
    /// </summary>
    public static string Format(string value, string? unit)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (unit is null || !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
        {
            return unit is null ? value : value + " " + unit;
        }

        if (number < 0)
        {
            return value + " (" + unit + ")";
        }

        return unit switch
        {
            "B" => Bytes(number),
            "kB" => Bytes(number * 1024),
            "8kB" => Bytes(number * 8192),
            "16kB" => Bytes(number * 16384),
            "MB" => Bytes(number * 1024 * 1024),
            "GB" => Bytes(number * 1024 * 1024 * 1024),
            "us" => number == 0 ? "0" : Time(TimeSpan.FromMicroseconds(number), "us", number),
            "ms" => number == 0 ? "0" : Time(TimeSpan.FromMilliseconds(number), "ms", number),
            "s" => number == 0 ? "0" : Time(TimeSpan.FromSeconds(number), "s", number),
            "min" => number == 0 ? "0" : Time(TimeSpan.FromMinutes(number), "min", number),
            _ => value + " " + unit,
        };
    }

    private static string Bytes(long bytes)
    {
        string[] units = ["bytes", "kB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1 && size % 1024 == 0)
        {
            size /= 1024;
            unit++;
        }

        return size.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string Time(TimeSpan time, string unit, long number) =>
        time >= TimeSpan.FromSeconds(1) && time.TotalMilliseconds % 1000 == 0
            ? SessionText.Duration(time)
            : number.ToString(CultureInfo.InvariantCulture) + " " + unit;
}
