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
    /// UI; with this set the summary prints it joined to this base as the finding's link, and without it no link is printed.
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
    /// The summary of every database the diagnostics list: its overall state, the finding counts by severity, and each
    /// finding's code, severity and title - with its link when <paramref name="studioBaseUrl" /> is set.
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
                database, PostgresDiagnosticsRequest.FindingsOnly, cancellationToken);

            text.AppendLine();
            text.Append(database.Source).Append(" / ").Append(database.Database).Append(": ");

            if (snapshot.Outcome != PostgresReadOutcome.Read)
            {
                text.Append(snapshot.Outcome).Append(" - ").AppendLine(snapshot.Reason ?? "no reason given");
                continue;
            }

            // An accepted finding (PostgresDiagnosticsOptions.AcceptedChecks) is out of every count and state.
            PostgresFinding[] findings = [.. snapshot.Findings.Where(static x => !x.Accepted).OrderBy(static x => x.Severity).ThenBy(static x => x.Code, StringComparer.Ordinal)];
            int critical = findings.Count(static x => x.Severity == PostgresSeverity.Critical);
            int warning = findings.Count(static x => x.Severity == PostgresSeverity.Warning);
            int info = findings.Count(static x => x.Severity == PostgresSeverity.Info);
            string state = critical > 0 ? "CRITICAL" : warning > 0 ? "WARNING" : "OK";

            text.Append(state).Append(" (").Append(critical).Append(" critical, ").Append(warning).Append(" warning, ")
                .Append(info).AppendLine(" info)");

            foreach (PostgresFinding finding in findings)
            {
                text.Append("  ").Append(finding.Code).Append(' ').Append(finding.Severity).Append(": ").Append(finding.Title);

                if (!string.IsNullOrWhiteSpace(studioBaseUrl) && finding.StudioPath.Length > 0)
                {
                    text.Append(" - ").Append(studioBaseUrl.TrimEnd('/')).Append('/').Append(finding.StudioPath.TrimStart('/'));
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
