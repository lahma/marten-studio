using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Schema;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T25 reads: the indexes on the judged tables, from the sweep's one index read.</summary>
/// <param name="Indexes">The indexes but the locked tables'.</param>
internal sealed record BloatedIndexFacts(IReadOnlyList<IndexFacts> Indexes)
{
    /// <summary>The tables locked for a schema change, which are not judged this sweep (<see cref="HealthIndexRead.LockedTables" />).</summary>
    public IReadOnlySet<string> LockedTables { get; init; } = HealthIndexRead.Empty.LockedTables;
}

/// <summary>How big a b-tree index should be for its entries, and how far past that it is - a lock-free estimate (T25).</summary>
/// <param name="Bytes">Its size, from <c>relpages</c>.</param>
/// <param name="Entries">Its <c>reltuples</c>.</param>
/// <param name="EntryBytes">What one entry costs, packed: the tuple, aligned, and its line pointer.</param>
/// <param name="ExpectedBytes">What the entries would take at the index's fillfactor.</param>
/// <param name="IssueRuleRatio">The size against <c>reltuples × 64</c> bytes - the issue's rule.</param>
/// <param name="KeyWidthRatio">The size against <see cref="ExpectedBytes" /> - the key-width rule.</param>
internal sealed record IndexBloatEstimate(long Bytes, double Entries, double EntryBytes, double ExpectedBytes, double IssueRuleRatio, double KeyWidthRatio);

/// <summary>
/// T25: a b-tree index of 64 MiB or more that is mostly empty space, by a lock-free estimate from the catalog (issue #8 G4) -
/// the issue's own rule at Warning and a key-width-scaled screen at Info, each behind a constant of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>The issue's rule</b> (<see cref="IssueRuleEnabled" />): at least 64 MiB and at least <see cref="IssueRuleFactor" /> times
/// <c>reltuples × 64</c> bytes - Warning, variant <c>issue-rule</c>. It misses a narrow key: a 95%-empty <c>int4</c> primary
/// key measured 7.1 times, a dense one 0.4 (B0 G4), because a dense <c>int4</c> entry costs about 23 bytes, not 64.
/// </para>
/// <para>
/// <b>The key-width screen</b> (<see cref="KeyWidthRuleEnabled" />): at least <see cref="KeyWidthFactor" /> times what the entries
/// would take packed - Info, variant <c>key-width</c>. One entry is its tuple header and keys aligned to eight bytes, plus a
/// four-byte line pointer (<see cref="EntryBytesFor" />), at the index's fillfactor (90 for a b-tree). Calibrated live on 17:
/// a dense <c>int4</c>, <c>int8</c> or <c>(int4, int4)</c> primary key of 200,000 rows measured 22.6 bytes an entry against
/// 22.2 expected; random <c>uuid</c> keys 43.7 against 31.1, and random 36-character text keys 79.9 against 48.9 (page splits
/// leave leaves part-full); the 95%-empty <c>int4</c> key 451 against 22.2 - 20 times, where the issue's rule says 7.1. A
/// variable-width key is taken at its <c>pg_stats.avg_width</c> where the reading role may read it, and at 32 bytes where not.
/// </para>
/// <para>
/// <b>Estimates, both.</b> Sizes and entries are <c>relpages</c> and <c>reltuples</c> as of the last <c>VACUUM</c> or
/// <c>ANALYZE</c>; a duplicate-heavy index deduplicates (13+) and reads small. The evidence says so, and points at the
/// Maintenance page's <c>pgstatindex</c> measure, which reads the index itself. B-trees only: the catalog says nothing honest
/// about a GIN index's entries.
/// </para>
/// <para>
/// <b>Not index design</b>, so a library's table is judged too (issue #8 G1: size rules apply), and so is a primary key.
/// </para>
/// </remarks>
[HealthCheck("T25", HealthArea.Tables, HealthCostTier.Catalog, "Bloated index", Variants = [IssueRuleVariant, KeyWidthVariant], Gate = HealthGate.Database)]
internal sealed class BloatedIndexCheck : HealthCheck<BloatedIndexFacts>
{
    /// <summary>The variant of the issue's own rule, at Warning.</summary>
    public const string IssueRuleVariant = "issue-rule";

    /// <summary>The variant of the key-width screen, at Info.</summary>
    public const string KeyWidthVariant = "key-width";

    /// <summary>Whether the issue's rule runs. W5-index §7.2: a one-line change drops it.</summary>
    public const bool IssueRuleEnabled = true;

    /// <summary>Whether the key-width screen runs. W5-index §7.2: a one-line change drops it.</summary>
    public const bool KeyWidthRuleEnabled = true;

    /// <summary>The smallest index judged (issue #8 G4).</summary>
    public const long MinIndexBytes = 64L * 1024 * 1024;

    /// <summary>The issue's bytes per entry.</summary>
    public const double IssueRuleBytesPerEntry = 64;

    /// <summary>The issue's multiple of <see cref="IssueRuleBytesPerEntry" />.</summary>
    public const double IssueRuleFactor = 10;

    /// <summary>The key-width screen's multiple of the packed size.</summary>
    public const double KeyWidthFactor = 4;

    /// <summary>The width a variable-width key column is taken at when its statistics are not readable.</summary>
    public const int VariableWidthGuess = 32;

    /// <summary>A b-tree's default leaf fillfactor.</summary>
    public const int DefaultFillFactor = 90;

    /// <inheritdoc />
    public override async Task<BloatedIndexFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);
        return new BloatedIndexFacts(read.Indexes) { LockedTables = read.LockedTables };
    }

    /// <summary>
    /// What one entry of an index on these keys costs, packed: an eight-byte tuple header and the keys, aligned to eight
    /// bytes, and a four-byte line pointer - a fixed-width key at its <c>attlen</c>, a variable-width one at its average width
    /// where known and <see cref="VariableWidthGuess" /> where not.
    /// </summary>
    internal static double EntryBytesFor(IReadOnlyList<short> widths, IReadOnlyList<int> averageWidths)
    {
        ArgumentNullException.ThrowIfNull(widths);
        ArgumentNullException.ThrowIfNull(averageWidths);

        long keys = 0;

        for (int i = 0; i < widths.Count; i++)
        {
            keys += widths[i] > 0
                ? widths[i]
                : i < averageWidths.Count && averageWidths[i] > 0 ? averageWidths[i] : VariableWidthGuess;
        }

        long tuple = 8 + keys;
        long aligned = (tuple + 7) / 8 * 8;
        return aligned + 4;
    }

    /// <summary>The estimate for one index, or <see langword="null" /> when it cannot be made: entries unknown, or not a b-tree.</summary>
    internal static IndexBloatEstimate? Estimate(HealthIndexRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!string.Equals(row.AccessMethod, "btree", StringComparison.Ordinal) || row.EstimatedTuples is not { } entries || entries < 0)
        {
            return null;
        }

        int fillFactor = StorageParameters.Parse(row.IndexOptions).TryGetValue("fillfactor", out string? text)
                         && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int own) && own is >= 10 and <= 100
            ? own
            : DefaultFillFactor;

        double entryBytes = EntryBytesFor(row.KeyWidths, row.KeyAverageWidths);

        // One page at least: an index of 64 MiB with no entries is all empty space, and is judged as such.
        double expected = Math.Max(entries * entryBytes / (fillFactor / 100d), 8192);
        double issue = Math.Max(entries * IssueRuleBytesPerEntry, 8192);

        return new IndexBloatEstimate(row.Bytes, entries, entryBytes, expected, row.Bytes / issue, row.Bytes / expected);
    }

    /// <summary>Which variant an estimate is, or <see langword="null" /> for none: the issue's rule first, then the screen.</summary>
    internal static string? VariantOf(IndexBloatEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);

        if (estimate.Bytes < MinIndexBytes)
        {
            return null;
        }

        if (IssueRuleEnabled && estimate.IssueRuleRatio >= IssueRuleFactor)
        {
            return IssueRuleVariant;
        }

        return KeyWidthRuleEnabled && estimate.KeyWidthRatio >= KeyWidthFactor ? KeyWidthVariant : null;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(BloatedIndexFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];

        foreach (IndexFacts index in facts.Indexes)
        {
            if (facts.LockedTables.Contains(index.Table.Key) || !index.Row.IsValid || Estimate(index.Row) is not { } estimate || VariantOf(estimate) is not { } variant)
            {
                continue;
            }

            findings.Add(Finding(index, estimate, variant, evaluation));
        }

        string? note = HealthTables.LockedNote(facts.LockedTables);
        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    private static string Times(double ratio) => ratio.ToString("0.0", CultureInfo.InvariantCulture) + "×";

    private static HealthFinding Finding(IndexFacts index, IndexBloatEstimate estimate, string variant, HealthEvaluation evaluation)
    {
        bool issue = variant == IssueRuleVariant;
        string keys = index.Row.KeyTypes.Count == 0
            ? "its keys"
            : string.Join(", ", index.Row.KeyTypes.Select((type, i) => type
                + (i < index.Row.KeyWidths.Count && index.Row.KeyWidths[i] > 0
                    ? " (" + index.Row.KeyWidths[i].ToString(CultureInfo.InvariantCulture) + " B)"
                    : i < index.Row.KeyAverageWidths.Count && index.Row.KeyAverageWidths[i] > 0
                        ? " (~" + index.Row.KeyAverageWidths[i].ToString(CultureInfo.InvariantCulture) + " B on average)"
                        : " (variable, taken at " + VariableWidthGuess.ToString(CultureInfo.InvariantCulture) + " B)")));

        return new HealthFinding
        {
            Code = "T25",
            Severity = issue ? HealthSeverity.Warning : HealthSeverity.Info,
            Variant = variant,
            Subject = index.Key,
            DocumentTypes = index.Table.DocumentTypes,
            GatedSchemas = index.Table.GatedSchemas,
            Title = "Index " + index.Row.Name + " on " + index.Table.Key + " is " + TableText.Bytes(estimate.Bytes) + ", about "
                + Times(estimate.KeyWidthRatio) + " what its " + TableText.Count(estimate.Entries) + " entries need",
            WhyItMatters =
                "A b-tree never gives a page back to the file: after a mass delete or update, VACUUM frees the entries but the "
                + "index keeps its size, and every scan of it still reads the half-empty pages into the buffer cache. "
                + (issue
                    ? "At over " + IssueRuleFactor.ToString(CultureInfo.InvariantCulture) + " times "
                      + IssueRuleBytesPerEntry.ToString(CultureInfo.InvariantCulture) + " bytes an entry it is past the issue's line. "
                    : "Its keys are narrow, so the issue's 64-bytes-an-entry line misses it; packed, its entries would take about "
                      + TableText.Bytes((long) estimate.ExpectedBytes) + ". ")
                + "REINDEX INDEX CONCURRENTLY (PostgreSQL 12+) rebuilds it, packed, under the same name and definition, without "
                + "blocking writes - so a configuration that declares it sees no change.",
            Evidence =
            [
                new HealthEvidence("Index", index.Row.Name),
                new HealthEvidence("Table", index.Table.Describe()),
                new HealthEvidence("Size", TableText.Bytes(estimate.Bytes) + " (relpages)"),
                new HealthEvidence("Entries", TableText.Rows((long) estimate.Entries) + " (reltuples)"),
                new HealthEvidence("Bytes an entry", (estimate.Bytes / Math.Max(estimate.Entries, 1)).ToString("N1", CultureInfo.InvariantCulture)),
                new HealthEvidence("Packed, an entry needs", estimate.EntryBytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes - " + keys + " (INCLUDE columns counted: a leaf entry stores them)"),
                new HealthEvidence("Against the issue's 64 bytes an entry", Times(estimate.IssueRuleRatio)),
                new HealthEvidence("Against its keys packed", Times(estimate.KeyWidthRatio)),
            ],
            Caveat =
                "An estimate from the catalog (relpages and reltuples, as of the last VACUUM or ANALYZE); measure with pgstatindex "
                + "- the Maintenance page's bloat measure - before rebuilding. An index with many duplicate keys deduplicates "
                + "(PostgreSQL 13+) and reads smaller than this; one filled in random key order leaves its leaves part-full, which "
                + "the screen's " + KeyWidthFactor.ToString(CultureInfo.InvariantCulture) + "× allows for.",
            Remedy = evaluation.TableRemedies?.RebuildBloatedIndex(index.Table, index.Row, index.Origin),
            Link = new HealthLink("Measure it on the Maintenance page", "maintenance"),
        };
    }
}
