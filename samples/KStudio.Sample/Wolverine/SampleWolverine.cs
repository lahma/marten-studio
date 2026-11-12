using System.Diagnostics;

using KStudio.Sample.Kafka;

using Wolverine;
using Wolverine.Kafka;

namespace KStudio.Sample.Wolverine;

/// <summary>
/// The sample's Wolverine application (D-CORR decision 11): one listener, one sender and the native dead-letter topic, on <b>topics of its own</b>
/// so that the seed of the host's cluster, and the walk-throughs that depend on it, stay as they were. KStudio is told about it by
/// <c>UseWolverineKafka()</c> (<see cref="SampleKafkaRegistration.ConfigureWolverine" />), which reads this configuration and starts nothing.
/// </summary>
public static class SampleWolverine
{
    /// <summary>The service name, which is also the listener's consumer group.</summary>
    public const string ServiceName = "KStudio.Sample";

    /// <summary>The topic the commands go to and the listener reads.</summary>
    public const string CheckoutTopic = "checkout.v1";

    /// <summary>Wolverine's address of <see cref="CheckoutTopic" />.</summary>
    public static readonly Uri CheckoutAddress = new("kafka://topic/" + CheckoutTopic);

    /// <summary>The topic the cascaded confirmations go to.</summary>
    public const string ConfirmedTopic = "checkout.confirmed.v1";

    /// <summary>Wolverine's native dead-letter topic.</summary>
    public const string DeadLetterTopic = "wolverine-dead-letter-queue";

    /// <summary>The number of commands the startup sender publishes.</summary>
    public const int Commands = 25;

    /// <summary>The command the sender publishes a second time, as the same record, so that the duplicates panel has a group.</summary>
    public const int DuplicatedCommand = 7;

    /// <summary>The Marten document type the topics' key points at.</summary>
    public const string DocumentType = nameof(CheckoutOrder);

    /// <summary>The order id of command <paramref name="number" />: <c>chk-0001</c> and on.</summary>
    /// <param name="number">The command's number, from 1.</param>
    public static string OrderId(int number) => $"chk-{number:0000}";

    /// <summary>The tenant of command <paramref name="number" />: <c>acme</c> for odd numbers, <c>globex</c> for even ones.</summary>
    /// <param name="number">The command's number, from 1.</param>
    public static string TenantOf(int number) => number % 2 == 1 ? "acme" : "globex";

    /// <summary>The name of the sender's <see cref="ActivitySource" />.</summary>
    public const string SenderSourceName = "KStudio.Sample.CheckoutSender";

    /// <summary>The sender's activity source: each command is sent inside an activity of its own, so that it carries a trace of its own.</summary>
    public static readonly ActivitySource SenderSource = new(SenderSourceName);

    /// <summary>
    /// Lets Wolverine's and the sender's activities exist. Wolverine writes the W3C trace context into a message's <c>parent-id</c> header only
    /// when an activity is current, and an activity is created only if something listens to its source - which a host with OpenTelemetry does and
    /// this one, with none, must do itself. KStudio reads the header as the record's trace id, which the <b>Traces</b> link and the timeline use.
    /// </summary>
    public static void ListenToTraces() =>
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = static source => source.Name is "Wolverine" or SenderSourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        });

    /// <summary>Configures Wolverine for the sample's broker.</summary>
    /// <param name="options">Wolverine's options.</param>
    /// <param name="bootstrapServers">The broker's bootstrap servers.</param>
    public static void Configure(WolverineOptions options, string bootstrapServers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        options.ServiceName = ServiceName;
        options.Durability.Mode = DurabilityMode.Solo;
        options.Discovery.IncludeAssembly(typeof(PlaceCheckoutHandler).Assembly);

        options.UseKafka(bootstrapServers).AutoProvision();
        options.ListenToKafkaTopic(CheckoutTopic).EnableNativeDeadLetterQueue();
        options.PublishMessage<CheckoutConfirmed>().ToKafkaTopic(ConfirmedTopic);
    }
}
