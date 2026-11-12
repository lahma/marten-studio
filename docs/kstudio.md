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
| `SearchRecords` | `false` | Searches by key, header and CloudEvents attribute; the record page; keys, timestamps, headers and attributes. Values need `ReadPayloads`. |
| `ReadPayloads` | `false` | Record values: the value on the record page and in a search's detail, `value` and JSON-path searches, compare, and copying a value as a search. Needs `SearchRecords`. |

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

With `ReadPayloads`, a search can read values:

    value ~ 'refund'
    $.customer.id = 'customer-042'
    $.items[*].sku in ('A-1', 'B-2')
    $.total >= 100

A JSON path reads the value, or a CloudEvent's data: `$.a.b`, `$.a[0]`, and `$.a[*].b` for any element. Numbers compare as
numbers (`1.5 = 1.50`), and a value of another type never matches. A record whose value cannot be decoded is counted as
undecidable, never as a non-match, and the count stands beside every "no match". With `ReadPayloads`, `any` also reads every
text in the value.

When a topic declares how its producer partitions keys, a search that fixes the key reads only the partitions the key maps to:

    o.Topic("orders.v1", t => t.Partitioner(KafkaPartitioner.Murmur2));   // Java clients, Dekaf; Crc32 for Confluent.Kafka's default; Fnv1a for Sarama

The claim is checked on the window first, on up to 1,000 records; one record in another partition turns pruning off for that
topic, and the page says which. A pruned "no match" offers a search of every partition.

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

## Related records

**Related records** on a record's page searches the topics around it for records that share one of its correlation
values. The values come only from what the page already shows you: a field that is redacted, cut or absent is never
used. By default KStudio compares Wolverine's headers (`correlation-id`; `id`, `conversation-id` and `causation-id`;
`saga-id`), the CloudEvents `id` and the record's trace id. Declare more per topic:

```csharp
o.Topic("orders.v1", t => t.Correlation(c =>
{
    c.Key().Document("Order");
    c.JsonPath("$.customer.id").As("customer");
}));
```

Fields of the same kind are compared across topics. A JSON path is read only for a visitor with `ReadPayloads` on that
topic. Without it, the topic is searched by its other fields, and the page says so.

The search runs over the declared topics you may read on this cluster, 30 minutes either side of the record by default.
It is one search: it is estimated before it runs, confirmed above the same thresholds as a search, recorded once in the
audit with every topic and each topic's predicate, and stopped by the same limits. A topic you may not read is not
listed. A topic Kafka refuses shows why. Nothing runs until you press **Search related records**.

## Duplicates

Under search results and related records, **Possible duplicates** groups the records found on one topic that carry the
same identity: the same `id` header, the same CloudEvents source and id, or, when you may read values, the same key and
value as shown. Only the records found are compared, and nothing more is read.

## Redaction

```csharp
o.Topic("orders.v1", t => t.Redact(r =>
{
    r.Header("customer-email").Searchable();   // masked, but `=` and `in` may still find an exact value
    r.Key();                                     // masks the key
    r.CloudEvent("subject");                     // masks ce.subject, and its ce_subject header in binary mode
}));
```

```csharp
o.Topic("orders.v1", t => t.Redact(r =>
{
    r.JsonPath("$.customer.email").Searchable();     // a leaf or subtree of the value (or of a CloudEvent's data)
    r.Pattern(@"\b[0-9]{16}\b", "card");             // every match in every text of the value
}));
```

A masked part reads `••• (redacted by $.customer.email)` in the value, in compare and nowhere else: it is never copied, never
in a link, and its literal reads `<redacted>` in the audit. A value a rule cannot be applied to (a text value under a JSON-path
rule, a binary value, a pattern that takes too long) is withheld whole. Searches compare what you would see: a masked leaf
only by an exact `=` or `in` on a searchable JSON-path rule, and pattern-masked text never. Patterns run without
backtracking and may not use backreferences or lookarounds. Redaction hides content, not size: a value's length stays visible.

A masked field shows `••• (redacted by …)` everywhere: the results, the record page, chips, "copy as", search links,
the audit and error messages. It is masked inside the engine before anything else sees it, and no copy is kept. The
headers `authorization`, `proxy-authorization`, `cookie`, `set-cookie` and `x-api-key` are always masked unless you opt one
out with `r.AllowHeader(…)`, and can never be searched. A masked field takes only `=` and `in`, so it cannot be read a
character at a time, and a search link leaves its value out.

## Profiles

A cluster can say what kind of cluster it is, and KStudio lowers its limits there:

```csharp
o.AddCluster("prod", config, c => c.Profile(KafkaProfile.Production));
```

A profile only lowers limits and forbids; it never turns a capability on and never raises a limit above yours. A cluster with no
profile uses your limits as they are; KStudio never guesses one from the host's environment or the cluster's `Environment` text.

| Limit | `Development` | `Test` | `Production` |
|---|---|---|---|
| `MaxBytesPerScan` | yours | 256 MiB | 64 MiB |
| `MaxRecordsPerScan` | yours | 5,000,000 | 1,000,000 |
| `MaxScanDuration` | yours | 2 min | 1 min |
| `MaxMatches` | yours | 500 | 500 |
| `MaxMatchBytes` | yours | 64 KiB | 64 KiB |
| `MaxDecodeBytes` | yours | 1 MiB | 1 MiB |
| `ConfirmAboveBytes` | yours | 64 MiB | 16 MiB |
| `ConfirmAboveRecords` | yours | 1,000,000 | 250,000 |
| `BytesPerSecond` (this cluster's own bucket) | yours | 10 MiB/s | 5 MiB/s |
| `OwnerBytesPerHour` (this cluster's own) | yours | 2 GiB | 512 MiB |
| `MaxExportBytes` | yours | 32 MiB | export forbidden |
| `MaxTailMatches` | yours | 200 | live tail forbidden |
| `TailPauseAfter` | yours | 5 min | live tail forbidden |
| `MaxConcurrentScans`, `MaxConcurrentScansPerOwner`, `QueueKilobytes`, `FetchBytes`, `CreateTimeMargin`, `PartitionStallTimeout` | yours | yours | yours |

Each limit is the smaller of yours and the profile's, so a host that set a limit lower keeps it. The confirmation thresholds are then
capped at the capped budgets. Concurrency and the consumer's sizes guard the application's memory, whichever cluster is read, and stay
yours.

`Production` also forbids export and live tail on that cluster even when `ExportRecords` and `LiveTail` are granted, and its byte
rate and hourly allowance are the cluster's own, on top of the process-wide ones: a scan of a profiled cluster is paced by the slower
of the two buckets and stops when either allowance is spent. They are made only where the profile's value is below yours. `Production`
keeps 5 MiB/s and 512 MiB an hour for each visitor; the rate must stay well below your principal's broker quota, and a host
that bills by egress may want it lower, which it does by lowering its own `BytesPerSecond` and `OwnerBytesPerHour`.

With Wolverine, a profile is set per cluster, and for a broker per tenant per tenant, because each is a broker of its own with its own
quota:

```csharp
k.UseWolverineKafka(w => w.Cluster((tenant, c) => c.Profile(KafkaProfile.Production)));
```

The cluster selector and the Settings page show each cluster's profile and the limits it comes to.

## Wolverine

`KStudio.Wolverine` lists the Kafka clusters and topics of the application's Wolverine configuration:

```csharp
builder.Services.AddKafkaStudio(k => k.UseWolverineKafka());
```

It reads `WolverineOptions` once, when KStudio first needs its clusters, and starts, connects and writes nothing. Each
Kafka transport becomes a cluster (`wolverine`, or `wolverine-{broker}` for a named broker), and each broker-per-tenant
registration becomes a cluster of its own, `wolverine/{tenant}`, which your policies see as `Cluster = "wolverine"`
with `TenantId` set. Listener and sender endpoints become topics with their direction, message types, serializer and
consumer group, and the native dead-letter topic is listed. A topic Wolverine serializes as CloudEvents is decoded as
CloudEvents.

KStudio never reads Wolverine's message store, never runs Wolverine's builder callbacks (pass a token refresh through
`w.Cluster((broker, c) => c.OAuthBearerTokenRefresh(…))`), and never consumes with Wolverine's group ids. **Discovery
adds no redaction rule:** declare rules for every topic that carries personal data.

Wolverine 6.61 keeps its broker-per-tenant registrations internal. KStudio reads them through a guarded probe that lists
nothing when Wolverine's shape differs. A tenant's cluster has no configuration until Wolverine has connected it, which
happens as the application starts, so KStudio reads tenants only after the application has started: until then the
Settings page says "Not listed: some clusters, because the application had not started", and the first read after it adds the
tenants Wolverine has connected. A tenant Wolverine had not connected by then is not listed.

The Settings page says what was not listed and why, without names.

A policy that checks only `Cluster` admits every tenant entry of that cluster: check `TenantId` to tell tenants apart.

## Links

In Marten Studio, a correlation field declared with `Document("Order")` or `Stream()` links a record to that document
or stream, in the tenant of the record's `tenant-id` header. The document and stream pages offer **Find in Kafka**, which
opens related records for that id. A link grants nothing: the page it opens checks your access again. A record whose
`tenant-id` is redacted has no Marten links.

In a store that keeps each tenant in a database of its own, the link names the tenant when the store keeps its rows per tenant, and the studio opens the database that holds it among those you may see; a store whose documents are single-tenanted in each tenant's database gets no Marten link yet.

`o.ExternalLink("Traces", "https://tempo.example.com/trace/{traceId}")` adds a link to your tracing or log tool, with
`{traceId}`, `{correlationId}`, `{from}`, `{to}`, `{fromMs}` and `{toMs}`. Never put a credential in a template: it is
shown to every visitor, and KStudio refuses user info and credential parameters at start: in the query or the fragment, any parameter named
`key`, `sig`, `auth`, `pwd`, `passwd` or `code`, or ending in `token`, `secret`, `password`, `signature`, `apikey`, `accesskey` or `credential`, whatever
its case and whatever `-`, `_` or `.` it holds (`api-key`, `X-Amz-Signature` and `private_token` are refused; `tokenizer` and `author` are not). A
placeholder goes in the path, the query or the fragment, never in the scheme or the host.

## Audit

Every search and every record opened is recorded in the studio's Activity page and in your log (events 9600-9605), with
who, the cluster, the topics, the window, the predicate with every redacted value replaced by `<redacted>`, what was read
and why it stopped. Reading a record's value is recorded separately from the search that found it, once per record per
session. A visitor sees an entry only if your policies would let them make the same read.
