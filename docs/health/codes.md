# Health check codes

Every check the health advisor runs has a **code**: a letter for its area and two digits, such as `T03`. A finding
carries its check's code, and where one check finds more than one kind of thing, a **variant** too (`deadlocks`,
`temp-files`). The Health page shows both, the mute keys are built from them, and the diagnostics API
([diagnostics.md](../diagnostics.md)) returns them as `PostgresFinding.Code` and `PostgresFinding.Variant`, so a host
can word a finding in its own language and key its own alerts on it.

This table is the list of every code there is. It is the contract, and the rules for it are these:

1. A code is never reused for another meaning, never renamed, and never moves area.
2. A retired check keeps its row here, marked with the release that retired it. Its code is never reassigned.
3. A check's thresholds and wording may change under its code. A change that would make a host's own wording wrong - a
   different kind of subject, a different meaning - gets a new code.
4. Variants follow the same three rules within their code.
5. A newer studio may return a code or a variant a host has no wording for. The host falls back to the finding's
   `Title`, which is always set.

A finding's `Key` is `Code`, then `|Variant` when the check has variants, then `|Subject` when the finding is about one
object (`T03|index|public.mt_doc_order_idx_total`). **Subject** below says what kind of object that is; `-` means a
finding about the database, the server or the configuration as a whole. **Studios** is `both` for a check Marten Studio
and Postgres Studio both run, and `Marten` for one that reads a Marten store and runs only in Marten Studio. **Since** is
the release that first shipped the code. Gaps in the numbering are codes no release has shipped.

`HealthCodesTests` holds this table to the registered checks: every registered code has its row with the same area,
variants and studios, every row is registered or retired, and the table's canonical rendering is a reviewed Verify
baseline, so editing or deleting a row is a reviewed change.

| Code | Title | Area | Variants | Subject | Studios | Since | Retired |
|---|---|---|---|---|---|---|---|
| [C01](connections.md#c01-connection-pool-saturated) | Connection pool saturated | Connections | - | - | both | 0.4 | - |
| [C02](connections.md#c02-connection-budget-across-databases) | Connection budget across databases | Connections | - | - | Marten | 0.4 | - |
| [C03](connections.md#c03-server-connection-headroom) | Server connection headroom | Connections | `server`, `role`, `database` | - | both | 0.4 | - |
| [C04](connections.md#c04-idle-in-transaction) | Idle in transaction | Connections | - | - | both | 0.4 | - |
| [C05](connections.md#c05-long-running-statement) | Long-running statement | Connections | - | - | both | 0.4 | - |
| [C06](connections.md#c06-lock-waits-and-blocking-chains) | Lock waits and blocking chains | Connections | - | - | both | 0.4 | - |
| [C07](connections.md#c07-orphaned-prepared-transaction) | Orphaned prepared transaction | Connections | - | - | both | 0.4 | - |
| [C08](connections.md#c08-deadlocks-rollbacks-and-temp-spills) | Deadlocks, rollbacks and temp spills | Connections | `deadlocks`, `rollbacks`, `temp-files` | - | both | 0.4 | - |
| [C09](connections.md#c09-async-shards-against-the-pool) | Async shards against the pool | Connections | - | - | Marten | 0.4 | - |
| [C10](connections.md#c10-hotcold-daemon-behind-a-transaction-pooler-proven) | HotCold daemon behind a transaction pooler, proven | Connections | - | - | Marten | 0.4 | - |
| [C11](connections.md#c11-low-cache-hit-ratio) | Low cache hit ratio | Connections | - | - | both | 0.5 | - |
| [C12](connections.md#c12-head-of-a-blocking-chain) | Head of a blocking chain | Connections | `idle-head`, `long-chain` | - | both | 0.6 | - |
| [D01](daemon.md#d01-high-water-mark-pinned-behind-a-gap) | High-water mark pinned behind a gap | Daemon | - | - | Marten | 0.4 | - |
| [D02](daemon.md#d02-async-projections-behind-and-nothing-runs-the-daemon) | Async projections behind, and nothing runs the daemon | Daemon | `no-daemon`, `lock-held-idle` | - | Marten | 0.4 | - |
| [D03](daemon.md#d03-shard-alive-but-not-moving) | Shard alive but not moving | Daemon | - | shard | Marten | 0.4 | - |
| [D04](daemon.md#d04-falling-behind-with-an-eta) | Falling behind, with an ETA | Daemon | - | shard | Marten | 0.4 | - |
| [D05](daemon.md#d05-shard-failed-or-paused) | Shard failed or paused | Daemon | - | shard | Marten | 0.4 | - |
| [D06](daemon.md#d06-dead-letters-accumulating) | Dead letters accumulating | Daemon | - | projection | Marten | 0.4 | - |
| [D07](daemon.md#d07-gaps-were-skipped) | Gaps were skipped | Daemon | - | - | Marten | 0.4 | - |
| [D08](daemon.md#d08-rebuilt-elsewhere-or-stopped-half-way) | Rebuilt elsewhere, or stopped half-way | Daemon | - | shard | Marten | 0.4 | - |
| [D09](daemon.md#d09-event-sequence-and-daemon-progression-disagree) | Event sequence and daemon progression disagree | Daemon | `mark-ahead`, `shard-ahead`, `sequence-behind` | `mark-ahead`: progression row; `shard-ahead`: shard; `sequence-behind`: sequence | Marten | 0.5 | - |
| [E02](events.md#e02-tombstones) | Tombstones | Events | - | - | Marten | 0.4 | - |
| [E03](events.md#e03-hot-stream) | Hot stream | Events | - | stream | Marten | 0.4 | - |
| [E04](events.md#e04-large-event-payloads) | Large event payloads | Events | - | - | Marten | 0.4 | - |
| [E05](events.md#e05-unregistered-event-types) | Unregistered event types | Events | - | - | Marten | 0.4 | - |
| [E06](events.md#e06-long-streams-without-a-snapshot) | Long streams without a snapshot | Events | - | - | Marten | 0.4 | - |
| [E07](events.md#e07-archived-share-without-partitioning) | Archived share without partitioning | Events | - | - | Marten | 0.4 | - |
| [E08](events.md#e08-dcb-tag-version-churn) | DCB tag-version churn | Events | - | - | Marten | 0.4 | - |
| [K01](configuration.md#k01-autocreate-in-production) | AutoCreate in Production | Configuration | - | - | Marten | 0.4 | - |
| [K02](configuration.md#k02-extended-progression-tracking) | Extended progression tracking | Configuration | - | - | Marten | 0.4 | - |
| [K03](configuration.md#k03-advanced-async-tracking) | Advanced async tracking | Configuration | - | - | Marten | 0.4 | - |
| [K04](configuration.md#k04-hotcold-daemon-behind-a-transaction-pooler) | HotCold daemon behind a transaction pooler | Configuration | - | - | Marten | 0.4 | - |
| [K05](configuration.md#k05-sql-console-without-a-narrowed-role) | SQL console without a narrowed role | Configuration | - | - | both | 0.5 | - |
| [K06](configuration.md#k06-database-browser-open-to-every-schema-without-a-narrowed-role) | Database browser open to every schema without a narrowed role | Configuration | - | - | both | 0.5 | - |
| [K07](configuration.md#k07-studio-open-to-anonymous-visitors-with-a-capability-on) | Studio open to anonymous visitors with a capability on | Configuration | - | - | both | 0.5 | - |
| [K08](configuration.md#k08-server-timeouts-end-the-daemons-leadership) | Server timeouts end the daemon's leadership | Configuration | `idle-in-transaction`, `transaction-timeout`, `idle-session` | setting | Marten | 0.5 | - |
| [P01](plans.md#p01-row-estimate-off-tenfold-or-more) | Row estimate off tenfold or more | Plans | - | plan node | both | 0.6 | - |
| [P02](plans.md#p02-sort-or-hash-spilled-to-disk) | Sort or hash spilled to disk | Plans | - | plan node | both | 0.6 | - |
| [P03](plans.md#p03-lossy-bitmap-heap-scan) | Lossy bitmap heap scan | Plans | - | plan node | both | 0.6 | - |
| [P04](plans.md#p04-sequential-scan-with-a-selective-filter) | Sequential scan with a selective filter | Plans | - | plan node | both | 0.6 | - |
| [P05](plans.md#p05-nested-loop-over-a-large-outer-side) | Nested loop over a large outer side | Plans | - | plan node | both | 0.6 | - |
| [P06](plans.md#p06-large-offset) | Large OFFSET | Plans | - | plan node | both | 0.6 | - |
| [P07](plans.md#p07-leading-wildcard-like) | Leading-wildcard LIKE | Plans | - | plan node | both | 0.6 | - |
| [P08](plans.md#p08-parallel-workers-short-of-the-plan) | Parallel workers short of the plan | Plans | - | plan node | both | 0.6 | - |
| [R01](replication.md#r01-replication-slot-retaining-wal) | Replication slot retaining WAL | Replication | `near-limit`, `inactive`, `lost` | - | both | 0.4 | - |
| [R02](replication.md#r02-wal-archiving-failing) | WAL archiving failing | Replication | `failing`, `backlog` | - | both | 0.5 | - |
| [R03](replication.md#r03-standby-lag) | Standby lag | Replication | `primary-view`, `standby-view`, `paused`, `aurora-reader` | - | both | 0.5 | - |
| [R04](replication.md#r04-requested-checkpoints) | Requested checkpoints | Replication | - | - | both | 0.5 | - |
| [S01](server.md#s01-postgresql-version-end-of-life) | PostgreSQL version end of life | Server | - | - | both | 0.4 | - |
| [S02](server.md#s02-settings-that-blind-or-endanger-the-store) | Settings that blind or endanger the store | Server | `setting`, `pending-restart` | `setting`: setting; `pending-restart`: - | both | 0.4 | - |
| [S03](server.md#s03-checksum-failures) | Checksum failures | Server | - | - | both | 0.4 | - |
| [S04](server.md#s04-connecting-as-a-superuser) | Connecting as a superuser | Server | `superuser`, `provider-admin`, `file-access` | - | both | 0.5 | - |
| [S05](server.md#s05-connection-without-tls) | Connection without TLS | Server | - | - | both | 0.5 | - |
| [S06](server.md#s06-anyone-may-create-in-an-application-schema) | Anyone may create in an application schema | Server | - | schema | both | 0.5 | - |
| [S07](server.md#s07-outdated-extension) | Outdated extension | Server | - | extension | both | 0.5 | - |
| [S08](server.md#s08-connecting-roles-password-expires) | Connecting role's password expires | Server | - | - | both | 0.5 | - |
| [S09](server.md#s09-planner-method-switched-off) | Planner method switched off | Server | - | setting | both | 0.5 | - |
| [S10](server.md#s10-store-connections-cannot-write) | Store connections cannot write | Server | `read-only`, `standby` | - | both | 0.5 | - |
| [T01](tables.md#t01-sequential-scans-on-a-large-table) | Sequential scans on a large table | Tables | - | table | both | 0.4 | - |
| [T02](tables.md#t02-expensive-statements) | Expensive statements | Tables | - | statement | both | 0.4 | - |
| [T03](tables.md#t03-invalid-index) | Invalid index | Tables | `index`, `partitions` | `index`: index; `partitions`: table | both | 0.4 | - |
| [T04](tables.md#t04-redundant-index) | Redundant index | Tables | - | index | both | 0.4 | - |
| [T05](tables.md#t05-unused-index-on-a-write-heavy-table) | Unused index on a write-heavy table | Tables | - | index | both | 0.4 | - |
| [T06](tables.md#t06-low-hot-update-ratio) | Low HOT-update ratio | Tables | - | table | both | 0.4 | - |
| [T07](tables.md#t07-vacuum-falling-behind) | Vacuum falling behind | Tables | `dead-rows`, `inserts` | table | both | 0.4 | - |
| [T08](tables.md#t08-never-analysed-or-stale-statistics) | Never analysed, or stale statistics | Tables | `stale`, `never-analysed`, `since-restart` | `stale`, `never-analysed`: table; `since-restart`: - | both | 0.4 | - |
| [T09](tables.md#t09-large-documents) | Large documents | Tables | - | table | Marten | 0.4 | - |
| [T10](tables.md#t10-soft-deletes-accumulating) | Soft deletes accumulating | Tables | - | table | Marten | 0.4 | - |
| [T11](tables.md#t11-numeric-id-headroom) | Numeric id headroom | Tables | - | document type | Marten | 0.4 | - |
| [T12](tables.md#t12-transaction-id-age) | Transaction-id age | Tables | - | - | both | 0.4 | - |
| [T13](tables.md#t13-multixact-age) | Multixact age | Tables | - | - | both | 0.4 | - |
| [T14](tables.md#t14-vacuum-held-back) | Vacuum held back | Tables | - | - | both | 0.4 | - |
| [T15](tables.md#t15-foreign-key-without-a-covering-index) | Foreign key without a covering index | Tables | `uncovered`, `partly-covered` | foreign key | both | 0.5 | - |
| [T16](tables.md#t16-autovacuum-workers-saturated) | Autovacuum workers saturated | Tables | - | - | both | 0.5 | - |
| [T17](tables.md#t17-orphaned-document-tables) | Orphaned document tables | Tables | - | - | Marten | 0.5 | - |
| [T18](tables.md#t18-tenant-skew) | Tenant skew | Tables | - | table | Marten | 0.6 | - |
| [T19](tables.md#t19-partitions-against-the-lock-table) | Partitions against the lock table | Tables | `per-transaction`, `capacity` | table | both | 0.5 | - |
| [T20](tables.md#t20-range-partitions-running-out) | Range partitions running out | Tables | `running-out`, `default-partition-rows` | table | both | 0.5 | - |
| [T22](tables.md#t22-optimistic-concurrency-conflicts) | Optimistic-concurrency conflicts | Tables | - | table | Marten | 0.4 | - |
| [T23](tables.md#t23-jit-compilation-share) | JIT compilation share | Tables | - | - | both | 0.4 | - |
| [T24](tables.md#t24-autovacuum-switched-off-for-a-table) | Autovacuum switched off for a table | Tables | - | table | both | 0.5 | - |
| [T25](tables.md#t25-bloated-index) | Bloated index | Tables | `issue-rule`, `key-width` | index | both | 0.5 | - |
| [T26](tables.md#t26-integer-key-running-out) | Integer key running out | Tables | `column`, `sequence` | column | both | 0.5 | - |

## Subjects

| Subject | What `Subject` holds |
|---|---|
| table | the table's schema-qualified name, `schema.table` |
| index | the index's schema-qualified name, `schema.index` |
| column | the table and column, `schema.table.column` |
| foreign key | the referencing table and the constraint, `schema.table.constraint` |
| setting | the server setting's name, as `pg_settings` spells it |
| schema | the schema's name |
| extension | the extension's name |
| statement | `pg_stat_statements`' query id, as text |
| shard | the async shard's name, `Projection:All` |
| projection | the projection's name |
| progression row | the event progression's high-water row |
| sequence | the event sequence's schema-qualified name |
| stream | the stream's id, prefixed with `tenant/` on a conjoined event store |
| document type | the document type's name |

A subject that names a tenant (E03's) or a statement (T02's) is Activity-grade: the Health page shows it only past the
Activity gate, and the diagnostics API only with `IncludeActivity` (see [diagnostics.md](../diagnostics.md)).
