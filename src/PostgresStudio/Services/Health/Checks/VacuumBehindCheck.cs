using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T07 reads: every judged table's autovacuum decisions, and what the role can see of running vacuums.</summary>
/// <param name="ReadAt">When they were read.</param>
/// <param name="Tables">The judged tables that exist.</param>
/// <param name="ProgressHidden">
/// The "not checked" sentence for a role that cannot see autovacuum's progress (<see cref="PostgresGrants.NotChecked" />)
/// while a <c>VACUUM</c> whose table it cannot see is running in this database - or <see langword="null" /> when nothing
/// running is hidden from it.
/// </param>
internal sealed record VacuumFacts(DateTimeOffset ReadAt, IReadOnlyList<TableFacts> Tables, string? ProgressHidden = null);

/// <summary>Which of autovacuum's two vacuum decisions a table is behind on.</summary>
internal enum VacuumBehindKind
{
    /// <summary>Dead rows, against the dead-row trigger.</summary>
    DeadRows,

    /// <summary>Inserts since the last vacuum, against the insert trigger.</summary>
    Inserts,
}

/// <summary>
/// T07: autovacuum is behind on a table - judged against the table's own triggers, the way autovacuum decides (issue #8, G2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Behind, as the issue defines it.</b> A counter at least <see cref="MinRowsPast" /> past twice its trigger - dead rows
/// against the dead-row trigger (<c>dead-rows</c>) or inserts since the last vacuum against the insert trigger
/// (<c>inserts</c>, which an append-only table such as <c>mt_events</c> depends on) - with the last vacuum of either kind
/// more than <see cref="StaleAfter" /> ago or never, and the table not being vacuumed now. The triggers are the table's own
/// (<see cref="Autovacuum.Triggers" />): its storage parameters over the server's settings, PostgreSQL 18's cap and unfrozen
/// share, so a table tuned to <c>scale_factor 0.01</c> is caught long before it is a fifth dead - the fixed 20% and one-day
/// rules of 0.4 are gone. A partitioned table is judged by the partition furthest behind, never named.
/// </para>
/// <para>
/// <b>"Not being vacuumed now" needs <c>pg_read_all_stats</c></b> for autovacuum's own work: without it the role sees only
/// the process id and database of a <c>VACUUM</c> it did not run, autovacuum's on its own tables included (B0 §7c). While
/// such a one runs, the finding still speaks, and says it may be under way now.
/// </para>
/// <para>
/// <b>Held, not glimpsed.</b> A batch delete or a bulk load makes any table look like this until autovacuum's next pass, so
/// the condition must hold across <see cref="PersistFor" /> of samples before it is a finding - unless autovacuum is switched
/// off for the table or the server, when there is nothing to wait for. Until then the status row says it is watching.
/// </para>
/// <para>
/// <b>Severity.</b> Warning; Critical past ten times the trigger, or with autovacuum switched off for the table or the
/// server and the table behind.
/// </para>
/// </remarks>
[HealthCheck("T07", HealthArea.Tables, HealthCostTier.Catalog, "Vacuum falling behind", Variants = [DeadRowsVariant, InsertsVariant])]
internal sealed class VacuumBehindCheck : HealthCheck<VacuumFacts>
{
    /// <summary>The variant of a table behind on dead rows.</summary>
    public const string DeadRowsVariant = "dead-rows";

    /// <summary>The variant of a table behind on inserts.</summary>
    public const string InsertsVariant = "inserts";

    /// <summary>How far past twice its trigger a counter must be.</summary>
    public const long MinRowsPast = 10_000;

    /// <summary>How many times its trigger a counter must be past for the finding to be Critical.</summary>
    public const double CriticalTimesTrigger = 10;

    /// <summary>How long ago the last vacuum must have been.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>How long the condition must hold while autovacuum is on.</summary>
    public static readonly TimeSpan PersistFor = TimeSpan.FromMinutes(10);

    /// <summary>The series T07 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.vacuum-behind.swept";

    /// <summary>The series of a table behind on dead rows, written as 1.</summary>
    internal const string BehindSeries = "tables.vacuum-behind.dead-rows:";

    /// <summary>The series of a table behind on inserts, written as 1.</summary>
    internal const string InsertsBehindSeries = "tables.vacuum-behind.inserts:";

    /// <inheritdoc />
    public override async Task<VacuumFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);
        return new VacuumFacts(context.Now, tables, await ProgressHiddenAsync(context, tables).ConfigureAwait(false));
    }

    /// <summary>
    /// The "not checked" sentence for running vacuums this role cannot see, or <see langword="null" /> - read only when the
    /// table read counted one, so a role that sees everything costs no grant read.
    /// </summary>
    internal static async Task<string?> ProgressHiddenAsync(HealthCheckContext context, IReadOnlyList<TableFacts> tables)
    {
        if (tables.Count == 0 || tables[0].Row.UnseenVacuums == 0)
        {
            return null;
        }

        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return SessionText.Count(tables[0].Row.UnseenVacuums, "VACUUM") + " running in this database this role cannot see the table of. "
            + PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, server.Platform, grants.ReadsAs);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(VacuumFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        yield return new HealthSamplePart(SweptSeries, 1);

        foreach (TableFacts table in facts.Tables)
        {
            if (IsBehind(table, VacuumBehindKind.DeadRows, facts.ReadAt))
            {
                yield return new HealthSamplePart(BehindSeries + table.Key, 1);
            }

            if (IsBehind(table, VacuumBehindKind.Inserts, facts.ReadAt))
            {
                yield return new HealthSamplePart(InsertsBehindSeries + table.Key, 1);
            }
        }
    }

    /// <summary>The decision a kind is about: the dead-row one or the insert one.</summary>
    internal static AutovacuumUnit? UnitOf(TableFacts table, VacuumBehindKind kind) =>
        kind == VacuumBehindKind.DeadRows ? table.Row.Vacuum : table.Row.Insert;

    /// <summary>
    /// Whether a decision is behind at <paramref name="at" />, before persistence is considered: at least
    /// <see cref="MinRowsPast" /> past twice its trigger, last vacuumed more than <see cref="StaleAfter" /> ago or never, and
    /// not being vacuumed now.
    /// </summary>
    internal static bool IsBehind(AutovacuumUnit? unit, DateTimeOffset at) =>
        unit is { } u
        && u.Counter - (2 * u.Trigger) >= MinRowsPast
        && (u.Last is not { } last || at - last > StaleAfter)
        && !u.Running;

    /// <summary>Whether a table is behind on <paramref name="kind" /> at <paramref name="at" />, before persistence is considered.</summary>
    internal static bool IsBehind(TableFacts table, VacuumBehindKind kind, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(table);
        return IsBehind(UnitOf(table, kind), at);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(VacuumFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        int watching = 0;

        foreach (TableFacts table in facts.Tables)
        {
            foreach (VacuumBehindKind kind in (VacuumBehindKind[]) [VacuumBehindKind.DeadRows, VacuumBehindKind.Inserts])
            {
                if (!IsBehind(table, kind, facts.ReadAt))
                {
                    continue;
                }

                string series = (kind == VacuumBehindKind.DeadRows ? BehindSeries : InsertsBehindSeries) + table.Key;
                DateTimeOffset? since = TableHistory.HeldSince(evaluation.History, SweptSeries, series);
                TimeSpan held = since is { } start ? facts.ReadAt - start : TimeSpan.Zero;

                if (!table.AutovacuumOff && held < PersistFor)
                {
                    watching++;
                    continue;
                }

                findings.Add(Finding(table, kind, held, facts, evaluation));
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(watching == 0
            ? null
            : "Watching " + watching.ToString(CultureInfo.InvariantCulture) + " table" + (watching == 1 ? string.Empty : "s")
              + " past twice " + (watching == 1 ? "its" : "their") + " own autovacuum trigger: a finding needs the condition to hold for "
              + TableText.Span(PersistFor) + ", because autovacuum usually catches up within a minute of a batch delete or a bulk load.");
    }

    private static HealthFinding Finding(TableFacts table, VacuumBehindKind kind, TimeSpan held, VacuumFacts facts, HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        AutovacuumUnit unit = UnitOf(table, kind)!;
        bool partitioned = table.Row.IsPartitioned;
        bool dead = kind == VacuumBehindKind.DeadRows;

        string parameter = dead ? "autovacuum_vacuum_scale_factor" : "autovacuum_vacuum_insert_scale_factor";
        double? lower = Autovacuum.LowerScaleFactor(unit.ScaleFactor);
        double times = unit.Trigger > 0 ? unit.Counter / unit.Trigger : double.PositiveInfinity;
        bool critical = times > CriticalTimesTrigger || table.AutovacuumOff;

        string trigger = Autovacuum.Explain(
            unit,
            dead ? "autovacuum_vacuum" : "autovacuum_vacuum_insert",
            table.Row.StorageOptions,
            partitioned,
            dead ? table.Row.VacuumMaxThreshold : null,
            dead ? null : table.Row.UnfrozenShare);

        string what = partitioned ? "A partition of " + declared.Key : declared.Key;
        string counted = dead
            ? TableText.Count(unit.Counter) + " dead rows"
            : TableText.Count(unit.Counter) + " rows inserted since its last vacuum";

        string vacuumed = unit.Last is { } last
            ? "was last vacuumed " + TableText.Span(facts.ReadAt - last) + " ago"
            : "has never been vacuumed";

        HealthRemedy? remedy = lower is { } suggested
            ? Autovacuum.Remedy(
                evaluation.TableRemedies,
                table,
                parameter,
                Autovacuum.Number(suggested),
                "Its " + (dead ? "dead-row" : "insert") + " trigger is now " + TableText.Count(Autovacuum.Rounded(unit.Trigger))
                + (partitioned ? " on the partition furthest behind" : string.Empty) + "; at " + Autovacuum.Number(suggested) + " it would be "
                + TableText.Count(Autovacuum.Rounded(unit.Threshold + (suggested * unit.Tuples * (dead ? 1 : table.Row.UnfrozenShare))))
                + ", so autovacuum vacuums the table in smaller, more frequent passes.")
            : table.AutovacuumOff ? Autovacuum.Remedy(evaluation.TableRemedies, table, parameter, Autovacuum.Number(unit.ScaleFactor), "Autovacuum is switched off for it.")
            : null;

        List<HealthEvidence> evidence =
        [
            new("Table", declared.Describe()),
            new(dead ? "Dead rows" : "Inserted since the last vacuum", TableText.Count(unit.Counter)
                + (partitioned ? " in the partition furthest behind" : string.Empty)),
            new(dead ? "Its dead-row trigger" : "Its insert trigger", trigger),
            new("Past its trigger", times.ToString("0.#", CultureInfo.InvariantCulture) + " times"),
            new("Last vacuum (either kind)", TableText.When(unit.Last)),
        ];

        if (partitioned)
        {
            evidence.Add(new HealthEvidence("Partitions past their trigger", TableText.Count(dead ? table.Row.PastVacuumTrigger : table.Row.PastInsertTrigger)
                + " of " + TableText.Count(table.Row.PartitionLeaves)));
        }
        else
        {
            evidence.Add(new HealthEvidence("Last autovacuum", TableText.When(table.Row.LastAutovacuum)));
        }

        evidence.Add(new HealthEvidence("Storage parameters", table.Row.StorageOptions.Count == 0 ? "none" : string.Join(", ", table.Row.StorageOptions)));
        evidence.Add(new HealthEvidence("Being vacuumed now", facts.ProgressHidden is null ? "no" : "not known - this role cannot see autovacuum's progress"));

        List<string> caveat =
        [
            "Counters and vacuum times are the statistics collector's, which a pg_stat_reset() or a crash clears; one read just after a "
            + "manual VACUUM can be inflated, because the backend's pending counts reach the collector after the VACUUM reset them.",
            held > TimeSpan.Zero ? "Seen for " + TableText.Span(held) + "." : "Seen in this sweep.",
        ];

        if (facts.ProgressHidden is { } hidden)
        {
            caveat.Add("It may be under way now: " + hidden);
        }

        caveat.Add("A long-running transaction stops vacuum removing dead rows even when it runs (T14), and busy workers (T16) delay it.");

        return new HealthFinding
        {
            Code = "T07",
            Variant = dead ? DeadRowsVariant : InsertsVariant,
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            Title = what + " has " + counted + ", " + times.ToString("0.#", CultureInfo.InvariantCulture) + " times the "
                + TableText.Count(Autovacuum.Rounded(unit.Trigger)) + " that trigger its autovacuum, and " + vacuumed,
            WhyItMatters =
                (dead
                    ? "A dead row is an old version an update or a delete left behind, and only vacuum makes its space reusable. Until it "
                      + "runs, every sequential scan reads the dead rows, every index still points at them, the table and its indexes keep "
                      + "growing, and the visibility map that index-only scans need goes stale."
                    : "An append-only table - an event log such as mt_events, a history table - leaves no dead rows, so autovacuum visits it "
                      + "only for its inserts: that pass sets the visibility map that index-only scans read, and freezes pages while they are "
                      + "still in memory, so the anti-wraparound vacuum that must come for every table later is cheap rather than a scan of "
                      + "everything at once.")
                + " Autovacuum acts on its own trigger, " + TableText.Count(Autovacuum.Rounded(unit.Trigger)) + " for this table, and this one is "
                + "more than twice past it, so autovacuum is not keeping up."
                + (lower is null && !table.AutovacuumOff
                    ? " Its scale factor is already " + Autovacuum.Number(unit.ScaleFactor) + ", so a lower one is not the answer: what keeps "
                      + "autovacuum from the table is elsewhere - workers busy elsewhere (T16), its cost limit, or a transaction holding the "
                      + "horizon (T14)."
                    : string.Empty)
                + Autovacuum.State(table, parameter, Autovacuum.Number(lower ?? unit.ScaleFactor), evaluation.Text),
            Evidence = evidence,
            Caveat = string.Join(" ", caveat),
            Remedy = remedy,
        };
    }
}
