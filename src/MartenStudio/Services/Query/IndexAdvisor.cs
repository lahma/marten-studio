using System.Globalization;
using System.Text;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>One index on a document table, as <c>pg_indexes</c> reports it.</summary>
/// <param name="Name">The index name.</param>
/// <param name="Definition">The full <c>CREATE INDEX</c> statement from <c>pg_indexes.indexdef</c>.</param>
internal sealed record PostgresIndex(string Name, string Definition);

/// <summary>How well the database can answer a filter.</summary>
internal enum IndexVerdictLevel
{
    /// <summary>An index serves this filter.</summary>
    Green,

    /// <summary>An index touches the column but may not be used for this filter.</summary>
    Amber,

    /// <summary>Nothing serves it: every row in the collection is read.</summary>
    Red,
}

/// <summary>A verdict on one filter, with the <c>StoreOptions</c> line that would improve it.</summary>
/// <param name="Level">Green, amber or red.</param>
/// <param name="Reason">Why, in one sentence a user can act on.</param>
/// <param name="Suggestion">The configuration line that would fix it, or <see langword="null"/>.</param>
internal sealed record IndexVerdict(IndexVerdictLevel Level, string Reason, string? Suggestion);

/// <summary>A predicate and what the advisor thinks of it.</summary>
/// <param name="Predicate">The filter term.</param>
/// <param name="Verdict">The verdict.</param>
internal sealed record PredicateVerdict(DocumentPredicate Predicate, IndexVerdict Verdict);

/// <summary>The advice for a whole search: per term, for the sort, and the worst of them.</summary>
/// <param name="Worst">The worst verdict, which is what the search box shows.</param>
/// <param name="Predicates">One verdict per filter term, in order.</param>
/// <param name="Sort">The verdict on the sort key, when one was asked about.</param>
internal sealed record IndexAdvice(
    IndexVerdictLevel Worst,
    IReadOnlyList<PredicateVerdict> Predicates,
    IndexVerdict? Sort);

/// <summary>
/// Says, before a filter runs, whether the database can answer it with an index — and if not, the exact
/// <c>StoreOptions</c> line the host would add to make it so (plan §3.4, differentiator 2).
/// </summary>
/// <remarks>
/// <para>
/// This is the thing a generic SQL UI cannot do. The studio knows the store's <c>DuplicatedFields</c> and
/// declared <c>Indexes</c> <em>and</em> what <c>pg_indexes</c> actually says, so it can tell the
/// difference between "this filter is indexed", "this filter reads the column of every row" and "this
/// filter reads every document's JSON as text".
/// </para>
/// <para>
/// The three levels mean one thing each. <b>Green</b>: an index can serve this — the leading column of a
/// btree, a computed index whose expression matches, or a GIN index for containment. <b>Amber</b>: an
/// index names the column but not in a position this filter can use, so Postgres may or may not take it.
/// <b>Red</b>: nothing serves it and every row is read; the page offers "Run anyway" and prints the
/// suggestion. The worst verdict wins for the search as a whole.
/// </para>
/// <para>
/// It reads index definitions as text rather than asking Postgres to plan the query. That is deliberate:
/// <c>EXPLAIN</c> costs a round trip per keystroke and answers for one set of statistics rather than for
/// the shape of the filter. A text read is approximate, and is allowed to be — the verdict is advice, and
/// the "Run anyway" button is always there.
/// </para>
/// </remarks>
internal static class IndexAdvisor
{
    /// <summary>Evaluates every predicate, and the sort key when one is given.</summary>
    public static IndexAdvice Evaluate(
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        IReadOnlyList<DocumentPredicate> predicates,
        DocumentColumn? sort = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(predicates);

        List<PredicateVerdict> verdicts = [];
        var worst = IndexVerdictLevel.Green;

        foreach (var predicate in predicates)
        {
            var verdict = Evaluate(table, indexes, predicate);

            verdicts.Add(new PredicateVerdict(predicate, verdict));

            if (verdict.Level > worst)
            {
                worst = verdict.Level;
            }
        }

        IndexVerdict? sortVerdict = null;

        if (sort is not null)
        {
            sortVerdict = EvaluateSort(table, indexes, sort);

            if (sortVerdict.Level > worst)
            {
                worst = sortVerdict.Level;
            }
        }

        return new IndexAdvice(worst, verdicts, sortVerdict);
    }

    /// <summary>Evaluates one predicate.</summary>
    public static IndexVerdict Evaluate(
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        DocumentPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);

        return predicate switch
        {
            DocumentPredicate.IdEquals => new IndexVerdict(
                IndexVerdictLevel.Green, "id is the primary key.", null),

            DocumentPredicate.Tenant => EvaluateTenant(table, indexes),
            DocumentPredicate.IsDeleted => EvaluateDeleted(table, indexes),
            DocumentPredicate.SubclassIs => EvaluateSubclass(table, indexes),
            DocumentPredicate.Contains => EvaluateContainment(table, indexes),

            DocumentPredicate.FreeText => new IndexVerdict(
                IndexVerdictLevel.Red,
                "Free text reads every document's JSON as text; no index can serve a leading-wildcard match.",
                null),

            DocumentPredicate.FieldLike like => EvaluateLike(table, indexes, like.Path),
            DocumentPredicate.FieldCompare compare => EvaluateCompare(table, indexes, compare.Path),

            _ => new IndexVerdict(IndexVerdictLevel.Amber, "The studio has no verdict for this filter.", null),
        };
    }

    /// <summary>Evaluates a sort key, which is a separate question from any filter.</summary>
    public static IndexVerdict EvaluateSort(
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        DocumentColumn sort)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);

        switch (sort)
        {
            case DocumentColumn.Id:
                return new IndexVerdict(IndexVerdictLevel.Green, "id is the primary key.", null);

            case DocumentColumn.Metadata metadata:
            {
                var column = table.MetadataColumnName(metadata.Column);

                if (column is null)
                {
                    return new IndexVerdict(
                        IndexVerdictLevel.Red, $"{metadata.Column} is not enabled on this collection.", null);
                }

                var byColumn = ForColumn(indexes, column);

                if (byColumn.Level == IndexVerdictLevel.Green)
                {
                    return byColumn with { Reason = $"Sorted by {column}, which an index leads with." };
                }

                // mt_last_modified is the one metadata column a host can index from StoreOptions, and it
                // is the one people reach for, so it gets the fix line the way a filter on a duplicated
                // field does. `Index(x => ...)` is not available for a metadata column - it takes a member
                // expression on the document - and the declared call is better than the hand-rolled DDL
                // anyway: an index the configuration does not ask for is dropped by the next apply, which
                // is what IndexAdvice.UndeclaredSuggestion says at length on the Schema screen.
                if (metadata.Column == DocumentMetadataColumn.LastModified)
                {
                    return new IndexVerdict(
                        byColumn.Level,
                        $"Sorting by {column} reads and sorts the whole collection: Marten declares no " +
                        "index on it unless the store asks for one. Declare it rather than creating it by " +
                        "hand - AutoCreate.CreateOrUpdate is not additive, and an index the configuration " +
                        "does not ask for is dropped by the next apply.",
                        StoreOptionsLine(table, "IndexLastModified()"));
                }

                return new IndexVerdict(
                    byColumn.Level,
                    $"Sorting by {column} with no index behind it means reading and sorting the whole collection.",
                    byColumn.Suggestion);
            }

            case DocumentColumn.Duplicated duplicated:
            {
                var byColumn = ForColumn(indexes, duplicated.ColumnName);

                return byColumn.Level == IndexVerdictLevel.Green
                    ? byColumn with { Reason = $"Sorted by {duplicated.ColumnName}, which an index leads with." }
                    : new IndexVerdict(
                        byColumn.Level,
                        $"Sorting by {duplicated.ColumnName} with no index behind it sorts the whole collection.",
                        Duplicate(table, [duplicated.ColumnName]));
            }

            case DocumentColumn.JsonPath path:
            {
                var duplicatedColumn = table.FindDuplicated(path.Path);

                return duplicatedColumn is null
                    ? new IndexVerdict(
                        IndexVerdictLevel.Red,
                        $"Sorting by {Join(path.Path)} reads that property out of every document's JSON.",
                        Duplicate(table, path.Path))
                    : ForColumn(indexes, duplicatedColumn.ColumnName);
            }

            default:
                return new IndexVerdict(IndexVerdictLevel.Amber, "The studio has no verdict for this sort.", null);
        }
    }

    private static IndexVerdict EvaluateTenant(DocumentTableInfo table, IReadOnlyList<PostgresIndex> indexes)
    {
        if (table.TenancyStyle != JasperFx.MultiTenancy.TenancyStyle.Conjoined)
        {
            return new IndexVerdict(
                IndexVerdictLevel.Red,
                $"'{table.Alias}' is not conjoined-tenanted, so there is no tenant_id to filter on.",
                StoreOptionsLine(table, "MultiTenanted()"));
        }

        var column = table.MetadataColumnName(DocumentMetadataColumn.TenantId) ?? "tenant_id";
        var verdict = ForColumn(indexes, column);

        return verdict.Level switch
        {
            IndexVerdictLevel.Green => verdict with { Reason = "tenant_id leads an index." },
            IndexVerdictLevel.Amber => verdict with
            {
                Reason = "tenant_id is part of the primary key but does not lead it, so Postgres may still scan.",
            },
            _ => new IndexVerdict(IndexVerdictLevel.Red, "No index covers tenant_id.", null),
        };
    }

    /// <summary>
    /// A subclass filter, which on a hierarchy Marten migrated is green.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Marten does index <c>mt_doc_type</c>.</b> <c>Marten.Storage.DocumentTable</c>'s constructor runs
    /// <c>if (mapping.IsHierarchy()) { Indexes.Add(new DocumentIndex(_mapping, SchemaConstants.DocumentTypeColumn)); … }</c>
    /// — verified against the Marten 9.35.0 tag, 2026-09-14, and asserted live by
    /// <c>DocumentSubclassIndexLiveTests</c>. The remark that used to be here predicted amber for every
    /// hierarchy, which was wrong about the common case and would have had somebody chasing an index that
    /// is already there.
    /// </para>
    /// <para>
    /// The amber branch is therefore not the normal answer but the drifted one: the column exists and the
    /// index Marten declares for it does not, which means the migration has not been applied here or the
    /// index was dropped by hand.
    /// </para>
    /// </remarks>
    private static IndexVerdict EvaluateSubclass(DocumentTableInfo table, IReadOnlyList<PostgresIndex> indexes)
    {
        var column = table.MetadataColumnName(DocumentMetadataColumn.DocumentType);

        if (column is null)
        {
            return new IndexVerdict(
                IndexVerdictLevel.Red,
                $"'{table.Alias}' is not a hierarchy, so it has no mt_doc_type column.",
                null);
        }

        var verdict = ForColumn(indexes, column);

        return verdict.Level == IndexVerdictLevel.Green
            ? verdict with { Reason = $"{column} leads an index." }
            : new IndexVerdict(
                IndexVerdictLevel.Amber,
                $"A subclass shares its root's table and is found through {column}. Marten declares an " +
                $"index on it for every hierarchy, and this database does not have it - so the migration " +
                "has not been applied here, and the filter scans the table until it is.",
                null);
    }

    private static IndexVerdict EvaluateDeleted(DocumentTableInfo table, IReadOnlyList<PostgresIndex> indexes)
    {
        if (!table.SoftDeleteEnabled)
        {
            return new IndexVerdict(
                IndexVerdictLevel.Red,
                $"'{table.Alias}' is not soft-deleted, so it has no mt_deleted column.",
                StoreOptionsLine(table, "SoftDeleted()"));
        }

        var column = table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) ?? "mt_deleted";
        var verdict = ForColumn(indexes, column);

        return verdict.Level == IndexVerdictLevel.Green
            ? verdict with { Reason = $"{column} leads an index." }
            : new IndexVerdict(
                verdict.Level,
                $"No index leads with {column}; on a mostly-live collection Postgres will scan for the deleted rows.",
                StoreOptionsLine(table, "SoftDeletedWithIndex()"));
    }

    private static IndexVerdict EvaluateContainment(DocumentTableInfo table, IReadOnlyList<PostgresIndex> indexes)
    {
        foreach (var index in indexes)
        {
            var definition = index.Definition.ToLowerInvariant();

            if (definition.Contains("using gin", StringComparison.Ordinal) &&
                MentionsColumn(definition, DocumentTableInfo.DataColumn))
            {
                return new IndexVerdict(
                    IndexVerdictLevel.Green, $"Containment is served by the GIN index {index.Name}.", null);
            }
        }

        return new IndexVerdict(
            IndexVerdictLevel.Red,
            "Containment without a GIN index on data reads every document.",
            StoreOptionsLine(table, "GinIndexJsonData()"));
    }

    private static IndexVerdict EvaluateLike(
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        IReadOnlyList<string> path)
    {
        var duplicated = table.FindDuplicated(path);
        var column = duplicated?.ColumnName;

        if (column is not null)
        {
            foreach (var index in indexes)
            {
                var definition = index.Definition.ToLowerInvariant();

                if (definition.Contains("trgm", StringComparison.Ordinal) && MentionsColumn(definition, column))
                {
                    return new IndexVerdict(
                        IndexVerdictLevel.Green, $"A trigram index ({index.Name}) serves this match.", null);
                }
            }

            return new IndexVerdict(
                IndexVerdictLevel.Amber,
                $"A contains-match on {column} still reads every row, but only that column rather than the JSON. " +
                "Only a pg_trgm index can serve a leading wildcard.",
                null);
        }

        return new IndexVerdict(
            IndexVerdictLevel.Red,
            $"A contains-match on {Join(path)} reads that property out of every document's JSON.",
            Duplicate(table, path));
    }

    private static IndexVerdict EvaluateCompare(
        DocumentTableInfo table,
        IReadOnlyList<PostgresIndex> indexes,
        IReadOnlyList<string> path)
    {
        var duplicated = table.FindDuplicated(path);

        if (duplicated is not null)
        {
            var verdict = ForColumn(indexes, duplicated.ColumnName);

            return verdict.Level switch
            {
                IndexVerdictLevel.Green => verdict with
                {
                    Reason = $"{duplicated.ColumnName} is a duplicated field and leads an index.",
                },
                IndexVerdictLevel.Amber => verdict with
                {
                    Reason = $"{duplicated.ColumnName} is in an index but does not lead it.",
                },
                _ => new IndexVerdict(
                    IndexVerdictLevel.Red,
                    $"{duplicated.ColumnName} is a duplicated field, but no index covers it, so every row is read.",
                    Index(table, path)),
            };
        }

        foreach (var index in indexes)
        {
            if (CoversJsonPath(index.Definition, path))
            {
                return new IndexVerdict(
                    IndexVerdictLevel.Green, $"The computed index {index.Name} matches this JSON path.", null);
            }
        }

        var gin = HasGinOnData(indexes);

        return new IndexVerdict(
            IndexVerdictLevel.Red,
            gin
                ? $"{Join(path)} is only in the JSON. The GIN index on data serves containment (@>), not " +
                  "comparisons through #>>, so every document is read."
                : $"{Join(path)} is only in the JSON, so every document is read.",
            Duplicate(table, path));
    }

    /// <summary>
    /// The verdict for a plain column: green when an index leads with it, amber when an index merely names
    /// it, red when nothing does.
    /// </summary>
    private static IndexVerdict ForColumn(IReadOnlyList<PostgresIndex> indexes, string column)
    {
        var mentioned = false;

        foreach (var index in indexes)
        {
            var definition = index.Definition.ToLowerInvariant();
            var lowered = column.ToLowerInvariant();

            if (string.Equals(LeadingColumn(definition), lowered, StringComparison.Ordinal))
            {
                return new IndexVerdict(IndexVerdictLevel.Green, $"{column} leads the index {index.Name}.", null);
            }

            mentioned |= MentionsColumn(definition, lowered);
        }

        return mentioned
            ? new IndexVerdict(IndexVerdictLevel.Amber, $"{column} is in an index, but does not lead one.", null)
            : new IndexVerdict(IndexVerdictLevel.Red, $"No index covers {column}.", null);
    }

    private static bool HasGinOnData(IReadOnlyList<PostgresIndex> indexes)
    {
        foreach (var index in indexes)
        {
            var definition = index.Definition.ToLowerInvariant();

            if (definition.Contains("using gin", StringComparison.Ordinal) &&
                MentionsColumn(definition, DocumentTableInfo.DataColumn))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an index expression reaches this JSON path — Marten's computed indexes are written as
    /// <c>(data ->> 'Email')</c> or <c>((data -> 'Address' ->> 'City'))</c>, sometimes wrapped in a cast,
    /// so the test is that the expression is on <c>data</c> and quotes every segment in order.
    /// </summary>
    private static bool CoversJsonPath(string definition, IReadOnlyList<string> path)
    {
        var body = IndexBody(definition);

        if (body is null || !body.Contains("data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var searchFrom = 0;

        foreach (var segment in path)
        {
            // JSON keys are case sensitive, so the quoted literal is compared exactly.
            var index = body.IndexOf("'" + segment + "'", searchFrom, StringComparison.Ordinal);

            if (index < 0)
            {
                return false;
            }

            searchFrom = index + segment.Length;
        }

        return true;
    }

    /// <summary>The first column or expression inside the index's column list, lower-cased.</summary>
    internal static string? LeadingColumn(string definition)
    {
        var body = IndexBody(definition);

        if (body is null)
        {
            return null;
        }

        var depth = 0;
        var end = body.Length;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                end = i;
                break;
            }
        }

        var first = body[..end].Trim();

        // Drop an operator class or a sort modifier: "data jsonb_path_ops", "name desc".
        var space = first.IndexOf(' ', StringComparison.Ordinal);

        if (space > 0)
        {
            first = first[..space];
        }

        return first.Trim('"').ToLowerInvariant();
    }

    /// <summary>The contents of the index's outermost column-list parentheses.</summary>
    private static string? IndexBody(string definition)
    {
        var usingClause = definition.IndexOf("using ", StringComparison.OrdinalIgnoreCase);
        var open = definition.IndexOf('(', usingClause < 0 ? 0 : usingClause);

        if (open < 0)
        {
            return null;
        }

        var depth = 0;

        for (var i = open; i < definition.Length; i++)
        {
            if (definition[i] == '(')
            {
                depth++;
            }
            else if (definition[i] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return definition[(open + 1)..i];
                }
            }
        }

        return null;
    }

    private static bool MentionsColumn(string definition, string column)
    {
        var body = IndexBody(definition) ?? definition;
        var from = 0;

        while (true)
        {
            var index = body.IndexOf(column, from, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                return false;
            }

            var before = index == 0 || !IsIdentifierChar(body[index - 1]);
            var afterIndex = index + column.Length;
            var after = afterIndex >= body.Length || !IsIdentifierChar(body[afterIndex]);

            if (before && after)
            {
                return true;
            }

            from = index + 1;
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Duplicate(DocumentTableInfo table, IReadOnlyList<string> path) =>
        StoreOptionsLine(table, $"Duplicate(x => x.{MemberPath(path)})");

    private static string Index(DocumentTableInfo table, IReadOnlyList<string> path) =>
        StoreOptionsLine(table, $"Index(x => x.{MemberPath(path)})");

    private static string StoreOptionsLine(DocumentTableInfo table, string call) =>
        $"options.Schema.For<{table.DocumentTypeName ?? table.Alias}>().{call};";

    private static string MemberPath(IReadOnlyList<string> path)
    {
        var builder = new StringBuilder();

        foreach (var segment in path)
        {
            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(segment.Length == 0
                ? segment
                : char.ToUpper(segment[0], CultureInfo.InvariantCulture) + segment[1..]);
        }

        return builder.ToString();
    }

    private static string Join(IReadOnlyList<string> path) => string.Join('.', path);
}
