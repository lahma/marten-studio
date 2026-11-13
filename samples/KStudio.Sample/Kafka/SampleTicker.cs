using System.Globalization;
using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using KStudio.Sample.Wolverine;

namespace KStudio.Sample.Kafka;

/// <summary>
/// The sample application's heartbeat (D-M5 decision 10): one record every <see cref="Interval" /> to <c>heartbeats.v1</c>, so that the live tail has
/// something to follow. The record carries the deny-listed <c>x-api-key</c> header and a JSON value with a customer email, which the topic's JSON-path
/// rule masks: the tail shows both masked, as Search shows an order's.
/// </summary>
/// <remarks>
/// <b>It is the application's, not KStudio's.</b> This class and the Confluent producer it uses are the sample's own, the way the seeder's and the
/// checkout sender's are: KStudio reads and never writes (hard rule 17). It runs only where the sample owns the cluster (a throwaway broker, or
/// <c>--allow-data-generation</c>), and a failure stops the ticker and leaves the pages up.
/// </remarks>
/// <param name="kafka">The cluster's bootstrap servers.</param>
/// <param name="logger">Where a failure is said.</param>
public sealed class SampleTicker(SampleKafkaAddress kafka, ILogger<SampleTicker> logger) : BackgroundService
{
    private readonly TimeProvider time = TimeProvider.System;

    /// <summary>How often a heartbeat is written.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>The header the built-in deny-list masks, as a client library or a gateway might stamp it.</summary>
    public const string SecretHeader = "x-api-key";

    /// <summary>What the header holds: a stand-in, not a credential.</summary>
    public const string SecretValue = "sample-secret";

    /// <summary>The key of every heartbeat.</summary>
    public const string Key = "heartbeat";

    /// <summary>The email of heartbeat number <paramref name="sequence" />, which the JSON-path rule masks.</summary>
    /// <param name="sequence">The heartbeat's number, from 1.</param>
    public static string Email(long sequence) => "heartbeat" + sequence.ToString(CultureInfo.InvariantCulture) + "@example.com";

    /// <summary>The value of heartbeat number <paramref name="sequence" />: <c>{"seq": 7, "at": "…", "customer": {"email": "…"}}</c>.</summary>
    /// <param name="sequence">The heartbeat's number, from 1.</param>
    /// <param name="at">The instant it is written.</param>
    public static byte[] Value(long sequence, DateTimeOffset at)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("seq", sequence);
            writer.WriteString("at", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteStartObject("customer");
            writer.WriteString("email", Email(sequence));
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Heartbeat number <paramref name="sequence" /> as the record the producer sends.</summary>
    /// <param name="sequence">The heartbeat's number, from 1.</param>
    /// <param name="at">The instant it is written.</param>
    public static Message<string?, byte[]?> Heartbeat(long sequence, DateTimeOffset at) =>
        new()
        {
            Key = Key,
            Value = Value(sequence, at),
            Headers = [new Header(SecretHeader, Encoding.UTF8.GetBytes(SecretValue))],
            Timestamp = new Timestamp(at),
        };

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception exception)
        {
            // A demo's traffic is not worth the host: say so, and leave the pages up.
            logger.LogWarning(exception, "The heartbeat ticker stopped.");
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        // Each client gets a configuration of its own (Confluent's config types built from another share its dictionary).
        ClientConfig config = new() { BootstrapServers = kafka.BootstrapServers };

        // A cluster an earlier sample seeded has no such topic: creating it is idempotent, and waits for the metadata to show it.
        await new ConfluentSeedTarget(config).CreateTopicsAsync([new SeedTopic(KafkaSeedPlan.HeartbeatsTopic, 1)], cancellationToken).ConfigureAwait(false);

        using IProducer<string?, byte[]?> producer = new ProducerBuilder<string?, byte[]?>(new ProducerConfig(config) { Acks = Acks.All }).Build();
        using PeriodicTimer timer = new(Interval, time);

        for (long sequence = 1; await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false); sequence++)
        {
            await producer.ProduceAsync(new TopicPartition(KafkaSeedPlan.HeartbeatsTopic, 0), Heartbeat(sequence, time.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
    }
}
