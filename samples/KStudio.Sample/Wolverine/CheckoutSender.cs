using System.Diagnostics;
using System.Text;

using Confluent.Kafka;

using Wolverine;

namespace KStudio.Sample.Wolverine;

/// <summary>
/// The sample application's own traffic: when <c>checkout.confirmed.v1</c> is empty, it publishes <see cref="SampleWolverine.Commands" /> commands
/// through Wolverine's <see cref="IMessageBus" />, with the order id as the Kafka key (the <c>PartitionKey</c>: Wolverine 6.61 keys a record by it, and by the envelope id when there is none) and a tenant, so every record
/// carries the headers Wolverine writes (<c>id</c>, <c>correlation-id</c>, <c>conversation-id</c>, <c>causation-id</c>, <c>message-type</c>, <c>sent-at</c>,
/// <c>tenant-id</c>). Then it produces one of them a second time, byte for byte, so that the duplicates panel has a group.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the application's, not KStudio's.</b> This class and the Confluent clients it uses are the sample's own, the way the seeder's are:
/// KStudio reads and never writes (hard rule 17).
/// </para>
/// <para>
/// <b>The second delivery is a re-produced record.</b> Wolverine 6.61 has no public way to send an <see cref="Envelope" /> again with the same id,
/// so the sender reads the record of command <see cref="SampleWolverine.DuplicatedCommand" /> back (an assigned consumer, no group membership, no
/// commit) and produces its key, value and headers again. The listener handles it as a command of its own: it stores the same document and confirms
/// again, so the topics hold 26 commands and 26 confirmations for 25 documents.
/// </para>
/// </remarks>
public sealed class CheckoutSender(IServiceScopeFactory scopes, SampleKafkaAddress kafka, ILogger<CheckoutSender> logger) : BackgroundService
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SendAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
        catch (Exception exception)
        {
            // A demo's traffic is not worth the host: say so, and leave the pages up.
            logger.LogWarning(exception, "The checkout sender stopped before it published the sample's commands.");
        }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        // Each client gets a configuration of its own: Confluent's config types built from another share its dictionary, and a consumer's group id
        // would reach the admin client's and the producer's.
        ClientConfig config = new() { BootstrapServers = kafka.BootstrapServers };

        // AutoProvision creates the topics as Wolverine starts; the sender is registered after Wolverine, but the broker may still be answering.
        await WaitAsync(() => Count(config, SampleWolverine.ConfirmedTopic) is not null, cancellationToken).ConfigureAwait(false);

        if (Count(config, SampleWolverine.ConfirmedTopic) is not 0)
        {
            return;
        }

        for (int number = 1; number <= SampleWolverine.Commands; number++)
        {
            // A scope, and so a bus, for each command: Wolverine gives every message a bus sends the bus's one correlation id, and the 25 orders
            // are 25 unrelated flows that the timeline must not join.
            using IServiceScope scope = scopes.CreateScope();
            IMessageBus bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

            // Inside an activity of its own: the command then carries a W3C trace context (the parent-id header) with a trace of its own.
            using Activity? activity = SampleWolverine.SenderSource.StartActivity("place checkout");

            // To the topic's own address, not through a routing rule: the application listens on checkout.v1 and sends nothing there by
            // configuration, so KStudio.Wolverine lists the topic as a listener, which is what it is.
            await bus.EndpointFor(SampleWolverine.CheckoutAddress).SendAsync(
                new PlaceCheckout(SampleWolverine.OrderId(number), $"Customer {number:000}", 10m + (number * 7.5m)),
                new DeliveryOptions { PartitionKey = SampleWolverine.OrderId(number), TenantId = SampleWolverine.TenantOf(number) }).ConfigureAwait(false);
        }

        // The copy is readable once the commands are on the topic.
        await WaitAsync(() => Count(config, SampleWolverine.CheckoutTopic) >= SampleWolverine.Commands, cancellationToken).ConfigureAwait(false);
        await ProduceAgainAsync(config, SampleWolverine.OrderId(SampleWolverine.DuplicatedCommand), cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("The checkout sender published {Commands} commands to {Topic}.", SampleWolverine.Commands, SampleWolverine.CheckoutTopic);
        }
    }

    /// <summary>The records on a topic, or <see langword="null" /> when it does not exist (yet).</summary>
    private static long? Count(ClientConfig config, string topic)
    {
        try
        {
            TopicMetadata? found;

            using (IAdminClient admin = new AdminClientBuilder(config).Build())
            {
                found = admin.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.FirstOrDefault(x => x.Topic == topic);
            }

            if (found is not { Error.IsError: false })
            {
                return null;
            }

            using IConsumer<Ignore, Ignore> consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = config.BootstrapServers, GroupId = "kstudio-sample-sender", EnableAutoCommit = false }).Build();

            return found.Partitions.Sum(partition =>
            {
                WatermarkOffsets marks = consumer.QueryWatermarkOffsets(new TopicPartition(topic, partition.PartitionId), TimeSpan.FromSeconds(10));
                return marks.High.Value - marks.Low.Value;
            });
        }
        catch (KafkaException)
        {
            return null;
        }
    }

    private static async Task ProduceAgainAsync(ClientConfig config, string key, CancellationToken cancellationToken)
    {
        Message<byte[], byte[]>? original = null;

        using (IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(new ConsumerConfig { BootstrapServers = config.BootstrapServers, GroupId = "kstudio-sample-sender", EnableAutoCommit = false }).Build())
        {
            using IAdminClient admin = new AdminClientBuilder(config).Build();
            Metadata metadata = admin.GetMetadata(SampleWolverine.CheckoutTopic, TimeSpan.FromSeconds(10));
            consumer.Assign(metadata.Topics.Single().Partitions.Select(static x => new TopicPartitionOffset(SampleWolverine.CheckoutTopic, x.PartitionId, Offset.Beginning)));

            DateTime deadline = DateTime.UtcNow + Patience;
            byte[] wanted = Encoding.UTF8.GetBytes(key);

            while (original is null && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (consumer.Consume(Poll) is { IsPartitionEOF: false, Message.Key: { } found } result && found.AsSpan().SequenceEqual(wanted))
                {
                    original = result.Message;
                }
            }
        }

        if (original is null)
        {
            throw new InvalidOperationException($"The command {key} was not found on {SampleWolverine.CheckoutTopic} to produce again.");
        }

        using IProducer<byte[], byte[]> producer = new ProducerBuilder<byte[], byte[]>(new ProducerConfig { BootstrapServers = config.BootstrapServers }).Build();
        await producer.ProduceAsync(SampleWolverine.CheckoutTopic, new Message<byte[], byte[]> { Key = original.Key, Value = original.Value, Headers = original.Headers }, cancellationToken).ConfigureAwait(false);
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The broker did not reach the state the sender waits for within " + Patience + ".");
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The bootstrap servers the sample's broker answers on, for the sender's own clients.</summary>
/// <param name="BootstrapServers">The bootstrap servers.</param>
public sealed record SampleKafkaAddress(string BootstrapServers);
