# Health: server (S)

These checks look at the Postgres server the store runs on: the major version and how long the community
supports it, the settings that blind the store's maintenance or trade its safety for speed, and pages whose
checksums failed. They read `pg_settings`, `pg_stat_database` and the server's version, once per regular sweep,
in the studio's read-only transaction, and lock nothing they read. Back to the [index](../health.md).

## Managed platforms

A small detector says who runs the server, from markers each provider documents. It decides **only wording and
mutes**: S01's wording, R01's remedy (where a setting is changed), and S02 standing down for a setting a
provider documents as its own design. It never raises a finding and never sets a severity. A server it does not
recognise is self-hosted, which is also the right wording for a provider it has never heard of: the setting is
somebody's to change.

| Platform | Marker | Documented in |
|---|---|---|
| Amazon Aurora | an `aurora_version()` function (looked up in `pg_proc`, never called) | Aurora User Guide, *aurora_version* |
| Amazon RDS | an `rds.extensions` or `rds.allowed_extensions` setting | RDS User Guide, *Supported PostgreSQL extension versions*; Aurora User Guide, *Working with extensions* |
| Azure Database for PostgreSQL | an `azure.extensions` setting | Microsoft Learn, *PostgreSQL extensions in Azure Database for PostgreSQL* |
| Google Cloud SQL | a `cloudsql.*` setting (a database flag) | Cloud SQL for PostgreSQL, *Configure database flags* |
| Neon | a `neon_superuser` role, or a host under `.neon.tech` | Neon, *Postgres compatibility* and *Connection pooling* |
| Supabase | a host under `.supabase.co` or `.supabase.com` | Supabase, *Connect to your database* |

The markers are read in that order, and the first that matches wins. Aurora comes first because an Aurora
cluster has the `rds.*` settings too. The server markers - the Aurora function, the RDS, Azure and Cloud SQL
settings and Neon's role - are existence tests against the catalog that need no privilege. The host markers
(Neon's `.neon.tech`, Supabase's domains) are read from the host names in the store database's connection
string, without a query. Supabase has no server-side marker here: the `supautils.*` settings come from an extension Supabase
publishes and other distributions also install, and no Supabase page documents a setting only its servers have.
So a Supabase server reached through a proxy of your own is taken to be self-hosted. Debug event 9290 says
which platform was detected and why.

## S01 PostgreSQL version end of life

**Critical** on and after the community's final release of the server's major version. **Warning** within 90
days before it, and **Info** within a year. **PostgreSQL 14's final release is 2026-11-12.**

| Major | First release | Final release (end of life) |
|---|---|---|
| 9.6 | 2016-09-29 | 2021-11-11 |
| 10 | 2017-10-05 | 2022-11-10 |
| 11 | 2018-10-18 | 2023-11-09 |
| 12 | 2019-10-03 | 2024-11-21 |
| 13 | 2020-09-24 | 2025-11-13 |
| 14 | 2021-09-30 | 2026-11-12 |
| 15 | 2022-10-13 | 2027-11-11 |
| 16 | 2023-09-14 | 2028-11-09 |
| 17 | 2024-09-26 | 2029-11-08 |
| 18 | 2025-09-25 | 2030-11-14 |

From <https://www.postgresql.org/support/versioning/> (read 2026-10-17): "The PostgreSQL Global Development Group
supports a major version for 5 years after its initial release." The table ships in the package. A major older
than its first row is past its end of life. A major newer than its last row is newer than this release of the
studio, which says so rather than guess a date. The fast suite fails a year before the last row's date, and
whenever the Postgres image the live suite runs is a major the table lacks, so the table cannot go stale
quietly.

After the final release the community ships no more fixes for that major: no security fix, and no fix for a bug
that corrupts data. Drivers, extensions and tools drop it in turn. A major upgrade (`pg_upgrade`, a dump and
restore, or logical replication into a new server) is a planned piece of work, so the finding comes early.

**On a managed platform the date is the community's, and the finding says so**: "community support ends; your
provider's schedule may differ." A provider decides when it stops offering and patching a major (an
extended-support period, or an upgrade it starts itself), so its own lifecycle page is the one that decides.

Shown to anybody the store policy passes for the scope they selected, because the Overview and the
Configuration page show the version already. The platform's name is evidence at the database-wide gate.

## S02 Settings that blind or endanger the store

- **Critical anywhere**: `track_counts = off`. Autovacuum chooses what to vacuum and analyse from those counters.
  With it off, nothing is vacuumed except to stop wraparound: every document Marten upserts leaves a dead row
  version that is never cleared, the tables bloat, the planner's statistics go stale, and the studio's own table
  checks have nothing to read.
- **Critical in Production**: `fsync = off` and `full_page_writes = off`, which give up crash safety (a power cut
  can corrupt the cluster, not merely lose a transaction). Also `zero_damaged_pages = on`, which silently turns a
  damaged page into an empty one, and `ignore_checksum_failure = on`, which goes on using it. The environment is
  read as K01 reads it: Production is what `IHostEnvironment` says. Elsewhere, and for a host that names no
  environment, these are a note in the status row, because a development database may turn the first two off for
  speed on purpose.
- **Info**: `synchronous_commit = off`, where a crash of the server loses the last few hundred milliseconds of
  commits the application was told had succeeded - events an async projection or a subscriber may already have
  acted on. Also Info: any setting whose change waits for a restart (`pending_restart`), which is a
  configuration the server does not run yet.

**Never** a sizing setting - `shared_buffers`, `work_mem`, `random_page_cost` and the rest. Advice computed from
the server's memory cries wolf (R1 §D). The six settings above are the whole list.

The values are read on a connection from the store's own data source, so a value set for its role, its database,
or in its connection string's startup options (`Options=-c …`) is the value the store's sessions run with.
`pg_settings.source` says which, and the remedy points there: the connection string for a startup option, the
role's or database's own settings for those, otherwise the server's configuration on the platform the detector
found.

**S02 fires on a Testcontainers Postgres, by design.** The Testcontainers PostgreSQL module starts every server
with `-c fsync=off -c full_page_writes=off -c synchronous_commit=off` (its `PostgreSqlBuilder`, 4.15.0, "Disable
durability"). The sample host's throwaway server (D18) and the integration suite's are such servers. So in
Development the sample shows `synchronous_commit`'s Info and a note about the other two, and under a Production
environment both are Critical, which is true of that server.

**Platforms whose storage is their own.** Only a documented design is muted:

- **Neon's `fsync = off` is muted.** It is Neon's documented design: "Neon syncs data to the Neon database storage
  engine" (*Postgres compatibility*). On a server the detector takes for Neon, it is a note.
- **Neon's `full_page_writes = off` is Info, not Critical, and not muted.** Neon documents no value for it, and
  neondatabase/postgres#822 reduces full-page images rather than turning them off.
- **Aurora's `fsync = off` and `full_page_writes = off` are both Info, not Critical, and not muted.** The Aurora
  User Guide's parameter reference lists neither, and no AWS page the studio could find says what Aurora reports
  for them (checked 2026-10-18).

For these three, the finding says the provider manages durability and Marten Studio cannot verify the setting's
meaning there, and it offers no remedy line. No other platform changes S02's judgement.

**Not read is not safe.** A setting S02 watches whose `pg_settings` row it could not read - hidden from the role,
or absent from the server - is named in the status row as not judged. It is never counted among the settings at
their safe values. A `synchronous_commit` of `local`, `remote_write` or `remote_apply` loses no commit on this
server, so it is not a finding; the status row says what each waits for.

## S03 Checksum failures

**Critical** for any page whose checksum failed: `pg_stat_database.checksum_failures` above zero (PostgreSQL 12+).

With data checksums on, Postgres checks every page it reads from disk. A failure is a page that changed after
Postgres wrote it - the storage, a controller, memory, a filesystem, or a copy taken the wrong way corrupted it.
The statement that read it failed with "invalid page", and the rows on it cannot be read until it is repaired.
Corruption seldom stops at one page. Check the storage and the memory, find the relation from the server log
("page verification failed" names its file), and restore it from a backup or a replica that does not have the
damage. `zero_damaged_pages` and `ignore_checksum_failure` are not repairs (S02).

The count covers the whole server: this database's failures, every other database's together (counted, and how
many databases, never their names), and the shared catalogs'. Corruption in another database is the same disks'
corruption. The counters only grow until somebody resets the statistics, so a failure stays after it is
repaired, and the finding says since when it counts. A damaged page that is never read is never counted.

**Checksums off is a status line, not a finding.** Clusters made before PostgreSQL 18 have them off unless
`initdb --data-checksums` was used, and turning them on takes a stopped server and `pg_checksums --enable`. That
is a DBA's decision, not something the studio can weigh.
