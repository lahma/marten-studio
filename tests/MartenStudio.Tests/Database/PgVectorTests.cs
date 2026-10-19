using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// PGV: what the studio makes of pgvector's catalog entries and of its values' text - the column types and their
/// dimensions, the operator classes and the distances they serve, the build parameters with pgvector's defaults, and
/// a value collapsed to its first components.
/// </summary>
public class PgVectorTests
{
    [Theory]
    [InlineData("vector", 1536, "Vector", "vector(1536)")]
    [InlineData("halfvec", 3, "HalfVector", "halfvec(3)")]
    [InlineData("sparsevec", 30000, "SparseVector", "sparsevec(30000)")]
    [InlineData("vector", -1, "Vector", "vector")]
    public void A_column_of_a_type_the_vector_extension_owns_is_spelled_with_its_type_modifier(
        string typeName, int typeModifier, string kind, string spelling)
    {
        PgVectorColumn? column = PgVector.Column("vector", typeName, typeModifier);

        column.Should().NotBeNull();
        column!.Kind.ToString().Should().Be(kind);
        column.Spelling.Should().Be(spelling);
        column.Dimensions.Should().Be(typeModifier > 0 ? typeModifier : null);
    }

    [Theory]
    [InlineData(null, "vector")]
    [InlineData("postgis", "vector")]
    [InlineData("vector", "bit")]
    [InlineData("vector", "text")]
    public void A_type_called_vector_that_pgvector_does_not_own_is_not_a_vector_column(string? extension, string typeName) =>
        PgVector.Column(extension, typeName, 3).Should().BeNull("the extension that owns the type decides, never its name");

    [Fact]
    public void A_columns_description_says_its_dimensions_come_from_the_type_and_not_from_a_value()
    {
        PgVector.Column("vector", "vector", 1536)!.Description.Should()
            .Contain("1,536 dimensions").And.Contain("type modifier").And.Contain("no value is read");

        PgVector.Column("vector", "vector", -1)!.Description.Should().Contain("of any length");
        PgVector.Column("vector", "halfvec", 3)!.Description.Should().Contain("half-precision");
    }

    [Theory]
    [InlineData("vector_l2_ops", "L2 distance", "<->")]
    [InlineData("vector_ip_ops", "negative inner product", "<#>")]
    [InlineData("vector_cosine_ops", "cosine distance", "<=>")]
    [InlineData("vector_l1_ops", "L1 distance", "<+>")]
    [InlineData("halfvec_l2_ops", "L2 distance", "<->")]
    [InlineData("halfvec_cosine_ops", "cosine distance", "<=>")]
    [InlineData("sparsevec_ip_ops", "negative inner product", "<#>")]
    [InlineData("bit_hamming_ops", "Hamming distance", "<~>")]
    [InlineData("bit_jaccard_ops", "Jaccard distance", "<%>")]
    public void Every_operator_class_pgvector_ships_names_the_distance_and_operator_it_serves(string operatorClass, string distance, string op)
    {
        PgVectorDistance? found = PgVector.DistanceOf(operatorClass);

        found.Should().Be(new PgVectorDistance(distance, op));
    }

    [Theory]
    [InlineData("int4_ops")]
    [InlineData("jsonb_path_ops")]
    [InlineData("")]
    [InlineData(null)]
    public void An_operator_class_the_studio_does_not_know_serves_nothing_it_will_name(string? operatorClass) =>
        PgVector.DistanceOf(operatorClass).Should().BeNull();

    [Fact]
    public void An_HNSW_index_with_no_parameters_shows_pgvectors_defaults_and_says_they_are_defaults()
    {
        PgVectorIndex? index = PgVector.Index(true, "hnsw", ["embedding"], ["vector_cosine_ops"], null);

        index.Should().NotBeNull();
        index!.Label.Should().Be("HNSW");
        index.Columns.Should().ContainSingle().Which.Should().Be(
            new PgVectorIndexColumn("embedding", "vector_cosine_ops", new PgVectorDistance("cosine distance", "<=>")));

        index.Parameters.Select(static x => (x.Name, x.Value, x.IsDefault)).Should().Equal(
            ("m", "16", true),
            ("ef_construction", "64", true));
    }

    [Fact]
    public void An_HNSW_index_with_its_parameters_set_shows_what_reloptions_holds_in_pgvectors_order()
    {
        PgVectorIndex index = PgVector.Index(true, "hnsw", ["embedding"], ["vector_l2_ops"], ["ef_construction=128", "m=24"])!;

        index.Parameters.Select(static x => (x.Name, x.Value, x.IsDefault)).Should().Equal(
            ("m", "24", false),
            ("ef_construction", "128", false));
    }

    [Fact]
    public void An_HNSW_index_with_one_parameter_set_defaults_only_the_other()
    {
        PgVectorIndex index = PgVector.Index(true, "hnsw", ["e"], ["halfvec_l2_ops"], ["m=8"])!;

        index.Parameters.Select(static x => (x.Name, x.Value, x.IsDefault)).Should().Equal(
            ("m", "8", false),
            ("ef_construction", "64", true));
    }

    [Fact]
    public void An_IVFFlat_index_shows_lists_set_or_defaulted()
    {
        PgVector.Index(true, "ivfflat", ["e"], ["vector_ip_ops"], null)!.Parameters
            .Should().ContainSingle().Which.Should().Match<PgVectorIndexParameter>(static x => x.Name == "lists" && x.Value == "100" && x.IsDefault);

        PgVectorIndex set = PgVector.Index(true, "ivfflat", ["e"], ["vector_ip_ops"], ["lists=10"])!;

        set.Label.Should().Be("IVFFlat");
        set.Parameters.Should().ContainSingle().Which.Should().Match<PgVectorIndexParameter>(static x => x.Name == "lists" && x.Value == "10" && !x.IsDefault);
    }

    [Fact]
    public void A_parameter_pgvector_does_not_document_is_shown_as_it_is_after_its_own()
    {
        PgVectorIndex index = PgVector.Index(true, "ivfflat", ["e"], ["vector_l2_ops"], ["lists=50", "fillfactor=90"])!;

        index.Parameters.Select(static x => (x.Name, x.Value, x.IsDefault, x.Meaning.Length > 0)).Should().Equal(
            ("lists", "50", false, true),
            ("fillfactor", "90", false, false));
    }

    [Fact]
    public void Every_known_parameter_says_what_it_does()
    {
        PgVector.Parameters("hnsw", null).Should().OnlyContain(static x => x.Meaning.Length > 0);
        PgVector.Parameters("ivfflat", null).Should().OnlyContain(static x => x.Meaning.Length > 0);
    }

    [Fact]
    public void A_two_column_index_keeps_each_columns_operator_class_in_key_order()
    {
        PgVectorIndex index = PgVector.Index(true, "hnsw", ["a", "b"], ["vector_l2_ops", "halfvec_cosine_ops"], null)!;

        index.Columns.Select(static x => x.OperatorClass).Should().Equal("vector_l2_ops", "halfvec_cosine_ops");
        index.Columns.Select(static x => x.Definition).Should().Equal("a", "b");
    }

    [Theory]
    [InlineData(false, "hnsw")]
    [InlineData(true, "btree")]
    [InlineData(true, "gin")]
    [InlineData(false, "btree")]
    public void Only_an_hnsw_or_ivfflat_access_method_the_vector_extension_owns_is_described(bool owned, string accessMethod) =>
        PgVector.Index(owned, accessMethod, ["e"], ["vector_l2_ops"], ["m=4"]).Should().BeNull(
            "another extension's hnsw has other parameters and other defaults, and a btree is not pgvector's");

    [Fact]
    public void The_defaults_are_pgvectors_documented_ones() =>
        (PgVector.HnswDefaultM, PgVector.HnswDefaultEfConstruction, PgVector.IvfFlatDefaultLists).Should().Be((16, 64, 100));

    [Fact]
    public void A_long_vector_is_collapsed_to_its_first_three_components()
    {
        PgVectorPreview preview = PgVector.Preview("[0.0123,-0.0441,0.2,0.7,0.5]", cut: false)!;

        preview.Text.Should().Be("[0.0123, -0.0441, 0.2, …]");
        preview.Shown.Should().Be(3);
        preview.Total.Should().Be(5);
        preview.Complete.Should().BeFalse();

        PgVector.Collapsed(PgVector.Column("vector", "vector", 5)!, preview).Should().Be("vector(5) [0.0123, -0.0441, 0.2, …]");
    }

    [Fact]
    public void A_vector_of_three_or_fewer_is_shown_whole()
    {
        PgVectorPreview preview = PgVector.Preview("[1,2,3]", cut: false)!;

        preview.Text.Should().Be("[1, 2, 3]");
        preview.Complete.Should().BeTrue();

        PgVector.Preview("[]", cut: false)!.Text.Should().Be("[]");
    }

    [Fact]
    public void A_vector_the_server_cut_is_never_counted_and_its_half_number_is_never_shown()
    {
        PgVectorPreview preview = PgVector.Preview("[0.1,0.2,0.3,0.4,0.5", cut: true)!;

        preview.Text.Should().Be("[0.1, 0.2, 0.3, …]");
        preview.Total.Should().BeNull("a value cut on the server is not counted");

        PgVector.Preview("[0.1,0.2,0.3,0.4,0.5…", cut: true)!.Text.Should().Be("[0.1, 0.2, 0.3, …]", "the reader marks its cut with an ellipsis");
        PgVector.Preview("[0.1,0.2,…", cut: true)!.Text.Should().Be("[0.1, 0.2, …]");

        PgVectorPreview short_ = PgVector.Preview("[0.1,0.2", cut: true)!;
        short_.Text.Should().Be("[0.1, …]", "\"0.2\" may be the start of 0.25 - only components ended by a comma are shown");
        short_.Shown.Should().Be(1);
    }

    [Fact]
    public void A_cut_value_that_reached_its_closing_bracket_is_still_not_counted_and_keeps_no_suffix()
    {
        PgVectorPreview sparse = PgVector.Preview("{1:0.5,7:0.25}/10", cut: true)!;

        sparse.Text.Should().Be("{1:0.5, 7:0.25, …}", "\"/10\" may be the start of \"/1000\" - a cut value's dimensions are not read");
        sparse.Total.Should().BeNull();
        sparse.Complete.Should().BeFalse();

        PgVectorPreview dense = PgVector.Preview("[1,2]…", cut: true)!;

        dense.Text.Should().Be("[1, 2, …]");
        dense.Total.Should().BeNull("a value the server cut is never counted, wherever the cut fell");
    }

    [Fact]
    public void Scientific_notation_and_negatives_are_components()
    {
        PgVector.Preview("[1e-05,-2.5E+10,3]", cut: false)!.Text.Should().Be("[1e-05, -2.5E+10, 3]");
    }

    [Fact]
    public void A_sparse_vector_shows_its_first_entries_and_its_dimensions()
    {
        PgVectorPreview preview = PgVector.Preview("{1:0.5,7:0.25,9:1,12:3}/1000", cut: false)!;

        preview.Text.Should().Be("{1:0.5, 7:0.25, 9:1, …}/1000");
        preview.Total.Should().Be(4);

        PgVector.Preview("{1:0.5,7:0.25}/5", cut: false)!.Text.Should().Be("{1:0.5, 7:0.25}/5");
        PgVector.Preview("{}/5", cut: false)!.Text.Should().Be("{}/5");
        PgVector.Preview("{1:0.5,7:0.25,9:1,12:", cut: true)!.Text.Should().Be("{1:0.5, 7:0.25, 9:1, …}");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("[a,b,c]")]
    [InlineData("[1,,2]")]
    [InlineData("[1,2,]")]
    [InlineData("[1,2] trailing")]
    [InlineData("{1:0.5}")]
    [InlineData("{x:0.5}/3")]
    [InlineData("[1,2")]
    [InlineData("")]
    [InlineData(null)]
    public void Text_that_is_not_pgvectors_output_is_not_collapsed(string? text) =>
        PgVector.Preview(text, cut: false).Should().BeNull("only a value whole and in pgvector's own shape is read as one");

    [Fact]
    public void Schema_Tables_attaches_vector_columns_only_to_the_tables_it_lists()
    {
        DatabaseObjectOwnership other = new(DatabaseObjectOwner.Other);
        MartenStudio.Services.Schema.TableStats listed = new("app", "items", 0, 0, 0, -1, null, null, null, null, null, null, null, null, other);
        MartenStudio.Services.Schema.TableStats plain = listed with { Table = "plain" };

        IReadOnlyList<MartenStudio.Services.Schema.TableStats> tables = MartenStudio.Services.Schema.SchemaDataService.WithVectorColumns(
            [listed, plain],
            [
                new MartenStudio.Internal.Sql.VectorColumnRow("app", "items", "embedding", "vector", 1536),
                new MartenStudio.Internal.Sql.VectorColumnRow("app", "items", "summary", "halfvec", -1),
                new MartenStudio.Internal.Sql.VectorColumnRow("app", "items", "odd", "text", 3),
                new MartenStudio.Internal.Sql.VectorColumnRow("app", "mt_doc_hidden", "embedding", "vector", 3),
            ]);

        tables.Should().HaveCount(2, "a hidden type's table or a partition the assembler left out is not brought back");
        tables[0].VectorColumns.Select(static x => x.Text).Should().Equal("embedding vector(1536)", "summary halfvec");
        tables[1].VectorColumns.Should().BeEmpty();
    }

    [Fact]
    public void A_collapsed_values_description_says_how_many_there_are_when_it_knows()
    {
        PgVectorColumn column = PgVector.Column("vector", "vector", 1536)!;

        PgVector.Describe(column, PgVector.Preview("[1,2,3,4]", cut: false)!).Should().Be("vector(1536): 4 components, the first 3 shown.");
        PgVector.Describe(column, PgVector.Preview("[1,2,3,4", cut: true)!).Should().Be("vector(1536): 1,536 dimensions, the first 3 shown.");

        PgVectorColumn sparse = PgVector.Column("vector", "sparsevec", 9)!;
        PgVector.Describe(sparse, PgVector.Preview("{1:1}/9", cut: false)!).Should().Be("sparsevec(9): 1 non-zero entry, the first 1 shown.");
    }
}
