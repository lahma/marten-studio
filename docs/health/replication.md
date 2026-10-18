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
