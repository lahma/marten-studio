using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The idle panel's examples, built from a store's own aliases and tables.
/// </summary>
/// <remarks>
/// The point of these is that they already run against the database in front of you - that is the whole
/// reason a Marten UI beats pgAdmin. So the tests are about the store's own names appearing in them, and
/// about the one column that is genuinely optional (<c>mt_last_modified</c>) never being named on a store
/// that does not have it (AGENTS.md hard rule 10).
/// </remarks>
public class QueryExampleBuilderTests
{
    private static QueryExampleSource Person(string? property = "FullName", bool lastModified = true) =>
        new("person", "\"studio\".\"mt_doc_person\"", property, lastModified);

    private static QueryExampleSource Tag() =>
        new("tag", "\"studio\".\"mt_doc_tag\"", "Label", true);

    [Fact]
    public void A_store_with_no_document_type_has_no_examples()
    {
        QueryExampleBuilder.Build([], "studio_events").Should().BeSameAs(QueryExamples.None);
    }

    [Fact]
    public void Three_where_clauses_and_three_statements_come_out_of_a_normal_store()
    {
        var examples = QueryExampleBuilder.Build([Person(), Tag()], "studio_events");

        examples.Where.Should().HaveCount(3);
        examples.Sql.Should().HaveCount(3);
        examples.Where.Should().AllSatisfy(x => x.Mode.Should().Be(QueryMode.Marten));
        examples.Sql.Should().AllSatisfy(x => x.Mode.Should().Be(QueryMode.Sql));
    }

    [Fact]
    public void The_clauses_name_the_first_types_own_property()
    {
        var examples = QueryExampleBuilder.Build([Person(), Tag()], "studio_events");

        examples.Where[0].Snippet.Should().Be("where data ->> 'FullName' = 'some value'");
        examples.Where[1].Snippet.Should().Contain("d.data @>").And.Contain("\"FullName\"");
        examples.Where[2].Snippet.Should().Be("where mt_last_modified > now() - interval '1 day'");
        examples.Where.Should().AllSatisfy(x => x.Alias.Should().Be("person"));
    }

    [Fact]
    public void A_type_whose_shape_is_unknown_falls_back_to_a_plausible_property()
    {
        var examples = QueryExampleBuilder.Build([Person(property: null)], "studio_events");

        examples.Where[0].Snippet.Should().Contain(QueryExampleBuilder.FallbackPropertyName);
    }

    [Fact]
    public void The_statements_name_this_stores_own_tables_and_event_schema()
    {
        var examples = QueryExampleBuilder.Build([Person(), Tag()], "studio_events");

        examples.Sql[0].Snippet.Should().Be("select count(*) from \"studio\".\"mt_doc_person\"");
        examples.Sql[1].Snippet.Should().Contain("\"studio_events\".\"mt_events\"").And.Contain("group by type");
        examples.Sql[2].Snippet.Should().Be(
            "select * from \"studio\".\"mt_doc_person\" order by mt_last_modified desc limit 20");
    }

    /// <summary>
    /// A store with <c>DisableInformationalFields()</c> has no <c>mt_last_modified</c> column at all, and
    /// an example naming a column that is not there would be worse than no example.
    /// </summary>
    [Fact]
    public void No_example_names_mt_last_modified_when_the_column_is_not_there()
    {
        var examples = QueryExampleBuilder.Build([Person(lastModified: false)], "studio_events");

        examples.Where.Should().HaveCount(3);
        examples.Sql.Should().HaveCount(3);
        examples.Where.Should().AllSatisfy(x => x.Snippet.Should().NotContain("mt_last_modified"));
        examples.Sql.Should().AllSatisfy(x => x.Snippet.Should().NotContain("mt_last_modified"));
    }

    /// <summary>
    /// A store with no event store configured still gets its document examples; the events one is simply
    /// absent rather than pointing at a schema that does not exist.
    /// </summary>
    [Fact]
    public void A_store_with_no_event_schema_gets_no_events_example()
    {
        var examples = QueryExampleBuilder.Build([Person()], eventsSchema: null);

        examples.Sql.Should().HaveCount(2);
        examples.Sql.Should().AllSatisfy(x => x.Snippet.Should().NotContain("mt_events"));
    }

    [Fact]
    public void A_property_name_with_a_quote_in_it_cannot_break_out_of_the_snippet()
    {
        var examples = QueryExampleBuilder.Build([Person(property: "O'Brien")], null);

        examples.Where[0].Snippet.Should().Be("where data ->> 'O''Brien' = 'some value'");
    }
}
