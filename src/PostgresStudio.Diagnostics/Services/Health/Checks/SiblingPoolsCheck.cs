using System.Globalization;

using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// C18: two or more Npgsql pools of this process connect to the data source's database as the same login, so the process can
/// open more connections than any one pool's ceiling says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted from the pools' own names.</b> Npgsql reports every pool to its meter under the pool's name, which is the data
/// source's connection string unless it was given a <c>Name</c> (<c>NpgsqlDataSource.Name = name ?? ConnectionString</c>, Npgsql
/// 9.0.4). The sweep parses each name in memory and compares the sorted <c>host:port</c> list, the database and the login with the
/// data source's own (<see cref="ConnectionPoolSiblings" />), and keeps only the counts. <b>No name, host, connection string or
/// password is ever kept or shown</b> (NP-DESIGN §2).
/// </para>
/// <para>
/// <b>Named pools are identified, not guessed (issue #56).</b> A name that is not a connection string - a data source the host
/// built with <c>NpgsqlDataSourceBuilder.Name</c>, as Aspire's integration and keyed registrations do - is looked up among the
/// data sources the studio already resolves (<see cref="NamedPoolIdentities" />) and counted like an unnamed pool with that data
/// source's identity. A named pool no resolved data source has stays unidentified, and the check says it did not judge it: a quiet
/// C18 is then not proof, and the verdict and the evidence count the pools it could not tell.
/// </para>
/// <para>
/// <b>Why Info.</b> Several pools are often deliberate (a pool per tenant database is a different database and is not counted;
/// a Wolverine pool beside Marten's is common). What the finding adds is the arithmetic: each pool may open its own Maximum Pool
/// Size, so N pools of 100 can hold 100 × N connections of this process on one database - C01 and C03 judge one pool, and this is
/// the sum they miss. The remedy is one shared <c>NpgsqlDataSource</c>.
/// </para>
/// <para>
/// <b>Not checked without a meter.</b> The pools are read from the process's Npgsql meter (<c>SessionPoolObserver</c>); where it
/// cannot be observed the check is "not checked", never quiet.
/// </para>
/// </remarks>
[HealthCheck("C18", HealthArea.Connections, HealthCostTier.Catalog, "Several pools to one database in this process")]
internal sealed class SiblingPoolsCheck : HealthCheck<ConnectionFacts>
{
    /// <inheritdoc />
    public override async Task<ConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ConnectionFacts facts = await ConnectionCheckSupport.ReadAsync(context).ConfigureAwait(false);

        if (facts.Siblings is null)
        {
            throw new HealthCheckUnavailableException(
                facts.SiblingsNotObserved
                ?? "This process's Npgsql pools cannot be observed (no pool name could be read from Npgsql's meter), so pools that share a database are not counted.");
        }

        return facts;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ConnectionPoolSiblings? siblings = facts.Siblings;

        if (siblings is null)
        {
            return HealthVerdict.Quiet(facts.SiblingsNotObserved ?? "This process's Npgsql pools were not observed.");
        }

        if (siblings.Pools < 2)
        {
            return HealthVerdict.Quiet(QuietNote(siblings));
        }

        string pools = siblings.Pools.ToString(CultureInfo.InvariantCulture);
        List<HealthEvidence> evidence =
        [
            new("Pools to this database as this login", pools),
            new(
                "Maximum Pool Size, added up",
                siblings.SumMaxPoolSize is { } sum
                    ? sum.ToString("N0", CultureInfo.InvariantCulture) + " connections at most"
                    : "not reported by every pool"),
        ];

        if (siblings.UnparsedNames > 0)
        {
            evidence.Add(new(
                "Named pools not identified, left out",
                siblings.UnparsedNames.ToString(CultureInfo.InvariantCulture)));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C18",
            Severity = HealthSeverity.Info,
            Title = pools + " Npgsql pools in this process connect to this database as the same login",
            WhyItMatters =
                "Every NpgsqlDataSource has a pool of its own, and each may open up to its own Maximum Pool Size. Several pools to one "
                + "database add up: the process can hold the sum of their ceilings, more than any one of them says, so C01's saturation "
                + "and C03's headroom, which judge one pool at a time, understate what this process can ask of the server. They also "
                + "idle and retire separately, so the server holds more idle connections than one shared pool would.",
            Evidence = evidence,
            Caveat =
                "Counted from the pool names Npgsql reports to its meter, compared by host, port, database and login. A pool per "
                + "tenant database is a different database and is not counted. A data source given a Name of its own is counted when "
                + "the studio resolves it (the one it reads, or one registered or listed with it); one it does not resolve cannot be "
                + "compared and is left out" + (siblings.UnparsedNames > 0 ? " (" + siblings.UnparsedNames.ToString(CultureInfo.InvariantCulture) + " here)" : string.Empty)
                + ". Several pools are sometimes deliberate - a separate pool for a long-running job - in which case the check can be "
                + "accepted with AcceptedChecks.",
            Remedy = new HealthRemedy(evaluation.Text.SharedDataSourceRemedyCode, evaluation.Text.SharedDataSourceRemedyExplanation),
            Link = SessionEvidence.HoldersLink,
        });
    }

    /// <summary>
    /// What a quiet verdict says. With every pool identified it counts none or one; with some unidentified it says the check did
    /// not judge them - quiet is then not proof - and what it did find (issue #56).
    /// </summary>
    private static string QuietNote(ConnectionPoolSiblings siblings)
    {
        if (siblings.UnparsedNames == 0)
        {
            return siblings.Pools == 0
                ? "No Npgsql pool of this process that connects to this database as this login reports to Npgsql's meter (pooling is off)."
                : "Only one Npgsql pool in this process connects to this database as this login.";
        }

        return "Not judged for " + siblings.UnparsedNames.ToString(CultureInfo.InvariantCulture)
            + " named pool(s) of this process that the studio cannot identify (a data source named with NpgsqlDataSourceBuilder.Name "
            + "that the studio does not resolve); "
            + (siblings.Pools == 0
                ? "no pool it can identify connects to this database as this login."
                : "one pool of this process connects to this database as this login.");
    }
}
