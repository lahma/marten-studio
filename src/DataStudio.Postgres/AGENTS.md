# AGENTS.md: the Postgres module and its engine

The [root AGENTS.md](../../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the Postgres module and its engine. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** SQL builders, catalog reads, the SQL console, the database browser: [sql-and-catalog.md](../../docs/maintainers/sql-and-catalog.md) (D13, D27); health checks, the sweep and the codes table: [health-engine.md](../../docs/maintainers/health-engine.md) (D29); the diagnostics API and the Connection page: [architecture.md](../../docs/maintainers/architecture.md#d31) (D31, D33); plans and plan insights: [plan-analyzer.md](../../docs/maintainers/plan-analyzer.md) (D32); authorization and the audit: [authorization.md](../../docs/maintainers/authorization.md) (D4, D5); [hard-rules.md](../../docs/maintainers/hard-rules.md) rules 4, 5, 7 and 16.

## Hard rules

4. **No raw SQL outside the three builders' folders, `src/DataStudio.Postgres.Diagnostics/Internal/Sql/`,
   `src/DataStudio.Postgres/Internal/Sql/` and `src/DataStudio.Marten/Internal/Sql/`.** Identifiers are quoted through the builder,
   values are always parameters. The engine's builders, `SqlIdentifier` and `SqlLexer` among them, live in the first; the
   Postgres module's own (the console's plans, the browser's rows) in the second; the statements only a Marten store has
   (the document, event, daemon and projection reads) in the third, which calls the first's quoting. The compiled fence in
   `HealthQueryConventionTests` exempts each assembly's builders' namespace by name, and each assembly **only in its own
   folder** (the first two share a namespace). Full text: hard rule 4.

## Design decisions

**D8 — Counts are `pg_class.reltuples` estimates by default, prefixed `~`.** An exact `count(*)` on a 10-million-row
collection is a sequential scan, and a dashboard that does one per collection per refresh is a denial of service against
the database it is supposed to help you understand. Exact counts are available on demand, under `statement_timeout`.

**D13 — The SQL console is off by default, and its real guard is the transaction.** `RunSql` is a capability like any
other. What makes it safe is `SET TRANSACTION READ ONLY` plus `statement_timeout` and an optional `SET LOCAL ROLE`, not
the statement parser. Grant it to people you would give `psql` to. The console describes a statement (Parse and
Describe, never Bind or Execute) inside the same read-only transaction before running it, so that columns Npgsql has no
handler for - pgvector's three types among them - are requested as text; this changes neither the guard nor the audit,
and it does not happen on a connection that auto-prepares (`Max Auto Prepare`), where the console runs as before. Full
text: [sql-and-catalog.md](../../docs/maintainers/sql-and-catalog.md#d13).

**D27 — The database browser: one capability, one schema filter, and Marten's tables are never read raw.** It lists the
database's other objects by kind, gated like `RunSql`: `Capabilities.BrowseDatabase` (off by default and under
`ReadOnly`) and `BrowsableSchemas`; every non-Marten definition and every row needs both, plus the write policy asked
**with `TenantId = null`**. `Capabilities.ViewDefinitions` is the definitions' second way in, authorized the same way
with its own name, and never reads a row. Marten's document and event tables and `mt_` objects are never read raw, and a
hidden document type is absent from every list. `BrowsableSchemas` limits the screens, not `RunSql`: `SqlConsoleRole` is
the real boundary. Every list reads `pg_catalog` and locks nothing it lists. **Reach, not scope:** an operation Marten
runs over a whole database is authorized as `(store, database, null)` unless the database is exclusively the selected
tenant's, never on a sharded tenancy (removing an orphaned progression row always asks `(store, database, null)`);
the SQL console, the Mode A subquery lift and `EXPLAIN` are always asked with `TenantId = null`. Full text, with *D27 in
the Postgres module*:
[sql-and-catalog.md](../../docs/maintainers/sql-and-catalog.md#d27).

**D29 — The health advisor: one shared sweep, three gates, and two deliberate exceptions.** A sweep is the database's,
once per interval under no visitor's identity; what a visitor sees of it is decided per finding and per piece of
evidence by three gates: `None` (what the Configuration page already says), `Database` (the store policy asked with
`TenantId = null` about the store's registered key and the resolved database's own `Id.Identity`) and `Activity`
(`ViewDatabaseActivity`, then that store policy, then the write policy with the capability named). A withheld finding is
counted, never described. The two deliberate exceptions: T02, T22 and T23 (and the Statements page) read
`pg_stat_statements` as the store's own role, fenced to `userid = current_user` and this database, while every other
health read and console statement stays under `SqlConsoleRole`; and the opt-in ASP.NET Core health check runs with no
visitor and reports only codes, outcomes, severities and counts, never a name, a tenant, a statement or anything
Postgres said. An accepted check (`AcceptedChecks`, #35) still runs and is shown on its check's row, out of every count
and state; acceptance changes no gate. **Database › Connection (D33) is the `Database` gate**, as is its
paste-to-compare; only its sessions part and the pool peak counted by application name are `Activity`, recorded
separately, and the Overview's line asks for neither. A finding may offer an on-demand action
(`IHealthFindingAction`, internal): one visitor's, under at least the finding's gate, audited, and kept nowhere; E06's
reads `mt_streams` as `SqlConsoleRole`. Full text: [health-engine.md](../../docs/maintainers/health-engine.md#d29).

**D31 — The diagnostics API: what the studio found, for the host's own code, by stable code, and never served by the
studio.** `IPostgresDiagnostics` (registered by `AddPostgresDiagnostics()`, or by `AddPostgres()` and `AddMarten()`) returns what the engine
found, for the host's own code, by stable code; there are no mutes without a UI, and its studio paths are mount-relative
strings (`postgres/health`) that mean nothing where no studio is mounted; results are
extend-only records, and activity-grade data is returned only when the call sets `IncludeActivity = true`. The studio
never serves it: nothing in any package puts an API result into an HTTP response, a log line above Debug, a metric,
the Activity ring or the health check's data, and no endpoint maps one; the Health page's Snapshot download, its
Markdown report and its compare are built from the visitor's own gated view, never from this API, and an API call is not
audited. A code is a letter and two digits, never reused, renamed or moved to another area, and a variant is frozen
within its code. Full text: [architecture.md](../../docs/maintainers/architecture.md#d31).

**D32 — The studio draws every plan, plan insights judge it, and EXPLAIN ANALYZE is the console's alone.** Every plan is
drawn through `QueryPlanReader` and `PlanView`; plan insights are pure evaluators with stable `P` codes in `codes.md`,
never a health check; `EXPLAIN ANALYZE` runs only under `RunSql`, for a statement the guard accepts, in the read-only
transaction; a plan reading a withheld schema is not shown. The facts Query › Plan reads beside a plan it ran
pass each relation's schema and every ancestor's, then the row gate for values, which are never a Marten table's. The Marten
module's own insights (P26 to P28) come through an internal seam, `IPlanInsightContributor`, that the Postgres module runs
and the Marten module registers: pure evaluators over (plan, facts, the tables the store declares from `StoreOptions`, never a
storage-ensuring call), for visible document types only, whose remedies are `StoreOptions` lines. Full text:
[plan-analyzer.md](../../docs/maintainers/plan-analyzer.md).

**D33 — The Connection page shows a data source's settings one by one, never its connection string.** A catalogue of the
loaded Npgsql's keywords decides what is shown. A `Secret` keyword shows only "set" or "not set"; an unknown keyword's
value is withheld; `PG*` environment values are shown on the same terms as their keyword, except `PGPASSWORD`; an
`Options` value is shown only for real server settings; paths are shown and files are never read. The raw string and the
meter's pool names never leave the reader, so no page, contributor, snapshot, log or audit entry can hold one, and the
sentinel tests hold all of this. The gate is D29's `Database` (the sessions part is `Activity`), and paste-to-compare
removes every secret before anything is read and keeps, logs and audits nothing. Full text:
[architecture.md](../../docs/maintainers/architecture.md#d33).
