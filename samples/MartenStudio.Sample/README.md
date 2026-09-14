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
| `--allow-data-generation` | Maps the demo-data panel outside `Development`. See below. |

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
