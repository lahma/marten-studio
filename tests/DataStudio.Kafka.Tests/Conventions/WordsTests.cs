using DataStudio.Kafka.Core.Predicates;
using DataStudio.Kafka.Core.Scanning;
using DataStudio.Kafka.Services;
using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// Every sentence the Kafka module's words hold, in one baseline (v2 step 6), so that a change of wording is read as a diff. A constant and a
/// property are written as they are; a method that builds a sentence from plain values is called with sample values, and with every value of an
/// enum, so that each arm of its <c>switch</c> is a line; a method that takes an object is listed by its signature at the end.
/// </summary>
public class WordsTests
{
    /// <summary>The mirror of the Marten suite's product-name scan, over the Kafka module's words: the product is Data Studio, and this module is Kafka.</summary>
    [Fact]
    public void No_sentence_of_the_Kafka_modules_words_says_a_removed_product_name()
    {
        string words = WordsSnapshot.OfStatic(typeof(KafkaText), ObjectValue);

        System.Text.RegularExpressions.Regex.Matches(words, @"Marten Studio|Postgres Studio|KStudio").Select(static x => x.Value).Should().BeEmpty();
        words.Should().Contain("Apache Kafka is a trademark of the Apache Software Foundation. Data Studio is not affiliated", "the trademark line stays attached to the Kafka module");
    }

    [Fact]
    public Task The_Kafka_modules_words_are_pinned() =>
        Verify(WordsSnapshot.OfStatic(typeof(KafkaText), ObjectValue), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("WordsTests_DataStudioKafka");

    /// <summary>
    /// The objects a sentence is built from: the limits a plan ran under, the allowance that was spent (and none), a cluster with a profile, the
    /// record a search was seeded from and the errors of a predicate that did not run.
    /// </summary>
    private static object?[]? ObjectValue(Type type) => type switch
    {
        _ when type == typeof(KafkaScanLimits) => [new KafkaScanLimits()],
        _ when type == typeof(KafkaSpentBudget) => [null, new KafkaSpentBudget(KafkaBudgetScope.Process, 10_485_760), new KafkaSpentBudget(KafkaBudgetScope.Cluster, 1_048_576)],
        _ when type == typeof(KafkaClusterEntry) => [new KafkaClusterEntry("primary", null, "Primary", "staging", KafkaProfile.Production)],
        _ when type == typeof(KafkaRelatedSubject) => [new KafkaRelatedSubject("orders.v1", 0, 12, "order id", null)],
        _ when type == typeof(IReadOnlyList<PredicateError>) => [new PredicateError[] { new(0, 3, "an error message") }],
        _ => null,
    };
}
