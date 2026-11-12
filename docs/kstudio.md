# KStudio for Apache Kafka® (preview)

KStudio is the Kafka section of Marten Studio and Postgres Studio. It shows the clusters and topics your application's own
Kafka credentials can describe, and, behind a capability that is off by default, searches their records. It runs inside
your application, with your configuration and your policies, and it never writes to Kafka.

Apache Kafka is a trademark of the Apache Software Foundation. KStudio is not affiliated with or endorsed by the Apache
Software Foundation.

## Registration

```csharp
builder.Services.AddMartenStudio();   // or AddPostgresStudio(): KStudio is a module of the studio you register
builder.Services.AddKafkaStudio(o =>
{
    o.AddCluster("primary", consumerConfig, c => c
        .DisplayName("Production")
        .Environment("prod")
        .ConsumerGroup("orders-handler"));      // its lag is shown on each topic it reads
    o.Topic("orders.v1", t => t
        .Description("Orders, as CloudEvents.")
        .Key(KafkaKeyFormat.Utf8String)
        .Value(KafkaDecoders.CloudEvents));
});
app.MapMartenStudio();                // there is no MapKafkaStudio(): the pages are under the studio's path, at /kafka
```

`AddCluster` copies your `ClientConfig` (a `ConsumerConfig` or an `AdminClientConfig` will do) when the options are
first resolved; KStudio never writes to your instance. A topic you do not declare is still listed and searchable, with its
key format assumed to be UTF-8 text.

## What it never does

KStudio reads and never writes. Its clients are copies of your configuration with these changes: no topic is ever
auto-created, no offset is committed or stored, no consumer group is joined (each search assigns its partitions with
explicit offsets), no statistics or client metrics are sent, and your builder delegates are never run. The Cluster
settings page lists every change. A search reads at most a budget you set, at a byte rate you set, and stops with its
reason.

## Capabilities and policies

Metadata needs no capability: the topic list, partitions, watermarks, configuration and your declared groups' lag.
Reading records does:

| Capability | Default | What it allows |
|---|---|---|
| `SearchRecords` | `false` | Searches by key, header and CloudEvents attribute; the record page; keys, timestamps, headers and attributes. Values are not read in this preview. |

`KafkaStudioOptions.ClusterAuthorizationPolicy` is asked about every cluster and every topic a page or a search touches,
on a `KafkaTopicResource(Cluster, TenantId, Topic, Capability)`; a topic it refuses is absent from every list, and a
search naming one is refused whole. `CapabilityAuthorizationPolicy` is asked for each use of a capability, per topic,
and falls back to the cluster policy. The studio's own `AuthorizationPolicy` decides who gets in, and its `ReadOnly`
turns every Kafka capability off. A host that turns a capability on with neither Kafka policy set refuses to start:
every visitor could otherwise read every topic your credentials can read.

## Search

Pick topics and a window, then type a predicate:

```
key = 'order-42'
header.correlation-id = '7f3c2a'
ce.type ^= 'Orders.' and not header.tenant-id = 'test'
header.tenant-id in ('acme', 'globex')
any ~ 'ORD-2026-'
```

Before reading, the page shows how each clause runs on each topic, what the window cannot cover (retention, open
transactions, compaction, the margin a `CreateTime` topic needs), and an estimate; above your threshold it asks you to
confirm. Results stream in as they are found. A search stops for exactly one reason, which it names, and **Continue**
reads on from exactly where it stopped. "No match" is always said with the number of records read and everything that
could make it untrue.

A search link keeps the cluster, topics and window in the address and the predicate after `#`, which browsers never send
to a server. A record link (`kafka/record?cluster=…&topic=…&partition=…&offset=…`) carries no record data.

Limits (`o.Limits`) bound every search: bytes and records per search, duration, matches kept, an hourly allowance per user,
two searches at a time per application and one per user, and a byte rate for the whole application. They can only be
lowered.

## Redaction

```csharp
o.Topic("orders.v1", t => t.Redact(r =>
{
    r.Header("customer-email").Searchable();   // masked, but `=` and `in` may still find an exact value
    r.Key();                                     // masks the key
    r.CloudEvent("subject");                     // masks ce.subject, and its ce_subject header in binary mode
}));
```

A masked field shows `••• (redacted by …)` everywhere: the results, the record page, chips, "copy as", search links,
the audit and error messages. It is masked inside the engine before anything else sees it, and no copy is kept. The
headers `authorization`, `proxy-authorization`, `cookie`, `set-cookie` and `x-api-key` are always masked unless you opt one
out with `r.AllowHeader(…)`, and can never be searched. A masked field takes only `=` and `in`, so it cannot be read a
character at a time, and a search link leaves its value out.

## Audit

Every search and every record opened is recorded in the studio's Activity page and in your log (events 9600-9604), with
who, the cluster, the topics, the window, the predicate with every redacted value replaced by `<redacted>`, what was read
and why it stopped. A visitor sees an entry only if your policies would let them make the same read.
