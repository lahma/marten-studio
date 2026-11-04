# The health advisor

This is the full text of D29 (see also hard rule 7's codes paragraph in hard-rules.md and D31 in architecture.md), moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D29

**D29 — The health advisor: one shared sweep, three gates, and two deliberate exceptions.** A sweep is the database's,
once per interval under no visitor's identity; what a visitor sees of it is decided per finding and per piece of
evidence (`HealthService`). `None` is for what the Configuration page already says (K01–K04, S01): anyone the scope
admits. `Database`, every other check's default, is for whole-database numbers: the store policy asked with
`TenantId = null` about the store's registered key and the resolved database's own `Id.Identity` (never the visitor's
spelling, another case or an empty id), so gates, audit, mute keys and the high-water Explain name one resource.
`Activity` is for other people's sessions and statements (process ids, roles, client addresses, statement text):
`ViewDatabaseActivity` (off by default and under `ReadOnly`), then that store policy, then the write policy with the
capability named, as Database › Sessions asks. `Database` covers the role the studio reads as (`SqlConsoleRole`, else
the store's login), which a "not checked" row names with its missing grant to a visitor the check's own gate admits; the
login the data source connects as (named by S04, S05, S08 and S10, and by C02 and C03 as `session_user` in the
role-limit evidence and the quiet row); and an Aurora instance's identifier (`aurora_db_instance_identifier()`,
`aurora_replica_status().server_id`), the cluster's name for an instance, not a session's, role's or client's. So R03's
`aurora-reader` evidence, the Replication page's Aurora replicas, the "Answered by" line, the Server page's Aurora
section (instance waits included) and the capacity line need no `ViewDatabaseActivity`; a session's Aurora waits
(`aurora_stat_backend_waits`) are `Activity`, read only through that gate and shown as counts and shares, never as a
time. A finding about one tenant names it, so it also needs `Activity` and that tenant's store policy: T18's, whose
tenant partitions are summed in SQL and never listed (`ColumnSkewQueries`), is one. A withheld
finding is counted, never described; a row below its check's gate reads "findings not shown to you" whether or not there
are any; the badge and banners read only a scope the report would be shown to. A finding naming a table outside the
store's schemas (`HealthFinding.GatedSchemas`, as for an `ExtendedSchemaObjects` table) also needs D27's gate for that
schema; the badge, banners and Critical toast, which resolve no scope, withhold such a finding from everybody, and T12
and T13, being the database's, name such a table generically.
The first exception is to D13 and D27, which call `SqlConsoleRole` the real boundary: T02, T22 and T23 read
`pg_stat_statements`' counters and texts as the store's own role, as the extension shows a narrowed console role
`<insufficient privilege>` for Marten's statements. The reads are fenced to `userid = current_user` and this database
(`StatementChecksFenceTests`), so they show what that role could read anyway; a text still carrying a literal as
written, or not a plain query or write, is withheld; plans run as `SqlConsoleRole`. The Statements page (W5-Q1) widens
the same read to the hundred statements with the most execution time (on Aurora, storage reads), under the same fence,
withholding and comment masking at read, every text behind the Activity gate and the visitor's own mask. Every other
health read and console statement stays under `SqlConsoleRole`. The second exception, the opt-in ASP.NET Core health
check, runs with no visitor (every store, up to 25 databases, no policy), acceptable only because it reports codes,
outcomes, severities and counts, never a name, a tenant, a statement or anything Postgres said; the host's own endpoint
is its only gate (`docs/health.md`, `docs/security.md`). A mute lives in the browser as an HMAC-SHA256 under the
browser's own random key, held only as the host's Data Protection protected it (purpose
`PostgresStudio.Health.MuteKeys.v1`, `HealthMuteKeys`), never as a plain hash a guess could confirm; an instance whose
key ring cannot open it reads nothing as muted and leaves the list to the instance that made it until the browser next
mutes there. The studio registers no Data Protection: `AddRazorComponents()` and `AddInteractiveServerComponents()` do,
and a container without it keys in memory.
**Whole-database table scope.** MartenStudio's table checks judge every table of the store's own schemas and, while
`BrowseDatabase` is on, of those `BrowsableSchemas` admits: the store's declarations over one catalog list
(`MartenHealthTableSource`, issue #8 G1). A declared table keeps its kind, origins and `StoreOptions` remedies (Marten's
event and bookkeeping tables are the store's own); an undeclared one is the host's (migration SQL), a recognised
library's (exempt only from the index-design rules) or somebody else's; one outside the store's schemas carries
`GatedSchemas`. Left out, and counted in the coverage sentence: other registered stores' tables, as a visitor-less sweep
cannot ask their policies, and, while any type may be hidden, tables named like a document table.
**Two studios, one engine.** MartenStudio's checks run only on Marten sources (the registry's source kinds), the shared
checks on both. A relational table's remedy may be migration SQL, shown "for your own migrations - the studio never runs
this" and built only in `MigrationSqlText`, which quotes every name and gives no statement for a name it cannot; a
Marten table's remedy is never DDL: a `StoreOptions` line in Marten Studio, nothing in Postgres Studio.
**The statistics window** (`StatisticsWindow`), the one answer to "since when have these counters counted", is
`max(database, min(shared, archiver))` over each view's `stats_reset` (`pg_stat_database.stats_reset`; the shared view,
`pg_stat_bgwriter` up to 16 or `pg_stat_checkpointer` from 17; `pg_stat_archiver`), so one view's
`pg_stat_reset_shared()` never shortens it. Restarts and failovers: decision-notes.md, D29. A verdict needing a long
window (T01's, T05's and T06's seven days, C08's and C11's day) gives none until the window covers it, nor while it
looks reset.
**The Maintenance page reads nothing of its own** but the on-demand bloat measure; its facts are the sweep's shared
reads (`HealthSweep.Shared`: the table read with each table's autovacuum triggers, T16's workers and progress reads, T12
and T13's wraparound read, T14's result). The bloat measure runs as `SqlConsoleRole` in a read-only transaction under
`lock_timeout` and `QueryTimeout`, one per database at a time, never in a sweep or a list, and is audited as "Measure
bloat" with the table only. The triggers T07, T08 and the page judge by are computed in SQL
(`HealthTableQueries.TablesSql`), mirrored in C# by `Autovacuum.Triggers` and held equal by `TriggerParityLiveTests`: no
rule of thumb that ignores a table's storage parameters is used.
**The lock table is read once per regular sweep** (X3): `HealthConnectionQueries.LockTable`, memoized as
`connections.locks` (`LockTableReads`), feeds C04, C06, C12, T14 and, as `oid[]` parameters, the table and index
reads; only the index read's `55P03` retry reads it again (`LockTableReadLiveTests` counts), and `HealthTableQueries`
reads no activity view (`ActivityViewsOffThePollPathTests`, which no longer exempts it). C12 names a chain's head, so
it is an Activity check; its terminate and cancel text is built in `SessionHandOver` and never run.
**A check whose platform does what it judges** throws `HealthCheckNotApplicableException` with `ReplicationPlatform`'s
sentence: listed as not applicable, never run, nothing logged (issue #8, A2), an expected state like a missing extension
or privilege, never a Warning. The platform is recognised only from markers a role short of a superuser cannot plant.

**Accepted checks** (`AcceptedChecks`, issue #35): a host accepts a check by code with a reason (at most 500 characters,
validated at startup against that studio's own registry by `AcceptedChecksValidator`, naming only the key). The sweep
stamps the check's findings `Accepted`, so every reader sees the same mark; it never drops one. An accepted finding is
shown on its check's row, never a card, and is out of every count and state: the Overview tile, the badge, the toast, the
banners (all through `HealthReportFinding.Counts` and the one list `CachedAsync` builds), and the export's `Degraded` and
`Unhealthy` (counted apart under `accepted`). It changes no gate: a withheld accepted finding is still counted as
withheld, and its reason is shown on the check's row only beside a finding the visitor may see. The *Accepted checks*
list (the Server page, Marten Studio's Configuration page) is configuration, shown to anyone who may open that page.

**Actions on a finding** (1.2). A finding may offer an on-demand action (`IHealthFindingAction`, internal; MartenStudio
registers E06's "Find the 20 longest streams"), which `HealthActionService` runs for the visitor who pressed it and never
in a sweep. Each press asks the finding's own gate (and its tenant's store policy) again, through the one
`HealthVisitorGate` the report asks, before anything is read, then whatever else the action asks: E06's asks the
`Activity` gate when it would list a conjoined store's streams across tenants, and is filed under `ViewDatabaseActivity`
then. One run at a time in a tab, and one per database and action across every visitor (`HealthActionFlights`: a second
visitor is refused, never joined, because the two visitors' gates differ). The action reads in the studio's read-only
session as `SqlConsoleRole`, under `QueryTimeout`. Every press past the "already running in this tab" and "no such action"
checks is one Activity entry named by the action (`AuditAction`, "Find the longest streams"), with a count and never a
value it read; a failure is logged as 9223 (Warning, one line per press), a refusal as 9224 (Debug). The runner holds no
result, so nothing reaches another circuit and nothing is cached, and no result is in the sweep, the snapshot, the
health-check export or the diagnostics API. Hidden document types are never named (`StreamAggregateMap.ShownAliases`).

**The connection-settings checks** (1.1: C16, C17, C18, S11, S12, S13) judge one memoized `ConnectionFacts` per sweep
(`ConnectionFacts.SharedAsync`: the redacted settings, what the server saw of the studio's connection as a new session
of the login starts with it, the platform, the session statistics when pooling is off, and the count of this process's
pools to the database). All six are `Database` checks, none shows a value of a secret keyword, a path, a host, a pool
name or an `Options` value (`ConnectionChecksSentinelTests`, `ConnectionSettingChecksLiveTests`), and a remedy shows the
connection-string form and the `NpgsqlDataSourceBuilder` form through `StudioText.DataSourceBuilderRemedy`, which Marten
Studio wraps in `opts.ConfigureNpgsqlDataSourceBuilder`. A stand-down that is the data source's design (pooling off for
C16, a pooler for C17, the Cloud SQL Auth Proxy or an unreadable TLS row for S11) is
`HealthCheckNotApplicableException`; an unread server is `HealthCheckUnavailableException`. The six rethrow the
connection read's failure (`ConnectionFacts.ObservedFailure`, captured once, thrown by
`ConnectionCheckSupport.ReadAsync`) when the server never answered or refused the login, so `Reached` stays meaningful:
a database the studio cannot reach is not "reached" by checks that judge settings, while a stand-down the connection
string alone decides still wins. S05 and S11 are also not applicable behind a pooler sign (a Neon pooled endpoint,
Supabase's port 6543, any port 6432 (PgBouncer, Azure's built-in included) or an Amazon RDS Proxy): the server's
`pg_stat_ssl` row is then the pooler's own connection, not the application's. A port alone says only that the data
source *may* reach a pooler, and the stand-down reason says so (`PoolerSigns.TlsStandDownReason`). Two settings override
the stand-down, because the setting alone settles the application's own leg: S05 fires behind a pooler sign when `SSL
Mode=Disable` and a host is remote (plaintext for certain, to the server or to a pooler in front of it), and S11 fires
its `require` variant when `SSL Mode=Require` (encrypted, never verified); both are `JudgedFromSslMode`, take no server
row, and say so in their evidence. `Prefer` and `Allow` behind a pooler stay not applicable. The from-setting findings
rethrow the connection read's failure too, so a server that never answered is Failed and never a finding. S05 reads the
signs from the same `ConnectionFacts`, so it adds no statement, and S11 asks for the pooler before it asks whether the
server could be read, so a pooler data source whose connection could not be read is not applicable rather than
unavailable. C16 skips its server variant behind an Amazon RDS Proxy with `No Reset On Close=true`, where the server's
cut reaches the client only on a pinned connection. K10 was retired in 1.1 with the Marten 9.57 floor: its row stays in
`docs/health/codes.md`, and an `AcceptedChecks` entry for it is ignored with one Information line.

**Database › Connection, the page's gates** (1.1, D33). The page and the Overview's line are the `Database` gate (the
source policy asked with `TenantId = null`); "This application's sessions" and the pool peak counted by application name
are the `Activity` gate (`ViewDatabaseActivity`, then the source and write policies), audited by signature like Sessions'
details and refused as a value. The pool peak and the sessions are recorded separately, and the Overview's line asks for
neither. The page itself is a read of configuration and is not audited. Its model holds only redacted records: no
connection string reaches a page, a contributor (`IConnectionInsightContributor`, internal, the same pattern as D32's
`IPlanInsightContributor`; a contribution may carry `HintNotes`, a sentence per hint id that the page appends to that
hint's card), the snapshot, a log or the audit. The snapshot's `connection` section is internal to the studio's own
download and is not part of the D31 read model. **Paste-to-compare** ("Compare a connection string") is the page's
`Database` gate, asked before the text is looked at, and is not audited: it reads and runs nothing. The pasted text is a
parameter, never a field: `PastedConnectionString` removes every catalogue `Secret` keyword (resolved through Npgsql's own
canonicalisation, never an alias list) and every `Options` value before `NpgsqlSettingsReader` reads it, re-checks the
result, and refuses a keyword by name only; `ConnectionCompareService` has no logger and no audit call, and nothing is
connected to. The only server facts it uses are the connection in effect's own cached ones. **Marten Studio's contributor**
(`MartenConnectionInsightContributor`, internal) adds a section and hint notes to the same page behind the same `Database`
gate. It reads only in-memory options, `IMartenDatabase.ConfiguredCommandTimeout` (a number parsed inside Marten), the
regular sweep's own daemon lookup and `MartenRowSizeCache`, and learns whether Marten built its data source from Weasel's
public `OwnsDataSource`, reading the container's `NpgsqlDataSource` registrations only as service descriptors (an instance
registered as an instance is compared by reference; nothing is ever resolved, so no data source is built). It opens no
session, calls nothing that ensures storage, and is handed no connection string. `MartenRowSizeCache` holds one average
size per kind (events, documents) per database, written by E04 and T09, bounded by `HealthDatabaseBound` and honoured for as
long as a deeper sweep's results are shown.
