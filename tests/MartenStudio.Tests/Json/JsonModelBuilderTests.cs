using System.Text;
using System.Text.Json;
using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Json;

public class JsonModelBuilderTests
{
    private const string Sample = """
        {
          "id": "9f8b1a7e-1c3d-4a55-9f0a-6d2c1b5e3a44",
          "firstName": "Ada",
          "active": true,
          "score": 12.5,
          "retiredOn": null,
          "createdAt": "2026-09-14T08:30:00Z",
          "homepage": "https://example.com/ada",
          "address": { "street": "1 Analytical Way", "city": "Helsinki" },
          "items": [ { "sku": "A-1" }, { "sku": "A-2" } ]
        }
        """;

    [Fact]
    public void The_root_is_node_zero_and_every_node_points_back_at_its_parent()
    {
        var model = JsonModelBuilder.Build(Sample);

        model.HasNodes.Should().BeTrue();
        model.Truncated.Should().BeFalse();
        model.HasError.Should().BeFalse();

        var root = model.Nodes[0];
        root.Id.Should().Be(0);
        root.ParentId.Should().Be(-1);
        root.Depth.Should().Be(0);
        root.Kind.Should().Be(JsonValueKind.Object);
        root.ChildCount.Should().Be(9);
        root.Path.Should().BeEmpty();
        root.JsonPath.Should().Be("$");
        root.PgExpression.Should().Be("data");

        foreach (var node in model.Nodes.Skip(1))
        {
            node.ParentId.Should().BeInRange(0, model.Nodes.Length - 1);
            model.Nodes[node.ParentId].Depth.Should().Be(node.Depth - 1);
        }
    }

    [Fact]
    public void A_containers_children_are_contiguous_in_the_node_array()
    {
        var model = JsonModelBuilder.Build(Sample);

        foreach (var node in model.Nodes)
        {
            if (node.ChildCount == 0)
            {
                node.ChildStart.Should().Be(-1);
                continue;
            }

            for (var i = 0; i < node.ChildCount; i++)
            {
                model.Nodes[node.ChildStart + i].ParentId.Should().Be(node.Id);
            }
        }
    }

    [Fact]
    public void Every_json_kind_comes_back_as_itself()
    {
        var model = JsonModelBuilder.Build(Sample);

        KindOf(model, "firstName").Should().Be(JsonValueKind.String);
        KindOf(model, "active").Should().Be(JsonValueKind.True);
        KindOf(model, "score").Should().Be(JsonValueKind.Number);
        KindOf(model, "retiredOn").Should().Be(JsonValueKind.Null);
        KindOf(model, "address").Should().Be(JsonValueKind.Object);
        KindOf(model, "items").Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public void Numbers_are_kept_verbatim_and_strings_are_unescaped()
    {
        var model = JsonModelBuilder.Build("""{"n": 12.50, "s": "a \"quoted\" word"}""");

        NodeFor(model, "n").Display.Should().Be("12.50");
        NodeFor(model, "n").Raw.Should().Be("12.50");
        NodeFor(model, "s").Display.Should().Be("a \"quoted\" word");
    }

    // The expected kind travels as a string because JsonSemanticKind is internal, and a public xUnit
    // theory method cannot take an internal parameter type.
    [Theory]
    [InlineData("createdAt", "DateTime")]
    [InlineData("id", "Guid")]
    [InlineData("homepage", "Url")]
    [InlineData("firstName", "None")]
    public void Semantics_are_decided_once_at_build_time(string key, string expected)
    {
        var model = JsonModelBuilder.Build(Sample);

        NodeFor(model, key).Semantic.ToString().Should().Be(expected);
    }

    [Fact]
    public void A_long_string_is_flagged_so_the_row_can_truncate_it()
    {
        var text = new string('x', 500);
        var model = JsonModelBuilder.Build("{\"note\":\"" + text + "\"}");

        NodeFor(model, "note").Semantic.Should().Be(JsonSemanticKind.LongText);
        NodeFor(model, "note").Display.Should().HaveLength(500, "the model keeps the whole value; the row decides how much to show");
    }

    [Fact]
    public void A_base64_looking_string_is_flagged_as_binary_and_a_hex_digest_is_not()
    {
        var model = JsonModelBuilder.Build("""
            {
              "payload": "TWFydGVuU3R1ZGlvQmluYXJ5UGF5bG9hZERhdGE=",
              "digest": "d41d8cd98f00b204e9800998ecf8427e"
            }
            """);

        NodeFor(model, "payload").Semantic.Should().Be(JsonSemanticKind.Base64);
        NodeFor(model, "digest").Semantic.Should().NotBe(JsonSemanticKind.Base64);
    }

    [Fact]
    public void Only_http_and_https_become_links()
    {
        var model = JsonModelBuilder.Build("""
            {
              "web": "https://example.com",
              "script": "javascript:alert(1)",
              "file": "file:///etc/passwd",
              "data": "data:text/html,<script>alert(1)</script>"
            }
            """);

        NodeFor(model, "web").Semantic.Should().Be(JsonSemanticKind.Url);
        NodeFor(model, "script").Semantic.Should().NotBe(JsonSemanticKind.Url);
        NodeFor(model, "file").Semantic.Should().NotBe(JsonSemanticKind.Url);
        NodeFor(model, "data").Semantic.Should().NotBe(JsonSemanticKind.Url);
    }

    [Fact]
    public void Every_node_carries_the_three_expressions_that_address_it()
    {
        var model = JsonModelBuilder.Build(Sample);

        var city = model.Nodes.Single(n => n.Key == "city");
        city.Path.Should().Be("address.city");
        city.JsonPath.Should().Be("$.address.city");
        city.PgExpression.Should().Be("data -> 'address' ->> 'city'");

        var address = NodeFor(model, "address");
        address.PgExpression.Should().Be("data -> 'address'", "a container stays jsonb, so the last step is -> and not ->>");

        var sku = model.Nodes.First(n => n.Key == "sku");
        sku.Path.Should().Be("items[0].sku");
        sku.JsonPath.Should().Be("$.items[0].sku");
        sku.PgExpression.Should().Be("data -> 'items' -> 0 ->> 'sku'");
    }

    [Fact]
    public void A_key_that_needs_quoting_gets_it()
    {
        var model = JsonModelBuilder.Build("""{"first-name":"Ada"}""");

        var node = NodeFor(model, "first-name");
        node.JsonPath.Should().Be("$['first-name']");
        node.PgExpression.Should().Be("data ->> 'first-name'");
        node.Path.Should().Be("[\"first-name\"]");
    }

    [Fact]
    public void Document_order_is_what_a_reader_sees_not_what_the_array_holds()
    {
        var model = JsonModelBuilder.Build("""{"a":{"b":1},"c":2}""");

        var keysInDocumentOrder = string.Join(
            ",",
            model.DocumentOrder.Select(id => model.Nodes[id].Key ?? "$"));

        keysInDocumentOrder.Should().Be("$,a,b,c", "the node array is breadth-first, but a reader reads depth-first");
        model.DocumentOrder.Should().HaveCount(model.Nodes.Length);
    }

    [Fact]
    public void Segments_are_recoverable_from_any_node()
    {
        var model = JsonModelBuilder.Build(Sample);
        var sku = model.Nodes.First(n => n.Key == "sku");

        var segments = model.SegmentsOf(sku.Id);

        segments.Should().HaveCount(3);
        segments[0].Key.Should().Be("items");
        segments[1].IsIndex.Should().BeTrue();
        segments[1].Index.Should().Be(0);
        segments[2].Key.Should().Be("sku");
    }

    [Fact]
    public void A_document_over_the_byte_threshold_is_refused_with_a_reason_and_no_nodes()
    {
        var big = "{\"blob\":\"" + new string('x', 600 * 1024) + "\"}";

        var model = JsonModelBuilder.Build(big);

        model.Truncated.Should().BeTrue();
        model.Nodes.Should().BeEmpty();
        model.Reason.Should().Contain("limit for building a tree");
        model.TotalBytes.Should().Be(Encoding.UTF8.GetByteCount(big));
    }

    [Fact]
    public void A_document_over_the_node_threshold_is_refused_and_building_anyway_still_works()
    {
        var json = ManyMembers(21_000);

        var refused = JsonModelBuilder.Build(json);
        refused.Truncated.Should().BeTrue();
        refused.Nodes.Should().BeEmpty();
        refused.Reason.Should().Contain("more than 20,000 nodes");

        var forced = JsonModelBuilder.Build(json, JsonModelOptions.Default, force: true);
        forced.Truncated.Should().BeFalse();
        forced.Nodes.Should().HaveCount(21_001);

        // Reading every node after Build has returned proves the model holds copied strings: a JsonElement
        // kept past its document's disposal would throw here, and a pooled buffer would read as garbage.
        forced.Nodes.Select(n => n.Display).Should().NotContainNulls();
        forced.Nodes[21_000].Display.Should().Be("20999");
    }

    [Fact]
    public void Thresholds_are_options_not_constants()
    {
        var options = new JsonModelOptions { MaxNodesForTree = 3 };

        JsonModelBuilder.Build("""{"a":1,"b":2,"c":3}""", options).Truncated.Should().BeTrue();
        JsonModelBuilder.Build("""{"a":1}""", options).Truncated.Should().BeFalse();
    }

    [Fact]
    public void Invalid_json_is_an_error_on_the_model_and_never_an_exception()
    {
        var model = JsonModelBuilder.Build("{ not json");

        model.HasError.Should().BeTrue();
        model.Error.Should().NotBeNullOrWhiteSpace();
        model.Nodes.Should().BeEmpty();
        model.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Nothing_at_all_is_the_empty_model()
    {
        JsonModelBuilder.Build(null).Should().BeSameAs(JsonModel.Empty);
        JsonModelBuilder.Build("   ").Should().BeSameAs(JsonModel.Empty);
    }

    [Fact]
    public void A_scalar_document_is_a_single_node()
    {
        var model = JsonModelBuilder.Build("42");

        model.Nodes.Should().HaveCount(1);
        model.Nodes[0].Kind.Should().Be(JsonValueKind.Number);
        model.Nodes[0].Display.Should().Be("42");
    }

    [Fact]
    public void The_column_the_expressions_start_from_is_configurable()
    {
        var model = JsonModelBuilder.Build("""{"a":1}""", column: "d.data");

        NodeFor(model, "a").PgExpression.Should().Be("d.data ->> 'a'");
    }

    private static string ManyMembers(int count)
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("\"k").Append(i).Append("\":").Append(i);
        }

        return builder.Append('}').ToString();
    }

    private static JsonViewNode NodeFor(JsonModel model, string key) =>
        model.Nodes.Single(n => n.Key == key);

    private static JsonValueKind KindOf(JsonModel model, string key) => NodeFor(model, key).Kind;
}
