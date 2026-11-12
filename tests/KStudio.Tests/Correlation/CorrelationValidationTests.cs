using KStudio.Core.Clusters;
using KStudio.Core.Correlation;

namespace KStudio.Tests.Correlation;

/// <summary>The bounds of a topic's correlation declarations (D-CORR 1.2), and what the declarations resolve to (1.3).</summary>
public class CorrelationValidationTests
{
    private static IReadOnlyList<string> Validate(Action<KafkaCorrelationBuilder> configure) => CorrelationRig.Validate(t => t.Correlation(configure));

    private static IReadOnlyList<CorrelationField> Resolve(Action<KafkaCorrelationBuilder> configure) =>
        CorrelationRig.Descriptor(CorrelationRig.Declare(t => t.Correlation(configure))).CorrelationFields;

    [Fact]
    public void A_good_declaration_has_no_problem()
    {
        Validate(c =>
        {
            c.Header("x-order").As("order");
            c.CloudEvent("subject").Document("Order", "main");
            c.Key().Stream();
            c.JsonPath("$.order.id").As("order");
        }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("naïve")]
    [InlineData("trailing\n")]
    public void A_header_name_outside_the_grammar_is_refused(string name)
    {
        Validate(c => c.Header(name)).Should().ContainSingle().Which.Should().Contain("a header name is 1 to 256");
    }

    [Fact]
    public void A_header_name_is_at_most_256_characters()
    {
        Validate(c => c.Header(new string('a', 256))).Should().BeEmpty();
        Validate(c => c.Header(new string('a', 257))).Should().ContainSingle().Which.Should().Contain("a header name is 1 to 256");
    }

    [Theory]
    [InlineData("Subject")]
    [InlineData("")]
    [InlineData("with-dash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaa")]
    public void A_cloudevents_attribute_is_one_to_twenty_lower_case_letters_and_digits(string attribute)
    {
        Validate(c => c.CloudEvent(attribute)).Should().ContainSingle().Which.Should().Contain("CloudEvents attribute name is 1 to 20");
    }

    [Fact]
    public void A_json_path_is_parsed_by_the_searchs_grammar_with_at_most_32_steps()
    {
        Validate(c => c.JsonPath("order.id")).Should().ContainSingle().Which.Should().Contain("not a JSON path");
        Validate(c => c.JsonPath("$." + string.Join(".", Enumerable.Repeat("a", 32)))).Should().BeEmpty();
        Validate(c => c.JsonPath("$." + string.Join(".", Enumerable.Repeat("a", 33)))).Should().ContainSingle().Which.Should().Contain("not a JSON path");
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("1order")]
    [InlineData("")]
    [InlineData("order_id")]
    [InlineData("document:")]
    public void A_kind_is_a_lower_case_name_or_a_document_type(string kind)
    {
        Validate(c => c.Header("x").As(kind)).Should().ContainSingle().Which.Should().Contain("a kind is");
    }

    [Fact]
    public void A_kind_is_at_most_forty_characters_and_a_document_kind_takes_any_type()
    {
        Validate(c => c.Header("x").As("a" + new string('b', 39))).Should().BeEmpty();
        Validate(c => c.Header("x").As("a" + new string('b', 40))).Should().ContainSingle();
        Validate(c => c.Header("x").As("document:My.App.Order")).Should().BeEmpty();
    }

    [Fact]
    public void A_document_type_is_one_to_200_characters_with_no_control_character()
    {
        Validate(c => c.Header("x").Document(string.Empty)).Should().ContainSingle().Which.Should().Contain("a document type is 1 to 200");
        Validate(c => c.Header("x").Document(new string('a', 201))).Should().ContainSingle();
        Validate(c => c.Header("x").Document("Ord\ter")).Should().ContainSingle();
        Validate(c => c.Header("x").Document(new string('a', 200))).Should().BeEmpty();
    }

    [Fact]
    public void A_store_is_one_to_100_characters()
    {
        Validate(c => c.Header("x").Document("Order", string.Empty)).Should().ContainSingle().Which.Should().Contain("a store is 1 to 100");
        Validate(c => c.Header("x").Stream(new string('a', 101))).Should().ContainSingle();
        Validate(c => c.Header("x").Stream(new string('a', 100))).Should().BeEmpty();
        Validate(c => c.Header("x").Stream()).Should().BeEmpty();
    }

    [Fact]
    public void A_topic_has_at_most_sixteen_fields_the_defaults_included()
    {
        // Nine defaults, so seven more fit.
        Validate(c =>
        {
            for (int i = 0; i < 7; i++)
            {
                c.Header("x-" + i);
            }
        }).Should().BeEmpty();

        Validate(c =>
        {
            for (int i = 0; i < 8; i++)
            {
                c.Header("x-" + i);
            }
        }).Should().ContainSingle().Which.Should().Contain("has 17 fields, the defaults included; the most is 16");

        // A host field with the name of a default replaces it, so it does not count twice.
        Validate(c =>
        {
            c.Header("saga-id");
            for (int i = 0; i < 7; i++)
            {
                c.Header("x-" + i);
            }
        }).Should().BeEmpty();

        Validate(c =>
        {
            c.WithoutDefaults();
            for (int i = 0; i < 16; i++)
            {
                c.Header("x-" + i);
            }
        }).Should().BeEmpty();
    }

    [Fact]
    public void A_second_declaration_of_the_same_source_and_name_is_refused()
    {
        Validate(c =>
        {
            c.Header("X-Order");
            c.Header("x-order");
        }).Should().ContainSingle().Which.Should().Contain("is declared twice");

        Validate(c =>
        {
            c.CloudEvent("subject");
            c.CloudEvent("subject");
        }).Should().ContainSingle();

        Validate(c =>
        {
            c.Key();
            c.Key();
        }).Should().ContainSingle().Which.Should().Contain("the key is declared twice");

        Validate(c =>
        {
            c.JsonPath("$.a.b");
            c.JsonPath("$.a['b']");
        }).Should().ContainSingle("two spellings of one path are one path");

        // Different sources with one name are two fields.
        Validate(c =>
        {
            c.Header("subject");
            c.CloudEvent("subject");
        }).Should().BeEmpty();
    }

    [Fact]
    public void Document_and_stream_on_one_field_are_refused()
    {
        Validate(c => c.Header("x").Document("Order").Stream()).Should().ContainSingle().Which.Should().Contain("Document and Stream cannot both be called");
    }

    [Fact]
    public void The_trace_kind_is_for_headers_only()
    {
        Validate(c => c.CloudEvent("traceid").As("trace")).Should().ContainSingle().Which.Should().Contain("the kind 'trace' is for headers");
        Validate(c => c.Header("x-trace").As("trace")).Should().BeEmpty();
    }

    [Fact]
    public void Every_problem_names_the_topic_and_a_long_name_is_bounded()
    {
        IReadOnlyList<string> problems = Validate(c => c.Header(new string('é', 300)));

        problems.Should().ContainSingle().Which.Should().StartWith("KStudio: topic 'orders.v1': correlation header '");
        problems[0].Length.Should().BeLessThan(400);
    }

    [Fact]
    public void A_cluster_topic_is_validated_too()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new Confluent.Kafka.ClientConfig { BootstrapServers = "broker:9092" }, c => c.Topic("orders", t => t.Correlation(x => x.Header("bad name"))));

        KafkaEngineOptionsValidation.Validate(options).Should().ContainSingle().Which.Should().Contain("correlation header 'bad name'");
    }

    // Resolution (1.3).

    [Fact]
    public void A_topic_that_declares_nothing_has_the_defaults()
    {
        IReadOnlyList<CorrelationField> fields = CorrelationRig.Descriptor(CorrelationRig.Declare()).CorrelationFields;

        fields.Select(static x => (x.Grammar, x.Kind)).Should().Equal(
            ("header.correlation-id", "correlation"),
            ("header.id", "message"),
            ("header.conversation-id", "message"),
            ("header.causation-id", "message"),
            ("ce.id", "message"),
            ("header.saga-id", "saga"),
            ("header.traceparent", "trace"),
            ("header.ce_traceparent", "trace"),
            ("header.parent-id", "trace"));
        fields.Should().OnlyContain(static x => x.IsDefault);
        fields.Select(static x => x.Name).Should().NotContain("tenant-id", "tenant-id is a scope, not a key");
    }

    [Fact]
    public void A_host_field_replaces_the_default_of_the_same_source_and_name_and_keeps_its_kind()
    {
        IReadOnlyList<CorrelationField> fields = Resolve(c => c.Header("SAGA-ID").Document("OrderSaga"));

        fields.Should().HaveCount(9);
        fields.Count(static x => x.Name.Equals("saga-id", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        CorrelationField saga = fields.Single(static x => x.Name.Equals("saga-id", StringComparison.OrdinalIgnoreCase));
        saga.Kind.Should().Be("document:OrderSaga");
        saga.DocumentType.Should().Be("OrderSaga");
        saga.IsDefault.Should().BeFalse();

        Resolve(c => c.Header("correlation-id")).Single(static x => x.Name == "correlation-id").Kind.Should().Be("correlation", "a replacement without a kind keeps the default's");
    }

    [Fact]
    public void A_kind_is_what_As_names_then_the_document_or_stream_then_the_default_then_the_fields_own_name()
    {
        Resolve(c => c.Header("X-Order").As("order")).Single(static x => x.Name == "X-Order").Kind.Should().Be("order");
        Resolve(c => c.Header("x-ref").Document("Order").As("explicit")).Single(static x => x.Name == "x-ref").Kind.Should().Be("explicit");
        Resolve(c => c.Header("x-ref").Document("Order")).Single(static x => x.Name == "x-ref").Kind.Should().Be("document:Order");
        Resolve(c => c.Header("x-ref").Stream("main")).Single(static x => x.Name == "x-ref").Should().Match<CorrelationField>(static x => x.Kind == "stream" && x.Store == "main" && x.IsStream);
        Resolve(c => c.Header("X-Order")).Single(static x => x.Name == "X-Order").Kind.Should().Be("x-order", "a header's own name, folded, so two spellings are one kind");
        Resolve(c => c.CloudEvent("subject")).Single(static x => x.Name == "subject").Kind.Should().Be("subject");
        Resolve(c => c.Key()).Single(static x => x.Source == CorrelationSource.Key).Kind.Should().Be("key");
        Resolve(c => c.JsonPath("$.a['b']")).Single(static x => x.Source == CorrelationSource.JsonPath).Should().Match<CorrelationField>(static x => x.Kind == "$.a.b" && x.Name == "$.a.b" && x.Path != null);
    }

    [Fact]
    public void WithoutDefaults_leaves_only_the_declared_fields()
    {
        IReadOnlyList<CorrelationField> fields = Resolve(c =>
        {
            c.WithoutDefaults();
            c.Header("x-order");
        });

        fields.Should().ContainSingle().Which.Grammar.Should().Be("header.x-order");
    }

    [Fact]
    public void Declarations_add_up_and_a_later_edit_of_a_kept_field_is_not_seen_by_a_snapshot()
    {
        KafkaTopicBuilder builder = new();
        KafkaCorrelationField? field = null;
        builder.Correlation(c => field = c.Header("x-a").As("first"));
        builder.Correlation(c => c.Header("x-b"));

        KafkaTopicDescriptor descriptor = new("t", builder);
        field!.As("second");
        builder.Correlation(c => c.Header("x-c"));

        descriptor.CorrelationFields.Where(static x => !x.IsDefault).Select(static x => (x.Name, x.Kind)).Should().Equal(("x-a", "first"), ("x-b", "x-b"));
    }

    [Fact]
    public void The_builder_refuses_null_and_returns_itself()
    {
        KafkaTopicBuilder builder = new();

        Action nothing = () => builder.Correlation(null!);
        Action noName = () => builder.Correlation(c => c.Header(null!));

        nothing.Should().Throw<ArgumentNullException>();
        noName.Should().Throw<ArgumentNullException>();
        builder.Correlation(static _ => { }).Should().BeSameAs(builder);
    }
}
