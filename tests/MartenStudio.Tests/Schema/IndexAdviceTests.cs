using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// The Indexes tab's judgement: which indexes exist that nobody declared, which are declared and are
/// not there, which have never been scanned, and which collections have nothing but a primary key.
/// </summary>
/// <remarks>
/// Pure, so it is tested without a Postgres. The two lists are exactly what
/// <c>SchemaDataService.IndexesAsync</c> hands it - <c>pg_index</c> joined to
/// <c>pg_stat_user_indexes</c>, and the tables Marten's own <c>AllObjects()</c> says it configures.
/// </remarks>
public class IndexAdviceTests
{
    private const string Schema = "studio";

    [Fact]
    public void An_index_the_configuration_declares_is_not_flagged_as_undeclared()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "mt_doc_customer_idx_email", scans: 12, unique: true)],
            [Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_email")],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes.Should().ContainSingle();
        result.Indexes[0].DeclaredByMarten.Should().BeTrue();
        result.Indexes[0].Suggestion.Should().BeNull();
    }

    [Fact]
    public void An_index_nobody_declared_is_flagged_and_says_what_AutoCreate_All_would_do_to_it()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "hand_rolled_idx", scans: 400)],
            [],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes[0].DeclaredByMarten.Should().BeFalse();
        result.Indexes[0].Suggestion.Should().Be(IndexAdvice.UndeclaredSuggestion);
        result.Indexes[0].Suggestion.Should().Contain("AutoCreate.All");
    }

    [Fact]
    public void An_index_the_configuration_declares_that_is_not_there_is_reported_as_missing()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "pkey_mt_doc_customer_id", primaryKey: true, unique: true)],
            [
                Declared("customer", "mt_doc_customer", "pkey_mt_doc_customer_id"),
                Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_email", "CREATE INDEX ..."),
            ],
            [Collection("customer", "mt_doc_customer")]);

        result.Missing.Should().ContainSingle();
        result.Missing[0].Name.Should().Be("mt_doc_customer_idx_email");
        result.Missing[0].CollectionAlias.Should().Be("customer");
        result.Missing[0].Definition.Should().Be("CREATE INDEX ...");
    }

    [Fact]
    public void A_never_scanned_index_is_flagged_and_the_suggestion_warns_about_reset_statistics()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "mt_doc_customer_idx_name", scans: 0)],
            [Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_name")],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes[0].NeverUsed.Should().BeTrue();
        result.NeverUsedCount.Should().Be(1);
        result.Indexes[0].Suggestion.Should().Be(IndexAdvice.NeverUsedSuggestion);
        result.Indexes[0].Suggestion.Should().Contain("pg_stat_reset()",
            "a 'never used' verdict that is really 'the statistics were reset yesterday' is how a UI " +
            "talks somebody into dropping an index they need");
    }

    [Fact]
    public void A_primary_key_and_a_unique_index_are_never_suggested_for_removal_however_unused()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [
                Row("mt_doc_customer", "pkey_mt_doc_customer_id", scans: 0, primaryKey: true, unique: true),
                Row("mt_doc_customer", "mt_doc_customer_idx_email", scans: 0, unique: true),
            ],
            [
                Declared("customer", "mt_doc_customer", "pkey_mt_doc_customer_id"),
                Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_email"),
            ],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes.Should().OnlyContain(x => x.Suggestion == null);
        result.NeverUsedCount.Should().Be(0, "dropping a constraint changes what the schema enforces");
    }

    [Fact]
    public void An_index_with_no_recorded_statistics_at_all_is_not_called_never_used()
    {
        // n_scans is null when the collector has nothing for the index, which is a different fact from
        // zero and has to be drawn differently (plan section 4.8).
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "mt_doc_customer_idx_name", scans: null)],
            [Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_name")],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes[0].NeverUsed.Should().BeFalse();
        result.Indexes[0].Suggestion.Should().BeNull();
    }

    [Fact]
    public void A_table_with_only_a_primary_key_is_flagged_with_the_StoreOptions_lines_that_would_fix_it()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_note", "pkey_mt_doc_note_id", primaryKey: true, unique: true)],
            [Declared("note", "mt_doc_note", "pkey_mt_doc_note_id")],
            [Collection("note", "mt_doc_note", typeName: "Note", sampleMember: "Text")]);

        result.Unindexed.Should().ContainSingle();
        result.Unindexed[0].Alias.Should().Be("note");
        result.Unindexed[0].Suggestions.Should().Equal(
            "opts.Schema.For<Note>().Index(x => x.Text);",
            "opts.Schema.For<Note>().Duplicate(x => x.Text);",
            "opts.Schema.For<Note>().GinIndexJsonData();",
            "opts.Schema.For<Note>().IndexLastModified();");
    }

    [Fact]
    public void A_table_with_a_second_index_is_not_flagged()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [
                Row("mt_doc_note", "pkey_mt_doc_note_id", primaryKey: true, unique: true),
                Row("mt_doc_note", "mt_doc_note_idx_text"),
            ],
            [],
            [Collection("note", "mt_doc_note")]);

        result.Unindexed.Should().BeEmpty();
    }

    [Fact]
    public void A_collection_whose_table_has_not_been_created_yet_belongs_on_the_Drift_tab_and_not_here()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [],
            [],
            [Collection("note", "mt_doc_note")]);

        result.Unindexed.Should().BeEmpty();
        result.Indexes.Should().BeEmpty();
    }

    [Fact]
    public void Every_suggestion_is_a_StoreOptions_line_and_never_a_create_index_statement()
    {
        IReadOnlyList<string> suggestions = IndexAdvice.SuggestionsFor(
            Collection("order", "mt_doc_order", typeName: "Order", sampleMember: "Status"));

        suggestions.Should().OnlyContain(x => x.StartsWith("opts.Schema.For<", StringComparison.Ordinal));
        suggestions.Should().NotContain(x => x.Contains("create index", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_type_with_no_property_worth_naming_still_produces_pasteable_lines()
    {
        IReadOnlyList<string> suggestions = IndexAdvice.SuggestionsFor(
            new DeclaredCollection("thing", "Thing", Schema, "mt_doc_thing", false, null));

        suggestions[0].Should().Be("opts.Schema.For<Thing>().Index(x => x.Property);");
    }

    private static IndexStatsRow Row(
        string table,
        string name,
        long? scans = 5,
        bool primaryKey = false,
        bool unique = false) =>
        new(Schema, table, name, $"CREATE INDEX {name} ON {Schema}.{table} USING btree (x)", 8192, scans, scans, primaryKey, unique);

    private static DeclaredIndex Declared(string alias, string table, string name, string definition = "CREATE INDEX ...") =>
        new(alias, alias, Schema, table, name, definition);

    private static DeclaredCollection Collection(
        string alias,
        string table,
        string typeName = "Thing",
        string? sampleMember = "Name") =>
        new(alias, typeName, Schema, table, false, sampleMember);
}
