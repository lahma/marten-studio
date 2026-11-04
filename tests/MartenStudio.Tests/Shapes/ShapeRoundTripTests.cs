using System.Text.Json;
using System.Text.Json.Serialization;

using Marten;

using MartenStudio.Services.Shapes;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Shapes;

/// <summary>The round trip through a real Marten serializer and no database (D-SHAPE S5).</summary>
public class ShapeRoundTripTests : IDisposable
{
    private const string Sentinel = "SENTINEL-7f3a";

    private readonly DocumentStore store = DocumentStore.For(options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = "shape_round_trip";
        options.Schema.For<ShapeOrder>();
        options.Schema.For<ShapeVehicle>().AddSubClass<ShapeCar>().AddSubClass<ShapeTruck>();
        options.Schema.For<ShapeLeaky>();
    });

    private IReadOnlyStoreOptions Options => ((IDocumentStore) store).Options;

    public void Dispose()
    {
        store.Dispose();
        GC.SuppressFinalize(this);
    }

    private ShapeItemResult Check(Type type, string json, string? rowAlias = null) =>
        ShapeRoundTrip.Check(Options.Serializer(), Options.FindOrResolveDocumentType(type), rowAlias, json);

    [Fact]
    public void A_document_that_fits_reports_nothing()
    {
        ShapeItemResult result = Check(typeof(ShapeOrder), """{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f","Total":5,"Note":"x"}""");

        result.Failure.Should().BeNull();
        result.Members.Should().BeEmpty();
        result.TypeName.Should().Be("ShapeOrder");
    }

    [Fact]
    public void A_stored_member_the_type_no_longer_declares_is_dropped_and_a_declared_one_the_document_lacks_is_defaulted()
    {
        ShapeItemResult result = Check(
            typeof(ShapeOrder), """{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f","Total":5,"LegacyCustomer":"x"}""");

        result.Failure.Should().BeNull();
        result.Members.Should().Contain(new ShapeMember("$.LegacyCustomer", ShapeMemberKind.Dropped));
        result.Members.Should().Contain(new ShapeMember("$.Note", ShapeMemberKind.Defaulted));
    }

    [Fact]
    public void A_property_whose_type_changed_is_a_failure_with_its_path_and_the_stored_kind()
    {
        ShapeItemResult result = Check(
            typeof(ShapeOrder), """{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f","Total":"a lot"}""");

        result.Members.Should().BeEmpty();
        result.Failure.Should().NotBeNull();
        result.Failure!.Kind.Should().Be(ShapeFailureKind.Unreadable);
        result.Failure.Path.Should().Be("$.Total");
        result.Failure.StoredKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void A_hierarchy_row_is_read_as_the_subclass_its_discriminator_names()
    {
        string alias = Options.FindOrResolveDocumentType(typeof(ShapeVehicle)).AliasFor(typeof(ShapeCar));

        ShapeItemResult result = Check(
            typeof(ShapeVehicle), """{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f","Doors":4,"Gone":1}""", alias);

        result.TypeName.Should().Be("ShapeCar");
        result.Failure.Should().BeNull();
        result.Members.Should().ContainSingle().Which.Should().Be(new ShapeMember("$.Gone", ShapeMemberKind.Dropped));
    }

    [Fact]
    public void A_discriminator_no_subclass_claims_is_an_unknown_subclass_named_by_its_alias_and_filed_under_the_root()
    {
        ShapeItemResult result = Check(typeof(ShapeVehicle), """{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f"}""", "bicycle");

        result.TypeName.Should().Be("ShapeVehicle");
        result.Failure.Should().NotBeNull();
        result.Failure!.Kind.Should().Be(ShapeFailureKind.UnknownSubclass);
        result.Failure.Detail.Should().Be("bicycle");
    }

    [Fact]
    public void A_converter_that_throws_with_the_sentinel_in_its_message_leaks_it_nowhere()
    {
        ShapeItemResult result = Check(
            typeof(ShapeLeaky), $$"""{"Id":"6e0f6a39-cb5b-4ea4-a56e-d3b3fd6a2c5f","Secret":"{{Sentinel}}"}""");

        result.Failure.Should().NotBeNull();
        ShapeSentinel.Strings(result).Should().NotContain(x => x.Contains(Sentinel, StringComparison.Ordinal));
    }

    public class ShapeOrder
    {
        public Guid Id { get; set; }

        public decimal Total { get; set; }

        public string? Note { get; set; } = string.Empty;
    }

    public class ShapeVehicle
    {
        public Guid Id { get; set; }
    }

    public class ShapeCar : ShapeVehicle
    {
        public int Doors { get; set; }
    }

    public class ShapeTruck : ShapeVehicle
    {
        public int Axles { get; set; }
    }

    public class ShapeLeaky
    {
        public Guid Id { get; set; }

        [JsonConverter(typeof(QuotingConverter))]
        public string Secret { get; set; } = string.Empty;
    }

    public sealed class QuotingConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new InvalidOperationException("cannot convert '" + reader.GetString() + "'");

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}
