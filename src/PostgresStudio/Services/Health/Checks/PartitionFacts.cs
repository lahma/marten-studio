using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// The partition facts T19 and T20 judge: every partitioned table of the judged set, read once per sweep
/// (<see cref="PartitionQueries.PartitionsSql" />), and the set they come from.
/// </summary>
/// <param name="ReadAt">When they were read, by the studio's clock.</param>
/// <param name="Tables">The partitioned tables, with their partitioning facts. Never a partition's name.</param>
/// <param name="Judged">Every table of the judged set, to describe a partitioned one.</param>
internal sealed record PartitionFacts(DateTimeOffset ReadAt, IReadOnlyList<HealthPartitionedTableRow> Tables, IReadOnlyList<StudioTable> Judged)
{
    /// <summary>Nothing to read about.</summary>
    public static PartitionFacts None(DateTimeOffset readAt) => new(readAt, [], []);

    /// <summary>The judged table behind <paramref name="row" />, or <see langword="null" />.</summary>
    public StudioTable? TableOf(HealthPartitionedTableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Judged.FirstOrDefault(x => string.Equals(x.Key, row.Key, StringComparison.Ordinal));
    }
}

/// <summary>The partition a range-partitioned table needs next (T20's remedy).</summary>
/// <param name="KeyType">The partition key's type, as <c>format_type</c> spells it.</param>
/// <param name="From">Its lower bound: Unix seconds for a time key, the number for an integer one.</param>
/// <param name="To">Its upper bound, likewise.</param>
/// <param name="Suffix">What the studio would name it after its table: <c>p20261201</c>.</param>
/// <param name="HasDefault">Whether the table has a <c>DEFAULT</c> partition, which creating the new one scans.</param>
internal sealed record RangePartitionNext(string KeyType, double From, double To, string Suffix, bool HasDefault);

/// <summary>The partition facts' one read per sweep, whichever of T19 and T20 asks first (W5-B1, from W5-P1's step 1).</summary>
/// <remarks>
/// Gate-free, and about the database: the Partitions page that would cache it beside the sweep's other facts is W5-P1's,
/// in 0.6; here it is the sweep's shared read alone, so it costs a database one statement per sweep, and none when the
/// judged set is empty.
/// </remarks>
internal static class HealthPartitions
{
    /// <summary>The memo key of the read.</summary>
    internal const string Key = "tables.partitions";

    /// <summary>The partition facts, read once per sweep.</summary>
    public static Task<PartitionFacts> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    private static async Task<PartitionFacts> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);

        if (set.Tables.Count == 0)
        {
            // Nothing to ask about, so no connection is opened at all.
            return PartitionFacts.None(context.Now);
        }

        IReadOnlyList<HealthPartitionedTableRow> rows = await context.Sql
            .QueryAsync(PartitionQueries.Partitions(HealthTables.Targets(set)), PartitionQueries.ReadPartitionedTable, cancellationToken)
            .ConfigureAwait(false);

        return new PartitionFacts(context.Now, rows, set.Tables);
    }
}
