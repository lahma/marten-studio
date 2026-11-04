using System.Globalization;
using System.Text;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// Compares an earlier snapshot file with the visitor's current view (1.2-H-B, D-HEALTH C6): pure, with the visitor's current
/// gated view as the only authority on what may be said.
/// </summary>
/// <remarks>
/// <para>
/// <b>P1. The file is the visitor's own, and nothing of it is kept.</b> The comparison holds derived values, counts and the
/// file name; no link or address is built from the file's content.
/// </para>
/// <para>
/// <b>P2. A fact about "now" comes only from the current gated view.</b> "No longer reported", "no longer among the top" and "no
/// longer among the largest" say something about the present, so each is said only where the current view would show the item
/// if it were there; otherwise the earlier item is <i>counted, never described</i>.
/// </para>
/// <para>
/// <b>P3. An earlier value is drawn only beside an item the current view also shows</b>, or for an item P2 lets the compare call
/// gone. A section the visitor is short of now is "not compared" with the current view's own sentence, and nothing of the file's
/// content for it is drawn.
/// </para>
/// <para>
/// <b>P4. While the host hides document types</b> (<c>IsDocumentTypeVisible</c>), nothing only the earlier file names is drawn
/// by name: the sweep drops a hidden type's finding, so "no longer reported" would be a name and untrue.
/// </para>
/// <para>
/// <b>No policy is asked with a value from the file</b> (no tenant oracle; nothing of the file reaches a host's authorization
/// handler): the gates are the current view's, already applied.
/// </para>
/// </remarks>
internal static class SnapshotComparer
{
    /// <summary>The longest string kept from the file.</summary>
    public const int MaxFileText = 512;

    /// <summary>How many rows of each kind a table lists.</summary>
    public const int MaxListed = 20;

    private const double MeanRelative = 0.25;
    private const double MeanAbsoluteMs = 1;
    private const double ShareMoved = 0.05;
    private const long TableAbsolute = 1024 * 1024;
    private const double TableRelative = 0.10;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The earlier snapshot compared with the current view.</summary>
    /// <param name="earlier">The earlier snapshot, as the reader built it.</param>
    /// <param name="now">The visitor's current gated view.</param>
    /// <param name="fileName">The file's name.</param>
    /// <param name="skippedItems">How many items the reader left out.</param>
    /// <param name="hidesDocumentTypes">Whether the host hides document types from the studio (P4).</param>
    public static SnapshotComparison Compare(
        PostgresDiagnosticsSnapshot earlier,
        VisitorView now,
        string? fileName,
        int skippedItems,
        bool hidesDocumentTypes)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(now);

        PostgresDiagnosticsSnapshot current = now.Snapshot;
        List<string> notices = [];

        if (!string.Equals(earlier.Database.Source, current.Database.Source, StringComparison.Ordinal)
            || !string.Equals(earlier.Database.Database, current.Database.Database, StringComparison.Ordinal))
        {
            notices.Add(
                "The earlier file is of " + Name(earlier.Database) + "; you are looking at " + Name(current.Database) + ".");
        }

        if (!string.Equals(ProductOf(earlier.Producer), ProductOf(current.Producer), StringComparison.Ordinal))
        {
            notices.Add("The earlier file was written by " + Cap(ProductOf(earlier.Producer), 64) + "; this is " + ProductOf(current.Producer) + ".");
        }

        if (earlier.GeneratedAt > current.GeneratedAt)
        {
            notices.Add("The earlier file is dated after now, so \"before\" and \"now\" below may be the wrong way round.");
        }

        SnapshotComparison basis = new()
        {
            Outcome = SnapshotCompareOutcome.Compared,
            FileName = Cap(fileName, 120),
            EarlierAt = earlier.GeneratedAt,
            NowAt = current.GeneratedAt,
            EarlierProducer = Cap(earlier.Producer, MaxFileText),
            SkippedItems = skippedItems,
        };

        if (earlier.Outcome != PostgresReadOutcome.Read)
        {
            notices.Add("The earlier snapshot holds nothing about the database (" + DiagnosticsJson.Name(earlier.Outcome) + ").");

            return basis with { Notices = notices, Verdict = "Nothing to compare: the earlier snapshot holds nothing about the database." };
        }

        Run run = new(earlier, now, hidesDocumentTypes);
        run.Findings();
        run.Server();
        run.Settings();
        run.Storage();
        run.Statements();
        run.Wal();
        run.Connections();
        run.Connection();

        return basis with
        {
            Notices = notices,
            Findings = run.FindingChanges,
            NotCompared = run.NotCompared,
            Server = run.ServerRows,
            Settings = run.SettingRows,
            Extensions = run.ExtensionRows,
            Storage = run.StorageRows,
            Tables = run.TableRows,
            Statements = run.StatementRows,
            Wal = run.WalRows,
            Connections = run.ConnectionRows,
            Connection = run.ConnectionSettingRows,
            Verdict = run.Verdict(basis),
        };
    }

    /// <summary>The text capped at <paramref name="length" /> characters, control characters replaced by a space.</summary>
    /// <param name="text">The text.</param>
    /// <param name="length">The cap.</param>
    internal static string? Cap(string? text, int length)
    {
        if (text is null)
        {
            return null;
        }

        StringBuilder result = new(Math.Min(text.Length, length));

        foreach (char c in text)
        {
            if (result.Length >= length)
            {
                break;
            }

            result.Append(char.IsControl(c) ? ' ' : c);
        }

        return result.ToString();
    }

    private static string Name(PostgresDatabaseRef database) =>
        Cap(database.Source, MaxFileText) + " / " + (database.Database.Length == 0 ? "default" : Cap(database.Database, MaxFileText));

    private static string ProductOf(string producer) => producer.Split(' ', 2)[0];

    /// <summary>One compare: the state the sections share, so each reads like the page it compares.</summary>
    private sealed class Run(PostgresDiagnosticsSnapshot earlier, VisitorView view, bool hides)
    {
        private readonly PostgresDiagnosticsSnapshot now = view.Snapshot;
        private readonly Dictionary<string, int> goneCounts = new(StringComparer.Ordinal);

        public List<SnapshotFindingChange> FindingChanges { get; } = [];

        public List<SnapshotNotCompared> NotCompared { get; } = [];

        public List<SnapshotValueChange> ServerRows { get; } = [];

        public List<SnapshotValueChange> SettingRows { get; } = [];

        public List<SnapshotValueChange> ExtensionRows { get; } = [];

        public List<SnapshotValueChange> StorageRows { get; } = [];

        public List<SnapshotTableChange> TableRows { get; } = [];

        public List<SnapshotStatementChange> StatementRows { get; } = [];

        public List<SnapshotValueChange> WalRows { get; } = [];

        public List<SnapshotValueChange> ConnectionRows { get; } = [];

        public List<SnapshotValueChange> ConnectionSettingRows { get; } = [];

        public string Verdict(SnapshotComparison basis)
        {
            List<string> parts = [];

            AddCount(parts, FindingChanges.Count(static x => x.Kind == SnapshotChangeKind.New), "new finding", "new findings");
            AddCount(parts, FindingChanges.Count(static x => x.Kind == SnapshotChangeKind.Worse), "worse");
            AddCount(parts, FindingChanges.Count(static x => x.Kind == SnapshotChangeKind.Better), "better");
            AddCount(parts, FindingChanges.Count(static x => x.Kind == SnapshotChangeKind.NoLongerReported), "no longer reported");
            AddCount(parts, FindingChanges.Count(static x => x.Kind is SnapshotChangeKind.NowAccepted or SnapshotChangeKind.NoLongerAccepted), "with a changed acceptance");

            int other = ServerRows.Count + SettingRows.Count + ExtensionRows.Count + StorageRows.Count + TableRows.Count + StatementRows.Count
                + WalRows.Count + ConnectionRows.Count + ConnectionSettingRows.Count;

            AddCount(parts, other, "other change", "other changes");

            string span = basis.EarlierAt is { } then && basis.NowAt is { } at && at >= then ? TableText.Span(at - then) + " earlier" : "earlier";
            string head = "Compared with the snapshot from " + span + ": ";

            return head + (parts.Count == 0 ? "nothing you may see has changed." : string.Join(", ", parts) + ".");
        }

        // ------------------------------------------------------------------------------------------------
        // Findings
        // ------------------------------------------------------------------------------------------------

        public void Findings()
        {
            Dictionary<string, PostgresFinding> nowByKey = new(StringComparer.Ordinal);

            foreach (PostgresFinding finding in now.Findings)
            {
                nowByKey.TryAdd(finding.Key, finding);
            }

            HashSet<string> seen = new(StringComparer.Ordinal);

            foreach (PostgresFinding before in earlier.Findings)
            {
                if (!seen.Add(before.Key))
                {
                    continue;
                }

                if (nowByKey.TryGetValue(before.Key, out PostgresFinding? after))
                {
                    SnapshotChangeKind kind = (before.Accepted, after.Accepted) switch
                    {
                        (false, true) => SnapshotChangeKind.NowAccepted,
                        (true, false) => SnapshotChangeKind.NoLongerAccepted,
                        _ when after.Severity < before.Severity => SnapshotChangeKind.Worse,
                        _ when after.Severity > before.Severity => SnapshotChangeKind.Better,
                        _ => SnapshotChangeKind.Unchanged,
                    };

                    FindingChanges.Add(Change(kind, after, before.Severity, after.Severity));
                    continue;
                }

                string? why = WhyNotGone(before);

                if (why is null)
                {
                    FindingChanges.Add(Change(SnapshotChangeKind.NoLongerReported, before, before.Severity, null));
                }
                else
                {
                    goneCounts[why] = goneCounts.GetValueOrDefault(why) + 1;
                }
            }

            HashSet<string> earlierKeys = [.. earlier.Findings.Select(static x => x.Key)];

            foreach (PostgresFinding after in now.Findings.Where(x => !earlierKeys.Contains(x.Key)))
            {
                FindingChanges.Add(Change(SnapshotChangeKind.New, after, null, after.Severity));
            }

            foreach ((string why, int count) in goneCounts.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                NotCompared.Add(new SnapshotNotCompared("Earlier findings no longer in your view", count, why));
            }

            if (now.FindingsWithheld > 0 && now.Withheld.TryGetValue("findings", out string? withheld))
            {
                NotCompared.Add(new SnapshotNotCompared("Findings not shown to you now", now.FindingsWithheld, withheld));
            }
        }

        /// <summary>
        /// Why an earlier finding that is not in the current view may not be called "no longer reported" - or <see langword="null" />
        /// when the current view would show it, were it there (P2).
        /// </summary>
        private string? WhyNotGone(PostgresFinding before)
        {
            if (hides)
            {
                return "while this application hides some document types from the studio, a finding that is gone is counted and not named";
            }

            if (before.TenantId is not null)
            {
                return "about one tenant, which the compare does not name";
            }

            HealthCheckStatus? row = view.Report.Checks.FirstOrDefault(x => string.Equals(x.Code, before.Code, StringComparison.Ordinal));

            if (row is null)
            {
                return "of checks with no result in your current view (a deeper or statement check that has not run in five minutes, or one this studio does not have)";
            }

            if (row.BelowGate is not null)
            {
                return "of checks whose findings are not shown to you now";
            }

            if (row.Outcome != HealthCheckOutcome.Checked)
            {
                return "of checks that did not check this time";
            }

            if (row.FindingsWithheld)
            {
                return "of checks some of whose findings are withheld from you now";
            }

            return null;
        }

        private static SnapshotFindingChange Change(SnapshotChangeKind kind, PostgresFinding finding, PostgresSeverity? before, PostgresSeverity? after) =>
            new(kind, Cap(finding.Code, 64)!, Cap(finding.Variant, MaxFileText), Cap(finding.Subject, MaxFileText), Cap(finding.Title, MaxFileText)!, before, after);

        // ------------------------------------------------------------------------------------------------
        // Server, settings, extensions
        // ------------------------------------------------------------------------------------------------

        public void Server()
        {
            if (Gate("server", "Server", earlier.Server is not null, true, null, now.Server is not null, true, null))
            {
                PostgresServerInfo before = earlier.Server!;
                PostgresServerInfo after = now.Server!;

                Value(ServerRows, "Version", Cap(before.Version, MaxFileText), after.Version);
                Value(ServerRows, "Platform", DiagnosticsJson.Name(before.Platform), DiagnosticsJson.Name(after.Platform));
                Value(ServerRows, "In recovery", before.InRecovery ? "yes" : "no", after.InRecovery ? "yes" : "no");

                if (before.StartedAt != after.StartedAt && after.StartedAt is { } startedAt)
                {
                    ServerRows.Add(new SnapshotValueChange(
                        "Server started",
                        before.StartedAt is { } was ? MarkdownText.Utc(was) : null,
                        MarkdownText.Utc(startedAt),
                        before.StartedAt is { } previous && startedAt > previous ? "the server started again at " + MarkdownText.Utc(startedAt) : null));
                }
            }
        }

        public void Settings()
        {
            PostgresSettingsSection? before = earlier.Settings;
            PostgresSettingsSection? after = now.Settings;

            if (!Gate("settings", "Settings and extensions", before is not null, Read(before?.Outcome), Describe(before?.Outcome), after is not null, Read(after?.Outcome), after?.Reason))
            {
                return;
            }

            ServerChecksums(before!, after!);

            Dictionary<string, PostgresSetting> was = Index(before!.Settings, static x => x.Name);
            Dictionary<string, PostgresSetting> current = Index(after!.Settings, static x => x.Name);

            foreach ((string name, PostgresSetting setting) in current.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                if (!was.TryGetValue(name, out PostgresSetting? old))
                {
                    SettingRows.Add(new SnapshotValueChange(name, null, SettingText(setting), "added"));
                }
                else if (SettingText(old) != SettingText(setting) || old.PendingRestart != setting.PendingRestart || old.Source != setting.Source)
                {
                    SettingRows.Add(new SnapshotValueChange(
                        name,
                        Cap(SettingText(old), MaxFileText),
                        SettingText(setting),
                        setting.PendingRestart && !old.PendingRestart ? "a restart is pending" : null));
                }
            }

            foreach ((string name, PostgresSetting old) in was.OrderBy(static x => x.Key, StringComparer.Ordinal).Where(x => !current.ContainsKey(x.Key)))
            {
                SettingRows.Add(new SnapshotValueChange(Cap(name, MaxFileText)!, Cap(SettingText(old), MaxFileText), null, "no longer listed"));
            }

            Dictionary<string, PostgresExtension> oldExtensions = Index(before.Extensions, static x => x.Name);
            Dictionary<string, PostgresExtension> newExtensions = Index(after.Extensions, static x => x.Name);

            foreach ((string name, PostgresExtension extension) in newExtensions.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                if (!oldExtensions.TryGetValue(name, out PostgresExtension? old))
                {
                    ExtensionRows.Add(new SnapshotValueChange(name, null, extension.InstalledVersion ?? "not installed", "added"));
                }
                else if (old.InstalledVersion != extension.InstalledVersion)
                {
                    ExtensionRows.Add(new SnapshotValueChange(name, Cap(old.InstalledVersion ?? "not installed", MaxFileText), extension.InstalledVersion ?? "not installed"));
                }
            }

            foreach ((string name, PostgresExtension old) in oldExtensions.OrderBy(static x => x.Key, StringComparer.Ordinal).Where(x => !newExtensions.ContainsKey(x.Key)))
            {
                ExtensionRows.Add(new SnapshotValueChange(Cap(name, MaxFileText)!, Cap(old.InstalledVersion ?? "not installed", MaxFileText), null, "no longer listed"));
            }
        }

        private void ServerChecksums(PostgresSettingsSection before, PostgresSettingsSection after)
        {
            if (before.DataChecksums != after.DataChecksums && after.DataChecksums is { } on)
            {
                ServerRows.Add(new SnapshotValueChange("Data checksums", before.DataChecksums is { } was ? (was ? "on" : "off") : null, on ? "on" : "off"));
            }

            if (before.ChecksumFailures is { } failuresBefore && after.ChecksumFailures is { } failuresNow && failuresBefore != failuresNow)
            {
                string note = failuresNow > failuresBefore
                    ? Count((long) Math.Min((double) failuresNow - failuresBefore, long.MaxValue)) + " new" + (after.LastChecksumFailure is { } last ? ", the last at " + MarkdownText.Utc(last) : string.Empty)
                    : "reset";

                ServerRows.Add(new SnapshotValueChange("Checksum failures", Count(failuresBefore), Count(failuresNow), note));
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Storage, tables
        // ------------------------------------------------------------------------------------------------

        public void Storage()
        {
            PostgresStorageSection? before = earlier.Storage;
            PostgresStorageSection? after = now.Storage;

            if (!Gate("storage", "Storage", before is not null, Read(before?.Outcome), Describe(before?.Outcome), after is not null, Read(after?.Outcome), after?.Reason))
            {
                return;
            }

            if (before!.DatabaseBytes != after!.DatabaseBytes)
            {
                double change = (double) after.DatabaseBytes - before.DatabaseBytes;
                TimeSpan span = Span(before.MeasuredAt, after.MeasuredAt);
                string? perDay = span >= TimeSpan.FromHours(1) ? Signed(change / span.TotalDays) + " a day" : null;

                StorageRows.Add(new SnapshotValueChange(
                    "Database size",
                    TableText.Bytes(before.DatabaseBytes),
                    TableText.Bytes(after.DatabaseBytes),
                    Signed(change) + (perDay is null ? string.Empty : ", " + perDay)));
            }

            Dictionary<string, PostgresTableStorage> was = Index(before.LargestTables, static x => x.Schema + "." + x.Name);
            Dictionary<string, PostgresTableStorage> current = Index(after.LargestTables, static x => x.Schema + "." + x.Name);
            bool tablesWithheld = now.Withheld.ContainsKey("storage.largestTables");

            List<SnapshotTableChange> changed = [];
            List<SnapshotTableChange> added = [];
            List<SnapshotTableChange> gone = [];
            int goneCounted = 0;

            foreach ((string name, PostgresTableStorage table) in current)
            {
                if (!was.TryGetValue(name, out PostgresTableStorage? old))
                {
                    added.Add(new SnapshotTableChange(SnapshotTableChangeKind.New, name, null, table.TotalBytes));
                    continue;
                }

                double delta = Math.Abs((double) table.TotalBytes - old.TotalBytes);

                if (delta > 0 && (delta >= TableAbsolute || delta >= TableRelative * old.TotalBytes))
                {
                    changed.Add(new SnapshotTableChange(SnapshotTableChangeKind.Changed, name, old.TotalBytes, table.TotalBytes));
                }
            }

            foreach ((string name, PostgresTableStorage old) in was.Where(x => !current.ContainsKey(x.Key)))
            {
                // P2: "no longer among the largest" is a fact about now, so it is said only where the current view lists every table
                // it would list - none withheld from the visitor, and none hidden by the host.
                if (tablesWithheld || hides)
                {
                    goneCounted++;
                }
                else
                {
                    gone.Add(new SnapshotTableChange(SnapshotTableChangeKind.Gone, Cap(name, MaxFileText)!, old.TotalBytes, null));
                }
            }

            TableRows.AddRange(changed.OrderByDescending(static x => Math.Abs((double) (x.BytesNow ?? 0) - (x.BytesBefore ?? 0))).ThenBy(static x => x.Table, StringComparer.Ordinal).Take(MaxListed));
            TableRows.AddRange(added.OrderByDescending(static x => x.BytesNow).ThenBy(static x => x.Table, StringComparer.Ordinal).Take(MaxListed));
            TableRows.AddRange(gone.OrderByDescending(static x => x.BytesBefore).ThenBy(static x => x.Table, StringComparer.Ordinal).Take(MaxListed));

            More("Tables", changed.Count + added.Count + gone.Count - TableRows.Count, "changed or listed beyond the 20 shown of each kind");

            if (goneCounted > 0)
            {
                NotCompared.Add(new SnapshotNotCompared(
                    "Earlier largest tables no longer listed",
                    goneCounted,
                    hides
                        ? "while this application hides some document types from the studio, a table that is gone is counted and not named"
                        : "some tables are withheld from your view of Storage, so a table that is not listed may be one of them"));
            }
        }

        // ------------------------------------------------------------------------------------------------
        // Statements
        // ------------------------------------------------------------------------------------------------

        public void Statements()
        {
            PostgresStatementsSection? before = earlier.Statements;
            PostgresStatementsSection? after = now.Statements;

            if (!Gate("statements", "Statements", before is not null, Read(before?.Outcome), Describe(before?.Outcome), after is not null, Read(after?.Outcome), after?.Reason))
            {
                return;
            }

            Dictionary<long, PostgresStatement> was = [];

            foreach (PostgresStatement statement in before!.Top)
            {
                was.TryAdd(statement.QueryId, statement);
            }

            Dictionary<long, PostgresStatement> current = [];

            foreach (PostgresStatement statement in after!.Top)
            {
                current.TryAdd(statement.QueryId, statement);
            }

            double? hoursBefore = Hours(before.Window);
            double? hoursNow = Hours(after.Window);
            bool rates = hoursBefore is not null && hoursNow is not null;

            List<SnapshotStatementChange> changed = [];
            List<SnapshotStatementChange> added = [];
            List<SnapshotStatementChange> gone = [];

            foreach ((long id, PostgresStatement statement) in current)
            {
                if (!was.TryGetValue(id, out PostgresStatement? old))
                {
                    added.Add(new SnapshotStatementChange(
                        SnapshotStatementChangeKind.New, id, statement.Text, null, statement.MeanMilliseconds, null, statement.ShareOfTime, null, hoursNow is null ? null : statement.Calls / hoursNow));
                    continue;
                }

                double mean = Math.Abs(statement.MeanMilliseconds - old.MeanMilliseconds);
                bool meanMoved = mean >= MeanAbsoluteMs && old.MeanMilliseconds > 0 && mean / old.MeanMilliseconds >= MeanRelative;
                bool shareMoved = Math.Abs(statement.ShareOfTime - old.ShareOfTime) >= ShareMoved;

                if (meanMoved || shareMoved)
                {
                    changed.Add(new SnapshotStatementChange(
                        SnapshotStatementChangeKind.Changed,
                        id,
                        statement.Text,
                        old.MeanMilliseconds,
                        statement.MeanMilliseconds,
                        old.ShareOfTime,
                        statement.ShareOfTime,
                        rates ? old.Calls / hoursBefore : null,
                        rates ? statement.Calls / hoursNow : null));
                }
            }

            foreach ((long id, PostgresStatement old) in was.Where(x => !current.ContainsKey(x.Key)))
            {
                // By queryid and the earlier numbers only: never the earlier text.
                gone.Add(new SnapshotStatementChange(SnapshotStatementChangeKind.Gone, id, null, old.MeanMilliseconds, null, old.ShareOfTime, null, hoursBefore is null ? null : old.Calls / hoursBefore, null));
            }

            StatementRows.AddRange(changed.OrderByDescending(static x => Math.Abs((x.ShareNow ?? 0) - (x.ShareBefore ?? 0))).ThenBy(static x => x.QueryId).Take(MaxListed));
            StatementRows.AddRange(added.OrderByDescending(static x => x.ShareNow).ThenBy(static x => x.QueryId).Take(MaxListed));
            StatementRows.AddRange(gone.OrderByDescending(static x => x.ShareBefore).ThenBy(static x => x.QueryId).Take(MaxListed));

            More("Statements", changed.Count + added.Count + gone.Count - StatementRows.Count, "changed or listed beyond the 20 shown of each kind");
        }

        private static double? Hours(TimeSpan? window) => window is { TotalHours: > 0 } span ? span.TotalHours : null;

        // ------------------------------------------------------------------------------------------------
        // WAL, connections
        // ------------------------------------------------------------------------------------------------

        public void Wal()
        {
            PostgresWalSection? before = earlier.Wal;
            PostgresWalSection? after = now.Wal;

            if (!Gate("wal", "Write-ahead log", before is not null, Read(before?.Outcome), Describe(before?.Outcome), after is not null, Read(after?.Outcome), after?.Reason))
            {
                return;
            }

            bool fell = Fell(before!.Records, after!.Records) || Fell(before.FullPageImages, after.FullPageImages) || Fell(before.Bytes, after.Bytes);

            if (before.StatsReset != after.StatsReset || fell)
            {
                WalRows.Add(new SnapshotValueChange("Written between", null, null, "the WAL counters were reset between the two, so what was written between them is not known"));
                return;
            }

            TimeSpan span = Span(before.MeasuredAt, after.MeasuredAt);
            double hours = span.TotalHours;

            WalDelta(WalRows, "WAL records written", before.Records, after.Records, hours, static x => Count(ToLong(x)));
            WalDelta(WalRows, "Full-page images written", before.FullPageImages, after.FullPageImages, hours, static x => Count(ToLong(x)));
            WalDelta(WalRows, "WAL bytes written", before.Bytes, after.Bytes, hours, static x => TableText.Bytes(ToLong(x)));
        }

        private static void WalDelta(List<SnapshotValueChange> rows, string label, long? before, long? after, double hours, Func<double, string> format)
        {
            if (before is not { } was || after is not { } current || current == was)
            {
                return;
            }

            double written = (double) current - was;

            rows.Add(new SnapshotValueChange(label, null, format(written), hours >= 1 ? format(written / hours) + " an hour" : null));
        }

        private static long ToLong(double value) => (long) Math.Clamp(value, long.MinValue, long.MaxValue);

        private static bool Fell(long? before, long? after) => before is { } was && after is { } current && current < was;

        public void Connections()
        {
            PostgresConnectionsSection? before = earlier.Connections;
            PostgresConnectionsSection? after = now.Connections;

            if (!Gate("connections", "Connections", before is not null, Read(before?.Outcome), Describe(before?.Outcome), after is not null, Read(after?.Outcome), after?.Reason))
            {
                return;
            }

            Number(ConnectionRows, "max_connections", before!.MaxConnections, after!.MaxConnections);
            Number(ConnectionRows, "Reserved connections", before.ReservedConnections, after.ReservedConnections);
            Number(ConnectionRows, "Connections to this database", before.Database.Total, after.Database.Total);
            Number(ConnectionRows, "Active", before.Database.Active, after.Database.Active);
            Number(ConnectionRows, "Idle in transaction", before.Database.IdleInTransaction, after.Database.IdleInTransaction);
        }

        // ------------------------------------------------------------------------------------------------
        // The Connection page's settings (D33): compared as the page exposes each keyword
        // ------------------------------------------------------------------------------------------------

        public void Connection()
        {
            ConnectionSnapshotSection? before = earlier.Connection;
            ConnectionSnapshotSection? after = now.Connection;

            if (!Gate("connection", "Connection settings", before is not null, true, null, after is not null, true, null))
            {
                return;
            }

            Value(ConnectionSettingRows, "Npgsql version", Cap(before!.NpgsqlVersion, MaxFileText), after!.NpgsqlVersion);
            Value(ConnectionSettingRows, "Platform", Cap(before.Platform, MaxFileText), after.Platform);
            Value(ConnectionSettingRows, "TLS", KnownTls(before.Tls), KnownTls(after.Tls));
            Value(
                ConnectionSettingRows,
                "Pooler",
                before.Pooler is null ? null : Cap(string.Join(", ", before.Pooler), MaxFileText),
                after.Pooler is null ? null : string.Join(", ", after.Pooler));

            Dictionary<string, ConnectionSnapshotSetting> was = Index(before.Settings, static x => x.Keyword);
            int unknownGone = 0;

            foreach (ConnectionSnapshotSetting setting in after.Settings)
            {
                NpgsqlSettingEntry? entry = NpgsqlSettingsCatalog.Find(setting.Keyword);

                if (!was.TryGetValue(setting.Keyword, out ConnectionSnapshotSetting? old))
                {
                    ConnectionSettingRows.Add(new SnapshotValueChange(setting.Keyword, null, Shown(entry, setting.Value), "new in this view"));
                    continue;
                }

                string? changed = ChangedConnectionValue(entry, old, setting);

                if (changed is not null)
                {
                    ConnectionSettingRows.Add(entry is { Exposure: NpgsqlSettingExposure.Shown or NpgsqlSettingExposure.Path }
                        ? new SnapshotValueChange(setting.Keyword, Cap(old.Value, MaxFileText), setting.Value)
                        : new SnapshotValueChange(setting.Keyword, entry?.Exposure == NpgsqlSettingExposure.Secret ? SetOrNot(old.Value) : null, entry?.Exposure == NpgsqlSettingExposure.Secret ? SetOrNot(setting.Value) : null, changed));
                }
            }

            HashSet<string> nowKeywords = [.. after.Settings.Select(static x => x.Keyword)];

            foreach (ConnectionSnapshotSetting old in before.Settings.Where(x => !nowKeywords.Contains(x.Keyword)))
            {
                NpgsqlSettingEntry? entry = NpgsqlSettingsCatalog.Find(old.Keyword);

                if (entry is null)
                {
                    unknownGone++;
                }
                else
                {
                    ConnectionSettingRows.Add(new SnapshotValueChange(entry.Keyword, Shown(entry, old.Value, earlierValue: true), null, "no longer listed"));
                }
            }

            if (unknownGone > 0)
            {
                NotCompared.Add(new SnapshotNotCompared("Earlier connection keywords", unknownGone, "keywords this studio does not know are counted, not named"));
            }

            HashSet<string> hintsWas = [.. before.Hints.Select(static x => x.Id)];
            HashSet<string> hintsNow = [.. after.Hints.Select(static x => x.Id)];

            foreach (ConnectionSnapshotHint hint in after.Hints.Where(x => !hintsWas.Contains(x.Id)))
            {
                ConnectionSettingRows.Add(new SnapshotValueChange("Hint " + hint.Id, null, null, "new" + (hint.Keyword is { } keyword ? " (" + keyword + ")" : string.Empty)));
            }

            foreach (ConnectionSnapshotHint hint in before.Hints.Where(x => !hintsNow.Contains(x.Id)))
            {
                string id = IsPlainId(hint.Id) ? hint.Id : "(unnamed)";
                string? keyword = hint.Keyword is { } word && NpgsqlSettingsCatalog.Find(word) is { } known ? known.Keyword : null;

                ConnectionSettingRows.Add(new SnapshotValueChange("Hint " + id, null, null, "gone" + (keyword is null ? string.Empty : " (" + keyword + ")")));
            }
        }

        private static string? ChangedConnectionValue(NpgsqlSettingEntry? entry, ConnectionSnapshotSetting before, ConnectionSnapshotSetting after) => entry?.Exposure switch
        {
            NpgsqlSettingExposure.Shown or NpgsqlSettingExposure.Path => before.Value == after.Value ? null : "changed",

            // A secret is compared as set or not set, and nothing else: any earlier text but "not set" reads "set".
            NpgsqlSettingExposure.Secret => SetOrNot(before.Value) == SetOrNot(after.Value) ? null : "changed",

            // An Options value, and a keyword this studio does not know, are never drawn: it says it changed, and not to what.
            _ => before.Value == after.Value ? null : "changed (the value is not shown)",
        };

        private static string Shown(NpgsqlSettingEntry? entry, string value, bool earlierValue = false) => entry?.Exposure switch
        {
            NpgsqlSettingExposure.Shown or NpgsqlSettingExposure.Path => earlierValue ? Cap(value, MaxFileText)! : value,
            NpgsqlSettingExposure.Secret => SetOrNot(value),
            _ => "(not shown)",
        };

        private static string SetOrNot(string value) => string.Equals(value, "not set", StringComparison.Ordinal) ? "not set" : "set";

        private static string? KnownTls(string tls) => tls switch
        {
            "verified" or "encryptedNotVerified" or "encryptedVerificationUnknown" or "notEncrypted" or "notEncryptedLocal" or "encryptedByProxy" or "encryptedGss" or "notVisible" or "notRead" => tls,
            _ => "(not a state this studio knows)",
        };

        private static bool IsPlainId(string id) => id.Length is > 0 and <= 32 && id.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

        // ------------------------------------------------------------------------------------------------
        // Shared
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Whether a section can be compared; when it cannot, say why. A section the visitor is short of now is "not compared" with
        /// the current view's own sentence, and the file's content for it is never drawn (P3).
        /// </summary>
        private bool Gate(string key, string label, bool earlierPresent, bool earlierRead, string? earlierWhy, bool nowPresent, bool nowRead, string? nowWhy)
        {
            if (!earlierPresent)
            {
                return false;
            }

            if (!nowPresent)
            {
                NotCompared.Add(new SnapshotNotCompared(label, 0, "not compared - " + (now.Withheld.TryGetValue(key, out string? sentence) ? sentence : "your current view does not have it")));
                return false;
            }

            if (!earlierRead)
            {
                NotCompared.Add(new SnapshotNotCompared(label, 0, "not compared - the earlier snapshot did not read it" + earlierWhy));
                return false;
            }

            if (!nowRead)
            {
                NotCompared.Add(new SnapshotNotCompared(label, 0, "not compared - not read now" + (string.IsNullOrWhiteSpace(nowWhy) ? string.Empty : ": " + nowWhy)));
                return false;
            }

            return true;
        }

        private static bool Read(PostgresReadOutcome? outcome) => outcome == PostgresReadOutcome.Read;

        private static string? Describe(PostgresReadOutcome? outcome) => outcome is { } value ? " (" + DiagnosticsJson.Name(value) + ")" : null;

        private void More(string what, int count, string why)
        {
            if (count > 0)
            {
                NotCompared.Add(new SnapshotNotCompared(what, count, why));
            }
        }

        private static void Value(List<SnapshotValueChange> rows, string label, string? before, string? after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                rows.Add(new SnapshotValueChange(label, before, after));
            }
        }

        private static void Number(List<SnapshotValueChange> rows, string label, long before, long after)
        {
            if (before != after)
            {
                rows.Add(new SnapshotValueChange(label, Count(before), Count(after)));
            }
        }

        private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
        {
            Dictionary<string, T> result = new(StringComparer.Ordinal);

            foreach (T item in items)
            {
                result.TryAdd(key(item), item);
            }

            return result;
        }

        private static string SettingText(PostgresSetting setting) =>
            setting.Value is null ? "not set" : setting.Value + (setting.Unit is { Length: > 0 } unit ? " " + unit : string.Empty);

        private static TimeSpan Span(DateTimeOffset before, DateTimeOffset after) =>
            before == default || after == default ? TimeSpan.Zero : after - before;

        private static string Count(long value) => value.ToString("N0", Invariant);

        private static string Signed(double bytes) => (bytes < 0 ? "-" : "+") + TableText.Bytes((long) Math.Min(Math.Abs(Math.Round(bytes)), long.MaxValue));

        private static void AddCount(List<string> parts, int count, string singular, string? plural = null)
        {
            if (count > 0)
            {
                parts.Add(count.ToString("N0", Invariant) + " " + (count == 1 || plural is null ? singular : plural));
            }
        }
    }
}
