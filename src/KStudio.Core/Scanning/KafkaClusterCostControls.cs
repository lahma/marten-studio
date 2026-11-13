using System.Collections.Concurrent;

using KStudio.Core.Clusters;

namespace KStudio.Core.Scanning;

/// <summary>The byte-rate bucket and the hourly allowance of one profiled cluster, either of which may be absent.</summary>
/// <param name="Governor">The cluster's own byte rate, when its profile lowers the host's; otherwise <see langword="null" />.</param>
/// <param name="OwnerBudget">The cluster's own hourly allowance per owner, when its profile lowers the host's; otherwise <see langword="null" />.</param>
internal sealed record KafkaClusterCost(KafkaByteRateGovernor? Governor, OwnerByteBudget? OwnerBudget)
{
    /// <summary>A cluster whose profile lowers neither: only the process-wide controls apply.</summary>
    public static KafkaClusterCost None { get; } = new(null, null);

    /// <summary>The smaller of the allowances the owner has left on this cluster and in the process.</summary>
    /// <param name="owner">The owner's key.</param>
    /// <param name="process">The process-wide budget.</param>
    public long Remaining(string owner, OwnerByteBudget process) =>
        OwnerBudget is { } own ? Math.Min(own.Remaining(owner), process.Remaining(owner)) : process.Remaining(owner);

    /// <summary>
    /// The allowance that has nothing left, or <see langword="null" /> when both have some: the process's when it is spent (it stops every cluster), otherwise
    /// the cluster's own. A refusal or stop names this one, because the plan's capped value is the lower of the two whichever ran out.
    /// </summary>
    /// <param name="owner">The owner's key.</param>
    /// <param name="process">The process-wide budget.</param>
    public KafkaSpentBudget? Spent(string owner, OwnerByteBudget process) =>
        process.Remaining(owner) <= 0 ? new KafkaSpentBudget(KafkaBudgetScope.Process, process.BytesPerHour)
        : OwnerBudget is { } own && own.Remaining(owner) <= 0 ? new KafkaSpentBudget(KafkaBudgetScope.Cluster, own.BytesPerHour)
        : null;

    /// <summary>The hourly allowance that is lower: this cluster's own, or the process's.</summary>
    /// <param name="process">The process-wide budget.</param>
    public long BytesPerHour(OwnerByteBudget process) =>
        OwnerBudget is { } own ? Math.Min(own.BytesPerHour, process.BytesPerHour) : process.BytesPerHour;
}

/// <summary>
/// What a profile adds to the process-wide byte rate and hourly allowance (AGENTS.md D47, D-M5 decision 4.5): for each cluster whose capped
/// <c>BytesPerSecond</c> is below the host's, a governor of its own; for each whose capped <c>OwnerBytesPerHour</c> is below the host's, a budget of
/// its own. They are keyed by the cluster's entry key (<c>{cluster}/{tenant}</c> is a cluster of its own, with a bucket of its own), made on first
/// use and kept, because a rolling hour and a bucket's debt must survive the scan that started them. A cluster with no profile, or one whose
/// profile lowers neither, has none, and nothing is allocated for it.
/// </summary>
internal sealed class KafkaClusterCostControls(KafkaEngineSnapshot settings, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> controls = new(StringComparer.Ordinal);

    /// <summary>How many clusters hold controls of their own: for tests.</summary>
    public int Count => controls.Count;

    /// <summary>The controls of a cluster.</summary>
    /// <param name="cluster">The cluster.</param>
    public KafkaClusterCost For(KafkaClusterDefinition cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (cluster.Profile is not { } profile)
        {
            return KafkaClusterCost.None;
        }

        KafkaScanLimits host = settings.Limits;
        KafkaScanLimits capped = KafkaProfileCeilings.Cap(profile, host);
        long? rate = capped.BytesPerSecond < host.BytesPerSecond ? capped.BytesPerSecond : null;
        long? budget = capped.OwnerBytesPerHour < host.OwnerBytesPerHour ? capped.OwnerBytesPerHour : null;

        if (rate is null && budget is null)
        {
            return KafkaClusterCost.None;
        }

        Entry entry = controls.AddOrUpdate(
            cluster.Key,
            _ => Make(rate, budget),
            (_, existing) => existing.Rate == rate && existing.Budget == budget ? existing : Make(rate, budget));
        return entry.Cost;
    }

    private Entry Make(long? rate, long? budget) => new(
        rate,
        budget,
        new KafkaClusterCost(
            rate is { } r ? new KafkaByteRateGovernor(r, time) : null,
            budget is { } b ? new OwnerByteBudget(b, time) : null));

    private sealed record Entry(long? Rate, long? Budget, KafkaClusterCost Cost);
}
