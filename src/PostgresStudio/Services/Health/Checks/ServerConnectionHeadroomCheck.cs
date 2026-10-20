using System.Globalization;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C03: the server's connections are nearly used up - every client backend on the server against
/// <c>max_connections</c> less what is reserved.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole server, deliberately, and only as a count.</b> Every other session read is bounded to this
/// database; this one cannot be, because <c>max_connections</c> is the server's and every database on it spends
/// it. Only the number of client backends is read, and nothing about the other databases' sessions.
/// </para>
/// <para>
/// <b>Whole without <c>pg_read_all_stats</c>, by counting what it cannot see into.</b> <c>pg_stat_activity</c>
/// has a row for every backend for every role, but a role without <c>pg_read_all_stats</c> reads another role's
/// row with its <c>backend_type</c> null. Such a row is counted when it has a role behind it and a database
/// (<see cref="PostgresStudio.Internal.Sql.HealthConnectionQueries.ClientSession" />), which every client session has - so a
/// hidden background worker running for a role in a database is counted too, and the count can be over by that
/// much, never under.
/// </para>
/// <para>
/// Background workers, autovacuum and (from PostgreSQL 12) replication senders have slots of their own and are
/// not counted against <c>max_connections</c>, so they are left out. A superuser may use the reserved slots,
/// which is how the count can pass what an ordinary role may open.
/// </para>
/// </remarks>
[HealthCheck("C03", HealthArea.Connections, HealthCostTier.Catalog, "Server connection headroom")]
internal sealed class ServerConnectionHeadroomCheck : HealthCheck<ServerConnectionLimits>
{
    /// <inheritdoc />
    public override Task<ServerConnectionLimits> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        ServerConnectionLimits.SharedAsync(context);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ServerConnectionLimits facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        double share = (double) facts.ClientBackends / facts.Available;
        string used = facts.ClientBackends.ToString("N0", CultureInfo.InvariantCulture) + " of "
            + facts.Available.ToString("N0", CultureInfo.InvariantCulture);

        if (share <= ConnectionThresholds.ServerUsedWarning)
        {
            return HealthVerdict.Quiet(used + " connections an ordinary role may open are in use on this server.");
        }

        bool critical = share >= ConnectionThresholds.ServerUsedCritical;

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C03",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "The server has almost no connections left"
                : "The server's connections are more than " + SessionText.Percent(ConnectionThresholds.ServerUsedWarning) + " used",
            WhyItMatters =
                "Once every connection an ordinary role may open is taken, the next one is refused with \"sorry, too many clients "
                + "already\" - a pool that wants to grow, a deployment's new instance, the async daemon's next connection, a person "
                + "with psql. Only superusers keep the reserved slots. Connections to every database on the server count, this "
                + "store's and everybody else's.",
            Evidence =
            [
                new("In use", used + " (" + SessionText.Percent(share) + ")"),
                new("To this database", facts.ThisDatabase.ToString("N0", CultureInfo.InvariantCulture)),
                new("Available to ordinary roles", facts.AvailableText),
            ],
            Caveat =
                "Counts every client backend on the server, every database included. A session the studio's role may not read into "
                + "is counted too, because Postgres withholds its backend type rather than its row - so a hidden background worker that "
                + "runs for a role can be counted with them. Background workers it can see, autovacuum and replication have slots of "
                + "their own and are not counted. A moment's reading: a burst can come and go between two checks.",
            Remedy = new HealthRemedy(
                """
                // Fewer connections per process - every data source Marten builds from a connection string:
                opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 50);
                """,
                "Lower pool ceilings across the applications that share the server, or a pooler such as PgBouncer in front of it. "
                + "max_connections itself is the server's setting, which the studio never changes; raising it costs memory per "
                + "connection on the server."),
        });
    }
}
