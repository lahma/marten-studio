# Every screen

What each page of each module shows, and what it needs to show it.

## Screens

Each module's pages are under a segment of their own below the mount: `/studio/marten/…`, `/studio/postgres/…` and
`/studio/kafka/…`. The shell has two of its own, the front page and Activity. A 404 is what an address that names none of
them gets: there are no aliases for another spelling. Below, the shell's screens come first, then the Marten module's, then
the Postgres module's (which a Marten host shows over the databases of its stores, and says so where the pages differ), and
then the Kafka module's.

### The shell

**Front page** (`/studio`) - one tile per module, in the order the host added them, each with the module's name, a sentence
and a link to its home; with one module there is nothing to choose, and the address opens that module's home instead. It is
authorized by `DataStudioOptions.AuthorizationPolicy` like every other page and reads nothing: a tile carries no count.

**Activity** (`/studio/activity`) - the last 500 actions taken through the studio in this process, from every module,
newest first, with user, scope, action, target, outcome, capability and message. It is in memory and goes away with the
process. The durable record is your own `ILogger`, under the studio's reserved event ids: 9200-9299, 9300-9329 and
9400-9519 for the database modules and 9600-9699 for the Kafka module.

### The Marten module

**Overview** — the selected store's card (with two or more stores, a line linking to Stores): databases, document
types, event types, the PostgreSQL version, tenancy, stream identity, event append mode and the schemas in play. A store
that will not build gets its own error region rather than blanking the page. A Health tile and the three worst findings
of the selected database sit beside the store card, and one line, "Connection: TLS verified · pool 100 · 2 things worth a
look", links to Database › Connection.

**Stores** (`/studio/marten/stores`, shown when you may see two or more stores) — every store you may see: whether it
builds and why not, its tenancy in words, its document types and event store, whether this process hosts its daemon, and
each database with a health chip from the last sweep. The page never starts a sweep by being opened; Check runs the one
a database's Overview would. A link to another store, database or tenant that you click moves the studio to it, and so
does Back or Forward to such an address when your own listings carry it; Back or Forward to an address that names only
part of the scope on screen rewrites the address to the scope on screen.

**Tenants** (`/studio/marten/tenants`, shown for a store with conjoined data) — the tenants you may address, 25 a page:
where each lives (its own database, its own partition, or shared tables), estimated documents, events and streams from
the catalog's statistics (never a `count(*)`), its last event where an index finds it, and its projection progress where
Marten keeps progress per tenant. Estimates and last events compare tenants, so they need `ViewDatabaseActivity`, as
T18 does.

**Documents** — a collections rail with per-collection counts (estimates, prefixed `~`; below
`ExactCountThreshold` the `=` beside a collection pays for an exact `count(*)`, and above it the studio
declines and says so on the badge), then a list with column chooser, keyset paging and a search
grammar (`field:value`, `>=`, `is:deleted`, `tenant:…`) that compiles to parameterised SQL against the
duplicated columns and JSON paths Marten actually created. Each search gets an index verdict —
green/amber/red with the `StoreOptions` line that would fix it — and a "Show SQL" disclosure with the
statement that ran. The detail view puts the JSON viewer (tree or raw, search, copy, download, "copy as
C# record") beside a metadata panel of the row's real columns. With `EditDocuments` you edit the JSON
in place and see a **round-trip diff** before saving: Marten has no untyped write path, so the edit is
deserialized into the CLR type and serialized back, and any property the type does not have is *gone* —
the dialog lists dropped, changed and added properties and refuses the save until you acknowledge the
drops. Saves are optimistically concurrent (`UpdateExpectedVersion` / `UpdateRevision`); a row that
moved comes back as a conflict with nothing written. A save whose JSON carries another id than the document's own is
refused, naming both, and the dialog also shows what Marten's own preview of the save
(`PreviewDocumentJsonAsync`) would keep, and holds a save Marten keeps differently or refuses. An **Order** control
lists a collection newest first by last modified, created or revision, where its table has the column. With
`DeleteDocuments` you get delete (soft where
the mapping says soft, hard otherwise), undelete, and a bulk delete over the selection. **Check shapes** asks
whether what is stored still fits the type this build has. It reads a sample of the collection in scope (all of it up
to 200 documents), loads each document as Marten would, writes it back in memory, and lists what does not fit:
documents that cannot be loaded, members the type no longer declares and the next save would drop, values that change
or come back empty, and members the stored documents never had. Each line has a count and a link to an example.
Nothing is written, and the result shows member names and counts, never values. On a conjoined-tenanted collection
with no tenant selected, only the lowest and highest keys are read, in the primary key's own order; choose a tenant
for a spread sample.

**Events** — *Streams* lists and filters streams; *stream detail* draws the timeline with each event's
JSON, headers and metadata, offers aggregate **time travel** (replayed on demand by Marten's own
aggregator over the events up to a version, writing nothing and running no projection; the studio's own
read of the events creates and migrates nothing, though an `Apply` method that loads data through the
session it is handed is the host's code and reads as the host wrote it), and a **stepper** that folds the stream one
event at a time with the same fold, showing each event, the aggregate after it and what it changed, where it threw,
and the dead letters that name it; and it archives the stream with `ArchiveStreams`. With `CompactStreams` it
**compacts** a long stream: Marten's own `CompactStreamAsync<T>`, on a session pinned to the stream's tenant and
database, folds the events up to
the version the dialog shows into one snapshot and deletes them, behind a typed confirmation that says so
(see [`compaction.md`](compaction.md)). *Streams* also finds a stream by its aggregate's
**natural key**, within the stream's own tenant. *Feed* is the global sequence, newest first, filtered by
event type, sequence range, stream, tenant, a **DCB tag** (`tag:<type>=<value>`, with its index verdict)
and the archived/skipped flags the store actually has, with a follow mode; each event's tags are chips
that link to that filter. *Event types* counts what the store has seen and lists the tag types with each
table's key; its **Check shapes** runs the documents' check for each event type through Marten's own event reader,
so it answers "would a rebuild throw?" before you start one: events no type can read, events that fail to deserialize
(with the sequence a rebuild would stop at, and whether `RebuildErrors` would skip them), and members your event types no
longer declare. Events an upcaster reads as a newer type are counted, not compared. Event types the store does not
register are never read through Marten's reader, which would register them on your store; they are reported from their
stored names alone. *Dead letters* lists the daemon's failures with the exception, and with `ManageDeadLetters`
discards a record, marks the underlying event as skipped — only on a store that enabled event skipping,
because it writes `mt_events.is_skipped` and a store without that column has no way to express the idea —
or rewinds the projection's subscription to the event that failed, behind a typed confirmation that states
what gets replayed, where the daemon is hosted in this process. A *Grouped by cause* view groups them by projection,
event type, exception type and message fingerprint, and opens a group as the list. *Event flow* draws which
projections and subscriptions read each event type and what each one writes, from `StoreOptions` alone: the event types
(by alias and .NET type); the projections that take them in, whether exactly, through a base type or interface they
declare, or as every event for one that declares none; and the document types, flat tables and live aggregates they
produce, along with the event types nothing reads. It filters to one projection or one event type, and the tables below
the picture list everything. Hidden document types, and the subclasses of a hidden root, are absent. Nothing on it reads
the database.

**Projections** — every registered projection with its shards, their state and progression against the
high-water mark, refreshed from a lazy observer on `ShardStateTracker` where the daemon is hosted in
this process and from polling where it is not. With the capabilities on: pause and resume the projection
daemon, restart the high-water agent (`ControlDaemon`), rebuild a projection behind a typed confirmation
(`RebuildProjections`), and advance or correct progression (`CorrectProgression`). Pausing is
process-wide — it stops the coordinator's leadership runner and then every agent of every database this
process hosts for that store — and it is the only stop that holds: with the coordinator running, it
restarts any agent it finds missing within `LeadershipPollingTime`, so per-agent Start and Stop are
offered only while the daemon is paused (or, under `MartenDaemonModeIsSolo()`'s coordinator, which has no leadership
loop, always), and each says when it would do nothing: an agent already running, no agent here to stop, or under
HotCold a shard another node leads, which it refuses, as it refuses a Rewind from such a node. The card says which
agents this node runs, and under what mode. Progression rows nothing registered advances are listed as orphaned
progression rows, with Marten's own removal call and the equivalent statement for the host and, under `CorrectProgression`, a
Remove button on each row the daemon does not still read. A rebuild
outlives the circuit that started it and is tracked by name. A composite projection's members, whose
progression rows its own shard advances in one batch, are listed under it and offer no Start or Stop of their
own. Where no daemon is hosted in this process,
the page says so — that is a value, not an error, and the studio never starts a daemon of its own. Each
shard also shows when it last progressed and a small chart of its position against the high water, drawn from the
screen's own samples, with a stalled colouring, a trend arrow and an estimated catch-up — and, while it is rebuilt,
when the rebuild should finish, measured from the rebuild's own samples; a rebuild in progress is a state on its row,
Pause, Stop and Rebuild quote the store's `StopAndDrainTimeout`, and Start is refused during a rebuild started outside
the studio, which the daemon says it is running.

**Schema** — five tabs: *Drift* (what Marten would change), *Tables*, *Indexes*, *Functions* and *DDL*.
*Tables* shows each table's TOAST size, dead and HOT percentages, last autovacuum and storage parameters
(with drift against the configuration), and names each pgvector column with its
dimensions; *Indexes* lists each index's keys, size and use from the catalogs alone - it locks nothing it lists, so it
answers while a migration holds a table - marks invalid, redundant and unused indexes from the health advisor's last
check, says what an HNSW or IVFFlat index's operator class and parameters mean, and reads an index's full statement when
you open "Show definition" on its row. Nothing here executes DDL on a
read path: schema names, declared indexes, managed tables and installed functions come from `StoreOptions`
and Marten's own feature schemas, and `Check`, `Preview` and `DDL` sit behind buttons that say on the
button what they may create. What those three show spans the whole database — every tenant's partition by
name, every document type's table — so they answer only a visitor the store policy allows for the database
with no tenant selected, and, while `IsDocumentTypeVisible` hides a document type, one past the database
browser's gate (`Capabilities.BrowseDatabase` and the write policy). An apply migrates the whole database,
every tenant, whichever tenant is selected, so both policies are asked about the database as a whole.
*Tables* says whose each table is by the database browser's rules, rolls partitions into their parent
without listing them, leaves hidden document types out, and shows a partition count or a Marten tenancy
table's row count only past that gate, because either is the number of tenants. *Functions* reads a body
when you open its row: Marten's own for everybody, the application's own only with
`Capabilities.BrowseDatabase` and a schema `BrowsableSchemas` admits. A name in a schema you may not see
reads `‹withheld›`, as in the browser. If a registered store cannot be built, the tabs keep what the other
stores declare and withhold, and say, what that store could own. The reads give up after three seconds
behind a migration's lock rather than wait for it. A known limit: *Tables* and *Indexes* list the store's
own schemas only, so a table handed to `StoreOptions.Storage.ExtendedSchemaObjects` in a schema of its own
(one no document type, the event store or a projection also uses) is not on either tab. The database
browser shows it once `Capabilities.BrowseDatabase` is on, the write policy passes and `BrowsableSchemas`
admits that schema. The tables-and-indexes health checks cover it, and show their findings about it only
to a visitor past that same gate.

> **An apply runs under `AutoCreate.CreateOrUpdate`, and `CreateOrUpdate` is not additive.** Weasel's
> update path emits `drop index` for every physical index your configuration does not declare,
> `drop column` for every extra column, `alter column … type` for a changed one,
> `alter table … drop constraint … CASCADE` for a changed primary key, and for a changed partition
> scheme a `create table … as select` / `drop table … cascade` pair that copies the whole table out and
> back. The preview is rendered under the same mode so what you read is what would run, and the
> destructive statements are listed in the dialog before a typed confirmation. **A hand-made index is
> dropped by the next apply** unless you declare it, or name it in
> `opts.Schema.For<T>().IgnoreIndex("…")` — or `opts.Events.IgnoreIndex("…")` on an event table, and
> `table.IgnoreIndex("…")` on a projection's or an `ExtendedSchemaObjects` table, which an apply migrates
> just the same — which takes it off both sides so it is neither created nor dropped. The Indexes and
> Drift tabs say this on screen, and name the index.

**Configuration** — everything your application told Marten, read back out: serializer, tenancy, schema
names, metadata columns, projections, the databases. Never a raw connection string and never a credential (Database ›
Connection lists a data source's settings one by one, with every password and every value the studio does not know
withheld); a database is identified by server, port, name and schema. The studio changes nothing here. A Runtime card
names the Marten, JasperFx, Weasel and Npgsql versions the host runs, the host's environment, and which of
the features newer Marten releases add this host has. It also lists the command timeout, sticky connections, Npgsql
logging, connection tracing, the hi-lo settings, the multi-host session preferences and whether row-level security is
configured (never its setting); [Database › Connection](connection.md) explains how each bears on the pool.

### The Postgres module

The Postgres module's pages are under `/studio/postgres/…`, the same pages in a Postgres host (over the
`NpgsqlDataSource`s it registered) and in a Marten host (over the databases of its stores). In a Marten host the Marten
module adds what only a Marten store has to them (the paragraphs below say where), and `/studio/postgres` itself redirects
to the Marten overview. The long form for a Postgres host is [postgres.md](postgres.md).

**Health** — the health advisor, at `/studio/postgres/health` (the long form is
[`health.md`](health.md), and every code, its variants and what its subject names are in
[`health/codes.md`](health/codes.md)): checks that read Marten's configuration and the database and say what
is wrong, how sure they are, and which `StoreOptions` line would change it — for a table the store declares a remedy
is never DDL, and one that needs a newer Marten says so. The table checks judge every table in the store's schemas,
and in the schemas `BrowsableSchemas` admits while `Capabilities.BrowseDatabase` is on, so a job store or a legacy
schema beside the store is judged too: its remedies are migration SQL, shown "for your own migrations - the studio
never runs this", and a recognised library's table is left out of the index-design rules. A finding about a table
outside the store's own schemas is shown only to a visitor the database browser's gate admits to that schema, and the
navigation badge never counts it. They are grouped by area: the daemon and the high-water mark (D),
events (E), tables and indexes (T), connections and sessions (C), replication (R), the server (S) and the
configuration (K). T20 says when a range-partitioned table is running out of partitions - a time range by its clock,
a `smallint`, `integer` or `bigint` range by where its rows stop - with the remedy each kind of range takes (for a
static Marten `ByRange()`, its `.AddRange(...)` line); T18 says when one tenant holds more than half of a large
conjoined table, and names it only to a visitor past the activity gate and that tenant's store policy; C12 names the
head of a blocking chain that sits idle in its transaction or has kept others waiting for minutes. Each finding
carries its evidence and when it was measured, Show SQL, a caveat such as "counters since `stats_reset` at 09:14", a
link to its section of the docs, and "mute in this browser". A
sweep runs only while a Health page or the Overview is open, or when the [opt-in health
check](setup.md#the-opt-in-health-check) is probed, at most once per `HealthInterval` per database however many
tabs watch, in the studio's read-only transaction, without DDL and without a lock beyond what a read
takes; **Run deeper checks** reads a bounded slice of the data, and **Run statement checks** reads
`pg_stat_statements` behind `ViewDatabaseActivity`. A database-wide number needs the store policy for the
database as a whole, and anything that names a session, a role, an address, a statement or a tenant needs
`ViewDatabaseActivity`. The navigation entry carries a severity badge, a toast says when a Critical
finding appears, and findings also show where they belong: D01 and D02 as Projections banners, index marks
on Schema › Indexes, E05 on Event types. A finding may carry an action that reads more when you press it, for you
alone and audited: E06's lists the 20 longest streams and the aggregate each belongs to. **Database › Sessions** (`/studio/postgres/database/sessions`) lists this
database's sessions, locks and prepared transactions under the same two gates, labels a lock by the
store's own table it belongs to, counts sessions by application beside the server's connection budget, and has no
cancel or terminate button. Its **Blocking** tab (`?view=blocking`) draws who holds up whom as trees - each chain's
head, then every session waiting on it - with the head's `pg_terminate_backend` (and `pg_cancel_backend`, when it runs
a statement) as text to copy for whoever may run it, which the studio never runs; on PostgreSQL 19 it also shows the
server's lock waits from `pg_stat_lock`. Its **Holders** tab (`?view=holders`) groups the whole server's client sessions - every
database on it, not only this one - by database, application, role and client address, against the connection budget C03
judges: the counts per database for anybody the store policy passes (a database named only where that policy admits them to
it, the rest together as "Other databases"), the applications, roles and addresses, and every database's name, behind the
activity gate. C13 (an application holding a tenth of the client limit idle while the server is 80% used) and C14 (connections
that set no `Application Name`, Info) are the checks over it. With `AddPostgresConnectionHistory()` Holders also says what
the server's connections peaked at before now, and when - each application's own peak behind the activity gate - from a
sample a minute that the host keeps where it likes ([diagnostics.md](diagnostics.md#connection-history)). The Health page's **Snapshot** menu views, copies or downloads what your
pages show of the database as JSON or as a Markdown report for a ticket, built from your own view of each page, and
compares an earlier snapshot file with now.

**Statements** (Operations, `/studio/postgres/statements`) — where the database's time goes, statement by statement, grouped
by Marten operation and document type, behind `ViewDatabaseActivity`. A statement's **Plan** link (`?queryid=`) opens
its drill-in: the text, the counters and its generic plan, planned as `SqlConsoleRole` with its plan insights and What
if?, and audited. A write, a locking read, a cut text or one withheld from you gets no plan and says why, and a plan
that reads a schema you may not see is not shown. **Index advisor** plans the 20 statements with the most execution
time that you may see, imagines each index their plans suggest with hypopg, one at a time, and ranks them by planned cost
saved times calls, with the `StoreOptions` line (Marten document tables) or `CREATE INDEX CONCURRENTLY` as the remedy; it
plans on a press, behind the page's own gate, creates nothing and applies nothing. Health's T01 and T15 cards offer it,
narrowed to the table, and a what-if of the foreign key's own lookup. **Mark** counts from now, in this tab: each statement's change
since the mark instead of the advisor's window, kept in this tab's connection only.

**Maintenance** (Operations, `/studio/postgres/maintenance`) — what autovacuum will do next and why: each table against the
triggers autovacuum itself uses, what is running now, the workers, the freeze forecast and the vacuum horizon, with an
on-demand bloat measure; **Mark** counts each table's vacuums and analyzes from now, in this tab; see
[`health/tables.md`](health/tables.md#the-maintenance-page).

**Storage** (Operations, `/studio/postgres/storage`) — how big the database is, how fast it grows and which tables hold the
space, from the catalog's own page counts (no size function over a table, nothing locked); the exact size of one table
is on its page in the database browser. **Mark** counts each table's growth and rows written from now, in
this tab.

**Replication** (Operations, `/studio/postgres/replication`) — slots, the standbys streaming from the server or a standby's own
replay, the archiver, WAL volume, checkpoints, I/O by backend type and tablespaces; names behind
`ViewDatabaseActivity`.

**Server** (System, `/studio/postgres/server`) — the server's version, platform and uptime, what the studio's reading role can
see grant by grant, a curated view of its settings, the extensions it uses and what stands in their way, and the
security posture.

> **Seeing replication lag.** On a primary, `pg_stat_replication` shows a standby's positions and lag only to a role
> with `pg_read_all_stats`, which `pg_monitor` includes. Any other role sees a sender's process id, role and application
> name. R03 and the Replication page then say "not checked", naming the grant and who can give it on your platform: a
> superuser when self-hosted, the master user on RDS and Aurora, the server admin on Azure, a `cloudsqlsuperuser` member
> on Cloud SQL. `pg_monitor` also lets the archiver's evidence count the WAL files waiting in `archive_status`, and the
> size of `pg_wal`. Without any grant, a data source that reaches a standby (a multi-host data source) measures lag
> there instead: what the standby has received less what it has replayed is exact and readable by any role, and so is
> whether replay is paused. A physical slot's `restart_lsn` is no substitute: it tracks flush, not replay.

**Database** — everything else in the store's Postgres database, for a host that keeps a Quartz.NET job
store, a legacy schema or another library's tables beside Marten. A schema rail, and kind tabs for
tables, views, functions, triggers, sequences and types; Marten's own objects are quieter, sorted last and
linked to where their data is really read, and a Quartz.NET, Wolverine, EF Core, Hangfire or Flyway object
is hinted by name. Each object has a detail page — columns, keys and indexes, foreign keys both ways,
triggers, a view's query, and its neighbourhood in the relationships diagram — and a table, view or
materialized view that is not Marten's has a **Rows** tab: a `col = value` / `col ~ text` / `col is:null`
filter with an index verdict (and "Run anyway" before an unindexed read of a large table), keyset paging
over composite keys, typed and sortable headers, JSON and `bytea` cells opening in the viewer, a "≈ date"
hint under `bigint` columns holding .NET ticks or epoch times, and row detail that follows foreign keys
both ways. The structure of the store's own schemas is shown to every visitor, as the Schema screen always
did; everything else — other schemas, definitions of what Marten does not own, and every row — needs
`Capabilities.BrowseDatabase`, a schema `BrowsableSchemas` names (or `"*"`), and the write policy asked
with `TenantId = null`, because nothing filters those tables by tenant. It is read-only, runs in the same
read-only transaction as the SQL console as `SqlConsoleRole`, locks nothing it lists, and audits the first
read of every relation. It never reads the rows of Marten's own document or event tables (they are read in
Documents and Events, where tenancy, soft delete and the serializer apply), never reads a foreign table,
directly or through a view, never shows a hidden document type or anything built on it, and masks the name
of a schema the visitor may not see as `‹withheld›` wherever Postgres prints it. A `vector`, `halfvec` or
`sparsevec` value is shown collapsed to its type and first components, with Expand and Copy.

**Database › Connection** (the Postgres module's page, shared by the Postgres and Marten modules; the long form is
[`connection.md`](connection.md)) shows how your application actually connects. A strip of facts gives the
server and database, the role, whether the connection is encrypted and whether the server's certificate is checked, the
pool, the Npgsql version loaded and any managed platform or pooler in front. "Worth a look" lists what the health advisor
found about the connection and what the studio would change - each with the setting it is about and the fix as a
connection string and as `NpgsqlDataSourceBuilder` code, ready to copy. Below that, every Npgsql setting with its value in
effect, where the value came from (the connection string, an environment variable such as `PGUSER`, or Npgsql's default)
and the default; by default only what differs from the default is shown. It then shows what the server saw of the studio's
own connection, and - with `ViewDatabaseActivity` - which applications this login has sessions for, how old the oldest is
and how many are on TLS. A password is only ever shown as set or not set; the connection string itself is never shown,
logged or exported. The Marten module adds "How Marten uses this connection": the command timeout Marten applies, whether the
data source is Marten's own or the application's, the store options that bear on connections and the connections the
hosted async daemon needs. The page reads and changes nothing. Under "Compare a connection string" you can paste another
Npgsql connection string - the one in a new configuration, one a colleague sent - to see which of its settings differ from
the connection in effect and what the page's advice would be if the application switched to it. Passwords are removed
before anything is read, an option's value is never shown, and the studio keeps, logs, audits and connects to nothing you
paste; it is read in this page only, so it is not part of the audit log. A password with a `;` in it must be quoted, or
the keyword that follows it is refused without a name. The advice is the page's own hints, judged against what the server
reported about the connection in effect; the health advisor's findings run on the application's real data source only.
What you paste travels to the server over SignalR like every other input in a Blazor Server page, so if your application
logs `Microsoft.AspNetCore.SignalR` at Debug level that log records it (typed SQL in the console included): keep that
category at Information or above in production.

**Relationships** — the document types of the store as a graph: one node per registered type with its
collection colour, its .NET type and a `reltuples` estimate, one arrow per foreign key. What
`StoreOptions` declares and what `pg_constraint` actually holds are cross-checked, so an arrow is
*declared and enforced*, *declared only* — configured, never applied, nothing enforcing it — or *in the
database only*, which Marten does not know about and an apply under `CreateOrUpdate` would drop. Keys
into the database's other tables are drawn too, as square, schema-coloured nodes — a Documents / Tables /
Both switch and schema chips narrow the picture, composite-key labels leave out the leading columns both
primary keys share, and a `NOT VALID` key is dashed — while a key into a schema the visitor may not see is
counted, never named, and a type hidden by `IsDocumentTypeVisible` appears nowhere at all. Clicking a node opens the collection; hovering an arrow
names the column, the target and the delete action. Beside the picture — and, on a narrow screen,
instead of it — a table carries the same relationships with the same links, which is also what a screen
reader gets. There is no pan, no zoom and no JavaScript: the layout is computed on the server and is a
pure function of the configuration, so the same store draws the same diagram every time. On a document's
detail page, **Referenced by** names each collection that points at it and how many of its documents do
— exact up to a thousand and "1,000+" beyond that, so the answer never costs a sequential scan — each row
linking to that collection already filtered on this document.

**Query** — three modes, and the asymmetry between the first two is the design. *Mode A, a Marten
`where` clause*, is a read against one document type and needs no capability, so the service holds it to
being a *clause*: no `;`, no statement of its own, brackets that balance, a tail that is
`[order by …] [limit n] [offset n]` and nothing else, no function that reads, writes or waits outside
the row, no `::regclass` cast — and, without `RunSql`, no subquery, union, join, `lateral` or
`returning` either. It runs inside the same `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK` as the
console, under the same `QueryTimeout` and the same optional `SqlConsoleRole`. Results come back as the
`data` column itself, never round-tripped through a CLR type, the composed statement is shown, and a plan
is offered where `RunSql` allows one (`EXPLAIN`, never `EXPLAIN ANALYZE`). *Mode B, the SQL console*, is arbitrary
read-only SQL against the whole database, is gated on `RunSql`, and is limited by `MaxSqlConsoleRows`,
`QueryTimeout`, a 3-second lock timeout, a 60-second idle-in-transaction timeout and an optional
`SqlConsoleRole` — inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK`. The row cap stops the reader;
it never appends a `LIMIT` to your statement. See [Security model and limits](limits.md#security-model-and-limits).
*LINQ mode* ([`linq-mode.md`](linq-mode.md)), gated on `RunLinqQueries`,
takes the predicate your code would write (`Status == "Open" && Total > 100`) and an ordering, parses them
with JasperFx's Dynamic LINQ under its allow-list and the studio's own, and applies them to Marten's own
`Query<T>()` on a session pinned to the scope, so tenancy, soft deletes and hierarchies are Marten's. It
shows the rows, the SQL Marten built, its plan (`EXPLAIN`, never `ANALYZE`) and index advice with the
`StoreOptions` line that would declare the index. It builds Marten's statement without `ToCommand`, so it
never creates a table, and it runs in the same read-only transaction as the console. It also queries the
event log: pick *Events* (or "Query these events with LINQ" on the Feed) to filter and order events by sequence, time,
type, stream, version, tenant and, where the store records them, correlation id, causation id and user name, through
Marten's own `IReadOnlyEventStore.QueryRawEvents()`; archived events are left out unless asked for.

The console's **Explain** and **Explain analyze** buttons draw a plan as a tree - each node's type and what it reads,
estimated and actual rows and loops, its share of the plan's time or cost, buffers and I/O - and so does an
`EXPLAIN (FORMAT JSON)` you type, and Mode A's plan. `EXPLAIN ANALYZE` runs only in the console, under `RunSql`, for a
statement its guard accepts and inside its read-only transaction: a write the guard misses is refused by Postgres
(`25006`) and changes nothing. **Query › Plan** is the same gate as a page of its own: write a statement - or paste the JSON of
a plan, which reads nothing - and get it planned, analyzed, or planned as a generic or custom plan of a statement with `$1`
placeholders, then drawn as a tree and as a sortable table of every node, with a summary strip, the critical path, heat
colouring, every key Postgres printed for a node and what each means, and Copy as Markdown / Download for
[speedscope](https://www.speedscope.app) beside the raw JSON; the console's **Open in Plan** link carries a statement there.
Under every plan, **plan insights** with stable codes P01-P20
([`health/plans.md`](health/plans.md)) point at a row estimate off tenfold, a sort or hash spilled to disk, a
lossy bitmap heap scan, a selective sequential scan, a nested loop over a large outer side, a large `OFFSET`, a
leading-wildcard `LIKE`, parallel workers short of the plan, an index-only scan fetching the heap, a filter discarding most
rows after an index, a hash built in batches, JIT costing more than it saves, planning slower than execution and a memoize
cache mostly missing; with the facts the page reads beside a plan it ran (each relation's estimate, analyze state,
indexes and extended statistics, and the statistics of the columns the plan uses), also stale statistics, correlated
columns, a function on an indexed column, missing partition pruning, a function's default row estimate and triggers
dominating the statement. Where the studio suggests an index - under a plan, or
in LINQ mode's and the Documents list's index advice - **What if?** plans the statement again with that one index
imagined by [hypopg](https://github.com/HypoPG/hypopg), and draws the plan now and the plan with the index side by side.
It creates nothing, and needs hypopg installed in the database, which is your decision; the studio never installs it.

### The Kafka module

The Kafka module's pages are under `/studio/kafka/…`: the clusters and topics the application's own Kafka credentials can
describe, and, behind capabilities that are off by default, search, compare, related records, export and a live tail. It
reads and never writes. [kafka.md](kafka.md) is its long form.

## The diagnostics API

`IPostgresDiagnostics`, registered by `AddMarten()` and `AddPostgres()` (and by `services.AddPostgresDiagnostics()`
alone, for a host with no UI), gives your own code what the
studio reads: the databases it would list, and for each one the findings, with stable codes and variants, and Storage,
Statements, Settings and Connections sections - for an e-mail, an admin page of your own or your own alerting, worded in
your own language and keyed on the codes. It answers host code, not a visitor, so no studio policy, capability or
`ReadOnly` is consulted; statement texts, application names, other sessions' evidence and findings about one tenant
come back only when the call sets `IncludeActivity`, which is off by default. The studio never serves a result: no
endpoint, no log line above Debug, no metric, no Activity entry. See [`diagnostics.md`](diagnostics.md).
