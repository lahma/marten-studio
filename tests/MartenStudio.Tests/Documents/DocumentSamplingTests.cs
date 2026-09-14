using System.Globalization;

using MartenStudio.Services.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The three pieces of the list read that are pure functions: how a cell is rendered, how a keyset cursor
/// value is rendered, and what the column chooser learns from the page that is on screen.
/// </summary>
public class DocumentSamplingTests
{
    private static DocumentRow Row(string id, string? json) => new() { Id = id, Json = json };

    [Fact]
    public void Json_keys_are_sampled_from_the_page_and_counted()
    {
        // Sampled rather than discovered: finding out what properties a collection has means reading every
        // document in it, which is the query this screen exists to help people avoid.
        var suggestions = DocumentDataService.SampleJsonKeys(
        [
            Row("1", """{"Name":"a","Email":"a@b.c"}"""),
            Row("2", """{"Name":"b","Tags":[]}"""),
            Row("3", """{"Name":"c"}"""),
        ]);

        suggestions.Select(x => x.Name).Should().Equal("Name", "Email", "Tags");
        suggestions[0].Frequency.Should().Be(3);
        suggestions[1].Frequency.Should().Be(1);
    }

    [Fact]
    public void A_row_whose_data_was_not_inlined_contributes_nothing_and_breaks_nothing()
    {
        DocumentDataService.SampleJsonKeys([Row("1", null), Row("2", """{"A":1}""")])
            .Should().ContainSingle().Which.Name.Should().Be("A");
    }

    [Fact]
    public void A_row_whose_data_is_not_valid_json_contributes_nothing_and_breaks_nothing()
    {
        // A database somebody migrated by hand really does contain these.
        DocumentDataService.SampleJsonKeys([Row("1", "{oh no"), Row("2", """{"A":1}""")])
            .Should().ContainSingle().Which.Name.Should().Be("A");
    }

    [Fact]
    public void A_document_that_is_not_an_object_contributes_nothing()
    {
        DocumentDataService.SampleJsonKeys([Row("1", "[1,2,3]")]).Should().BeEmpty();
    }

    [Fact]
    public void The_suggestion_list_is_capped_so_a_wide_document_cannot_fill_the_chooser()
    {
        var wide = "{" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"\"k{i}\":1")) + "}";

        DocumentDataService.SampleJsonKeys([Row("1", wide)]).Should().HaveCount(50);
    }

    [Fact]
    public void A_cell_is_rendered_invariantly_and_a_timestamp_keeps_its_offset()
    {
        DocumentDataService.DisplayValue(null).Should().BeNull();
        DocumentDataService.DisplayValue(DBNull.Value).Should().BeNull();
        DocumentDataService.DisplayValue(true).Should().Be("true");
        DocumentDataService.DisplayValue(1234.5m).Should().Be("1234.5");
        DocumentDataService.DisplayValue(new Guid("8f1d5a6e-0000-0000-0000-000000000001"))
            .Should().Be("8f1d5a6e-0000-0000-0000-000000000001");
        DocumentDataService.DisplayValue(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3)))
            .Should().Be("2026-09-14 12:00:00.000+03:00");
    }

    [Fact]
    public void A_cursor_value_is_round_trippable_because_it_is_bound_back_to_the_sort_column()
    {
        var instant = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

        var text = DocumentDataService.CursorValue(instant);

        text.Should().Be(instant.ToString("O", CultureInfo.InvariantCulture));
        DateTimeOffset.Parse(text!, CultureInfo.InvariantCulture).Should().Be(instant);
    }

    [Fact]
    public void A_stored_dotnet_type_that_names_the_mapped_type_is_not_a_mismatch_however_the_assembly_is_versioned()
    {
        // Marten writes the assembly-qualified name, so comparing the whole string would fire on every
        // store whose assembly was rebuilt - which is every store.
        DocumentDataService.IsDotNetTypeMismatch(
            "MartenStudio.Tests.Documents.DocumentSamplingTests+Sample, MartenStudio.Tests, Version=9.9.9.9",
            typeof(Sample)).Should().BeFalse();
    }

    [Fact]
    public void A_stored_dotnet_type_that_names_a_different_type_is_a_mismatch()
    {
        DocumentDataService.IsDotNetTypeMismatch("Somewhere.Else.Renamed, Other", typeof(Sample)).Should().BeTrue();
    }

    [Fact]
    public void A_hierarchy_row_naming_its_subclass_is_not_a_mismatch()
    {
        DocumentDataService.IsDotNetTypeMismatch(
            "MartenStudio.Tests.Documents.DocumentSamplingTests+Sample, X", typeof(Sample)).Should().BeFalse();
    }

    [Fact]
    public void A_missing_column_or_an_unknown_type_is_not_a_mismatch()
    {
        DocumentDataService.IsDotNetTypeMismatch(null, typeof(Sample)).Should().BeFalse();
        DocumentDataService.IsDotNetTypeMismatch("Anything", null).Should().BeFalse();
    }

    private sealed class Sample;
}
