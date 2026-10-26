using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T24 reads: every judged table's storage parameters and its oldest transaction-id age.</summary>
/// <param name="Tables">The judged tables that exist.</param>
internal sealed record AutovacuumDisabledFacts(IReadOnlyList<TableFacts> Tables);

/// <summary>
/// T24: a table - or its TOAST table, or some of its partitions - has autovacuum switched off by its own storage parameter
/// (<c>autovacuum_enabled = false</c>), so nothing vacuums or analyses it but the forced anti-wraparound vacuum (issue #8 G2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reads</b> is T07's table read (<c>HealthTableQueries.TablesSql</c>): the reloption on the table and on its TOAST
/// table (<c>toast.autovacuum_enabled</c>), and, for a partitioned table, on each partition - counted, never named. The
/// server's own <c>autovacuum = off</c> is S02's, Critical there; a table here is about one table's switch.
/// </para>
/// <para>
/// <b>Severity.</b> Warning; Critical once the table's oldest transaction-id age (its own, its TOAST table's and its
/// partitions' <c>age(relfrozenxid)</c>, the same age T12 reads) is past half of <c>autovacuum_freeze_max_age</c> - the
/// forced vacuum that is all such a table gets is then on its way, and it scans the whole table at once.
/// </para>
/// </remarks>
[HealthCheck("T24", HealthArea.Tables, HealthCostTier.Catalog, "Autovacuum switched off for a table", Gate = HealthGate.Database)]
internal sealed class AutovacuumDisabledCheck : HealthCheck<AutovacuumDisabledFacts>
{
    /// <inheritdoc />
    public override async Task<AutovacuumDisabledFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        new(await HealthTables.TablesAsync(context).ConfigureAwait(false));

    /// <summary>Whether the table, its TOAST table or a partition of it has autovacuum switched off by its own storage parameter.</summary>
    internal static bool SwitchedOff(TableFacts table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Row.AutovacuumOffLeaves > 0 || table.Row.ToastAutovacuumOffLeaves > 0;
    }

    /// <summary>Whether its oldest transaction-id age is past half of <c>autovacuum_freeze_max_age</c>.</summary>
    internal static bool NearForcedVacuum(TableFacts table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Row.OldestXidAge is { } age && age > table.Row.FreezeMaxAge / 2;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AutovacuumDisabledFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [.. facts.Tables.Where(SwitchedOff).Select(x => Finding(x, evaluation))];

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(facts.Tables.Count == 0
                ? null
                : "No judged table has autovacuum switched off by its own storage parameter, on itself, its TOAST table or a partition.");
    }

    private static HealthFinding Finding(TableFacts table, HealthEvaluation evaluation)
    {
        StudioTable declared = table.Table;
        HealthTableRow row = table.Row;
        bool partitioned = row.IsPartitioned;
        bool heapOff = row.AutovacuumOffLeaves > 0;
        bool toastOff = row.ToastAutovacuumOffLeaves > 0;
        bool critical = NearForcedVacuum(table);

        string where = (partitioned, heapOff) switch
        {
            (true, true) => SessionText.Count(row.AutovacuumOffLeaves, "partition") + " of " + TableText.Count(row.PartitionLeaves)
                + (toastOff ? ", and the TOAST tables of " + SessionText.Count(row.ToastAutovacuumOffLeaves, "partition") : string.Empty),
            (true, false) => "the TOAST tables of " + SessionText.Count(row.ToastAutovacuumOffLeaves, "partition"),
            (false, true) => toastOff ? "the table and its TOAST table" : "the table",
            (false, false) => "its TOAST table",
        };

        HealthRemedy? remedy = evaluation.TableRemedies is not { } remedies || !row.AutovacuumEnabled
            ? null
            : heapOff
                ? remedies.AutovacuumSetting(declared, "autovacuum_enabled", "true",
                    "Switches autovacuum back on for this table: it is then vacuumed and analysed when its own triggers say so, not only "
                    + "when wraparound forces it.")
                : remedies.ReenableToastAutovacuum(declared);

        List<HealthEvidence> evidence =
        [
            new("Table", declared.Describe()),
            new("Switched off for", where),
            new("Storage parameters", row.StorageOptions.Count == 0 ? "none" : string.Join(", ", row.StorageOptions)),
        ];

        if (!partitioned)
        {
            evidence.Add(new HealthEvidence("TOAST storage parameters", row.ToastStorageOptions.Count == 0 ? "none" : string.Join(", ", row.ToastStorageOptions)));
        }

        evidence.Add(new HealthEvidence("Oldest transaction-id age", row.OldestXidAge is { } age
            ? Wraparound.Number(age) + " of autovacuum_freeze_max_age " + Wraparound.Number(row.FreezeMaxAge)
              + (critical ? " - past half of it" : string.Empty)
            : "not read"));

        return new HealthFinding
        {
            Code = "T24",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Subject = declared.Key,
            DocumentTypes = declared.DocumentTypes,
            GatedSchemas = declared.GatedSchemas,
            Title = "Autovacuum is switched off for " + (partitioned, heapOff) switch
                {
                    (true, true) => TableText.Count(row.AutovacuumOffLeaves) + " of the " + TableText.Count(row.PartitionLeaves) + " partitions of " + declared.Key,
                    (true, false) => "the TOAST tables of " + SessionText.Count(row.ToastAutovacuumOffLeaves, "partition") + " of " + declared.Key,
                    (false, true) => declared.Key,
                    (false, false) => "the TOAST table of " + declared.Key,
                }
                + (critical ? ", and its transaction-id age is past half of autovacuum_freeze_max_age" : string.Empty),
            WhyItMatters =
                "Its storage parameter autovacuum_enabled = false" + (toastOff && !heapOff ? " (toast.autovacuum_enabled, on the TOAST table that holds its large values)" : string.Empty)
                + " keeps autovacuum away: dead rows are never cleared, statistics never refreshed, the visibility map never set - and the only "
                + "vacuum that still comes is the forced one at autovacuum_freeze_max_age, which scans the whole table at once and cannot be "
                + "cancelled. A table switched off for a bulk load is a common one to forget to switch back."
                + (critical ? " That forced vacuum is already more than half way here." : string.Empty)
                + (!row.AutovacuumEnabled ? " The server's own autovacuum is off as well (S02), so no table setting brings it back alone." : string.Empty),
            Evidence = evidence,
            Caveat = "Read in pg_class.reloptions, of the table, its TOAST table" + (partitioned ? " and each partition (counted, never named)" : string.Empty)
                + ". The transaction-id age is the one T12 reads.",
            Remedy = remedy,
        };
    }
}
