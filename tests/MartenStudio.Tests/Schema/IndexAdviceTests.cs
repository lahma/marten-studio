using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// The Indexes tab's judgement: which indexes exist that nobody declared, which are declared and are
/// not there, which have never been scanned, and which collections have nothing but a primary key.
/// </summary>
/// <remarks>
/// Pure, so it is tested without a Postgres. The lists are exactly what
/// <c>SchemaDataService.IndexesAsync</c> hands it - <c>pg_index</c> joined to
/// <c>pg_stat_user_indexes</c>, and the tables <c>SchemaDeclarationReader</c>'s kind map says an apply
/// migrates (never <c>AllObjects()</c>, which applies migrations on the way).
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

    /// <summary>
    /// P7-fix B2. The old wording said <c>CreateOrUpdate</c> left an undeclared index alone and only
    /// <c>All</c> would drop it. Weasel's <c>TableDelta.WriteUpdate</c> emits <c>drop index</c> for every
    /// physical index in <c>Indexes.Extras</c>, reports the table as <c>Update</c>, and <c>Update</c> is
    /// exactly what <c>CreateOrUpdate</c> allows - proven live by dropping a hand-made index with it. A
    /// studio that tells somebody their index is safe and then drops it is worse than one that says
    /// nothing, so the wording is asserted rather than left to review.
    /// </summary>
    [Fact]
    public void An_index_nobody_declared_says_the_apply_drops_it_and_names_IgnoreIndex()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "hand_rolled_idx", scans: 400)],
            [],
            [Collection("customer", "mt_doc_customer")]);

        result.Indexes[0].DeclaredByMarten.Should().BeFalse();
        result.Indexes[0].WouldBeDropped.Should().BeTrue();
        result.WouldBeDroppedCount.Should().Be(1);

        result.Indexes[0].Suggestion.Should().Be(IndexAdvice.UndeclaredSuggestion);

        IndexAdvice.UndeclaredSuggestion.Should().Contain("DROPS it");
        IndexAdvice.UndeclaredSuggestion.Should().Contain("AutoCreate.CreateOrUpdate is not additive");
        IndexAdvice.UndeclaredSuggestion.Should().Contain("IgnoreIndex");
        IndexAdvice.UndeclaredSuggestion.Should().NotContain("Marten leaves it alone",
            "CreateOrUpdate drops it, and saying otherwise is how somebody loses an index");
    }

    [Fact]
    public void An_index_the_host_told_Marten_to_ignore_is_neither_undeclared_nor_dropped()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "hand_rolled_idx", scans: 400)],
            [],
            [Collection("customer", "mt_doc_customer")],
            managedTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Schema + ".mt_doc_customer" },
            ignoredIndexes: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Schema + ".mt_doc_customer.hand_rolled_idx" });

        result.Indexes[0].IgnoredByConfiguration.Should().BeTrue();
        result.Indexes[0].DeclaredByMarten.Should().BeTrue();
        result.Indexes[0].WouldBeDropped.Should().BeFalse();
        result.WouldBeDroppedCount.Should().Be(0);
        result.Indexes[0].Suggestion.Should().Be(IndexAdvice.IgnoredSuggestion);
    }

    /// <summary>
    /// A host's own table can live in a schema Marten owns. The studio cannot see an apply dropping its
    /// indexes, so "undeclared" must not carry the drop warning there - and must not promise the opposite
    /// either (DB-7).
    /// </summary>
    [Fact]
    public void An_index_on_a_table_Marten_does_not_manage_is_not_threatened_with_a_drop()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("host_audit_log", "host_audit_log_idx_at", scans: 12)],
            [],
            [Collection("customer", "mt_doc_customer")],
            managedTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Schema + ".mt_doc_customer" });

        result.Indexes[0].OnMartenTable.Should().BeFalse();
        result.Indexes[0].WouldBeDropped.Should().BeFalse();
        result.WouldBeDroppedCount.Should().Be(0);
        result.Indexes[0].Suggestion.Should().Be(IndexAdvice.ForeignTableSuggestion);
    }

    /// <summary>
    /// DB-7, acceptance 4. The old sentence said a table Marten does not configure is one "no migration
    /// from here touches" - false for every <c>ExtendedSchemaObjects</c> table (Marten yields them as a
    /// feature of its own and migrates them, drop index included) and unprovable for a feature added with
    /// <c>Storage.Add</c>, which the studio cannot list without applying migrations (hard rule 14). The new
    /// sentence is pinned whole, because the wording is the fix.
    /// </summary>
    [Fact]
    public void The_unmanaged_table_sentence_promises_nothing_about_what_an_apply_leaves_alone()
    {
        IndexAdvice.ForeignTableSuggestion.Should().Be(
            "This store's configuration does not declare this table, as far as the studio can see, so the " +
            "studio cannot say an apply drops this index - and cannot promise it will not: an apply also " +
            "migrates every feature added with StoreOptions.Storage.Add(...), whose tables the studio cannot " +
            "list, drop index included. It is listed because it lives in a schema this store owns.");

        IndexAdvice.ForeignTableSuggestion.Should().NotContain("no migration",
            "hard rule 14: nothing may tell a person that an apply leaves their index alone");
        IndexAdvice.ForeignTableSuggestion.Should().NotContain("touches");
    }

    /// <summary>
    /// DB-7: a projection's table and an <c>ExtendedSchemaObjects</c> table are migrated by an apply, so an
    /// index nothing declares on one is dropped - and the remedy is the Weasel table's, not a
    /// <c>Schema.For&lt;T&gt;()</c> line.
    /// </summary>
    [Fact]
    public void An_undeclared_index_on_an_extended_or_projection_table_would_be_dropped_and_says_how_to_keep_it()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [
                Row("ef_things", "ef_things_idx_name", scans: 3),
                Row("ef_things", "hand_rolled_on_extended", scans: 3),
            ],
            [new DeclaredIndex(IndexAdvice.ManagedTableAlias, IndexAdvice.ManagedTableAlias, Schema, "ef_things", "ef_things_idx_name", "CREATE INDEX ...")],
            [],
            managedTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            relationalTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Schema + ".ef_things" });

        IndexInfo declared = result.Indexes.Single(x => x.Name == "ef_things_idx_name");
        declared.OnMartenTable.Should().BeTrue("a relational table Marten migrates is a Marten-managed table");
        declared.DeclaredByMarten.Should().BeTrue();
        declared.WouldBeDropped.Should().BeFalse();

        IndexInfo handMade = result.Indexes.Single(x => x.Name == "hand_rolled_on_extended");
        handMade.OnMartenTable.Should().BeTrue();
        handMade.WouldBeDropped.Should().BeTrue();
        handMade.Suggestion.Should().Be(IndexAdvice.ManagedTableSuggestion);
        result.WouldBeDroppedCount.Should().Be(1);

        IndexAdvice.ManagedTableSuggestion.Should().Contain("DROPS it");
        IndexAdvice.ManagedTableSuggestion.Should().Contain("ExtendedSchemaObjects");
        IndexAdvice.ManagedTableSuggestion.Should().Contain("AutoCreate.CreateOrUpdate is not additive");
        IndexAdvice.ManagedTableSuggestion.Should().Contain("table.IgnoreIndex(");
    }

    /// <summary>
    /// A hidden document type's table is absent from the Indexes tab as it is from every list: its indexes,
    /// its declared-but-missing indexes and its "only a primary key" advice all name it.
    /// </summary>
    [Fact]
    public void A_hidden_types_indexes_missing_indexes_and_collection_advice_are_all_absent()
    {
        HashSet<string> hidden = new(StringComparer.OrdinalIgnoreCase) { Schema + ".mt_doc_secret" };

        SchemaIndexes result = IndexAdvice.Join(
            [
                Row("mt_doc_secret", "pkey_mt_doc_secret_id", primaryKey: true, unique: true),
                Row("mt_doc_secret", "hand_rolled_secret_idx"),
                Row("mt_doc_customer", "pkey_mt_doc_customer_id", primaryKey: true, unique: true),
            ],
            [
                Declared("secret", "mt_doc_secret", "mt_doc_secret_idx_code"),
                Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_email"),
            ],
            [Collection("secret", "mt_doc_secret"), Collection("customer", "mt_doc_customer")],
            hiddenTables: hidden);

        result.Indexes.Select(x => x.Table).Should().OnlyContain(x => x == "mt_doc_customer");
        result.Missing.Select(x => x.Name).Should().Equal("mt_doc_customer_idx_email");
        result.Unindexed.Select(x => x.Alias).Should().Equal("customer");
        result.WouldBeDroppedCount.Should().Be(0, "the hand-made index on the hidden table is not on the page at all");
    }

    /// <summary>
    /// Weasel reads <c>indisprimary</c> rows into <c>Table.PrimaryKeyName</c> and never into
    /// <c>Table.Indexes</c>, so a primary-key index is never an "extra" and is never dropped as one. The
    /// declaration set is now built from <c>StoreOptions</c>, which does not name it - so the rule lives
    /// here instead.
    /// </summary>
    [Fact]
    public void A_primary_key_on_a_Marten_table_counts_as_declared_whatever_its_name_is()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "pkey_mt_doc_customer_id", primaryKey: true, unique: true)],
            [],
            [Collection("customer", "mt_doc_customer")],
            managedTables: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Schema + ".mt_doc_customer" });

        result.Indexes[0].DeclaredByMarten.Should().BeTrue();
        result.Indexes[0].WouldBeDropped.Should().BeFalse();
        result.Indexes[0].Suggestion.Should().BeNull();
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
            new DeclaredCollection("thing", "Thing", Schema, "mt_doc_thing", null));

        suggestions[0].Should().Be("opts.Schema.For<Thing>().Index(x => x.Property);");
    }

    /// <summary>
    /// A whole table that has not been created yet is a Drift tab fact. Listing each of its declared
    /// indexes here as "missing" buries the one thing that matters, which is that the table is absent.
    /// </summary>
    [Fact]
    public void A_declared_index_on_a_table_that_does_not_exist_is_not_reported_as_missing()
    {
        SchemaIndexes result = IndexAdvice.Join(
            [Row("mt_doc_customer", "pkey_mt_doc_customer_id", primaryKey: true, unique: true)],
            [
                Declared("customer", "mt_doc_customer", "mt_doc_customer_idx_email"),
                Declared("order", "mt_doc_order", "mt_doc_order_idx_total"),
            ],
            [Collection("customer", "mt_doc_customer"), Collection("order", "mt_doc_order")]);

        result.Missing.Select(x => x.Name).Should().Equal("mt_doc_customer_idx_email");
    }

    /// <summary>
    /// A generic document type reflects as <c>Envelope`1</c>, and a suggestion nobody can paste is not a
    /// suggestion.
    /// </summary>
    [Fact]
    public void A_generic_document_type_is_named_the_way_C_sharp_spells_it()
    {
        SchemaTypeName.Of(typeof(List<int>)).Should().Be("List<Int32>");
        SchemaTypeName.Of(typeof(Dictionary<string, List<Guid>>)).Should().Be("Dictionary<String, List<Guid>>");
        SchemaTypeName.Of(typeof(Nested)).Should().Be("IndexAdviceTests.Nested");

        IndexAdvice.SuggestionsFor(new DeclaredCollection("thing", SchemaTypeName.Of(typeof(List<int>)), Schema, "t", "Count"))[0]
            .Should().Be("opts.Schema.For<List<Int32>>().Index(x => x.Count);");
    }

    /// <summary>A nested type, so the C#-friendly name has something to qualify.</summary>
    public sealed class Nested
    {
        /// <summary>Its id.</summary>
        public Guid Id { get; set; }
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
        new(alias, typeName, Schema, table, sampleMember);
}
