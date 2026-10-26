using PostgresStudio.Services.Sources;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

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
    bool MultiHost = false)
{
    /// <summary>The tables locked for a schema change, which are not judged this sweep (<see cref="HealthIndexRead.LockedTables" />).</summary>
    public IReadOnlySet<string> LockedTables { get; init; } = HealthIndexRead.Empty.LockedTables;

    /// <summary>
    /// The locked tables' indexes, read undeparsed (<see cref="HealthIndexRead.LockedIndexes" />): sampled, so their
    /// "unscanned since" clock runs through the lock, and never judged.
    /// </summary>
    public IReadOnlyList<IndexFacts> LockedIndexes { get; init; } = [];
}

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
/// <b>Statistics that were never reset are unproven too</b> - their age is unknown - and for those, and for a
/// recent reset, the advisor's own history is the proof it can offer: once its samples have seen the index
/// unscanned for <see cref="MinCounterAge" />, the finding is a Warning.
/// </para>
/// <para>
/// <b>A multi-host data source is Info always.</b> Its reads may be served by a standby, which counts its own
/// index scans; the history is samples of this primary's counters, so no length of it proves anything about
/// the standby's, and the finding says to check every standby before removing the index.
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
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);

        return new UnusedIndexFacts(context.Now, read.Indexes, tables, DatabaseStatistics.Of(tables), IsMultiHost(context))
        {
            LockedTables = read.LockedTables,
            LockedIndexes = read.LockedIndexes,
        };
    }

    /// <summary>
    /// Whether the database is reached through more than one host - read off the settings of a connection the data
    /// source hands out, which is never opened.
    /// </summary>
    internal static bool IsMultiHost(HealthCheckContext context)
    {
        try
        {
            using NpgsqlConnection unopened = context.Target.CreateConnection(StudioConnectionUsage.ReadWrite);
            return IsMultiHost(unopened.ConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Unknown is treated as the cautious answer, multi-host: the finding stays Info.
            return true;
        }
    }

    /// <summary>Whether a connection string names more than one host.</summary>
    internal static bool IsMultiHost(string? connectionString) =>
        !string.IsNullOrEmpty(connectionString)
        && new NpgsqlConnectionStringBuilder(connectionString).Host is { } host
        && host.Contains(',', StringComparison.Ordinal);

    /// <summary>
    /// Whether an index is a candidate at all: valid, enforcing nothing, removable, never scanned, and on a table whose index
    /// design is its owner's - a library's table is exempt from the index-design rules (issue #8, G1).
    /// </summary>
    private static bool IsUnscanned(IndexFacts index) =>
        index.Row is { IsValid: true, IsUnique: false, IsPrimaryKey: false, IsExclusion: false, Scans: 0 }
        && index.Row.Bytes >= MinIndexBytes
        && index.Removable
        && !index.Table.ExemptFromIndexDesign;

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(UnusedIndexFacts facts)
    {
        // Every sweep counts, a locked table or not: whether an index is unscanned needs its scans, size, validity and
        // origin, none of which the deparse gives, so a locked table's indexes are sampled from their undeparsed rows and
        // their clock runs through the lock - and a table locked in every sweep starves nobody's (TableHistory.HeldSince).
        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (IndexFacts index in facts.Indexes.Concat(facts.LockedIndexes))
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
        // Ordinal, as the set keys its tables: "Orders" and orders are two tables, and ignoring case threw on the second.
        Dictionary<string, TableFacts> tables = facts.Tables.ToDictionary(static x => x.Key, StringComparer.Ordinal);
        List<HealthFinding> findings = [];

        foreach (IndexFacts index in facts.Indexes)
        {
            if (!IsUnscanned(index) || facts.LockedTables.Contains(index.Table.Key) || !tables.TryGetValue(index.Table.Key, out TableFacts? table))
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

        string? note = HealthTables.LockedNote(facts.LockedTables);
        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    /// <summary>
    /// Why "never scanned" is not yet proven, or <see langword="null" /> when it is: a multi-host data source,
    /// which nothing read here can prove; the counters' own age; or the advisor's history of the index.
    /// </summary>
    /// <remarks>
    /// The multi-host reason comes first and the history never lifts it: the history is samples of this
    /// primary's <c>idx_scan</c>, so a day of zeroes says nothing about the scans a standby served.
    /// </remarks>
    internal static string? Unproven(IndexFacts index, UnusedIndexFacts facts, HealthEvaluation evaluation)
    {
        if (facts.MultiHost)
        {
            return "The database is reached through a multi-host data source, so reads may be served by a standby, which counts "
                + "its own index scans: this primary's zero proves nothing about them, however long it has held. Check "
                + "pg_stat_user_indexes on every standby before removing it.";
        }

        DateTimeOffset? seenSince = TableHistory.HeldSince(evaluation.History, SweptSeries, UnscannedSeries + index.Key);
        if (seenSince is { } since && facts.ReadAt - since >= MinCounterAge)
        {
            return null;
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
            GatedSchemas = index.Table.GatedSchemas,
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
                    : facts.MultiHost
                        ? " " + unproven + " So this is Info, and stays Info."
                        : " " + unproven + " So this is Info until the advisor's own samples have seen the index unscanned for a day.")
                + " An index used by a monthly job, a failover's replica or a query that has not run yet looks unused too: "
                + "check before removing it.",
            Remedy = evaluation.TableRemedies?.RemoveUnusedIndex(index.Table, index.Row, index.Origin),
        };
    }
}
