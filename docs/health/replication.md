# Health: replication and WAL (R)

These checks look at what the server keeps of its write-ahead log for somebody else: the replication slots
that change data capture, logical subscriptions and standbys read through. A slot is the server's, not one
database's - a physical slot belongs to no database, and a logical slot for another database still holds the
WAL this one writes - so, like C03, the numbers are the whole server's, and every store database on the server
shows the same ones. Back to the [index](../health.md).

## What these checks read, and when

`pg_replication_slots`, `pg_current_wal_lsn()` (on a server in recovery, its last replayed position instead) and
the settings `max_slot_wal_keep_size` and `wal_keep_size`, once per regular sweep, in the studio's read-only
transaction on the primary. `pg_replication_slots` is a view over the slots' shared-memory state: no relation is
locked and nothing is written. The read is shared, under the key `server.replication-slots`, with T14, which
reads the same slots for the vacuum horizon they pin.

Columns that later releases added are read so that an older server answers "not reported" rather than failing:
`wal_status` and `safe_wal_size` from PostgreSQL 13, `conflicting` from 16, `inactive_since` and
`invalidation_reason` from 17.

## Who sees what

The counts, the sizes and the settings are shown to anybody the store policy passes for the database as a whole
(`TenantId = null`). They are the whole server's: every slot is counted, whichever database it serves.

Names are shown more narrowly:

- **A physical slot's name**, and **a logical slot's name, plugin and database when the slot is this database's**,
  need `MartenStudioOptions.Capabilities.ViewDatabaseActivity` and the write policy asked with
  `Capability = "ViewDatabaseActivity"` and `TenantId = null`, like every other name in the advisor. A physical
  slot belongs to no database, so it is the server's to name.
- **A logical slot of another database on the same server is never named**, whatever gate the visitor passes. It
  reads "a logical slot of another database on this server", with its size, state and horizon but no name, no
  plugin and no database. In a store with a database per tenant, another database is another tenant, and a
  visitor admitted to this database has not been admitted to that one. That is the rule Database › Sessions keeps
  for another database's sessions, and S03 for another database's checksum failures.

A finding's subject is the kind of finding (`near-limit`, `inactive`, `lost`), never a slot, because the subject is
in the mute key every visitor of the finding is handed.

The read is shared with T14 under `server.replication-slots`, as `ReplicationSlotSnapshot`. Each slot carries a
`ThisDatabase` flag (true for a physical slot and for a logical slot of the connected database), and every reader
names a slot only where it is true.

## R01 Replication slot retaining WAL

A replication slot keeps every WAL file its consumer has not confirmed. With nobody reading it, it grows with
every write the server takes - every Marten append, every document upsert - until the WAL volume fills and
Postgres stops accepting writes. Change data capture on `mt_events` (Debezium, a logical subscription, a cloud
provider's replication service) is how events leave a Marten store, and a connector that was stopped for a
deployment and never restarted, or a replica that went away without its slot, is the usual cause.

- **Critical**: a slot with `wal_status = 'unreserved'`. Postgres has stopped keeping WAL for it and removes the
  files at the next checkpoint, after which the slot is lost. Also Critical: with `max_slot_wal_keep_size` set,
  an inactive slot holding 80% of it or more on two consecutive samples, or for a minute by PostgreSQL 17's
  `inactive_since`. A slot seen there once is a note in the status row, so that a consumer reconnecting between
  two sweeps does not flash a Critical, and its toast, for one sweep.
- **Warning**: an inactive slot holding 1 GiB of WAL or more, and doing so for 10 minutes of the advisor's
  samples. From PostgreSQL 17 its own `inactive_since` counts too, which is Postgres's clock and needs no
  watching. Below 10 minutes the status row says it is watching.
- **Info**: a slot Postgres has invalidated: `wal_status = 'lost'`, an `invalidation_reason` (17+), or a
  recovery conflict (16+). It holds nothing now, and its consumer has stopped receiving changes whether or not
  it says so.
- **Never** an active slot, unless it is unreserved. A connected consumer that is behind is replication lag,
  not a forgotten slot. A server with no slots has nothing to say.

The evidence gives the counts, the WAL held, both settings and, from 13, how much may still be written before
the first slot is lost. Each slot, behind the activity gate, is listed with what it holds, its `wal_status`, and
the ages of its `xmin` and `catalog_xmin`, and with its kind, plugin and database when it is this database's to
name. A logical slot holds back vacuum of the system catalogs, and a physical one with a standby's feedback holds
back vacuum everywhere; that horizon is T14's finding.

**History.** Every slot the read returns - another database's too, at most 200, most WAL first - adds
`replication.slot-idle-retained:<slot>` to the database's samples: the WAL it held while inactive, and zero while
a consumer was connected or once it was lost. So a slot that was connected in between breaks the 10-minute run
rather than being skipped over. Another database's slot is sampled here because nothing else may ever sweep its
database (a Debezium slot in a database the store does not have), and before PostgreSQL 17 these samples are the
only clock it has. The series name is internal: it is never shown, exported or logged, and the one log line about
series names a series by its family, never by its slot. Samples exist only while somebody watches (the advisor runs
no background work), and are lost on a restart.

What to change - in words first, because the remedy is the consumer's and the server's, not the store's:

1. Restart whatever reads the slot - the connector, the subscription, the standby - so that it catches up and
   Postgres can recycle the WAL it confirms. If nothing will read the slot again, drop it
   (`pg_drop_replication_slot`) and the WAL goes at the next checkpoint.
2. Cap what any slot may hold, so that a forgotten one is invalidated instead of filling the disk:

```text
# A server setting, not a StoreOptions one (PostgreSQL 13+):
max_slot_wal_keep_size = 50GB
```

A slot that passes the cap is invalidated, and its consumer has to be re-initialised from a fresh copy. So size
the cap above the longest outage a consumer should survive. For an unreserved slot, raising the cap buys time
only where the disk has room for it; restarting the consumer before the next checkpoint is what keeps its
place.

**Where the setting is changed** depends on who runs the server, and the remedy names the place for the
platform the studio detected (see [Server, Managed platforms](server.md#managed-platforms)):

| Platform | Where |
|---|---|
| Self-hosted | `postgresql.conf`, then a reload |
| Amazon RDS | the instance's DB parameter group |
| Amazon Aurora | the cluster's DB cluster parameter group |
| Azure Database for PostgreSQL | the server's Server parameters |
| Google Cloud SQL | the instance's database flags |
| Neon | a request to Neon Support: "Postgres parameters are not user-configurable outside of a session, database, or role context" |
| Supabase | the project's Postgres configuration, where Supabase allows it |

The studio never changes a setting and never drops a slot itself.

## R02 WAL archiving failing

Runs in both studios, Marten Studio and Postgres Studio. Variants: `failing`, `backlog`.

With `archive_mode` on, Postgres keeps every WAL file until the archive command has copied it. An archiver that
fails, hangs or falls behind fills `pg_wal` until the WAL volume is full and the server stops accepting writes, and
every point-in-time restore from the archive ends at the last file it did copy.

R02 reads `pg_stat_archiver`, the settings `archive_mode`, `archive_command` (or `archive_library` from 15) and
`wal_segment_size`, and - on a server not in recovery - `pg_walfile_name(pg_current_wal_lsn())`. None of them needs a
role or takes a lock. It judges a server only when `archive_mode` archives there: `on` on a primary, `always`
anywhere.

- **`failing`, Critical**: `failed_count` rose within the advisor's window, the last failure is later than the last
  success, or there are failures and no success at all. The evidence is the failed count (and how many in the
  window), the last failed WAL file and when, and the last success and how long ago. The archive command is failing,
  and WAL accumulates in `pg_wal` until it succeeds. Postgres logs each failure with what the command said, so the
  server log is where to look first. There is no remedy line: the command is the host's own.
- **`backlog`, Warning; Critical at 10 GiB, or with no successful archive for an hour while it grows**: the distance
  from the last archived segment to the segment being written is 64 segments or more **and** 1 GiB or more, and has
  grown across 10 minutes of the advisor's samples. A hung or slow `archive_command` (an upload that stalls), and
  `archive_mode = on` with an empty `archive_command` - which, per the manual, "continues to accumulate WAL segment
  files" - never raise `failed_count`, so only the distance says so.

The distance is parsed from the two file names in the studio, the same way on every release (`pg_split_walfile_name`
exists only from 16): a WAL file name is a timeline and a segment number, and the numbering continues across a
timeline change. A `.partial` file (the last segment of the old timeline at a promotion) is read as the segment its
24-digit prefix names, which is where the archiver got to. A last archived file that names no position counts as
"nothing archived yet": none yet, a `.history` file, or a `.backup` label, whose 24 digits are the base backup's *start*
segment and which the archiver sends before the backup's stop segment, so it says nothing about where the archiver got
to. The distance series then has a gap rather than a jump, and the backlog is the
segments the advisor has seen written in its window, judged only while the archiver archived nothing in that same
window (an archived file is a working archiver): a lower bound, never sized by the whole WAL sequence. A standby is
not judged for a backlog, because `pg_walfile_name` cannot run in recovery. A `failing` archiver has a backlog too, and
is said once, as `failing`.

With `pg_monitor` (or as a superuser), the evidence and the Replication page add how many files wait in
`archive_status` (`.ready`, from `pg_ls_archive_statusdir()`) and the size of `pg_wal` (`pg_ls_waldir()`, read once for
both). Without it they say "not checked", naming `pg_monitor`, the role the studio reads as and who can grant it. They
are never the trigger: a directory that cannot be read (a revoked function, a statement timeout) says "could not be
read" with its SQLSTATE, and R02 judges the archiver's own counters regardless.

`archive_command`'s text is shown only with `ViewDatabaseActivity` and the write policy, like every other name: a
command line may carry a host, a bucket or a key.

**Not applicable** (listed, never run) on Amazon Aurora, whose storage layer does the archiving, and on Amazon RDS,
Azure Database for PostgreSQL and Google Cloud SQL, which archive WAL themselves for their own backups and
point-in-time restore:

| Platform | What its own page says |
|---|---|
| Amazon RDS | "RDS uploads transaction logs for DB instances to Amazon S3 every five minutes" (*Restoring a DB instance to a specified time for Amazon RDS*) |
| Azure Database for PostgreSQL | "The server also backs up transaction logs when the write-ahead log (WAL) file is ready to be archived" (*Backup and Restore in Azure Database for PostgreSQL Flexible Server*) |
| Google Cloud SQL | point-in-time recovery reads the instance logs stored in Cloud Storage (*Perform point-in-time recovery (PITR)*) |

**History.** R02 adds `replication.archiver-failed`, `replication.archiver-archived` and `replication.wal-segment` (the
absolute number of the segment being written) to the database's samples while the server archives, and
`replication.archiver-backlog` (the distance, in segments) only while there is a last archived segment to measure from.
The two never share a series: a distance next to an absolute segment number would read as billions of segments
written the moment the archiver first succeeds, or as a backlog that shrank to nothing at a promotion.

## R03 Standby lag

Runs in both studios, Marten Studio and Postgres Studio. Variants: `primary-view`, `standby-view`, `paused`.

A standby that is behind is behind for whatever reads it, and if it is the failover target, everything it has not
replayed is what a failover loses or waits for. The thresholds are issue #8 G8's: **Warning** at 5 minutes or 1 GiB
behind, **Critical** at an hour.

- **`primary-view`**: the data source answered as a primary, and `pg_stat_replication` has each standby's replay
  position and `replay_lag`. The bytes are the primary's WAL position less the standby's replay position;
  `replay_lag` is empty once a standby has caught up and nothing new arrives, so an idle primary never makes it grow.
  **This needs `pg_read_all_stats`** (or `pg_monitor`, which includes it): without it, `pg_stat_replication` shows a
  sender's process id, role and application name and nothing else, so R03 is "not checked", naming the grant, the
  role the studio reads as and who can grant it - and a physical slot's `restart_lsn` is no substitute, because it
  tracks flush, not replay.
- **`standby-view`**: the data source answered in recovery - a multi-host data source that reaches a standby. What
  the standby has received less what it has replayed is exact and needs no grant. The time is **how long WAL it
  received has gone unreplayed**: each sweep samples what the standby has received (`pg_last_wal_receive_lsn()`),
  and the time behind is the age of the oldest sample whose position replay has still not reached. That is a lower
  bound, exact to within one sample. It is never the age of the last replayed commit, which grows with no lag at all
  on an idle primary, behind a long writer that has not committed (a freeze `VACUUM` writes WAL for hours and
  commits nothing), and on a delayed standby after a quiet night; that age is shown in the evidence and never judged.
  Before there is an earlier sample, or while replay has reached nothing the samples saw received and the bound is
  still under 5 minutes, the status row says the time is "not judged yet". A standby stuck since before the advisor
  started watching fires on the bound once it reaches 5 minutes, and says "at least".
- **`paused`**, Warning: replay is paused (`pg_wal_replay_pause()`, `pg_get_wal_replay_pause_state()` from 14). The
  standby keeps receiving WAL and applies none of it until somebody runs `pg_wal_replay_resume()`.

**Only physical senders are standbys.** `pg_stat_replication` lists every WAL sender, logical ones too - a
subscription, a change data capture connector - and a logical sender of *another database* on the same server is
another tenant's in a database-per-tenant store. So the read joins each sender to `pg_stat_activity`: one connected to
no database is physical and judged; a logical sender of this database is listed on the page, named through the activity
gate, and never judged; a logical sender of another database is counted ("N logical senders of other databases on this
server, never named") and nothing else - its application name, client address and role are blanked in the query
itself, for every visitor.

**A delayed standby** (`recovery_min_apply_delay`) replays late on purpose. On the standby the setting is read, and the
time judged is the time beyond the delay: a healthy one replays what it received a delay ago, so it fires only when WAL
received more than the delay and 5 minutes ago is still unreplayed - replay stuck behind the delay, not waiting on it.
That needs samples that reach back past the delay: the advisor keeps 60 samples per database, an hour at the default
`HealthInterval` of one minute, so a standby delayed by more than about 55 minutes is judged only with a longer
`HealthInterval`, and until then its status row says "not judged yet" and why. An hour's delay after a night with no commits is not 8.5 hours of lag: the
last replayed commit is that old, but replay has reached everything received more than an hour ago. The WAL it holds
for the delay is shown and not judged.

**A known limit from the primary.** The delay is the standby's own setting, which the primary cannot read, so from the
primary a delayed standby's `replay_lag` and bytes include its delay: an hour's delay reads as Critical there. The
caveat says so. Point a data source at the standby to judge it beyond its delay.

**A cascading standby** - a standby that others stream from - measures its downstream standbys against the furthest it
has received or replayed, never `pg_current_wal_lsn()` (which cannot run in recovery). R03 judges only the server the
data source reached; the page lists the downstream standbys and says they are not judged.

One finding per variant, about every standby it judges, with the worst lag as its numbers; a standby's application
name, client address and role are evidence shown only with `ViewDatabaseActivity` and the write policy.

**Not applicable** on Amazon Aurora: its readers share the cluster volume and do not stream WAL.

**History.** On a standby, R03 adds `replication.standby-received`, the position it has received, to the samples: the
earlier samples are what the time behind is read from.

## R04 Requested checkpoints

Runs in both studios, Marten Studio and Postgres Studio.

A checkpoint is *requested* rather than timed when WAL reaches `max_wal_size` before `checkpoint_timeout` comes round
(or a `CHECKPOINT`, a base backup or `CREATE DATABASE` asks for one). Checkpoints then come closer together than
configured; each writes every dirty buffer, and the first change to each page after it writes the whole page to WAL
again, so frequent checkpoints raise both write I/O and WAL volume.

R04 judges the advisor's own window of samples - never "since a reset nobody remembers" - and fires, at **Warning**,
on either of two triggers:

1. **The share** (issue #8 G8): requested checkpoints are 20% or more of at least 10 checkpoints in the window. The
   denominator is the release's own count:
   - up to PostgreSQL 16, `pg_stat_bgwriter`'s `checkpoints_timed + checkpoints_req` - where `checkpoints_timed`
     counts the checkpoints an idle server **skipped** too, so idle time dilutes the share;
   - on 17, `pg_stat_checkpointer`'s `num_timed + num_requested`, with the same dilution: `num_timed` counts skipped
     checkpoints too;
   - from 18, `num_done`, the checkpoints that ran.
2. **More than the timeout allows**, on every release: at least 10 requested checkpoints in the window, and more than
   the window divided by `checkpoint_timeout`. It does not depend on a timed count that includes skipped checkpoints.

The remedy doubles `max_wal_size`, worded for the platform that runs the server (see
[Server, Managed platforms](server.md#managed-platforms)); it costs `pg_wal` disk and a longer crash recovery.

**Not applicable** on Amazon Aurora, whose storage layer checkpoints.

**History.** R04 adds `replication.checkpoints-timed`, `replication.checkpoints-requested` and (18)
`replication.checkpoints-done` to the samples, and `replication.wal-bytes`, `replication.wal-records` and
`replication.wal-fpi`, which the Replication page's WAL rate and full-page-image share are read from.

## The Replication page

Operations › Replication shows, for the database the scope names: the slots (R01's read, with each logical slot's
decoding statistics from `pg_stat_replication_slots` and, from 17, `failover` and `synced`), the standbys streaming
from the server (or, on a standby, its own replay), the archiver, WAL volume and the full-page-image share, the
checkpoints, I/O by backend type (`pg_stat_io`, 16+; byte columns are 18's own, and operations times `op_bytes` on 16
and 17), and the tablespaces. Numbers are shown to anybody the store policy passes for the database as a whole;
names follow R01's rule and `ViewDatabaseActivity`, and a logical slot of another database has its decoding statistics
shown under no name, as R01 shows the slot. The standbys table marks each sender physical or logical, and counts
another database's logical senders without listing them. A tablespace's size is read only behind its button, one at a
time per database for the whole process: `pg_tablespace_size` walks the whole directory, and is refused to a role
without `CREATE` on the tablespace or `pg_read_all_stats` (`pg_global` among them), which the page says as "not
readable by this role".

On Amazon Aurora the checkpoint, WAL, archiver, tablespace and standby sections each say "Not applicable on Aurora:
Aurora's storage layer does this, and its readers do not stream", and the slots and I/O stay. On Amazon RDS, Azure and
Cloud SQL the archiver section says the provider archives WAL itself.

**Standby feedback moved to slots** (B0 §4). R1 read a standby's `hot_standby_feedback` horizon from
`pg_stat_replication.backend_xmin`, which is null even for `pg_monitor` when the standby uses a slot: the feedback
lands on the slot's `xmin`, which R01 already reads. Moving T14's horizon reading to it is V1's fix, in T14.
