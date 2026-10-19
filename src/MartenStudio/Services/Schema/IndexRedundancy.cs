using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Schema;

/// <summary>How one index makes another redundant.</summary>
internal enum RedundancyKind
{
    /// <summary>The same keys, operator classes, collations, order and predicate: two indexes doing one job.</summary>
    Duplicate,

    /// <summary>Its keys are a leading prefix of a b-tree index's, which answers every lookup it answers.</summary>
    Prefix,
}

/// <summary>An index another index on the same table already covers.</summary>
/// <param name="Index">The redundant one.</param>
/// <param name="CoveredBy">The one that covers it.</param>
/// <param name="Kind">How.</param>
internal sealed record RedundantIndex(HealthIndexRow Index, HealthIndexRow CoveredBy, RedundancyKind Kind);

/// <summary>
/// Finds the indexes on a table that another valid index on the same table already does the job of. Pure:
/// the T04 check and its unit tests hand it rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Conservative on purpose.</b> An index is called redundant only when another one answers every query it
/// can answer, and enforces everything it enforces:
/// </para>
/// <list type="bullet">
/// <item>the same access method, and the same predicate - a partial index is smaller than the whole one and
/// may be the one the planner prefers, and a whole index does not cover a partial one's rows' order;</item>
/// <item>each of its key columns equal, in order, to the other's - the same column or expression text (as
/// <c>pg_get_indexdef(oid, k, true)</c> prints it), operator class, collation, and sort order;</item>
/// <item>fewer keys only for a b-tree, where a leading prefix is what a lookup uses - a GIN or GiST index's
/// columns are not searched left to right in the same way, so for those only an exact duplicate counts;</item>
/// <item>its <c>INCLUDE</c> columns all among the other's columns, so an index-only scan it allows still is;</item>
/// <item>never a primary key, and never a unique index unless the other is unique on exactly the same keys -
/// a unique index on <c>(a)</c> enforces more than one on <c>(a, b)</c>, and nothing non-unique covers a
/// uniqueness constraint (an exclusion constraint's index is not unique, so it covers none either);</item>
/// <item>nor a unique index the other enforces less than: a <c>NULLS NOT DISTINCT</c> one
/// (<c>indnullsnotdistinct</c>, PostgreSQL 15) also forbids duplicate nulls, so only another
/// <c>NULLS NOT DISTINCT</c> index covers it; and an immediate one (<c>indimmediate</c>) checks at every
/// statement and is the only kind <c>ON CONFLICT</c> can use, so a <c>DEFERRABLE</c> one does not cover it;</item>
/// <item>never an exclusion constraint's index (<c>indisexclusion</c>): it enforces its constraint whatever
/// else answers its queries.</item>
/// </list>
/// <para>
/// Of two exact duplicates only one is reported: the one <c>keepRank</c> ranks lower (the primary key and
/// Marten's own declarations are kept over a hand-made index), and between equals the one whose name sorts
/// last.
/// </para>
/// </remarks>
internal static class IndexRedundancy
{
    /// <summary>Every redundant index among <paramref name="indexes" />, each with the index that covers it.</summary>
    /// <param name="indexes">Indexes on any number of tables; only indexes on the same table are compared.</param>
    /// <param name="keepRank">How much an index is worth keeping, for choosing between exact duplicates.</param>
    public static IReadOnlyList<RedundantIndex> Find(
        IReadOnlyList<HealthIndexRow> indexes,
        Func<HealthIndexRow, int>? keepRank = null)
    {
        ArgumentNullException.ThrowIfNull(indexes);

        Func<HealthIndexRow, int> rank = keepRank ?? (static x => x.IsPrimaryKey ? 2 : x.IsUnique ? 1 : 0);
        List<RedundantIndex> found = [];

        foreach (IGrouping<string, HealthIndexRow> table in indexes.GroupBy(static x => x.TableKey, StringComparer.Ordinal))
        {
            HealthIndexRow[] onTable = [.. table.OrderBy(static x => x.Name, StringComparer.Ordinal)];

            foreach (HealthIndexRow candidate in onTable)
            {
                if (!candidate.IsValid || candidate.IsPrimaryKey)
                {
                    continue;
                }

                RedundantIndex? best = null;

                foreach (HealthIndexRow other in onTable)
                {
                    if (ReferenceEquals(other, candidate) || !other.IsValid)
                    {
                        continue;
                    }

                    if (Covers(other, candidate) is not { } kind)
                    {
                        continue;
                    }

                    // Two exact duplicates cover each other; only the one that loses is reported.
                    if (kind == RedundancyKind.Duplicate && Covers(candidate, other) is RedundancyKind.Duplicate && !Loses(candidate, other, rank))
                    {
                        continue;
                    }

                    if (best is null || other.KeyColumnCount < best.CoveredBy.KeyColumnCount)
                    {
                        best = new RedundantIndex(candidate, other, kind);
                    }
                }

                if (best is not null)
                {
                    found.Add(best);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="cover" /> does all of <paramref name="index" />'s job, and how - or
    /// <see langword="null" />.
    /// </summary>
    internal static RedundancyKind? Covers(HealthIndexRow cover, HealthIndexRow index)
    {
        ArgumentNullException.ThrowIfNull(cover);
        ArgumentNullException.ThrowIfNull(index);

        if (index.IsPrimaryKey
            || index.IsExclusion
            || !string.Equals(cover.TableKey, index.TableKey, StringComparison.Ordinal)
            || !string.Equals(cover.AccessMethod, index.AccessMethod, StringComparison.Ordinal)
            || !string.Equals(Predicate(cover.Definition), Predicate(index.Definition), StringComparison.Ordinal))
        {
            return null;
        }

        int keys = index.KeyColumnCount;
        if (keys <= 0 || keys > cover.KeyColumnCount || keys > index.Columns.Count || cover.KeyColumnCount > cover.Columns.Count)
        {
            return null;
        }

        bool sameKeys = keys == cover.KeyColumnCount;

        if (!sameKeys && !string.Equals(index.AccessMethod, "btree", StringComparison.Ordinal))
        {
            return null;
        }

        if (index.IsUnique && !(cover.IsUnique && sameKeys))
        {
            return null;
        }

        // What a unique index enforces beyond uniqueness: duplicate nulls refused too, and a check at every statement.
        if (index.IsUnique
            && ((index.NullsNotDistinct && !cover.NullsNotDistinct) || (index.IsImmediate && !cover.IsImmediate)))
        {
            return null;
        }

        for (int i = 0; i < keys; i++)
        {
            if (!string.Equals(index.Columns[i], cover.Columns[i], StringComparison.Ordinal)
                || At(index.OperatorClasses, i) != At(cover.OperatorClasses, i)
                || At(index.Collations, i) != At(cover.Collations, i)
                || At(index.Options, i) != At(cover.Options, i))
            {
                return null;
            }
        }

        HashSet<string> covering = new(cover.Columns, StringComparer.Ordinal);
        for (int i = keys; i < index.Columns.Count; i++)
        {
            if (!covering.Contains(index.Columns[i]))
            {
                return null;
            }
        }

        return sameKeys ? RedundancyKind.Duplicate : RedundancyKind.Prefix;
    }

    /// <summary>
    /// The predicate of a partial index, from its <c>pg_get_indexdef</c> text - what follows the
    /// top-level <c>WHERE</c> after the column list - or <see langword="null" /> for a whole one.
    /// </summary>
    /// <remarks>
    /// Read from the definition rather than with <c>pg_get_expr(indpred, indrelid)</c>, which opens the table;
    /// and compared as text, which is sound because both definitions are deparsed by the same server, from the
    /// same table, with the same <c>search_path</c>. Quotes and parentheses are skipped over, so a column or a
    /// literal that spells "where" is not mistaken for the clause.
    /// </remarks>
    internal static string? Predicate(string definition)
    {
        if (string.IsNullOrEmpty(definition))
        {
            return null;
        }

        int at = OutsideQuotes(definition, " USING ", 0);
        int open = at < 0 ? -1 : definition.IndexOf('(', at);
        if (open < 0)
        {
            return null;
        }

        int close = Matching(definition, open);
        if (close < 0)
        {
            return null;
        }

        int where = OutsideQuotes(definition, " WHERE ", close + 1);
        return where < 0 ? null : definition[(where + " WHERE ".Length)..].Trim();
    }

    private static bool Loses(HealthIndexRow index, HealthIndexRow other, Func<HealthIndexRow, int> rank)
    {
        int a = rank(index);
        int b = rank(other);

        return a != b ? a < b : string.CompareOrdinal(index.Name, other.Name) > 0;
    }

    private static T? At<T>(IReadOnlyList<T> values, int index) where T : struct =>
        index < values.Count ? values[index] : null;

    /// <summary>Where <paramref name="token" /> first appears at parenthesis depth zero and outside quotes.</summary>
    private static int OutsideQuotes(string text, string token, int start)
    {
        int depth = 0;
        char? quote = null;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    continue;
                case '(':
                    depth++;
                    continue;
                case ')':
                    depth--;
                    continue;
            }

            if (depth == 0 && string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The parenthesis that closes the one at <paramref name="open" />, outside quotes.</summary>
    private static int Matching(string text, int open)
    {
        int depth = 0;
        char? quote = null;

        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];

            if (quote is { } q)
            {
                if (c == q)
                {
                    quote = null;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
