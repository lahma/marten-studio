using System.Globalization;
using System.Text;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PostgresStudio.Worker;

/// <summary>What <see cref="DiagnosticsSummaryService" /> reads from the <c>Summary</c> configuration section.</summary>
internal sealed class SummaryOptions
{
    /// <summary>How often the summary is built after the first one, at start: once a day by default.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Where Postgres Studio is mounted, when a host has one - <c>https://studio.example.com/postgres</c>. Each finding's
    /// studio path is a string relative to the mount (<c>health?store=default&amp;db=app</c>), which means nothing without a
    /// UI; with this set the summary prints each line's path joined to this base as its link, and without it no link is printed.
    /// </summary>
    public string? StudioBaseUrl { get; set; }
}

/// <summary>
/// Builds a plain-text summary of every database's health findings from <see cref="IPostgresDiagnostics" /> and
/// writes it to the host's log: at start, and then once every <see cref="SummaryOptions.Interval" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Writing it to the log is the "send".</b> The sample needs no mail or chat package, so the summary goes to the
/// host's own logger at Information. A real host would hand <c>text</c> (in <see cref="RunOnceAsync" />) to whatever it
/// already notifies through - an SMTP client, a Slack or Teams webhook, a ticket queue - and keep the loop exactly as it is.
/// </para>
/// <para>
/// <b>This is host code, not the studio.</b> D31's rule that nothing in the studio puts an API result into a log line
/// above Debug binds the packages, which never serve what the API returns. A worker that calls the API and decides to log
/// what it got is the host choosing where its own findings go, which is the API's purpose; whoever can read this
/// process's log can read these names, so a real host keeps it where only people who may know them can.
/// </para>
/// </remarks>
internal sealed partial class DiagnosticsSummaryService(
    IPostgresDiagnostics diagnostics,
    IOptions<SummaryOptions> options,
    ILogger<DiagnosticsSummaryService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = options.Value.Interval;

        // PeriodicTimer throws for a zero or negative period and for one past about 49 days; say which setting is wrong.
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException($"Summary:Interval is {interval}, which must be above zero and at most 30 days.");
        }

        using PeriodicTimer timer = new(interval);

        try
        {
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Summary}")]
    private static partial void LogSummary(ILogger logger, string summary);

    [LoggerMessage(Level = LogLevel.Error, Message = "The diagnostics summary could not be built; the next one is tried at the next interval.")]
    private static partial void LogFailure(ILogger logger, Exception exception);

    /// <summary>Builds one summary and sends it. A failure is logged and the next tick tries again.</summary>
    /// <param name="cancellationToken">Cancelled when the host stops.</param>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            string text = await BuildAsync(diagnostics, options.Value.StudioBaseUrl, cancellationToken);

            // Where a real host would send it (SMTP, a chat webhook, a ticket); the sample writes it to its log.
            LogSummary(logger, text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure(logger, exception);
        }
    }

    /// <summary>
    /// The connection history's peak line, from the snapshot's <c>ConnectionHistory</c> section (1.0.1, issue #47): the peak
    /// against the client limit now and when, over how many samples. <see langword="null" /> when no history is registered.
    /// </summary>
    /// <param name="history">The section, or <see langword="null" /> when the snapshot has none.</param>
    internal static string? HistoryLine(PostgresConnectionHistorySection? history)
    {
        if (history is null || history.Outcome == PostgresReadOutcome.NotApplicable)
        {
            return null;
        }

        if (history.Outcome != PostgresReadOutcome.Read)
        {
            return "Connection history: " + history.Outcome + " - " + (history.Reason ?? "no reason given");
        }

        string share = history.ClientLimitNow > 0
            ? " (" + ((double) history.Peak / history.ClientLimitNow * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%)"
            : string.Empty;

        return "Connection history: peak " + history.Peak.ToString("N0", CultureInfo.InvariantCulture) + " of "
            + history.ClientLimitNow.ToString("N0", CultureInfo.InvariantCulture) + " client connections" + share
            + " at " + history.PeakAt?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + ", " + history.Samples.ToString("N0", CultureInfo.InvariantCulture) + " samples since "
            + history.Since?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
            + (history.InstanceChanges > 0 ? ", " + history.InstanceChanges.ToString(CultureInfo.InvariantCulture) + " instance changes" : string.Empty);
    }

    /// <summary>
    /// The summary of every database the diagnostics list: its overall state, the finding counts by severity, and a line per
    /// check (<see cref="PostgresDiagnosticsSnapshot.FindingsByCheck" />) with its severity and title - and its link when
    /// <paramref name="studioBaseUrl" /> is set.
    /// </summary>
    /// <param name="diagnostics">The diagnostics API.</param>
    /// <param name="studioBaseUrl">Where a studio is mounted, or <see langword="null" /> for no links.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    internal static async Task<string> BuildAsync(IPostgresDiagnostics diagnostics, string? studioBaseUrl, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        text.AppendLine("Postgres diagnostics summary");

        int databases = 0;

        await foreach (PostgresDatabaseRef database in diagnostics.ListDatabasesAsync(cancellationToken))
        {
            databases++;

            PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(
                database, PostgresDiagnosticsRequest.FindingsOnly with { ConnectionHistory = true }, cancellationToken);

            text.AppendLine();
            text.Append(database.Source).Append(" / ").Append(database.Database).Append(": ");

            if (snapshot.Outcome != PostgresReadOutcome.Read)
            {
                text.Append(snapshot.Outcome).Append(" - ").AppendLine(snapshot.Reason ?? "no reason given");
                continue;
            }

            // One line per check, as the Health page lists them: the snapshot groups them, worst first, and leaves out the
            // findings the host accepted (PostgresDiagnosticsOptions.AcceptedChecks), which are out of every count and state.
            IReadOnlyList<PostgresFindingGroup> groups = snapshot.FindingsByCheck();
            int critical = groups.Sum(static x => x.Findings.Count(static f => f.Severity == PostgresSeverity.Critical));
            int warning = groups.Sum(static x => x.Findings.Count(static f => f.Severity == PostgresSeverity.Warning));
            int info = groups.Sum(static x => x.Findings.Count(static f => f.Severity == PostgresSeverity.Info));
            string state = critical > 0 ? "CRITICAL" : warning > 0 ? "WARNING" : "OK";

            text.Append(state).Append(" (").Append(critical).Append(" critical, ").Append(warning).Append(" warning, ")
                .Append(info).AppendLine(" info)");

            if (HistoryLine(snapshot.ConnectionHistory) is { } history)
            {
                text.Append("  ").AppendLine(history);
            }

            foreach (PostgresFindingGroup group in groups)
            {
                text.Append("  ").Append(group.Severity).Append("  ").Append(group.Title);

                if (group.Count > 1)
                {
                    text.Append(" (").Append(group.Count).Append(')');
                }
                else if (!string.IsNullOrWhiteSpace(group.Subject))
                {
                    text.Append(": ").Append(group.Subject);
                }

                if (!string.IsNullOrWhiteSpace(studioBaseUrl) && group.StudioPath.Length > 0)
                {
                    text.Append(" - ").Append(studioBaseUrl.TrimEnd('/')).Append('/').Append(group.StudioPath.TrimStart('/'));
                }

                text.AppendLine();
            }
        }

        if (databases == 0)
        {
            text.AppendLine().AppendLine("No database is registered: add an NpgsqlDataSource, or name one in AddPostgresDiagnostics.");
        }

        return text.ToString();
    }
}
