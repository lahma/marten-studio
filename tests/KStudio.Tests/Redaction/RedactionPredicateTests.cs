using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Tests.Predicates;

namespace KStudio.Tests.Redaction;

/// <summary>What a redaction rule does to the search (decision 4): masked fields take only equality, <c>any</c> skips them, and no sink sees a literal.</summary>
public class RedactionPredicateTests
{
    private static KafkaRedactionRules Rules(Action<KafkaRedactionBuilder> configure)
    {
        KafkaTopicBuilder topic = new();
        topic.Redact(configure);
        return topic.Rules!;
    }

    [Fact]
    public void A_redacted_field_takes_only_equals_and_in()
    {
        KafkaRedactionRules rules = Rules(static r => r.Header("x-email").Searchable().CloudEvent("subject").Searchable().Key().Searchable());

        foreach (string field in new[] { "header.x-email", "ce.subject", "key" })
        {
            PredicateEvaluationTests.Compile($"{field} = 'a'", rules: rules).Errors.Should().BeEmpty(field);
            PredicateEvaluationTests.Compile($"{field} in ('a', 'b')", rules: rules).Errors.Should().BeEmpty(field);

            foreach (string op in new[] { "^= 'a'", "~ 'a'", "is null", "is not null" })
            {
                PredicateEvaluationTests.Compile($"{field} {op}", rules: rules).Errors.Should().NotBeEmpty(field + " " + op);
            }
        }

        // Not searchable: not even equality.
        PredicateEvaluationTests.Compile("header.x-email = 'a'", rules: Rules(static r => r.Header("x-email"))).Errors.Should().ContainSingle();
    }

    [Fact]
    public void Any_skips_a_header_masked_through_its_attribute_rule()
    {
        KafkaRedactionRules rules = Rules(static r => r.CloudEvent("subject"));
        PredicateCompilation compiled = PredicateEvaluationTests.Compile("any ~ 's3cr3t'", rules: rules);

        TriState verdict = compiled.Predicate!.Evaluate(new RecordEvaluationContext(
            PredicateEvaluationTests.Record("k", "v", ("Ce_Subject", "s3cr3t-subject")),
            KafkaKeyFormat.Utf8String,
            new DecodeCounters(),
            1024 * 1024,
            false));

        verdict.Should().Be(TriState.False, "the binary-mode header of a masked attribute is skipped by any, whatever its case");
    }

    [Fact]
    public void The_audit_text_hides_the_literal_of_a_binary_mode_attribute_rule()
    {
        KafkaRedactionRules rules = Rules(static r => r.CloudEvent("subject").Searchable().Header("ce_type").Searchable());

        // header.ce_subject is masked through the attribute rule, ce.type through the header rule.
        PredicateCompilation compiled = PredicateEvaluationTests.Compile("header.ce_subject = 'secret-1' and ce.type = 'secret-2' and header.tenant = 'acme'", rules: rules);

        compiled.Errors.Should().BeEmpty();
        compiled.Predicate!.AuditText.Should().Be("header.ce_subject = <redacted> and ce.type = <redacted> and header.tenant = 'acme'");
    }
}
