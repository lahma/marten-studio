using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KStudio.Sample.Kafka;

/// <summary>A topic the sample creates.</summary>
/// <param name="Name">The topic.</param>
/// <param name="Partitions">Its partition count.</param>
/// <param name="Config">Its topic configuration, or <see langword="null" /> for the broker's defaults.</param>
internal sealed record SeedTopic(string Name, int Partitions, IReadOnlyDictionary<string, string>? Config = null);

/// <summary>A header of a seeded record.</summary>
/// <param name="Name">The header's name.</param>
/// <param name="Value">Its bytes, or <see langword="null" />.</param>
internal sealed record SeedHeader(string Name, byte[]? Value);

/// <summary>A record the sample produces, to an explicit partition and with an explicit timestamp.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Key">The key, or <see langword="null" />.</param>
/// <param name="Value">The value, or <see langword="null" /> for a tombstone.</param>
/// <param name="Headers">The headers, in order.</param>
/// <param name="Timestamp">The record's <c>CreateTime</c>.</param>
internal sealed record SeedRecord(string Topic, int Partition, string? Key, byte[]? Value, IReadOnlyList<SeedHeader> Headers, DateTimeOffset Timestamp)
{
    /// <summary>The value as text, or <see langword="null" /> for a tombstone.</summary>
    public string? ValueText => Value is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>The header named <paramref name="name" /> as text, or <see langword="null" /> when the record has none.</summary>
    /// <param name="name">The header's name.</param>
    public string? Header(string name) =>
        Headers.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))?.Value is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>A line that is equal for two equal records, for comparing two plans.</summary>
    public string Describe() =>
        string.Join(
            '|',
            Topic,
            Partition.ToString(CultureInfo.InvariantCulture),
            Key ?? "<null>",
            Value is null ? "<tombstone>" : Convert.ToHexString(Value),
            string.Join(',', Headers.Select(static x => x.Name + "=" + (x.Value is null ? "<null>" : Convert.ToHexString(x.Value)))),
            Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture));
}

/// <summary>An offset the sample commits for its own consumer group, as the host's real consumer would.</summary>
/// <param name="Group">The consumer group.</param>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="Offset">The next offset to read.</param>
internal sealed record SeedCommit(string Group, string Topic, int Partition, long Offset);

/// <summary>
/// Everything the sample puts on a cluster, computed and not yet sent: the topics, the records in the order they are produced, and the
/// offsets of the sample application's consumer group (D-UI decision 18).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic.</b> A <c>Random(20261112)</c> is drawn from in one fixed order, so two plans made for the same <c>now</c> are equal
/// record for record.
/// </para>
/// <para>
/// <b>Every timestamp is in the past</b> (F7): records span the five hours and fifty minutes that end five minutes before <c>now</c>, so a
/// search over "the last 6 h" covers all of them for as long as a demo lasts. The marker record is last.
/// </para>
/// <para>
/// <b>The needle.</b> <c>orders.v1</c> record <c>order-0042</c> has <c>correlation-id</c> <c>c-0042</c>, and its payment in
/// <c>payments.v1</c> has the same correlation id: <c>header.correlation-id = 'c-0042'</c> finds both.
/// </para>
/// </remarks>
internal sealed class KafkaSeedPlan
{
    /// <summary>The seed's random seed.</summary>
    public const int RandomSeed = 20261112;

    /// <summary>The topic of orders, as structured CloudEvents.</summary>
    public const string OrdersTopic = "orders.v1";

    /// <summary>The topic of payments, as binary-mode CloudEvents.</summary>
    public const string PaymentsTopic = "payments.v1";

    /// <summary>The compacted topic of customers.</summary>
    public const string CustomersTopic = "customers.v1";

    /// <summary>The dead-letter topic of orders.</summary>
    public const string DeadLetterTopic = "orders.v1.dlq";

    /// <summary>The topic whose headers the built-in deny-list masks.</summary>
    public const string SecretsTopic = "secrets.v1";

    /// <summary>The topic whose presence says the sample seeded this cluster.</summary>
    public const string MarkerTopic = "kstudio-sample.seed-v1";

    /// <summary>The consumer group of the sample application's own consumer: its lag is shown on the topic page.</summary>
    public const string ConsumerGroup = "sample-orders-handler";

    /// <summary>The number of orders: 1,000 in each of six partitions.</summary>
    public const int Orders = 6000;

    /// <summary>The number of payments: 500 in each of three partitions.</summary>
    public const int Payments = 1500;

    /// <summary>The order the README's walk-through looks for.</summary>
    public const int NeedleOrder = 42;

    private static readonly TimeSpan SpanStart = TimeSpan.FromMinutes((5 * 60) + 50);
    private static readonly TimeSpan SpanEnd = TimeSpan.FromMinutes(5);

    private readonly List<SeedRecord> records = [];
    private readonly Random random = new(RandomSeed);
    private readonly DateTimeOffset start;
    private readonly DateTimeOffset end;

    private KafkaSeedPlan(DateTimeOffset now)
    {
        start = now - SpanStart;
        end = now - SpanEnd;
        Now = now;
    }

    /// <summary>The instant the plan was made for.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>The topics to create, before any record is produced.</summary>
    public IReadOnlyList<SeedTopic> Topics { get; } =
    [
        new(OrdersTopic, 6),
        new(PaymentsTopic, 3),
        new(CustomersTopic, 1, new Dictionary<string, string> { ["cleanup.policy"] = "compact" }),
        new(DeadLetterTopic, 1),
        new(SecretsTopic, 1),
        new(MarkerTopic, 1),
    ];

    /// <summary>The records, in the order they are produced. The marker is last.</summary>
    public IReadOnlyList<SeedRecord> Records => records;

    /// <summary>The offsets committed for <see cref="ConsumerGroup" />, after the records are produced.</summary>
    public IReadOnlyList<SeedCommit> Commits { get; private set; } = [];

    /// <summary>The key of order number <paramref name="number" />: <c>order-0042</c>.</summary>
    /// <param name="number">The order's number, from 1.</param>
    public static string OrderKey(int number) => "order-" + number.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>The correlation id of order number <paramref name="number" />: <c>c-0042</c>.</summary>
    /// <param name="number">The order's number, from 1.</param>
    public static string CorrelationId(int number) => "c-" + number.ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>The (redacted, searchable) customer email of order number <paramref name="number" />: <c>customer42@example.com</c>.</summary>
    /// <param name="number">The order's number, from 1.</param>
    public static string CustomerEmail(int number) => "customer" + number.ToString(CultureInfo.InvariantCulture) + "@example.com";

    /// <summary>Makes the plan for <paramref name="now" />.</summary>
    /// <param name="now">The instant the seed is made; every timestamp is before it.</param>
    public static KafkaSeedPlan Create(DateTimeOffset now)
    {
        KafkaSeedPlan plan = new(now);
        plan.AddOrders();
        plan.AddPayments();
        plan.AddCustomers();
        plan.AddDeadLetters();
        plan.AddSecrets();
        plan.AddMarker();

        // The host's own consumer has read 800 of the 1,000 records in each partition of orders.v1 and committed, so the lag panel has a number.
        plan.Commits = [.. Enumerable.Range(0, 6).Select(static partition => new SeedCommit(ConsumerGroup, OrdersTopic, partition, 800))];
        return plan;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static SeedHeader Header(string name, string value) => new(name, Utf8(value));

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            write(writer);
        }

        return stream.ToArray();
    }

    /// <summary>The instant of the <paramref name="index" />-th of <paramref name="count" /> evenly spaced events, plus under a second of jitter that never reorders them.</summary>
    private DateTimeOffset At(int index, int count) =>
        start + TimeSpan.FromTicks((end - start).Ticks / count * index) + TimeSpan.FromMilliseconds(random.Next(0, 1000));

    private Guid NextGuid()
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private string TraceParent()
    {
        byte[] bytes = new byte[24];
        random.NextBytes(bytes);
        return "00-" + Convert.ToHexStringLower(bytes.AsSpan(0, 16)) + "-" + Convert.ToHexStringLower(bytes.AsSpan(16, 8)) + "-01";
    }

    private void AddOrders()
    {
        for (int number = 1; number <= Orders; number++)
        {
            string key = OrderKey(number);
            string type = number % 3 == 0 ? "Orders.OrderShipped" : "Orders.OrderPlaced";
            DateTimeOffset timestamp = At(number - 1, Orders);
            Guid id = NextGuid();
            decimal total = random.Next(500, 50_000) / 100m;
            int lines = random.Next(1, 4);
            int sku = random.Next(1, 200);
            string time = timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

            byte[] value = Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("specversion", "1.0");
                w.WriteString("id", id);
                w.WriteString("type", type);
                w.WriteString("source", "/sample/orders");
                w.WriteString("subject", key);
                w.WriteString("time", time);
                w.WriteString("datacontenttype", "application/json");
                w.WriteStartObject("data");
                w.WriteString("orderId", key);
                w.WriteString("customerId", "customer-" + ((number % 500) + 1).ToString("000", CultureInfo.InvariantCulture));
                w.WriteNumber("total", total);
                w.WriteString("currency", "EUR");
                w.WriteStartArray("lines");
                for (int line = 0; line < lines; line++)
                {
                    w.WriteStartObject();
                    w.WriteString("sku", "SKU-" + (sku + line).ToString("000", CultureInfo.InvariantCulture));
                    w.WriteNumber("quantity", line + 1);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
                w.WriteEndObject();
            });

            // Wolverine's envelope headers (EnvelopeConstants at 6.61), the way a Wolverine application stamps its Kafka messages.
            SeedHeader[] headers =
            [
                Header("id", id.ToString()),
                Header("message-type", type),
                Header("correlation-id", CorrelationId(number)),
                Header("conversation-id", NextGuid().ToString()),
                Header("tenant-id", number % 2 == 0 ? "globex" : "acme"),
                Header("traceparent", TraceParent()),
                Header("customer-email", CustomerEmail(number)),
            ];

            records.Add(new SeedRecord(OrdersTopic, (number - 1) % 6, key, value, headers, timestamp));
        }
    }

    private void AddPayments()
    {
        for (int number = 1; number <= Payments; number++)
        {
            string order = OrderKey(number);
            string key = "pay-" + number.ToString("0000", CultureInfo.InvariantCulture);
            DateTimeOffset timestamp = At(number - 1, Orders) + TimeSpan.FromSeconds(30);
            Guid id = NextGuid();
            decimal amount = random.Next(500, 50_000) / 100m;

            byte[] value = Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("paymentId", key);
                w.WriteString("orderId", order);
                w.WriteNumber("amount", amount);
                w.WriteString("currency", "EUR");
                w.WriteEndObject();
            });

            // Binary-mode CloudEvents: the attributes are the ce_ headers and the value is just the data.
            SeedHeader[] headers =
            [
                Header("ce_specversion", "1.0"),
                Header("ce_id", id.ToString()),
                Header("ce_type", "Payments.PaymentCaptured"),
                Header("ce_source", "/sample/payments"),
                Header("ce_subject", order),
                Header("content-type", "application/json"),
                Header("correlation-id", CorrelationId(number)),
            ];

            records.Add(new SeedRecord(PaymentsTopic, (number - 1) % 3, key, value, headers, timestamp));
        }
    }

    private void AddCustomers()
    {
        const int Customers = 500;
        const int Tombstones = 50;
        const int Total = Customers + Tombstones;

        for (int number = 1; number <= Customers; number++)
        {
            string key = "customer-" + number.ToString("000", CultureInfo.InvariantCulture);
            DateTimeOffset timestamp = At(number - 1, Total);
            int tier = random.Next(1, 4);
            string name = "Customer " + number.ToString(CultureInfo.InvariantCulture);

            byte[] value = Json(w =>
            {
                w.WriteStartObject();
                w.WriteString("customerId", key);
                w.WriteString("name", name);
                w.WriteNumber("tier", tier);
                w.WriteEndObject();
            });

            records.Add(new SeedRecord(CustomersTopic, 0, key, value, [], timestamp));
        }

        // Every tenth customer is deleted afterwards: a tombstone is the same key with no value.
        for (int index = 1; index <= Tombstones; index++)
        {
            string key = "customer-" + (index * 10).ToString("000", CultureInfo.InvariantCulture);
            records.Add(new SeedRecord(CustomersTopic, 0, key, null, [], At(Customers + index - 1, Total)));
        }
    }

    private void AddDeadLetters()
    {
        string[][] failures =
        [
            ["System.TimeoutException", "The operation has timed out while reserving stock."],
            ["System.InvalidOperationException", "Order has no lines."],
            ["Npgsql.PostgresException", "23505: duplicate key value violates unique constraint."],
        ];

        for (int index = 0; index < 20; index++)
        {
            int number = (index + 1) * 37;
            string[] failure = failures[index % failures.Length];

            // The dead letter carries the original order's value, as a retry policy that gave up would leave it.
            records.Add(new SeedRecord(
                DeadLetterTopic,
                0,
                OrderKey(number),
                records[number - 1].Value,
                [
                    Header("wolverine-kafka-retry-exception-type", failure[0]),
                    Header("wolverine-kafka-retry-exception-message", failure[1]),
                    Header("correlation-id", CorrelationId(number)),
                ],
                At(index, 20)));
        }
    }

    private void AddSecrets()
    {
        for (int index = 1; index <= 10; index++)
        {
            string number = index.ToString("00", CultureInfo.InvariantCulture);

            records.Add(new SeedRecord(
                SecretsTopic,
                0,
                "secret-" + number,
                Json(static w =>
                {
                    w.WriteStartObject();
                    w.WriteString("note", "A record whose headers the built-in deny-list masks.");
                    w.WriteEndObject();
                }),
                [
                    // Not credentials: stand-ins the deny-list must hide whatever they hold.
                    Header("x-api-key", "sample-api-key-" + number),
                    Header("authorization", "Bearer sample-token-" + number),
                    Header("note", "Open this record: the first two headers read as masked."),
                ],
                At(index - 1, 10)));
        }
    }

    private void AddMarker() =>
        records.Add(new SeedRecord(
            MarkerTopic,
            0,
            "seed",
            Json(static w =>
            {
                w.WriteStartObject();
                w.WriteString("seededBy", "KStudio.Sample");
                w.WriteNumber("version", 1);
                w.WriteEndObject();
            }),
            [],
            end));
}
