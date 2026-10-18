# Marten Studio: the health advisor

The Health page (`/marten/health`) says what is wrong with the database a studio is pointed at, how sure it
is, and which line of the host's `StoreOptions` would change it. It is an advisor, not a monitor: it pages
nobody, it keeps an hour of history in memory and no more, and it runs only while somebody is looking.

This page is the index. Each area has a file of its own with one section per check, and each section's
heading is the anchor the page's "About" link opens:

| Area | Codes | File |
|---|---|---|
| Daemon and high water | `D` | [`health/daemon.md`](health/daemon.md) |
| Events | `E` | [`health/events.md`](health/events.md) |
| Tables and indexes | `T` | [`health/tables.md`](health/tables.md) |
| Connections and sessions | `C` | [`health/connections.md`](health/connections.md) |
| Replication and WAL | `R` | [`health/replication.md`](health/replication.md) |
| Server | `S` | [`health/server.md`](health/server.md) |
| Configuration | `K` | [`health/configuration.md`](health/configuration.md) |

An area's file is written by the packet that adds its first check; until then its link has nothing behind it.

## What a finding is

Every finding has a code, a severity, a title, the evidence behind it with the time it was measured, the SQL
that read it (Show SQL - parameter names, never values), a caveat that says how sure it is ("counters since
`stats_reset` at 09:14", "cannot see sessions of other roles"), and a remedy.

- **Critical**: something is broken now, or will be soon.
- **Warning**: something is wrong or risky and is worth fixing on purpose.
- **Info**: a setting that would let the store - or the advisor - tell you more.

A remedy is always a `StoreOptions` line for the host's own registration, never DDL. An index, a storage
parameter or a setting the studio created itself would be schema the host's configuration does not know
about, and the next `CreateOrUpdate` would take it away again. A remedy that needs a Marten newer than the
studio's floor says so ("Marten 9.47+") and, on a host that runs an older one, what that host runs.

"Mute in this browser" hides one finding on one database for you, in your browser's `localStorage`. It is
never written to the database and never hides anything from anybody else.

## What it costs

Checks come in four cost tiers:

| Tier | Reads | Runs |
|---|---|---|
| 0 | The store's configuration and the process's own state. No I/O. | With every sweep |
| 1 | The catalog and the statistics views (`pg_class`, `pg_stat_*`, `pg_locks`, `pg_stat_activity`). | With every sweep |
| 2 | A bounded slice of the data itself. | Only behind **Run deeper checks**, for a visitor the store policy allows the database as a whole; cached for five minutes |
| 3 | `pg_stat_statements` and `EXPLAIN`. | Only for a visitor through the activity gate, cached for five minutes |

Who may *start* a costlier sweep is decided by the studio's service, not by the page's button: a tier-2 sweep
reads a slice of every table it checks, so a visitor the store policy admits for one tenant only cannot start
one, whatever their browser sends. They see the button off, the policy it needs, and the results of a run
somebody else started - through the same per-finding gates as everything else.

A sweep runs **only when the Health page or the Overview asks**, and its result is shared by everybody
watching that database until `MartenStudioOptions.HealthInterval` (a minute by default) has passed: ten open
tabs cost one sweep per interval, not ten. There is no hosted service and no timer of the advisor's own - a
studio nobody has open checks nothing. The navigation badge reads the last sweep and never starts one.

Every statement runs in the studio's read-only transaction under `statement_timeout` and `lock_timeout`,
reads the catalog and takes no lock beyond what a catalog read takes, and never runs DDL. A check that fails
is a "could not check" row on the page; it never hides the other checks. A check that cannot run here for a
reason that is the host's choice - no extension, no privilege, an old Postgres - says so and is logged at
Debug, never as a warning.

The page's **Studio cost** line is the database time the studio's own read-only sessions took in the
selected database in the last five minutes: the health checks, the SQL console, the Query page's Marten mode,
the database browser and the schema and relationship reads. The documents, events and projections screens'
own reads are not counted yet. It is a database-wide number, so it is shown at the same gate as one.

## Who sees what

The sweep is about the database; what each visitor is shown of it is decided per visitor:

- A configuration finding (tier 0) is shown to anybody the store policy lets see the scope they selected -
  it says nothing the Configuration page does not. That is a rule the page is kept to rather than a hope:
  what K01 prints (the database's `AutoCreate` and the host's environment name), what K02 and K03 print
  (the two tracking switches) and what K04 prints (the daemon mode and the leadership lock's scope) are on the
  Configuration page's store, Events, daemon and Runtime cards. K04's host and port are evidence at the
  database-wide gate, as the Configuration page's database list is. S01, a catalog read, is shown at the same
  gate as K01: the server's version is on the Overview already.
- A database-wide number needs the store policy for the database as a whole, asked with `TenantId = null`.
- Anything that names a session, a process id, a role, a client address, an application, a statement or a
  tenant needs `MartenStudioOptions.Capabilities.ViewDatabaseActivity` - off by default, off under
  `ReadOnly` - and the write policy, asked with `Capability = "ViewDatabaseActivity"` and `TenantId = null`.
  Without it a visitor still sees the counts and ages, never the names.
- A document type `IsDocumentTypeVisible` hides never appears in a finding.
- A number one check measures can become another's evidence through the history, and only downwards: each
  number keeps the gate of the check that read it, and a check is handed only the numbers read at or below
  its own gate. A count of sessions by role never turns up in a finding every visitor sees.

A check that could not check says why in the studio's own words - which name a code, a count, a type or a
SQLSTATE, never a subject, a tenant or a table. After that, and only under its own label:

- **Postgres said:** Postgres' own message, from a Postgres error and nothing else, with every schema the
  visitor may not see masked out of it by the same rule the database browser uses - or withheld outright
  when that cannot be decided.
- **The connection error said:** the words of a connection failure (Npgsql's or the network's), which name
  the database's address, so only for a visitor through the database-wide gate.

Any other exception's message - a bug in a check, a library's own complaint - is not on the page at all. It
is the author's of whatever threw, it may name something the page withholds, and it is in the server log
under event 9222.

## Export

The advisor pages nobody, but the host's own monitoring already knows how to: an ASP.NET Core health check.
It is opt-in, and `AddMartenStudio()` does not register it:

```csharp
builder.Services.AddMartenStudio();
builder.Services.AddHealthChecks().AddMartenStudio(name: "marten-studio", failureStatus: null, tags: ["marten"]);

// ...
app.MapHealthChecks("/healthz");
```

`AddHealthChecks().AddMartenStudio(...)` takes ASP.NET Core's usual `name` (`marten-studio` unless given),
`failureStatus`, `tags` and `timeout`, and registers one check. Each run sweeps tiers 0 and 1 - the configuration
and catalog checks, never the deeper reads or the statements - over every registered store and the databases
each one holds (*Many databases*, below), straight through the sweeper the Health page uses.

**Status.** Any Critical finding reports `failureStatus` (`Unhealthy` unless given). These report `Degraded`, never
worse than `failureStatus`, so a check registered with `failureStatus: HealthStatus.Degraded` never reports
`Unhealthy`:

- any Warning;
- any check that failed (a "could not check" row on the page);
- any check that ran out of time - a statement that hit `statement_timeout` (57014) or waited too long for a lock
  (55P03), a check past its own budget, or a check the sweep had no time left for;
- a store that would not build or list its databases;
- a run that ran out of its own budget.

Info findings, and checks that could not run for a reason that is the host's choice - no privilege, no extension,
an old Postgres - leave it `Healthy`. The Health page shows both kinds of "not checked" the same way; the export
does not, because a database that cannot answer a catalog read in time is worth somebody's look. A host that added
the health check but not `services.AddMartenStudio()` gets `Degraded` with a description saying which call is
missing. A fault in the export itself is `Degraded` too, naming the exception's type, with its message in event 9270
(a Warning once per ten minutes, Debug while it repeats). The check never throws, except when the probe's own
cancellation fires.

**Data: codes and counts, never names.** There is no visitor to authorize, so the report says only what anyone
who can reach the endpoint may read:

| Key | What it is |
|---|---|
| `critical`, `warning`, `info` | The codes with a finding of that severity in any database, each code once. |
| `failed` | The codes of checks that failed somewhere. |
| `unavailable` | The codes of checks that could not run somewhere for a reason that is the host's choice. |
| `timedOut` | The codes of checks that ran out of time somewhere. |
| `databases` | How many databases were checked. |
| `databasesSkipped` | Databases left out: past the 25 a run checks, or held by a sharded tenancy under an id its `TenantIdStyle` would correct (below). `databases + databasesSkipped` is how many the stores hold. |
| `databasesTimedOut` | Databases the run had not finished when its budget ran out: two minutes, or one `HealthInterval` if that is longer. |
| `storesNotReached` | Stores that would not build, or whose databases could not be listed. |
| `storesNotEnumerated` | Stores whose tenancy holds no database it can name without building one (below). |
| `measuredAt` | When the run began, ISO 8601 in UTC - how old the result being read is. |

Never a tenant, a stream, a session, a process id, a role, a client address, a table, a statement, a store key, a
database name or anything Postgres said. The summary is built from each check's own code and each finding's
severity and nothing else a finding carries, so a check cannot leak through it whatever it writes. A document type
`IsDocumentTypeVisible` hides contributes nothing, not even to a count. The description is the same facts as one
sentence - `1 critical (D02), 2 warnings (C04, T06) on 3 databases. Refreshed after 60 s, served for at most 120 s.`
- with findings counted and each code named once.

ASP.NET Core's default response writer prints only the status word, so a plain `MapHealthChecks` endpoint says
no more than `Healthy`, `Degraded` or `Unhealthy`. The data and the description reach whoever can read the
endpoint only through a response writer the host chooses - HealthChecks.UI's `UIResponseWriter`, the common one,
prints both. One count is worth a thought before choosing one: on a database-per-tenant tenancy,
`databases + databasesSkipped` is the number of tenant databases this process holds.

**Cost.** Concurrent probes share one run, and once a run has completed every probe is answered at once with the
latest result. A probe that finds that result older than `MartenStudioOptions.HealthInterval` (a minute by default,
counted from when the run finished) starts the next run as it goes, and the probes after it get the new result. A
result older than two intervals is not served at all: the probe that finds it - the first after a quiet spell -
waits for a fresh run, as the process's first probe does. So an orchestrator probing every ten seconds costs one
run per interval rather than six and never waits, and no probe is told about a result more than two intervals old;
`measuredAt` says when the one it reads began. A probe that gives up waiting cancels its own wait, never the run;
`timeout` sets how long ASP.NET Core lets a waiting probe wait. There is still no hosted service and no timer: a run
starts only when the endpoint is probed, no new run starts once the application is stopping, and a run in flight
ends with it. The run is no request's child: it is started with the probe's execution context suppressed, so a
trace of the first probe does not include it.

Each run is what a Health page's sweep is - one database at a time, one connection, catalog reads in the read-only
transaction - for each database it checks, and its sweeps add samples to the same history the Health page reads.
Every sweep opens its database's connection through that database's own Npgsql pool. A probe every minute keeps
one physical connection per swept database warm: Npgsql closes an idle pooled connection only after its
`Connection Idle Lifetime` (300 seconds by default), and the next sweep comes sooner. On a database-per-tenant
server that is up to 25 server connections held by the export alone, one per checked database - count them against
`max_connections` (C03 does, since it counts every session on the server).

**Many databases.** A run checks at most 25 databases, in the order the studio lists them: stores in the picker's
order, then each store's databases in Marten's. The rest are counted under `databasesSkipped` and do not change the
status. Enumerating a dynamic tenancy is not a read - it applies, seeds, queries or creates (AGENTS.md D27) - so the
export's own listing never enumerates one. It checks the databases the tenancy already holds, read from memory:

- A single database, or a static multi-tenancy (`MultiTenantedDatabases`), is listed from its configuration.
- A single-server tenancy (`MultiTenantedWithSingleServer`) would run `CREATE DATABASE` for each tenant it was
  given the first time it is enumerated. The probe checks the databases it has already built, and none until
  something else - the daemon, `ApplyAllDatabaseChangesOnStartup()`, the host's own traffic - has built one.
- A master-table or sharded tenancy would apply its lookup tables' schema, upsert its seed rows and read its whole
  master table. The probe checks the databases it holds - the tenants this process has already resolved, or every
  one once something has enumerated it - each found in the tenancy's own cache with `TryFindDatabase`, which never
  provisions. A tenancy that has resolved one tenant is checked for that one tenant, and nothing else is loaded.
  A sharded pool id that the store's `TenantIdStyle` would correct (`Shard_A` under `ForceLowerCase`) would miss
  that cache, so its database is not looked up: it is counted under `databasesSkipped`.
- A tenancy of the host's own, with dynamically created databases, is never listed by a probe.

A store with no database it can name this way is counted under `storesNotEnumerated`; it is an expected state,
logged at Debug, and `Healthy`. The same rule lists the databases for the connection-budget check (C02) and for
the Projections screen's daemon card, both of which run inside every sweep.

**One enumeration that is Marten's.** With a projection coordinator hosted in this process, the daemon checks
(D02 to D05, D08 and C09) ask that coordinator for its daemons, once per sweep between them. Marten's coordinator answers by
enumerating the store - `AllDaemonsAsync()` is `Storage.AllDatabases()` - which is what its own leadership loop
does on every cycle anyway, so a probe can be the first enumerator only in the moment before that loop's first
cycle. Under `MartenDaemonModeIsSolo()`, a testing coordinator with no loop, the probe is the enumerator. Asking
the coordinator for one database's daemon instead (`DaemonForDatabase`) would be worse: on Marten's coordinator it
is `FindOrCreateDatabase`, which provisions.
