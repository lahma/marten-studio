# Marten Studio sample host

A small ASP.NET Core host that configures Marten with the demo domain in
`samples/MartenStudio.SampleDomain`, adds Marten Studio, and mounts it at `/marten`.

```powershell
dotnet run --project samples/MartenStudio.Sample --urls http://localhost:5210
```

With no `ConnectionStrings:Marten` configured it starts a throwaway `postgres:17-alpine` through
Testcontainers and prints a banner saying so. It never falls back to `localhost` silently.

Sign in at `/login` as `admin`, `ops` or `viewer` — the password is the user name.

| Switch | What it does |
|---|---|
| `--anonymous` | Maps the studio with `AllowAnonymous()` and draws a red banner. Never do this anywhere real. |
| `--readonly` | Sets `MartenStudioOptions.ReadOnly`, which turns every mutating capability off. |
| `--path /ops/marten` | Mounts the studio somewhere other than `/marten`, which exercises the sub-path re-rooting. |
| `--allow-data-generation` | Maps the demo-data panel and the "make it unhealthy" switches outside `Development`. See below. It also lets startup run `drop schema … cascade` on `studio_sample` and `studio_sample_events` when another build seeded them or their rows carry no seed marker, and the cascade drops whatever in other schemas depends on them too. |

## The studio's own role

The store logs in to the throwaway container as its superuser, so the sample narrows what the studio reads:
on every start it creates the role `studio_sample_reader` (`NOLOGIN`) if it is missing, makes the store's login
a member, and the seeding grants it `SELECT` on the demo's four schemas - `studio_sample`, `studio_sample_events`,
`quartz` and `legacy` - and on the tables created there later. `MartenStudioOptions.SqlConsoleRole` is set to it,
so the SQL console, the database browser and the health checks read as that role, and the Health page's K05
("SQL console without a narrowed role") shows the remedy applied rather than a Warning.

A role belongs to the whole server, not to one database, so the sample makes it only on the throwaway container or
on a database `--allow-data-generation` names as the demo's. On any other configured database it creates no role and
grants nothing, and the studio reads as the store's login, as it always did; so it does where the login may not
create roles, which the console says at start-up.

The role is also a member of `pg_monitor`, which lets the health checks and Database › Sessions see every session
and statistic as before. That is wider than the four schemas: `pg_monitor` reads no table's rows, but it does read
every session's current statement text across the whole server, and `pg_stat_statements`' statement texts, so the
SQL console - which runs as this role - can read them too. On the demo's own throwaway server that is what it is for;
it is why the sample does not make the role anywhere else.

## Rebuilding ShipmentTracker

`ShipmentTracker` throws on one SKU on purpose, and the sample sets `Projections.Errors.SkipApplyErrors` so the
running shard dead-letters that event and carries on. A rebuild runs under `Projections.RebuildErrors` instead,
which skips nothing by default, so every rebuild of it used to end Failed at the poisoned event. The sample sets
`Projections.RebuildErrors.SkipApplyErrors` too: a rebuild now skips that event into a dead letter and finishes,
which is the demo of a rebuild meeting a bad event.

## Demo data

The landing page has a **Demo data** panel in `Development` (or with `--allow-data-generation`), for the
one question a twenty-five-row demo cannot answer: *does the studio stay usable against a real database?*

It generates realistic data at a chosen size, as a cancellable background job with live progress:

| Size | Documents | Events | Streams |
|---|---|---|---|
| Small | ~76 | ~75 | 25 |
| Medium | ~100 000 | ~100 000 | 20 000 |
| Large | ~1 205 000 | ~1 250 000 | 250 000 |
| Custom | whatever the boxes say | | |

Roughly 11 000 rows per second on a developer machine against a container, so Medium is about twenty
seconds and Large about four minutes.

**What it writes, and why each part is there.**

- Documents go into every demo collection through `IDocumentStore.BulkInsertAsync` — Postgres's binary
  `COPY` — in batches of 5 000. Orders get soft-deleted in bulk afterwards so the documents screen's
  include-deleted tri-state has something to hide; invoices are split across the `acme` and `globex`
  tenants; cars and trucks land in the one `vehicle` table under their own `mt_doc_type`.
- Streams go in through a `LightweightSession`, 500 streams at a time, and **not** through Marten 9.35's
  much faster `BulkInsertEventsAsync`: the fast path bypasses the append pipeline, so it would never run
  the store's *inline* `OrderSummary` projection. The async daemon catches up on `DailySales` and
  `ShipmentTracker` afterwards, which is what makes the projections screen worth opening right after a
  run. The panel shows the daemon's lag, read from `mt_event_progression`, while it does.
- One ~2 MB `MediaAsset` per 100 000 documents, so every list view has a collection it must be seen
  *not* fetching.
- A handful of customers get JSON keys no CLR property matches, written with raw SQL after the insert,
  so the document editor's round-trip diff has real property loss to show.
- One stream in 10 000 carries the SKU `ShipmentTracker` throws on, so dead letters accumulate in
  proportion to the data instead of staying at the seeder's single row.
- `analyze` runs at the end. `pg_class.reltuples` is `-1` until something analyses the table, and that
  estimate is the number the collections rail shows — so without this a million freshly loaded rows
  render as "~0" and the rail pays for an exact `count(*)` instead.

Content is a pure function of the seed, so two runs of one plan write the same names, prices and
addresses. Identity is a function of the run id, so a second run appends a second set of rows rather
than colliding with the first on `customer.email`'s unique index.

**Truncation.** Every generated document carries a `GeneratedRun` marker, so *Truncate* deletes exactly
what was generated and leaves the seeder's own demo data alone. Streams are a different matter: Marten
has no operation that deletes a stream at any level of its API, so the generated ones are **archived**.
To remove the event rows themselves, type `DELETE ALL EVENT DATA` into the box — which calls
`Advanced.Clean.DeleteAllEventDataAsync()` and takes the seeded demo streams and every projection's
progress with it.

**Gating.** Three gates, all of them real:

1. The endpoints are only mapped in `Development` or with `--allow-data-generation`. Outside that they
   are not there at all, so a POST answers 404 rather than 403.
2. Every one of them requires the `MartenStudioAdmin` policy, which only `admin` satisfies.
3. Every POST validates an antiforgery token, exactly as the sign-in form does.

A "generate a million documents" button a cross-site page could press is a denial-of-service gadget, and
being in a sample is not a defence — samples are what people copy.

**Endpoints**, if you would rather drive it with `curl` than with the page:

| | |
|---|---|
| `POST /sample/generate` | Form: `size` (`small`/`medium`/`large`/`custom`), `seed`, and for `custom` the counts. |
| `POST /sample/generate/cancel` | Stops the running job after the batch it is in. |
| `GET /sample/generate/status` | JSON: state, phase, counters, rows/s, elapsed, ETA, and the daemon's lag. |
| `POST /sample/truncate` | Optional `confirmEventData=DELETE ALL EVENT DATA`. |

## Make it unhealthy

The sample starts with no configuration finding: its store turns on `Events.EnableExtendedProgressionTracking`
(the studio's K02 would suggest it otherwise), and Marten 9 already has advanced async tracking on (K03); the
throwaway server's own `synchronous_commit` Info (S02) still shows. Below the demo-data panel, under the same
gates, four switches make the database unhealthy **on purpose**, so a demo or a screenshot can show the studio's
Health page finding something real:

| Switch | What it does | Look for |
|---|---|---|
| **Pinned gap** | A transaction draws the next number from the event sequence Marten uses (read from the store, never typed) and holds it open, while one marker event is committed above it. The daemon's high-water mark cannot pass the number. | D01 once the marker has waited a minute (it needs this host's daemon), C04 for the open transaction, and Projections › Explain. After it is off, Marten skips the abandoned number and D07 shows the skip as an Info for a day. |
| **Idle in transaction** | A session begins a repeatable-read transaction, runs one statement and idles, holding its snapshot. | C04: a Warning after a minute, Critical at ten. |
| **Bloated table** | Recreates the sample's own demo table `studio_sample.demo_bloat`, switches autovacuum off for that table alone, fills it with 30 000 rows and updates each once - half the table dead rows. | T07 at once (with autovacuum off there is nothing to wait for). |
| **Pool hog** | Holds up to 90% of the store's own connection pool open and idle: at most `Maximum Pool Size` less two, at most what the server has room for less five, and it stops growing - rather than wait out the pool's `Timeout` - once no more than two other connections can still be had. Those are limits on what it takes, not a promise about the pool: the host's own work can still use up the connections it left. | C01 at once; C03 too on a server whose `max_connections` is close to the pool's size. |

**Pinned gap and Pool hog run only on the throwaway database.** They reach beyond the demo's own rows - every async
projection stalled, most of a server's connections taken - so on a database somebody configured
(`ConnectionStrings:Marten`), which may be shared, they are refused unless the host was started with
`--allow-data-generation`, which says outright that this database is the demo's to break. Development alone maps the
panel but does not lift the refusal. The panel says which kind of database the host has, and why a switch is refused.

Each switch is a background task of the sample's own on a connection of its own. It turns itself off after **10
minutes**, and the panel says when; it can be turned off by hand; and the host turns every switch off when it
stops. Whichever ends it, it cleans up: the transaction is rolled back, the connections are closed (the pool hog
clears the pool, so they are not left idle in it), and the demo table's autovacuum is reset and the table vacuumed
(or the table created again, if it was cancelled between being dropped and recreated). A clean-up that fails is not
reported as off: the switch shows as Failed, with the reason, and the host logs a Warning. All four are off when the
host starts - and a host that was killed with the bloated table on, which ran no clean-up, has autovacuum put back
and the table vacuumed when it next starts. The panel shows, while a switch is on, what it is holding, when it turns
itself off and which check codes to look for; the Health page checks once a minute while it is open.

The demo table is declared to Marten as one of `StoreOptions.Storage.ExtendedSchemaObjects` (and created when the
host starts), because the studio's table checks judge only the tables a store declares. It holds nothing but the
switch's rows, it is not a document or an event table, and its name does not start with `mt_`. The pinned gap's marker
is an event type of the sample's own, `UnhealthyDemoMarker`, registered with the store so that E05 never calls it
unregistered; no projection reads it.

| | |
|---|---|
| `POST /sample/unhealthy/{switch}/on` | `pinned-gap`, `idle-in-transaction`, `bloated-table` or `pool-hog`. Answers at once. |
| `POST /sample/unhealthy/{switch}/off` | Waits, for a bounded time, until the switch has cleaned up. |
| `GET /sample/unhealthy/status` | JSON: each switch's state, what it is doing, when it turns itself off, and its codes. |

## Responsiveness tests

`tests/MartenStudio.Integration.Tests/Generation/` generates `Medium` into a schema of its own and
asserts that every screen's data call stays inside the budgets in `ResponsivenessBudgets`. The same
assertions run against `Large` when `MARTENSTUDIO_LARGE=1` is set:

```powershell
$env:MARTENSTUDIO_PG_REUSE='false'; $env:MARTENSTUDIO_LARGE='1'
dotnet test tests/MartenStudio.Integration.Tests --filter FullyQualifiedName~Generation
```

A budget that is over for a reason already written down lives in
`ResponsivenessBudgets.KnownHotSpots`, is reported in the test output, and does not fail the run — so
the suite still fails on a *new* one.
