using Confluent.Kafka;

using KStudio.Sample.Wolverine;

namespace KStudio.Sample.Kafka;

/// <summary>
/// What the sample tells KStudio about its cluster: the body of its <c>AddKafkaStudio(…)</c> call, in a method of its own so that the live
/// tests configure the engine exactly as the host does.
/// </summary>
internal static class SampleKafkaRegistration
{
    /// <summary>The cluster's key.</summary>
    public const string ClusterKey = "local";

    /// <summary>The setting that lowers how long a live tail reads before it pauses itself (a <see cref="TimeSpan" />, <c>00:00:10</c> at least), so a demo shows the pause.</summary>
    public const string TailPauseAfterSetting = "KStudioSample:TailPauseAfter";

    /// <summary>The setting that picks the capabilities: <c>all</c> (the default) or <c>search</c>, which leaves export, payloads and the live tail off so their notices can be seen.</summary>
    public const string CapabilitiesSetting = "KStudioSample:Capabilities";

    /// <summary>Configures KStudio for the sample's cluster.</summary>
    /// <param name="o">KStudio's options.</param>
    /// <param name="bootstrapServers">The cluster's bootstrap servers.</param>
    /// <param name="configuration">The host's configuration, for <see cref="TailPauseAfterSetting" /> and <see cref="CapabilitiesSetting" />; <see langword="null" /> for neither.</param>
    public static void Configure(KafkaStudioOptions o, string bootstrapServers, IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(o);

        o.AddCluster(ClusterKey, new ClientConfig { BootstrapServers = bootstrapServers }, c => c
            .DisplayName("Sample Kafka")
            .Environment("Development")
            // A shared test cluster: the Production cluster below is the same broker, so the two profiles' rules can be seen side by side.
            .Profile(KafkaProfile.Test)
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

        // What the sample's ticker writes every two seconds (the live tail's demo): the deny-listed x-api-key header and the same JSON-path rule as orders.v1.
        o.Topic(KafkaSeedPlan.HeartbeatsTopic, t => t
            .Description("A heartbeat every two seconds, for the live tail.")
            .Key(KafkaKeyFormat.Utf8String)
            .Redact(r => r.JsonPath("$.customer.email").Searchable()));

        o.Topic("customers.v1", t => t
            .Description("The customer table, compacted.")
            .Redact(r => r.Pattern(@"\+[0-9]{3} [0-9]{2} [0-9]{3} [0-9]{4}", "phone")));

        o.Capabilities = string.Equals(configuration?[CapabilitiesSetting], "search", StringComparison.OrdinalIgnoreCase)
            ? new KafkaStudioCapabilities { SearchRecords = true }
            : KafkaStudioCapabilities.All();
        o.ClusterAuthorizationPolicy = SampleKafkaPolicies.Topics;
        o.CapabilityAuthorizationPolicy = SampleKafkaPolicies.Search;

        // Lowered so that the demo shows what it is about: "Continue" after 50 matches, and the confirmation above 5,000 records.
        o.Limits.MaxMatches = 50;
        o.Limits.ConfirmAboveRecords = 5_000;

        if (configuration?.GetValue<TimeSpan?>(TailPauseAfterSetting) is { } pauseAfter)
        {
            o.Limits.TailPauseAfter = pauseAfter;
        }
    }

    /// <summary>The key of the cluster KStudio.Wolverine discovers (the adapter's default).</summary>
    public const string WolverineClusterKey = "wolverine";

    /// <summary>
    /// Adds what the sample's Wolverine application needs in KStudio (D-CORR decision 11), beside <see cref="Configure" />: the application's own
    /// Kafka, read from its Wolverine configuration, as a second cluster under the key <c>wolverine</c>; the two checkout topics' correlation field;
    /// and a link to a tracing tool.
    /// </summary>
    /// <remarks>
    /// The host cluster <c>local</c> and the discovered one point at the same broker here, which is a demo's shortcut: a real host declares its
    /// cluster once, either with <c>AddCluster</c> or by letting <c>UseWolverineKafka()</c> find it, and the Settings page shows both so the difference
    /// can be seen.
    /// </remarks>
    /// <param name="o">KStudio's options.</param>
    public static void ConfigureWolverine(KafkaStudioOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);

        o.UseWolverineKafka(w => w.Cluster((_, c) => c.DisplayName("Sample Kafka (Wolverine)").Profile(KafkaProfile.Production)));

        // The key of both topics is the order id, and the order id is the id of a CheckoutOrder in the sample's Marten store: KStudio links the
        // record to the document, and Marten Studio's page for the document back to the records.
        foreach (string topic in new[] { SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic })
        {
            o.Topic(topic, t => t.Correlation(c => c.Key().Document(SampleWolverine.DocumentType)));
        }

        // The .invalid top-level domain (RFC 2606) never resolves: replace it with your own tracing tool's address.
        o.ExternalLink("Traces", "https://tracing.example.invalid/trace/{traceId}");
    }
}
