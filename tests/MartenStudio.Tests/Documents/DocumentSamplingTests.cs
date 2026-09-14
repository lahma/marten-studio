using System.Globalization;

using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Sql;

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
        DocumentDataService.CheckDotNetType(
                Plain(typeof(Sample)),
                rowAlias: null,
                "MartenStudio.Tests.Documents.DocumentSamplingTests+Sample, MartenStudio.Tests, Version=9.9.9.9")
            .Mismatch.Should().BeFalse();
    }

    [Fact]
    public void A_stored_dotnet_type_that_names_a_different_type_is_a_mismatch()
    {
        DocumentDataService.CheckDotNetType(Plain(typeof(Sample)), null, "Somewhere.Else.Renamed, Other")
            .Mismatch.Should().BeTrue();
    }

    /// <summary>
    /// The simple-name forgiveness that used to hide the one failure this check exists for.
    /// </summary>
    /// <remarks>
    /// The old comparison accepted any stored name whose last segment matched the expected simple name, so
    /// that a hierarchy's subclass rows would not all read as mismatches. It also accepted a type that had
    /// moved to a different namespace - a type Marten will not find and cannot deserialize - which is
    /// exactly the drift the pane is supposed to point at.
    /// </remarks>
    [Fact]
    public void A_type_of_the_same_name_in_another_namespace_is_a_mismatch()
    {
        DocumentDataService.CheckDotNetType(Plain(typeof(Sample)), null, "Somewhere.Else.Sample, X")
            .Mismatch.Should().BeTrue();
    }

    [Fact]
    public void A_hierarchy_row_is_compared_against_the_subclass_its_discriminator_names()
    {
        DocumentDataService.CollectionContext context = Hierarchy();

        // Marten's own alias for the subclass, not a guess at its aliasing rule: mt_doc_type holds
        // whatever AliasFor produced when the row was written, and TypeFor is the inverse of it.
        var alias = context.DocumentType!.AliasFor(typeof(SqlTestVipCustomer));

        DocumentDataService.DotNetTypeCheck subclass = DocumentDataService.CheckDotNetType(
            context, alias, typeof(SqlTestVipCustomer).FullName + ", MartenStudio.Tests");

        subclass.Mismatch.Should().BeFalse("the row says it is a VIP customer, and it is");
        subclass.Expected.Should().Be(typeof(SqlTestVipCustomer).FullName);
        subclass.Warning.Should().BeNull();

        // The root's own rows carry Marten's BASE discriminator, which TypeFor maps back to the root.
        DocumentDataService.DotNetTypeCheck root = DocumentDataService.CheckDotNetType(
            context, "BASE", typeof(SqlTestCustomer).FullName + ", MartenStudio.Tests");

        root.Mismatch.Should().BeFalse();
        root.Expected.Should().Be(typeof(SqlTestCustomer).FullName);
    }

    [Fact]
    public void A_subclass_row_holding_some_other_type_is_still_a_mismatch()
    {
        DocumentDataService.CollectionContext context = Hierarchy();

        DocumentDataService.CheckDotNetType(
                context,
                context.DocumentType!.AliasFor(typeof(SqlTestVipCustomer)),
                "Somewhere.Else.Renamed, Other")
            .Mismatch.Should().BeTrue();
    }

    /// <summary>
    /// A discriminator no subclass claims. <c>IDocumentType.TypeFor</c> throws
    /// <see cref="ArgumentOutOfRangeException"/> for it (Marten 9.35), and a row Marten cannot deserialize
    /// is a finding rather than an exception on a detail page.
    /// </summary>
    [Fact]
    public void A_discriminator_the_store_no_longer_registers_is_a_warning_and_not_a_throw()
    {
        DocumentDataService.DotNetTypeCheck check = DocumentDataService.CheckDotNetType(
            Hierarchy(), "goldcustomer", "MartenStudio.Tests.Sql.SqlTestGoldCustomer, X");

        check.Warning.Should().Contain("goldcustomer");
        check.Expected.Should().Be(typeof(SqlTestCustomer).FullName, "the root is the only type left to expect");
    }

    [Fact]
    public void A_missing_column_or_an_unknown_type_is_not_a_mismatch()
    {
        DocumentDataService.CheckDotNetType(Plain(typeof(Sample)), null, null).Mismatch.Should().BeFalse();
        DocumentDataService.CheckDotNetType(Plain(null), null, "Anything").Mismatch.Should().BeFalse();
    }

    private static DocumentDataService.CollectionContext Plain(Type? clrType) =>
        new(SqlTestTables.MetadataLess(), null, clrType, null, IsRegistered: true, null);

    private static DocumentDataService.CollectionContext Hierarchy()
    {
        IDocumentType documentType = SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().AddSubClassHierarchy(typeof(SqlTestVipCustomer)));

        return new DocumentDataService.CollectionContext(
            DocumentTableInfo.FromDocumentType(documentType),
            documentType,
            typeof(SqlTestCustomer),
            null,
            IsRegistered: true,
            null);
    }

    private sealed class Sample;
}
