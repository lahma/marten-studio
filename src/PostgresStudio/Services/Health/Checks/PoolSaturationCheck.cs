using System.Globalization;

using PostgresStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C01 reads.</summary>
/// <param name="Pooling">Whether the database's data source pools at all.</param>
/// <param name="ConfiguredMax">Its connection string's <c>Max Pool Size</c>.</param>
/// <param name="Used">Connections lent out, as Npgsql's pool reports it.</param>
/// <param name="Idle">Connections open and idle.</param>
/// <param name="ReportedMax">The ceiling Npgsql reports, which wins over the configured one when present.</param>
/// <param name="PoolsInProcess">How many Npgsql pools the process has, for the caveat.</param>
/// <param name="ReadAt">When the pool was read.</param>
internal sealed record PoolFacts(
    bool Pooling,
    int ConfiguredMax,
    int Used,
    int Idle,
    int? ReportedMax,
    int PoolsInProcess,
    DateTimeOffset ReadAt)
{
    /// <summary>The ceiling the pool is judged against.</summary>
    public int Max => ReportedMax is > 0 ? ReportedMax.Value : ConfiguredMax;

    /// <summary>Whether every connection is lent out.</summary>
    public bool Saturated => Pooling && Max > 0 && Used >= Max;
}

/// <summary>
/// C01: this process's connection pool to the database is (nearly) used up - read in process, from Npgsql's own
/// pool counters.
/// </summary>
/// <remarks>
/// <para>
/// Tier 0: no SQL. <see cref="SessionPoolObserver" /> listens to Npgsql's two observable pool instruments for
/// as long as this gatherer holds a lease, and reads them once. Synchronous instruments are never enabled, so
/// nothing is recorded per command - which is also why "pending requests" is not read: Npgsql 9.0.4 keeps that
/// one as a synchronous counter. A pool lent out to its ceiling on two consecutive samples is the same
/// condition seen from the other side: every request that wants a connection then is waiting.
/// </para>
/// <para>
/// The pool is matched to the database by Npgsql's <c>pool.name</c> tag, which is the data source's connection
/// string unless the host named the data source; a named one cannot be matched, and the check says so
/// (<see cref="HealthCheckUnavailableException" />) rather than guessing.
/// </para>
/// </remarks>
[HealthCheck("C01", HealthArea.Connections, HealthCostTier.InProcess, "Connection pool saturated")]
internal sealed class PoolSaturationCheck : HealthCheck<PoolFacts>
{
    /// <summary>Whether the pool was fully lent out, by sweep.</summary>
    internal const string SaturatedSeries = "connections.pool-saturated";

    /// <summary>Connections lent out, by sweep.</summary>
    internal const string UsedSeries = "connections.pool-used";

    /// <summary>The ceiling, by sweep.</summary>
    internal const string MaxSeries = "connections.pool-max";

    /// <inheritdoc />
    public override Task<PoolFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabasePoolSettings settings = DatabasePoolSettings.Of(context.Target)
            ?? throw new HealthCheckUnavailableException(
                "This database's data source is not one the studio can read pool settings from, so its pool is not judged.");

        if (!settings.Pooling)
        {
            return Task.FromResult(new PoolFacts(false, settings.MaxPoolSize, 0, 0, null, 0, context.Now));
        }

        SessionPoolObserver observer = context.Services.GetService<SessionPoolObserver>()
            ?? throw new HealthCheckUnavailableException("The studio's pool observer is not registered in this container.");

        using SessionPoolLease lease = observer.Lease();
        IReadOnlyList<NpgsqlPoolReading> pools = lease.Read();

        NpgsqlPoolReading? mine = pools.FirstOrDefault(x => DatabasePoolSettings.IsPoolOf(context.Target, x.PoolName))
            ?? throw new HealthCheckUnavailableException(
                "This database's Npgsql pool is not among the pools Npgsql reports in this process - most likely its data "
                + "source was given a name of its own (NpgsqlDataSourceBuilder.Name), which Npgsql reports instead of the "
                + "connection string the studio matches on. The pool is not judged.");

        if (mine.IsAmbiguous)
        {
            throw new HealthCheckUnavailableException(
                "More than one Npgsql pool in this process uses this database's connection string - the host's own "
                + "AddNpgsqlDataSource beside Marten's, or NpgsqlConnections opened with Marten's connection string - and Npgsql "
                + "reports each under that same name. Their numbers differ, so which pool is Marten's cannot be told, and the "
                + "pool is not judged; naming the host's data source (NpgsqlDataSourceBuilder.Name), or giving its connections an "
                + "Application Name of their own, tells them apart.");
        }

        return Task.FromResult(new PoolFacts(true, settings.MaxPoolSize, mine.Used, mine.Idle, mine.Max, pools.Count, context.Now));
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(PoolFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (!facts.Pooling || facts.Max <= 0)
        {
            yield break;
        }

        yield return new HealthSamplePart(UsedSeries, facts.Used);
        yield return new HealthSamplePart(MaxSeries, facts.Max);
        yield return new HealthSamplePart(SaturatedSeries, facts.Saturated ? 1 : 0);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PoolFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!facts.Pooling)
        {
            return HealthVerdict.Quiet(
                "This database's data source does not pool (Pooling=false): every connection is a new physical one, so there is no pool to use up.");
        }

        if (facts.Max <= 0)
        {
            return HealthVerdict.Quiet("Npgsql reports no ceiling for this database's pool.");
        }

        double share = (double) facts.Used / facts.Max;
        string usage = facts.Used.ToString(CultureInfo.InvariantCulture) + " of " + facts.Max.ToString(CultureInfo.InvariantCulture);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = evaluation.History.Trend(SaturatedSeries).Points;

        // The history holds this sweep's own sample when the sweep kept one: a regular sweep whose sample is far
        // enough from the last. When it did not - too close to the previous one, or a unit test with no history -
        // this sweep's reading counts on its own, after the samples before it.
        bool thisSweepRecorded = points.Count > 0 && points[^1].At == evaluation.Now;
        int saturatedInARow = 0;

        if (facts.Saturated)
        {
            for (int i = points.Count - 1 - (thisSweepRecorded ? 1 : 0); i >= 0 && points[i].Value >= 1; i--)
            {
                saturatedInARow++;
            }

            saturatedInARow++;
        }

        bool critical = facts.Saturated && saturatedInARow >= ConnectionThresholds.PoolSaturatedSamples;

        if (!critical && share <= ConnectionThresholds.PoolUsedWarning)
        {
            return HealthVerdict.Quiet(usage + " connections of this process's pool are in use.");
        }

        List<HealthEvidence> evidence =
        [
            new("In use", usage + " (" + SessionText.Percent(share) + ")"),
            new("Idle in the pool", facts.Idle.ToString(CultureInfo.InvariantCulture)),
            new("Max Pool Size", facts.Max.ToString(CultureInfo.InvariantCulture)),
        ];

        if (facts.Saturated)
        {
            evidence.Add(new HealthEvidence(
                "Fully used",
                saturatedInARow.ToString(CultureInfo.InvariantCulture) + " consecutive sample" + (saturatedInARow == 1 ? string.Empty : "s")));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C01",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "This process's connection pool to the database has been fully in use on consecutive checks"
                : "This process's connection pool to the database is nearly used up",
            WhyItMatters =
                "Every Marten session, query and daemon batch in this process borrows its connection from this pool. When all of "
                + "them are lent out, the next one waits inside Npgsql for up to its Timeout and then fails with \"The connection "
                + "pool has been exhausted\" - so the application stalls while Postgres itself looks idle. A pool that fills up is "
                + "more often connections held too long (idle in a transaction, C04; long statements, C05; sessions that are never "
                + "disposed) than a ceiling that is too low.",
            Evidence = evidence,
            Caveat =
                "Read in this process from Npgsql's own pool counters (db.client.connections.usage and db.client.connections.max) at "
                + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + ", including the studio's own connection while it "
                + "checks. Other processes on this database have pools of their own, which this does not see. Npgsql counts waiting "
                + "requests only per request, which the studio never listens to, so \"fully in use\" is read from the pool reaching its "
                + "ceiling on consecutive checks, one HealthInterval apart.",
            Remedy = new HealthRemedy(
                """
                // The ceiling is the connection string's "Maximum Pool Size" (Npgsql's default is 100).
                // For every data source Marten builds from a connection string:
                opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 200);
                """,
                "Raise it only when the server has the headroom (C03, and C02 for a store with a database per tenant), and after "
                + "ruling out sessions that hold their connection too long: dispose every IDocumentSession (await using), and keep "
                + "transactions short. A data source the host builds itself (UseNpgsqlDataSource) is configured where it is built."),
        });
    }
}
