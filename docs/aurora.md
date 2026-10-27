# Aurora

Amazon Aurora PostgreSQL-Compatible Edition is PostgreSQL with Aurora's own storage underneath: a cluster volume shared by
every instance, "copies of the data across three Availability Zones" (*Amazon Aurora storage*), readers that read that
volume instead of streaming WAL, and on Serverless v2 a compute capacity, in ACU, that scales while the server runs. Both
studios recognise an Aurora server and read it the way Aurora documents it (issue #8, A1-A8). This page says what the
studio reads there, what it marks as not applicable and why, how a host supplies the capacity SQL cannot see, and what the
studio does for each of issue #8's Aurora items in this release.

Nothing here needs an AWS SDK, an AWS credential or a setting: the studio reads Aurora's own SQL functions, and the
capacity is the host's to supply if it wants it. Every quotation is from the AWS page named beside it, read on 2026-10-26.

## How the studio knows it is Aurora

The platform read looks up `aurora_version()` in `pg_proc` - in `pg_catalog` only, where nobody short of a superuser may
create a function - and calls nothing. A `public.aurora_version()` a database owner made does not turn a server of
somebody's own into an Aurora cluster (`PlatformMarkerSpoofLiveTests`, `AuroraShimLiveTests`), because Aurora's platform
decides which checks stand down. AWS's page for `aurora_version` does not name the function's schema; Aurora's own
functions are built in, and the studio's test shim puts them in `pg_catalog` for the same reason.

Once the platform read has said Aurora, and only then, the studio calls Aurora's functions - each by its `pg_catalog`
name, so a function of the same name in another schema is never the one read:

| Function | What the studio reads | Where it is shown |
|---|---|---|
| `aurora_version()` | the Aurora version | the Health page, both Overviews, the Server page |
| `aurora_db_instance_identifier()` | which instance answered - from 13.7, 12.11, 11.16 and 10.21; before that the instance is "not named" | the same, and the statistics window |
| `aurora_replica_status()` | each instance, writer or reader, and a reader's `replica_lag_in_msec` | R03 `aurora-reader`, Replication › Aurora replicas, the Server page |
| `aurora_stat_system_waits()`, `aurora_stat_wait_event()`, `aurora_stat_wait_type()` | the instance's cumulative waits, named | the Server page's Aurora section |
| `aurora_stat_get_db_commit_latency(oid)` | this database's cumulative commit latency | the Server page |
| `aurora_stat_dml_activity(oid)` | this database's successful DML, counts and latency by kind | the Server page |
| `aurora_stat_backend_waits(pid)` | each session's waits since it connected - counts and shares only | the Server page, behind `ViewDatabaseActivity` |

**By column name, always.** AWS documents these functions' columns more loosely than their names:
`aurora_replica_status()`'s column set varies by release (the `aurora_db_instance_identifier` page's own example has no
`read_ios`, `iops` or `cpu`), `aurora_stat_wait_event()` is documented with a `type_name` its own example lacks, and
`aurora_stat_get_db_commit_latency` is a `SETOF record` in the text and a scalar in every example. So each row comes back
as `to_jsonb(row)` and is read by name: a column a release lacks is absent, never another column's value.

**Read-only, and locking nothing.** The functions read the instance's own counters and its replica status manager; the
studio's statements around them read `pg_database`, `pg_stat_database` and `pg_stat_activity`. Nothing opens a relation, so
nothing queues behind a migration, and nothing creates anything (`AuroraShimLiveTests`).

**Privileges.** No AWS page states the privilege these functions need. If one is refused (42501), that section says so on
the page, the rest is shown, and the log has it at Debug.

## Which instance answered (A1)

Every Aurora instance answers SQL on its own - the writer through the cluster endpoint, a reader through the reader
endpoint - and its statistics are its own. So the Health page's header, both Overviews and the Server page say which one
answered the advisor's last sweep: "Answered by instance db-1-instance-2 (a reader), Aurora 16.4.1, up since ...", from
`aurora_db_instance_identifier()`, `pg_is_in_recovery()`, `aurora_version()` and `pg_postmaster_start_time()`. An instance
identifier is the cluster's own name for one of its instances - not a session's, a role's or a client's - so it is shown to
a visitor the store policy passes for the database as a whole (D29's database-wide gate), never needing
`ViewDatabaseActivity`.

**A failover reads as one.** When another instance answers than at the advisor's last sweep - a failover promoted a
reader, a connection string now reaches another instance - the statistics window says the counters look reset, with the
reason "another instance answered; its counters started at ...", and the window starts at that instance's own reset
times. Every rate that needs a long window waits for it again, and the Health page says so once, in the sweep that first
sees it, rather than showing a quiet database whose counters just went backwards.

## What does not apply on Aurora (A2)

Aurora's storage layer does what some checks judge, and judging it there raises false alarms. Each such check is listed on
the Health page as **not applicable**, with the reason, and never run. Every shared check, reviewed against AWS's
documentation:

| Code | Check | On Aurora | Why |
|---|---|---|---|
| C01 | Connection pool saturated | applies | the process's own pool |
| C03 | Server connection headroom | applies, reworded | `max_connections` follows the instance's memory, and on Serverless v2 the maximum ACU (A6) |
| C04-C07, C12 | Idle in transaction, long statements, lock waits, prepared transactions, the head of a blocking chain | apply | sessions and locks are Postgres' own; C12's `idle_in_transaction_session_timeout` is worded for the parameter group it lives in (A6) |
| C08 | Deadlocks, rollbacks and temp spills | applies, reworded | temporary files use the instance's local storage (A6) |
| C11 | Low cache hit ratio | applies, reworded | on Aurora Standard a miss is billed I/O (A5) |
| R01 | Replication slot retaining WAL | applies | Aurora has logical slots; below `wal_level = logical` (`rds.logical_replication` off) it refuses `pg_current_wal_lsn()`, so the read asks for a WAL position only under logical WAL, and otherwise says no slot's retention is measured |
| R02 | WAL archiving failing | **not applicable** | Aurora's storage layer archives; its readers do not stream (*Replication with Amazon Aurora PostgreSQL*) |
| R03 | Standby lag | applies, reworked | `aurora-reader`: Aurora's own `replica_lag_in_msec` (A3) |
| R04 | Requested checkpoints | **not applicable** | Aurora's storage layer checkpoints |
| S01 | PostgreSQL version end of life | applies | worded as the community's dates, "your provider's schedule may differ" |
| S02 | Settings that blind or endanger the store | applies | `fsync` and `full_page_writes` off are reported softly (Info): Aurora replaces Postgres' storage and its parameter reference lists neither |
| S03 | Checksum failures | **not applicable** | the cluster volume is Aurora's storage layer, "distributed, fault-tolerant, and self-healing" (*Amazon Aurora FAQ*); Postgres' page checksums are not the host's to choose there |
| S04-S10 | Security posture | apply | `rds_superuser` membership is "provider admin", not a superuser |
| K05-K08 | The studio's own configuration | apply | |
| T01-T08, T12-T16, T24 | Tables, indexes and vacuum | apply | Aurora runs autovacuum like PostgreSQL; its autovacuum settings are cluster-level parameters (A6) |
| T02, T22, T23 | Statements | apply | `pg_stat_statements`, preloaded by Aurora's default parameter group (A7) |

MartenStudio's own checks (the daemon, events, documents) read the store and apply unchanged. The Replication page's
checkpoint, WAL, archiver and tablespace sections say "Not applicable on Aurora"; its standbys become **Aurora replicas**
(A3). `pg_control_checkpoint()` and `pg_control_system()`, which the statistics window reads for the timeline and the
system identifier, are not documented by AWS for Aurora either way; a revoked or failing call nulls only the timeline and
system id, never the rest of the window.

## Reader lag (A3)

R03's `aurora-reader` variant judges each reader's `replica_lag_in_msec` - "Time that reader instance lags behind writer
instance, in milliseconds" (*aurora_replica_status*), the number CloudWatch keeps as `AuroraReplicaLag` - on issue #8 G8's
time thresholds: **Warning** at 5 minutes, **Critical** at an hour. There is no byte figure: Aurora replicates through its
storage. The writer (`session_id = 'MASTER_SESSION_ID'`) has no lag and is never judged. The Replication page lists every
instance with its role, its lag, its expected replay time, its hot-standby feedback and when its status was last updated.
See [R03](health/replication.md#r03-standby-lag).

## The wait profile (A4)

A snapshot of `pg_stat_activity` misses most of what an Aurora instance waits for - a commit waiting for storage
(`IO:XactSync`) lasts milliseconds. Aurora counts every wait instead, cumulatively: "the cumulative number of waits and
cumulative wait time for each wait event generated by the DB instance", with `wait_time` "in microseconds", reset "when a DB
instance restarts" (*aurora_stat_system_waits*). The Server page's Aurora section shows:

- **Waits**: the ten events with the most time, named `type:event` (`IO:XactSync`, `Lock:transactionid`), over a window
  of the health advisor's samples - each regular sweep of an Aurora database samples the twenty longest - and, before two
  samples, since the instance started. The idle `Activity` type (background processes' main loops waiting for work) is
  left out. A window never spans two instances: each sample carries the instance's identity.
- **Commits**: the mean commit latency over the window - the commit latency's gain over `xact_commit`'s - and since the
  statistics were last reset (`pg_stat_reset()` resets it, its page says).
- **DML**: each kind's count and mean latency, over the window and since the reset - successful statements only.
- **Sessions**, behind `ViewDatabaseActivity` and the write policy: each client session's waits since it connected, as
  counts and *shares of its own wait time*, never a time. `aurora_stat_backend_waits`' page says its `wait_time` is in
  milliseconds where the instance's function says microseconds, and the two pages' example magnitudes agree with each
  other, so its unit is not relied on. Sessions are read only for a visitor who passes the gate, and the first view in a
  circuit is one Activity entry.

The profile raises **no finding**: issue #8 names no threshold for a wait.

## Remedies name the right knob (A5, A6, A7)

- **Server settings.** Every server-setting remedy and the Server page's "where it changes" say where Aurora's parameter
  reference (*Amazon Aurora PostgreSQL parameters*, which lists `default.aurora-postgresql14`'s cluster-level and
  instance-level parameters) puts the setting: "the cluster's DB cluster parameter group" for a cluster-level parameter
  (`autovacuum_*`, `synchronous_commit`, `ssl`), "or one instance's DB parameter group to set it for that instance alone"
  for one listed at both levels (`work_mem`, `statement_timeout`, `enable_*`, `max_connections`), and "where Aurora allows
  it" for one the reference does not list (`max_wal_size`, `checkpoint_timeout`, settings newer than 14). On Serverless v2
  `max_connections` follows the maximum ACU; `autovacuum_max_workers`, `autovacuum_vacuum_cost_limit`,
  `autovacuum_work_mem`, `effective_cache_size` and `maintenance_work_mem` default from it; and `shared_buffers` is sized
  as the instance scales and ignores a set value (*Performance and scaling for Aurora serverless*). Every other platform's
  wording is unchanged.
- **C03** on Aurora names the knob that raises `max_connections` there, from the capacity range and `max_connections`
  itself (*Performance and scaling for Aurora serverless*): Serverless v2 derives it from the memory of the cluster's
  *maximum* capacity; "when you specify a minimum capacity of 0 or 0.5 ACUs on PostgreSQL-compatible DB instances, the
  maximum value of `max_connections` is capped at 2,000"; and its default stops at 5,000 from 32 ACU. So:
  - a minimum of 0.5 ACU or less with `max_connections` at the 2,000 cap: "Raise the cluster's minimum capacity to 1 ACU or
    more (now 0.5 ACU)";
  - a maximum of 32 ACU or more, or `max_connections` at 5,000: Aurora's ceiling - "Lower the pool ceilings of the
    applications that share the cluster, or put RDS Proxy or PgBouncer in front of it", since more capacity adds no
    connections;
  - otherwise, with an ACU reading: "Raise the cluster's maximum capacity (now 16 ACU)";
  - without one: both Serverless v2 cases and the provisioned one, and a pointer to the capacity source below.

  Each says that a `max_connections` set in a custom DB cluster or DB parameter group replaces the formula whatever the
  capacity (AWS recommends "a variation of the formula instead of specifying a constant value"), and a new range takes
  effect when the instances reboot (`max_connections` "is a static parameter for Aurora serverless"). See
  [C03](health/connections.md#c03-server-connection-headroom).
- **C08**: temporary files "share local storage with other log files" (*Managing temporary files with PostgreSQL*) - watch
  `FreeLocalStorage`, and `temp_file_limit` stops one statement from filling it.
- **C11** (A5): on Aurora Standard a read that misses the cache is billed I/O; on I/O-Optimized it is not. The finding
  says which from the capacity source's `IoOptimized`, and both when it is not set.
- **`pg_stat_statements`** (A7): "Typically, the default DB cluster parameter group loads only the
  `pg_stat_statements`" (*Working with extensions and foreign data wrappers*), so on Aurora a missing extension's sentence
  says `CREATE EXTENSION pg_stat_statements` is usually all it takes, and - if it then reports the library is not loaded -
  to add it to `shared_preload_libraries` in the DB cluster parameter group. A monitoring role is granted by the master
  user, a member of `rds_superuser`.

## The capacity source

Aurora Serverless v2's capacity is not readable from SQL: it exists as CloudWatch's `ServerlessDatabaseCapacity` - "the
number of ACUs represented by the current DB instance capacity" - and `ACUUtilization`, and the scaling range is the
cluster's configuration (*Performance and scaling for Aurora serverless*). The studio takes no AWS dependency. A host that
wants the capacity shown, C03 judged against it and A5's billing worded registers an `IPostgresCapacitySource` in its
container:

```csharp
builder.Services.AddSingleton<IPostgresCapacitySource, CloudWatchCapacitySource>();
```

```csharp
public interface IPostgresCapacitySource
{
    ValueTask<PostgresComputeCapacity?> GetCapacityAsync(
        PostgresDatabaseRef database, PostgresServerInfo server, CancellationToken cancellationToken);
}
```

The studio's side of the contract:

- It finds the source in the container once, lazily (`GetService`). **Register it as a singleton**: the studio asks it from
  the root container, so a scoped registration is not found - nor is one whose construction throws - and either is said
  once in the log, as event 9500, and read as no source.
- It asks at most **once per `HealthInterval` per database and instance**, one call at a time however many pages and sweeps
  want it, and gives it **five seconds** - the token it is handed is cancelled then.
- An answer of `null` (the source does not know this database - it is not Aurora, say), a timeout or an exception means
  "capacity unknown" for that interval, and the page says so. An exception is **event 9500**, at Warning once in ten minutes
  per database and Debug in between - the source's own `TimeoutException` or cancellation included: only the studio's five
  seconds running out is "took longer".
- `server.Instance` is the instance that answered - `aurora_db_instance_identifier()` - so a cluster with several instances
  reads the right one, and a failover asks again. `server.Platform` is `AmazonAurora` on Aurora; answer null elsewhere.

**A CloudWatch source, in prose.** With the AWS SDK of the host's choosing:

1. Answer `null` unless `server.Platform` is `AmazonAurora` and `server.Instance` is set.
2. Ask CloudWatch for `ServerlessDatabaseCapacity` in namespace `AWS/RDS` with the dimension `DBInstanceIdentifier` =
   `server.Instance`, the last few minutes at a one-minute period, statistic `Average`; take the latest datapoint and its
   timestamp.
3. Ask RDS's `DescribeDBClusters` for the instance's cluster (the cluster that lists it among its members): its
   `ServerlessV2ScalingConfiguration` has `MinCapacity` and `MaxCapacity`, and its `StorageType` is `aurora-iopt1` for
   I/O-Optimized.
4. Return `PostgresComputeCapacity.AuroraServerlessV2(current, minimum, maximum, measuredAt) with { IoOptimized = storageType == "aurora-iopt1" }`.
   `AuroraServerlessV2` checks 0 to 384 ACU, platform version 4's range (earlier platform versions stop at 256). C03 keys on
   the unit, `ACU`.
5. Cache your own reads for a minute or more: CloudWatch calls cost money, and the studio asks every `HealthInterval` for
   every database a visitor is watching.

A provisioned instance has no ACU; a source may still return its class's vCPUs as `PostgresComputeCapacity.Of(...)` in any
unit, which the page shows and C03 lists as evidence, and `IoOptimized` for C11.

## Issue #8's Aurora items in this release

| # | Item | What this release does |
|---|---|---|
| A1 | Which instance answered | "Answered by instance X (writer or reader), Aurora V" on the Health page, both Overviews and the Server page, through the database-wide gate. Another instance answering than at the last sweep reads as a reset of the statistics window, reason "another instance answered", said once on the Health page. Before Aurora 13.7 / 12.11 / 11.16 / 10.21 the instance is "not named". |
| A2 | No judgement of what Aurora's storage does | R02, R04 and S03 are listed as not applicable and never run; the Replication page's checkpoint, WAL, archiver and tablespace sections say so; every other shared check was reviewed and applies (the table above). |
| A3 | Reader lag | R03 `aurora-reader` from `aurora_replica_status()`'s `replica_lag_in_msec`: Warning at 5 min, Critical at 1 h; Replication › Aurora replicas lists every instance. |
| A4 | Wait profile | The Server page's Aurora section: the ten longest waits over the advisor's window, the commit latency, DML by kind, and per-session waits as counts and shares behind `ViewDatabaseActivity`. No finding. |
| A5 | I/O is cost | C11's wording on Aurora from `PostgresComputeCapacity.IoOptimized`, both cases when it is not set. The statements screen's storage-read column is the Statements packet's (Q1). |
| A6 | Remedies name the right knob | Every server-setting remedy per parameter (DB cluster parameter group, or an instance's DB parameter group too, or "where Aurora allows it"); C03 against the maximum ACU; C08's temporary files on local storage. |
| A7 | Privileges | "Granted by the master user, a member of `rds_superuser`"; `pg_stat_statements` preloaded by the default DB cluster parameter group, as AWS states it. |
| A8 | Capacity, host-supplied | `IPostgresCapacitySource` with `PostgresComputeCapacity`, asked once per interval per database and instance, five seconds, unknown on null, timeout or exception (event 9500); shown on both Overviews, the Health page and the Server page; read by C03 and C11. |

## Not verified

These need an AWS account, or a page AWS has not written:

- Whether a cluster's default parameter group really preloads `pg_stat_statements` (the text says "typically").
- The privileges Aurora's functions need: no page says, and the test shim grants them to every role.
- `max_connections` on Serverless v2 against the maximum ACU is quoted from AWS's table and text, not measured.
- The Standard and I/O-Optimized billing of a miss is AWS's statement; nothing in SQL says which a cluster uses.
- Whether Aurora allows `pg_control_checkpoint()` and `pg_control_system()`.
- The column types of every function, which the shim infers from AWS's examples (B0).

## Testing

`PostgresStudio.Integration.Tests` has VERS's Aurora shim (`Versions/AuroraShim.cs`): Aurora's functions written as plain
SQL in `pg_catalog`, from AWS's signatures, in a database of their own on the version matrix's PostgreSQL 17 - never one a
self-hosted test reads, because the shim makes its database an Aurora server for the detector. `Aurora/AuroraShimLiveTests`
proves the instance, R03 `aurora-reader` firing and quiet, the wait section, a failover between two sweeps, the
not-applicable checks, C03's remedy with and without a capacity source, the no-lock and no-DDL reads, and that no role short
of a superuser can plant an Aurora marker.
