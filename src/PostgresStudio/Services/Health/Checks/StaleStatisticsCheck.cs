using System.Globalization;

using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T08 reads: every declared table's estimate, its modifications since the last analyze, and when that was.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Tables">The declared tables that exist.</param>
internal sealed record StatisticsFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables);

/// <summary>Why a table's statistics are wrong.</summary>
internal enum StatisticsProblem
{
    /// <summary>Nothing wrong.</summary>
    None,

    /// <summary>Never analysed, though it has rows: <c>reltuples = -1</c>.</summary>
    NeverAnalysed,

    /// <summary>Analysed, but more than a fifth of it has changed since, and not recently.</summary>
    Stale,
}

/// <summary>
/// T08: a Marten table that has never been analysed, or whose statistics are stale.
/// </summary>
/// <remarks>
/// <para>
/// The planner chooses every plan from <c>pg_class.reltuples</c> and <c>pg_statistic</c>, and the studio's own
/// <c>~</c> counts are <c>reltuples</c> (D8): both are only as good as the last <c>ANALYZE</c>. A table with
/// rows and <c>reltuples = -1</c> has never been analysed - the planner guesses its size from its page count
/// and the Documents rail counts it the slow way - and one where <c>n_mod_since_analyze</c> exceeds a fifth of
/// <c>reltuples</c> with no analyze in <see cref="StaleAfter" /> is planned against a table that no longer
/// exists.
/// </para>
/// <para>
/// <b>Held, not glimpsed</b>, like T07: right after a bulk load every table looks like this until autovacuum's
/// next pass, so the condition must hold across <see cref="PersistFor" /> unless autovacuum is off for the
/// table or the server.
/// </para>
/// </remarks>
[HealthCheck("T08", HealthArea.Tables, HealthCostTier.Catalog, "Never analysed, or stale statistics")]
internal sealed class StaleStatisticsCheck : HealthCheck<StatisticsFacts>
{
    /// <summary>Live rows below which a never-analysed table is not worth a finding.</summary>
    public const long MinRows = 1_000;

    /// <summary>Modifications since the last analyze below which stale statistics are not worth a finding.</summary>
    public const long MinModifications = 10_000;

    /// <summary>The share of the table that must have changed since the last analyze.</summary>
    public const double MaxModifiedShare = 0.2;

    /// <summary>The analyze scale factor the remedy suggests: re-analyse at 5% changed rather than 10%.</summary>
    public const double SuggestedScaleFactor = 0.05;

    /// <summary>How long ago the last analyze must have been for statistics to count as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>How long the condition must hold while autovacuum is on.</summary>
    public static readonly TimeSpan PersistFor = TimeSpan.FromMinutes(10);

    /// <summary>The series T08 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.statistics.swept";

    /// <summary>The series of a table whose statistics are wrong, written as 1.</summary>
    internal const string WrongSeries = "tables.statistics-wrong:";

    /// <inheritdoc />
    public override async Task<StatisticsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return new StatisticsFacts(context.Now, await HealthTables.TablesAsync(context).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(StatisticsFacts facts)
    {
        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (TableFacts table in facts.Tables)
        {
            if (ProblemOf(table, facts.ReadAt) != StatisticsProblem.None)
            {
                yield return new HealthSamplePart(WrongSeries + table.Key, 1);
            }
        }
    }

    /// <summary>What is wrong with a table's statistics at <paramref name="at" />, before persistence is considered.</summary>
    internal static StatisticsProblem ProblemOf(TableFacts table, DateTimeOffset at)
    {
        long estimate = table.Row.EstimatedRows;

        if (estimate < 0)
        {
            return table.Row.LiveRows >= MinRows && table.LastAnalyzeOfAnyKind is null
                ? StatisticsProblem.NeverAnalysed
                : StatisticsProblem.None;
        }

        return table.Row.ModifiedSinceAnalyze is { } modified
            && modified >= MinModifications
            && modified > Math.Max(estimate, 1) * MaxModifiedShare
            && (table.LastAnalyzeOfAnyKind is not { } last || at - last > StaleAfter)
                ? StatisticsProblem.Stale
                : StatisticsProblem.None;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(StatisticsFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];
        int watching = 0;

        foreach (TableFacts table in facts.Tables)
        {
            StatisticsProblem problem = ProblemOf(table, facts.ReadAt);
            if (problem == StatisticsProblem.None)
            {
                continue;
            }

            DateTimeOffset? since = TableHistory.HeldSince(evaluation.History, SweptSeries, WrongSeries + table.Key);
            TimeSpan held = since is { } start ? facts.ReadAt - start : TimeSpan.Zero;

            if (!table.AutovacuumOff && held < PersistFor)
            {
                watching++;
                continue;
            }

            findings.Add(Finding(table, problem, held, evaluation));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watching == 0
            ? null
            : "Watching " + watching.ToString(CultureInfo.InvariantCulture) + " table" + (watching == 1 ? string.Empty : "s")
              + " whose statistics are behind: a finding needs that to hold for " + TableText.Span(PersistFor)
              + ", because autovacuum analyses a freshly loaded table within a minute or two.");
    }

    private static HealthFinding Finding(TableFacts table, StatisticsProblem problem, TimeSpan held, HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        string scale = SuggestedScaleFactor.ToString(CultureInfo.InvariantCulture);
        bool never = problem == StatisticsProblem.NeverAnalysed;

        return new HealthFinding
        {
            Code = "T08",
            Severity = HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            Title = never
                ? declared.Key + " has " + TableText.Count(table.Row.LiveRows ?? 0) + " rows and has never been analysed"
                : TableText.Count(table.Row.ModifiedSinceAnalyze ?? 0) + " rows of " + declared.Key
                  + " have changed since it was last analysed",
            WhyItMatters =
                (never
                    ? "Postgres has no statistics for this table at all (reltuples is -1), so the planner guesses how many rows it has "
                      + "and how its values are spread, and the studio's own ~ counts cannot estimate it either."
                    : "More than a fifth of the table has changed since its statistics were gathered, so the planner is choosing "
                      + "plans for a table that is no longer there, and the studio's ~ counts are off by as much.")
                + " A wrong row estimate is how a query that should use an index ends up scanning the table, or a join picks a "
                + "nested loop over a million rows."
                + Autovacuum.State(table, "autovacuum_analyze_scale_factor", scale, evaluation.Text),
            Evidence =
            [
                new HealthEvidence("Table", declared.Describe()),
                new HealthEvidence("Estimated rows (reltuples)", table.Row.EstimatedRows < 0 ? "not analysed" : TableText.Rows(table.Row.EstimatedRows)),
                new HealthEvidence("Live rows", TableText.Count(table.Row.LiveRows ?? 0)),
                new HealthEvidence("Modified since the last analyze", TableText.Count(table.Row.ModifiedSinceAnalyze ?? 0)),
                new HealthEvidence("Last analyze", TableText.When(table.Row.LastAnalyze)),
                new HealthEvidence("Last autoanalyze", TableText.When(table.Row.LastAutoanalyze)),
            ],
            Caveat =
                "The modification count and analyze times are the statistics collector's, which a pg_stat_reset() or a crash "
                + "clears. " + (held > TimeSpan.Zero ? "Seen for " + TableText.Span(held) + "." : "Seen in this sweep.")
                + (table.Row.IsPartitioned
                    ? " This is a partitioned table, judged by its partitions' own statistics: autovacuum never analyses a "
                      + "partitioned parent."
                    : string.Empty),
            Remedy = Autovacuum.Remedy(
                evaluation.TableRemedies,
                table,
                "autovacuum_analyze_scale_factor",
                scale,
                "Autovacuum then re-analyses this table once 5% of it has changed rather than 10%. A DBA's ANALYZE fixes the "
                + "statistics now; this keeps them fixed."),
        };
    }
}
