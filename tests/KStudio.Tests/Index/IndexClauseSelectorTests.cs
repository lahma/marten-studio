using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Index;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Tests.Index;

/// <summary>
/// What a search asks the host's index (AGENTS.md D48, D-M5 decision 5.2): one equality on the key, a header or a CloudEvents attribute, from the
/// top-level conjunction, on a field no topic redacts; never an <c>in</c> list, a prefix, a range, <c>any</c>, a value, a JSON path, or anything under
/// <c>or</c> or <c>not</c>.
/// </summary>
public class IndexClauseSelectorTests
{
    private static string CorrelationKind => CorrelationDefaults.Defaults.First(static x => x.Name == "correlation-id").Kind;

    private static KafkaTopicDescriptor Plain(string name = "orders") => new(name, null);

    private static IndexClause? Choose(string text, KafkaTopicDescriptor[]? descriptors = null, IKafkaRedactor[]? redactors = null)
    {
        PredicateParseResult parsed = PredicateParser.Parse(text);
        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors.Select(static x => x.Message)));
        return IndexClauseSelector.Choose(parsed.Tree, descriptors ?? [Plain()], redactors ?? [KafkaRedactor.DenyListOnly]);
    }

    [Fact]
    public void A_key_equality_is_asked_with_its_literal_as_typed()
    {
        IndexClause? clause = Choose("key = 'order-42'");

        clause.Should().Be(new IndexClause(KafkaIndexField.Key, null, "order-42", null));
    }

    [Fact]
    public void A_header_and_an_attribute_are_asked_by_name_with_the_declared_correlation_kind()
    {
        Choose("header.correlation-id = 'c-1'").Should().Be(new IndexClause(KafkaIndexField.Header, "correlation-id", "c-1", CorrelationKind));
        Choose("ce.id = 'e-9'").Should().Be(new IndexClause(KafkaIndexField.CloudEventAttribute, "id", "e-9", CorrelationDefaults.Defaults.First(static x => x.Source == CorrelationSource.CloudEvent && x.Name == "id").Kind));
        Choose("header.tenant-id = 'acme'").Should().Be(new IndexClause(KafkaIndexField.Header, "tenant-id", "acme", null), "an undeclared header has no kind");
    }

    [Theory]
    [InlineData("key in ('a', 'b')")]
    [InlineData("key ^= 'ord'")]
    [InlineData("key ~ 'ord'")]
    [InlineData("key is null")]
    [InlineData("key is not null")]
    [InlineData("any = 'x'")]
    [InlineData("value = 'x'")]
    [InlineData("value ~ 'x'")]
    [InlineData("$.customer.id = 'x'")]
    [InlineData("$.total >= 100")]
    [InlineData("header.a in ('x')")]
    [InlineData("header.a ^= 'x'")]
    [InlineData("key = 'a' or key = 'b'")]
    [InlineData("not key = 'a'")]
    [InlineData("not (key = 'a' and header.a = 'b')")]
    [InlineData("(key = 'a' or key = 'b') and header.a ~ 'x'")]
    public void A_search_with_nothing_the_index_can_be_asked_asks_nothing(string text)
    {
        Choose(text).Should().BeNull();
    }

    [Fact]
    public void An_equality_inside_the_top_level_conjunction_is_asked_beside_clauses_that_are_not()
    {
        Choose("header.a ~ 'x' and key = 'k1' and any ~ 'y'")!.Value.Should().Be("k1");
        Choose("(key = 'k1' and header.a = 'x') and header.b ~ 'y'")!.Value.Should().Be("k1", "a parenthesised conjunction is still a conjunction");
    }

    [Fact]
    public void The_key_comes_first_then_a_declared_correlation_field_then_any_other_then_the_order_typed()
    {
        Choose("header.tenant-id = 'acme' and header.correlation-id = 'c-1' and key = 'k1'")!.Field.Should().Be(KafkaIndexField.Key);
        Choose("header.tenant-id = 'acme' and header.correlation-id = 'c-1'")!.FieldName.Should().Be("correlation-id");
        Choose("header.tenant-id = 'acme' and header.region = 'eu'")!.FieldName.Should().Be("tenant-id", "textual order breaks the tie");
        Choose("ce.source = 'a' and ce.id = 'e-1'")!.FieldName.Should().Be("id", "a default correlation attribute outranks an undeclared one");
    }

    [Fact]
    public void A_field_any_topic_redacts_is_never_asked_and_the_next_clause_is()
    {
        KafkaRedactor masksKey = new(new KafkaTopicBuilder().Redact(r => r.Key()).Rules);

        Choose("key = 'secret-key'", redactors: [KafkaRedactor.DenyListOnly, masksKey]).Should().BeNull("one topic masks the key, so the literal never leaves the engine");
        Choose("key = 'secret-key' and header.correlation-id = 'c-1'", redactors: [masksKey])!.FieldName.Should().Be("correlation-id");
    }

    [Fact]
    public void A_header_a_rule_masks_is_not_asked_even_when_it_is_searchable()
    {
        KafkaRedactor masksHeader = new(new KafkaTopicBuilder().Redact(r => r.Header("customer-email").Searchable()).Rules);

        Choose("header.customer-email = 'a@b.c'", redactors: [masksHeader]).Should().BeNull();
    }

    [Fact]
    public void The_correlation_kind_is_named_only_when_the_topics_agree_on_it()
    {
        KafkaTopicDescriptor a = new("a", new KafkaTopicBuilder().Correlation(c => c.Header("order-ref").As("order")));
        KafkaTopicDescriptor b = new("b", new KafkaTopicBuilder().Correlation(c => c.Header("order-ref").As("order")));
        KafkaTopicDescriptor other = new("c", new KafkaTopicBuilder().Correlation(c => c.Header("order-ref").As("invoice")));

        Choose("header.order-ref = 'o-1'", [a, b])!.CorrelationKind.Should().Be("order");
        Choose("header.order-ref = 'o-1'", [a, other])!.CorrelationKind.Should().BeNull("the topics disagree, so none is claimed");
        Choose("header.order-ref = 'o-1'", [Plain()])!.CorrelationKind.Should().BeNull();
    }

    [Fact]
    public void A_declared_header_outranks_an_undeclared_one_that_comes_first()
    {
        KafkaTopicDescriptor declared = new("orders", new KafkaTopicBuilder().Correlation(c => c.Header("order-ref")));

        Choose("header.tenant-id = 'acme' and header.order-ref = 'o-1'", [declared])!.FieldName.Should().Be("order-ref");
    }
}
