using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T04 reads: every index on the declared tables.</summary>
/// <param name="Indexes">The indexes, with where each comes from.</param>
internal sealed record RedundantIndexFacts(IReadOnlyList<IndexFacts> Indexes)
{
    /// <summary>The tables locked for a schema change, which are not judged this sweep (<see cref="HealthIndexRead.LockedTables" />).</summary>
    public IReadOnlySet<string> LockedTables { get; init; } = HealthIndexRead.Empty.LockedTables;
}

/// <summary>
/// T04: an index another valid index on the same table already does the whole job of.
/// </summary>
/// <remarks>
/// <para>
/// The judgement is <see cref="IndexRedundancy" />'s, and it is deliberately narrow: the same access method,
/// predicate, operator classes, collations and order, keys a leading prefix of a b-tree's or an exact
/// duplicate, and never a unique or primary-key index called redundant to one that enforces less. A redundant
/// index costs every write and buys no read.
/// </para>
/// <para>
/// <b>Only what a line can remove.</b> An index Marten adds by itself (<c>mt_deleted</c>, <c>mt_doc_type</c>,
/// <c>tenant_id</c>) or the event store declares is Marten's design, not the host's, and is never reported as
/// the redundant one - though it can be the one that covers another.
/// </para>
/// </remarks>
[HealthCheck("T04", HealthArea.Tables, HealthCostTier.Catalog, "Redundant index")]
internal sealed class RedundantIndexCheck : HealthCheck<RedundantIndexFacts>
{
    /// <inheritdoc />
    public override async Task<RedundantIndexFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        HealthIndexRead read = await HealthTables.IndexReadAsync(context).ConfigureAwait(false);
        return new RedundantIndexFacts(read.Indexes) { LockedTables = read.LockedTables };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(RedundantIndexFacts facts, HealthEvaluation evaluation)
    {
        IndexFacts[] judged = [.. facts.Indexes.Where(x => !facts.LockedTables.Contains(x.Table.Key))];

        Dictionary<HealthIndexRow, IndexFacts> byRow = new(ReferenceEqualityComparer.Instance);
        foreach (IndexFacts index in judged)
        {
            byRow[index.Row] = index;
        }

        IReadOnlyList<RedundantIndex> redundant = IndexRedundancy.Find(
            [.. judged.Select(static x => x.Row)],
            row => KeepRank(byRow[row].Origin));

        List<HealthFinding> findings = [];

        foreach (RedundantIndex found in redundant)
        {
            IndexFacts index = byRow[found.Index];
            IndexFacts cover = byRow[found.CoveredBy];

            // A library's table is exempt from the index-design rules (issue #8, G1): its indexes are the library's design.
            if (index.Origin is IndexOrigin.PrimaryKey or IndexOrigin.Structural or IndexOrigin.EventStore || index.Table.ExemptFromIndexDesign)
            {
                continue;
            }

            findings.Add(Finding(index, cover, found.Kind, evaluation.TableRemedies));
        }

        string? note = HealthTables.LockedNote(facts.LockedTables);
        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    /// <summary>Which of two exact duplicates to keep: Marten's own over a declaration over a hand-made one.</summary>
    internal static int KeepRank(IndexOrigin origin) => origin switch
    {
        IndexOrigin.PrimaryKey => 6,
        IndexOrigin.Structural => 5,
        IndexOrigin.EventStore => 4,
        IndexOrigin.Declared or IndexOrigin.Managed or IndexOrigin.Host => 3,
        IndexOrigin.Ignored => 2,
        _ => 1,
    };

    private static HealthFinding Finding(IndexFacts index, IndexFacts cover, RedundancyKind kind, IHealthTableRemedies? remedies)
    {
        StudioTable table = index.Table;
        string how = kind == RedundancyKind.Duplicate
            ? "an exact duplicate of " + cover.Row.Name
            : "a leading prefix of " + cover.Row.Name + "'s keys";

        return new HealthFinding
        {
            Code = "T04",
            Severity = HealthSeverity.Warning,
            Subject = index.Key,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = table.GatedSchemas,
            Title = "Index " + index.Row.Name + " on " + table.Key + " is redundant: " + cover.Row.Name + " already answers every lookup it answers",
            WhyItMatters =
                "The index is " + how + ", with the same predicate, operator classes, collations and order, so every query that "
                + "could use it can use " + cover.Row.Name + " instead. Meanwhile every insert, every update that is not HOT and "
                + "every delete maintains both, and both take space in the buffer cache."
                + (index.Origin == IndexOrigin.Undeclared
                    ? " Nothing declares it, so the next apply drops it anyway (AutoCreate.CreateOrUpdate is not additive)."
                    : string.Empty),
            Evidence =
            [
                new HealthEvidence("Index", index.Row.Name),
                new HealthEvidence("Covered by", cover.Row.Name),
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("How", kind == RedundancyKind.Duplicate ? "an exact duplicate" : "a leading prefix of a b-tree index"),
                new HealthEvidence("Size", TableText.Bytes(index.Row.Bytes)),
                new HealthEvidence("Scans of it", index.Row.Scans is { } scans ? TableText.Count(scans) : "n/a"),
            ],
            Caveat =
                "Compared as Postgres prints each index, column by column. A planner can still prefer the smaller index for a "
                + "query that reads only its columns, so drop it on purpose; a scan count above zero says some query chose it.",
            Remedy = remedies?.RemoveRedundantIndex(table, index.Row, index.Origin, cover.Row),
        };
    }
}
