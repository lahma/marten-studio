using Marten.Storage;

using Npgsql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T05 reads: the indexes, the tables' write counters, and how old the counters are.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Indexes">The indexes on the declared tables.</param>
/// <param name="Tables">The declared tables, for their write volume.</param>
/// <param name="Database">When the counters began.</param>
/// <param name="MultiHost">
/// Whether the database is reached through a multi-host data source, whose reads may be served by a standby -
/// where an index's scans are counted on the standby, not on the primary the advisor reads.
/// </param>
internal sealed record UnusedIndexFacts(
    DateTimeOffset ReadAt,
    IReadOnlyList<IndexFacts> Indexes,
    IReadOnlyList<TableFacts> Tables,
    DatabaseStatistics Database,
    bool MultiHost = false);

/// <summary>
/// T05: an index nothing has scanned since the counters began, on a table that is written to a lot.
/// </summary>
/// <remarks>
/// <para>
/// The Indexes tab's "never used" flag with the two facts that make it worth acting on: the table's write
/// volume, which is what the index costs, and the index's size. A unique or primary-key index is never
/// reported - it enforces something whether or not anything reads it - and neither is one Marten adds by
/// itself or the event store declares, which no line of the host's removes.
/// </para>
/// <para>
/// <b>How old the counters are is the whole caveat.</b> <c>pg_stat_reset()</c>, a crash and a restore all zero
/// <c>idx_scan</c>, and an index used by a monthly report looks unused for most of the month. A finding over
/// counters reset less than <see cref="MinCounterAge" /> ago is Info rather than a Warning, and says so.
/// </para>
/// <para>
/// <b>Two more things make "never scanned" unproven, and cap the finding at Info</b>: statistics that were never
/// reset (their age is unknown - they may be a minute old after a crash-free restore of a fresh cluster), and a
/// multi-host data source, whose reads may be served by a standby that counts its own scans. In either case the
/// advisor's own history is the proof it can offer: once its samples have seen the index unscanned for
/// <see cref="MinCounterAge" />, the finding is a Warning.
/// </para>
/// </remarks>
[HealthCheck("T05", HealthArea.Tables, HealthCostTier.Catalog, "Unused index on a write-heavy table")]
internal sealed class UnusedIndexCheck : HealthCheck<UnusedIndexFacts>
{
    /// <summary>Inserts, updates and deletes on the table since the counters began, before an unused index costs enough.</summary>
    public const long MinWrites = 100_000;

    /// <summary>The smallest index worth a finding.</summary>
    public const long MinIndexBytes = 1024 * 1024;

    /// <summary>How long "never scanned" must have been seen before it is a Warning rather than Info.</summary>
    public static readonly TimeSpan MinCounterAge = TimeSpan.FromDays(1);

    /// <summary>The series T05 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.unused-index.swept";

    /// <summary>The series of an index seen with no scans, written as 1.</summary>
    internal const string UnscannedSeries = "tables.unused-index:";

    /// <inheritdoc />
    public override async Task<UnusedIndexFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<IndexFacts> indexes = await MartenTables.IndexesAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await MartenTables.TablesAsync(context).ConfigureAwait(false);

        return new UnusedIndexFacts(context.Now, indexes, tables, DatabaseStatistics.Of(tables), IsMultiHost(context));
    }

    /// <summary>
    /// Whether the database is reached through more than one host - read off the settings of a connection the data
    /// source hands out, which is never opened.
    /// </summary>
    internal static bool IsMultiHost(HealthCheckContext context)
    {
        try
        {
            using NpgsqlConnection unopened = context.Database.CreateConnection(ConnectionUsage.ReadWrite);
            return IsMultiHost(unopened.ConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Unknown is treated as the cautious answer: the finding stays Info until the history proves it.
            return true;
        }
    }

    /// <summary>Whether a connection string names more than one host.</summary>
    internal static bool IsMultiHost(string? connectionString) =>
        !string.IsNullOrEmpty(connectionString)
        && new NpgsqlConnectionStringBuilder(connectionString).Host is { } host
        && host.Contains(',', StringComparison.Ordinal);

    /// <summary>Whether an index is a candidate at all: valid, enforcing nothing, removable, and never scanned.</summary>
    private static bool IsUnscanned(IndexFacts index) =>
        index.Row is { IsValid: true, IsUnique: false, IsPrimaryKey: false, IsExclusion: false, Scans: 0 }
        && index.Row.Bytes >= MinIndexBytes
        && index.Removable;

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(UnusedIndexFacts facts)
    {
        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (IndexFacts index in facts.Indexes)
        {
            if (IsUnscanned(index))
            {
                yield return new HealthSamplePart(UnscannedSeries + index.Key, 1);
            }
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(UnusedIndexFacts facts, HealthEvaluation evaluation)
    {
        Dictionary<string, TableFacts> tables = facts.Tables.ToDictionary(static x => x.Key, StringComparer.OrdinalIgnoreCase);
        List<HealthFinding> findings = [];

        foreach (IndexFacts index in facts.Indexes)
        {
            if (!IsUnscanned(index) || !tables.TryGetValue(index.Table.Key, out TableFacts? table))
            {
                continue;
            }

            long writes = (table.Row.Inserts ?? 0) + (table.Row.Updates ?? 0) + (table.Row.Deletes ?? 0);
            if (writes < MinWrites)
            {
                continue;
            }

            findings.Add(Finding(index, table, writes, facts, evaluation));
        }

        return findings.Count == 0 ? HealthVerdict.Quiet() : new HealthVerdict(findings);
    }

    /// <summary>
    /// Why "never scanned" is not yet proven, or <see langword="null" /> when it is: the counters' own age, or
    /// the advisor's history of the index.
    /// </summary>
    internal static string? Unproven(IndexFacts index, UnusedIndexFacts facts, HealthEvaluation evaluation)
    {
        DateTimeOffset? seenSince = TableHistory.HeldSince(evaluation.History, SweptSeries, UnscannedSeries + index.Key);
        if (seenSince is { } since && facts.ReadAt - since >= MinCounterAge)
        {
            return null;
        }

        if (facts.MultiHost)
        {
            return "The database is reached through a multi-host data source, so reads may be served by a standby, which counts "
                + "its own index scans: this primary's zero proves nothing about them.";
        }

        return facts.Database.StatsReset switch
        {
            null => "The statistics were never reset, so how long they have been counting is not known.",
            { } reset when facts.ReadAt - reset < MinCounterAge => "The statistics were reset " + TableText.Ago(reset, facts.ReadAt) + ".",
            _ => null,
        };
    }

    private static HealthFinding Finding(IndexFacts index, TableFacts table, long writes, UnusedIndexFacts facts, HealthEvaluation evaluation)
    {
        long hot = table.Row.HotUpdates ?? 0;
        long maintained = (table.Row.Inserts ?? 0) + Math.Max(0, (table.Row.Updates ?? 0) - hot);
        string? unproven = Unproven(index, facts, evaluation);

        return new HealthFinding
        {
            Code = "T05",
            Severity = unproven is null ? HealthSeverity.Warning : HealthSeverity.Info,
            Subject = index.Key,
            DocumentTypes = index.Table.DocumentTypes,
            Title = "Index " + index.Row.Name + " on " + index.Table.Key + " has never been scanned, while the table took "
                + TableText.Count(writes) + " writes",
            WhyItMatters =
                "Every insert, and every update that is not a HOT update, writes an entry into every index on the table - about "
                + TableText.Count(maintained) + " entries into this one since the counters began - and the index holds "
                + TableText.Bytes(index.Row.Bytes) + " of disk and buffer cache. No query has used it in that time."
                + (index.Origin == IndexOrigin.Undeclared
                    ? " Nothing declares it, so the next apply drops it anyway (AutoCreate.CreateOrUpdate is not additive)."
                    : string.Empty),
            Evidence =
            [
                new HealthEvidence("Index", index.Row.Name),
                new HealthEvidence("Table", index.Table.Describe()),
                new HealthEvidence("Size", TableText.Bytes(index.Row.Bytes)),
                new HealthEvidence("Index scans", "0"),
                new HealthEvidence("Writes to the table", TableText.Count(writes)
                    + " (" + TableText.Count(table.Row.Inserts ?? 0) + " inserts, " + TableText.Count(table.Row.Updates ?? 0)
                    + " updates, " + TableText.Count(table.Row.Deletes ?? 0) + " deletes)"),
                new HealthEvidence("Counters since", TableText.When(facts.Database.StatsReset)),
            ],
            Caveat =
                TableText.CountersSince(facts.Database.StatsReset, evaluation.Now)
                + (unproven is null
                    ? string.Empty
                    : " " + unproven + " So this is Info until the advisor's own samples have seen the index unscanned for a day.")
                + " An index used by a monthly job, a failover's replica or a query that has not run yet looks unused too: "
                + "check before removing it.",
            Remedy = index.Origin switch
            {
                IndexOrigin.Declared when index.Table.TypeName is { } type => new HealthRemedy(
                    "// opts.Schema.For<" + type + ">() declares " + index.Row.Name + ", and nothing has scanned it:\n"
                    + "// remove the .Index(...), .Duplicate(...) or GIN/full-text index that declares it from the registration.",
                    "The next apply drops it. Removing a Duplicate(...) removes its column along with its index."),
                IndexOrigin.Managed => new HealthRemedy(
                    "// Remove " + index.Row.Name + " from the Weasel table's Indexes where the Marten-managed table is defined.",
                    "The next apply then drops it."),
                IndexOrigin.Ignored when index.Table.TypeName is { } type => new HealthRemedy(
                    "// Stop ignoring it, and the next apply drops it:\n"
                    + "// remove opts.Schema.For<" + type + ">().IgnoreIndex(\"" + index.Row.Name + "\") from the registration.",
                    "AutoCreate.CreateOrUpdate drops every index on a Marten table that the configuration does not declare."),
                _ => null,
            },
        };
    }
}
