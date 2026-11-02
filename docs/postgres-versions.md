# PostgreSQL versions

Marten Studio and Postgres Studio are **tested on PostgreSQL 15 to 19**, and every release is tested on each of them -
19 on its release candidate until its general availability. **14 works but is past its end of life and untested from
1.1:** the studio keeps every read it makes on 14, and warns about its end of life, but no release is run against it any
more. The studio reads every server it is pointed at; the floor is what it promises, not what it refuses.

| Major | Status in 1.1 | Community end of life |
|---|---|---|
| 13 and older | **Below the floor.** The studio still runs, read by read, with no promise | 13: 2025-11-13 (passed) |
| 14 | **Works, untested from 1.1.** The studio's reads still run on it, and health check S01 says it is out of support from 2026-11-12. Plan the move to 15 | 2026-11-12 |
| 15 | Supported | 2027-11-11 |
| 16 | Supported | 2028-11-09 |
| 17 | Supported; the default of the live test suite | 2029-11-08 |
| 18 | Supported | 2030-11-14 |
| 19 | **Supported, tested on its release candidate** (`19rc1`) until its general availability (planned 2026-11-19), when the matrix moves to the released image | 2031-11-13 (planned) |

The dates are the community's ([versioning policy](https://www.postgresql.org/support/versioning/), read
2026-10-26): five years from a major's first release. Health check S01 raises the end of life of the server it is
pointed at a year ahead (Info), 90 days ahead (Warning) and from the day itself (Critical); on a managed platform it
says the provider's own schedule may differ ([S01](health/server.md#s01-postgresql-version-end-of-life)).

## How the studio is tested on each version

Every release runs the **version matrix**: one server per major - `postgres:15.19-alpine`, `16.15-alpine`,
`17.11-alpine`, `18.6-alpine` and `19rc1-alpine`, with `pg_stat_statements` preloaded and `track_io_timing` on - and
on each one, as the application's own role and as a role with no grant at all:

- a health sweep of the shared studio's checks - the regular ones and the statement checks; it has no deeper ones,
  and Marten Studio's own checks (the daemon, the event store, the document tables' Marten-only checks) are not part
  of the matrix - in which every check reads, is not applicable, or says why it was not checked, and none fails on a
  column, function or view that version does not have;
- every list and detail read of the database browser, the Sessions read, the Overview's server read and the
  relationship read;
- the lists and the regular sweep again while another session holds `ACCESS EXCLUSIVE` on a table and on a
  partition - what a migration's `ALTER` takes - under a 300 ms `lock_timeout`, so that no list queues behind a
  migration, and the index checks judge every other table and count the locked one as not judged;
- a fingerprint of every schema, extension and role before and after, which must be identical: no read creates
  anything;
- the studio's table of version gates against the server's own catalog.

PostgreSQL 19's release candidate (`postgres:19rc1-alpine`) is a leg like the others: a finding on it fails the run.
(The matrix keeps a *preview* leg for a major whose image is a beta - it reports what it finds and fails nothing - and
none is in it today.) A later patch swaps the candidate's tag for 19's general-availability image.

## What each version adds, and what the studio does without it

The studio reads a column or a view that only some versions have by name, so one statement runs on all of them, and
it gates what it shows on the version that has it. Below a gate, a screen or a check shows a row that says what it
needs - "needs PostgreSQL 16" - never an error.

**From 14, the floor**, the studio relies on: `pg_stat_statements_info` and `pg_stat_statements.toplevel`;
`pg_stat_activity.query_id`; the `pg_stat_wal` and `pg_stat_replication_slots` views;
`pg_get_wal_replay_pause_state()`; `pg_locks.waitstart`; the session counters of `pg_stat_database`;
`vacuum_failsafe_age`; the `pg_database_owner` and `pg_read_all_data` roles; and `reltuples = -1` for a table never
analysed. 13 has none of them, which is why 14 is the floor.

**On 15 you also get** `pg_stat_statements`' JIT columns, `pg_stat_force_next_flush()`, and statistics kept in shared
memory - written out at a clean shutdown and dropped after a crash, which from 15 leaves
`pg_stat_database.stats_reset` empty, so the studio tells a crash by the shared views' `stats_reset`, the server's
start time and a counter that went down.

**On 16 you also get** the `pg_stat_io` view; `last_seq_scan` and `last_idx_scan`, which date an index's last use;
`n_tup_newpage_upd`, which says why an update was not HOT; `EXPLAIN (GENERIC_PLAN)`; a replication slot's
`conflicting`; and `reserved_connections`.

**On 17 you also get** the `pg_stat_checkpointer` view; a slot's `inactive_since` and `invalidation_reason`; vacuum
progress per index; `pg_stat_statements`' renamed I/O-time columns (`shared_blk_read_time` and the rest); the
`pg_wait_events` view; the `pg_maintain` role; and EXPLAIN's JIT timings as an object.

**On 18 you also get** `pg_class.relallfrozen` and `autovacuum_vacuum_max_threshold`, which change when autovacuum
runs; `pg_stat_io`'s byte counters and its WAL rows (and `pg_stat_wal` loses its write and sync columns - the studio
reads the WAL writes and syncs from the WAL rows there, so they mean the same on every release);
`pg_stat_checkpointer.num_done`; NOT NULL constraints in `pg_constraint`; EXPLAIN's fractional row counts and its
buffers by default; `idle_replication_slot_timeout`; data checksums on by default for a new cluster; and the total
vacuum and analyse times per table. Two of 18's changes needed a fix in the studio itself:

- **NOT NULL constraints.** 18 stores each `NOT NULL` as a constraint. The database browser's Constraints tab leaves
  them to the Columns tab, which already says which columns are NOT NULL.
- **The `pg_sequences` view locks.** On 18 it takes a lock on every sequence it lists, for every reader, whatever
  columns are selected. The studio never reads it: its lists read `pg_catalog.pg_sequence`, and a sequence's value
  is read on demand, one at a time, after the role's privilege on it is checked - which on 18 is also what keeps the
  lock away.

**What the studio shows of 18, and where.** Each is absent below 18 - not a column of dashes, not an error - and reads the
catalog and the statistics views only, so nothing here locks what it lists:

- **Virtual generated columns** (`attgenerated = 'v'`): the database browser's Columns tab badges a generated column
  "stored" or "virtual" - before 18 every generated column is stored, and 17 refuses the `VIRTUAL` syntax outright.
- **Temporal keys** (`pg_constraint.conperiod`, one column for both): Keys & indexes badges a primary key or unique
  constraint declared `WITHOUT OVERLAPS`, and a foreign key declared with `PERIOD`, whose last column is spelled
  `PERIOD valid_at` as it was declared. Relationships draws a `PERIOD` foreign key like any other, with the word in its
  label and in the table's column lists.
- **`pg_stat_statements` 1.12**, which 18 ships: `parallel_workers_to_launch`, `parallel_workers_launched` and
  `wal_buffers_full` - and no other column (18.6's 1.12 has no generic or custom plan counts). The Statements page and its
  drill-in say, under a statement, how often it filled the WAL buffers and how many parallel workers its plans asked for
  and were given, since its counters began and only where there is something to say. They come from the extension's
  version, so 17's extension at 1.11 shows nothing.
- **Per-backend I/O** (`pg_stat_get_backend_io(pid)`): Database › Sessions, behind the same gate as the session rows' other
  columns (`ViewDatabaseActivity`), adds Reads, Writes and Cache hits for each session - the blocks and bytes it read and
  wrote, and the reads served from shared buffers - with its extends, evictions and time in the cell's tooltip. One
  statement per snapshot reads every session shown, never one a row. The function checks no role (a role in no monitoring
  role reads another's backend, as it reads `pg_stat_io`), so the gate is the studio's, not the server's.

**On 19 you also get** a `stats_reset` per table and per index, autovacuum's own score for each table
(`pg_stat_autovacuum_scores`), multixact usage (`pg_get_multixact_stats()`) and the `pg_stat_lock` view. The studio also
knows what else 19 changes: JIT is off by default (so the JIT-share check usually has nothing to judge),
`max_locks_per_transaction` defaults to 128 and the lock table is sized differently, the `BUFFERPIN` wait type is renamed
`BUFFER`, and `pg_stat_subscription_stats.sync_error_count` is renamed `sync_table_error_count`. Nothing the studio ships
names either renamed literal.

**What the studio reads on 19, and where.** Each is absent below 19 - not a column of dashes, not an error; a view or
function that only 19 has is never named in a statement sent to an older server - and reads the catalog and the statistics views only, so nothing here locks what it lists. Each
sits behind the gate its page or check already has: 19 adds no capability and no check.

- **A table's and an index's reset time** (`stats_reset` of `pg_stat_all_tables` and `pg_stat_all_indexes`; null where none
  is recorded). Measured on 19rc1, resetting one table with `pg_stat_reset_single_table_counters` also moves the database's
  own `stats_reset` to the same instant, and `pg_stat_reset()` stamps every table with the database's time. So on 19 any
  reset restarts the database window, and the seven-day guard of T01, T05 and T06 already covers it ("an index reset an hour
  ago is not unused"): no check judges an object over a window of its own. The matrix asserts the single-table behaviour on
  the 19 leg, so a change at 19's general availability is noticed.
  - **Storage** and **Maintenance** show a *Stats since* column where they show per-table counters: when that table's
    counters were last reset, which `pg_stat_reset()` sets for every table. A dash means no reset time is recorded for it.
- **Autovacuum's own score** (`pg_stat_autovacuum_scores`), for the tables the studio judges and no others: the largest of
  the parts autovacuum weighs (dead rows, inserts, changes since the last analyze, transaction-id age, multixact age), each
  a counter against its trigger, so 1 or more is a table autovacuum takes at its next look. **Maintenance › Next for
  autovacuum** lists the ten highest, with the parts, and what autovacuum would do (vacuum, analyse, or to prevent
  wraparound); **T07**'s evidence carries the table's score. A partitioned table shows the partition autovacuum would take
  first. The view lists every relation of the database whatever the reader may see, which is why the studio joins it to the
  judged tables' own oids and names nothing else.
- **Multixact usage** (`pg_get_multixact_stats()`): **T13**'s evidence adds how many multixacts and members the server holds
  and the space the members take. It is evidence only: no line, threshold or variant moves. PostgreSQL answers a role outside
  `pg_read_all_stats` (`pg_monitor` includes it) a row of nulls, and the evidence then says it is not shown to that role -
  never that there is none.
- **`pg_stat_lock`**: Database › Sessions › Blocking already carries *Lock waits by type* - waits, time waited, how often a
  backend's fast-path slots were full, and since when - on 19. **T19** adds the relation-lock row's fast-path figure to its
  evidence, in either variant, whenever a partitioned table is past one of its lines. The view is readable by every role.

The studio's table of version gates (`PostgresFeatures`) is held against each server's own catalog by the matrix, so a
column that 19's final release renames fails the 19 leg.

## Privileges, and "not checked"

Some reads need more than the role the studio reads as has. Such a row is never an error: it says **not checked**,
for which role, and who can grant what it lacks on the platform that runs the server.

- A check that knows the grant it needs names it - for example "Not checked: this needs pg_read_all_stats (pg_monitor
  includes it) for role app_reader. On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can grant
  it."
- A check Postgres refused (`42501`) without the check knowing why names the role refused - `SqlConsoleRole`, or the
  store's own login for the statement checks, which read as it - and leaves what was refused to Postgres' own words,
  shown below: a table, schema or sequence is its owner's to grant (`SELECT` or `USAGE`); a statistics view or function
  is covered by `pg_monitor`.

A role holds a monitoring role only when it has its privileges: a `NOINHERIT` member, or one granted it `WITH INHERIT
FALSE` (16+), does not, because the studio never runs `SET ROLE`. The studio never suggests a `GRANT` statement to
paste, and never runs one. A column's statistics (`pg_stats`) need `SELECT` on that column, which only the table's
owner can grant; `pg_monitor` does not give it. `pg_stat_statements`, `pgstattuple` and `hypopg` are not trusted
extensions, so creating one needs a superuser, or the platform's administrator.
