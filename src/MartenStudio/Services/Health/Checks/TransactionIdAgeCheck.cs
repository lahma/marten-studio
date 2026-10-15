using System.Globalization;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T12 reads: every declared table's oldest transaction-id age.</summary>
/// <param name="Tables">The declared tables that exist.</param>
internal sealed record TransactionIdAgeFacts(IReadOnlyList<TableFacts> Tables);

/// <summary>
/// T12: Marten tables are far into Postgres' transaction-id range and need freezing.
/// </summary>
/// <remarks>
/// <para>
/// Postgres numbers transactions with 32-bit ids, so every row's id must be "frozen" by vacuum before the
/// counter wraps; <c>age(relfrozenxid)</c> is how far a table is from its last complete freeze. Autovacuum forces
/// an anti-wraparound vacuum at <c>autovacuum_freeze_max_age</c> (200 million by default), and at about 2.1
/// billion Postgres stops accepting writes altogether.
/// </para>
/// <para>
/// <b>The lines are the server's own, not fixed numbers.</b> A table past <c>autovacuum_freeze_max_age</c> is one
/// autovacuum should already be freezing, so the lines sit on the way from that setting to wraparound: a Warning
/// <see cref="WarningShare" /> of the way along, Critical <see cref="CriticalShare" /> - about 1.0 and 1.5
/// billion at the default, and much later on a server whose DBA raised the setting, where a fixed billion would
/// call a table behind before autovacuum had even been asked to freeze it.
/// </para>
/// <para>
/// One finding for the database rather than one per table: tables age together, so a database near the edge
/// would otherwise have a finding for every table it has. The oldest is named, with how many others are past
/// the line. A partitioned table is judged by its oldest partition - never by its own <c>relfrozenxid</c>, which
/// is zero - and its TOAST table counts, because vacuum freezes it separately.
/// </para>
/// </remarks>
[HealthCheck("T12", HealthArea.Tables, HealthCostTier.Catalog, "Transaction-id age")]
internal sealed class TransactionIdAgeCheck : HealthCheck<TransactionIdAgeFacts>
{
    /// <summary>Where the transaction-id counter wraps: <c>2^31</c>, about 2.1 billion.</summary>
    public const long WraparoundAge = 2_147_483_648;

    /// <summary>How far from <c>autovacuum_freeze_max_age</c> to wraparound the oldest table must be for a Warning.</summary>
    public const double WarningShare = 0.4;

    /// <summary>How far from <c>autovacuum_freeze_max_age</c> to wraparound the oldest table must be for a Critical.</summary>
    public const double CriticalShare = 2.0 / 3;

    /// <summary>How many tables past the line the evidence names, beside the oldest.</summary>
    public const int NamedTables = 5;

    /// <summary>The age past which the check warns, given the server's <c>autovacuum_freeze_max_age</c>.</summary>
    internal static long WarningAge(long freezeMaxAge) => Line(freezeMaxAge, WarningShare);

    /// <summary>The age past which the finding is Critical, given the server's <c>autovacuum_freeze_max_age</c>.</summary>
    internal static long CriticalAge(long freezeMaxAge) => Line(freezeMaxAge, CriticalShare);

    private static long Line(long freezeMaxAge, double share)
    {
        long forced = Math.Clamp(freezeMaxAge, 0, WraparoundAge);
        return forced + (long) ((WraparoundAge - forced) * share);
    }

    /// <inheritdoc />
    public override async Task<TransactionIdAgeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        return new TransactionIdAgeFacts(await MartenTables.TablesAsync(context).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TransactionIdAgeFacts facts, HealthEvaluation evaluation)
    {
        List<TableFacts> aged = [.. facts.Tables
            .Where(static x => x.Row.OldestXidAge is not null)
            .OrderByDescending(static x => x.Row.OldestXidAge)
            .ThenBy(static x => x.Key, StringComparer.Ordinal)];

        if (aged.Count == 0)
        {
            return HealthVerdict.Quiet(facts.Tables.Count == 0 ? "No table this store declares exists yet." : null);
        }

        TableFacts oldest = aged[0];
        long age = oldest.Row.OldestXidAge!.Value;
        long freezeMaxAge = oldest.Row.FreezeMaxAge;
        long warning = WarningAge(freezeMaxAge);
        long critical = CriticalAge(freezeMaxAge);

        if (age <= warning)
        {
            return HealthVerdict.Quiet(
                "The oldest Marten table, " + oldest.Key + ", is " + age.ToString("N0", CultureInfo.InvariantCulture)
                + " transactions from its last freeze.");
        }

        List<TableFacts> past = [.. aged.Where(x => x.Row.OldestXidAge > warning)];

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T12",
            Severity = age > critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            DocumentTypes = [.. past.SelectMany(static x => x.Table.DocumentTypes).Distinct()],
            Title = oldest.Key + " is " + Billions(age) + " transactions from its last freeze; Postgres stops accepting writes at about 2.1 billion",
            WhyItMatters =
                "Postgres numbers transactions with 32-bit ids, so vacuum must freeze every row before the counter wraps round. "
                + "Autovacuum forces that long before now - at autovacuum_freeze_max_age, which this server sets to "
                + freezeMaxAge.ToString("N0", CultureInfo.InvariantCulture) + " - so a table this far behind is one autovacuum has "
                + "not managed to freeze: usually because a transaction, a replication slot or a prepared transaction holds the "
                + "oldest transaction id back, or because vacuum keeps being cancelled. At about 2.1 billion Postgres refuses every "
                + "write to the database until a VACUUM has run: a DBA's VACUUM (FREEZE) of these tables now, once whatever holds "
                + "the horizon back is gone, is the cure.",
            Evidence =
            [
                new HealthEvidence("Oldest table", oldest.Table.Describe()),
                new HealthEvidence("Its age(relfrozenxid)", age.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("autovacuum_freeze_max_age", freezeMaxAge.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("Warning and Critical lines", warning.ToString("N0", CultureInfo.InvariantCulture) + " and "
                    + critical.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("Marten tables past the warning line", past.Count.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("The oldest of them", string.Join(", ", past.Take(NamedTables).Select(static x => x.Key
                    + " (" + x.Row.OldestXidAge!.Value.ToString("N0", CultureInfo.InvariantCulture) + ")"))),
                new HealthEvidence("Last autovacuum of the oldest", TableText.When(oldest.Row.LastAutovacuum)),
            ],
            Caveat =
                "Read from pg_class.relfrozenxid of the store's own tables, their TOAST tables and their partitions. The lines sit "
                + TableText.Percent(WarningShare) + " and " + TableText.Percent(CriticalShare) + " of the way from "
                + "autovacuum_freeze_max_age to wraparound. The database's age(datfrozenxid) - which is what wraparound protection "
                + "acts on - also counts every other table in it.",
        });
    }

    private static string Billions(long value) =>
        (value / 1_000_000_000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " billion";
}
