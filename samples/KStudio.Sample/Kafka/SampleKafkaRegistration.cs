using Confluent.Kafka;

namespace KStudio.Sample.Kafka;

/// <summary>
/// What the sample tells KStudio about its cluster: the body of its <c>AddKafkaStudio(…)</c> call, in a method of its own so that the live
/// tests configure the engine exactly as the host does.
/// </summary>
internal static class SampleKafkaRegistration
{
    /// <summary>The cluster's key.</summary>
    public const string ClusterKey = "local";

    /// <summary>Configures KStudio for the sample's cluster.</summary>
    /// <param name="o">KStudio's options.</param>
    /// <param name="bootstrapServers">The cluster's bootstrap servers.</param>
    public static void Configure(KafkaStudioOptions o, string bootstrapServers)
    {
        ArgumentNullException.ThrowIfNull(o);

        o.AddCluster(ClusterKey, new ClientConfig { BootstrapServers = bootstrapServers }, c => c
            .DisplayName("Sample Kafka")
            .Environment("Development")
            .ConsumerGroup(KafkaSeedPlan.ConsumerGroup));

        // A rule offers only Searchable(), which returns the builder, so the next rule chains from it.
        o.Topic("orders.v1", t => t
            .Description("Orders placed and shipped, as CloudEvents.")
            .Key(KafkaKeyFormat.Utf8String)
            .Value(KafkaDecoders.CloudEvents)
            .Partitioner(KafkaPartitioner.Murmur2)
            .Redact(r => r.Header("customer-email").Searchable().JsonPath("$.customer.email").Searchable()));

        o.Topic("payments.v1", t => t
            .Description("Payments, as binary-mode CloudEvents.")
            // Declared on purpose against a round-robin seed: the search page shows a verification that fails, and why.
            .Partitioner(KafkaPartitioner.Crc32)
            .Redact(r => r.CloudEvent("subject")));

        o.Topic("customers.v1", t => t
            .Description("The customer table, compacted.")
            .Redact(r => r.Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone")));

        o.Capabilities = KafkaStudioCapabilities.All();
        o.ClusterAuthorizationPolicy = SampleKafkaPolicies.Topics;
        o.CapabilityAuthorizationPolicy = SampleKafkaPolicies.Search;

        // Lowered so that the demo shows what it is about: "Continue" after 50 matches, and the confirmation above 5,000 records.
        o.Limits.MaxMatches = 50;
        o.Limits.ConfirmAboveRecords = 5_000;
    }
}
