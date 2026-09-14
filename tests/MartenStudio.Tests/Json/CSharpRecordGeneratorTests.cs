using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class CSharpRecordGeneratorTests
{
    [Fact]
    public void A_document_becomes_a_record_per_object_shape()
    {
        var generated = CSharpRecordGenerator.Generate(
            """
            {
              "id": "9f8b1a7e-1c3d-4a55-9f0a-6d2c1b5e3a44",
              "firstName": "Ada",
              "age": 41,
              "score": 12.5,
              "active": true,
              "createdAt": "2026-09-14T08:30:00Z",
              "address": { "street": "1 Analytical Way", "city": "Helsinki" },
              "tags": [ "a", "b" ],
              "retired": null
            }
            """,
            "Person");

        generated.Should().Be(
            """
            public sealed record Person(
                Guid Id,
                string FirstName,
                int Age,
                decimal Score,
                bool Active,
                DateTimeOffset CreatedAt,
                Address Address,
                List<string> Tags,
                object? Retired);

            public sealed record Address(
                string Street,
                string City);

            """);
    }

    [Fact]
    public void A_key_missing_from_one_sample_makes_that_member_nullable()
    {
        var generated = CSharpRecordGenerator.Generate(
            """[{"sku":"A","qty":1},{"sku":"B"}]""",
            "Line");

        generated.Should().Be(
            """
            public sealed record Line(
                string Sku,
                int? Qty);

            """);
    }

    [Fact]
    public void Samples_that_disagree_about_the_kind_come_out_as_JsonElement()
    {
        var generated = CSharpRecordGenerator.Generate("""[{"v":1},{"v":"x"}]""", "Mixed");

        generated.Should().Be(
            """
            public sealed record Mixed(
                JsonElement V);

            """);
    }

    [Fact]
    public void A_collection_member_names_its_element_record_in_the_singular()
    {
        var generated = CSharpRecordGenerator.Generate("""{"orders":[{"id":1},{"id":2}]}""", "Basket");

        generated.Should().Be(
            """
            public sealed record Basket(
                List<Order> Orders);

            public sealed record Order(
                int Id);

            """);
    }

    [Theory]
    [InlineData("""{"n":2147483648}""", "long N")]
    [InlineData("""{"n":1}""", "int N")]
    [InlineData("""{"n":1.5}""", "decimal N")]
    [InlineData("""{"n":1e400}""", "double N")]
    public void Numbers_widen_only_as_far_as_the_samples_require(string json, string expectedMember)
    {
        CSharpRecordGenerator.Generate(json, "Sample").Should().Contain("    " + expectedMember);
    }

    [Fact]
    public void Member_names_become_valid_identifiers()
    {
        var generated = CSharpRecordGenerator.Generate("""{"first-name":"a","IS_ACTIVE":true,"123":"c"}""", "Odd");

        generated.Should().Be(
            """
            public sealed record Odd(
                string FirstName,
                bool IsActive,
                string _123);

            """);
    }

    [Fact]
    public void A_member_that_would_collide_with_its_own_record_is_renamed()
    {
        var generated = CSharpRecordGenerator.Generate("""{"person":{"a":1}}""", "Person");

        generated.Should().Be(
            """
            public sealed record Person(
                Person2 PersonValue);

            public sealed record Person2(
                int A);

            """);
    }

    [Fact]
    public void Two_member_names_that_land_on_one_identifier_are_told_apart()
    {
        // JSON member names are case-sensitive and may contain anything; C# record parameters are
        // neither. Emitting the same name twice produces a record that does not compile, which is a
        // worse answer than a name the user has to rename.
        var generated = CSharpRecordGenerator.Generate("""{"first-name":"a","first_name":"b"}""", "Person");

        generated.Should().Be(
            """
            public sealed record Person(
                string FirstName,
                string FirstName2);

            """);
    }

    [Fact]
    public void Two_member_names_that_differ_only_in_case_are_told_apart_too()
    {
        var generated = CSharpRecordGenerator.Generate("""{"a":1,"A":2}""", "Sample");

        generated.Should().Be(
            """
            public sealed record Sample(
                int A,
                int A2);

            """);
    }

    [Fact]
    public void A_member_named_after_a_keyword_needs_no_escape_because_pascal_case_is_not_one()
    {
        // Every C# keyword is lower case and every generated identifier starts upper case, so `class`
        // becomes `Class` and there is no case left for an @-prefix to handle.
        var generated = CSharpRecordGenerator.Generate("""{"class":"a","int":1,"new":true}""", "Lesson");

        generated.Should().Be(
            """
            public sealed record Lesson(
                string Class,
                int Int,
                bool New);

            """);
        generated.Should().NotContain("@");
    }

    [Fact]
    public void An_empty_array_says_nothing_about_its_elements()
    {
        CSharpRecordGenerator.Generate("""{"tags":[]}""", "Sample")
            .Should().Contain("List<object> Tags");
    }

    [Fact]
    public void An_empty_object_is_still_a_record()
    {
        CSharpRecordGenerator.Generate("{}", "Blank").Should().Be("public sealed record Blank();\n");
    }

    [Fact]
    public void The_output_is_the_same_every_time()
    {
        const string Json = """{"b":1,"a":{"y":"2026-09-14T08:30:00Z"},"c":[{"d":true}]}""";

        var first = CSharpRecordGenerator.Generate(Json, "Sample");
        var second = CSharpRecordGenerator.Generate(Json, "Sample");

        second.Should().Be(first);
        first.Should().StartWith("public sealed record Sample(\n    int B,");
    }

    [Fact]
    public void Rubbish_in_gets_a_comment_out_rather_than_an_exception()
    {
        CSharpRecordGenerator.Generate("{ nope", "Sample").Should().StartWith("// This is not valid JSON");
        CSharpRecordGenerator.Generate(null, "Sample").Should().StartWith("// There is no document");
        CSharpRecordGenerator.Generate("42", "Sample").Should().Be("// A record needs an object; this document is a number.");
        CSharpRecordGenerator.Generate("""["a"]""", "Sample").Should().Be("// A record needs an object; this document is an array of non-objects.");
    }
}
