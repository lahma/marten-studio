# KStudio sample host

A small ASP.NET Core host that registers Marten Studio over the demo store in `samples/MartenStudio.SampleDomain`, adds KStudio, the Kafka
section of the studio, and serves both at `/marten`. The Kafka pages are at `/marten/kafka`.

```powershell
dotnet run --project samples/KStudio.Sample
```

With no `ConnectionStrings:Kafka` and no `ConnectionStrings:Postgres` it starts a throwaway `apache/kafka:4.4.0` and a throwaway
`postgres:17-alpine` through Testcontainers, seeds the Kafka with a small, deterministic set of topics, and logs a banner at Warning that
names both:

> KStudio sample: started a throwaway Kafka at {bootstrap} and a throwaway Postgres at {host}; both are deleted when this process stops.
> Set ConnectionStrings:Kafka and ConnectionStrings:Postgres to use your own.

It never falls back to `localhost:9092`. With Docker absent and nothing configured it stops with:

> KStudio sample: no ConnectionStrings:Kafka is configured and Docker is not available to start a throwaway Kafka. Start Docker, or set
> ConnectionStrings:Kafka to a cluster you own (the sample creates and seeds topics there only with --allow-data-generation).

Open `http://localhost:5212`, sign in at `/login` as `admin` or `support` (the password is the user name).

| Switch or variable | What it does |
|---|---|
| `--allow-data-generation` | Lets the sample create and seed its topics on a cluster you configured, and start its Wolverine application there (which creates three topics of its own and produces to them). Without it a configured cluster is only browsed, and no Wolverine application runs. A configured cluster that has an `orders.v1` the sample did not seed is refused either way. |
| `KSTUDIO_SAMPLE_REUSE=true` | Reuses the two containers between runs instead of starting fresh ones, and the banner says they are kept; `docker rm -f` removes them. The seed is not repeated: the marker topic says it was done. |
| `ConnectionStrings:Kafka` | The bootstrap servers of a cluster of your own. |
| `ConnectionStrings:Postgres` | A Postgres of your own for the Marten half. |

## The two users

| User | Studio | Kafka |
|---|---|---|
| `admin` | opens it, and may change things in the Marten screens | every topic, Search and record values (`ReadPayloads`) |
| `support` | opens it, read-only | the cluster and the topics starting `orders.` or `checkout.`, and Search over them by key, header and attribute; **not** record values: they read `The value of this record is not available to you.`, and a search on a value is refused. `payments.v1`, `customers.v1`, `secrets.v1` and Wolverine's dead-letter topic are absent from every list and a search that names one is refused whole |

## What is seeded

Every timestamp is in the past, within the last six hours of when the sample started, so a search over the last 24 hours covers all of it for as long as a demo lasts.

| Topic | Partitions | What it shows |
|---|---|---|
| `orders.v1` | 6 | 6,000 orders as structured CloudEvents, with Wolverine-style headers, placed by murmur2 (the topic declares `KafkaPartitioner.Murmur2`, so a key search reads one partition); `customer-email` and the data's `$.customer.email` are masked and still searchable by `=` and `in` |
| `payments.v1` | 3 | 1,500 payments as binary-mode CloudEvents, placed round-robin; the `subject` attribute is masked in both bindings. The topic declares `crc32` on purpose: the page shows a verification that fails, and why |
| `customers.v1` | 1 | a compacted topic: 500 customers with a phone number (masked by the `phone` pattern, in `phone` and in `note`), then 50 tombstones |
| `orders.v1.dlq` | 1 | 20 dead letters, with Wolverine's retry headers |
| `secrets.v1` | 1 | headers `x-api-key` and `authorization`, which the built-in deny-list always masks |
| `kstudio-sample.seed-v2` | 1 | the marker: the sample seeded this cluster (a cluster with `seed-v1` was seeded by an earlier sample and must be deleted) |

The sample application's own consumer group, `sample-orders-handler`, has committed 800 of the about 1,000 records in each partition of
`orders.v1`, so the topic page shows a lag. That consumer is the sample's, not KStudio's: KStudio reads and never writes.

## Find a record from a ticket

A support ticket says: *the customer with correlation id `c-0042` has not got their order.* Sign in as `support`, open **Search**, pick
`orders.v1`, choose the last 24 hours, and type:

```
header.correlation-id = 'c-0042'
```

The page shows how the clause runs on the topic and an estimate; the window is above the demo's confirmation threshold (5,000 records), so it
asks you to confirm. One record matches: `order-0042`. Open it for its metadata, headers (with `customer-email` masked), CloudEvents attributes
and the records around it. Its payment in `payments.v1` has the same correlation id - but support cannot see that topic; sign in as `admin` to
find both.

## Read a value

Sign in as `admin` (the value is the payload gate's, and `support` does not pass it). Search `orders.v1` over the last 6 hours for:

```
$.customer.id = 'customer-042'
```

One order matches. Open **Details**: the CloudEvent's data is shown in the JSON view, with `$.customer.email` as
`••• (redacted by $.customer.email)` and the line `1 part of this value is masked: $.customer.email.` above it. The node menu offers
**Copy as search** on `$.customer.id`, no download and no Postgres items. Compare two matches to see a masked path listed and not diffed.

| Predicate | Shows |
|---|---|
| `$.customer.email = 'customer42@example.com'` | one match: an exact `=` on a searchable JSON-path rule; the Activity entry reads `<redacted>` |
| `$.customer.email ~ 'customer'` | refused: only `=` and `in` can search a masked leaf |
| `key = 'order-0042'` | `one partition: murmur2 maps the key to partition … of 6 (verified on 1,002 records)`; one partition is read |
| `key = 'order-9999'` | no match, with the caveat that only one partition was read; **Search all partitions** reads the other five |
| `payments.v1`, `key = 'pay-0001'` | `not pruned: record …` — the declared `crc32` is not what the round-robin producer did |
| `customers.v1`, `any ~ '+358'` | no phone: pattern-masked text can never be searched |

Other things to try:

| Predicate | Shows |
|---|---|
| `key = 'order` | an error with its position |
| `header.customer-email = 'customer42@example.com'` | one match, with the cell masked |
| `header.customer-email ^= 'customer'` | refused: a masked field takes only `=` and `in` |
| `ce.type ^= 'Orders.'` | stops at the demo's lowered match limit (50); **Continue** reads on |
| `header.correlation-id = 'no-such-id'` | "no match", with the count read and every caveat |

The demo lowers `Limits.MaxMatches` to 50 and `Limits.ConfirmAboveRecords` to 5,000 so that these are easy to see; profiles can only lower a limit.

## A Wolverine application, and the records around a record

The sample also hosts a small [Wolverine](https://wolverinefx.net) application on topics of its own, so that the seed above and its walk-throughs stay as
they were. It listens on `checkout.v1` (consumer group `KStudio.Sample`) and publishes to `checkout.confirmed.v1`, with Wolverine's native
dead-letter topic `wolverine-dead-letter-queue`. At start, when `checkout.confirmed.v1` is empty, the application's own sender (`CheckoutSender`)
puts 25 `PlaceCheckout` commands on `checkout.v1`, keyed by the order id (`chk-0001` to `chk-0025`), alternately for tenant `acme` and tenant `globex`.
`PlaceCheckoutHandler` stores a `CheckoutOrder` document in the demo's Marten store, **in the command's tenant**, and returns a `CheckoutConfirmed`
that Wolverine cascades to `checkout.confirmed.v1`. Command `chk-0007` is delivered twice - the sender produces its record again, headers
included - so the topic holds 26 commands, the store 25 documents and `checkout.confirmed.v1` 26 confirmations.

`WolverineFx.RuntimeCompilation` is a reference of this host alone: Wolverine 6.61 no longer ships its runtime compiler, and a host with a
handler needs it (or pre-generated code) to start. KStudio and KStudio.Wolverine do not use it.

KStudio is told about all of this with one line, `UseWolverineKafka()`, which reads Wolverine's configuration and starts nothing:

```csharp
k.UseWolverineKafka(w => w.Cluster((broker, c) => c.DisplayName("Sample Kafka (Wolverine)")));
k.Topic("checkout.v1", t => t.Correlation(c => c.Key().Document("CheckoutOrder")));
```

The Settings page lists **both** clusters: `Sample Kafka`, which the host declared with `AddCluster` and which keeps the seed's topics, and
`Sample Kafka (Wolverine)`, which Wolverine's configuration declared. Both point at the same broker; that is a demo's shortcut, and a real host
declares its cluster once, either way. `Correlation(c => c.Key().Document("CheckoutOrder"))` says that the key of both topics is the id of a
`CheckoutOrder` in the Marten store, which is what lets the two studios link to one another.

### Walk-through

1. Sign in as `admin`. **Kafka › Topics**, cluster **Sample Kafka (Wolverine)**: `checkout.v1` (listener, group `KStudio.Sample`),
   `checkout.confirmed.v1` (sender) and `wolverine-dead-letter-queue` (dead letters).
2. **Search**, `checkout.v1`, the last hour, no predicate: 26 records. Open `chk-0007`.
3. **Related records**: the timeline shows the command on `checkout.v1` and its `CheckoutConfirmed` on `checkout.confirmed.v1` on one screen, matched by
   `correlation` and `message`. Nothing runs until you press **Search related records**.
4. In the record detail, **Document CheckoutOrder chk-0007 →** opens Marten Studio's document, in tenant `acme`.
5. On the document page, **Find in Kafka** returns to the timeline over both checkout topics.
6. Back in the search results of `checkout.v1`: **Possible duplicates: 1 group**, the two copies of `chk-0007`.

As `support`, the same pages show only `orders.*` and `checkout.*`, and no value.

**Traces.** The record page of a checkout record offers a **Traces** link to `https://tracing.example.invalid/trace/{traceId}`. The `.invalid`
top-level domain never resolves: replace it in `SampleKafkaRegistration.ConfigureWolverine` with your own tracing tool's address. A link is drawn only
for a record that has a value for every placeholder in the template. Wolverine writes the trace into a message's `parent-id` header only while an
activity is current, so the sample registers an `ActivityListener` for Wolverine's source and sends each command inside an activity of its own
(`SampleWolverine.ListenToTraces`); a host with OpenTelemetry has both already.

If you reuse the broker between runs (`KSTUDIO_SAMPLE_REUSE=true`) and not the database, the sender finds `checkout.confirmed.v1` non-empty and sends
nothing, and the Wolverine listener has nothing new to store: delete the containers, or reuse both.
