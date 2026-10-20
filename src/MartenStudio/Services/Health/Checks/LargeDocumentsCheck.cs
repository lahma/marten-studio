using System.Diagnostics;
using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One document table T09 looked at: the catalog's row, and what its sample measured or why it was not sampled.</summary>
/// <param name="Table">The table and its catalog facts.</param>
/// <param name="Sample">The sample, or <see langword="null" /> when the table was not sampled.</param>
/// <param name="Skipped">Why it was not sampled, when it was not.</param>
internal sealed record DocumentSizeFacts(TableFacts Table, DocumentSizeSampleRow? Sample, string? Skipped);

/// <summary>What T09 read.</summary>
/// <param name="Tables">Every visible document table, sampled or not.</param>
/// <param name="StoppedEarly">Whether the time budget ran out before every candidate was sampled.</param>
internal sealed record LargeDocumentsFacts(IReadOnlyList<DocumentSizeFacts> Tables, bool StoppedEarly);

/// <summary>
/// T09: the documents of a collection are large - a deeper check, behind "Run deeper checks".
/// </summary>
/// <remarks>
/// <para>
/// <b>A bounded data read</b>, so tier 2 (plan §3.3): cached for five minutes, never run by the regular sweep,
/// and given no history. Each sampled table costs one statement - <c>TABLESAMPLE SYSTEM</c> over enough of its
/// pages for about <see cref="TargetRows" /> rows, never more than that many - and the whole check stops
/// sampling after <see cref="SampleBudget" />, largest tables first, at most <see cref="MaxSampledTables" />
/// of them. It reads <c>pg_column_size(data)</c>, the stored size, from the TOAST pointer: nothing is
/// decompressed and nothing is sent back but three numbers.
/// </para>
/// <para>
/// <b>Never sampled:</b> a hidden document type's table (it is not in the set, and its name would be in the
/// statement's text), a partitioned table (a sample would lock every partition at once), and a table with
/// fewer than <see cref="MinRows" /> rows - one enormous document is a document, not a pattern; the sample
/// demo's two-megabyte media asset is exactly that. The TOAST share comes from <c>pg_class</c> for every
/// table, sampled or not.
/// </para>
/// </remarks>
[HealthCheck("T09", HealthArea.Tables, HealthCostTier.BoundedData, "Large documents")]
internal sealed class LargeDocumentsCheck : HealthCheck<LargeDocumentsFacts>
{
    /// <summary>Rows below which a table is not sampled.</summary>
    public const long MinRows = 1_000;

    /// <summary>How many rows a sample aims for, and never exceeds.</summary>
    public const int TargetRows = 2_000;

    /// <summary>Sampled rows below which the percentile is not judged.</summary>
    public const int MinSampledRows = 100;

    /// <summary>The most tables one run samples.</summary>
    public const int MaxSampledTables = 20;

    /// <summary>A 95th-percentile stored size above which the check speaks (Info).</summary>
    public const long LargeBytes = 64 * 1024;

    /// <summary>A 95th-percentile stored size above which the finding is a Warning.</summary>
    public const long VeryLargeBytes = 512 * 1024;

    /// <summary>How long the check may spend sampling before it stops with what it has.</summary>
    public static readonly TimeSpan SampleBudget = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public override async Task<LargeDocumentsFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<TableFacts> tables = await MartenTables.TablesAsync(context).ConfigureAwait(false);

        List<TableFacts> documents = [.. tables
            .Where(static x => x.Table.Kind == MartenTableKind.Document)
            .OrderByDescending(static x => x.Row.HeapBytes + x.Row.ToastBytes)
            .ThenBy(static x => x.Key, StringComparer.Ordinal)];

        List<DocumentSizeFacts> facts = new(documents.Count);
        long started = Stopwatch.GetTimestamp();
        int sampled = 0;
        bool stoppedEarly = false;

        foreach (TableFacts table in documents)
        {
            string? skipped = table.Row.IsPartitioned
                ? "partitioned: a sample would lock every partition"
                : table.Rows < MinRows
                    ? "fewer than " + MinRows.ToString("N0", CultureInfo.InvariantCulture) + " rows"
                    : sampled >= MaxSampledTables
                        ? "past the " + MaxSampledTables.ToString(CultureInfo.InvariantCulture) + " largest tables"
                        : Stopwatch.GetElapsedTime(started) > SampleBudget
                            ? "the sampling budget ran out"
                            : null;

            if (skipped is not null)
            {
                stoppedEarly |= skipped == "the sampling budget ran out";
                facts.Add(new DocumentSizeFacts(table, null, skipped));
                continue;
            }

            IReadOnlyList<DocumentSizeSampleRow> rows = await context.Sql
                .QueryAsync(
                    HealthTableQueries.DocumentSizeSample(table.Table.Schema, table.Table.Table, PercentFor(table.Rows), TargetRows),
                    HealthTableQueries.ReadDocumentSizeSample,
                    cancellationToken)
                .ConfigureAwait(false);

            sampled++;
            facts.Add(new DocumentSizeFacts(table, rows.Count == 0 ? null : rows[0], rows.Count == 0 ? "the sample read nothing" : null));
        }

        return new LargeDocumentsFacts(facts, stoppedEarly);
    }

    /// <summary>
    /// The share of a table's pages to sample for about <see cref="TargetRows" /> rows: twice the target's share
    /// of the rows, because <c>SYSTEM</c> sampling picks whole pages and the limit trims the excess.
    /// </summary>
    internal static float PercentFor(long rows) =>
        rows <= 0 ? 100f : (float) Math.Clamp(100.0 * 2 * TargetRows / rows, 0.01, 100.0);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(LargeDocumentsFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];

        foreach (DocumentSizeFacts table in facts.Tables)
        {
            if (table.Sample is not { Sampled: >= MinSampledRows, P95Bytes: { } p95 } sample || p95 < LargeBytes)
            {
                continue;
            }

            findings.Add(Finding(table.Table, sample, p95));
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        int sampled = facts.Tables.Count(static x => x.Sample is not null);
        return HealthVerdict.Quiet(
            "Sampled " + sampled.ToString(CultureInfo.InvariantCulture) + " document table" + (sampled == 1 ? string.Empty : "s")
            + (facts.StoppedEarly ? "; the sampling budget ran out before the rest." : "."));
    }

    private static HealthFinding Finding(TableFacts table, DocumentSizeSampleRow sample, double p95)
    {
        long heapAndToast = table.Row.HeapBytes + table.Row.ToastBytes;
        string toastShare = heapAndToast > 0 ? TableText.Percent((double) table.Row.ToastBytes / heapAndToast) : "n/a";

        return new HealthFinding
        {
            Code = "T09",
            Severity = p95 >= VeryLargeBytes ? HealthSeverity.Warning : HealthSeverity.Info,
            Subject = table.Key,
            DocumentTypes = table.Table.DocumentTypes,
            GatedSchemas = table.Table.GatedSchemas,
            Title = "One in twenty " + table.Table.TypeName + " documents is stored at " + TableText.Bytes((long) p95) + " or more",
            WhyItMatters =
                "Marten reads and writes a document whole. Every load deserializes all of it, every upsert writes all of it again "
                + "- into TOAST, compressed, in two-kilobyte chunks - and leaves the old version for vacuum, and a list or a "
                + "projection that needs one field still fetches the rest. A document that keeps growing (an order with every "
                + "line ever added, an aggregate with its whole history) is usually two things that want to be stored apart.",
            Evidence =
            [
                new HealthEvidence("Table", table.Table.Describe()),
                new HealthEvidence("Rows sampled", sample.Sampled.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("95th percentile, stored", TableText.Bytes((long) p95)),
                new HealthEvidence("Largest in the sample", sample.MaxBytes is { } max ? TableText.Bytes(max) : "n/a"),
                new HealthEvidence("Share of the table in TOAST", toastShare),
            ],
            Caveat =
                "From a TABLESAMPLE SYSTEM read of about " + TargetRows.ToString("N0", CultureInfo.InvariantCulture)
                + " rows, which samples whole pages: documents written together are sampled together. The size is the stored, "
                + "compressed one; the JSON a load deserializes is larger.",
        };
    }
}
