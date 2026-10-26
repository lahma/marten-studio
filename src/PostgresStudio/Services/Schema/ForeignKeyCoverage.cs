namespace PostgresStudio.Services.Schema;

/// <summary>One index on a foreign key's own table, as <see cref="ForeignKeyCoverage" /> reads it.</summary>
/// <param name="KeyAttributes">
/// Its key columns as the table's attribute numbers, in index order (<c>indkey</c>, never an <c>INCLUDE</c> column) -
/// zero for an expression.
/// </param>
/// <param name="IsValid"><c>indisvalid</c>: a failed concurrent build is maintained and never used.</param>
/// <param name="IsPartial">Whether it has a predicate: a partial index holds only the rows its <c>WHERE</c> admits.</param>
/// <param name="AccessMethod"><c>btree</c>, <c>hash</c>, <c>gin</c>...</param>
internal sealed record CoveringIndexCandidate(IReadOnlyList<short> KeyAttributes, bool IsValid, bool IsPartial, string AccessMethod);

/// <summary>
/// Whether an index lets Postgres find the rows that point at a referenced row without reading the whole referencing table -
/// T15's rule and the database browser's Keys badge, one pure function for both (W5-B1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> Every delete from the referenced table, and every update of its key, makes Postgres look up the
/// referencing rows (<c>RI_FKey_noaction_del</c> and its kin run <c>SELECT 1 FROM t WHERE fk_cols = $1 ...</c>), and so does
/// a join from the referenced table on the key. Postgres creates an index for a primary key or a unique constraint, never
/// for a foreign key.
/// </para>
/// <para>
/// <b>The rule (B0 §9, as the issue's host B words it).</b> A valid, non-partial b-tree whose leading key columns are
/// exactly the key's columns, in any order: an index on <c>(b, a)</c> covers a key on <c>(a, b)</c>; one on <c>(a)</c> alone
/// does not, because every row matching <c>a</c> is still read and filtered; an expression in the leading columns is not
/// one of the key's columns, so it never matches. A one-column hash index answers the same equality, and counts.
/// </para>
/// <para>
/// <b>A partitioned referencing table prunes before it reads.</b> The check's query names every key column, so where every
/// column of the table's partition key is one of the foreign key's, the planner prunes to one partition on them and needs
/// an index only for the rest: Marten's DCB tag table under archived-stream partitioning references
/// <c>(seq_id, is_archived)</c>, is partitioned on <c>is_archived</c>, and 9.51's index on <c>(seq_id)</c> covers it. A key
/// made only of the partition key's columns still reads its whole partition, and is judged by the full rule.
/// </para>
/// </remarks>
/// <summary>How far an index on a foreign key's own table serves the referential lookup (<see cref="ForeignKeyCoverage.Judge" />).</summary>
internal enum ForeignKeyCover
{
    /// <summary>No usable index leads with any of the key's columns: the lookup reads the whole table.</summary>
    None,

    /// <summary>
    /// A usable index leads with some of the key's columns, not all: the lookup reads the rows matching those and filters
    /// the rest - narrower than the whole table, wider than a covering index.
    /// </summary>
    Partly,

    /// <summary>An index leads with every column the lookup needs (<see cref="ForeignKeyCoverage.IsCovered" />).</summary>
    Covered,
}

internal static class ForeignKeyCoverage
{
    /// <summary>
    /// How far <paramref name="indexes" /> serve a foreign key on <paramref name="keyAttributes" />: covered, partly, or not
    /// at all.
    /// </summary>
    /// <param name="keyAttributes">The foreign key's columns (<c>conkey</c>).</param>
    /// <param name="partitionKey">The table's partition key columns, as <see cref="IsCovered" /> takes them.</param>
    /// <param name="indexes">The indexes on the table itself.</param>
    /// <param name="trailing">
    /// Key columns the table's source says an index need not lead with (W5-B1 review F1): the <c>tenant_id</c> a conjoined
    /// Marten table's <c>ForeignKey&lt;TRef&gt;</c> adds to the key (<c>DocumentForeignKey</c>, V9.39.0) while indexing only
    /// the member's column (<c>MartenRegistry.ForeignKey</c>: <c>AddIndex(ColumnNames[0])</c>, <c>TenancyScope.Global</c> by
    /// default). An index on the rest covers the key: one tenant's share of a member value's rows is what it filters.
    /// </param>
    public static ForeignKeyCover Judge(
        IReadOnlyList<short> keyAttributes,
        IReadOnlyList<short> partitionKey,
        IEnumerable<CoveringIndexCandidate> indexes,
        IReadOnlyCollection<short>? trailing = null)
    {
        ArgumentNullException.ThrowIfNull(keyAttributes);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(indexes);

        IReadOnlyList<CoveringIndexCandidate> candidates = [.. indexes];

        if (IsCovered(keyAttributes, partitionKey, candidates))
        {
            return ForeignKeyCover.Covered;
        }

        if (trailing is { Count: > 0 })
        {
            short[] leading = [.. keyAttributes.Where(x => !trailing.Contains(x))];

            if (leading.Length > 0 && leading.Length < keyAttributes.Count && IsCovered(leading, [], candidates))
            {
                return ForeignKeyCover.Covered;
            }
        }

        HashSet<short> key = [.. keyAttributes];
        return candidates.Any(x => Usable(x) && x.KeyAttributes.Count > 0 && key.Contains(x.KeyAttributes[0]))
            ? ForeignKeyCover.Partly
            : ForeignKeyCover.None;
    }

    /// <summary>Whether <paramref name="indexes" /> holds one that covers a foreign key on <paramref name="keyAttributes" />.</summary>
    /// <param name="keyAttributes">The foreign key's columns (<c>conkey</c>), as attribute numbers of its table.</param>
    /// <param name="partitionKey">
    /// The table's partition key columns (<c>partattrs</c>) when it is a partitioned table, zero for an expression; empty
    /// otherwise.
    /// </param>
    /// <param name="indexes">The indexes on the table itself - a partitioned table's partitioned indexes.</param>
    public static bool IsCovered(IReadOnlyList<short> keyAttributes, IReadOnlyList<short> partitionKey, IEnumerable<CoveringIndexCandidate> indexes)
    {
        ArgumentNullException.ThrowIfNull(keyAttributes);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentNullException.ThrowIfNull(indexes);

        if (keyAttributes.Count == 0)
        {
            return true;
        }

        HashSet<short> all = [.. keyAttributes];
        HashSet<short>? pruned = PrunedRemainder(all, partitionKey);

        foreach (CoveringIndexCandidate index in indexes)
        {
            if (!Usable(index))
            {
                continue;
            }

            if (Leads(index.KeyAttributes, all) || (pruned is not null && Leads(index.KeyAttributes, pruned)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The key's columns the partition key leaves to an index - or <see langword="null" /> when pruning does not apply: the
    /// table is not partitioned, its partition key has an expression or a column outside the key, or the key is all
    /// partition key.
    /// </summary>
    internal static HashSet<short>? PrunedRemainder(HashSet<short> key, IReadOnlyList<short> partitionKey)
    {
        if (partitionKey.Count == 0 || partitionKey.Any(x => x <= 0 || !key.Contains(x)))
        {
            return null;
        }

        HashSet<short> remainder = [.. key.Except(partitionKey)];
        return remainder.Count == 0 ? null : remainder;
    }

    /// <summary>Whether an index can serve the check's lookup at all: valid, unconditional, and an equality-answering method.</summary>
    private static bool Usable(CoveringIndexCandidate index) =>
        index.IsValid
        && !index.IsPartial
        && (string.Equals(index.AccessMethod, "btree", StringComparison.Ordinal)
            || (string.Equals(index.AccessMethod, "hash", StringComparison.Ordinal) && index.KeyAttributes.Count == 1));

    /// <summary>Whether the index's first <c>set.Count</c> key columns are exactly <paramref name="set" />, in any order.</summary>
    private static bool Leads(IReadOnlyList<short> keyColumns, HashSet<short> set) =>
        keyColumns.Count >= set.Count && set.SetEquals(keyColumns.Take(set.Count)) && keyColumns.Take(set.Count).Distinct().Count() == set.Count;
}
