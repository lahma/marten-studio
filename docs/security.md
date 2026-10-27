# Marten Studio and Postgres Studio: the security model

Marten Studio is an admin UI that runs *inside* your application, with your application's own
`IDocumentStore` and your application's own database credentials. It can read and edit every document in
every Marten store in the process, archive event streams, control the projection daemon, apply schema
migrations and run SQL. Nothing about that is incidental — it is what an admin UI is for — so the whole
design is about making every one of those powers something you turned on deliberately, refused in the
place where the operation happens, and recorded whether it succeeded or not.

Postgres Studio, the core Marten Studio is built on since 0.5, runs the same way against the `NpgsqlDataSource`s an
application registered. It has no mutating operation at all: its four capabilities are reads authorized like writes.
Everything below holds for both studios unless a section says otherwise; [Postgres Studio](#postgres-studio) says what
differs.

This page is the long form of the READMEs' *Security model and limits*. It says what the studio
guarantees, what it merely makes harder, and what you have to configure yourself.

## Authentication is never the studio's

The studio authenticates nobody and ships no login page. It asks your application's authorization stack
questions, using the visitor your application already established. In a rendered studio that visitor
comes from `AuthenticationStateProvider` rather than from an `HttpContext`, because a rendered studio is
a Blazor circuit and the request that created it is long gone by the time a page asks for anything.

## Three layers, two switches

| | Option | Decides | Asked |
|---|---|---|---|
| 1 | `AuthorizationPolicy` | Who may open the studio at all | Once, by the authorization middleware, on every studio endpoint |
| 2 | `StoreAuthorizationPolicy` | Which store, database and tenant | On **every** data call, resource-based against `MartenStoreResource` |
| 3 | `WriteAuthorizationPolicy` | Each mutating call | Before every mutation, against the same resource with the capability named |
| — | `Capabilities` | Which mutating operations exist at all in this process | In the service layer, before layer 3 |
| — | `ReadOnly` | Nothing mutating exists | Before everything |

Layer 1 can equally be `RequireAuthorization()` on the builder `MapMartenStudio()` returns. Both cover
the studio's pages **and** its Blazor circuit, which is the half that matters most: a page that answers
401 while `POST /_blazor/negotiate` answers 200 is a studio anyone can drive. The studio therefore stamps
no `AllowAnonymous` of its own on the circuit; only the static assets under `_content/PostgresStudio/` (both
studios' shell) opt out, and only when no studio policy is configured, so that an application with a fail-closed
`FallbackPolicy` can still load a stylesheet.

Layer 2 filters listings rather than annotating them. A tenant you may not see is one you never learn
exists — the *count* of tenants in a process is itself something a tenant should not learn. In Postgres Studio layer 2
is `SourceAuthorizationPolicy`, against a `PostgresStudioResource` ([Postgres Studio](#postgres-studio)).

With no store policy configured, layers 2 and 3 ask nothing and everything passes. That is deliberate:
an application that never set the option must be exactly the application it was.

### `MartenStoreResource`

```csharp
public sealed record MartenStoreResource(
    string StoreName,           // "default", or the marker interface name of an ancillary store
    string DatabaseIdentifier,  // Marten's database identity - never a connection string
    string? TenantId,           // the selected tenant, or null for a view spanning all of them
    string? Capability);        // "EditDocuments", "RunSql", ... - or null for a read
```

One `AuthorizationHandler<TRequirement, MartenStoreResource>` answers for the scope selector, the page
frame and every data call. `Capability` is `null` for a read and carries the capability's own name for a
write, so a single handler can be as coarse or as fine as you want.

`TenantId = null` means every tenant at once, so a handler must require an explicit all-tenants claim for
it rather than let it through. Anything that is not tenant-scoped is asked that way whatever tenant the
visitor has selected: `BrowseDatabase` and `ViewDefinitions` are always authorized against `TenantId = null` (with
`Capability = "BrowseDatabase"` or `"ViewDefinitions"`), because no tenant filters a Quartz or legacy table, and a handler that
confines people to their own tenant therefore refuses them the database browser — which is the point.
`RunSql`, the dead-letter rewind, the progression corrections, rebuilds and a schema apply are asked the
same way, because each of them reaches every tenant in the database (the operations are narrowed to the
tenant only when the database holds that tenant alone). One rewind is the exception: on a store with
`Events.UseTenantPartitionedEvents`, from Marten 9.51 with JasperFx 2.91, the dead-letter rewind is the daemon's
per-tenant one - it stops, rewinds and restarts only that tenant's agents and deletes only that tenant's dead
letters - so it is asked about the dead letter's tenant, and it is offered only where the daemon in this process
runs that tenant's agent. On such a store the database-wide rewind is never offered. One window remains, and
the studio can only keep it short. The agent check is a snapshot of the daemon, taken immediately before the call
(nothing awaits between them), but the window is the whole daemon call: JasperFx stops the tenant's agents, has
Marten rewrite their progression, polls the tenant's high-water mark, and only then restarts the agents. Under
HotCold the node can lose leadership at any point in that, and its coordinator then stops every agent it runs, while
the rewind - which takes no leadership lock - still restarts the tenant's agents here at its end, beside the ones the
new leader starts. This cannot be closed from the studio; the extra agents last until the node's next leadership
poll stops them (at most one `LeadershipPollingTime` after they started). A tenant id
with a `:` in it is never rewound: its agents' identity does not read back as its own, and the daemon would stop
them before Marten refused the rewind. Skip this group (0.6), on the dead-letter page's grouped view, reaches no
further than Skip event: it is Skip event's own path - `ManageDeadLetters`, the write policy, every sequence proved to
be the scope's, and Marten's `MarkEventsAsSkipped` (its tenant overload under `UseTenantPartitionedEvents`, in one
tenant, and refused below Marten 9.51 as Skip event is) - over at most 1,000 of one group's events, audited once with
the group and the count.

The database-wide rewind has the same shape under HotCold: the daemon's `RewindSubscriptionAsync` restarts every agent
of the subscription in the daemon it is asked of, whether or not that daemon ran them (JasperFx 2.74 and 2.95). So it is
refused on a node that does not hold the leadership lock of every one of the projection's shards - asked of the
coordinator's own distributor, in memory - and made from the node that leads it; a Start of a shard another node leads
is refused the same way. Marten Studio's pause keeps a node's locks, so a lock it does not hold while paused is another
node's, or nobody's.

`BrowsableSchemas` limits what the database browser's screens show, not what can be read: the SQL console
reads whatever the connection's role can, in any schema. `SqlConsoleRole` is the real boundary for both,
since every console statement and every browser read runs under `SET LOCAL ROLE` to it.

### The startup guard

An application that maps the studio and says nothing about authorization **does not start**. The check
runs in `IHostedLifecycleService.StartingAsync` — before the web host binds a listener — and runs again
in `StartedAsync` to catch the hosting shapes that map their endpoints from inside the pipeline
(`Startup.Configure`, `UseEndpoints`, a `MapGroup`). An endpoint passes if it carries `IAuthorizeData`
or `IAllowAnonymous`; the whole check passes if the application has a non-null
`AuthorizationOptions.FallbackPolicy`.

The message names the three ways to answer, and `AllowAnonymous()` is one of them — saying "serve this
to anyone" out loud is allowed, and is what the sample's `--anonymous` switch demonstrates. What is not
allowed is saying nothing.

One case is out of scope and always will be: a `MapMartenStudio()` made *after* the host has started —
from a hosted service, or from a lazily built `EndpointDataSource`. Both of the guard's passes have run
by then. There is no hook that would catch it without inspecting every endpoint on every request, which
is a cost the whole application would pay for a mistake nobody has made. Map the studio while the
application is being built.

## The capability model

Fourteen flags on `MartenStudioCapabilities`, every one `false` by default:

`EditDocuments`, `DeleteDocuments`, `ArchiveStreams`, `ManageDeadLetters`, `ControlDaemon`,
`RebuildProjections`, `CorrectProgression`, `ApplySchemaChanges`, `RunSql`, `BrowseDatabase`, `ViewDefinitions`,
`ViewDatabaseActivity`, `CompactStreams`, `RunLinqQueries`.

Five of them are reads rather than writes — `RunSql`, `BrowseDatabase` and `ViewDefinitions` read beyond what Marten
declares, `ViewDatabaseActivity` reads other sessions and their statements, and `RunLinqQueries` evaluates an
expression somebody typed — and they are treated as writes for authorization, because they are the dangerous
reads. `CompactStreams` is a write that deletes events: what it does is in [`compaction.md`](compaction.md).

`MartenStudioCapabilities.All()` is the one-line, greppable opt-in. `MartenStudioOptions.ReadOnly`
overrides all fourteen whatever they say.

This is Hangfire's [GHSA-7rq6-7gv8-c37h](https://github.com/advisories/GHSA-7rq6-7gv8-c37h) applied to
the write axis: the dashboard that is dangerous by default is the one somebody maps without reading the
docs. A freshly mapped Marten Studio is a read-only browser.

**The refusal lives in the service layer, not in the markup.** Hiding a button is a UI convenience; a
Blazor circuit is a long-lived object a client can drive, so every mutating service method begins with
`StudioCapabilityGuard.Require(…)` and throws `StudioCapabilityDeniedException` — whose message names
the exact option a host would set — before anything else happens. The order is always the same:

```text
capability guard  →  scope resolution (with the write policy, capability named)  →  the operation  →  audit
```

and the audit record is written on the failing paths too. A refusal you cannot see in a log is a
refusal you cannot investigate.

Every disabled control in the UI names the property that would enable it
(`MartenStudioOptions.Capabilities.EditDocuments`), because "not permitted" with nothing to act on is
the failure mode of every feature-flagged admin UI.

### `ViewDefinitions`

`BrowseDatabase` covers two different things: the rows of tables and views, and the definitions of views, functions,
triggers, sequences and types. A host can have good reasons to refuse the first and want the second - the studio has no
row-level filtering, some tables hold credentials or personal data, and a view's query, a trigger function or a function
body is schema the application's own migrations wrote, which is what you need to read when Statements or a Health
finding points at one. `ViewDefinitions` is the second alone: off by default, included in `All()`, off under `ReadOnly`,
and authorized as a write - the write policy asked with `Capability = "ViewDefinitions"` and `TenantId = null`.

- **Exactly named schemas only.** It reads the definitions - and, in Marten Studio, the structure - of the schemas
  `BrowsableSchemas` names exactly. A schema only `"*"` admits, which is a statement about the database rather than this
  configuration, still needs `BrowseDatabase` for its definitions, as does an object Marten owns outside the store's own
  schemas. The `‹withheld›` masking of every other schema's name inside a definition is unchanged.
- **Never a row.** The rows of a table and of a view are `BrowseDatabase`'s alone, and refused naming it.
- **`BrowseDatabase` implies it.** A visitor `BrowseDatabase` admits reads definitions exactly as before, whether or not
  `ViewDefinitions` is on; the write policy is asked about `BrowseDatabase` first, and only when it refuses (or the
  capability is off) about `ViewDefinitions`. A definition can carry a literal - a function body with a constant in it -
  which is why this is a capability of its own rather than part of structure.
- **Refused and audited in the service.** The capability is checked, then the store policy, then the write policy; the
  refusal is an Activity entry under `BrowseDatabase` when that is on and under `ViewDefinitions` when it is not, shown
  to a visitor who may read definitions either way.

### `ViewDatabaseActivity`

`pg_stat_activity`, `pg_locks` and `pg_prepared_xacts` describe every session on the database server, not
only this application's: other applications' process ids, roles, client addresses and application names,
and the text of the statements they are running — literals included, and the names of schemas the
database browser would withhold. `ViewDatabaseActivity` is the capability for that, shaped like
`BrowseDatabase`: off by default, included in `All()`, off under `ReadOnly`, and authorized as a write —
the write policy asked with `Capability = "ViewDatabaseActivity"` and `TenantId = null`, because a session
belongs to no tenant. It is for the people who operate the database rather than those who browse its data.

Without it, a visitor the store policy passes for the database as a whole still sees how many sessions
there are, how old their transactions are, what state they are in, the class of what they wait for, lock
modes, and which of this store's objects is locked (the apply-changes lock, the daemon's lock, an
`mt_streams` row, a document table) — on the Health page's connection findings and on Database › Sessions
alike — and never a process id, a role, an address, an application name or a statement. A lock label names
only this store's own declared, visible objects and Postgres' system catalogs, and always by the table a
locked index, partition or TOAST table belongs to — never by that relation's own name, which can carry a
tenant id or a hidden type's alias; a hidden type's table, another store's table and an `mt_` name no store
declares are named to nobody, details included. A session the studio's role cannot see into is counted, and
said to be one it cannot see into, never left out.

The refusal lives in the service (`ISessionActivityService.GetDetailsAsync` refuses as a value, and every
refusal is audited); on the Health page each such piece of evidence is withheld per visitor. Statements
appear on Database › Sessions, in the Projections screen's high-water Explain and in the findings of the
statement checks T02 and T23 (behind **Run statement checks**; T22 shows only counts by kind of write), by
one rule for all three — masked for the schemas that visitor may not see, cut before a half-written name,
and withheld outright when the visitor's schemas cannot be checked, when the statement names a document type
`IsDocumentTypeVisible` hides, while hiding is configured when it names a Marten document table no
registered store declares, or when it names a document table of another registered store whose store policy
refuses the visitor (its alias is in the table's name). A statement check reads `pg_stat_statements` as the
store's own role, so its texts are held to more: a text is shown only with every comment printed as
`/* ‹withheld› */`, and is withheld outright, with the finding and its numbers kept and nothing planned,
while it still carries a string or dollar-quoted constant as written, or is not a plain query or write (a
`COPY`, `CALL`, `DECLARE`, `FETCH` or `select … into`). A numeric constant in a text Postgres kept
un-normalised is not caught, and is shown.

What a shown statement can still carry is everything that is not a schema's or a table's name: literals and
bound values as the application wrote them — a tenant id in `where tenant_id = 'acme'`, an e-mail address, an
amount, a document's JSON in an `insert` — column and JSON member names, a schema it reaches only through its own
`search_path` (an unqualified name is not a qualifier to mask), and any name it builds in dynamic SQL. Statements
are other people's source text; grant `ViewDatabaseActivity` to the people you would let read them.

Both are audited by signature, never per poll. The first read of session details per circuit, store and
database is one Activity entry and event 9245. On the Health page, the first report in a circuit that shows
any evidence behind the activity gate for a store and database is one Activity entry under
`ViewDatabaseActivity` (event 9200, with the checks it came from), and another is recorded only when the set
of checks showing it changes; the sidebar's health badge shows no evidence and records nothing. A read that
fails for a reason nobody chose is event 9246, throttled. The studio never cancels or terminates a session.

Database › Sessions › Blocking (0.6) draws the same snapshot as trees, behind the same gate and audited with the rest of
the details: its counts - chains, sessions held up, the longest wait, heads idle in a transaction - are the database's,
and the trees, which name every session, are details. A relation a session waits for is named only past the gate, and
masked as the database browser masks it: `‹withheld›`, with no name, for a schema the visitor may not see. The head of a
chain is shown with its `select pg_terminate_backend(<pid>);` - and `pg_cancel_backend`, when it runs a statement - as text
to copy for whoever may run it: there is no button that runs either, and nothing in either package executes them. C12,
which names the heads of chains in its findings, is an activity check.

Which names stay at the database-wide gate, and why: the name of the role the studio reads as (`SqlConsoleRole`, or
the store's or data source's own login where none is set) and the login the data source connects as are
configuration, not somebody else's session, so a "not checked" row names the role and the grant it lacks, and S04,
S05, S08, S10, C02 and C03 name the login, to a visitor the check's own gate admits. Database › Sessions' "By
application" shows its totals at that gate too; the application names need `ViewDatabaseActivity`.

### Operations › Statements

Operations › Statements shows other programs' SQL. It needs `ViewDatabaseActivity` and the write policy for the
database as a whole. It reads `pg_stat_statements` as the source's own role, and sees only that role's statements in
that database. So another role's statements never appear, but another application's do when it connects as the same
role.

A text that still carries a string constant as written, or that is not a plain query or write, is never shown.
Comments are masked, and schema names a visitor may not see are masked. A hidden document type's statements are
grouped as "a hidden type" and never shown. The page's first read in a circuit is one Activity entry (event 9452).

A statement's drill-in (`?queryid=`, 0.6) shows its text, its counters and its generic plan, behind the same gate. It
plans the text `pg_stat_statements` recorded by `prepare` and `force_generic_plan`, with `VERBOSE`, as
`SqlConsoleRole`, in the read-only transaction, under the visitor's `search_path` less the schemas withheld from them,
and deallocates what it prepared. A text withheld from the visitor, one that names a schema the visitor may not see
(wherever their masked
text shows `‹withheld›`, or, while any schema is withheld, a `U&"…"` name the mask cannot read), a write, a locking read
and a cut text are never planned, and no What if? is asked of them: a schema-qualified view or `LANGUAGE sql` function
would be expanded or inlined into the tables it reads, and `VERBOSE` would print its definition. A view or SQL function
in a visible schema that reads a withheld one is expanded like any other (a known limit, `docs/health/plans.md`). A plan
that reads a schema the visitor may not see is not shown; every other plan is masked as the text is.
Each plan is audited by `queryid` ("Plan database statement"), and so is each What if? there ("What-if database
statement").

### Replication names

Replication shows counts, sizes, lag, LSNs and settings at the database-wide gate. Standby application names, client
addresses and roles, slot names, and `archive_command`'s text need `ViewDatabaseActivity` and the write policy. The
first view that shows them is audited as "Read replication names". Replication never names another database's logical
WAL senders or slots, whatever the visitor may see: they are counted, and their application name, client address and
role are blanked in the query itself.

### Aurora

On Aurora, instance identifiers are shown at the database-wide gate: an instance's identifier is the cluster's own name
for one of its instances, not a session's, a role's or a client's, so R03's `aurora-reader` evidence, the Replication
page's Aurora replicas, the "Answered by" line, the Server page's Aurora section and the capacity line need no
`ViewDatabaseActivity`. Each session's waits (`aurora_stat_backend_waits`) need `ViewDatabaseActivity`, are read only
for a visitor through that gate, are shown as counts and shares, never as a time, and are audited on first view
("Read Aurora session waits").
Aurora's functions are called only once the platform read has found `aurora_version()` in `pg_catalog`, where nobody
short of a superuser may create one, and each is called by its `pg_catalog` name ([aurora.md](aurora.md)).

### Storage

The Storage page is a database-wide read, shown to a visitor the store (or source) policy passes for the database
with `TenantId = null`. A table in a schema outside the source's own is named only to a visitor the database
browser's gate admits to that schema (`BrowseDatabase`, the write policy and a `BrowsableSchemas` entry); anybody
else sees its sizes, with `‹withheld›` in place of its name, owner and link. `pg_database_size` needs `CONNECT` on the
database, or `pg_read_all_stats`, for the role the studio reads as (`SqlConsoleRole` when set). Without it, the page
says so, names the grant, and shows the catalog's estimate. The sizes come from the catalog's page counts, so the page
locks nothing it lists.

### Measure bloat

**Measure bloat**, on the Maintenance page, is on demand and audited like an exact count (D8), and needs no capability
of its own. It runs in a read-only transaction as `SqlConsoleRole`, under `lock_timeout`, one measure per database at a
time. The heap is read first, by `pgstattuple_approx`, in a statement of its own. Then `pgstatindex` reads up to the
five largest indexes, smallest first, each under what is left of `QueryTimeout`; when the time runs out, the measure
stops and keeps what it has. While it reads, it holds an `AccessShareLock` on the table and on the index being
measured. A migration's `ALTER TABLE` that arrives meanwhile waits behind that lock, and every reader of the table then
waits behind the `ALTER`. `lock_timeout` keeps the measure from queueing behind a migration, but not a migration from
queueing behind the measure. On a busy table, measure outside a deploy window. Every press is recorded as "Measure
bloat" with the table and nothing else, under `BrowseDatabase` when the table is in a schema only that gate shows.

### `RunLinqQueries`

LINQ mode ([`linq-mode.md`](linq-mode.md)) takes a predicate somebody typed, such as
`Status == "Open" && Total > 100`, and hands it to Marten's own `Query<T>()`. It changes nothing, but its
risk is not the SQL console's. Marten's LINQ provider evaluates **in this process** every part of an
expression that does not reference the document — it compiles the subtree and invokes it to get a constant —
so a parsed `Status == System.IO.File.ReadAllText("c:/x")` would be code running inside your application.
That is why it is a capability of its own: off by default, included in `All()`, off under `ReadOnly`,
authorized as a write (the write policy asked with `Capability = "RunLinqQueries"`), and present only on
Marten 9.48 or later, which brings JasperFx 2.84 and its `DynamicQuery`.

**What it can read.** Any document of a type the visitor can see — never one `IsDocumentTypeVisible` hides,
directly or through a navigation — as Marten's own `Query<T>()` reads it, on a session opened with
`SessionOptions.ForDatabase(tenant, database)`. Marten writes its own tenant predicate and soft-delete
filter, and nothing in a predicate can widen them: `AnyTenant()`, `TenantIsOneOf()` and `MatchesSql()` are
extension methods, which the parser cannot call. So the rows of a conjoined collection are asked for with
the tenant, and the rows of a collection not divided by tenant, read from a tenant scope, and the plan of
any query are asked with `TenantId = null`, because they reach the database as a whole. A hidden subclass
under a visible root is read through the root, as on the Documents screen. The read runs in the console's
read-only transaction under `QueryTimeout` and `SqlConsoleRole`, and no page starts past row 10,000.

**Where its guard lives.** Not in one library. Before Marten sees anything, the text passes the studio's own
lexical screen — every name bound to a member of the document's graph where the parser will bind it, size
and depth caps, no type names, no `new`, `iif`, `@`, indexers or casts — then JasperFx's Dynamic LINQ under
its allow-list (`DynamicQueryPolicy`, with the studio's guard as its first shape rule and `SqlNullSemantics`
after it), then the studio's own walk of the whole composed expression, which refuses every node that is not
on a closed list. The only parts of an accepted expression that do not reference the document are literals,
so the most Marten can evaluate in this process is a constant. The mode refuses to run on a
System.Linq.Dynamic.Core older than 1.7.4, the version JasperFx 2.84 requires and one past the fixes for
CVE-2023-32571 (code execution through reflection) and CVE-2024-51417.

**What it never runs.** Never `ANALYZE`: the plan is `EXPLAIN` of Marten's statement, which plans without
executing. And never `ToCommand`: at Marten 9.39 and 9.50 alike, `ToCommand` — like every LINQ execution
path — runs `EnsureStorageExists` for the queried type, which under the database's `AutoCreate` creates or
migrates its table and, through `AllObjects()`, `mt_hilo`. The studio builds Marten's statement the way
`ToCommand` builds it, minus that call, through Marten's own LINQ parser reached by reflection; a Marten
that changes it makes the mode absent rather than falling back to `ToCommand`. Marten's statement must then
pass the console's `ReadOnlySqlGuard` as well.

Every refusal, and the first run of each query in a circuit, is audited (events 9260 to 9262), and the
predicate is in your logs from then on. **Grant `RunLinqQueries` to people you would trust with the store's
data.**

### Time travel and the stepper run your projection's code

A stream's *time travel* (the aggregate at a version) and its *stepper* (the aggregate after each event, and
what each event changed) fold the stream with Marten's own aggregator. That aggregator calls your
projection's own `Create`, `Apply`, `ShouldDelete` and `Evolve` methods, in this process, on a session pinned
to the scope's database and tenant. **Neither has a gate beyond reading the stream**: the store policy for the
scope, as every read asks it. There is no capability for either, and nothing is audited, because neither
writes anything, starts nothing and asks no daemon. A visitor who can open a stream can therefore make your
`Apply` methods run against its events, as often as they click. What those methods do is yours: one that
loads other documents through the session it is handed reads them as your code wrote it (and a Marten
`LoadAsync` there may ensure that document type's storage, which the studio's own reads never do). The fold
runs under `QueryTimeout`. An exception a method throws is shown on the page, its type and message: the
stepper stops on that event and logs nothing, since a projection that fails on an event is what it is there to
find; time travel reports it as a failed read (event 9217, throttled). The aggregate types offered are the
store's own single-stream projections and the document types `IsDocumentTypeVisible` shows, and a type name is
never looked up from what the browser sent.

### The opt-in health check

`services.AddHealthChecks().AddMartenStudio()` (or `.AddPostgresStudio()`) runs the advisor's configuration and
catalog checks with no visitor at all - every registered store or source, up to 25 databases a run in rotation, no
studio policy asked - so the host's own health endpoint is its only gate. That is acceptable only because of what it
reports: check codes, outcomes, severities and counts, never a tenant, a stream, a session, a role, a table, a
statement, a store or source key, a database name or anything Postgres said ([`health.md`](health.md#export)). With a
response writer that prints the data, `databases + databasesSkipped` on a database-per-tenant tenancy is the number of
tenant databases this process holds. Neither `AddMartenStudio()` nor `AddPostgresStudio()` registers it.

### The studio checks its own configuration

- K05 is D13 as a finding: with `Capabilities.RunSql` on and no `SqlConsoleRole`, the console reads everything the
  data source's login can.
- K06 is D27 as a finding: with `BrowseDatabase` on, `BrowsableSchemas` containing `"*"` and no `SqlConsoleRole`,
  every table that login may select from is browsable row by row.
- K07 says when the studio is served anonymously with any capability on (Critical in Production).

The remedy for K05 and K06 is one option line, `SqlConsoleRole`, naming a role a DBA creates with `SELECT` on what the
console and browser should read. `ReadOnly` mutes all three.

### Muting a finding

"Mute in this browser" keeps an HMAC-SHA256 of each finding's key in the browser's `localStorage`, under a random key of
that browser's own, which the browser holds only as your application's ASP.NET Core Data Protection protected it. A key
names a store, a database, a code and a subject - a tenant or a stream, sometimes - so neither the key nor a plain hash
of it, which anybody who could guess a whole key could confirm, is kept. The studio registers no Data Protection of its
own, and nothing a mute writes reaches the database or another visitor ([health.md](health.md#what-a-finding-is)).

## The diagnostics API

`IPostgresDiagnostics` ([`diagnostics.md`](diagnostics.md)) answers the host's own code, not a visitor. No authorization
policy, capability or `ReadOnly` setting is consulted, because those decide what the studio's pages show to the people
who open them. It reads nothing your code could not read through the same data source or store, as the same roles:
`SqlConsoleRole` for every read but the statement checks'. Those run as the store's own role, fenced to
`userid = current_user` (D29's first exception).

It does not apply the per-visitor decisions, nor the database browser's masking of schemas withheld from a visitor. It
always applies the rules about content:

- a document type hidden with `IsDocumentTypeVisible` is never named;
- a statement text that still carries a literal, or is not a plain query or write, is withheld;
- comments are masked;
- texts are capped;
- Postgres' own error words are never carried, only the SQLSTATE.

Activity-grade data is opt-in per call with `IncludeActivity`, which is off by default: statement texts, application
names, other sessions' evidence, and findings about one tenant. Treat a result as you would the output of
`pg_stat_activity`. It names tables, indexes and settings, and with `IncludeActivity` other programs' statements.

The studio never serves a result: no endpoint maps one, no log line above Debug carries one, no metric and no Activity
entry records one, and the opt-in health check stays codes and counts. An API call is not audited, because your code is
not a visitor; it is event 9250, at Debug. If you expose a result yourself, put it behind your own authorization.

The Health page's Snapshot download is not this API. It is built from the visitor's own pages, and holds only what they
show that visitor.

## Postgres Studio

Postgres Studio has the same three layers, the same switches and the same startup guard. What differs:

- **The resource.** Layer 2 is `SourceAuthorizationPolicy`, asked about a `PostgresStudioResource(SourceName,
  DatabaseIdentifier, Capability)`: the source's key (`default`, or a name given to `AddDataSource` or
  `AddDatabaseSource`) and the database's identity in that source - a registration name or a name the host listed,
  never a connection string. It has **no tenant**, and nothing in Postgres Studio filters a table by tenant: a visitor
  who may read a table's rows reads all of them. It is a different type from `MartenStoreResource` on purpose, so a
  handler written for one is never handed the other.
- **Four capabilities, all reads.** `BrowseDatabase`, `ViewDefinitions`, `ViewDatabaseActivity` and `RunSql`, off by
  default, in `All()`, off under `ReadOnly`, and each asked of the write policy with its name. There is nothing to edit,
  delete, rebuild or apply.
- **The schemas it browses.** `BrowsableSchemas` plays the store's own schemas: the schemas it names exactly - `public`
  when it names none - have their structure shown to anyone the source policy admits, their definitions need
  `ViewDefinitions` or `BrowseDatabase`, and their rows `BrowseDatabase`, each with the write policy. A schema only `"*"`
  admits needs `BrowseDatabase` for its structure and definitions too. As in Marten
  Studio, it limits what the screens show, not what the console can read; `SqlConsoleRole` is the boundary.
- **Marten's tables.** Postgres Studio knows no store's declarations, so the `mt_` prefix is the only thing that names
  an object Marten's, and it fails closed: such an object's rows are never read, nor a view over one, and its
  definition is shown only with `BrowseDatabase`. It cannot honour another application's `IsDocumentTypeVisible`; a
  Marten database whose types a host hides belongs in Marten Studio.
- **What it reads with.** The application's own data sources, with their role, pool and TLS; a factory's result is kept
  for the process and never disposed, and a scoped or transient registration is refused rather than resolved per
  visitor. A host database source must list without creating anything, and its names are identities.

## The SQL console

`RunSql` is off by default like every other capability. What makes the console safe when it *is* on is
the transaction, not the parser.

### What the database guarantees

Every statement runs on a connection of its own as:

```sql
BEGIN;
  SET TRANSACTION READ ONLY;
  SET LOCAL statement_timeout = …;                    -- MartenStudioOptions.QueryTimeout
  SET LOCAL lock_timeout = …;                         -- 3 seconds
  SET LOCAL idle_in_transaction_session_timeout = …;  -- 60 seconds
  SET LOCAL ROLE …;                                   -- MartenStudioOptions.SqlConsoleRole, when set
  <your statement>
ROLLBACK;                                             -- always, on its own cancellation token
```

Every GUC — the role included — is set through `select set_config(@name, @value, true)` rather than a
`SET` statement, because `SET` takes no parameters and would mean interpolating text into SQL. The role
is *also* validated at startup against the shape of a plain Postgres identifier; both, not either.

So: **no `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` or DDL survives**, whatever the parser thought. Three
timeouts, because there are three ways to hang: the query itself, a lock queue in front of somebody's
migration, and a transaction outliving the circuit that started it. And the row cap
(`MaxSqlConsoleRows`) stops the *reader* — it never appends a `LIMIT` to your statement, because
rewriting somebody's SQL would silently change an aggregate, a window function or a `LIMIT` they wrote
themselves.

### What the parser is for

`ReadOnlySqlGuard` allows only statements whose first significant token is `select`, `with`, `explain`,
`table` or `values`, and only one statement — a `;` outside a string, a quoted identifier, a
dollar-quoted body or a comment ends it, and anything but whitespace and comments after that is a second
statement. `EXPLAIN` gets one extra check, because `EXPLAIN ANALYZE` *runs* what it is given, so the
guard looks past the explain options and holds what follows to the same list.

**This is advisory and must never be treated as the security boundary.** It exists so that
`delete from mt_doc_customer` produces a sentence instead of Postgres' `25006: cannot execute DELETE in
a read-only transaction`. Every statement the parser lets through and the database refuses is working as
designed: `WITH x AS (DELETE … RETURNING *) SELECT * FROM x` is exactly that statement, and the
integration tests assert it comes back as `25006` with the table unchanged.

### Explain analyze and What if?

The console's **Explain analyze** button (0.6), like a typed `EXPLAIN ANALYZE`, runs the statement: that is what
`ANALYZE` means. So it runs only in the console, under `RunSql`, for a statement the guard accepts, inside the same
read-only transaction and `statement_timeout`. A write the guard misses is refused by Postgres (`25006`) and changes
nothing, and every run and refusal is audited as a `RunSql` run (9204, 9205). Mode A's plan, LINQ mode's and the
Statements drill-in's are `EXPLAIN` without `ANALYZE`.

**What if?** (0.6) plans a statement again with one index imagined by hypopg. The studio never runs
`CREATE EXTENSION`: whether hypopg is installed is the host's decision, and without it, or when the role the studio
plans as may not use it, the page says which. The index is only one a builder wrote - read off the plan's sequential
scan, or Marten's own DDL for the advisor's `StoreOptions` line - and the service finds it again from the plan or the
advice, never from text the page sent. It runs behind the gate of the plan it accompanies (`RunSql` under the console's
and Mode A's plans, `RunLinqQueries` beside LINQ mode's and the Documents list's index advice, the Statements page's
own gate on the drill-in), in the read-only transaction under `statement_timeout` and `lock_timeout`, and every
what-if is audited. A hypothetical index lives in the backend that made it and survives a rollback there, so the
studio calls `hypopg_reset()` on the same connection before the connection goes back to the pool, again after a
failure or a cancellation, and a session whose reset fails ends itself: no pooled connection keeps a hypothetical
index.

### The function denylist, and why it is not a guarantee

A read-only transaction is not a sandbox. Verified against PostgreSQL 17,
`select pg_terminate_backend(pg_backend_pid())` runs happily inside one and kills the backend; so do
`pg_read_file`, `lo_export`, `pg_ls_dir`, `dblink` and the session-level advisory-lock family, because
none of them is a *write* as far as SQLSTATE 25006 is concerned.

The console therefore refuses them **by name**. This is the complete list, from
`ReadOnlySqlGuard.DisallowedFunctions`, with the reason each one is given to the user. Matching is
case-insensitive against every identifier token in the statement, which also catches the
schema-qualified spelling — `pg_catalog.pg_terminate_backend` reaches the scanner as three tokens and it
is the bare name that is looked up. Quoted strings, quoted identifiers, dollar-quoted bodies and
comments are skipped, so they cannot trip it either.

| Function | Refused because it… |
|---|---|
| `pg_terminate_backend` | terminates other sessions |
| `pg_cancel_backend` | cancels other sessions' running statements |
| `pg_log_backend_memory_contexts` | makes another session dump its memory contexts to the server log |
| `pg_reload_conf` | makes the server re-read its configuration files |
| `pg_rotate_logfile` | rotates the server's log file |
| `pg_stat_reset` | throws away the statistics the planner and everyone else depends on |
| `pg_switch_wal` | forces a write-ahead-log switch |
| `pg_create_restore_point` | writes a named restore point into the write-ahead log |
| `pg_backup_start` | puts the server into backup mode |
| `pg_backup_stop` | ends the server's backup mode |
| `pg_promote` | promotes a standby to be a primary |
| `pg_read_file` | reads files from the server's filesystem |
| `pg_read_binary_file` | reads files from the server's filesystem |
| `pg_stat_file` | reads file metadata from the server's filesystem |
| `pg_ls_dir` | lists the server's filesystem |
| `pg_ls_logdir` | lists the server's log directory |
| `pg_ls_waldir` | lists the server's write-ahead-log directory |
| `lo_export` | writes a file to the server's filesystem |
| `lo_import` | reads a file from the server's filesystem into a large object |
| `lo_unlink` | deletes a large object, which no read-only transaction prevents |
| `dblink` | opens a connection to another database, outside this transaction and its read-only flag |
| `dblink_exec` | runs a statement on another database, outside this transaction and its read-only flag |
| `dblink_connect` | opens a connection to another database, outside this transaction and its read-only flag |
| `pg_advisory_lock` | takes a session-level advisory lock that outlives this transaction |
| `pg_advisory_lock_shared` | takes a session-level advisory lock that outlives this transaction |
| `pg_advisory_xact_lock` | takes an advisory lock Marten's own daemon contends for |
| `pg_advisory_xact_lock_shared` | takes an advisory lock Marten's own daemon contends for |
| `pg_sleep_for` | parks the session for an interval rather than doing work |
| `pg_sleep_until` | parks the session until a wall-clock time the statement timeout does not describe |
| `set_config` | changes a server setting, which is how every other limit here would be undone |
| `pg_notify` | sends a notification to other sessions listening on this database |

Two deliberate omissions: `pg_sleep` is allowed, because `statement_timeout` bounds it and it is how the
tests prove the timeout fires; `txid_current` is allowed, because it assigns a transaction id and
nothing more.

**That refusal is worth exactly what a parser's refusal is worth.** A determined caller can reach the
same function through a view, a `SECURITY DEFINER` wrapper or an alias, and this list will not see it.
The list is here so that a host which configured no role is still not one paste away from terminating
its own connections.

### The only true narrowing

**`MartenStudioOptions.SqlConsoleRole`.** A Postgres role that was never granted `EXECUTE` on those
functions cannot call them however they are spelled, and a role granted `SELECT` on only the tables you
meant can read only those. That is a guarantee the database makes; everything above it is a guarantee
the studio makes. If you enable `RunSql` on anything that matters, configure a role.

Without one, the console reads everything the store's own Postgres role can read — every table in the
database, Marten's or not, including tables belonging to other applications sharing it.

### Residual risks, stated plainly

- Reading is the point, and reading is not narrowed except by the role. The console is a read of the
  whole database.
- Denylisted functions are refused by name, not by permission. Assume the denylist is bypassable.
- `EXPLAIN` without `ANALYZE` is offered for a Mode A clause when `RunSql` is granted; it plans rather
  than runs, but it is still a statement sent to Postgres under this capability, and it is audited as
  one.
- A long-running `select` can still consume a connection and CPU until `statement_timeout` fires.
  `QueryTimeout` is the bound; set it to something you would tolerate.
- The statement text itself is written to the audit ring and to your `ILogger` (event ids 9204 and
  9205). If a query would contain a secret, that secret is now in your logs.

**Grant `RunSql` only to people you would give `psql` to.**

## Mode A: the Marten `where` clause

The Query page's other mode needs *no* capability, because it is a read against one document type — the
same thing the documents list does with a search box. The studio composes the statement itself:

```sql
select d."id", d."data"::text /*, d."tenant_id", d."mt_deleted" when the table has them */
from <schema>."mt_doc_<alias>" as d
where 1 = 1 and d."tenant_id" = @tenant and d."mt_deleted" = false
  and ( <your clause> )
  and d."tenant_id" = @tenant and d."mt_deleted" = false
order by … limit @limit
```

The tenant predicate and the soft-delete predicate are the Documents browser's, decided from the
document type's configuration reconciled against the physical columns, and the SQL the page shows is the
SQL that ran. Marten's own string query (`Query<T>("where …")`) is never used for this: it applies
neither predicate. The studio's terms are repeated *after* your predicate as well as in front of it,
because a parenthesised predicate only contains an `or` for as long as the parentheses hold — and
`1 = 1) or (1 = 1` was measured closing the studio's own bracket and returning four rows on a scope
narrowed to one tenant. The clause is held to a stricter shape than the console for the same reason it
needs no capability, and it runs inside the same read-only transaction the console does, with the same
`statement_timeout`, `lock_timeout`, `idle_in_transaction_session_timeout` and optional
`SqlConsoleRole`.

Six rules, all structural rather than semantic:

1. **Always:** no `;` outside a string, quoted identifier, dollar-quoted body or comment.
2. **Always:** the clause does not begin a statement of its own — `select`, `with`, `insert`, `update`,
   `delete`, `merge`, `truncate`, `drop`, `alter`, `create`, `grant`, `revoke`, `copy`, `explain`,
   `call`, `do`, `vacuum`, `analyze`, `set`, `reset`, `table`, `values`, `begin`, `commit`, `rollback`,
   `listen`, `notify`, `lock`, `refresh`, `reindex`, `cluster`, `comment`, `security`, `prepare`,
   `execute`, `deallocate`, `discard`, `import`, `checkpoint`.
3. **Always:** the clause's parentheses balance — a `)` that closes nothing would close the studio's own
   wrapper, and a `(` left open would swallow it.
4. **Always:** the tail must then be `[order by <sort list>] [limit <integer>] [offset <integer>]` and
   nothing else — each at most once, `order by` first, both counts plain non-negative integers, and the
   sort list free of `for`, `fetch`, `into`, `union`, `intersect` and `except` at parenthesis depth zero.
   Postgres accepts a locking clause between `ORDER BY` and `LIMIT`, and `order by 1 for update` is not a
   read.
5. **Always:** no function that reads, writes or waits outside the row — the console's denylist above,
   plus `pg_sleep`, `current_setting`, `query_to_xml`, `query_to_xml_and_xmlschema`, `table_to_xml`,
   `xpath`, `xpath_exists`, `nextval`, `setval` — and no `::regclass` or `::regproc` cast.
6. **Without `RunSql`:** no word that reaches another relation (`select`, `from`, `union`, `intersect`,
   `except`, `join`, `into`, `with`, `lateral`, `returning`, `copy`, `do`, `call`, `execute`).

A visitor who *may* run SQL — `RunSql` enabled **and** allowed by the store policy and the write policy
against this scope's database as a whole (`TenantId = null`) — gets rule 6 lifted, and only rule 6, because everything it refuses they could type into the
console instead. Asking only the capability and not the policy would hand subqueries to somebody the
write policy refuses the console to, which is the hole the policy exists to close.

Rule 6 has deliberate false positives: `from` is in the list, so `extract(year from …)`,
`substring(x from 1)` and `trim(both ' ' from x)` are refused. The list is structural, the escape hatch
is the capability, and a parser treated as a security boundary is how these features get CVEs. Every
refusal names `MartenStudioOptions.Capabilities.RunSql` as the thing that would lift it.

### What Mode A does **not** do

It does not make the clause guard the only thing between a visitor and the database, and it never did
the opposite either. The statement runs inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK`, so a
clause that reaches a writing function through a view or a `SECURITY DEFINER` wrapper comes back as
`25006` and changes nothing. What a read-only transaction does *not* refuse is an advisory lock, a
`setval`, a `pg_terminate_backend` or a `pg_read_file` — which is precisely why the function denylist
applies to a `RunSql` holder here as well, unlike the nested-read rule.

It does not let you bind parameters. The clause is SQL text, so a literal you paste in is SQL too.

Every successful run is audited under 9204 `SqlExecuted` with the statement that ran; every refusal
under 9205 `SqlRejected`, and a scope refusal under 9203 `ScopeAuthorizationDenied` — so a clause that
should not have been typed is on the record either way.

## What the studio never does

- **Never starts a second projection daemon.** `store.BuildProjectionDaemonAsync()` is never called: it
  would start a daemon alongside the host's own and the two would fight over the same advisory locks
  until one hung. The only supported way to reach a running daemon is the DI-registered coordinator, and
  its absence is a *value* — "not hosted in this process" — rather than an error.
- **Never executes DDL on a read or navigation path.** Below Marten 9.50.3 `IMartenDatabase.AllSchemaNames()`
  and `AllObjects()` *apply migrations* — a single `AllSchemaNames()` call on an empty schema creates
  `mt_hilo` and `mt_get_next_hi`, proven live at the floor — so the studio calls neither, at any version
  (9.50.3 made them describe the HiLo feature without applying it, and the floor is older). Schema names, declared
  indexes, managed tables and installed functions come from `IReadOnlyStoreOptions`, `IDocumentType` and
  the event store's own feature schema, and touch no connection. `Check`, `Preview` and `DDL` are behind
  buttons that say on the button what they may create. An apply is gated on `ApplySchemaChanges`, runs
  under `AutoCreate.CreateOrUpdate` and never `AutoCreate.All`, lists its destructive statements, and
  requires a typed confirmation that travels to the service rather than being re-supplied by the page.
  The one exception is Marten's, not the studio's: enumerating a master-table, sharded or single-server
  tenancy's databases builds them, and the first time a store instance does so it applies the tenancy's
  lookup table or creates a configured database that does not exist yet - and the studio's first
  navigation can be that enumeration (AGENTS.md D27).
- **Never writes document DML of its own.** There is no `mt_upsert_*` function to call — Marten 9.35
  installs none, and the upsert is inline SQL in its generated write path — and re-implementing that
  write path is how a UI corrupts a store. Writes go through a Marten session (`StoreObjects`,
  `UpdateExpectedVersion<T>`, `UpdateRevision<T>`, `Delete<T>(id)`, `UndoDeleteWhere<T>`), so metadata
  columns, soft-delete semantics, hierarchy discriminators and tenancy stay exactly as Marten defines
  them. Reads are parameterised SQL built in one place per assembly, `Internal/Sql/` in PostgresStudio and in
  MartenStudio, with identifiers quoted through a builder and values always parameters.
- **Never replaces your `StoreOptions.Logger`.** A live SQL tail would require it, which is exactly the
  "registration changes host behaviour" failure this project refuses. Every generated grid carries a
  "Show SQL" disclosure instead.
- **Never shows a connection string or a credential.** A database is identified by server, port, name
  and schema, everywhere — in the Overview, in the Configuration dump, in the audit log and in
  `MartenStoreResource.DatabaseIdentifier`.
- **Never serializes with a serializer of its own.** Everything round-trips through
  `store.Options.Serializer()`, so a document is written back with the settings Marten wrote it with.
- **Never adds a form.** There is no `<form>` and no `<EditForm>` anywhere in the studio's components;
  every mutation is a Blazor event over the circuit, and SignalR's own same-origin check is what stands
  between a cross-site page and that circuit. The studio's component endpoints therefore say
  `.DisableAntiforgery()`, so it imposes no middleware-ordering requirement on your application.

## What you have to configure

A checklist for a deployment that is not a demo:

1. **Authentication**, by your application. The studio adds none.
2. **`AuthorizationPolicy`, or `RequireAuthorization()` on the mapping.** The startup guard will not let
   you forget, but it will accept `AllowAnonymous()` — do not give it that answer.
3. **`StoreAuthorizationPolicy`**, if this process has more than one store, more than one database or
   more than one tenant and not everyone may see all of them.
4. **`WriteAuthorizationPolicy`**, if the people who may look are not exactly the people who may change.
5. **`Capabilities`**, one flag at a time, to the smallest set that makes the studio useful to the people
   who have it. `ReadOnly = true` in production and a separate read-write deployment is a legitimate
   shape.
6. **`SqlConsoleRole`**, if `RunSql` is on at all.
7. **`QueryTimeout`, `MaxPageSize`, `MaxSqlConsoleRows`, `ExactCountThreshold`** — the limits that keep
   an admin UI from becoming a denial of service against the database it exists to help you understand.
8. **`RebuildShardTimeout`**, if `RebuildProjections` is on. A rebuild drops the projection's tables
   *before* the timeout starts applying to the replay, so a timeout that is too short leaves the tables
   empty and the operation failed.
9. **Log retention for event ids 9200–9299, 9300–9329 and 9400–9519.** The Activity page is a 500-entry
   in-memory ring that dies with the process; your own `ILogger` is the copy that survives a deployment.
10. **Browser security headers** — CSP, HSTS, frame options — are your application's, as they are for
    every other page it serves. The studio sets none and would be wrong to. Since 0.5 its stylesheet and script are
    served under `{Path}/_content/PostgresStudio/`; a policy that names the old `_content/MartenStudio/` must follow.

In Postgres Studio, item 3 is `SourceAuthorizationPolicy`, item 8 does not apply, and the capabilities of item 5 are
its four reads.

## Audit events

Ids **9200–9299**, and from 0.5 **9300–9329** and **9400–9519**, are reserved for the two studios, which share one id
space: Postgres Studio logs each shared event under Marten Studio's id, level and properties, in its own words. The
audit events are these, and the numbers are fixed: an operator's log query is written against the number, and
renumbering one later would silently change what a saved query matches. The README lists the operational ones, which
say something went wrong.

Every value a visitor could shape - a typed `?store=` or `?db=`, a statement, a reason, a document id - reaches these
events only escaped (control and format characters, line and paragraph separators, a backslash doubled) and bounded (a
name at 256 characters, other text at 4,000, saying how long a cut value was), so it cannot write a log line of any
length or forge a second one. The one exception is 9204's executed statement, kept whole up to 32,768 characters,
the most one circuit message carries by default, and by its first and last 16,384 beyond that, so no padding can keep
a statement out of the record.

| Id | Event | Level |
|---|---|---|
| 9200 | `ActionPerformed` — user, action, target, store, database, tenant, outcome | Information |
| 9201 | `ActionFailed` — the same, with the reason | Information |
| 9202 | `CapabilityDenied` — the capability, and the option that would have allowed it | Warning |
| 9203 | `ScopeAuthorizationDenied` — the scope and the policy that refused it | Warning |
| 9204 | `SqlExecuted` — the statement, elapsed milliseconds, row count | Information |
| 9205 | `SqlRejected` — the statement and why it was refused | Information |
| 9206 | `SchemaChangeApplied` — the summary of what was applied | Information |
| 9207 | `ProjectionRebuildStarted` | Information |
| 9208 | `ProjectionRebuildFinished` — elapsed milliseconds and the outcome | Information |
| 9209 | `DaemonControlRequested` — the operation and its target | Information |
| 9210 | `StoreUnavailable` — a registered store that would not build, and why | Warning |
| 9211 | `DocumentWriteRoundTripDropped` — a save whose round trip dropped properties, counted and named | Warning |
| 9235 | `TableRowsRead` — the first read of a relation's rows per signature: the relation, the filter as typed and the sort | Information |
| 9236 | `TableRowOpened` — a row opened, a cell expanded or a key followed: the relation and the row's key | Information |
| 9245 | `SessionDetailsRead` — the first read of other sessions' details per circuit, store and database | Information |
| 9260 | `LinqQueryExecuted` — a LINQ mode query's first run in a circuit: type, scope, rows, the predicate and ordering | Information |
| 9261 | `LinqQueryRefused` — a LINQ mode refusal, naming its stage | Information |
| 9262 | `LinqQueryFailed` — a LINQ mode query Postgres failed, by SQLSTATE | Information |
| 9263 | `LinqQueryUnexpected` — a LINQ mode query the studio could not build | Information |
| 9452 | `StatementsRead` — Operations › Statements' first read in a circuit, per store and database: how many statements it showed | Information |

The Activity ring also records, under 9200 and 9201, the reads that reveal other people's details for the first time
in a circuit - "Read replication names", "Read Aurora session waits", the Health page's first evidence behind the
activity gate - and every "Measure bloat", every Statements drill-in plan ("Plan database statement") and every What
if? ("RunSql what-if", "ExplainQuery what-if", "LinqWhatIf", "DocumentsWhatIf" and "What-if database statement"). A
diagnostics API call is not audited (event 9250, at Debug, codes and counts only).

## Reporting a vulnerability

Open a private security advisory on the repository rather than a public issue.
