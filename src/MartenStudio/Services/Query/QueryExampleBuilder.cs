using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

/// <summary>
/// One document type, reduced to the four facts an example needs. Keeping it a plain record is what lets
/// the examples be built - and tested - without a store, a database or Marten.
/// </summary>
/// <param name="Alias">The document alias, so picking an example also picks the type.</param>
/// <param name="QualifiedTableName">The quoted, schema-qualified table name.</param>
/// <param name="PropertyName">
/// A property of this type worth filtering on - a duplicated field when there is one, because that is the
/// filter that can use an index - or <see langword="null" /> when nothing is known about its shape.
/// </param>
/// <param name="HasLastModified">
/// Whether <c>mt_last_modified</c> exists. It does not on a store with
/// <c>Policies.DisableInformationalFields()</c>, and an example that named a column that is not there
/// would be worse than no example (AGENTS.md hard rule 10).
/// </param>
internal sealed record QueryExampleSource(
    string Alias,
    string QualifiedTableName,
    string? PropertyName,
    bool HasLastModified);

/// <summary>
/// Builds the idle panel's examples out of <em>this</em> store's aliases, tables and duplicated fields.
/// </summary>
/// <remarks>
/// The whole reason a Marten UI beats pgAdmin is that it knows what the host configured, and the idle
/// state of a query page is where that shows first: a person who has never seen this database gets three
/// clauses and three statements that run against it as they are, naming its own tables. Generic examples
/// would have to be edited before they did anything, which is the same as no examples.
/// </remarks>
internal static class QueryExampleBuilder
{
    /// <summary>The property name used when nothing is known about a type's shape.</summary>
    internal const string FallbackPropertyName = "Name";

    /// <summary>
    /// Three <c>where</c> clauses and three SQL statements, or <see cref="QueryExamples.None" /> when the
    /// store has no document type to build them from.
    /// </summary>
    /// <param name="types">The visible document types, in the order the picker shows them.</param>
    /// <param name="eventsSchema">The event store's schema, for the <c>mt_events</c> example.</param>
    public static QueryExamples Build(IReadOnlyList<QueryExampleSource> types, string? eventsSchema)
    {
        ArgumentNullException.ThrowIfNull(types);

        if (types.Count == 0)
        {
            return QueryExamples.None;
        }

        var first = types[0];
        var property = string.IsNullOrWhiteSpace(first.PropertyName) ? FallbackPropertyName : first.PropertyName;
        var quotedProperty = property.Replace("'", "''", StringComparison.Ordinal);

        List<QueryExample> where =
        [
            new(
                QueryMode.Marten,
                "Filter on a JSON property",
                $"where data ->> '{quotedProperty}' = 'some value'",
                first.Alias),
            new(
                QueryMode.Marten,
                "Containment, which a GIN index can answer",
                $$"""where d.data @> '{"{{quotedProperty}}": "some value"}'""",
                first.Alias),
        ];

        // mt_last_modified is optional, so the third example is only offered where the column exists; a
        // type without it gets an ordering example instead, which is true of every Marten table.
        where.Add(first.HasLastModified
            ? new QueryExample(
                QueryMode.Marten,
                "Changed in the last day",
                "where mt_last_modified > now() - interval '1 day'",
                first.Alias)
            : new QueryExample(
                QueryMode.Marten,
                "The newest rows first",
                "order by d.id desc",
                first.Alias));

        List<QueryExample> sql =
        [
            new(
                QueryMode.Sql,
                "Count one collection exactly",
                $"select count(*) from {first.QualifiedTableName}",
                first.Alias),
        ];

        if (!string.IsNullOrWhiteSpace(eventsSchema))
        {
            sql.Add(new QueryExample(
                QueryMode.Sql,
                "Events by type",
                $"select type, count(*) from {SqlIdentifier.Qualify(eventsSchema, "mt_events")} group by type order by 2 desc",
                null));
        }

        sql.Add(BuildRecentRowsExample(first));

        return new QueryExamples(where, sql);
    }

    private static QueryExample BuildRecentRowsExample(QueryExampleSource first)
    {
        var limit = 20.ToString(CultureInfo.InvariantCulture);

        return first.HasLastModified
            ? new QueryExample(
                QueryMode.Sql,
                "The most recently written rows",
                $"select * from {first.QualifiedTableName} order by mt_last_modified desc limit {limit}",
                first.Alias)
            : new QueryExample(
                QueryMode.Sql,
                "A page of rows",
                $"select * from {first.QualifiedTableName} order by id limit {limit}",
                first.Alias);
    }
}
