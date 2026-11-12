using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

namespace KStudio.Tests.Redaction;

/// <summary>
/// The public redaction builder and REVIEW-ENG-B finding 6 (decision 4): rules add up, a rule masks every case variant, the two spellings
/// of a CloudEvents attribute are one field, a deny-listed header can never be searchable, and the trace context is not read from a masked
/// header.
/// </summary>
public class RedactionBuilderTests
{
    private const string Secret = "s3cr3t-value";

    private static KafkaTopicBuilder Declare(Action<KafkaRedactionBuilder> configure)
    {
        KafkaTopicBuilder topic = new();
        topic.Redact(configure);
        return topic;
    }

    private static TopicScanContext Context(KafkaTopicBuilder builder)
    {
        KafkaTopicDescriptor descriptor = new("t", builder);
        KafkaRedactor redactor = new(builder.Rules);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            null,
            new PredicateCompileContext("t", descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, KafkaReadGrant.Envelope, false));

        return new TopicScanContext("t", descriptor, redactor, compiled.Predicate!, null);
    }

    private static KafkaMatch Match(KafkaTopicBuilder builder, KafkaReadGrant grant, string value, params (string Name, string? Value)[] headers) =>
        KafkaMatchFactory.Create(
            new WireRecord(
                "t",
                0,
                7,
                Encoding.UTF8.GetBytes("k"),
                Encoding.UTF8.GetBytes(value),
                [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
                1_700_000_000_000,
                WireTimestampType.CreateTime,
                0),
            Context(builder),
            grant,
            new KafkaScanLimits(),
            new DecodeCounters());

    [Fact]
    public void A_rule_declared_after_the_registry_was_built_is_not_seen()
    {
        KafkaRedactionBuilder? kept = null;
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new ClientConfig { BootstrapServers = "broker:9092" });
        options.Topic("orders", topic => topic.Redact(rules =>
        {
            kept = rules;
            rules.Header("x-first");
        }));

        KafkaClusterRegistry registry = new(options);
        KafkaRedactor before = new(registry.Get("prod").Descriptor("orders").Settings.Rules);

        // The host kept the builder it was handed and adds a rule, and an opt-out, once the registry exists.
        kept!.Header("x-late");
        kept.AllowHeader("authorization");
        KafkaRedactor after = new(registry.Get("prod").Descriptor("orders").Settings.Rules);

        before.ForHeader("x-first").Should().NotBeNull();
        after.ForHeader("x-first").Should().NotBeNull();
        after.ForHeader("x-late").Should().BeNull("a rule added after validation is not the rule set that was validated");
        after.ForHeader("authorization").Should().NotBeNull("an opt-out added later does not lift the deny-list either");
    }

    private static IReadOnlyList<string> Validate(Action<KafkaTopicBuilder> topic)
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new ClientConfig { BootstrapServers = "broker:9092" });
        options.Topic("orders.v1", topic);
        return KafkaEngineOptionsValidation.Validate(options);
    }

    [Fact]
    public void Redact_twice_adds_up()
    {
        KafkaTopicBuilder topic = new();

        topic.Redact(static r => r.Header("x-email")).Redact(static r => r.Key()).Should().BeSameAs(topic);

        topic.Rules!.Declared.Select(static x => x.Name).Should().Equal("header x-email", "key");
    }

    [Fact]
    public void Searchable_marks_exactly_the_rule_it_was_called_on_and_returns_to_the_builder()
    {
        KafkaTopicBuilder topic = Declare(static r =>
        {
            r.Header("a").Searchable();
            r.Header("b");
            r.Key();
        });

        topic.Rules!.Declared.Select(static x => (x.Name, x.Searchable)).Should().Equal(("header a", true), ("header b", false), ("key", false));
    }

    [Fact]
    public void A_header_rule_masks_every_case_variant()
    {
        KafkaTopicBuilder topic = Declare(static r => r.Header("Customer-Email"));

        KafkaMatch match = Match(topic, KafkaReadGrant.Envelope, "v", ("customer-email", Secret), ("CUSTOMER-EMAIL", Secret), ("x-other", "visible"));

        match.Headers.Take(2).Should().OnlyContain(static x => x.Value.IsRedacted && x.Value.RedactedBy == "header Customer-Email");
        match.Headers[2].Value.Text.Should().Be("visible");
        JsonSerializer.Serialize(match).Should().NotContain(Secret);
    }

    [Fact]
    public void A_ce_header_rule_masks_the_attribute_and_back()
    {
        // An attribute rule masks the binary-mode header in any case.
        KafkaTopicBuilder byAttribute = Declare(static r => r.CloudEvent("subject"));
        KafkaMatch fromAttribute = Match(byAttribute, KafkaReadGrant.Envelope, "{}", ("ce_specversion", "1.0"), ("ce_subject", Secret));

        fromAttribute.Headers.Single(static x => x.Name == "ce_subject").Value.IsRedacted.Should().BeTrue();
        new KafkaRedactor(byAttribute.Rules).ForHeader("CE_SUBJECT").Should().NotBeNull("the prefix is compared ignoring case too");
        fromAttribute.CloudEventAttributes.Single(static x => x.Name == "subject").Value.IsRedacted.Should().BeTrue();

        // A header rule on the carrier masks the attribute as well, which it did not before.
        KafkaTopicBuilder byHeader = Declare(static r => r.Header("ce_subject"));
        KafkaMatch fromHeader = Match(byHeader, KafkaReadGrant.Envelope, "{}", ("ce_specversion", "1.0"), ("ce_subject", Secret));

        fromHeader.Headers.Single(static x => x.Name == "ce_subject").Value.IsRedacted.Should().BeTrue();
        fromHeader.CloudEventAttributes.Single(static x => x.Name == "subject").Value.IsRedacted.Should().BeTrue();
        JsonSerializer.Serialize(fromHeader).Should().NotContain(Secret);
    }

    [Fact]
    public void A_datacontenttype_rule_masks_content_type_and_back()
    {
        KafkaTopicBuilder byAttribute = Declare(static r => r.CloudEvent("datacontenttype"));
        KafkaRedactor attributeRedactor = new(byAttribute.Rules);
        attributeRedactor.ForHeader("Content-Type")!.Name.Should().Be("ce.datacontenttype");

        KafkaTopicBuilder byHeader = Declare(static r => r.Header("content-type"));
        KafkaRedactor headerRedactor = new(byHeader.Rules);
        headerRedactor.ForCloudEvent("datacontenttype")!.Name.Should().Be("header content-type");
        headerRedactor.ForCloudEvent("subject").Should().BeNull();

        Match(byAttribute, KafkaReadGrant.Envelope, "{}", ("content-type", Secret)).Headers.Single().Value.IsRedacted.Should().BeTrue();
    }

    [Fact]
    public void A_ce_header_rule_withholds_a_structured_envelope_under_Payload()
    {
        KafkaTopicBuilder topic = Declare(static r => r.Header("ce_subject"));
        string envelope = $$$"""{"specversion":"1.0","type":"t","subject":"{{{Secret}}}","data":{"a":1}}""";

        KafkaMatch match = Match(topic, KafkaReadGrant.Envelope | KafkaReadGrant.Payload, envelope, ("content-type", "application/cloudevents+json"));

        match.ValueText.Should().BeNull("fail closed: the header rule names an attribute the envelope holds");
        match.ValueWithheldBy.Should().Be("header ce_subject");
        JsonSerializer.Serialize(match).Should().NotContain(Secret);

        new KafkaRedactor(Declare(static r => r.Header("content-type")).Rules).AnyCloudEventRule().Should().NotBeNull();
        new KafkaRedactor(Declare(static r => r.Header("x-email")).Rules).AnyCloudEventRule().Should().BeNull();
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("Authorization")]
    [InlineData("COOKIE")]
    public void Searchable_on_a_deny_listed_header_is_refused(string header)
    {
        IReadOnlyList<string> problems = Validate(t => t.Redact(r => r.Header(header).Searchable()));

        problems.Should().ContainSingle().Which.Should().Be(
            $"KStudio: topic 'orders.v1': header '{header}' is on the built-in deny-list, which can never be searched. Remove Searchable().");

        Validate(t => t.Redact(r => r.AllowHeader(header).Header(header).Searchable())).Should().ContainSingle("opting out of masking does not make it searchable");
        Validate(t => t.Redact(r => r.Header(header))).Should().BeEmpty("a declared rule that is not searchable only renames the rule");
    }

    [Fact]
    public void A_deny_listed_header_stays_unsearchable_even_where_validation_did_not_run()
    {
        KafkaRedactor redactor = new(Declare(static r => r.Header("Authorization").Searchable()).Rules);

        redactor.ForHeader("authorization")!.Searchable.Should().BeFalse();
        PredicateCompilation compiled = PredicateCompiler.Compile(
            PredicateParser.Parse("header.authorization = 'x'").Tree,
            new PredicateCompileContext("t", KafkaKeyFormat.Utf8String, false, redactor, KafkaReadGrant.Envelope, false));
        compiled.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void A_rule_declared_twice_ignoring_case_is_refused()
    {
        Validate(static t => t.Redact(static r =>
        {
            r.Header("Customer-Email");
            r.Header("customer-email");
        })).Should().ContainSingle().Which.Should().Be(
            "KStudio: topic 'orders.v1' declares a redaction rule on header 'customer-email' twice (names are compared ignoring case).");

        Validate(static t => t.Redact(static r =>
        {
            r.Key();
            r.Key();
        })).Should().ContainSingle().Which.Should().Contain("on the key twice");
        Validate(static t => t.Redact(static r => r.CloudEvent("subject")).Redact(static r => r.CloudEvent("subject"))).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Subject")]
    [InlineData("sub-ject")]
    [InlineData("sub ject")]
    public void A_bad_attribute_name_is_refused(string attribute)
    {
        Validate(t => t.Redact(r => r.CloudEvent(attribute))).Should().ContainSingle().Which.Should().Contain("CloudEvents attribute name");
        Validate(static t => t.Redact(static r =>
        {
            r.CloudEvent("subject");
            r.Header("x-ok");
        })).Should().BeEmpty();
    }

    [Fact]
    public void A_bad_header_name_is_refused()
    {
        Validate(static t => t.Redact(static r => r.Header("bad\nname"))).Should().ContainSingle().Which.Should().Contain("header name");
        Validate(static t => t.Redact(static r => r.Header(new string('h', 257)))).Should().ContainSingle();
    }

    [Fact]
    public void The_trace_is_not_read_from_a_masked_header()
    {
        const string trace = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

        Match(new KafkaTopicBuilder(), KafkaReadGrant.Envelope, "{}", ("traceparent", trace)).Trace.Should().NotBeNull("with no rule it is read");

        Match(Declare(static r => r.Header("traceparent")), KafkaReadGrant.Envelope, "{}", ("traceparent", trace)).Trace
            .Should().BeNull("a header rule masks the header, so nothing is read back from it");

        Match(Declare(static r => r.CloudEvent("traceparent")), KafkaReadGrant.Envelope, "{}", ("ce_traceparent", trace)).Trace
            .Should().BeNull("a rule on the attribute masks ce_traceparent, which is skipped too");
    }
}
