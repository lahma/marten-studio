using System.Globalization;

using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T03 reads: every index on the declared tables, and each table's count of invalid partition indexes.</summary>
/// <param name="Indexes">The indexes on the declared tables (never a partition's).</param>
/// <param name="Tables">The declared tables, for the invalid indexes on their partitions.</param>
internal sealed record InvalidIndexFacts(IReadOnlyList<IndexFacts> Indexes, IReadOnlyList<TableFacts> Tables)
{
    /// <summary>The tables locked for a schema change, which are not judged this sweep (<see cref="HealthIndexRead.LockedTables" />).</summary>
    public IReadOnlySet<string> LockedTables { get; init; } = HealthIndexRead.Empty.LockedTables;
}

/// <summary>
/// T03: an index on a Marten table that Postgres has marked invalid - and that no build is still working on.
/// </summary>
/// <remarks>
/// <para>
/// <c>indisvalid = false</c> is what a <c>CREATE INDEX CONCURRENTLY</c> that failed half-way leaves behind - a
/// unique build that met a duplicate, a cancelled statement, a deadlock - and what a partitioned index is until
/// every partition has its index attached. The planner never uses it; every insert and every update still
/// maintains it. Critical, because it is the worst of both, and because it is the shape of an index somebody
/// believes they have.
/// </para>
/// <para>
/// <b>But it is also what every concurrent build looks like while it runs</b> - <c>CREATE INDEX CONCURRENTLY</c>,
/// <c>REINDEX CONCURRENTLY</c>'s <c>_ccnew</c> index, Weasel's concurrent build on a partitioned parent until its
/// last child is attached - and re-applying then would queue a <c>DROP INDEX</c> for an
/// <c>AccessExclusiveLock</c> behind the build, with every query on the table queued behind that. So an
/// invalid index a build in <c>pg_stat_progress_create_index</c> is working on right now is no finding, only a
/// note; and while that view shows builds whose table this role cannot see - another role's, without
/// <c>pg_read_all_stats</c> - an invalid index is a Warning that says why, never a Critical.
/// </para>
/// <para>
/// <b>"Working on" is narrow on purpose</b>: the build's own index (<c>index_relid</c>); for a
/// <c>REINDEX CONCURRENTLY</c>, the <c>_ccnew</c> or <c>_ccold</c> copy of the index it reports - its first phase
/// reports the index it copies, not the copy, measured live; and for a partitioned index, a <c>CREATE INDEX</c>
/// on one of its partitions. A build of another index on the same table is not a reason this one is invalid,
/// and matching it would hide a failed index for as long as a nightly rolling <c>REINDEX</c> ran. Such a build
/// does change the advice, though: the finding then says to let it finish before re-applying.
/// </para>
/// <para>
/// <b>Re-apply is the remedy on an ordinary table</b>, verified rather than hoped: Weasel reads
/// <c>indisvalid</c> with an existing table's indexes and treats an invalid one as different from its
/// declaration even when the definitions agree, so an apply drops it and builds it again (weasel#503,
/// <c>IndexDefinition.Matches</c> at Weasel 9.33.0 - the floor's); one nobody declares is dropped by the same
/// apply. <b>Not on a partitioned table:</b> Weasel's <c>Table.FetchExisting</c> reads only the parent's indexes,
/// and its child index names are fixed with no <c>IF NOT EXISTS</c>, so a resumed build fails on the leftover
/// child index with <c>42P07</c>. There the finding says, in words, that a DBA drops the leftover index on the
/// partition first - and names no partition: a partition is named after its tenant.
/// </para>
/// </remarks>
[HealthCheck("T03", HealthArea.Tables, HealthCostTier.Catalog, "Invalid index")]
internal sealed class InvalidIndexCheck : HealthCheck<InvalidIndexFacts>
{
    /// <summary>
    /// What a partitioned table's invalid index needs instead of a re-apply, in MartenStudio
    /// (<see cref="Settings.StudioText.PartitionedIndexRepair" /> is each studio's).
    /// </summary>
    internal const string PartitionedInWords =
        " On a partitioned table a re-apply cannot resume the build: Weasel reads only the partitioned table's own indexes, "
        + "and rebuilding under the same child index name fails while the leftover index on the partition is still there. A "
        + "DBA drops that leftover partition index first; the next apply then builds the index again. The partition is not "
        + "named here, because Marten names a tenant's partition after the tenant.";

    /// <inheritdoc />
    public override async Task<InvalidIndexFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);

        return new InvalidIndexFacts(read.Indexes, tables) { LockedTables = read.LockedTables };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(InvalidIndexFacts facts, HealthEvaluation evaluation)
    {
        List<HealthFinding> findings = [];
        List<string> building = [];
        HashSet<string> partitioned = new(
            facts.Tables.Where(static x => x.Row.IsPartitioned).Select(static x => x.Key),
            StringComparer.Ordinal);

        foreach (IndexFacts index in facts.Indexes)
        {
            if (index.Row.IsValid || facts.LockedTables.Contains(index.Table.Key))
            {
                continue;
            }

            if (index.Row.BuildPhase is { } phase)
            {
                // A status row's note is shown at the check's gate alone, so it names no table outside the store's own schemas.
                building.Add((index.Table.OutsideOwnSchemas ? "an index on " + index.Table.NoteName : index.Row.Name) + " (" + phase + ")");
                continue;
            }

            findings.Add(OnTable(index, partitioned.Contains(index.Table.Key), evaluation.TableRemedies, evaluation.Text));
        }

        foreach (TableFacts table in facts.Tables)
        {
            if (facts.LockedTables.Contains(table.Key))
            {
                continue;
            }

            if (table.Row.BuildingPartitionIndexes > 0)
            {
                int count = table.Row.BuildingPartitionIndexes;
                building.Add((count == 1 ? "an index" : count.ToString("N0", CultureInfo.InvariantCulture) + " indexes")
                    + " on the partitions of " + table.Table.NoteName + " (" + (table.Row.PartitionBuildPhase ?? "in progress") + ")");
            }

            if (table.Row.InvalidPartitionIndexes > 0)
            {
                findings.Add(OnPartitions(table, evaluation.Text));
            }
        }

        string? note = building.Count > 0
            ? "Being built right now, and so not judged until the build ends: " + string.Join(", ", building) + "."
            : null;

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings, HealthTables.WithLockedNote(note, facts.LockedTables));
        }

        return HealthVerdict.Quiet(HealthTables.WithLockedNote(
            note ?? (facts.Indexes.Count == 0 && facts.LockedTables.Count == 0 ? evaluation.Text.NoJudgedTable : null),
            facts.LockedTables));
    }

    /// <summary>
    /// What the finding can say about the builds running now: none in view, another index's on the same table, or
    /// some whose table this role may not see - the last of which caps it at a Warning.
    /// </summary>
    /// <param name="unseenBuilds">Builds in this database whose table the role may not see.</param>
    /// <param name="tableBuild">The phase of a build of another index on the table, or <see langword="null" />.</param>
    /// <param name="source">What the finding was read from, for the caveat.</param>
    private static (HealthSeverity Severity, string Builds, string Caveat) Certainty(int unseenBuilds, string? tableBuild, string source)
    {
        string meanwhile = tableBuild is null
            ? string.Empty
            : " A build of another index on this table is running right now (" + tableBuild + "): let it finish before "
              + "re-applying or dropping anything, or the DROP INDEX queues for an AccessExclusiveLock behind it, and every "
              + "query on the table queues behind that.";

        string inView = tableBuild is null
            ? "and no index build is running on the table"
            : "and the index build running on the table is another index's";

        if (unseenBuilds == 0)
        {
            return (HealthSeverity.Critical,
                inView + ", so it is what",
                "Read from " + source + ", with every index build running in this database in view "
                + "(pg_stat_progress_create_index): none of them is working on this one." + meanwhile);
        }

        return (HealthSeverity.Warning,
            "and no index build this role can see is working on it, so it is most likely what",
            "Read from " + source + ". Postgres reports " + unseenBuilds.ToString("N0", CultureInfo.InvariantCulture)
            + " index build" + (unseenBuilds == 1 ? string.Empty : "s")
            + " running in this database whose table this role may not see (another role's, without pg_read_all_stats), and this "
            + "may be one of them still being built - so a Warning rather than a Critical until it can be told. Check "
            + "pg_stat_progress_create_index as a role that can, before acting." + meanwhile);
    }

    private static HealthFinding OnTable(IndexFacts index, bool onPartitionedTable, IHealthTableRemedies? remedies, StudioText text)
    {
        StudioTable table = index.Table;
        (HealthSeverity severity, string builds, string caveat) = Certainty(
            index.Row.UnseenIndexBuilds, index.Row.TableBuildPhase, "pg_index.indisvalid");

        string whose = index.Origin switch
        {
            IndexOrigin.Host =>
                " It is the host's own index, which nothing the studio knows of reconciles: whoever owns the schema rebuilds it "
                + "(REINDEX INDEX CONCURRENTLY) or drops it, in the host's own migrations.",
            IndexOrigin.StoreManaged =>
                " It is on a table a Marten store manages: that store's next apply decides it, so change it in that store's "
                + "StoreOptions - re-applying its configuration drops an invalid index and builds it again.",
            IndexOrigin.Ignored =>
                " The configuration tells Marten's migration to skip it (IgnoreIndex), so an apply neither rebuilds nor drops it - "
                + "short of one that rebuilds the whole table, as a changed partition scheme does: it stays invalid until a DBA "
                + "drops or rebuilds it in Postgres directly.",
            _ when onPartitionedTable =>
                " On a partitioned table an index stays invalid until every partition has its own index attached - a build that "
                + "stopped before the last partition." + text.PartitionedIndexRepair,
            IndexOrigin.Undeclared =>
                " Nothing in the configuration declares it, so it is a hand-made CREATE INDEX CONCURRENTLY that failed, and the "
                + "next apply drops it. If it is wanted, declare it first.",
            _ => text.DeclaredIndexRebuild,
        };

        return new HealthFinding
        {
            Code = "T03",
            Severity = severity,
            Subject = index.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = "Index " + index.Row.Name + " on " + table.Key + " is invalid: every write maintains it and no query uses it",
            WhyItMatters =
                "Postgres has marked the index invalid, " + builds + " a CREATE INDEX CONCURRENTLY that failed half-way leaves "
                + "behind. The planner never chooses it, so every query that "
                + "needed it reads the table instead, while every insert and every update still pays to maintain it." + whose,
            Evidence =
            [
                new HealthEvidence("Index", index.Row.Name),
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Access method", index.Row.AccessMethod + (index.Row.IsUnique ? ", unique" : string.Empty)),
                new HealthEvidence("Size", TableText.Bytes(index.Row.Bytes)),
                new HealthEvidence("Declared", OriginText(index)),
            ],
            Caveat = caveat,
            // An ignored index is one no apply touches, and a partitioned table's is one no apply can resume: both are said in
            // words above. Anything else is the studio's to word - a re-apply in MartenStudio.
            Remedy = index.Origin == IndexOrigin.Ignored || onPartitionedTable ? null : remedies?.RebuildInvalidIndex(table, index.Row, index.Origin),
        };
    }

    private static HealthFinding OnPartitions(TableFacts table, StudioText text)
    {
        int count = table.Row.InvalidPartitionIndexes;
        string indexes = count == 1 ? "1 index" : count.ToString("N0", CultureInfo.InvariantCulture) + " indexes";
        (HealthSeverity severity, _, string caveat) = Certainty(
            table.Row.UnseenIndexBuilds,
            table.Row.TreeBuildPhase,
            "pg_index.indisvalid over the table's partitions, walked through pg_inherits without opening any of them");

        string builds = table.Row.UnseenIndexBuilds > 0
            ? ", and no build this role can see is working on them, so this is most likely what"
            : table.Row.TreeBuildPhase is not null
                ? ", and the build running on the table now is another index's, so this is what"
                : ", and no build is running on any of them, so this is what";

        return new HealthFinding
        {
            Code = "T03",
            Severity = severity,
            Subject = table.Key + "#partitions",
            DocumentTypes = table.Table.DocumentTypes,
            GatedSchemas = table.Table.GatedSchemas,
            Title = indexes + " on the partitions of " + table.Key + (count == 1 ? " is" : " are") + " invalid",
            WhyItMatters =
                "Postgres has marked an index on one or more of this table's partitions invalid" + builds
                + " a concurrent build or rebuild on a partition that failed leaves behind. Queries that reach that partition read it "
                + "without the index, and every write to it still maintains it." + text.PartitionedIndexRepair,
            Evidence =
            [
                new HealthEvidence("Table", table.Table.Describe()),
                new HealthEvidence("Invalid indexes on its partitions", count.ToString("N0", CultureInfo.InvariantCulture)),
            ],
            Caveat = caveat,
        };
    }

    private static string OriginText(IndexFacts index) => index.Origin switch
    {
        IndexOrigin.PrimaryKey => "the table's primary key",
        IndexOrigin.Structural => "Marten adds it to the table itself",
        IndexOrigin.Declared => "by opts.Schema.For<" + index.Table.TypeName + ">()",
        IndexOrigin.EventStore => "by the event store's configuration",
        IndexOrigin.Managed => "by the Marten-managed table's own definition",
        IndexOrigin.Ignored => "no - the configuration ignores it (IgnoreIndex)",
        IndexOrigin.Host => "by the host's own schema",
        IndexOrigin.StoreManaged => "managed by a Marten store; change it in that store's StoreOptions",
        _ => "no - nothing in the configuration declares it",
    };
}
