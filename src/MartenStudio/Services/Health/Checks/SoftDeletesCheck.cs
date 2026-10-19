using JasperFx.MultiTenancy;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One soft-deleted collection, and what the statistics say of its <c>mt_deleted</c> column.</summary>
/// <param name="Table">The declared table.</param>
/// <param name="Share">What <see cref="HealthTableQueries.SoftDeleteShares" /> read, or <see langword="null" /> when the table does not exist yet.</param>
internal sealed record SoftDeleteFacts(MartenTable Table, SoftDeleteShareRow? Share);

/// <summary>What T10 read.</summary>
/// <param name="Tables">Every visible soft-deleted document type's table.</param>
internal sealed record SoftDeletesFacts(IReadOnlyList<SoftDeleteFacts> Tables);

/// <summary>
/// T10: a soft-deleted collection is mostly deleted documents.
/// </summary>
/// <remarks>
/// <para>
/// Only for a document type whose <c>DeleteStyle</c> is soft - read off the mapping, as AGENTS.md hard rule 10
/// says, because only then does the table have <c>mt_deleted</c> at all. The share comes from
/// <c>pg_stats</c>' most-common-value frequencies for that boolean column: no scan, no count, and as fresh as
/// the last <c>ANALYZE</c>. A role that may not read the column sees no statistics, and the check says it
/// cannot see rather than that there are none.
/// </para>
/// <para>
/// Info: soft deletes are a choice, and a table that keeps its deleted rows on purpose is working as designed.
/// What the finding adds is the cost - every query filters the deleted rows out of every page it reads - and
/// the purge job, which is a few lines of the host's own code.
/// </para>
/// </remarks>
[HealthCheck("T10", HealthArea.Tables, HealthCostTier.Catalog, "Soft deletes accumulating")]
internal sealed class SoftDeletesCheck : HealthCheck<SoftDeletesFacts>
{
    /// <summary>The deleted share above which the check speaks.</summary>
    public const double MaxDeletedShare = 0.3;

    /// <summary>Estimated rows below which the share is not worth a finding.</summary>
    public const double MinRows = 1_000;

    /// <inheritdoc />
    public override async Task<SoftDeletesFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        MartenTableSet set = await MartenTables.SetAsync(context).ConfigureAwait(false);
        List<MartenTable> softDeleted = [.. set.Tables.Where(static x => x.Kind == MartenTableKind.Document && x.SoftDeleted)];

        if (softDeleted.Count == 0)
        {
            return new SoftDeletesFacts([]);
        }

        IReadOnlyList<SoftDeleteShareRow> rows = await context.Sql
            .QueryAsync(
                HealthTableQueries.SoftDeleteShares([.. softDeleted.Select(static x => x.Target)]),
                HealthTableQueries.ReadSoftDeleteShare,
                cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, SoftDeleteShareRow> byKey = rows.ToDictionary(
            static x => x.Schema + "." + x.Table,
            StringComparer.OrdinalIgnoreCase);

        return new SoftDeletesFacts([.. softDeleted.Select(x => new SoftDeleteFacts(x, byKey.GetValueOrDefault(x.Key)))]);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(SoftDeletesFacts facts, HealthEvaluation evaluation)
    {
        if (facts.Tables.Count == 0)
        {
            return HealthVerdict.Quiet("No document type this store shows is soft-deleted.");
        }

        List<HealthFinding> findings = [];
        int unseen = 0;

        foreach (SoftDeleteFacts table in facts.Tables)
        {
            if (table.Share is not { } share || share.EstimatedRows < MinRows)
            {
                continue;
            }

            if (share.DeletedShare is not { } deleted)
            {
                unseen++;
                continue;
            }

            if (deleted > MaxDeletedShare)
            {
                findings.Add(Finding(table.Table, share, deleted));
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(unseen == 0
            ? null
            : "Postgres has no statistics this role may read for mt_deleted on "
              + unseen.ToString(System.Globalization.CultureInfo.InvariantCulture)
              + " soft-deleted table" + (unseen == 1 ? string.Empty : "s")
              + " - never analysed, or a role without SELECT on the column - so their deleted share cannot be seen.");
    }

    private static HealthFinding Finding(MartenTable table, SoftDeleteShareRow share, double deleted)
    {
        string type = table.TypeName ?? table.Table;
        bool conjoined = table.DocumentType?.TenancyStyle == TenancyStyle.Conjoined;

        return new HealthFinding
        {
            Code = "T10",
            Severity = HealthSeverity.Info,
            Subject = table.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = TableText.Percent(deleted) + " of the " + type + " documents are soft-deleted",
            WhyItMatters =
                "A soft delete keeps the row and sets mt_deleted. Every query Marten runs on the collection adds "
                + "\"mt_deleted = false\" and still reads the deleted rows' pages to skip them, the table and its indexes keep "
                + "their size, and each backup copies them. If the deleted documents are kept for an audit, a purge after the "
                + "retention period keeps that and drops the cost.",
            Evidence =
            [
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Soft-deleted share", TableText.Percent(deleted)),
                new HealthEvidence("Estimated rows", TableText.Rows((long) share.EstimatedRows)),
            ],
            Caveat =
                "From pg_stats' most-common-value frequency for mt_deleted, as of the last ANALYZE"
                + (share.RowsWithStatistics < share.EstimatedRows
                    ? ", over the part of the table that has statistics."
                    : ".")
                + " No row was read to measure it.",
            Remedy = new HealthRemedy(
                "// A purge job, on whatever schedule suits: hard-delete what was soft-deleted more than 90 days ago.\n"
                + (conjoined ? "// The collection is multi-tenanted: run it in a session per tenant.\n" : string.Empty)
                + "await using var session = store.LightweightSession();\n"
                + "session.HardDeleteWhere<" + type + ">(x => x.DeletedBefore(DateTimeOffset.UtcNow.AddDays(-90)));\n"
                + "await session.SaveChangesAsync();",
                "DeletedBefore and HardDeleteWhere are Marten's own (Marten.Linq.SoftDeletes); a hard delete reaches the "
                + "soft-deleted rows, which is the point of it. The space comes back to the table when vacuum next runs."),
        };
    }
}
