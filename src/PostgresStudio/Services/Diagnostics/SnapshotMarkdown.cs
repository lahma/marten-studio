using System.Globalization;
using System.Text;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The Health page's Markdown report (1.2-H-B, D-HEALTH D1 and D2): the visitor's gated snapshot written as a readable
/// report for a ticket, CommonMark with GitHub-flavoured tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Its only input is the snapshot.</b> <see cref="VisitorSnapshotBuilder.BuildMarkdownAsync" /> hands it the same
/// <see cref="PostgresDiagnosticsSnapshot" /> the JSON download is written from, so the report holds nothing the JSON does not,
/// and a section the visitor may not see is only a sentence under <c>Withheld from you</c>. It never reaches the diagnostics API.
/// </para>
/// <para>
/// <b>Every value from the database is untrusted</b> (<see cref="MarkdownText" />): names and values are code spans, prose is
/// escaped to the last punctuation character, a statement or a remedy is a fenced block, and the only link written is the
/// check's documentation address when it is a plain <c>https</c> one.
/// </para>
/// </remarks>
internal static class SnapshotMarkdown
{
    /// <summary>How many of the statements by time the report prints; the JSON holds them all.</summary>
    public const int MaxStatements = 20;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The report for <paramref name="snapshot" />.</summary>
    /// <param name="snapshot">The visitor's gated snapshot.</param>
    public static string Write(PostgresDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        StringBuilder md = new();
        string database = snapshot.Database.Database.Length == 0 ? "default" : snapshot.Database.Database;

        md.Append("# Health report: ").Append(MarkdownText.Code(snapshot.Database.Source)).Append(" / ").Append(MarkdownText.Code(database)).Append("\n\n");
        md.Append("What ").Append(MarkdownText.Inline(Product(snapshot.Producer))).Append("’s pages showed you of this database at ")
            .Append(MarkdownText.Utc(snapshot.GeneratedAt))
            .Append(", masked as they mask it. Sections you may not see are listed at the end.\n\n");

        Summary(md, snapshot);

        if (snapshot.Outcome == PostgresReadOutcome.Read)
        {
            Findings(md, snapshot);
            Accepted(md, snapshot);
            Checked(md, snapshot);
            Storage(md, snapshot.Storage);
            Statements(md, snapshot.Statements);
            SettingsAndExtensions(md, snapshot.Settings);
            Connections(md, snapshot.Connections);
            Wal(md, snapshot.Wal);
            Connection(md, snapshot.Connection);
        }

        Withheld(md, snapshot);

        return md.ToString().TrimEnd('\n') + "\n";
    }

    private static string Product(string producer)
    {
        string name = producer.Split(' ', 2)[0];

        return name switch
        {
            "MartenStudio" => "Marten Studio",
            "PostgresStudio" => "Postgres Studio",
            _ => name,
        };
    }

    private static void Summary(StringBuilder md, PostgresDiagnosticsSnapshot snapshot)
    {
        md.Append("## Summary\n\n");
        md.Append("- **Outcome:** ").Append(MarkdownText.Code(DiagnosticsJson.Name(snapshot.Outcome)));

        if (snapshot.Reason is { Length: > 0 } reason)
        {
            md.Append(" - ").Append(MarkdownText.Inline(reason));
        }

        md.Append('\n');

        if (snapshot.Outcome != PostgresReadOutcome.Read)
        {
            md.Append('\n');
            return;
        }

        if (snapshot.Server is { } server)
        {
            md.Append("- **Server:** PostgreSQL ").Append(MarkdownText.Code(server.Version)).Append(", platform ")
                .Append(MarkdownText.Code(DiagnosticsJson.Name(server.Platform)))
                .Append(", started ").Append(MarkdownText.Utc(server.StartedAt))
                .Append(", in recovery: ").Append(server.InRecovery ? "yes" : "no").Append('\n');
        }

        PostgresFinding[] shown = [.. snapshot.Findings.Where(static x => !x.Accepted)];
        int accepted = snapshot.Findings.Count - shown.Length;

        md.Append("- **Findings:** ")
            .Append(Count(shown.Count(static x => x.Severity == PostgresSeverity.Critical))).Append(" critical, ")
            .Append(Count(shown.Count(static x => x.Severity == PostgresSeverity.Warning))).Append(" warning, ")
            .Append(Count(shown.Count(static x => x.Severity == PostgresSeverity.Info))).Append(" info");

        if (accepted > 0)
        {
            md.Append("; ").Append(Count(accepted)).Append(" accepted, apart");
        }

        md.Append('\n');

        if (snapshot.FindingsWithheld > 0)
        {
            md.Append("- **Not shown to you:** ").Append(Count(snapshot.FindingsWithheld)).Append(snapshot.FindingsWithheld == 1 ? " finding" : " findings").Append('\n');
        }

        md.Append("- **Checked:** ").Append(MarkdownText.Utc(snapshot.FindingsMeasuredAt)).Append("\n\n");
    }

    private static void Findings(StringBuilder md, PostgresDiagnosticsSnapshot snapshot)
    {
        md.Append("## Findings\n\n");

        PostgresFinding[] shown = [.. snapshot.Findings.Where(static x => !x.Accepted)];

        if (shown.Length == 0)
        {
            md.Append("No finding is reported to you.\n\n");
            return;
        }

        foreach ((PostgresSeverity severity, string heading) in new[] { (PostgresSeverity.Critical, "Critical"), (PostgresSeverity.Warning, "Warning"), (PostgresSeverity.Info, "Info") })
        {
            PostgresFinding[] group = [.. shown.Where(x => x.Severity == severity)];

            if (group.Length == 0)
            {
                continue;
            }

            md.Append("### ").Append(heading).Append("\n\n");

            foreach (PostgresFinding finding in group)
            {
                Finding(md, finding);
            }
        }
    }

    private static void Finding(StringBuilder md, PostgresFinding finding)
    {
        md.Append("#### ").Append(MarkdownText.Code(finding.Code)).Append(' ').Append(MarkdownText.Inline(finding.Title)).Append("\n\n");

        if (finding.Detail.Length > 0)
        {
            md.Append(MarkdownText.Inline(finding.Detail)).Append("\n\n");
        }

        if (finding.Evidence.Count > 0)
        {
            md.Append("| What | Value |\n|---|---|\n");

            foreach (KeyValuePair<string, string> item in finding.Evidence)
            {
                md.Append("| ").Append(MarkdownText.Inline(item.Key)).Append(" | ").Append(MarkdownText.Code(item.Value, inTableCell: true)).Append(" |\n");
            }

            md.Append('\n');
        }

        if (finding.Caveat is { Length: > 0 } caveat)
        {
            md.Append("**How sure:** ").Append(MarkdownText.Inline(caveat)).Append("\n\n");
        }

        if (finding.Action is { Length: > 0 } action)
        {
            string heading = finding.ActionKind switch
            {
                PostgresActionKind.Migration => HealthRemedy.MigrationHeading,
                PostgresActionKind.Repair => HealthRemedy.RepairHeading,
                _ => HealthRemedy.ChangeHeading,
            };

            md.Append("**").Append(MarkdownText.Inline(heading)).Append("**\n\n");
            md.Append(MarkdownText.Fence(action, finding.ActionKind is PostgresActionKind.Migration or PostgresActionKind.Repair ? "sql" : "text")).Append("\n\n");

            if (finding.ActionExplanation is { Length: > 0 } explanation)
            {
                md.Append(MarkdownText.Inline(explanation)).Append("\n\n");
            }

            if (finding.ActionRequires is { Length: > 0 } requires)
            {
                md.Append("Needs ").Append(MarkdownText.Inline(requires)).Append(".\n\n");
            }
        }

        md.Append(MarkdownText.Link("About " + finding.Code, finding.DocsUrl)).Append("\n\n");
    }

    private static void Accepted(StringBuilder md, PostgresDiagnosticsSnapshot snapshot)
    {
        PostgresFinding[] accepted = [.. snapshot.Findings.Where(static x => x.Accepted)];

        if (accepted.Length == 0)
        {
            return;
        }

        md.Append("## Accepted checks\n\n| Code | Title | Reason |\n|---|---|---|\n");

        foreach (PostgresFinding finding in accepted)
        {
            md.Append("| ").Append(MarkdownText.Code(finding.Code, inTableCell: true)).Append(" | ").Append(MarkdownText.Inline(finding.Title))
                .Append(" | ").Append(MarkdownText.Inline(finding.AcceptedReason)).Append(" |\n");
        }

        md.Append('\n');
    }

    private static void Checked(StringBuilder md, PostgresDiagnosticsSnapshot snapshot)
    {
        if (snapshot.Checks.Count == 0)
        {
            return;
        }

        md.Append("## What was checked\n\n| Code | Check | Result |\n|---|---|---|\n");

        foreach (PostgresCheck check in snapshot.Checks)
        {
            string result = check.Outcome is { } outcome ? DiagnosticsJson.Name(outcome) : "not run";

            if (check.Outcome == PostgresReadOutcome.Read)
            {
                result += ", " + Count(check.Findings) + (check.Findings == 1 ? " finding" : " findings");
            }

            if (check.Reason is { Length: > 0 } reason)
            {
                result += " - " + reason;
            }

            md.Append("| ").Append(MarkdownText.Code(check.Code, inTableCell: true)).Append(" | ").Append(MarkdownText.Inline(check.Title))
                .Append(" | ").Append(MarkdownText.Inline(result)).Append(" |\n");
        }

        md.Append('\n');
    }

    private static void Storage(StringBuilder md, PostgresStorageSection? storage)
    {
        if (storage is null)
        {
            return;
        }

        md.Append("## Storage\n\n");
        Outcome(md, storage.Outcome, storage.Reason);

        if (storage.Outcome != PostgresReadOutcome.Read)
        {
            return;
        }

        md.Append("- **Database size:** ").Append((storage.SizesAreEstimates ? "about " : string.Empty) + TableText.Bytes(storage.DatabaseBytes)).Append('\n');

        if (storage.GrowthBytesPerDay is { } growth)
        {
            md.Append("- **Growth:** ").Append(SignedBytes(growth) + " a day").Append('\n');
        }

        if (storage.StatisticsSince is { } since)
        {
            md.Append("- **Counted since:** ").Append(MarkdownText.Utc(since)).Append('\n');
        }

        md.Append('\n');

        PostgresTableStorage[] tables = [.. storage.LargestTables.Take(20)];

        if (tables.Length > 0)
        {
            md.Append("The ").Append(Count(tables.Length)).Append(" largest tables:\n\n");
            md.Append("| Table | Total | Table | Indexes | Toast | Rows | Share |\n|---|---:|---:|---:|---:|---:|---:|\n");

            foreach (PostgresTableStorage table in tables)
            {
                md.Append("| ").Append(MarkdownText.Code(table.Schema + "." + table.Name, inTableCell: true))
                    .Append(" | ").Append(TableText.Bytes(table.TotalBytes))
                    .Append(" | ").Append(TableText.Bytes(table.TableBytes))
                    .Append(" | ").Append(TableText.Bytes(table.IndexBytes))
                    .Append(" | ").Append(TableText.Bytes(table.ToastBytes))
                    .Append(" | ").Append("\\~" + Count(table.EstimatedRows))
                    .Append(" | ").Append(TableText.Percent(table.ShareOfDatabase))
                    .Append(" |\n");
            }

            md.Append('\n');

            if (storage.LargestTables.Count > tables.Length)
            {
                md.Append("The JSON holds all ").Append(Count(storage.LargestTables.Count)).Append(".\n\n");
            }
        }
    }

    private static void Statements(StringBuilder md, PostgresStatementsSection? statements)
    {
        if (statements is null)
        {
            return;
        }

        md.Append("## Statements\n\n");
        Outcome(md, statements.Outcome, statements.Reason);

        if (statements.Outcome != PostgresReadOutcome.Read)
        {
            return;
        }

        md.Append("- **Execution time over the window:** ").Append(Milliseconds(statements.TotalMilliseconds)).Append('\n');

        if (statements.Window is { } window)
        {
            md.Append("- **Window:** ").Append(TableText.Span(window)).Append('\n');
        }

        if (statements.StatisticsSince is { } since)
        {
            md.Append("- **Statistics since:** ").Append(MarkdownText.Utc(since)).Append('\n');
        }

        md.Append('\n');

        PostgresStatement[] top = [.. statements.Top.Take(MaxStatements)];

        for (int i = 0; i < top.Length; i++)
        {
            PostgresStatement statement = top[i];

            md.Append("#### Statement ").Append(Count(i + 1)).Append(' ').Append(MarkdownText.Code(statement.QueryId.ToString(Invariant))).Append("\n\n");
            md.Append(Count(statement.Calls)).Append(statement.Calls == 1 ? " call, " : " calls, ").Append(Milliseconds(statement.TotalMilliseconds)).Append(" in all, mean ")
                .Append(Milliseconds(statement.MeanMilliseconds)).Append(", ")
                .Append(TableText.Percent(statement.ShareOfTime)).Append(" of the time, ")
                .Append(Count(statement.Rows)).Append(" rows.\n\n");

            if (statement.Text is { } text)
            {
                md.Append(MarkdownText.Fence(text, "sql")).Append("\n\n");

                if (statement.TextTruncated)
                {
                    md.Append("The text is cut where the studio reads it.\n\n");
                }
            }
            else
            {
                md.Append("Text withheld: ").Append(MarkdownText.Inline(statement.TextWithheld ?? "withheld")).Append("\n\n");
            }
        }

        if (statements.Top.Count > top.Length)
        {
            md.Append("The JSON holds all ").Append(Count(statements.Top.Count)).Append(".\n\n");
        }
    }

    private static void SettingsAndExtensions(StringBuilder md, PostgresSettingsSection? settings)
    {
        if (settings is null)
        {
            return;
        }

        md.Append("## Settings and extensions\n\n");
        Outcome(md, settings.Outcome, settings.Reason);

        if (settings.Outcome != PostgresReadOutcome.Read)
        {
            return;
        }

        if (settings.DataChecksums is { } checksums)
        {
            md.Append("- **Data checksums:** ").Append(checksums ? "on" : "off").Append('\n');
        }

        if (settings.ChecksumFailures is { } failures)
        {
            md.Append("- **Checksum failures:** ").Append(Count(failures));

            if (settings.LastChecksumFailure is { } last)
            {
                md.Append(", the last at ").Append(MarkdownText.Utc(last));
            }

            md.Append('\n');
        }

        md.Append('\n');

        if (settings.Settings.Count > 0)
        {
            md.Append("| Setting | Value | Source | Restart |\n|---|---|---|---|\n");

            foreach (PostgresSetting setting in settings.Settings)
            {
                string value = setting.Value is null ? "not set" : setting.Value + (setting.Unit is { Length: > 0 } unit ? " " + unit : string.Empty);

                md.Append("| ").Append(MarkdownText.Code(setting.Name, inTableCell: true))
                    .Append(" | ").Append(MarkdownText.Code(value, inTableCell: true))
                    .Append(" | ").Append(setting.Source is null ? string.Empty : MarkdownText.Code(setting.Source, inTableCell: true))
                    .Append(" | ").Append(setting.PendingRestart ? "pending" : string.Empty).Append(" |\n");
            }

            md.Append('\n');
        }

        if (settings.Extensions.Count > 0)
        {
            md.Append("| Extension | Installed | Available | Note |\n|---|---|---|---|\n");

            foreach (PostgresExtension extension in settings.Extensions)
            {
                md.Append("| ").Append(MarkdownText.Code(extension.Name, inTableCell: true))
                    .Append(" | ").Append(extension.InstalledVersion is null ? "not installed" : MarkdownText.Code(extension.InstalledVersion, inTableCell: true))
                    .Append(" | ").Append(extension.AvailableVersion is null ? string.Empty : MarkdownText.Code(extension.AvailableVersion, inTableCell: true))
                    .Append(" | ").Append(MarkdownText.Inline(extension.Note)).Append(" |\n");
            }

            md.Append('\n');
        }
    }

    private static void Connections(StringBuilder md, PostgresConnectionsSection? connections)
    {
        if (connections is null)
        {
            return;
        }

        md.Append("## Connections\n\n");
        Outcome(md, connections.Outcome, connections.Reason);

        if (connections.Outcome != PostgresReadOutcome.Read)
        {
            return;
        }

        md.Append("- **Limit:** ").Append(Count(connections.MaxConnections)).Append(" connections, ").Append(Count(connections.ReservedConnections))
            .Append(" reserved; the server holds ").Append(Count(connections.ServerConnections)).Append(" in all.\n");

        PostgresConnectionCounts database = connections.Database;

        md.Append("- **This database:** ").Append(Count(database.Total)).Append(" in all, ").Append(Count(database.Active)).Append(" active, ")
            .Append(Count(database.Idle)).Append(" idle, ").Append(Count(database.IdleInTransaction)).Append(" idle in transaction, ")
            .Append(Count(database.Waiting)).Append(" waiting\n\n");

        if (connections.ByApplication.Count > 0)
        {
            md.Append("| Application | Total | Active | Idle | Idle in transaction | Waiting |\n|---|---:|---:|---:|---:|---:|\n");

            foreach (PostgresConnectionCounts application in connections.ByApplication)
            {
                md.Append("| ").Append(application.ApplicationName is { Length: > 0 } name ? MarkdownText.Code(name, inTableCell: true) : "no name")
                    .Append(" | ").Append(Count(application.Total))
                    .Append(" | ").Append(Count(application.Active))
                    .Append(" | ").Append(Count(application.Idle))
                    .Append(" | ").Append(Count(application.IdleInTransaction))
                    .Append(" | ").Append(Count(application.Waiting)).Append(" |\n");
            }

            md.Append('\n');
        }
    }

    private static void Wal(StringBuilder md, PostgresWalSection? wal)
    {
        if (wal is null)
        {
            return;
        }

        md.Append("## Write-ahead log\n\n");
        Outcome(md, wal.Outcome, wal.Reason);

        if (wal.Outcome != PostgresReadOutcome.Read)
        {
            return;
        }

        md.Append("- **Records:** ").Append(wal.Records is { } records ? Count(records) : "unknown").Append('\n');
        md.Append("- **Full-page images:** ").Append(wal.FullPageImages is { } images ? Count(images) : "unknown").Append('\n');
        md.Append("- **Bytes written:** ").Append(wal.Bytes is { } bytes ? TableText.Bytes(bytes) : "unknown").Append('\n');
        md.Append("- **Writes and syncs:** ").Append(wal.Writes is { } writes ? Count(writes) : "unknown").Append(" and ").Append(wal.Syncs is { } syncs ? Count(syncs) : "unknown").Append('\n');
        md.Append("- **Counted since:** ").Append(MarkdownText.Utc(wal.StatsReset)).Append("\n\n");
    }

    private static void Connection(StringBuilder md, ConnectionSnapshotSection? connection)
    {
        if (connection is null)
        {
            return;
        }

        md.Append("## Connection\n\n");
        md.Append("- **Npgsql:** ").Append(MarkdownText.Code(connection.NpgsqlVersion)).Append('\n');

        if (connection.Platform is { Length: > 0 } platform)
        {
            md.Append("- **Platform:** ").Append(MarkdownText.Code(platform)).Append('\n');
        }

        md.Append("- **TLS:** ").Append(MarkdownText.Code(connection.Tls)).Append('\n');

        if (connection.Pooler is { Count: > 0 } poolers)
        {
            md.Append("- **Pooler:** ").Append(string.Join(", ", poolers.Select(static x => MarkdownText.Code(x)))).Append('\n');
        }

        md.Append('\n');

        if (connection.Settings.Count > 0)
        {
            md.Append("| Keyword | Value | From |\n|---|---|---|\n");

            foreach (ConnectionSnapshotSetting setting in connection.Settings)
            {
                md.Append("| ").Append(MarkdownText.Code(setting.Keyword, inTableCell: true))
                    .Append(" | ").Append(MarkdownText.Code(setting.Value, inTableCell: true))
                    .Append(" | ").Append(MarkdownText.Code(setting.From, inTableCell: true)).Append(" |\n");
            }

            md.Append('\n');
        }

        if (connection.Observed.Count > 0)
        {
            md.Append("What the server saw:\n\n| What | Value |\n|---|---|\n");

            foreach (ConnectionSnapshotValue value in connection.Observed)
            {
                md.Append("| ").Append(MarkdownText.Inline(value.Name)).Append(" | ").Append(MarkdownText.Code(value.Value, inTableCell: true)).Append(" |\n");
            }

            md.Append('\n');
        }

        foreach (ConnectionSnapshotHint hint in connection.Hints)
        {
            md.Append("- ").Append(MarkdownText.Code(hint.Id)).Append(hint.Keyword is null ? string.Empty : " (" + MarkdownText.Code(hint.Keyword) + ")")
                .Append(": ").Append(MarkdownText.Inline(hint.Text)).Append('\n');
        }

        if (connection.Hints.Count > 0)
        {
            md.Append('\n');
        }

        if (connection.Applications is { Count: > 0 } applications)
        {
            md.Append("| Application | Sessions | Oldest connection |\n|---|---:|---|\n");

            foreach (ConnectionSnapshotApplication application in applications)
            {
                md.Append("| ").Append(MarkdownText.Code(application.Application, inTableCell: true))
                    .Append(" | ").Append(Count(application.Sessions))
                    .Append(" | ").Append(MarkdownText.Utc(application.OldestConnection)).Append(" |\n");
            }

            md.Append('\n');
        }

        foreach (string note in connection.Notes)
        {
            md.Append("- ").Append(MarkdownText.Inline(note)).Append('\n');
        }

        if (connection.Notes.Count > 0)
        {
            md.Append('\n');
        }
    }

    private static void Withheld(StringBuilder md, PostgresDiagnosticsSnapshot snapshot)
    {
        if (snapshot.Withheld.Count == 0)
        {
            return;
        }

        md.Append("## Withheld from you\n\n");

        foreach (KeyValuePair<string, string> section in snapshot.Withheld.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            md.Append("- ").Append(MarkdownText.Code(section.Key)).Append(": ").Append(MarkdownText.Inline(section.Value)).Append('\n');
        }

        md.Append('\n');
    }

    private static void Outcome(StringBuilder md, PostgresReadOutcome outcome, string? reason)
    {
        if (outcome != PostgresReadOutcome.Read)
        {
            md.Append("Not read: ").Append(MarkdownText.Code(DiagnosticsJson.Name(outcome))).Append(reason is { Length: > 0 } ? " - " + MarkdownText.Inline(reason) : string.Empty).Append("\n\n");
        }
        else if (reason is { Length: > 0 })
        {
            md.Append(MarkdownText.Inline(reason)).Append("\n\n");
        }
    }

    private static string Count(long value) => value.ToString("N0", Invariant);

    private static string Milliseconds(double ms) =>
        ms >= 1000 ? (ms / 1000).ToString("N1", Invariant) + " s" : ms.ToString("N1", Invariant) + " ms";

    private static string SignedBytes(long bytes) => (bytes < 0 ? "-" : "+") + TableText.Bytes(Math.Abs(bytes));
}
