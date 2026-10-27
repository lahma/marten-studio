using System.Globalization;
using System.Text;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>What the Health page's Snapshot menu hands the browser: the JSON and the name to save it under.</summary>
/// <param name="Json">The snapshot, <c>schemaVersion</c> 1.</param>
/// <param name="FileName">
/// <c>{studio}-snapshot-{source}-{database}-{yyyyMMdd'T'HHmmss'Z'}.json</c>, each name kept to <c>[A-Za-z0-9._-]</c>.
/// </param>
internal sealed record VisitorSnapshot(string Json, string FileName);

/// <summary>
/// The studio's snapshot download (API design §2.4 and §4.3): one visitor's view of one database as a
/// <c>schemaVersion</c> 1 file, built in that visitor's circuit from each section's own page service, as that visitor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pages' gates, never the API's.</b> It asks the Health service for the report (gated per finding and per piece of
/// evidence), and the Storage, Statements, Server and Sessions services for their views - so the file holds exactly what this
/// visitor's screens hold, masked the same way, and a section the visitor is short of is a <c>withheld</c> entry carrying that
/// page's own sentence. It never reaches the diagnostics API or its host gate, which exist only for host code; the convention
/// fence (<c>DiagnosticsApiIsNotServedTests</c>) holds it to that.
/// </para>
/// <para>
/// <b>It starts nothing costlier than the pages do.</b> The report is asked with <see cref="HealthDeeperChecks.IfCached" /> for
/// both costlier tiers and the statements from the last statement sweep that finished (<see cref="IStatementsService.GetLatestAsync" />),
/// so a download never runs a deeper or a statement sweep the visitor did not start themselves.
/// </para>
/// <para>
/// <b>No audit of its own.</b> The services it calls record what they record for a page view - the first reveal of
/// Activity-grade evidence or sessions in a circuit - and the download reveals nothing those views do not.
/// </para>
/// <para>
/// <b>The server section</b> is the Server page's, at the database-wide gate, version and platform included: one gate for the
/// whole object rather than a split no page makes. Compute is the Health page's server line, at the same gate.
/// </para>
/// <para>
/// <b>What the download leaves null.</b> The <c>wal</c> section: it is the Replication page's facts, which this builder asks
/// no service for and whose gate it would be a second one, so the visitor's file carries <c>wal: null</c> and the API's snapshot
/// is where a baseline reads it. The checksum failures and the all-visible ratio are carried: the Server and Storage pages
/// already hold them, at the gates those pages apply.
/// </para>
/// </remarks>
internal sealed class VisitorSnapshotBuilder
{
    private readonly IHealthService health;
    private readonly IStorageService storage;
    private readonly IStatementsService statements;
    private readonly IServerPageService server;
    private readonly ISessionActivityService sessions;
    private readonly HealthCheckRegistry registry;
    private readonly IStudioSettings settings;
    private readonly StudioDescriptor descriptor;
    private readonly TimeProvider timeProvider;

    /// <param name="health">The Health page's service.</param>
    /// <param name="storage">The Storage page's service.</param>
    /// <param name="statements">The Statements page's service.</param>
    /// <param name="server">The Server page's service.</param>
    /// <param name="sessions">Database › Sessions' service.</param>
    /// <param name="registry">The checks, for their variants.</param>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio, for its key.</param>
    /// <param name="timeProvider">The clock.</param>
    public VisitorSnapshotBuilder(
        IHealthService health,
        IStorageService storage,
        IStatementsService statements,
        IServerPageService server,
        ISessionActivityService sessions,
        HealthCheckRegistry registry,
        IStudioSettings settings,
        StudioDescriptor descriptor,
        TimeProvider? timeProvider = null)
    {
        this.health = health;
        this.storage = storage;
        this.statements = statements;
        this.server = server;
        this.sessions = sessions;
        this.registry = registry;
        this.settings = settings;
        this.descriptor = descriptor;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The visitor's snapshot of <paramref name="scope" />: each page's view, as this visitor, as one file.</summary>
    /// <param name="scope">The visitor's scope.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<VisitorSnapshot> BuildAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        HealthReport report = await health.GetReportAsync(scope, HealthReportRequest.Regular, cancellationToken).ConfigureAwait(false);
        StorageView storageView = await storage.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        StatementsView statementsView = await statements.GetLatestAsync(scope, cancellationToken).ConfigureAwait(false);
        ServerPageView serverView = await server.GetAsync(scope, cancellationToken).ConfigureAwait(false);
        SessionActivitySummary summary = await sessions.GetSummaryAsync(scope, cancellationToken).ConfigureAwait(false);

        // The details only for a visitor the summary says may have them, as Database > Sessions asks: a closed gate is the
        // summary's sentence, never a refused request - D27's "using a page whose gate is closed refuses nothing", so no
        // audit entry and no 9202/9203 for a click on the menu.
        SessionActivityDetails details = summary is { Refusal: DatabaseRefusal.None, Details.IsOpen: true }
            ? await sessions.GetDetailsAsync(scope, cancellationToken).ConfigureAwait(false)
            : NotAsked(summary);

        DateTimeOffset now = timeProvider.GetUtcNow();
        PostgresDiagnosticsSnapshot snapshot = Compose(
            new PostgresDatabaseRef(scope.StoreKey, scope.DatabaseId),
            report,
            storageView,
            statementsView,
            serverView,
            summary,
            details,
            registry,
            DiagnosticsJson.ProducerFor(settings.Text.Product),
            now);

        return new VisitorSnapshot(snapshot.ToJson(), FileName(descriptor.Key, scope.StoreKey, scope.DatabaseId, now));
    }

    /// <summary>
    /// The details a visitor the summary already shuts out is not asked for: refused with the summary's own sentence - what it
    /// would take, naming the option or the policy - so the file says why without the service recording a refusal.
    /// </summary>
    internal static SessionActivityDetails NotAsked(SessionActivitySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        DatabaseRefusal refusal = summary.Refusal != DatabaseRefusal.None
            ? summary.Refusal
            : summary.Details.Refusal != DatabaseRefusal.None ? summary.Details.Refusal : DatabaseRefusal.WritePolicy;

        return SessionActivityDetails.Refused(refusal, summary.Details.Denial ?? summary.Reason ?? "withheld");
    }

    /// <summary>
    /// <c>{studio}-snapshot-{source}-{database}-{yyyyMMdd'T'HHmmss'Z'}.json</c> (API design §4.3): each name kept to
    /// <c>[A-Za-z0-9._-]</c>, the rest replaced with <c>-</c>; an empty database id - a source's only database - is <c>default</c>.
    /// </summary>
    internal static string FileName(string studio, string source, string database, DateTimeOffset at) =>
        Slug(studio) + "-snapshot-" + Slug(source) + "-" + Slug(database.Length == 0 ? "default" : database) + "-"
        + at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".json";

    private static string Slug(string name)
    {
        StringBuilder slug = new(name.Length);

        foreach (char c in name)
        {
            slug.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return slug.Length == 0 ? "-" : slug.ToString();
    }

    /// <summary>
    /// The snapshot from the pages' views, every one already this visitor's: pure, so each gate's effect on the file is a unit
    /// test's.
    /// </summary>
    internal static PostgresDiagnosticsSnapshot Compose(
        PostgresDatabaseRef database,
        HealthReport report,
        StorageView storageView,
        StatementsView statementsView,
        ServerPageView serverView,
        SessionActivitySummary summary,
        SessionActivityDetails details,
        HealthCheckRegistry registry,
        string producer,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(storageView);
        ArgumentNullException.ThrowIfNull(statementsView);
        ArgumentNullException.ThrowIfNull(serverView);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(registry);

        SortedDictionary<string, string> withheld = new(StringComparer.Ordinal);

        PostgresDiagnosticsSnapshot snapshot = new()
        {
            SchemaVersion = DiagnosticsJson.SchemaVersion,
            Producer = producer,
            GeneratedAt = now,
            Database = database,
        };

        if (report.Refusal != HealthRefusal.None)
        {
            // Nothing about the database is shown to a visitor the store policy refuses, or about one that is not there.
            return snapshot with
            {
                Outcome = report.Refusal == HealthRefusal.StorePolicy ? PostgresReadOutcome.Withheld : PostgresReadOutcome.Unavailable,
                Reason = report.Reason,
            };
        }

        List<PostgresFinding> findings = [.. report.Findings.Select(DiagnosticsMapper.Finding)];
        findings.Sort(DiagnosticsMapper.Compare);

        List<PostgresCheck> checks = [.. report.Checks.Select(x => DiagnosticsMapper.Check(x, registry.Find(x.Code)?.Info))];
        checks.Sort(static (a, b) => string.CompareOrdinal(a.Code, b.Code));

        if (report.WithheldCount > 0 && report.WithheldReason is { } findingsWithheld)
        {
            withheld["findings"] = findingsWithheld;
        }

        (PostgresServerInfo? serverInfo, PostgresSettingsSection? settingsSection) = Server(serverView, withheld);

        PostgresComputeCapacity? compute = report.Server?.Compute;
        if (report.Server is null && report.CostWithheld is { } databaseWide)
        {
            // The Health page's server line is the database-wide gate's; so is a cost line, which says why when it is withheld.
            withheld["compute"] = databaseWide;
        }

        return snapshot with
        {
            Outcome = PostgresReadOutcome.Read,
            Server = serverInfo,
            Compute = compute,
            Findings = findings,
            Checks = checks,
            FindingsMeasuredAt = report.MeasuredAt,
            FindingsWithheld = report.WithheldCount,
            Storage = Storage(storageView, withheld),
            Statements = Statements(statementsView, withheld),
            Settings = settingsSection,
            Connections = Connections(summary, details, withheld),
            Withheld = withheld,
        };
    }

    private static PostgresStorageSection? Storage(StorageView view, SortedDictionary<string, string> withheld)
    {
        if (view.Refusal != DatabaseRefusal.None)
        {
            withheld["storage"] = view.Reason ?? "withheld";
            return null;
        }

        List<string> reasons = [];
        AddSentence(reasons, view.DatabaseBytesNotRead);
        AddSentence(reasons, view.GrowthPending);
        AddSentence(reasons, view.WindowNotRead);
        AddSentence(reasons, view.TablesNotRead);

        if (view.Tables.Any(static x => x.Withheld) && view.WithheldReason is { } tables)
        {
            // A table in a schema the database browser withholds from this visitor is left out here, as the page leaves out
            // its name; the page's own sentence says why.
            withheld["storage.largestTables"] = tables;
        }

        return new PostgresStorageSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = reasons.Count == 0 ? null : string.Join(" ", reasons),
            MeasuredAt = view.MeasuredAt ?? default,
            DatabaseBytes = view.DatabaseBytes ?? view.EstimatedDatabaseBytes,
            SizesAreEstimates = true,
            StatisticsSince = view.Window?.Since,
            GrowthBytesPerDay = Whole(view.GrowthBytesPerDay),
            LargestTables =
            [
                .. view.Tables.Where(static x => !x.Withheld).Select(static x => new PostgresTableStorage
                {
                    Schema = x.Schema ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    Ownership = x.Ownership is { } ownership ? DiagnosticsMapper.Ownership(ownership) : PostgresTableOwnership.Other,
                    Owner = x.Owner,
                    TotalBytes = x.TotalBytes,
                    TableBytes = x.HeapBytes,
                    IndexBytes = x.IndexBytes,
                    ToastBytes = x.ToastBytes,
                    EstimatedRows = x.EstimatedRows,
                    ShareOfDatabase = x.ShareOfDatabase ?? 0,
                    HotUpdateRatio = x.HotUpdateRatio,
                    GrowthBytesPerDay = Whole(x.GrowthBytesPerDay),
                    AllVisibleRatio = DiagnosticsMapper.Ratio(x.AllVisibleRatio),
                }),
            ],
        };
    }

    private static PostgresStatementsSection? Statements(StatementsView view, SortedDictionary<string, string> withheld)
    {
        if (view.Refusal != DatabaseRefusal.None)
        {
            // The Statements page's activity gate: needs ViewDatabaseActivity, then the store and write policies.
            withheld["statements"] = view.Reason ?? "withheld";
            return null;
        }

        if (view.State != StatementsState.Read)
        {
            return new PostgresStatementsSection
            {
                Outcome = view.State == StatementsState.Failed ? PostgresReadOutcome.Failed : PostgresReadOutcome.Unavailable,
                Reason = view.Status,
                MeasuredAt = view.MeasuredAt ?? default,
            };
        }

        List<string> reasons = [];
        AddSentence(reasons, view.WindowText);
        AddSentence(reasons, view.TextsWithheld);

        return new PostgresStatementsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = reasons.Count == 0 ? null : string.Join(" ", reasons),
            MeasuredAt = view.MeasuredAt ?? default,
            TotalMilliseconds = view.TotalExecMs,
            Top =
            [
                .. view.Rows
                    .Where(static x => x.Window is not null)
                    .OrderByDescending(static x => x.Window!.ExecMs)
                    .ThenBy(static x => x.QueryId)
                    .Take(DiagnosticsMapper.MaxStatements)
                    .Select(static x => new PostgresStatement
                    {
                        QueryId = x.QueryId,

                        // The page's text, masked for this visitor and capped where it was read; where it shows none, its note.
                        Text = x.Text,
                        TextTruncated = x.Text is not null && x.TextNote is not null,
                        TextWithheld = x.Text is null ? x.TextNote ?? "withheld" : null,
                        Calls = (long) x.Window!.Calls,
                        TotalMilliseconds = x.Window.ExecMs,
                        MeanMilliseconds = x.Window.MeanMs ?? 0,
                        ShareOfTime = x.Share ?? 0,
                        Rows = (long) x.Window.Rows,
                        SharedBlocksHit = (long) x.Window.SharedBlocksHit,
                        SharedBlocksRead = (long) x.Window.SharedBlocksRead,
                        TempBlocksWritten = (long) x.Window.TempBlocksWritten,
                        StorageBlocksRead = x.Window.StorageBlocksRead is { } read ? (long) read : null,
                    }),
            ],
        };
    }

    private static (PostgresServerInfo? Server, PostgresSettingsSection? Settings) Server(ServerPageView view, SortedDictionary<string, string> withheld)
    {
        if (view.Refusal == ServerRefusal.StorePolicy)
        {
            withheld["server"] = view.Reason ?? "withheld";
            withheld["settings"] = view.Reason ?? "withheld";
            return (null, null);
        }

        if (view.Refusal == ServerRefusal.Unavailable)
        {
            return (null, new PostgresSettingsSection
            {
                Outcome = PostgresReadOutcome.Unavailable,
                Reason = view.Reason,
                MeasuredAt = view.MeasuredAt,
            });
        }

        List<string> reasons = [];
        AddSentence(reasons, view.SettingsNote);
        AddSentence(reasons, view.ExtensionsNote);

        return (view.Server?.ToServerInfo(), new PostgresSettingsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = reasons.Count == 0 ? null : string.Join(" ", reasons),
            MeasuredAt = view.MeasuredAt,
            Settings = [.. view.Settings.SelectMany(static x => x.Settings).Select(static x => DiagnosticsMapper.Setting(x.Row))],
            Extensions = [.. view.Extensions.Select(DiagnosticsMapper.Extension)],
            DataChecksums = ServerConnectionPosture.ChecksumsState(view.DataChecksums),
            ChecksumFailures = view.ChecksumFailures,
            LastChecksumFailure = view.LastChecksumFailure,
        });
    }

    private static PostgresConnectionsSection? Connections(
        SessionActivitySummary summary,
        SessionActivityDetails details,
        SortedDictionary<string, string> withheld)
    {
        if (summary.Refusal != DatabaseRefusal.None)
        {
            withheld["connections"] = summary.Reason ?? "withheld";
            return null;
        }

        if (summary.Connections is not { } total || summary.Budget is not { } budget)
        {
            return new PostgresConnectionsSection
            {
                Outcome = PostgresReadOutcome.Unavailable,
                Reason = "Database › Sessions did not read connections by application.",
                MeasuredAt = summary.MeasuredAt ?? default,
            };
        }

        // The totals are the database-wide gate's; the names are the activity gate's - Database › Sessions' details.
        bool named = details.Refusal == DatabaseRefusal.None;
        if (!named)
        {
            withheld["connections.byApplication"] = details.Reason ?? summary.Details.Denial ?? "withheld";
        }

        bool capped = named && details.ApplicationCount > details.Applications.Count;

        return new PostgresConnectionsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = capped
                ? "Only the " + details.Applications.Count.ToString(CultureInfo.InvariantCulture) + " applications with the most sessions, of "
                  + details.ApplicationCount.ToString(CultureInfo.InvariantCulture) + ", are listed; the database's total counts them all."
                : null,
            MeasuredAt = summary.MeasuredAt ?? default,
            MaxConnections = budget.MaxConnections,
            ReservedConnections = budget.Reserved,
            ServerConnections = DiagnosticsMapper.Count(budget.ServerConnections),
            Database = DiagnosticsMapper.Counts(total),
            ByApplication = named ? [.. details.Applications.Select(DiagnosticsMapper.Counts)] : [],
            AllSessionsVisible = budget.AllSessionsVisible,
        };
    }

    private static void AddSentence(List<string> sentences, string? sentence)
    {
        if (sentence is { Length: > 0 } text)
        {
            sentences.Add(text.EndsWith('.') ? text : text + ".");
        }
    }

    private static long? Whole(double? value) => value is { } number && double.IsFinite(number) ? (long) Math.Round(number) : null;
}
