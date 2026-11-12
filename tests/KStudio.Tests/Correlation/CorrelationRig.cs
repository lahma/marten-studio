using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;

namespace KStudio.Tests.Correlation;

/// <summary>Matches made the way the engine makes them (through <see cref="KafkaMatchFactory" />, so redaction has run), and the topics they belong to.</summary>
internal static class CorrelationRig
{
    public const string Topic = "orders";

    public static KafkaTopicBuilder Declare(Action<KafkaTopicBuilder>? configure = null)
    {
        KafkaTopicBuilder builder = new();
        configure?.Invoke(builder);
        return builder;
    }

    public static KafkaTopicDescriptor Descriptor(KafkaTopicBuilder builder, string topic = Topic) => new(topic, builder);

    public static TopicScanContext Context(KafkaTopicBuilder builder, string topic = Topic)
    {
        KafkaTopicDescriptor descriptor = Descriptor(builder, topic);
        KafkaRedactor redactor = new(builder.Rules);
        PredicateCompilation compiled = PredicateCompiler.Compile(
            null,
            new PredicateCompileContext(topic, descriptor.KeyFormat, descriptor.KeyFormatDeclared, redactor, KafkaReadGrant.Envelope, false));

        return new TopicScanContext(topic, descriptor, redactor, compiled.Predicate!, null);
    }

    /// <summary>A match of a record with these headers, key and value.</summary>
    public static KafkaMatch Match(
        KafkaTopicBuilder builder,
        KafkaReadGrant grant,
        string? key = "k-1",
        string? value = null,
        IEnumerable<(string Name, string? Value)>? headers = null,
        string topic = Topic,
        long offset = 7,
        int partition = 0,
        long timestampMs = 1_700_000_000_000) =>
        Match(builder, grant, key, value is null ? null : Encoding.UTF8.GetBytes(value), [.. (headers ?? []).Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))], topic, offset, partition, timestampMs);

    /// <summary>A match of a record with these exact bytes.</summary>
    public static KafkaMatch Match(
        KafkaTopicBuilder builder,
        KafkaReadGrant grant,
        string? key,
        byte[]? value,
        WireHeader[] headers,
        string topic = Topic,
        long offset = 7,
        int partition = 0,
        long timestampMs = 1_700_000_000_000) =>
        KafkaMatchFactory.Create(
            new WireRecord(topic, partition, offset, key is null ? null : Encoding.UTF8.GetBytes(key), value, headers, timestampMs, WireTimestampType.CreateTime, 0),
            Context(builder, topic),
            grant,
            new KafkaScanLimits(),
            new DecodeCounters());

    public static IReadOnlyList<string> Validate(Action<KafkaTopicBuilder> topic)
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", new Confluent.Kafka.ClientConfig { BootstrapServers = "broker:9092" });
        options.Topic("orders.v1", topic);
        return KafkaEngineOptionsValidation.Validate(options);
    }
}
