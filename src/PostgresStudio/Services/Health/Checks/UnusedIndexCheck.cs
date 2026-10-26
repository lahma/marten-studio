using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T05 reads: the indexes, the tables' write counters, and how long the counters have counted.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Indexes">The indexes on the declared tables.</param>
/// <param name="Tables">The declared tables, for their write volume.</param>
/// <param name="Database">The database-wide facts the table read carries.</param>
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
    /// The statistics window's signals and the counters' oldest recorded moment (<see cref="UsageGuard" />), or
    /// <see langword="null" /> in a unit test that judges the window through <see cref="HealthEvaluation.Statistics" /> alone.
    /// </summary>
    public UsageWindowReading? Usage { get; init; }
}

/// <summary>
/// T05: an index nothing has scanned - since the counters began, or (PostgreSQL 16+) for thirty days - on a table that is
/// written to a lot.
/// </summary>
/// <remarks>
/// <para>
/// The Indexes tab's "never used" flag with the two facts that make it worth acting on: the table's write
/// volume, which is what the index costs, and the index's size. A unique or primary-key index is never
/// reported - it enforces something whether or not anything reads it - and neither is one Marten adds by
/// itself or the event store declares, which no line of the host's removes.
/// </para>
/// <para>
/// <b>Never over a short or reset window</b> (issue #8 G4, <see cref="UsageGuard" />). <c>pg_stat_reset()</c>, a crash, a
/// restore, a pre-18 <c>pg_upgrade</c> and a failover all zero <c>idx_scan</c>, and an index used by a weekly job looks
/// unused for most of the week. So no finding is made until the counters have counted for seven days and do not look reset;
/// until then the status row says why. This replaces 0.4's Info-until-a-day-of-samples rule (<c>MinCounterAge</c>): a finding
/// is a Warning or nothing.
/// </para>
/// <para>
/// <b>Not scanned for thirty days</b> (PostgreSQL 16+). <c>last_idx_scan</c>, set at the statistics flush after a scan and
/// readable by every role (B0 G4), adds the index that was used once and never since: one last scanned more than
/// <see cref="NotScannedFor" /> ago is unused too, with the date in the evidence.
/// </para>
/// <para>
/// <b>A multi-host data source is Info always.</b> Its reads may be served by a standby, which counts its own
/// index scans; nothing read on this primary proves anything about the standby's, and the finding says to check every
/// standby before removing the index.
/// </para>
/// </remarks>
[HealthCheck("T05", HealthArea.Tables, HealthCostTier.Catalog, "Unused index on a write-heavy table")]
internal sealed class UnusedIndexCheck : HealthCheck<UnusedIndexFacts>
{
    /// <summary>Inserts, updates and deletes on the table since the counters began, before an unused index costs enough.</summary>
    public const long MinWrites = 100_000;

    /// <summary>The smallest index worth a finding.</summary>
    public const long MinIndexBytes = 1024 * 1024;

    /// <summary>How long ago an index's last scan (<c>last_idx_scan</c>, PostgreSQL 16+) must be for it to count as unused.</summary>
    public static readonly TimeSpan NotScannedFor = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public override async Task<UnusedIndexFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);
        UsageWindowReading usage = await UsageGuard.ReadAsync(context).ConfigureAwait(false);

        return new UnusedIndexFacts(context.Now, read.Indexes, tables, DatabaseStatistics.Of(tables), IsMultiHost(context))
        {
            LockedTables = read.LockedTables,
            Usage = usage,
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
    /// Whether an index is a candidate at all: valid, enforcing nothing, removable, big enough, and on a table whose index
    /// design is its owner's - a library's table is exempt from the index-design rules (issue #8, G1).
    /// </summary>
    private static bool IsCandidate(IndexFacts index) =>
        index.Row is { IsValid: true, IsUnique: false, IsPrimaryKey: false, IsExclusion: false }
        && index.Row.Bytes >= MinIndexBytes
        && index.Removable
        && !index.Table.ExemptFromIndexDesign;

    /// <summary>
    /// How an index counts as unused, or <see langword="null" /> when it does not: never scanned since the counters began, or
    /// - on PostgreSQL 16 and later - last scanned more than <see cref="NotScannedFor" /> before <paramref name="now" />.
    /// </summary>
    internal static DateTimeOffset? LastScanIfUnused(HealthIndexRow row, DateTimeOffset now, out bool unused)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Scans == 0)
        {
            unused = true;
            return null;
        }

        if (PostgresFeatures.Has(PostgresFeature.LastScanColumns, row.ServerVersion) && row.LastScan is { } last && now - last >= NotScannedFor)
        {
            unused = true;
            return last;
        }

        unused = false;
        return null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(UnusedIndexFacts facts, HealthEvaluation evaluation)
    {
        string? locked = HealthTables.LockedNote(facts.LockedTables);

        // Nothing to say how long the counters have counted: never a verdict over a window nobody measured.
        if (UsageGuard.For(facts.Usage, evaluation) is not { } usage)
        {
            return HealthVerdict.Quiet(HealthTables.WithLockedNote(UsageGuard.NotRead, facts.LockedTables));
        }

        if (usage.Withheld(UsageGuard.MinimumWindow) is { } withheld)
        {
            return HealthVerdict.Quiet(HealthTables.WithLockedNote(withheld, facts.LockedTables));
        }

        // Ordinal, as the set keys its tables: "Orders" and orders are two tables, and ignoring case threw on the second.
        Dictionary<string, TableFacts> tables = facts.Tables.ToDictionary(static x => x.Key, StringComparer.Ordinal);
        List<HealthFinding> findings = [];
        int youngIndexes = 0;

        foreach (IndexFacts index in facts.Indexes)
        {
            if (!IsCandidate(index) || facts.LockedTables.Contains(index.Table.Key) || !tables.TryGetValue(index.Table.Key, out TableFacts? table))
            {
                continue;
            }

            DateTimeOffset? lastScan = LastScanIfUnused(index.Row, facts.ReadAt, out bool unused);
            if (!unused)
            {
                continue;
            }

            long writes = (table.Row.Inserts ?? 0) + (table.Row.Updates ?? 0) + (table.Row.Deletes ?? 0);
            if (writes < MinWrites)
            {
                continue;
            }

            // PostgreSQL 19 keeps a reset time per index: an index whose own counters were reset inside the window is judged
            // over its own, shorter one.
            if (index.Row.CountersReset is { } own && own > usage.Since && facts.ReadAt - own < UsageGuard.MinimumWindow && lastScan is null)
            {
                youngIndexes++;
                continue;
            }

            findings.Add(Finding(index, table, writes, lastScan, usage, facts, evaluation.TableRemedies));
        }

        string? note = youngIndexes switch
        {
            0 => locked,
            1 => HealthTables.WithLockedNote("1 index's own counters were reset less than 7 days ago and was not judged.", facts.LockedTables),
            int count => HealthTables.WithLockedNote(SessionText.Number(count) + " indexes' own counters were reset less than 7 days ago and were not judged.", facts.LockedTables),
        };

        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    /// <summary>What the caveat says of a multi-host data source, which no length of this primary's counters lifts.</summary>
    internal const string MultiHostCaveat =
        "The database is reached through a multi-host data source, so reads may be served by a standby, which counts its own "
        + "index scans: this primary's counters prove nothing about them, however long they have counted. Check "
        + "pg_stat_user_indexes on every standby before removing it. So this is Info, and stays Info.";

    private static HealthFinding Finding(
        IndexFacts index,
        TableFacts table,
        long writes,
        DateTimeOffset? lastScan,
        UsageWindow usage,
        UnusedIndexFacts facts,
        IHealthTableRemedies? remedies)
    {
        long hot = table.Row.HotUpdates ?? 0;
        long maintained = (table.Row.Inserts ?? 0) + Math.Max(0, (table.Row.Updates ?? 0) - hot);
        bool lastScanColumn = PostgresFeatures.Has(PostgresFeature.LastScanColumns, index.Row.ServerVersion);

        string unused = lastScan is { } last
            ? "has not been scanned for " + TableText.Span(facts.ReadAt - last)
            : "has never been scanned";

        List<HealthEvidence> evidence =
        [
            new HealthEvidence("Index", index.Row.Name),
            new HealthEvidence("Table", index.Table.Describe()),
            new HealthEvidence("Size", TableText.Bytes(index.Row.Bytes)),
            new HealthEvidence("Index scans", index.Row.Scans is { } scans ? TableText.Count(scans) : "0"),
        ];

        if (lastScanColumn)
        {
            evidence.Add(new HealthEvidence("Last scan", lastScan is { } at ? TableText.When(at) : "never, since the counters began"));
        }

        evidence.Add(new HealthEvidence("Writes to the table", TableText.Count(writes)
            + " (" + TableText.Count(table.Row.Inserts ?? 0) + " inserts, " + TableText.Count(table.Row.Updates ?? 0)
            + " updates, " + TableText.Count(table.Row.Deletes ?? 0) + " deletes)"));
        evidence.Add(new HealthEvidence("Counters since", usage.SinceText));

        return new HealthFinding
        {
            Code = "T05",
            Severity = facts.MultiHost ? HealthSeverity.Info : HealthSeverity.Warning,
            Subject = index.Key,
            DocumentTypes = index.Table.DocumentTypes,
            GatedSchemas = index.Table.GatedSchemas,
            Title = "Index " + index.Row.Name + " on " + index.Table.Key + " " + unused + ", while the table took "
                + TableText.Count(writes) + " writes",
            WhyItMatters =
                "Every insert, and every update that is not a HOT update, writes an entry into every index on the table - about "
                + TableText.Count(maintained) + " entries into this one since the counters began - and the index holds "
                + TableText.Bytes(index.Row.Bytes) + " of disk and buffer cache. "
                + (lastScan is { } since
                    ? "No query has used it since " + TableText.When(since) + "."
                    : "No query has used it in " + UsageWindow.Age(usage.Length) + " of statistics.")
                + (index.Origin == IndexOrigin.Undeclared
                    ? " Nothing declares it, so the next apply drops it anyway (AutoCreate.CreateOrUpdate is not additive)."
                    : string.Empty),
            Evidence = evidence,
            Caveat =
                "Judged over " + UsageWindow.Age(usage.Length) + " of statistics, " + usage.SinceText + ": a usage verdict is never "
                + "given over less than 7 days or over counters that look reset."
                + (facts.MultiHost ? " " + MultiHostCaveat : string.Empty)
                + " An index used by a monthly job, a failover's replica or a query that has not run yet looks unused too: "
                + "check before removing it.",
            Remedy = remedies?.RemoveUnusedIndex(index.Table, index.Row, index.Origin),
        };
    }
}
