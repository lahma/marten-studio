using System.Text.Json;

using MartenStudio.Services.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The metadata pane's agreement dots exist to catch a row that was written without going through
/// Marten's upsert function. That only works if the studio can find the JSON property behind a duplicated
/// column — which is harder than it looks, because Marten reports a nested duplicated field's member name
/// with the names run together (<c>AddressCity</c>), and that string is ambiguous on its own.
/// </summary>
public class DuplicatedFieldPathsTests
{
    private sealed class Address
    {
        public string City { get; set; } = string.Empty;

        public string CityCode { get; set; } = string.Empty;
    }

    private sealed class Customer
    {
        public string Email { get; set; } = string.Empty;

        public Address Address { get; set; } = new();

        public int OrderCount { get; set; }
    }

    [Fact]
    public void A_top_level_member_resolves_to_a_one_segment_path()
    {
        DuplicatedFieldPaths.Resolve(typeof(Customer), "Email", null).Should().Equal("Email");
    }

    [Fact]
    public void A_nested_member_is_split_against_the_type_rather_than_guessed_from_the_string()
    {
        DuplicatedFieldPaths.Resolve(typeof(Customer), "AddressCity", null).Should().Equal("Address", "City");
        DuplicatedFieldPaths.Resolve(typeof(Customer), "AddressCityCode", null).Should().Equal("Address", "CityCode");
    }

    [Fact]
    public void The_naming_policy_is_applied_to_every_segment()
    {
        // A store using camel-case serialization writes `address.city`, and a viewer that looked for
        // `Address` would find nothing and report a disagreement that is not there.
        DuplicatedFieldPaths.Resolve(typeof(Customer), "AddressCity", JsonNamingPolicy.CamelCase)
            .Should().Equal("address", "city");
    }

    [Fact]
    public void A_member_name_that_does_not_fit_the_type_resolves_to_nothing()
    {
        DuplicatedFieldPaths.Resolve(typeof(Customer), "NotAMember", null).Should().BeNull();
        DuplicatedFieldPaths.Resolve(null, "Email", null).Should().BeNull();
    }

    [Fact]
    public void A_value_is_read_out_of_the_document_at_the_resolved_path()
    {
        const string Json = """{"Email":"a@b.c","Address":{"City":"Helsinki"},"OrderCount":3}""";

        DuplicatedFieldPaths.ReadValue(Json, ["Email"]).Should().Be("a@b.c");
        DuplicatedFieldPaths.ReadValue(Json, ["Address", "City"]).Should().Be("Helsinki");
        DuplicatedFieldPaths.ReadValue(Json, ["OrderCount"]).Should().Be("3");
        DuplicatedFieldPaths.ReadValue(Json, ["Missing"]).Should().BeNull();
        DuplicatedFieldPaths.ReadValue("not json", ["Email"]).Should().BeNull();
        DuplicatedFieldPaths.ReadValue(Json, null).Should().BeNull();
    }

    [Fact]
    public void A_json_null_reads_as_null_rather_than_as_the_text_null()
    {
        DuplicatedFieldPaths.ReadValue("""{"Email":null}""", ["Email"]).Should().BeNull();
    }

    // The expected value is named rather than typed because AgreementState is internal and a public xunit
    // theory method may not take one (CS0051).
    [Theory]
    [InlineData("a", "a", "Agrees")]
    [InlineData(null, null, "Agrees")]
    [InlineData("a", "b", "Differs")]
    [InlineData("a", null, "Differs")]
    [InlineData(null, "b", "Differs")]
    // Postgres renders a numeric as 1000.00 and System.Text.Json as 1000; that is not a drift.
    [InlineData("1000.00", "1000", "Agrees")]
    [InlineData("1000.00", "1000.01", "Differs")]
    // Both sides of a boolean arrive as true/false: DocumentDataService.DisplayValue renders the column
    // that way, so there is nothing to forgive.
    [InlineData("true", "true", "Agrees")]
    // ... and the comparison is ordinal, so a column that differs only in case differs. That is the drift
    // the dot exists to catch: the column is what an indexed filter reads, and Postgres compares it
    // case-sensitively unless somebody made it citext.
    [InlineData("ADA@EXAMPLE.COM", "ada@example.com", "Differs")]
    public void Agreement_tolerates_only_the_renderings_that_are_genuinely_the_same_value(
        string? column,
        string? json,
        string expected)
    {
        DuplicatedFieldPaths.Compare(column, json, pathResolved: true).ToString().Should().Be(expected);
    }

    [Fact]
    public void An_unresolved_path_is_unknown_rather_than_a_disagreement()
    {
        // Claiming a disagreement the studio cannot substantiate is worse than saying nothing: the whole
        // value of the dot is that a red one means something.
        DuplicatedFieldPaths.Compare("a", null, pathResolved: false).Should().Be(AgreementState.Unknown);
    }
}
