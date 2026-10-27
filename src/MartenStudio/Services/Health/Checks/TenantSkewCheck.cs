using JasperFx.MultiTenancy;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One conjoined table T18 judges, and how its rows spread over <c>tenant_id</c>.</summary>
/// <param name="Table">The table the store declares.</param>
/// <param name="Skew">What <see cref="ColumnSkewQueries.Skew" /> read, or <see langword="null" /> when the table does not exist yet.</param>
internal sealed record TenantSkewTable(MartenTable Table, HealthColumnSkewRow? Skew);

/// <summary>What T18 read.</summary>
/// <param name="Tables">Every conjoined table the store shows: its conjoined document types' tables, and the event store's when it is conjoined.</param>
internal sealed record TenantSkewFacts(IReadOnlyList<TenantSkewTable> Tables)
{
    /// <summary>The role the reads ran as - read only when a table's statistics were hidden from it, for the grant it needs.</summary>
    public string? ReadsAs { get; init; }

    /// <summary>Who runs the server - read with <see cref="ReadsAs" />, for who can grant it.</summary>
    public PostgresPlatform? Platform { get; init; }
}

/// <summary>
/// T18: one tenant holds more than half of a conjoined table's rows (X4, from W5-P1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two shapes.</b> A conjoined table that is not partitioned by tenant is judged by <c>pg_stats</c>: the most common values of
/// <c>tenant_id</c> and their frequencies, weighted over its leaves - as fresh as the last <c>ANALYZE</c>, and visible only to a
/// role that may <c>SELECT</c> the column, never to <c>pg_monitor</c> (B0 §8c). A table Marten partitions by tenant
/// (<c>MultiTenantedWithPartitioning</c>, <c>PartitionMultiTenantedDocumentsUsingMartenManagement</c>) is judged by each tenant
/// partition's estimated rows, which the catalog gives any role - and is rolled up and judged as one table, its partitions
/// never named, because their names are the tenant list (D27). <see cref="ColumnSkewQueries" /> reads both in one lock-free
/// statement.
/// </para>
/// <para>
/// <b>The line.</b> Info, when one tenant holds more than <see cref="MinShare" /> of a table of <see cref="MinRows" /> rows or
/// more with <see cref="MinTenants" /> tenants or more: a store with two tenants is not skewed for having a large one.
/// </para>
/// <para>
/// <b>A tenant is named only past D29's gates.</b> The finding names its tenant (<see cref="HealthFinding.TenantId" />), so it
/// is shown only through the activity gate to a visitor the store policy lets see that tenant, and counted as withheld for
/// everybody else; the check's row note gives a share and never a name. A table whose statistics the role may not read is
/// "not checked", naming the grant (<see cref="PostgresGrant.ColumnSelect" />), when it is every table there is to judge.
/// </para>
/// </remarks>
[HealthCheck("T18", HealthArea.Tables, HealthCostTier.Catalog, "Tenant skew")]
internal sealed class TenantSkewCheck : HealthCheck<TenantSkewFacts>
{
    /// <summary>The column a conjoined table carries its tenant in.</summary>
    public const string TenantColumn = "tenant_id";

    /// <summary>The share of a table's rows above which one tenant is a finding.</summary>
    public const double MinShare = 0.5;

    /// <summary>The fewest estimated rows a table may have and be judged.</summary>
    public const double MinRows = 100_000;

    /// <summary>The fewest tenants a table may have and be judged: with fewer, one being large is the shape of the data.</summary>
    public const double MinTenants = 5;

    /// <summary>Whether a table the store declares carries a tenant in <see cref="TenantColumn" />.</summary>
    internal static bool IsConjoined(MartenTable table, bool conjoinedEvents, string eventSchema)
    {
        ArgumentNullException.ThrowIfNull(table);

        return table.Kind switch
        {
            MartenTableKind.Document => table.DocumentType?.TenancyStyle == TenancyStyle.Conjoined,
            MartenTableKind.EventStore => conjoinedEvents
                && string.Equals(table.Schema, eventSchema, StringComparison.Ordinal)
                && table.Table is "mt_events" or "mt_streams",
            _ => false,
        };
    }

    /// <inheritdoc />
    public override async Task<TenantSkewFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet set = await MartenTables.SetAsync(context).ConfigureAwait(false);
        bool conjoinedEvents = context.StoreOptions.Events.TenancyStyle == TenancyStyle.Conjoined;
        string eventSchema = context.StoreOptions.Events.DatabaseSchemaName;

        List<MartenTable> conjoined = [.. set.Tables.Where(x => IsConjoined(x, conjoinedEvents, eventSchema))];

        if (conjoined.Count == 0)
        {
            // No statement at all for a store with nothing to judge.
            return new TenantSkewFacts([]);
        }

        IReadOnlyList<HealthColumnSkewRow> rows = await context.Sql
            .QueryAsync(ColumnSkewQueries.Skew([.. conjoined.Select(static x => x.Target)], TenantColumn), ColumnSkewQueries.ReadSkew, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, HealthColumnSkewRow> byKey = rows.ToDictionary(static x => x.Key, StringComparer.OrdinalIgnoreCase);
        List<TenantSkewTable> tables = [.. conjoined.Select(x => new TenantSkewTable(x, byKey.GetValueOrDefault(x.Key)))];

        List<TenantSkewTable> large = [.. tables.Where(static x => x.Skew is { Rows: >= MinRows })];
        List<TenantSkewTable> hidden = [.. large.Where(static x => Hidden(x.Skew!))];

        if (hidden.Count == 0)
        {
            return new TenantSkewFacts(tables);
        }

        // Only now who reads, for the sentence that names the grant (A7): both are the sweep's shared reads.
        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        if (hidden.Count == large.Count)
        {
            throw new HealthCheckUnavailableException(PostgresGrants.NotChecked(Grant(hidden), server.Platform, grants.ReadsAs));
        }

        return new TenantSkewFacts(tables) { ReadsAs = grants.ReadsAs, Platform = server.Platform };
    }

    /// <summary>Whether the statistics a table is judged by are hidden from the reading role: none of its leaves' columns is readable.</summary>
    private static bool Hidden(HealthColumnSkewRow skew) => !skew.ByPartition && skew.ReadableLeaves == 0;

    /// <summary>The grant the hidden tables need, naming the first by its ungated name and counting the rest.</summary>
    private static PostgresGrant Grant(List<TenantSkewTable> hidden) =>
        PostgresGrant.ColumnSelect(
            hidden[0].Table.NoteName + (hidden.Count == 1
                ? string.Empty
                : " (and " + TableText.Count(hidden.Count - 1) + " more conjoined table" + (hidden.Count == 2 ? ")" : "s)")),
            TenantColumn);

    /// <summary>The share of <paramref name="skew" />'s rows its largest tenant holds, or <see langword="null" /> when nothing was counted.</summary>
    internal static double? LargestShare(HealthColumnSkewRow skew)
    {
        ArgumentNullException.ThrowIfNull(skew);

        double counted = skew.ByPartition ? skew.RowsCounted + skew.DefaultRows : skew.RowsCounted;
        return counted > 0 && skew.TopRows.Count > 0 ? Math.Min(skew.TopRows[0] / counted, 1) : null;
    }

    /// <summary>
    /// The largest tenant's id: the statistics' value, or the tenant partition's one value - <see langword="null" /> for a
    /// partition that holds several tenants, which is not one tenant's skew.
    /// </summary>
    internal static string? LargestTenant(HealthColumnSkewRow skew)
    {
        ArgumentNullException.ThrowIfNull(skew);

        return skew.TopValues.Count == 0
            ? null
            : skew.ByPartition ? PartitionBoundText.SingleListValue(skew.TopValues[0]) : skew.TopValues[0];
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TenantSkewFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Tables.Count == 0)
        {
            return HealthVerdict.Quiet("No table this store shows is conjoined-tenanted.");
        }

        List<HealthFinding> findings = [];
        List<TenantSkewTable> hidden = [];
        int unanalysed = 0;
        double? largest = null;

        foreach (TenantSkewTable table in facts.Tables)
        {
            if (table.Skew is not { } skew || skew.Rows < MinRows)
            {
                continue;
            }

            if (Hidden(skew))
            {
                hidden.Add(table);
                continue;
            }

            if (LargestShare(skew) is not { } share)
            {
                unanalysed++;
                continue;
            }

            largest = Math.Max(largest ?? 0, share);

            if (share > MinShare && skew.Values >= MinTenants && LargestTenant(skew) is { } tenant)
            {
                findings.Add(Finding(table.Table, skew, tenant, share));
            }
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        List<string> notes = [];

        if (largest is { } top)
        {
            notes.Add("The largest tenant holds " + TableText.Percent(top) + " of a conjoined table's rows; T18 speaks past "
                + TableText.Percent(MinShare) + ", with " + TableText.Count(MinTenants) + " tenants or more.");
        }
        else if (hidden.Count == 0 && unanalysed == 0)
        {
            notes.Add("No conjoined table holds " + TableText.Count(MinRows) + " rows yet.");
        }

        if (unanalysed > 0)
        {
            notes.Add(TableText.Count(unanalysed) + (unanalysed == 1 ? " conjoined table has" : " conjoined tables have")
                + " no statistics for tenant_id yet - never analysed - so how its rows spread over tenants cannot be seen.");
        }

        if (hidden.Count > 0 && facts.ReadsAs is { } role && facts.Platform is { } platform)
        {
            notes.Add(PostgresGrants.NotChecked(Grant(hidden), platform, role));
        }

        return HealthVerdict.Quiet(notes.Count == 0 ? null : string.Join(" ", notes));
    }

    private static HealthFinding Finding(MartenTable table, HealthColumnSkewRow skew, string tenant, double share)
    {
        double counted = skew.ByPartition ? skew.RowsCounted + skew.DefaultRows : skew.RowsCounted;
        string what = table.Kind == MartenTableKind.Document && table.TypeName is { } type
            ? "the " + type + " documents"
            : "the rows of " + table.Key;

        List<HealthEvidence> evidence =
        [
            new("Table", table.Describe()),
            new("Tenant", tenant),
            new("Its rows", "~" + TableText.Count(skew.TopRows[0]) + " of ~" + TableText.Count(counted) + " (" + TableText.Percent(share) + ")"),
            new("Tenants", (skew.ByPartition ? string.Empty : "~") + TableText.Count(skew.Values)
                + (skew.ByPartition ? " tenant partitions" + (skew.DefaultRows > 0 ? ", and a DEFAULT partition holding others" : string.Empty) : " (estimated)")),
        ];

        if (skew.TopRows.Count > 1)
        {
            evidence.Add(new HealthEvidence("The next largest tenant", TableText.Percent(Math.Min(skew.TopRows[1] / counted, 1))));
        }

        evidence.Add(new HealthEvidence(
            "Measured from",
            skew.ByPartition
                ? "each tenant partition's estimated rows (reltuples, or n_live_tup where higher)"
                : "pg_stats' most common values of tenant_id, weighted over " + TableText.Count(skew.Leaves)
                  + (skew.Leaves == 1 ? " table" : " partitions") + " by their estimated rows"));

        return new HealthFinding
        {
            Code = "T18",
            Severity = HealthSeverity.Info,
            Subject = table.Key,
            TenantId = tenant,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = "One tenant holds " + TableText.Percent(share) + " of " + what,
            WhyItMatters = skew.ByPartition
                ? "The table is partitioned by tenant, so the tenant's rows are a partition of their own: its queries are pruned to it, "
                  + "and it is vacuumed and indexed on its own. What it still shares with every other tenant is the database - its "
                  + "I/O, WAL, connections and backups - and a plan that is not pruned at plan time, such as a generic one, locks "
                  + "every tenant's partition (T19)."
                : "Every tenant's rows share this table, its indexes, its statistics and its vacuum. When one tenant holds most of "
                  + "them, its growth sets the table's size and vacuum cost for everybody, and the planner's estimates for every "
                  + "tenant come from one set of column statistics that mostly describes the large tenant's rows - so a small "
                  + "tenant's query can be planned for the large one's numbers.",
            Evidence = evidence,
            Caveat =
                (skew.ByPartition
                    ? "Estimated from the catalog and the statistics collector, as of the last VACUUM, ANALYZE or statistics flush."
                    : "From the statistics of the last ANALYZE, a sample of the rows"
                      + (skew.RowsCounted < skew.Rows ? ", over the part of the table that has statistics." : "."))
                + " No row was read. The finding names a tenant, so it is shown only to a visitor who may see that tenant.",
            Remedy = Remedy(table, skew),
        };
    }

    /// <summary>What to change, in words: a partition or a database of the tenant's own - never a line that changes it by itself.</summary>
    private static HealthRemedy Remedy(MartenTable table, HealthColumnSkewRow skew)
    {
        if (skew.ByPartition)
        {
            return new HealthRemedy(
                "// The table is already partitioned by tenant. What the tenant still shares is the database:\n"
                + "// a database of its own is a multi-database tenancy - opts.MultiTenantedDatabases(...).",
                "A move between databases is a migration of the tenant's data, which Marten does not do by itself.");
        }

        if (table.Kind == MartenTableKind.EventStore)
        {
            return new HealthRemedy(
                "// mt_events and mt_streams are shared by every tenant. Room of the tenant's own is one of:\n"
                + "// - a database of its own: a multi-database tenancy, opts.MultiTenantedDatabases(...);\n"
                + "// - opts.Events.UseTenantPartitionedEvents = true, which partitions the event store by tenant.",
                "UseTenantPartitionedEvents is not a setting to flip: it gives each tenant its own event sequence and progression, "
                + "needs a quick append mode, and moves every event. Either way the tenant's events are moved, which is a migration "
                + "to plan.");
        }

        string type = table.TypeName ?? "T";

        return new HealthRemedy(
            "// Room of the tenant's own is one of:\n"
            + "// - partition the table by tenant: opts.Schema.For<" + type + ">().MultiTenantedWithPartitioning(x => x.ByList()...),\n"
            + "//   or opts.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement(schema) for every conjoined type;\n"
            + "// - a database of its own: a multi-database tenancy, opts.MultiTenantedDatabases(...).",
            "Partitioning an existing table is a changed partition scheme, which Marten's apply carries out by copying the whole "
            + "table out and back (AutoCreate.CreateOrUpdate is not additive): a migration to plan, not a line to paste.");
    }
}
