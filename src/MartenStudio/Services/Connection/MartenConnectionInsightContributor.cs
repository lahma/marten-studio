using JasperFx;
using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Connection;

/// <summary>
/// MartenStudio's <see cref="IConnectionInsightContributor" /> (1.1-NP4): the section "How Marten uses this connection", the
/// sentences it adds to the page's own hints, and the average size the <c>BUFFER</c> hint judges.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is read from the database and nothing is touched that ensures storage</b> (hard rule 14), and no session is opened
/// (hard rule 15): the options and mappings are in memory, <c>IMartenDatabase.ConfiguredCommandTimeout</c> parses the data source's
/// own connection string inside Marten and returns a number, the daemon is what the regular sweep already looked up, and the average
/// sizes are the two numbers the deeper checks recorded (<see cref="MartenRowSizeCache" />).
/// </para>
/// <para>
/// <b>It runs after the Database gate.</b> The page asked the source policy about this store and database before any contributor is
/// called; the figures it shows are database-wide, never a tenant's or a type's.
/// </para>
/// <para>
/// <b>No connection string, and no data source built.</b> The contributor is handed the redacted settings only; whether Marten built
/// its data source is Weasel's <c>OwnsDataSource</c>, and the container is read as descriptors, never resolved (a registration nothing
/// had asked for would build a pool of the studio's own).
/// </para>
/// </remarks>
internal sealed class MartenConnectionInsightContributor : IConnectionInsightContributor
{
    private readonly MartenDataSourceRegistry dataSources;
    private readonly HealthSweepCache sweeps;
    private readonly MartenRowSizeCache rowSizes;
    private readonly ILogger<MartenConnectionInsightContributor> logger;

    public MartenConnectionInsightContributor(
        MartenDataSourceRegistry dataSources,
        HealthSweepCache sweeps,
        MartenRowSizeCache rowSizes,
        ILogger<MartenConnectionInsightContributor> logger)
    {
        this.dataSources = dataSources;
        this.sweeps = sweeps;
        this.rowSizes = rowSizes;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task<ConnectionContribution?> ContributeAsync(ConnectionContributorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only a Marten store's source: the shared page runs every contributor against every source.
        if (context.Source is not ResolvedScope scope || scope.Store.Options is not StoreOptions storeOptions)
        {
            return Task.FromResult<ConnectionContribution?>(null);
        }

        MartenConnectionOptions options = MartenConnectionOptions.Read(storeOptions, scope.Database);

        // Whether Marten built the data source is a public fact of Weasel's database; the container is only read as descriptors.
        bool? ownedByMarten = scope.Database is Weasel.Postgresql.PostgresqlDatabase postgres ? postgres.OwnsDataSource : null;
        MartenDataSourceOrigin origin = MartenDataSourceOrigin.Of(scope.Target.DataSource, ownedByMarten, dataSources.Read());

        string storeKey = scope.Source.Key;
        string databaseId = scope.Target.Identity;

        MartenDaemonNeeds? daemon = DaemonNeeds(sweeps.Latest(storeKey, databaseId), storeOptions);
        int? poolMax = context.Settings is { Readable: true, Values.Pooling: true } ? context.Settings.Values.MaxPoolSize : null;
        long? average = rowSizes.Average(storeKey, databaseId);

        if (logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace(
                "Marten Studio's connection section for {StoreKey}/{DatabaseId}: origin {Origin}, daemon {Daemon}, average size {Average}",
                storeKey,
                databaseId,
                origin.Kind,
                daemon is null ? "unknown" : daemon.Hosted ? "hosted" : "not hosted",
                average);
        }

        return Task.FromResult<ConnectionContribution?>(new ConnectionContribution
        {
            Sections = [MartenConnectionSection.Build(options, origin, daemon, poolMax)],
            AverageRowBytes = average,
            HintNotes = MartenConnectionSection.HintNotes(options, origin, context.Observed),
        });
    }

    /// <summary>The daemon's connection needs from the regular sweep's own lookup, or <see langword="null" /> when it has made none.</summary>
    private static MartenDaemonNeeds? DaemonNeeds(HealthSweep? sweep, StoreOptions storeOptions)
    {
        if (sweep is null || !sweep.TryShared<DaemonHosting>(SweptDaemon.Key, out DaemonHosting? hosting))
        {
            return null;
        }

        if (!hosting.TryGetDaemon(out IProjectionDaemon daemon))
        {
            return new MartenDaemonNeeds(false, hosting.Explanation, 0, null);
        }

        int running = 0;

        foreach (ISubscriptionAgent agent in daemon.CurrentAgents())
        {
            if (agent.Status == AgentStatus.Running)
            {
                running++;
            }
        }

        int? registered = null;

        try
        {
            registered = storeOptions.Projections.AllShards().Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Configuration that cannot list its shards: the running count is the fact that matters.
        }

        return new MartenDaemonNeeds(true, null, running, registered);
    }
}
