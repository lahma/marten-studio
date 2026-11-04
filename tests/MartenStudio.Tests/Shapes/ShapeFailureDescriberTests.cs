using System.Text.Json;
using System.Text.Json.Serialization;

using MartenStudio.Services.Shapes;

namespace MartenStudio.Tests.Shapes;

/// <summary>How a failure is described: a type, a path and the JSON kind stored there, and never a message (D-SHAPE S6).</summary>
public class ShapeFailureDescriberTests
{
    private const string Sentinel = "SENTINEL-7f3a";

    [Fact]
    public void A_json_exception_gives_its_path_and_the_stored_kind_at_it()
    {
        const string stored = """{"Total":"abc"}""";
        Exception exception = Throws(() => JsonSerializer.Deserialize<Order>(stored));

        ShapeFailure failure = ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception, stored);

        failure.ExceptionType.Should().Be("JsonException");
        failure.Path.Should().Be("$.Total");
        failure.StoredKind.Should().Be(JsonValueKind.String);
        failure.Kind.Should().Be(ShapeFailureKind.Unreadable);
    }

    [Fact]
    public void An_array_index_in_the_path_is_a_star_and_the_stored_kind_is_still_found_by_the_real_index()
    {
        const string stored = """{"Lines":[{"Quantity":1},{"Quantity":"two"}]}""";
        Exception exception = Throws(() => JsonSerializer.Deserialize<Order>(stored));

        ShapeFailure failure = ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception, stored);

        failure.Path.Should().Be("$.Lines[*].Quantity");
        failure.StoredKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void An_exception_with_a_path_property_is_found_through_an_inner_exception()
    {
        // Shaped like Newtonsoft's JsonSerializationException, which the studio does not reference: no leading $, a bare index.
        Exception exception = new InvalidOperationException("outer", new NewtonsoftShaped("items[0].sku"));

        ShapeFailure failure = ShapeFailureDescriber.Describe(
            ShapeFailureKind.Unreadable, exception, """{"items":[{"sku":7}]}""");

        failure.ExceptionType.Should().Be(nameof(NewtonsoftShaped));
        failure.Path.Should().Be("$.items[*].sku");
        failure.StoredKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public void A_path_that_walks_nowhere_in_the_stored_item_gives_no_kind()
    {
        ShapeFailure failure = ShapeFailureDescriber.Describe(
            ShapeFailureKind.Unreadable, new NewtonsoftShaped("missing.deeper"), """{"a":1}""");

        failure.Path.Should().Be("$.missing.deeper");
        failure.StoredKind.Should().BeNull();
    }

    [Fact]
    public void An_exception_with_no_path_has_a_type_and_nothing_else()
    {
        ShapeFailure failure = ShapeFailureDescriber.Describe(
            ShapeFailureKind.NotWritable, new NotSupportedException("a message"), """{"a":1}""");

        failure.ExceptionType.Should().Be("NotSupportedException");
        failure.Path.Should().BeNull();
        failure.StoredKind.Should().BeNull();
    }

    [Fact]
    public void A_converter_that_throws_with_the_sentinel_in_its_message_leaks_it_nowhere()
    {
        string stored = $$"""{"Secret":"{{Sentinel}}"}""";
        Exception exception = Throws(() => JsonSerializer.Deserialize<Leaky>(stored));

        ShapeFailure failure = ShapeFailureDescriber.Describe(ShapeFailureKind.Unreadable, exception, stored);

        // The exception carries it; the description must not.
        exception.ToString().Should().Contain(Sentinel, "the arrangement is real: the message does quote the value");
        ShapeSentinel.Strings(failure).Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("$.a.b", "$.a.b")]
    [InlineData("a.b", "$.a.b")]
    [InlineData("[3].a", "$[*].a")]
    [InlineData("$.a[12].b[0]", "$.a[*].b[*]")]
    public void Paths_are_normalized_to_the_usual_spelling(string raw, string expected) =>
        ShapeFailureDescriber.Normalize(raw).Should().Be(expected);

    private static Exception Throws(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The arrangement was meant to throw.");
    }

    private sealed class NewtonsoftShaped(string path) : Exception("a message that quotes a value")
    {
        public string Path { get; } = path;
    }

    private sealed class Order
    {
        public decimal Total { get; set; }

        public List<Line> Lines { get; set; } = [];
    }

    private sealed class Line
    {
        public int Quantity { get; set; }
    }

    private sealed class Leaky
    {
        [JsonConverter(typeof(Quoting))]
        public string Secret { get; set; } = string.Empty;
    }

    private sealed class Quoting : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new InvalidOperationException("cannot convert '" + reader.GetString() + "'");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
