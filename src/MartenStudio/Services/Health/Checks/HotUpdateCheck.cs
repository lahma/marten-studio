using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T06 reads: the document tables' update counters and the columns their indexes read.</summary>
/// <param name="Tables">The declared tables (only document tables are judged).</param>
/// <param name="Indexes">The indexes on them.</param>
/// <param name="Database">When the counters began.</param>
internal sealed record HotUpdateFacts(IReadOnlyList<TableFacts> Tables, IReadOnlyList<IndexFacts> Indexes, DatabaseStatistics Database);

/// <summary>
/// T06: most updates to a document table are not HOT updates.
/// </summary>
/// <remarks>
/// <para>
/// A HOT (heap-only tuple) update writes the new row version on the same page and touches no index. It is
/// possible only when no indexed column changes and the page has room. With the default fillfactor of 100 a
/// table's pages are full, so a Marten upsert usually lands on another page and writes a new entry into every
/// index. Leaving room with <c>FillFactor(80)</c> (Marten 9.47+) is the cure - when the second condition is
/// the one failing.
/// </para>
/// <para>
/// <b>When fillfactor cannot help, the finding says so.</b> Every Marten upsert rewrites <c>data</c>, stamps
/// <c>mt_last_modified</c> and draws a new <c>mt_version</c>, so an index that reads any of those columns - a
/// GIN index on the document, any computed <c>Index(x =&gt; x.Member)</c> (an expression over <c>data</c>),
/// <c>IndexLastModified()</c> - makes every update a non-HOT one whatever the free space. The columns an index
/// reads come from <c>pg_depend</c> and <c>indkey</c>; a BRIN index is left out on PostgreSQL 16 and later
/// (<c>server_version_num</c> 160000), because from 16 a summarizing index no longer blocks HOT - and kept in
/// before it, where it does.
/// </para>
/// <para>
/// Rates over the window, like T01: the counters are sampled per document table and judged by their change,
/// only past <see cref="MinUpdates" /> updates in the window.
/// </para>
/// </remarks>
[HealthCheck("T06", HealthArea.Tables, HealthCostTier.Catalog, "Low HOT-update ratio")]
internal sealed class HotUpdateCheck : HealthCheck<HotUpdateFacts>
{
    /// <summary>Updates in the window before the ratio means anything.</summary>
    public const long MinUpdates = 10_000;

    /// <summary>The HOT share below which the check speaks.</summary>
    public const double MinHotShare = 0.5;

    /// <summary>The fillfactor the remedy suggests.</summary>
    public const int SuggestedFillFactor = 80;

    /// <summary>The series of a table's <c>n_tup_upd</c>.</summary>
    internal const string UpdatesSeries = "tables.updates:";

    /// <summary>The series of a table's <c>n_tup_hot_upd</c>.</summary>
    internal const string HotUpdatesSeries = "tables.hot-updates:";

    /// <summary>The columns every Marten upsert changes, so an index on any of them blocks every HOT update.</summary>
    internal static readonly string[] ColumnsEveryUpsertChanges = ["data", "mt_last_modified", "mt_version"];

    /// <inheritdoc />
    public override async Task<HotUpdateFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<TableFacts> tables = await MartenTables.TablesAsync(context).ConfigureAwait(false);
        IReadOnlyList<IndexFacts> indexes = await MartenTables.IndexesAsync(context).ConfigureAwait(false);

        return new HotUpdateFacts(tables, indexes, DatabaseStatistics.Of(tables));
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(HotUpdateFacts facts)
    {
        foreach (TableFacts table in facts.Tables)
        {
            if (table.Table.Kind == MartenTableKind.Document && table.Row is { Updates: { } updates, HotUpdates: { } hot })
            {
                yield return new HealthSamplePart(UpdatesSeries + table.Key, updates);
                yield return new HealthSamplePart(HotUpdatesSeries + table.Key, hot);
            }
        }
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(HotUpdateFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];
        bool watched = false;

        foreach (TableFacts table in facts.Tables)
        {
            if (table.Table.Kind != MartenTableKind.Document)
            {
                continue;
            }

            CounterDelta? updates = TableHistory.Delta(evaluation.History.Trend(UpdatesSeries + table.Key));
            CounterDelta? hot = TableHistory.Delta(evaluation.History.Trend(HotUpdatesSeries + table.Key));

            if (updates is null || hot is null)
            {
                continue;
            }

            watched = true;

            if (updates.Change < MinUpdates)
            {
                continue;
            }

            double share = Math.Clamp(hot.Change / updates.Change, 0, 1);
            if (share >= MinHotShare)
            {
                continue;
            }

            IReadOnlyList<IndexFacts> blocking = [.. BlockingIndexes(table, facts.Indexes)];

            findings.Add(blocking.Count == 0
                ? FillFactorFinding(table, updates, share, facts, evaluation)
                : CannotHelpFinding(table, updates, share, blocking, facts, evaluation));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watched ? null : "An update rate needs two samples, one interval apart.");
    }

    /// <summary>The first server version (<c>server_version_num</c>) on which a BRIN index no longer blocks HOT.</summary>
    internal const int SummarizingIndexesAllowHotFrom = 160_000;

    /// <summary>
    /// The valid indexes on the table that read a column every Marten upsert changes - a BRIN index among them only
    /// before PostgreSQL 16, which is when a summarizing index stopped blocking HOT.
    /// </summary>
    internal static IEnumerable<IndexFacts> BlockingIndexes(TableFacts table, IReadOnlyList<IndexFacts> indexes)
    {
        foreach (IndexFacts index in indexes)
        {
            if (!string.Equals(index.Table.Key, table.Key, StringComparison.OrdinalIgnoreCase)
                || !index.Row.IsValid
                || (index.Row.ServerVersion >= SummarizingIndexesAllowHotFrom
                    && string.Equals(index.Row.AccessMethod, "brin", StringComparison.Ordinal)))
            {
                continue;
            }

            if (index.Row.ReferencedColumns.Any(static column =>
                    ColumnsEveryUpsertChanges.Contains(column, StringComparer.OrdinalIgnoreCase)))
            {
                yield return index;
            }
        }
    }

    private static List<HealthEvidence> Evidence(TableFacts table, CounterDelta updates, double share) =>
    [
        new HealthEvidence("Table", table.Table.Describe()),
        new HealthEvidence("Updates in the window", TableText.Count(updates.Change)),
        new HealthEvidence("HOT updates", TableText.Percent(share)),
        new HealthEvidence("Window", TableText.Span(updates.Span)),
    ];

    private static HealthFinding FillFactorFinding(
        TableFacts table,
        CounterDelta updates,
        double share,
        HotUpdateFacts facts,
        HealthEvaluation evaluation) => new()
    {
        Code = "T06",
        Severity = HealthSeverity.Warning,
        Subject = table.Key,
        DocumentTypes = table.Table.DocumentTypes,
        GatedSchemas = table.Table.GatedSchemas,
        Title = "Only " + TableText.Percent(share) + " of the updates to " + table.Key + " were HOT updates",
        WhyItMatters =
            "No index on this table reads a column every upsert changes, so its updates could stay on their page and skip "
            + "every index - but the pages are full, so each update lands on another page and writes a new entry into every "
            + "index, and leaves a dead row for vacuum. Leaving free space on each page lets most of them stay put.",
        Evidence = Evidence(table, updates, share),
        Caveat =
            "Measured over " + TableText.Span(updates.Span) + " of history. " + TableText.CountersSince(facts.Database.StatsReset, evaluation.Now),
        Remedy = new HealthRemedy(
            "opts.Schema.For<" + table.Table.TypeName + ">().FillFactor(" + SuggestedFillFactor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ");",
            "Leaves a fifth of each page free for an update to stay on. It costs about a quarter more heap, and - in Marten's own "
            + "words - affects only pages written after the change: existing rows are not repacked until the table is rewritten.",
            RuntimeFeature.DocumentFillFactor),
    };

    private static HealthFinding CannotHelpFinding(
        TableFacts table,
        CounterDelta updates,
        double share,
        IReadOnlyList<IndexFacts> blocking,
        HotUpdateFacts facts,
        HealthEvaluation evaluation)
    {
        string names = string.Join(", ", blocking.Select(static x => x.Row.Name + " (reads "
            + string.Join(", ", x.Row.ReferencedColumns.Where(static c => ColumnsEveryUpsertChanges.Contains(c, StringComparer.OrdinalIgnoreCase)))
            + ")"));

        bool computed = blocking.Any(static x => x.Origin == IndexOrigin.Declared
            && x.Row.ReferencedColumns.Contains("data", StringComparer.OrdinalIgnoreCase)
            && !string.Equals(x.Row.AccessMethod, "gin", StringComparison.Ordinal));

        List<HealthEvidence> evidence = Evidence(table, updates, share);
        evidence.Add(new HealthEvidence("Indexes that block HOT", names));

        return new HealthFinding
        {
            Code = "T06",
            Severity = HealthSeverity.Info,
            Subject = table.Key,
            DocumentTypes = table.Table.DocumentTypes,
            GatedSchemas = table.Table.GatedSchemas,
            Title = "Only " + TableText.Percent(share) + " of the updates to " + table.Key + " were HOT, and fillfactor cannot help",
            WhyItMatters =
                "An update can be a HOT update only when no indexed column changes. Every Marten upsert rewrites data, stamps "
                + "mt_last_modified and draws a new mt_version, and this table has an index on one of them - " + names
                + " - so no upsert to it can be a HOT update, however much free space its pages have. FillFactor would cost "
                + "disk and change nothing.",
            Evidence = evidence,
            Caveat =
                "Measured over " + TableText.Span(updates.Span) + " of history. The indexed columns are Postgres' own record of what "
                + "each index reads (pg_depend).",
            Remedy = computed
                ? new HealthRemedy(
                    "// A computed index reads data, which every upsert rewrites. A duplicated field is a column of its own,\n"
                    + "// changed only when the member is, so its index lets the other updates stay HOT:\n"
                    + "opts.Schema.For<" + table.Table.TypeName + ">().Duplicate(x => x.IndexedMember);  // in place of .Index(x => x.IndexedMember)",
                    "Worth it only for a member that changes less often than the document does. A GIN index on the whole document "
                    + "and IndexLastModified() block HOT by design; keep them only where a query needs them.")
                : null,
        };
    }
}
