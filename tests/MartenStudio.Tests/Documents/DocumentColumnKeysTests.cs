using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// Column keys are what <c>?cols=</c> carries and what local storage remembers, so they have to round-trip
/// — and, more importantly, a key that this collection has no column for has to be dropped rather than
/// trusted, because it arrives from a URL somebody edited.
/// </summary>
public class DocumentColumnKeysTests
{
    [Fact]
    public void Every_kind_of_column_round_trips_through_its_key()
    {
        var table = SqlTestTables.FullyFeatured();

        foreach (var column in new DocumentColumn[]
        {
            DocumentColumn.ById,
            new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            new DocumentColumn.Duplicated("email"),
            new DocumentColumn.JsonPath(["Address", "City"]),
        })
        {
            var key = DocumentColumnKeys.KeyFor(column);
            var resolved = DocumentColumnKeys.Resolve(table, key);

            resolved.Should().Be(column, "'{0}' must resolve back to itself", key);
            DocumentColumnKeys.KeyFor(resolved!).Should().Be(key);
        }
    }

    [Fact]
    public void The_keys_are_readable_because_they_end_up_in_a_url()
    {
        DocumentColumnKeys.KeyFor(DocumentColumn.ById).Should().Be("id");
        DocumentColumnKeys.KeyFor(new DocumentColumn.Metadata(DocumentMetadataColumn.Version)).Should().Be("meta:Version");
        DocumentColumnKeys.KeyFor(new DocumentColumn.Duplicated("address_city")).Should().Be("dup:address_city");
        DocumentColumnKeys.KeyFor(new DocumentColumn.JsonPath(["Address", "City"])).Should().Be("json:Address.City");
    }

    [Fact]
    public void A_metadata_column_the_store_disabled_does_not_resolve()
    {
        // DisableInformationalFields leaves a table of id and data; a bookmarked ?cols=meta:LastModified
        // must become "no such column" rather than a select list that fails at the database.
        var bare = SqlTestTables.MetadataLess();

        DocumentColumnKeys.Resolve(bare, "meta:LastModified").Should().BeNull();
        DocumentColumnKeys.Resolve(SqlTestTables.FullyFeatured(), "meta:LastModified").Should().NotBeNull();
    }

    [Fact]
    public void A_duplicated_column_the_table_does_not_have_does_not_resolve()
    {
        var table = SqlTestTables.FullyFeatured();

        DocumentColumnKeys.Resolve(table, "dup:email").Should().NotBeNull();
        DocumentColumnKeys.Resolve(table, "dup:no_such_column").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something else entirely")]
    [InlineData("json:")]
    [InlineData("meta:NotAColumn")]
    public void Anything_else_resolves_to_nothing(string? key)
    {
        DocumentColumnKeys.Resolve(SqlTestTables.FullyFeatured(), key).Should().BeNull();
    }

    [Fact]
    public void A_metadata_key_is_matched_without_regard_to_case()
    {
        DocumentColumnKeys.Resolve(SqlTestTables.FullyFeatured(), "META:lastmodified")
            .Should().Be(new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));
    }

    [Fact]
    public void The_header_says_what_kind_of_column_it_is_so_a_cell_knows_how_to_draw_itself()
    {
        DocumentColumnKeys.HeaderFor(DocumentColumn.ById).Kind.Should().Be(DocumentColumnKind.Id);
        DocumentColumnKeys.HeaderFor(new DocumentColumn.Metadata(DocumentMetadataColumn.Version)).Kind
            .Should().Be(DocumentColumnKind.Metadata);
        DocumentColumnKeys.HeaderFor(new DocumentColumn.Duplicated("email")).Kind
            .Should().Be(DocumentColumnKind.Duplicated);
        DocumentColumnKeys.HeaderFor(new DocumentColumn.JsonPath(["a"])).Kind
            .Should().Be(DocumentColumnKind.Json);
    }
}
