using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The URL is the whole state of the documents browser (plan §3.2, D9), so these are the tests that keep a
/// pasted link reopening the same screen.
/// </summary>
public class DocumentLinksTests
{
    private static readonly MartenStudioOptions Options = new();

    [Fact]
    public void A_collection_link_carries_the_scope()
    {
        var url = DocumentLinks.ToCollection(Options, new StudioScope("default", "localhost.marten", "acme"), "customer");

        url.Should().StartWith("marten/documents/customer?");
        url.Should().Contain("store=default");
        url.Should().Contain("db=localhost.marten");
        url.Should().Contain("tenant=acme");
    }

    [Fact]
    public void A_document_link_puts_the_id_in_the_query_string()
    {
        // D9: the router has no catch-all segment and Marten ids are frequently strings with a '/' in them.
        var url = DocumentLinks.ToDocument(Options, null, "product", "SKU/001");

        url.Should().StartWith("marten/documents/product/doc?");
        url.Should().Contain("id=SKU%2F001");
    }

    [Fact]
    public void An_alias_with_a_slash_in_it_is_escaped_into_one_segment()
    {
        var url = DocumentLinks.ToCollection(Options, null, "odd/alias");

        url.Should().Be("marten/documents/odd%2Falias");
    }

    [Fact]
    public void The_query_link_carries_the_type_and_the_filter()
    {
        var url = DocumentLinks.ToQuery(Options, null, "customer", "Email = 'a@b.c'");

        url.Should().StartWith("marten/query?");
        url.Should().Contain("type=customer");
        url.Should().Contain("where=");
    }

    [Theory]
    [InlineData(null, "2026-01-01T00:00:00.0000000+00:00")]
    [InlineData("Customer 01", "42")]
    [InlineData("has|a|pipe and a : colon", "SKU-001")]
    [InlineData("", "")]
    public void A_cursor_round_trips_whatever_is_in_it(string? sortValue, string lastId)
    {
        // The two halves are arbitrary text, which is why the encoding is length-prefixed rather than
        // delimited: a delimiter a value can contain eventually splits in the wrong place.
        var cursor = new DocumentKeysetCursor(sortValue, lastId);

        var encoded = DocumentLinks.Encode(cursor);
        var decoded = DocumentLinks.Decode(encoded);

        if (lastId.Length == 0)
        {
            decoded.Should().BeNull("a cursor with no id cannot page");
            return;
        }

        decoded.Should().NotBeNull();
        decoded!.SortValue.Should().Be(sortValue);
        decoded.LastId.Should().Be(lastId);
    }

    [Fact]
    public void A_null_sort_value_survives_the_round_trip_as_null_and_not_as_empty()
    {
        // They mean different things to the keyset predicate: null sorts last, empty is a value.
        DocumentLinks.Decode(DocumentLinks.Encode(new DocumentKeysetCursor(null, "x")))!.SortValue.Should().BeNull();
        DocumentLinks.Decode(DocumentLinks.Encode(new DocumentKeysetCursor(string.Empty, "x")))!.SortValue.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all !!")]
    [InlineData("Zm9v")]
    public void A_cursor_that_is_not_one_is_a_null_rather_than_a_throw(string? value)
    {
        // It arrives from a URL somebody edited, so it has to be a value.
        DocumentLinks.Decode(value).Should().BeNull();
    }

    // The expected values are named rather than typed because the enums are internal and a public xunit
    // theory method may not take one (CS0051).
    [Theory]
    [InlineData("exclude", "Exclude")]
    [InlineData("include", "Include")]
    [InlineData("only", "Only")]
    [InlineData("ONLY", "Only")]
    [InlineData(null, "Exclude")]
    [InlineData("nonsense", "Exclude")]
    public void The_deleted_tri_state_round_trips_through_its_token(string? token, string expected)
    {
        var parsed = DocumentLinks.ParseDeleted(token);

        parsed.ToString().Should().Be(expected);
        DocumentLinks.ParseDeleted(DocumentLinks.DeletedToken(parsed)).Should().Be(parsed);
    }

    [Theory]
    [InlineData("asc", "Ascending")]
    [InlineData("desc", "Descending")]
    [InlineData(null, "Descending")]
    public void The_sort_direction_round_trips_through_its_token(string? token, string expected)
    {
        var parsed = DocumentLinks.ParseDirection(token);

        parsed.ToString().Should().Be(expected);
        DocumentLinks.ParseDirection(DocumentLinks.DirectionToken(parsed)).Should().Be(parsed);
    }

    [Fact]
    public void The_column_list_round_trips_and_ignores_blanks()
    {
        DocumentLinks.ParseColumns("id, meta:LastModified ,,dup:email")
            .Should().Equal("id", "meta:LastModified", "dup:email");

        DocumentLinks.FormatColumns(["id", "dup:email"]).Should().Be("id,dup:email");
        DocumentLinks.ParseColumns(null).Should().BeEmpty();
    }
}
