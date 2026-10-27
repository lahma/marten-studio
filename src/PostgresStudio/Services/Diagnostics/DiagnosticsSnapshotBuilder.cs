using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The diagnostics API's sections, from the facts the pages read (API design §6.2): never a second read of anything - the
/// sweep's results, the Storage page's facts, the Statements page's facts, the Server page's facts, the Replication page's facts
/// (the write-ahead log) and the sessions snapshot -
/// shown through the host gate. Pure, so every field's source is a unit test's.
/// </summary>
internal static class DiagnosticsSnapshotBuilder
{
    /// <summary>The most statements the statements section lists: the Statements page's own most.</summary>
    public const int MaxStatements = DiagnosticsMapper.MaxStatements;

    /// <summary>The most characters of a statement's text: the statement checks' read, as the Statements page shows it.</summary>
    public const int StatementCharacters = HealthStatementQueries.MaxTextCharacters;

    /// <summary>
    /// The findings and every check's outcome, from the sweeps' results through the host gate: a finding the gate withholds
    /// is counted and never described, and a check whose gate it is short of reads <see cref="PostgresReadOutcome.Withheld" />.
    /// </summary>
    /// <param name="results">Every check's part of the sweeps, regular first.</param>
    /// <param name="gate">The host gate.</param>
    /// <param name="runtime">The studio's runtime.</param>
    /// <param name="sourceParameter">The studio's source parameter, for the findings' links.</param>
    /// <param name="scope">The database, for the findings' links.</param>
    public static (IReadOnlyList<PostgresFinding> Findings, IReadOnlyList<PostgresCheck> Checks, int Withheld) Findings(
        IEnumerable<HealthCheckResult> results,
        HostGate gate,
        IStudioRuntime runtime,
        string sourceParameter,
        StudioScope scope)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(runtime);

        List<PostgresFinding> findings = [];
        List<PostgresCheck> checks = [];
        int withheld = 0;

        foreach (HealthCheckResult result in results)
        {
            int included = 0;

            foreach (HealthFinding finding in result.Findings)
            {
                if (!gate.Shows(finding))
                {
                    withheld++;
                    continue;
                }

                findings.Add(DiagnosticsMapper.Finding(finding, result.Check, gate, runtime, sourceParameter, scope));
                included++;
            }

            checks.Add(DiagnosticsMapper.Check(result, gate, included));
        }

        findings.Sort(DiagnosticsMapper.Compare);
        checks.Sort(static (a, b) => string.CompareOrdinal(a.Code, b.Code));

        return (findings, checks, withheld);
    }

    /// <summary>
    /// The storage section from the Storage page's facts. A hidden document type's table is left out, as from every list; a
    /// table in a schema the database browser withholds from visitors is named, because the host's role reads <c>pg_class</c>
    /// (API design §2.3).
    /// </summary>
    /// <param name="facts">The facts.</param>
    /// <param name="settings">The studio's settings, for the document-type filter.</param>
    public static PostgresStorageSection Storage(StorageFacts facts, IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(settings);

        long share = facts.ShareBase;
        StatisticsWindow? window = facts.Window;
        List<string> reasons = [];

        if (facts.DatabaseBytesNotRead is { } notMeasured)
        {
            reasons.Add(notMeasured + " The database's size is the catalog's estimate instead.");
        }

        if (facts.GrowthBytesPerDay is null)
        {
            reasons.Add("No growth figure yet: " + (window is null
                ? facts.WindowNotRead ?? "the statistics window was not read."
                : StorageService.GrowthPendingText(window) + "."));
        }

        if (facts.TablesNotRead is { } tables)
        {
            reasons.Add(tables);
        }

        return new PostgresStorageSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = reasons.Count == 0 ? null : string.Join(" ", reasons),
            MeasuredAt = facts.MeasuredAt,
            DatabaseBytes = facts.DatabaseBytes ?? facts.EstimatedDatabaseBytes,
            SizesAreEstimates = facts.SizesAreEstimates,
            StatisticsSince = window?.Since,
            GrowthBytesPerDay = Whole(facts.GrowthBytesPerDay),
            LargestTables =
            [
                .. facts.LargestTables
                    .Where(table => !table.Table.DocumentTypes.Any(type => settings.IsDocumentTypeVisible?.Invoke(type) == false))
                    .Select(table => new PostgresTableStorage
                    {
                        Schema = table.Table.Schema,
                        Name = table.Table.Table,
                        Ownership = DiagnosticsMapper.Ownership(table.Table.Ownership),
                        Owner = table.Table.Library,
                        TotalBytes = table.TotalBytes,
                        TableBytes = table.Row.HeapBytes,
                        IndexBytes = table.Row.IndexBytes,
                        ToastBytes = table.Row.ToastBytes,
                        EstimatedRows = table.EstimatedRows,
                        ShareOfDatabase = share > 0 ? Math.Clamp((double) table.TotalBytes / share, 0, 1) : 0,
                        HotUpdateRatio = table.HotUpdateRatio,
                        GrowthBytesPerDay = window is null ? null : Whole(table.GrowthBytesPerDay(window)),
                        AllVisibleRatio = DiagnosticsMapper.Ratio(table.AllVisibleRatio),
                    }),
            ],
        };
    }

    /// <summary>
    /// The statements section from the Statements page's facts, through the host's screen: T02's withholding and comment
    /// masking were applied where the text was read; here a hidden document type's text is withheld and every text capped.
    /// Only statements with counters over the window are listed, so the shares add up.
    /// </summary>
    /// <param name="facts">The facts.</param>
    /// <param name="gate">The host gate, whose screen masks the texts.</param>
    /// <param name="text">The studio's words, for a read that did not read.</param>
    public static PostgresStatementsSection Statements(StatementFacts facts, HostGate gate, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(text);

        if (facts.State != StatementsState.Read)
        {
            return new PostgresStatementsSection
            {
                Outcome = facts.State == StatementsState.Failed ? PostgresReadOutcome.Failed : PostgresReadOutcome.Unavailable,
                Reason = StatementsService.Status(facts, text),
                MeasuredAt = facts.MeasuredAt,
                StatisticsSince = facts.StatsReset,
            };
        }

        return new PostgresStatementsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = facts.Watched ? null : "Counted since the counters began: the statement checks have not watched a window of this database yet.",
            MeasuredAt = facts.MeasuredAt,
            StatisticsSince = facts.StatsReset,
            Window = facts.Span,
            TotalMilliseconds = facts.TotalExecMs,
            Top =
            [
                .. facts.Statements
                    .Where(static x => x.Window is not null)
                    .Take(MaxStatements)
                    .Select(fact => Statement(fact, gate.Screen)),
            ],
        };
    }

    /// <summary>One statement, its text through the host's screen.</summary>
    internal static PostgresStatement Statement(StatementFact fact, StatementScreen screen)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(screen);

        StatementCounts counts = fact.Window ?? fact.SinceBegan;
        string? textShown = null;
        string? withheld = fact.TextWithheld;
        bool truncated = fact.TextTruncated;

        if (withheld is null && fact.Text is { } read)
        {
            SessionStatementText masked = SessionQueryText.Present(read, fact.TextTruncated, screen, StatementCharacters);
            textShown = masked.Text;
            withheld = masked.Text is null ? masked.Note : null;
            truncated = masked.Text is not null && (fact.TextTruncated || masked.Note is not null);
        }

        return new PostgresStatement
        {
            QueryId = fact.QueryId,
            Text = textShown,
            TextTruncated = truncated,
            TextWithheld = withheld,
            Calls = (long) counts.Calls,
            TotalMilliseconds = counts.ExecMs,
            MeanMilliseconds = counts.MeanMs ?? 0,
            ShareOfTime = fact.Share ?? 0,
            Rows = (long) counts.Rows,
            SharedBlocksHit = (long) counts.SharedBlocksHit,
            SharedBlocksRead = (long) counts.SharedBlocksRead,
            TempBlocksWritten = (long) counts.TempBlocksWritten,
            StorageBlocksRead = counts.StorageBlocksRead is { } storage ? (long) storage : null,
        };
    }

    /// <summary>The settings section from the Server page's facts: the curated settings and the extension chips, read as the page reads them.</summary>
    /// <param name="facts">The facts.</param>
    public static PostgresSettingsSection Settings(ServerSettingsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        bool read = facts.Settings.Read || facts.Extensions.Read;

        return new PostgresSettingsSection
        {
            Outcome = read ? PostgresReadOutcome.Read : PostgresReadOutcome.Failed,
            Reason = facts.Failures.Count == 0 ? null : string.Join(" ", facts.Failures.Select(static x => x.EndsWith('.') ? x : x + ".")),
            MeasuredAt = facts.ReadAt,
            Settings = [.. facts.CuratedSettings().Select(DiagnosticsMapper.Setting)],
            Extensions = [.. facts.Chips().Select(DiagnosticsMapper.Extension)],
            DataChecksums = facts.Posture.Value?.ChecksumsOn,
            ChecksumFailures = facts.Posture.Value?.ChecksumFailures,
            LastChecksumFailure = facts.Posture.Value?.LastChecksumFailure,
        };
    }

    /// <summary>
    /// The write-ahead log section from the Replication page's facts: <c>pg_stat_wal</c> as that page reads it, with the WAL
    /// writes and syncs the same on every release (the platform and release gates are applied there, so Aurora and a release
    /// without the view are <see cref="PostgresReadOutcome.NotApplicable" /> with the page's own sentence). Never gated:
    /// nothing in it is another program's work.
    /// </summary>
    /// <param name="facts">The Replication page's facts.</param>
    public static PostgresWalSection Wal(ReplicationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        ReplicationRead<WalStatistics> read = facts.Wal;

        if (read.Value is not { } wal)
        {
            return new PostgresWalSection
            {
                Outcome = read.Outcome switch
                {
                    ReplicationReadOutcome.NotApplicable or ReplicationReadOutcome.NeedsVersion => PostgresReadOutcome.NotApplicable,
                    ReplicationReadOutcome.Failed => PostgresReadOutcome.Failed,
                    _ => PostgresReadOutcome.Unavailable,
                },
                Reason = read.Note ?? "pg_stat_wal returned no row.",
                MeasuredAt = facts.ReadAt,
            };
        }

        return new PostgresWalSection
        {
            Outcome = PostgresReadOutcome.Read,
            MeasuredAt = facts.ReadAt,
            Records = wal.Records,
            FullPageImages = wal.FullPageImages,
            Bytes = Whole(wal.Bytes),
            Writes = wal.Writes,
            Syncs = wal.Syncs,
            WriteTimeMs = Finite(wal.WriteTimeMs),
            SyncTimeMs = Finite(wal.SyncTimeMs),
            StatsReset = wal.StatsReset,
        };
    }

    /// <summary>
    /// The connections section from the sessions snapshot: the database's row and the budget always; the applications - their
    /// names - only past the activity gate.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="gate">The host gate.</param>
    public static PostgresConnectionsSection Connections(SessionSnapshot snapshot, HostGate gate)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(gate);

        if (snapshot.Connections is not { } connections)
        {
            return new PostgresConnectionsSection
            {
                Outcome = PostgresReadOutcome.Unavailable,
                Reason = "The sessions snapshot did not read connections by application.",
                MeasuredAt = snapshot.ReadAt,
            };
        }

        bool capped = gate.IncludeActivity && connections.ApplicationCount > connections.Applications.Count;

        return new PostgresConnectionsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = capped
                ? "Only the " + connections.Applications.Count.ToString(CultureInfo.InvariantCulture) + " applications with the most sessions, of "
                  + connections.ApplicationCount.ToString(CultureInfo.InvariantCulture) + ", are listed; the database's total counts them all."
                : null,
            MeasuredAt = snapshot.ReadAt,
            MaxConnections = connections.MaxConnections,
            ReservedConnections = connections.Reserved,
            ServerConnections = Count(connections.ServerConnections),
            Database = DiagnosticsMapper.Counts(connections.Database),
            ByApplication = gate.IncludeActivity ? [.. connections.Applications.Select(DiagnosticsMapper.Counts)] : [],
            AllSessionsVisible = connections.AllSessionsVisible,
        };
    }

    private static int Count(long value) => DiagnosticsMapper.Count(value);

    private static long? Whole(double? value) => value is { } number && double.IsFinite(number) ? (long) Math.Round(number) : null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
}
