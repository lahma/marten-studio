using System.Globalization;

using JasperFx;
using JasperFx.Events.Daemon;

using MartenStudio.Services.Database;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What C09 reads.</summary>
/// <param name="Hosted">Whether the async daemon for this database is hosted in this process.</param>
/// <param name="Explanation">Why not, when it is not - the projections screen's own sentence.</param>
/// <param name="RunningShards">How many of its shards are running.</param>
/// <param name="AsyncShards">How many async shards the store registers, or <see langword="null" /> when they cannot be listed.</param>
/// <param name="Pooling">Whether the database's data source pools.</param>
/// <param name="MaxPoolSize">Its <c>Max Pool Size</c>.</param>
internal sealed record DaemonPoolFacts(
    bool Hosted,
    string? Explanation,
    int RunningShards,
    int? AsyncShards,
    bool Pooling,
    int MaxPoolSize);

/// <summary>
/// C09: the async daemon hosted here runs enough shards on this database to use up its connection pool on its
/// own.
/// </summary>
/// <remarks>
/// <para>
/// Each running shard loads events and commits its batches through the database's data source - the same pool
/// the application's requests borrow from - and the daemon's own high-water detection and, under HotCold, its
/// leadership lock take connections from it too (<c>ProjectionCoordinator</c> builds its <c>AdvisoryLock</c> on
/// <c>MartenDatabase.DataSource</c>, read at Marten V9.39.0). Running shards plus
/// <see cref="ConnectionThresholds.DaemonOwnConnections" /> at or above <c>Max Pool Size</c> means a catch-up or a
/// rebuild can take every connection the application has.
/// </para>
/// <para>
/// Per pool, not per store: each of a store's databases has a pool of its own, and the daemon for a database runs
/// that database's shards in it. A store with a database per tenant multiplies the shards across pools - which is
/// C02's budget - and not within one.
/// </para>
/// <para>
/// Tier 0, read in process through <see cref="DaemonAccessor" />, which never starts a daemon (hard rule 11). No
/// daemon hosted here, or one an external system manages, is a value: the check says so and finds nothing.
/// </para>
/// </remarks>
[HealthCheck("C09", HealthArea.Connections, HealthCostTier.InProcess, "Async shards against the pool")]
internal sealed class DaemonPoolCheck : HealthCheck<DaemonPoolFacts>
{
    /// <inheritdoc />
    public override async Task<DaemonPoolFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabasePoolSettings settings = DatabasePoolSettings.Of(context.Database)
            ?? throw new HealthCheckUnavailableException(
                "This database's data source is not one the studio can read pool settings from, so the daemon is not weighed against it.");

        DaemonAccessor accessor = context.Services.GetService<DaemonAccessor>()
            ?? throw new HealthCheckUnavailableException("The studio's daemon accessor is not registered in this container.");

        DaemonHosting hosting = await SweptDaemon.HostingAsync(context, accessor).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new DaemonPoolFacts(false, hosting.Explanation, 0, null, settings.Pooling, settings.MaxPoolSize);
        }

        int running = 0;

        foreach (ISubscriptionAgent agent in daemon.CurrentAgents())
        {
            if (agent.Status == AgentStatus.Running)
            {
                running++;
            }
        }

        int? configured = null;

        try
        {
            configured = context.Store.Options is Marten.StoreOptions options ? options.Projections.AllShards().Count : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Configuration that cannot list its shards: the running count is the fact that matters.
        }

        return new DaemonPoolFacts(true, null, running, configured, settings.Pooling, settings.MaxPoolSize);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DaemonPoolFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (!facts.Hosted)
        {
            return HealthVerdict.Quiet(
                "The async daemon is not hosted in this process for this database, so none of its shards draw on this process's pool. "
                + facts.Explanation);
        }

        if (!facts.Pooling)
        {
            return HealthVerdict.Quiet("This database's data source does not pool, so there is no pool for the daemon to use up.");
        }

        int needed = facts.RunningShards + ConnectionThresholds.DaemonOwnConnections;

        if (needed < facts.MaxPoolSize)
        {
            return HealthVerdict.Quiet(
                SessionText.Count(facts.RunningShards, "shard") + " running, plus the daemon's own connection, against a pool of "
                + facts.MaxPoolSize.ToString(CultureInfo.InvariantCulture) + ".");
        }

        List<HealthEvidence> evidence =
        [
            new("Running shards", facts.RunningShards.ToString(CultureInfo.InvariantCulture)),
            new("The daemon's own connections", ConnectionThresholds.DaemonOwnConnections.ToString(CultureInfo.InvariantCulture)),
            new("Max Pool Size", facts.MaxPoolSize.ToString(CultureInfo.InvariantCulture)),
        ];

        if (facts.AsyncShards is { } shards)
        {
            evidence.Add(new HealthEvidence("Async shards registered", shards.ToString(CultureInfo.InvariantCulture)));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C09",
            Severity = HealthSeverity.Warning,
            Title = "The async daemon alone can use up this database's connection pool",
            WhyItMatters =
                "Each running shard loads events and commits its batches through the same pool the application's own sessions borrow "
                + "from, and the daemon keeps a connection of its own beside them. With " + SessionText.Count(facts.RunningShards, "shard")
                + " running against a pool of " + facts.MaxPoolSize.ToString(CultureInfo.InvariantCulture) + ", a catch-up after a "
                + "deployment or a rebuild can lend out every connection at once, and the application's requests then wait for the "
                + "daemon - or fail with \"The connection pool has been exhausted\".",
            Evidence = evidence,
            Caveat =
                "Counts the shards the daemon hosted in this process runs on this database now. A daemon on another node uses that "
                + "process's pool, which this does not see; a shard that is idle holds no connection, so this is the worst case, "
                + "which is what a catch-up is.",
            Remedy = new HealthRemedy(
                """
                // A pool with room for the daemon beside the application, for every data source Marten builds:
                opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 100);
                """,
                "Or run the daemon on nodes of its own (AddAsyncDaemon only there, DaemonMode.HotCold), so that the application's "
                + "pool and the daemon's are different pools."),
        });
    }
}
