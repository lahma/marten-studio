using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C15 reads.</summary>
/// <param name="View">The history's samples of the last day, summarized - or <see langword="null" /> when it holds none.</param>
/// <param name="Server">The server, <c>host:port</c> as the history keys it.</param>
/// <param name="Judged">How far back the samples reach: a day, or the history's whole retention when that is shorter.</param>
/// <param name="Window">The history's retention, which Database › Sessions › Holders shows whole.</param>
/// <param name="Limits">The server's connection budget (C03's memoized read), which words the remedy; <see langword="null" /> only in a test.</param>
/// <param name="Platform">Who runs the server, for where its connections are raised.</param>
/// <param name="Capacity">What the host's capacity source said this interval, or <see langword="null" />.</param>
internal sealed record ConnectionNearMissFacts(
    ConnectionHistoryView? View,
    string Server,
    TimeSpan Judged,
    TimeSpan Window,
    ServerConnectionLimits? Limits,
    PostgresPlatform Platform,
    CapacityReading? Capacity);

/// <summary>
/// C15 (1.0.1, issue #47): the server's client connections peaked close to the client limit within the last day - read from the
/// connection history, so it catches the burst at 03:00 that was gone before anyone looked, which C03 (the connections now)
/// cannot.
/// </summary>
/// <remarks>
/// <para>
/// <b>The peak against the limit as it is now.</b> The highest sample of the last day (or of the history's whole retention when that
/// is shorter) is compared with the client limit of the <i>latest</i> sample, not the one at the peak: a cluster that was resized
/// after the peak - Aurora's maximum capacity raised, a larger instance - has room now, and the finding clears. Warning from
/// <see cref="ConnectionThresholds.ServerUsedWarning" />, Critical from <see cref="ConnectionThresholds.ServerUsedCritical" />, C03's
/// own lines. A limit the latest sample did not record (zero) is no finding.
/// </para>
/// <para>
/// <b>It reads the history, not the database.</b> The samples come from <see cref="ConnectionHistoryReader" /> - the one read Holders
/// and the diagnostics API share, with its 30-second cache - through <see cref="ConnectionHistoryLookup" />, so the three can never
/// disagree on a peak. Without a connection history (<c>AddPostgresConnectionHistory()</c>) the check is not applicable; with one
/// that holds no sample of the last day, it is quiet. It opens no connection of its own: it asks for C03's memoized read of the
/// server's settings - which fails with C03 when the database cannot be read, so the diagnostics API's judgement of whether the
/// database was reached is not fooled by a check that read only the history - and, only when the peak came close, for the platform
/// and the capacity, the remedy's words.
/// </para>
/// <para>
/// <b>Gates.</b> The finding and its numbers - peak, limits, when, samples, instance changes - are at the default gate, C03's: the
/// count of connections is the server's own. Which applications held the most is other applications' names, so it is evidence
/// behind <see cref="HealthGate.Activity" />, withheld per visitor and from the diagnostics API without <c>IncludeActivity</c>.
/// </para>
/// </remarks>
[HealthCheck("C15", HealthArea.Connections, HealthCostTier.Catalog, "Connections came close to the limit")]
internal sealed class ConnectionNearMissCheck : HealthCheck<ConnectionNearMissFacts>
{
    /// <inheritdoc />
    public override async Task<ConnectionNearMissFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Services.GetService<ConnectionHistoryReader>() is not { } reader)
        {
            throw new HealthCheckNotApplicableException(
                "No connection history is kept: register AddPostgresConnectionHistory() to have the server's connections sampled, and this check judges their peak.");
        }

        if (ConnectionHistoryLookup.ServerOf(context.Source) is not { } server)
        {
            throw new HealthCheckNotApplicableException("The source exposes no data source, so there is no server whose connections could be sampled.");
        }

        // C03's own read of the server's settings (memoized: a sweep makes it once, and C03 asks for it too) comes first and is not
        // caught: when the database cannot be read this check fails with C03, as every check that reads the catalog does - which the
        // diagnostics API's "was the database reached" judges by - instead of judging a peak of a server it cannot reach.
        ServerConnectionLimits limits = await ServerConnectionLimits.SharedAsync(context).ConfigureAwait(false);

        TimeSpan judged = reader.Window < ConnectionThresholds.NearMissWindow ? reader.Window : ConnectionThresholds.NearMissWindow;

        // Names are in the facts and gated in the evidence, as C13's holders are: the sweep is the database's, the gate is per visitor.
        ConnectionHistoryRead read = await ConnectionHistoryLookup.ReadAsync(reader, server, names: true, context.Now - judged, cancellationToken).ConfigureAwait(false);

        if (read.Outcome == PostgresReadOutcome.TimedOut)
        {
            throw new HealthCheckUnavailableException("The connection history's store took longer than QueryTimeout to answer.");
        }

        if (read.Outcome == PostgresReadOutcome.Failed)
        {
            throw new InvalidOperationException("The connection history could not be read.", read.Failure);
        }

        PostgresPlatform platform = PostgresPlatform.SelfHosted;
        CapacityReading? capacity = null;

        if (read.View is { } view && Share(view) is { } share && share >= ConnectionThresholds.ServerUsedWarning)
        {
            // The words of the remedy only, as C03 reads them: a failure of either words it as a server of somebody's own.
            try
            {
                platform = (await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false)).Platform;
                capacity = await CapacityReading.SharedAsync(context).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                capacity = null;
            }
        }

        return new ConnectionNearMissFacts(read.View, server, judged, reader.Window, limits, platform, capacity);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionNearMissFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        string span = Span(facts.Judged);

        if (facts.View is not { } view)
        {
            return HealthVerdict.Quiet("The connection history holds no sample of this server from the last " + span + ".");
        }

        if (Share(view) is not { } share)
        {
            return HealthVerdict.Quiet(
                "The latest sample did not record the server's client limit, so the peak of " + Count(view.Peak) + " connections in the last " + span + " is not judged.");
        }

        string peak = Count(view.Peak) + " of " + Count(view.ClientLimitNow) + " (" + SessionText.Percent(share) + ")";

        if (share < ConnectionThresholds.ServerUsedWarning)
        {
            return HealthVerdict.Quiet(
                "The peak of the server's client connections in the last " + span + " was " + peak + ", under the "
                + SessionText.Percent(ConnectionThresholds.ServerUsedWarning) + " from which a peak is raised.");
        }

        bool critical = share >= ConnectionThresholds.ServerUsedCritical;
        string reached = view.PeakAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

        List<HealthEvidence> evidence =
        [
            new("Peak", peak + ", reached at " + reached),
            new("Client limit now", Count(view.ClientLimitNow)),
        ];

        if (view.ClientLimit != view.ClientLimitNow)
        {
            evidence.Add(new HealthEvidence("Client limit at the peak", Count(view.ClientLimit)));
        }

        evidence.Add(new HealthEvidence(
            "Samples",
            Count(view.Samples) + " from " + view.Since.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + " to " + view.Until.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)));
        evidence.Add(new HealthEvidence("Instance changes", Count(view.InstanceChanges)));

        IReadOnlyList<ConnectionHistoryPeak> applications = [.. view.Applications.Take(ConnectionThresholds.NearMissApplicationsNamed)];

        for (int i = 0; i < applications.Count; i++)
        {
            evidence.Add(new HealthEvidence(
                SessionEvidence.Label("Application", i, "highest peak"),
                ApplicationLabel(applications[i].Application) + ": " + SessionText.Count(applications[i].Peak, "connection")
                + " at its peak, " + SessionText.Percent((double) applications[i].Peak / view.ClientLimitNow) + " of the client limit now, at "
                + applications[i].At.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
                HealthGate.Activity));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C15",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "Connections almost reached the client limit: a peak of " + peak + " in the last " + span
                : "Connections came within " + Count(Math.Max(0, view.ClientLimitNow - view.Peak)) + " of the client limit: a peak of " + peak + " in the last " + span,
            WhyItMatters =
                "The server's client connections peaked at " + Count(view.Peak) + " at " + reached + ", within the last " + span
                + ", against a client limit of " + Count(view.ClientLimitNow) + " connections now. Once every connection an ordinary role "
                + "may open is taken, the next one is refused with \"sorry, too many clients already\" - "
                + evaluation.Text.NextConnectionExamples + ", a person with psql. C03 judges the connections now; this reads the history, "
                + "so it catches a burst that was over before anyone looked, and the next one may be a little higher. Connections to every "
                + "database on the server count, " + evaluation.Text.EveryDatabasesConnections + ".",
            Evidence = evidence,
            Caveat =
                "Judged from the connection history's samples of this server, " + Count(view.Samples) + " in the last " + span
                + ": a burst between two samples is not seen, so the real peak can be higher. The peak is compared with the client limit "
                + "of the latest sample, so a resize clears this finding. Database › Sessions › Holders shows the whole " + Span(facts.Window)
                + " the history keeps, and which applications held the connections.",
            Link = SessionEvidence.HoldersLink,
            Remedy = ServerConnectionHeadroomCheck.ServerRemedy(facts.Platform, facts.Capacity, facts.Limits, evaluation.Text),
        });
    }

    /// <summary>The peak against the client limit of the latest sample, or <see langword="null" /> when that limit was not recorded.</summary>
    internal static double? Share(ConnectionHistoryView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return view.ClientLimitNow > 0 ? (double) view.Peak / view.ClientLimitNow : null;
    }

    private static string Span(TimeSpan span) =>
        span.TotalDays >= 2 && span.TotalDays % 1 == 0 ? Count((long) span.TotalDays) + " days"
        : span.TotalHours >= 1 && span.TotalHours % 1 == 0 ? Count((long) span.TotalHours) + (span.TotalHours == 1 ? " hour" : " hours")
        : Count((long) Math.Round(span.TotalMinutes)) + " minutes";

    /// <summary>"'billing-api'", or "(no application name)" for sessions that set none.</summary>
    private static string ApplicationLabel(string? application) => string.IsNullOrEmpty(application) ? "(no application name)" : "'" + application + "'";

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
