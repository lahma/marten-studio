using System.Globalization;

using MartenStudio.Services.Sources;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T07 reads: every declared table's dead rows and vacuum times.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Tables">The declared tables that exist.</param>
internal sealed record VacuumFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables);

/// <summary>
/// T07: dead rows pile up in a Marten table and vacuum has not been round in a day.
/// </summary>
/// <remarks>
/// <para>
/// A dead row is an old version an update or a delete left behind; only vacuum makes its space reusable.
/// More than <see cref="MaxDeadShare" /> of a table dead, at least <see cref="MinDeadRows" /> rows of it, with
/// the last vacuum of either kind more than <see cref="StaleAfter" /> ago (or never), is a table autovacuum is
/// not keeping up with: bloat that every scan reads and every backup copies.
/// </para>
/// <para>
/// <b>Held, not glimpsed.</b> A batch delete makes any table look like this for the minute before autovacuum
/// wakes up, so the condition must hold across <see cref="PersistFor" /> of samples before it is a finding -
/// unless autovacuum is switched off for the table or the server, when there is nothing to wait for. Until
/// then the status row says it is watching.
/// </para>
/// <para>
/// The <c>n_mod_since_analyze</c> half of the plan's T07 is T08's (stale statistics), so one condition is one
/// finding: this check is about dead rows and vacuum.
/// </para>
/// </remarks>
[HealthCheck("T07", HealthArea.Tables, HealthCostTier.Catalog, "Vacuum falling behind")]
internal sealed class VacuumBehindCheck : HealthCheck<VacuumFacts>
{
    /// <summary>The dead share above which the check speaks.</summary>
    public const double MaxDeadShare = 0.2;

    /// <summary>Dead rows below which bloat is not worth a finding.</summary>
    public const long MinDeadRows = 10_000;

    /// <summary>The scale factor the remedy suggests: vacuum at 2% dead rather than Postgres' default 20%.</summary>
    public const double SuggestedScaleFactor = 0.02;

    /// <summary>How long ago the last vacuum must have been.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>How long the condition must hold while autovacuum is on.</summary>
    public static readonly TimeSpan PersistFor = TimeSpan.FromMinutes(10);

    /// <summary>The series T07 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.vacuum-behind.swept";

    /// <summary>The series of a table that is behind, written as 1.</summary>
    internal const string BehindSeries = "tables.vacuum-behind:";

    /// <inheritdoc />
    public override async Task<VacuumFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return new VacuumFacts(context.Now, await HealthTables.TablesAsync(context).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(VacuumFacts facts)
    {
        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (TableFacts table in facts.Tables)
        {
            if (IsBehind(table, facts.ReadAt))
            {
                yield return new HealthSamplePart(BehindSeries + table.Key, 1);
            }
        }
    }

    /// <summary>Whether a table is behind at <paramref name="at" />, before persistence is considered.</summary>
    internal static bool IsBehind(TableFacts table, DateTimeOffset at) =>
        table.DeadShare is { } share
        && share > MaxDeadShare
        && table.Row.DeadRows >= MinDeadRows
        && (table.LastVacuumOfAnyKind is not { } last || at - last > StaleAfter);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(VacuumFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];
        int watching = 0;

        foreach (TableFacts table in facts.Tables)
        {
            if (!IsBehind(table, facts.ReadAt))
            {
                continue;
            }

            DateTimeOffset? since = TableHistory.HeldSince(evaluation.History, SweptSeries, BehindSeries + table.Key);
            TimeSpan held = since is { } start ? facts.ReadAt - start : TimeSpan.Zero;

            if (!table.AutovacuumOff && held < PersistFor)
            {
                watching++;
                continue;
            }

            findings.Add(Finding(table, held, facts.ReadAt, evaluation.TableRemedies));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watching == 0
            ? null
            : "Watching " + watching.ToString(CultureInfo.InvariantCulture) + " table" + (watching == 1 ? string.Empty : "s")
              + " with many dead rows: a finding needs the condition to hold for " + TableText.Span(PersistFor)
              + ", because autovacuum usually catches up within a minute of a batch delete.");
    }

    private static HealthFinding Finding(TableFacts table, TimeSpan held, DateTimeOffset now, IHealthTableRemedies? remedies)
    {
        StudioTable declared = table.Table;
        string scale = SuggestedScaleFactor.ToString(CultureInfo.InvariantCulture);

        HealthRemedy? remedy = Autovacuum.Remedy(
            remedies,
            table,
            "autovacuum_vacuum_scale_factor",
            scale,
            "Autovacuum then vacuums this table once 2% of its rows are dead rather than 20%, in smaller, more frequent passes.");

        return new HealthFinding
        {
            Code = "T07",
            Severity = HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            // "it was last vacuumed never" is not a sentence: a table nothing has vacuumed gets a clause of its own.
            Title = TableText.Percent(table.DeadShare ?? 0) + " of " + declared.Key + " is dead rows, and "
                + (table.LastVacuumOfAnyKind is { } last
                    ? "it was last vacuumed " + TableText.Span(now - last) + " ago"
                    : "it has never been vacuumed"),
            WhyItMatters =
                "A dead row is an old version an update or a delete left behind, and only vacuum makes its space reusable. Until "
                + "it runs, every sequential scan reads the dead rows, every index still points at them, the table and its "
                + "indexes keep growing, and the visibility map that index-only scans need goes stale."
                + Autovacuum.State(table, "autovacuum_vacuum_scale_factor", scale),
            Evidence =
            [
                new HealthEvidence("Table", declared.Describe()),
                new HealthEvidence("Dead rows", TableText.Count(table.Row.DeadRows ?? 0)
                    + " (" + TableText.Percent(table.DeadShare ?? 0) + ")"),
                new HealthEvidence("Live rows", TableText.Count(table.Row.LiveRows ?? 0)),
                new HealthEvidence("Last vacuum", TableText.When(table.Row.LastVacuum)),
                new HealthEvidence("Last autovacuum", TableText.When(table.Row.LastAutovacuum)),
                new HealthEvidence("Storage parameters", table.Row.StorageOptions.Count == 0 ? "none" : string.Join(", ", table.Row.StorageOptions)),
            ],
            Caveat =
                "Dead-row counts and vacuum times are the statistics collector's, which a pg_stat_reset() or a crash clears. "
                + (held > TimeSpan.Zero ? "Seen for " + TableText.Span(held) + "." : "Seen in this sweep.")
                + " A long-running transaction stops vacuum removing dead rows even when it runs - see the connections checks.",
            Remedy = remedy,
        };
    }
}
