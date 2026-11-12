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
| `--allow-data-generation` | Lets the sample create and seed its topics on a cluster you configured. Without it a configured cluster is only browsed. A configured cluster that has an `orders.v1` the sample did not seed is refused either way. |
| `KSTUDIO_SAMPLE_REUSE=true` | Reuses the two containers between runs instead of starting fresh ones, and the banner says they are kept; `docker rm -f` removes them. The seed is not repeated: the marker topic says it was done. |
| `ConnectionStrings:Kafka` | The bootstrap servers of a cluster of your own. |
| `ConnectionStrings:Postgres` | A Postgres of your own for the Marten half. |

## The two users

| User | Studio | Kafka |
|---|---|---|
| `admin` | opens it, and may change things in the Marten screens | every topic, and Search |
| `support` | opens it, read-only | the cluster and the topics starting `orders.`, and Search over them; `payments.v1`, `customers.v1` and `secrets.v1` are absent from every list and a search that names one is refused whole |

## What is seeded

Every timestamp is in the past, within the last six hours, so a search over `6 h` covers all of it.

| Topic | Partitions | What it shows |
|---|---|---|
| `orders.v1` | 6 | 6,000 orders as structured CloudEvents, with Wolverine-style headers; `customer-email` is masked and still searchable by `=` and `in` |
| `payments.v1` | 3 | 1,500 payments as binary-mode CloudEvents; the `subject` attribute is masked in both bindings |
| `customers.v1` | 1 | a compacted topic: 500 customers, then 50 tombstones |
| `orders.v1.dlq` | 1 | 20 dead letters, with Wolverine's retry headers |
| `secrets.v1` | 1 | headers `x-api-key` and `authorization`, which the built-in deny-list always masks |
| `kstudio-sample.seed-v1` | 1 | the marker: the sample seeded this cluster |

The sample application's own consumer group, `sample-orders-handler`, has committed 800 of the 1,000 records in each partition of
`orders.v1`, so the topic page shows a lag. That consumer is the sample's, not KStudio's: KStudio reads and never writes.

## Find a record from a ticket

A support ticket says: *the customer with correlation id `c-0042` has not got their order.* Sign in as `support`, open **Search**, pick
`orders.v1`, keep the window at the last 6 hours, and type:

```
header.correlation-id = 'c-0042'
```

The page shows how the clause runs on the topic and an estimate; the window is above the demo's confirmation threshold (5,000 records), so it
asks you to confirm. One record matches: `order-0042`. Open it for its metadata, headers (with `customer-email` masked), CloudEvents attributes
and the records around it. Its payment in `payments.v1` has the same correlation id - but support cannot see that topic; sign in as `admin` to
find both.

Other things to try:

| Predicate | Shows |
|---|---|
| `key = 'order` | an error with its position |
| `header.customer-email = 'customer42@example.com'` | one match, with the cell masked |
| `header.customer-email ^= 'customer'` | refused: a masked field takes only `=` and `in` |
| `ce.type ^= 'Orders.'` | stops at the demo's lowered match limit (50); **Continue** reads on |
| `header.correlation-id = 'no-such-id'` | "no match", with the count read and every caveat |

The demo lowers `Limits.MaxMatches` to 50 and `Limits.ConfirmAboveRecords` to 5,000 so that these are easy to see; profiles can only lower a limit.
