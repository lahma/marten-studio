using System.Globalization;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C14: client connections on the server set no application name, so nothing else in the advisor can say whose they are.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a finding at all.</b> Every other connection finding - C03's headroom, C04's idle transactions, C13's hoarded idle
/// connections - can name an application, and ask its owner to change something; a connection with an empty
/// <c>application_name</c> is a session nobody can attribute, so those findings about it end at "somebody's". The fix is one
/// connection-string option, <c>Application Name</c>, set per container and pool, and until it is set the server's connections
/// cannot be told apart. Info, never more: an unnamed connection is not a fault, it is the reason a fault cannot be pinned on
/// anyone.
/// </para>
/// <para>
/// <b>What it counts</b> is every client session of the whole server, every database included, whose application name is empty,
/// the studio's own reading session aside. The studio's other reads share the host's data source, and so its application name:
/// they are counted under it, as on the rest of Database › Sessions. It names the largest few holders as
/// <c>role@address/database (count)</c> - the three things that tell one unnamed pool from another.
/// </para>
/// <para>
/// <b>An activity check</b> (D29): the roles, addresses and databases of other people's sessions are what the finding names, so it
/// is behind <c>ViewDatabaseActivity</c>, and the diagnostics API returns it only with <c>IncludeActivity</c>. It reads nothing
/// of its own: the holders are the sweep's one read (<see cref="ServerConnectionHolders" />), shared with C13.
/// </para>
/// </remarks>
[HealthCheck("C14", HealthArea.Connections, HealthCostTier.Catalog, "Connections with no application name", Gate = HealthGate.Activity)]
internal sealed class UnattributedConnectionsCheck : HealthCheck<ConnectionHolderFacts>
{
    /// <inheritdoc />
    public override Task<ConnectionHolderFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        ServerConnectionHolders.FactsAsync(context);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionHolderFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<SessionHolderGroup> unnamed = [.. facts.Holders.Groups.Where(static x => x.Application.Length == 0)];
        long total = unnamed.Sum(static x => x.Total);

        if (total == 0)
        {
            return HealthVerdict.Quiet(
                "Every client connection on the server sets an application name." + Suffix(facts.TruncationNote));
        }

        ServerConnectionLimits limits = facts.Limits;
        double share = (double) total / limits.Available;

        List<SessionHolderGroup> largest =
        [
            .. unnamed
                .OrderByDescending(static x => x.Total)
                .ThenBy(static x => x.Database, StringComparer.Ordinal)
                .ThenBy(static x => x.Role, StringComparer.Ordinal)
                .ThenBy(static x => x.ClientText, StringComparer.Ordinal)
                .Take(ConnectionThresholds.SessionsNamed),
        ];

        List<HealthEvidence> evidence =
        [
            new("Without an application name", SessionText.Count(total, "client connection") + " of the " + Count(limits.Available) + " the server allows ordinary roles ("
                + SessionText.Percent(share) + ")"),
            new("Of the server's client connections", Count(limits.ClientBackends) + " in use (" + SessionText.Percent((double) limits.ClientBackends / limits.Available) + ")"),
        ];

        for (int i = 0; i < largest.Count; i++)
        {
            evidence.Add(new HealthEvidence(
                SessionEvidence.Label("Holder", i, "largest"),
                ServerConnectionHolders.Holder(largest[i], largest[i].Total),
                HealthGate.Activity));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C14",
            Severity = HealthSeverity.Info,
            Title = total == 1
                ? "A client connection sets no application name, so it cannot be attributed"
                : SessionText.Number(total) + " client connections set no application name, so they cannot be attributed",
            WhyItMatters =
                "Every other connection finding - how full the server is, which sessions sit idle in a transaction, which application "
                + "holds idle connections - can name whose connections they are, and ask whoever owns them to change something. A "
                + "connection with no application name is nobody's: whatever is wrong with it, nothing says whom to ask. Naming each "
                + "pool of each container is one connection-string option, and it turns \"somebody holds " + SessionText.Number(total)
                + " connections\" into a name.",
            Evidence = evidence,
            Caveat =
                "Counted in pg_stat_activity across every database on the server at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                + ", the studio's own reading session aside; the studio's other reads share the host's data source, and so its application name, "
                + "and are counted under it. Every role may read every session's application name. A moment's reading: a pool's connections "
                + "come and go between two checks." + Suffix(facts.TruncationNote),
            Link = SessionEvidence.HoldersLink,
            Remedy = new HealthRemedy(
                "Application Name=billing-api/orders",
                "Set Application Name in each connection string - Npgsql sends it to the server on every connection, and it shows in "
                + "pg_stat_activity and, with %a, in the server's log. Name it by the container and the pool, \"{host}/{pool}\", so one row "
                + "of Database › Sessions › Holders is one pool of one container. It is visible to every role on the server and in its "
                + "logs, so it must carry nothing secret."),
        });
    }

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A sentence added after another, with its space - or nothing.</summary>
    private static string Suffix(string? note) => note is null ? string.Empty : " " + note;
}
