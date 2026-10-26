using MartenStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What T17 reads: how many document-named tables no store declares, and their size - never their names.</summary>
/// <param name="Tables">How many tables in the store's schemas are named like a document table and no registered store declares.</param>
/// <param name="EstimatedBytes">Their estimated size together, from <c>relpages</c>.</param>
/// <param name="Schemas">How many of the store's own schemas were read.</param>
internal sealed record OrphanedDocumentTablesFacts(int Tables, long EstimatedBytes, int Schemas);

/// <summary>
/// T17: tables in the store's own schemas named like Marten document tables (<c>mt_doc_*</c>) that no store in this process
/// declares - a document type that was renamed or removed, whose table Marten never drops - counted, and never named (R1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Never named.</b> Marten learns a document type lazily: one never registered with <c>Schema.For&lt;T&gt;()</c> that nothing
/// in the process has touched yet is not among <c>AllKnownDocumentTypes()</c>, so its table is indistinguishable here from an
/// orphan - and it may be a type the host hides (<c>IsDocumentTypeVisible</c>, D27's known limit). So the finding is a count
/// and a size, and says it may include such a type. A hidden type the process does know is declared, and never counted.
/// </para>
/// <para>
/// <b>Every registered store's declarations</b> (<see cref="MartenHealthTableSource.EveryStoresDeclarations" />), read with no
/// connection: another store's document table in the same schema is that store's, not an orphan. When they cannot be read,
/// nothing can be told from another store's, and the check says so rather than count.
/// </para>
/// <para>
/// One catalog statement (<see cref="OrphanedDocumentTableQueries.CandidatesSql" />): sizes from <c>relpages</c>, locking
/// nothing it lists. Info: an orphaned table costs disk and backups, nothing else.
/// </para>
/// </remarks>
[HealthCheck("T17", HealthArea.Tables, HealthCostTier.Catalog, "Orphaned document tables")]
internal sealed class OrphanedDocumentTablesCheck : HealthCheck<OrphanedDocumentTablesFacts>
{
    /// <summary>What the status row says when another store's declarations cannot be read.</summary>
    internal const string Unclassified =
        "Not checked: a registered store's configuration could not be read, so a table no store here declares cannot be told "
        + "from one that store declares.";

    /// <inheritdoc />
    public override async Task<OrphanedDocumentTablesFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (MartenHealthTableSource.EveryStoresDeclarations(context) is not { } classifier)
        {
            throw new HealthCheckUnavailableException(Unclassified);
        }

        MartenTableSet declared = await MartenTables.SetAsync(context).ConfigureAwait(false);
        IReadOnlyList<string> schemas = declared.Declarations.Schemas;

        if (schemas.Count == 0)
        {
            return new OrphanedDocumentTablesFacts(0, 0, 0);
        }

        IReadOnlyList<DocumentTableCandidateRow> candidates = await context.Sql
            .QueryAsync(OrphanedDocumentTableQueries.Candidates(schemas), OrphanedDocumentTableQueries.ReadCandidate, cancellationToken)
            .ConfigureAwait(false);

        // Reduced to numbers here: the names never leave the gatherer.
        int count = 0;
        long bytes = 0;

        foreach (DocumentTableCandidateRow candidate in candidates)
        {
            if (classifier.IsUndeclaredDocumentTable(candidate.Schema, candidate.Table))
            {
                count++;
                bytes += candidate.EstimatedBytes;
            }
        }

        return new OrphanedDocumentTablesFacts(count, bytes, schemas.Count);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(OrphanedDocumentTablesFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.Tables == 0)
        {
            return HealthVerdict.Quiet(facts.Schemas == 0 ? "The store declares no schema to read." : null);
        }

        string tables = facts.Tables == 1 ? "1 table" : SessionText.Number(facts.Tables) + " tables";

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T17",
            Severity = HealthSeverity.Info,
            Title = tables + " in the store's schemas " + (facts.Tables == 1 ? "is" : "are") + " named like document tables no store "
                + "here declares, " + TableText.Bytes(facts.EstimatedBytes) + " together",
            WhyItMatters =
                "Marten creates a document type's table and never drops it: a type that was renamed, moved to another schema or "
                + "removed leaves its old table behind, still on disk, in every backup and in every restore. Such a table may also "
                + "be a document type this process has not loaded yet - Marten learns a type the first time something touches it, "
                + "unless it is registered with Schema.For<T>() - which is why none is named here. Confirm each one is no type's "
                + "before dropping it; Marten will not.",
            Evidence =
            [
                new HealthEvidence("Tables", SessionText.Number(facts.Tables)),
                new HealthEvidence("Estimated size", TableText.Bytes(facts.EstimatedBytes) + " (heap, TOAST and indexes, from relpages)"),
                new HealthEvidence("Schemas read", SessionText.Number(facts.Schemas) + " (the store's own)"),
            ],
            Caveat =
                "Compared with every registered store's configuration as this process knows it. Their names are not shown, because "
                + "one may be a document type this process has not loaded yet, which the host may hide; the database browser lists "
                + "the store's mt_ tables to whoever its gate admits.",
        });
    }
}
