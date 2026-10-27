using System.Globalization;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C13: while the server's client connections are nearly used up, one application holds at least a tenth of them idle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it waits for usage.</b> A pool keeps a connection open and idle until its idle lifetime ends - five minutes by default in
/// Npgsql (<c>Connection Idle Lifetime=300</c>) - so ten idle connections are just a warm pool, and a finding about them would
/// be raised on every healthy host. After a burst, though, one container's pool can sit on a large share of what the server
/// allows while the others, and a person with <c>psql</c>, are refused with "sorry, too many clients already". So the check
/// speaks only while the server is at or above C03's own Warning line (<see cref="ConnectionThresholds.IdleHoardUsage" />, the
/// same number C03 judges: <see cref="ServerConnectionLimits.ClientBackends" /> against <see cref="ServerConnectionLimits.Available" />),
/// and then names the application - summed across every database it connects to, because a pool per tenant database is one
/// application - that holds idle at least <see cref="ConnectionThresholds.IdleHoardShare" /> of the client limit.
/// </para>
/// <para>
/// <b>Idle is <c>idle</c>.</b> A session idle inside a transaction is C04's (and holds locks, which this does not); one whose state
/// the role may not read - another role's, without <c>pg_read_all_stats</c> - is counted as neither, and the finding says so, so
/// what it counts is a lower bound. A session with no application name is an application of its own here ("no application
/// name"), because the unnamed pool is as likely a hoarder as any, and C14 is the check that says it cannot be told apart.
/// </para>
/// <para>
/// <b>An activity check</b> (D29): every finding names applications, roles, client addresses and databases of other people's
/// sessions, so the whole check is behind <c>ViewDatabaseActivity</c>, and the diagnostics API returns it only with
/// <c>IncludeActivity</c>. It reads nothing of its own: the budget is C03's read and the holders are the sweep's one read
/// (<see cref="ServerConnectionHolders" />). The studio never ends a session; the remedy is the holder's pool, or the server's own
/// <c>idle_session_timeout</c>, for whoever runs them.
/// </para>
/// </remarks>
[HealthCheck("C13", HealthArea.Connections, HealthCostTier.Catalog, "Idle connections hoarded near the limit", Gate = HealthGate.Activity)]
internal sealed class IdleConnectionsHoardedCheck : HealthCheck<ConnectionHolderFacts>
{
    /// <inheritdoc />
    public override Task<ConnectionHolderFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        ServerConnectionHolders.FactsAsync(context);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionHolderFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ServerConnectionLimits limits = facts.Limits;
        double usage = (double) limits.ClientBackends / limits.Available;
        string used = Count(limits.ClientBackends) + " of " + Count(limits.Available) + " (" + SessionText.Percent(usage) + ")";

        if (usage < ConnectionThresholds.IdleHoardUsage)
        {
            return HealthVerdict.Quiet(
                used + " of the client connections an ordinary role may open are in use on this server - under the "
                + SessionText.Percent(ConnectionThresholds.IdleHoardUsage) + " from which idle connections are judged: below it, idle connections are "
                + "a warm pool, not a finding." + Suffix(facts.VisibilityNote));
        }

        List<Hoarder> hoarders = [.. Hoarders(facts)];

        if (hoarders.Count == 0)
        {
            return HealthVerdict.Quiet(
                used + " of the client connections an ordinary role may open are in use, and no application holds "
                + Count(Threshold(limits)) + " or more of them idle - " + SessionText.Percent(ConnectionThresholds.IdleHoardShare) + " of the " + Count(limits.Available)
                + " the server allows ordinary roles." + Suffix(facts.VisibilityNote) + Suffix(facts.TruncationNote));
        }

        Hoarder top = hoarders[0];

        List<HealthEvidence> evidence =
        [
            new("Client connections in use", used),
            new("Client limit", limits.AvailableText),
            new("Hoarding at least " + SessionText.Percent(ConnectionThresholds.IdleHoardShare) + " idle", SessionText.Number(hoarders.Count) + " "
                + (hoarders.Count == 1 ? "application" : "applications") + ", " + Count(Threshold(limits)) + " idle connections or more each"),
        ];

        for (int i = 0; i < Math.Min(hoarders.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            evidence.Add(new HealthEvidence(
                SessionEvidence.Label("Application", i, "most idle"),
                Describe(hoarders[i], limits),
                HealthGate.Activity));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C13",
            Severity = HealthSeverity.Warning,
            Title = hoarders.Count == 1
                ? "One application holds " + SessionText.Count(top.Idle, "idle connection") + ", " + SessionText.Percent(Share(top.Idle, limits))
                  + " of the server's client limit, while the server is " + SessionText.Percent(usage) + " used"
                : SessionText.Number(hoarders.Count) + " applications each hold at least " + SessionText.Percent(ConnectionThresholds.IdleHoardShare)
                  + " of the server's client limit idle, while the server is " + SessionText.Percent(usage) + " used",
            WhyItMatters =
                "A connection pool keeps a connection open and idle until its idle lifetime ends - five minutes by default in Npgsql - so "
                + "after a burst one container's pool can hold a tenth of everything the server allows while it waits for work. Once every "
                + "connection an ordinary role may open is taken, the next one is refused with \"sorry, too many clients already\" - "
                + evaluation.Text.NextConnectionExamples + ", a person with psql. Idle connections are only a warm pool while there is room, "
                + "which is why this finding waits until the server is " + SessionText.Percent(ConnectionThresholds.IdleHoardUsage)
                + " used. Connections to every database on the server count, " + evaluation.Text.EveryDatabasesConnections + ".",
            Evidence = evidence,
            Caveat =
                "Counted in pg_stat_activity across every database on the server at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                + ": idle means a session whose state is idle, which a session idle inside a transaction (C04) is not, and a session with no "
                + "application name is counted as an application of its own. An application's connections are summed across every database "
                + "and every role it uses. A moment's reading: a pool's connections come and go between two checks."
                + Suffix(facts.VisibilityNote) + Suffix(facts.TruncationNote),
            Link = SessionEvidence.HoldersLink,
            Remedy = new HealthRemedy(
                "Connection Idle Lifetime=60",
                "Npgsql closes an idle pooled connection after Connection Idle Lifetime (300 seconds by default), but only down to Minimum Pool Size "
                + "and as often as Connection Pruning Interval (10 seconds) comes round - so after a burst a container's pool holds what it "
                + "grew to for five minutes. Shorten the idle lifetime in the holder's connection string, or lower its Maximum Pool Size: "
                + "the cap is per process, and every container's pool counts against the server. The server's own backstop is "
                + "idle_session_timeout (PostgreSQL 14 and later) for the holder's role or database, which ends a session idle that long "
                + "whatever its pool thinks - set it above the pool's own idle lifetime. The studio never ends a session and changes none of these."),
        });
    }

    /// <summary>
    /// The applications that hold idle at least <see cref="ConnectionThresholds.IdleHoardShare" /> of the client limit - one in
    /// <see cref="ConnectionThresholds.IdleHoardDivisor" />, in integers - the most idle first, ties by name.
    /// </summary>
    internal static IEnumerable<Hoarder> Hoarders(ConnectionHolderFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        int available = facts.Limits.Available;

        return facts.Holders.Groups
            .GroupBy(static x => x.Application, StringComparer.Ordinal)
            .Select(static x => new Hoarder(x.Key, x.Sum(static y => y.Idle), x.Sum(static y => y.Total), [.. x]))
            .Where(x => x.Idle * ConnectionThresholds.IdleHoardDivisor >= available)
            .OrderByDescending(static x => x.Idle)
            .ThenBy(static x => x.Application, StringComparer.Ordinal);
    }

    /// <summary>One application's connections, summed across every database, role and address it uses.</summary>
    /// <param name="Application">Its <c>application_name</c>; empty for the sessions that set none.</param>
    /// <param name="Idle">How many of its sessions are idle.</param>
    /// <param name="Total">How many sessions it has.</param>
    /// <param name="Groups">Its groups.</param>
    internal sealed record Hoarder(string Application, long Idle, long Total, IReadOnlyList<SessionHolderGroup> Groups);

    /// <summary>The fewest idle connections that make a hoarder, as a count: a tenth of the client limit, rounded up.</summary>
    internal static long Threshold(ServerConnectionLimits limits) =>
        (limits.Available + ConnectionThresholds.IdleHoardDivisor - 1) / ConnectionThresholds.IdleHoardDivisor;

    /// <summary>
    /// "14 idle of 22 connections, 14% of the client limit, in orders (10), billing (4), from app@10.0.0.5 (9), app@10.0.0.6 (5)" - the
    /// databases and the holders by their idle sessions, the most first.
    /// </summary>
    private static string Describe(Hoarder hoarder, ServerConnectionLimits limits)
    {
        string databases = string.Join(", ", hoarder.Groups
            .Where(static x => x.Idle > 0)
            .GroupBy(static x => x.Database, StringComparer.Ordinal)
            .Select(static x => (Name: x.Key, Idle: x.Sum(static y => y.Idle)))
            .OrderByDescending(static x => x.Idle)
            .ThenBy(static x => x.Name, StringComparer.Ordinal)
            .Take(ConnectionThresholds.SessionsNamed)
            .Select(static x => (x.Name.Length == 0 ? "(no database)" : x.Name) + " (" + Count(x.Idle) + ")"));

        string holders = string.Join(", ", hoarder.Groups
            .Where(static x => x.Idle > 0)
            .GroupBy(static x => ServerConnectionHolders.Who(x), StringComparer.Ordinal)
            .Select(static x => (Who: x.Key, Idle: x.Sum(static y => y.Idle)))
            .OrderByDescending(static x => x.Idle)
            .ThenBy(static x => x.Who, StringComparer.Ordinal)
            .Take(ConnectionThresholds.SessionsNamed)
            .Select(static x => x.Who + " (" + Count(x.Idle) + ")"));

        return ServerConnectionHolders.ApplicationLabel(hoarder.Application) + ": " + Count(hoarder.Idle) + " idle of "
            + SessionText.Count(hoarder.Total, "connection") + ", " + SessionText.Percent(Share(hoarder.Idle, limits)) + " of the client limit"
            + ", idle in " + databases + ", from " + holders;
    }

    private static double Share(long idle, ServerConnectionLimits limits) => (double) idle / limits.Available;

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A sentence added after another, with its space - or nothing.</summary>
    private static string Suffix(string? note) => note is null ? string.Empty : " " + note;
}
