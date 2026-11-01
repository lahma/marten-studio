using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What one query that is not pruned locks of a partitioned table, against the lock table (T19).</summary>
/// <param name="Locks">
/// <c>1 + intermediate partitioned tables + Σ leaf (1 + its indexes)</c>, plus the partitioned indexes from 16 - B0 §8a's
/// count, 601 (603 from 16) for 200 partitions of two indexes each.
/// </param>
/// <param name="PerTransaction"><c>max_locks_per_transaction</c>.</param>
/// <param name="Capacity">
/// The documented capacity, <c>max_locks_per_transaction × (max_connections + max_prepared_transactions)</c>.
/// </param>
/// <param name="FastPathSlots">The fast-path slots a backend has: 16 up to 17, sized from <c>max_locks_per_transaction</c> from 18.</param>
internal sealed record PartitionLockBudget(long Locks, int PerTransaction, long Capacity, int FastPathSlots);

/// <summary>What T19 reads: the sweep's partition facts, and who runs the server - for the remedy's words.</summary>
/// <param name="Partitions">The partition facts (<see cref="HealthPartitions.SharedAsync" />).</param>
/// <param name="Platform">Who runs the server.</param>
internal sealed record PartitionLockFacts(PartitionFacts Partitions, PostgresPlatform Platform);

/// <summary>
/// T19: a partitioned table one unpruned query - or a generic plan of a pruned one - takes more locks on than the lock table
/// is sized for (W5-B1, moved from W5-P1; B0 §8's corrected formula).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> Not the partitions: every partition and every index on it, the table itself, every intermediate
/// partitioned table and - from PostgreSQL 16 - every partitioned index (B0 §8a). A generic plan of a tenant-scoped prepared
/// statement locks all of them too, on every release measured: only a custom plan prunes at plan time.
/// </para>
/// <para>
/// <b>Two lines.</b> <b>Info</b> (variant <c>per-transaction</c>) when the count passes <c>max_locks_per_transaction</c>: one
/// such query uses more than the per-transaction average the setting sizes the table by. <b>Warning</b> (variant
/// <c>capacity</c>) when it passes half the documented capacity, <c>max_locks_per_transaction × (max_connections +
/// max_prepared_transactions)</c>: two such queries at once, or one beside ordinary traffic, can fill the shared lock table
/// and fail with "out of shared memory". Up to 18 that capacity is a floor - one backend measured about twice it, because
/// the table borrows spare shared memory (B0 §8b); from 19 the lock table keeps no such margin and the default is 128
/// (<see cref="PostgresFeature.MaxLocksDefault128" />), so the documented capacity is close to the real one.
/// </para>
/// </remarks>
[HealthCheck("T19", HealthArea.Tables, HealthCostTier.Catalog, "Partitions against the lock table", Variants = [PerTransactionVariant, CapacityVariant], Gate = HealthGate.Database)]
internal sealed class PartitionLockBudgetCheck : HealthCheck<PartitionLockFacts>
{
    /// <summary>The variant past <c>max_locks_per_transaction</c>.</summary>
    public const string PerTransactionVariant = "per-transaction";

    /// <summary>The variant past half the documented capacity.</summary>
    public const string CapacityVariant = "capacity";

    /// <summary>The share of the documented capacity one query may take before it is a Warning.</summary>
    public const double CapacityShare = 0.5;

    /// <inheritdoc />
    public override async Task<PartitionLockFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        PartitionFacts partitions = await HealthPartitions.SharedAsync(context).ConfigureAwait(false);

        // The platform only where there is a table to word a remedy for: a sweep with none reads nothing for it.
        PostgresPlatform platform = partitions.Tables.Count == 0
            ? PostgresPlatform.SelfHosted
            : (await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false)).Platform;

        return new PartitionLockFacts(partitions, platform);
    }

    /// <summary>The lock count and the budget for one partitioned table.</summary>
    internal static PartitionLockBudget Budget(HealthPartitionedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        long locks = 1 + row.Intermediates + row.Leaves + row.LeafIndexes
            + (PostgresFeatures.Has(PostgresFeature.PartitionedIndexLocks, row.ServerVersion) ? row.PartitionedIndexes : 0);

        long capacity = (long) row.MaxLocksPerTransaction * (row.MaxConnections + row.MaxPreparedTransactions);

        // 18 sizes a backend's fast-path groups as pg_nextpower2_32(max_locks_per_transaction) / 16, between 1 and 1024 groups of
        // 16 slots (InitializeFastPathLocks): the smallest power of two at or above the setting, at least 16 - measured on 18.6,
        // 128 slots at max_locks_per_transaction = 100 (W5-B1 review F7).
        int fastPath = PostgresFeatures.Has(PostgresFeature.FastPathSlotsFromMaxLocks, row.ServerVersion)
            ? FastPathSlots(row.MaxLocksPerTransaction)
            : 16;

        return new PartitionLockBudget(locks, row.MaxLocksPerTransaction, capacity, fastPath);
    }

    /// <summary>A backend's fast-path lock slots from PostgreSQL 18: the smallest power of two at or above the setting, 16 to 16,384.</summary>
    internal static int FastPathSlots(int maxLocksPerTransaction)
    {
        int slots = 16;

        while (slots < maxLocksPerTransaction && slots < 1024 * 16)
        {
            slots *= 2;
        }

        return slots;
    }

    /// <summary>
    /// The <c>max_locks_per_transaction</c> a remedy suggests: the smallest power of two that holds one such query within the
    /// per-transaction average, or within half the capacity - whichever the variant is about - and never less than double
    /// the setting in force.
    /// </summary>
    internal static int Suggested(PartitionLockBudget budget, HealthPartitionedTableRow row, bool capacity)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(row);

        long slots = Math.Max(1, row.MaxConnections + row.MaxPreparedTransactions);
        long needed = capacity
            ? (long) Math.Ceiling(budget.Locks / CapacityShare / slots)
            : budget.Locks;

        long value = Math.Max(needed, (long) budget.PerTransaction * 2);
        long power = 64;
        while (power < value && power < int.MaxValue / 2)
        {
            power *= 2;
        }

        return (int) power;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PartitionLockFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];

        foreach (HealthPartitionedTableRow row in facts.Partitions.Tables)
        {
            if (facts.Partitions.TableOf(row) is not { } table)
            {
                continue;
            }

            PartitionLockBudget budget = Budget(row);

            if (budget.Capacity > 0 && budget.Locks > CapacityShare * budget.Capacity)
            {
                findings.Add(Finding(table, row, budget, capacity: true, facts.Platform, evaluation));
            }
            else if (budget.Locks > budget.PerTransaction)
            {
                findings.Add(Finding(table, row, budget, capacity: false, facts.Platform, evaluation));
            }
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(facts.Partitions.Tables.Count == 0 ? "No table the studio judges is partitioned." : null);
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static HealthFinding Finding(
        StudioTable table,
        HealthPartitionedTableRow row,
        PartitionLockBudget budget,
        bool capacity,
        PostgresPlatform platform,
        HealthEvaluation evaluation)
    {
        bool nineteen = PostgresFeatures.Has(PostgresFeature.MaxLocksDefault128, row.ServerVersion);
        bool partitionedIndexes = PostgresFeatures.Has(PostgresFeature.PartitionedIndexLocks, row.ServerVersion);
        int suggested = Suggested(budget, row, capacity);

        string formula = "1 (the table) + " + Number(row.Intermediates) + " (partitioned partitions) + " + Number(row.Leaves)
            + " (partitions) + " + Number(row.LeafIndexes) + " (their indexes)"
            + (partitionedIndexes ? " + " + Number(row.PartitionedIndexes) + " (partitioned indexes, from PostgreSQL 16)" : string.Empty)
            + " = " + Number(budget.Locks);

        return new HealthFinding
        {
            Code = "T19",
            Severity = capacity ? HealthSeverity.Warning : HealthSeverity.Info,
            Variant = capacity ? CapacityVariant : PerTransactionVariant,
            Subject = table.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = "One unpruned query on " + table.Key + " takes " + Number(budget.Locks) + " locks, "
                + (capacity
                    ? "over half the lock table's documented capacity of " + Number(budget.Capacity)
                    : "more than max_locks_per_transaction (" + Number(budget.PerTransaction) + ")"),
            WhyItMatters =
                "A query that reads the table without pruning - and a generic plan of one that filters on the partition key, "
                + "which still locks every partition (measured) - holds a lock on every partition and every index on them until "
                + "its transaction ends. The locks share one table, sized for max_locks_per_transaction on average per connection; "
                + (capacity
                    ? "two such queries at once, or one beside ordinary traffic, can fill it, and every session then fails with "
                      + "\"out of shared memory\" until one ends."
                    : "one such query takes more than its share, which is fine alone and is what fills the table when several run "
                      + "at once.")
                + " Fast-path slots, " + Number(budget.FastPathSlots) + " a backend here, hold only the first few of them.",
            Evidence =
            [
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Locks one unpruned query takes", formula),
                new HealthEvidence("max_locks_per_transaction", Number(budget.PerTransaction)),
                new HealthEvidence(
                    "Documented capacity",
                    Number(budget.Capacity) + " = max_locks_per_transaction × (max_connections " + Number(row.MaxConnections)
                    + " + max_prepared_transactions " + Number(row.MaxPreparedTransactions) + ")"
                    + (nineteen
                        ? "; from PostgreSQL 19 the lock table keeps no margin past it"
                        : "; a floor: one backend measured about twice it")),
                new HealthEvidence("Fast-path slots a backend", Number(budget.FastPathSlots)),
            ],
            Caveat =
                "A generic plan of a query that filters on the partition key still locks every partition (measured, B0 §8a); only a "
                + "custom plan prunes at plan time. Counted from the catalog, never by locking anything.",
            Remedy = ServerSettingRemedy.Build(
                platform,
                "max_locks_per_transaction",
                suggested.ToString(CultureInfo.InvariantCulture),
                evaluation.Text,
                row.MaxLocksContext,
                ":",
                where => (capacity
                        ? "Room for one such query in half the lock table, at max_connections " + Number(row.MaxConnections) + ". "
                        : "Room for one such query within the per-transaction average. ")
                    + "It sizes shared memory at the server's start."
                    + (nineteen
                        ? " On PostgreSQL 19 a value carried over from an older release holds about half the locks it did."
                        : string.Empty)
                    + " Set it in " + where + "; a standby's must be at least the primary's."),
        };
    }
}
