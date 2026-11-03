using System.Globalization;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C17: the data source's pool is switched off (<c>Pooling=false</c>) and nothing in front of it pools instead, so every
/// connection the application opens is a new server process.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it costs.</b> Without a pool every open is a TCP connection, a TLS handshake where TLS is on, an authentication
/// exchange and a new PostgreSQL backend process, and every close ends it: the cost is paid per connection, on the request's own
/// time, and the server's connection count follows the application's request rate. The evidence is the database's own session
/// statistics (<c>pg_stat_database.sessions</c> and <c>session_time</c>, PostgreSQL 14): an average session shorter than a second
/// is a connection per request, the pattern the pool exists to avoid (<see cref="ConnectionFacts.Sessions" />).
/// </para>
/// <para>
/// <b>Not applicable behind a pooler.</b> Npgsql's compatibility page says, for PgBouncer, "turn off Npgsql's internal connection
/// pool by specifying <c>Pooling=false</c>", so a data source that reaches a pooler (<see cref="PoolerSigns" />) is the advised
/// configuration, not a finding. A pooler the studio does not recognise by its host or port is not seen; the check can be
/// accepted (<c>AcceptedChecks</c>).
/// </para>
/// <para>
/// <b>Severity.</b> Warning when the average session is under a second, Info when it is longer or not known (older than
/// PostgreSQL 14, no session counted, a counter the role cannot read). The statistics count every client of this database, not
/// only this data source, and say so.
/// </para>
/// </remarks>
[HealthCheck("C17", HealthArea.Connections, HealthCostTier.Catalog, "Connection pooling switched off")]
internal sealed class PoolingOffCheck : HealthCheck<ConnectionFacts>
{
    /// <summary>An average session shorter than this, in milliseconds, is a connection per request: a Warning.</summary>
    public const double WarningBelowMs = 1000;

    /// <inheritdoc />
    public override async Task<ConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A pooler is the connection string's to say, so it stands the check down whether or not the server answered.
        return await ConnectionCheckSupport.ReadAsync(context, NotApplicableReason).ConfigureAwait(false);
    }

    /// <summary>Why this check does not apply to the data source (a pooler stands in front of it), or <see langword="null" /> when it does.</summary>
    /// <param name="facts">The connection facts.</param>
    public static string? NotApplicableReason(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Settings.Values.Pooling || facts.Settings.PoolerSigns.Count == 0)
        {
            return null;
        }

        return facts.Settings.PoolerSigns.Any(static x => x.IsPgBouncerFamily)
            ? "The data source reaches a connection pooler (PgBouncer or a service built on it), and Npgsql's compatibility page advises turning Npgsql's own pool off there: \"turn off Npgsql's internal connection pool by specifying Pooling=false\"."
              + " Pooling=false is the advised setting there, so it is not judged."
            : "The data source reaches an Amazon RDS Proxy, which pools the server's connections itself, so a client that turns its own pool off is not judged here.";
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.Settings.Values.Pooling)
        {
            return HealthVerdict.Quiet("Pooling is on (Npgsql's default), so connections are reused.");
        }

        ConnectionSessionStatistics? sessions = facts.Sessions;
        double? average = sessions?.AverageSessionMs;
        bool warning = average is { } ms && ms < WarningBelowMs;

        List<HealthEvidence> evidence = [new("Pooling", "off")];

        if (average is { } known && sessions is { Sessions: { } count })
        {
            evidence.Add(new(
                "Average session",
                Milliseconds(known) + " over " + count.ToString("N0", CultureInfo.InvariantCulture) + " sessions"
                + (sessions.StatsReset is { } reset ? " since " + TableText.When(reset) : string.Empty)));
        }
        else
        {
            evidence.Add(new(
                "Average session",
                "not known - the server counts sessions from PostgreSQL 14, and none may have ended since its statistics were reset"));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C17",
            Severity = warning ? HealthSeverity.Warning : HealthSeverity.Info,
            Title = "Connection pooling is switched off for " + SourceWords.DataSource(evaluation.Text),
            WhyItMatters =
                "With Pooling=false every connection the application opens is a new TCP connection, a TLS handshake where TLS is on, an "
                + "authentication exchange and a new PostgreSQL backend process, and closing it ends them all. That cost lands on the "
                + "request that needed the connection, and the server's connection count follows the application's request rate instead "
                + "of the size of a pool."
                + (warning
                    ? " The database's sessions last " + Milliseconds(average!.Value) + " on average: about one connection per request."
                    : string.Empty),
            Evidence = evidence,
            Caveat =
                "pg_stat_database counts every session of this database, not only this data source's, since its statistics were last "
                + "reset. Warning when the average is under a second, Info otherwise or when it is not known. No connection pooler was "
                + "recognised in front of the data source by its host or port; behind one the setting is the advised one "
                + "(Npgsql's compatibility page), and the check can be accepted with AcceptedChecks.",
            Remedy = new HealthRemedy(
                ConnectionCheckSupport.Block(
                    evaluation.Text,
                    "In the connection string the data source is built from (Npgsql's default):",
                    "Pooling=true",
                    "b.ConnectionStringBuilder.Pooling = true;"),
                "Reuses connections: the application pays for a connection once and the pool hands it out again. Size the pool with "
                + "Maximum Pool Size (C01, C03) once it is on."),
            Link = SessionEvidence.SessionsLink,
        });
    }

    private static string Milliseconds(double ms) =>
        ms < 1000
            ? ms.ToString("N0", CultureInfo.InvariantCulture) + " ms"
            : (ms / 1000).ToString("N1", CultureInfo.InvariantCulture) + " s";
}
