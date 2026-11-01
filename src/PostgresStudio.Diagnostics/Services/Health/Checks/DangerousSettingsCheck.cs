using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One server setting as the studio's session sees it.</summary>
/// <param name="Name">The setting.</param>
/// <param name="Value">Its value, as <c>pg_settings.setting</c> spells it.</param>
/// <param name="Source">Where it was set: <c>default</c>, <c>configuration file</c>, <c>client</c>, <c>database</c>, …</param>
/// <param name="PendingRestart">Whether a change to it waits for the server to restart.</param>
internal sealed record ServerSetting(string Name, string Value, string? Source, bool PendingRestart)
{
    /// <summary>
    /// <c>pg_settings.context</c> - <c>postmaster</c>, <c>sighup</c>, <c>superuser</c>, <c>user</c>, ... - or
    /// <see langword="null" /> when it was not read; what tells a remedy whether the change needs a restart.
    /// </summary>
    public string? Context { get; init; }
}

/// <summary>What S02 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Settings">The settings it asks about, and every one waiting for a restart.</param>
/// <param name="Total">How many rows matched, beyond the ones read.</param>
/// <param name="EnvironmentName">
/// <c>IHostEnvironment.EnvironmentName</c>, or <see langword="null" /> when the host registers none - read the way K01
/// reads it.
/// </param>
/// <param name="Platform">Who runs the server, for the settings a provider documents as its own design.</param>
internal sealed record DangerousSettingsFacts(
    DateTimeOffset ReadAt,
    IReadOnlyList<ServerSetting> Settings,
    long Total,
    string? EnvironmentName,
    PostgresPlatform Platform)
{
    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => string.Equals(EnvironmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>The setting called <paramref name="name" />, or <see langword="null" />.</summary>
    public ServerSetting? Find(string name) => Settings.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// S02: a server setting that blinds the store's maintenance, or that trades its durability or its integrity for speed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it judges, and how hard</b> (plan R1, candidate 7):
/// </para>
/// <list type="bullet">
/// <item><b>Critical anywhere</b>: <c>track_counts = off</c>. Autovacuum decides which tables to vacuum and analyse from
/// those counters, so with it off nothing is vacuumed but to prevent wraparound - Marten's upserted documents bloat and
/// its statistics go stale - and every statistics-based check here goes blind with it. And <c>autovacuum = off</c> (W5-V1, issue
/// #8 G2), which leaves every table to the forced anti-wraparound vacuum alone, whatever its own storage parameters say.</item>
/// <item><b>Warning</b>: <c>track_activities = off</c> (W5-V1), which blinds the session checks C04, C05 and T14 to every
/// session's state and age.</item>
/// <item><b>Critical in Production</b>: <c>fsync = off</c> and <c>full_page_writes = off</c>, which give up crash safety
/// (a power cut can corrupt the cluster, not merely lose a transaction), and <c>zero_damaged_pages = on</c> and
/// <c>ignore_checksum_failure = on</c>, which hide corruption by destroying or using the damaged page. A development
/// database that turns the first two off for speed is a reasonable choice, so outside Production they are a note. The
/// environment is read as K01 reads it.</item>
/// <item><b>Info</b>: <c>synchronous_commit = off</c> - a crash can lose the last few hundred milliseconds of commits
/// that the application was told succeeded, which an event store's readers may already have acted on - and any
/// setting whose change waits for a restart (<c>pending_restart</c>), which is a configuration the server does not run
/// yet.</item>
/// </list>
/// <para>
/// <b>Never</b> a sizing setting - <c>shared_buffers</c>, <c>work_mem</c>, <c>random_page_cost</c> and the rest (R1 §D,
/// "RAM-based tuning advice would cry wolf"). The list is exactly the one above.
/// </para>
/// <para>
/// <b>As the store sees them.</b> The studio's connection comes from the store's own data source, so a value set for its
/// role, its database or in its connection string's startup options is the value the store's sessions run with - and
/// <c>pg_settings.source</c> says which, so the remedy can point at the right place.
/// </para>
/// <para>
/// <b>A provider's documented design is muted; an undocumented one is reported, softly.</b> Neon documents
/// <c>fsync = off</c> as its own ("Neon syncs data to the Neon database storage engine", <i>Postgres compatibility</i>), so on
/// a server the detector takes for Neon it is a note. Aurora's and Neon's storage replace Postgres's own, but neither
/// documents what its servers report for <c>full_page_writes</c> (nor Aurora for <c>fsync</c>), so an <c>off</c> there is
/// Info - "the provider manages durability; Marten Studio cannot verify this setting's meaning" - never Critical
/// (<see cref="ManagesDurability" />).
/// </para>
/// <para>
/// <b>Not read is not safe.</b> A watched setting with no row - hidden from the role, or absent from the server - is named
/// in the status row as not judged, rather than counted among the ones at their safe values.
/// </para>
/// <para>
/// <b>Two kinds of finding, each a variant</b> (API design §1.10): <see cref="SettingVariant" />, one per watched setting at
/// a dangerous value, whose subject is the setting; and <see cref="PendingRestartVariant" />, one for every setting that waits
/// for a restart together, with no subject.
/// </para>
/// </remarks>
[HealthCheck("S02", HealthArea.Server, HealthCostTier.Catalog, "Settings that blind or endanger the store",
    Variants = [SettingVariant, PendingRestartVariant])]
internal sealed class DangerousSettingsCheck : HealthCheck<DangerousSettingsFacts>
{
    /// <summary>The variant of a finding about one watched setting; its subject is the setting's name.</summary>
    internal const string SettingVariant = "setting";

    /// <summary>The variant of the finding about settings that wait for the server to restart.</summary>
    internal const string PendingRestartVariant = "pending-restart";

    /// <summary>The settings S02 reads, and no others.</summary>
    public static readonly IReadOnlyList<string> Watched =
    [
        "track_counts", "fsync", "full_page_writes", "zero_damaged_pages", "ignore_checksum_failure", "synchronous_commit",
        "autovacuum", "track_activities",
    ];

    /// <summary>How many settings waiting for a restart a finding lists.</summary>
    public const int PendingNamed = 10;

    /// <inheritdoc />
    public override async Task<DangerousSettingsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = HealthServerQueries.Settings(Watched);
        IReadOnlyList<(ServerSetting Setting, long Total)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (
                    new ServerSetting(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3))
                    {
                        Context = reader.IsDBNull(5) ? null : reader.GetString(5),
                    },
                    reader.GetInt64(4)),
                cancellationToken)
            .ConfigureAwait(false);

        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);
        string? environment = context.Services.GetService<IHostEnvironment>()?.EnvironmentName;

        return new DangerousSettingsFacts(
            context.Now,
            [.. rows.Select(static x => x.Setting)],
            rows.Count > 0 ? rows[0].Total : 0,
            string.IsNullOrWhiteSpace(environment) ? null : environment,
            server.Platform);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DangerousSettingsFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        List<string> notes = [];

        if (Is(facts.Find("track_counts"), "off") is { } trackCounts)
        {
            findings.Add(Setting(
                facts,
                trackCounts,
                HealthSeverity.Critical,
                "Postgres is not counting changes to tables (track_counts = off), so autovacuum cannot tell which ones need it",
                "Autovacuum chooses what to vacuum and analyse from the per-table counters track_counts keeps. With it off, nothing is "
                + "vacuumed except to stop transaction-id wraparound: " + evaluation.Text.UnclearedDeadRows
                + ", its tables bloat, the planner's statistics go stale, and the studio's own table checks - and its "
                + "~ estimated counts - have nothing to read.",
                "on",
                evaluation.Text));
        }

        // W5-V1 (issue #8 G2): autovacuum switched off for the whole server is Critical anywhere, as track_counts is.
        if (Is(facts.Find("autovacuum"), "off") is { } autovacuum)
        {
            findings.Add(Setting(
                facts,
                autovacuum,
                HealthSeverity.Critical,
                "Autovacuum is switched off for the whole server (autovacuum = off)",
                "Nothing vacuums or analyses a table by itself any more: dead rows pile up in every table, statistics go stale, the "
                + "visibility map index-only scans read is never set, and the only vacuum that still comes is the forced one at "
                + "autovacuum_freeze_max_age, which scans a whole table at once. No table's own storage parameter can switch it back "
                + "on; the studio's table checks (T07, T08) read every table as switched off.",
                "on",
                evaluation.Text));
        }

        // W5-V1 (2026-10-26 health-gap analysis): without track_activities a session's state and statement are not recorded.
        if (Is(facts.Find("track_activities"), "off") is { } activities)
        {
            findings.Add(Setting(
                facts,
                activities,
                HealthSeverity.Warning,
                "Postgres is not recording what sessions are doing (track_activities = off)",
                "pg_stat_activity then shows no state, no statement and no times for a session, so the session checks C04, C05 and "
                + "T14 cannot see sessions' state or age: a transaction left idle, a statement running for an hour or a snapshot "
                + "holding vacuum back goes unseen, and Database › Sessions shows sessions without what they are doing.",
                "on",
                evaluation.Text));
        }

        foreach ((string name, string bad, string title, Func<StudioText, string> whyIn) in ProductionOnly)
        {
            if (Is(facts.Find(name), bad) is not { } setting)
            {
                continue;
            }

            string why = whyIn(evaluation.Text);

            if (facts.Platform.Kind == PostgresPlatformKind.Neon && name == "fsync")
            {
                notes.Add("fsync = " + setting.Value + " is Neon's documented design: its storage engine, not the compute's disk, makes commits durable.");
                continue;
            }

            if (!facts.IsProduction)
            {
                notes.Add(name + " = " + setting.Value + (facts.EnvironmentName is null
                    ? ", and the host registers no IHostEnvironment that names an environment, so it is not judged."
                    : "; this is " + facts.EnvironmentName + ", and it is judged only in Production."));
                continue;
            }

            if (ManagesDurability(facts.Platform, name))
            {
                findings.Add(Setting(
                    facts,
                    setting,
                    HealthSeverity.Info,
                    name + " = " + setting.Value + " on " + facts.Platform.Name + ", whose storage manages durability itself",
                    "On a server's own disk this would be Critical: " + why + " " + facts.Platform.Name + " replaces Postgres's storage with "
                    + "its own, and the provider manages durability - " + evaluation.Text.ProductName + " cannot verify this setting's meaning on "
                    + facts.Platform.Name + ", so it reports the value rather than judging it. The provider's documentation, or its support, "
                    + "is where to confirm it.",
                    bad == "off" ? "on" : "off",
                    evaluation.Text) with
                {
                    Remedy = null,
                });

                continue;
            }

            findings.Add(Setting(facts, setting, HealthSeverity.Critical, title, why, bad == "off" ? "on" : "off", evaluation.Text));
        }

        if (Is(facts.Find("synchronous_commit"), "off") is { } synchronous)
        {
            findings.Add(Setting(
                facts,
                synchronous,
                HealthSeverity.Info,
                "Commits are acknowledged before they reach disk (synchronous_commit = off)",
                "A commit returns before its WAL is flushed, so a crash of the server - not of the application - loses the last few "
                + "hundred milliseconds of transactions the application was told had committed." + evaluation.Text.LostCommitsMeaning
                + " It never corrupts the database; it only forgets.",
                "on",
                evaluation.Text));
        }
        else if (facts.Find("synchronous_commit") is { } other && !string.Equals(other.Value, "on", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(SynchronousCommit(other.Value));
        }

        // A watched setting with no row is not judged, and is said to be: a role or a server that hides one is not a
        // server where it is safe.
        List<string> unread = [.. Watched.Where(x => facts.Find(x) is null)];
        if (unread.Count > 0)
        {
            notes.Add("Could not read " + string.Join(", ", unread) + ": this role is not shown "
                + (unread.Count == 1 ? "it" : "them") + ", or this server does not have " + (unread.Count == 1 ? "it" : "them") + ", so "
                + (unread.Count == 1 ? "it is" : "they are") + " not judged.");
        }

        List<ServerSetting> pending = [.. facts.Settings.Where(static x => x.PendingRestart).OrderBy(static x => x.Name, StringComparer.Ordinal)];
        if (pending.Count > 0)
        {
            findings.Add(new HealthFinding
            {
                Code = "S02",
                Severity = HealthSeverity.Info,
                Variant = PendingRestartVariant,
                Title = pending.Count == 1
                    ? "A changed setting waits for the server to restart"
                    : SessionText.Number(pending.Count) + " changed settings wait for the server to restart",
                WhyItMatters =
                    "Somebody changed these settings in the server's configuration, and they take effect only at the next restart. "
                    + "Until then the server runs the old values - and the restart, whenever it comes, will change its behaviour with no "
                    + "deployment to point at.",
                Evidence =
                [
                    new HealthEvidence("Waiting for a restart", string.Join(", ", pending.Take(PendingNamed).Select(static x => x.Name))
                        + (pending.Count > PendingNamed ? " and " + SessionText.Number(pending.Count - PendingNamed) + " more" : string.Empty)),
                    new HealthEvidence("Platform", facts.Platform.Name),
                ],
                Caveat = Caveat(facts, evaluation.Text),
            });
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings, notes.Count > 0 ? string.Join(" ", notes) : null);
        }

        return HealthVerdict.Quiet(notes.Count > 0
            ? string.Join(" ", notes) + " Everything else S02 judges is at its safe value, and nothing waits for a restart."
            : "track_counts, autovacuum, track_activities, fsync, full_page_writes, zero_damaged_pages and ignore_checksum_failure are "
              + "at their safe values, synchronous_commit is on, and nothing waits for a restart.");
    }

    /// <summary>
    /// Whether <paramref name="platform" /> replaces Postgres's storage with its own, so that what <paramref name="setting" />
    /// (<c>fsync</c> or <c>full_page_writes</c>) means there is the provider's to say: Aurora for both, and Neon for
    /// <c>full_page_writes</c>. Neither provider documents what its servers report for them (checked 2026-10-18: the Aurora
    /// User Guide's parameter reference lists neither; Neon's <i>Postgres compatibility</i> documents <c>fsync = off</c> only,
    /// and neondatabase/postgres#822 reduces full-page images rather than turning them off), so a value of <c>off</c> there is
    /// reported as Info, never Critical, and never muted as a documented design is.
    /// </summary>
    internal static bool ManagesDurability(PostgresPlatform platform, string setting) => platform.Kind switch
    {
        PostgresPlatformKind.AmazonAurora => setting is "fsync" or "full_page_writes",
        PostgresPlatformKind.Neon => setting == "full_page_writes",
        _ => false,
    };

    /// <summary>A <c>synchronous_commit</c> other than <c>on</c> and <c>off</c>, in words: none of them loses a commit here.</summary>
    internal static string SynchronousCommit(string value) => value.ToLowerInvariant() switch
    {
        "local" => "synchronous_commit = local: a commit waits for this server's own WAL flush - it is durable here - and never for a "
            + "synchronous standby.",
        "remote_write" => "synchronous_commit = remote_write: a commit waits for this server's own WAL flush and, where a synchronous "
            + "standby is configured, for the standby to have handed the WAL to its operating system rather than flushed it.",
        "remote_apply" => "synchronous_commit = remote_apply: a commit waits for this server's own WAL flush and, where a synchronous "
            + "standby is configured, for the standby to have applied it.",
        _ => "synchronous_commit = " + value + ".",
    };

    /// <summary>The settings that are Critical in Production only: what is bad, and what it costs in the studio's words.</summary>
    private static readonly (string Name, string Bad, string Title, Func<StudioText, string> Why)[] ProductionOnly =
    [
        ("fsync", "off",
            "Postgres does not wait for its writes to reach disk (fsync = off), in Production",
            static _ => "With fsync off, a power cut or an operating-system crash can leave the database corrupted - not only missing the last "
            + "transactions, but with tables and indexes that no longer agree - and the only cure is the last backup. Postgres's own "
            + "documentation says to turn it off only for a database that can be recreated from scratch."),
        ("full_page_writes", "off",
            "Postgres does not protect pages against a torn write (full_page_writes = off), in Production",
            static _ => "A page half-written when the power went cannot be repaired from the WAL without the full-page image this setting writes, "
            + "so crash recovery can leave a corrupted page behind. It is safe only on storage that guarantees an 8 kB page is written "
            + "whole, which ordinary disks and most cloud volumes do not promise."),
        ("zero_damaged_pages", "on",
            "Postgres replaces any damaged page it reads with zeros (zero_damaged_pages = on), in Production",
            static text => "Every page that fails to read cleanly is silently turned into an empty one, and the rows on it are gone for good - "
            + text.ZeroedPageRows + ". It is a last resort for salvaging a broken database, "
            + "set for one session by a superuser, never a way to run one."),
        ("ignore_checksum_failure", "on",
            "Postgres goes on using pages whose checksum does not match (ignore_checksum_failure = on), in Production",
            static _ => "A checksum failure is the one warning Postgres gets that a page changed on disk behind its back. With this on, it reads "
            + "the damaged page as though it were good, and the corruption spreads into whatever is written from it. See S03 for "
            + "failures already counted."),
    ];

    private static ServerSetting? Is(ServerSetting? setting, string value) =>
        setting is not null && string.Equals(setting.Value, value, StringComparison.OrdinalIgnoreCase) ? setting : null;

    private static HealthFinding Setting(
        DangerousSettingsFacts facts,
        ServerSetting setting,
        HealthSeverity severity,
        string title,
        string why,
        string safeValue,
        StudioText text)
    {
        List<HealthEvidence> evidence =
        [
            new("Setting", setting.Name + " = " + setting.Value),
            new("Set by", SetBy(setting.Source)),
            new("Environment", facts.EnvironmentName ?? "not named by the host"),
            new("Platform", facts.Platform.Name),
        ];

        return new HealthFinding
        {
            Code = "S02",
            Severity = severity,
            Variant = SettingVariant,
            Subject = setting.Name,
            Title = title,
            WhyItMatters = why,
            Evidence = evidence,
            Caveat = Caveat(facts, text),
            Remedy = ServerSettingRemedy.Build(
                facts.Platform,
                setting.Name,
                safeValue,
                text,
                setting.Context,
                ":",
                where => Where(setting.Source, where, text)),
        };
    }

    /// <summary><c>pg_settings.source</c>, in words.</summary>
    internal static string SetBy(string? source) => source switch
    {
        "default" => "Postgres's own default",
        "configuration file" => "the server's configuration file",
        "command line" => "the server's command line",
        "environment variable" => "an environment variable of the server",
        "database" => "the database's own settings (ALTER DATABASE … SET)",
        "user" => "the role's own settings (ALTER ROLE … SET)",
        "database user" => "the role's settings in this database (ALTER ROLE … IN DATABASE … SET)",
        "client" => "the connection itself: this data source's connection string sends it as a startup option",
        "session" => "the session that read it",
        "override" => "Postgres itself, which forces this value",
        null => "not known",
        _ => source,
    };

    /// <summary>
    /// Where the remedy goes, from where the setting was made - <paramref name="where" /> being the platform's place for it
    /// (<see cref="ServerSettingRemedy.WhereToSet" />), which only a setting made in the server's own configuration needs.
    /// </summary>
    private static string Where(string? source, string where, StudioText text) => source switch
    {
        "client" => "It arrives with the connection: remove it from the Options of the connection string " + SourceWords.DataSource(text)
            + " is built from, and every new connection runs with the server's value.",
        "database" or "user" or "database user" => "It is set for this role or this database, not for the server: whoever owns the "
            + "role or the database resets it there, and new sessions pick the server's value up.",
        _ => "Set it in " + where + ".",
    };

    private static string Caveat(DangerousSettingsFacts facts, StudioText text) =>
        "Read in pg_settings at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
        + ", " + text.SettingsReadOn + ". Only the settings named in this check's documentation are judged; sizing "
        + "settings never are.";
}
