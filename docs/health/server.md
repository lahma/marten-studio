# Health: server (S)

These checks look at the Postgres server the store runs on: the major version and how long the community
supports it, the settings that blind the store's maintenance or trade its safety for speed, and pages whose
checksums failed. They read `pg_settings`, `pg_stat_database` and the server's version, once per regular sweep,
in the studio's read-only transaction, and lock nothing they read. Back to the [index](../health.md).

## Managed platforms

A small detector says who runs the server, from markers each provider documents. It decides **only wording,
mutes and what does not apply**: S01's wording, R01's remedy (where a setting is changed), S02 standing down for a
setting a provider documents as its own design, and which of R02-R04 and the Replication page's sections are not
applicable there (issue #8 A2, [Replication](replication.md)). It never raises a finding and never sets a severity. A server it does not
recognise is self-hosted, which is also the right wording for a provider it has never heard of: the setting is
somebody's to change.

| Platform | Marker | Documented in |
|---|---|---|
| Amazon Aurora | an `aurora_version()` function in `pg_catalog` (looked up in `pg_proc`, never called) | Aurora User Guide, *aurora_version* |
| Amazon RDS | an `rds.extensions` or `rds.allowed_extensions` setting from the server's configuration | RDS User Guide, *Supported PostgreSQL extension versions*; Aurora User Guide, *Working with extensions* |
| Azure Database for PostgreSQL | an `azure.extensions` setting from the server's configuration | Microsoft Learn, *PostgreSQL extensions in Azure Database for PostgreSQL* |
| Google Cloud SQL | a `cloudsql.*` setting (a database flag) from the server's configuration | Cloud SQL for PostgreSQL, *Configure database flags* |
| Neon | a `neon_superuser` role that is `NOLOGIN`, `REPLICATION` and `BYPASSRLS`, or a host under `.neon.tech` | Neon, *Postgres compatibility* and *Connection pooling* |
| Supabase | a host under `.supabase.co` or `.supabase.com` | Supabase, *Connect to your database* |

The markers are read in that order, and the first that matches wins. Aurora comes first because an Aurora
cluster has the `rds.*` settings too. The server markers - the Aurora function, the RDS, Azure and Cloud SQL
settings and Neon's role - are existence tests against the catalog that need no privilege. The host markers
(Neon's `.neon.tech`, Supabase's domains) are read from the host names in the store database's connection
string, without a query. Supabase has no server-side marker here: the `supautils.*` settings come from an extension Supabase
publishes and other distributions also install, and no Supabase page documents a setting only its servers have.
So a Supabase server reached through a proxy of your own is taken to be self-hosted. Debug event 9290 says
which platform was detected and why.

**No marker can be planted by a role short of a superuser.** A platform silences findings (S02's stand-down, R02-R04
on Aurora), so each server marker is fenced to where only a superuser or the provider can put it. A setting is read
from `pg_settings`, and on a server without the provider's module a name such as `rds.extensions` can only be a
placeholder, which `pg_settings` never lists - whether a session's `SET` or `SET LOCAL` or a client's startup options
made it - while a database owner's `ALTER DATABASE ... SET` or a role's `ALTER ROLE ... SET` of one is refused to
anybody but a superuser. As defence in depth, a setting also counts only when it comes from the server's own
configuration. The Aurora function counts only in `pg_catalog`, where only a superuser may create one - a
`public.aurora_version()` does not.
Neon's role counts only with `REPLICATION` and `BYPASSRLS`, which only a superuser may grant before PostgreSQL 16,
and from 16 only a `CREATEROLE` role holding both.

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
| 19 | 2026-11-19 (planned) | 2031-11-13 (planned) |

From <https://www.postgresql.org/support/versioning/> (read 2026-10-17, and again 2026-10-26, when every row from
9.6 to 18 still matched): "The PostgreSQL Global Development Group supports a major version for 5 years after its
initial release." The table ships in the package. A major older than its first row is past its end of life. A
major newer than its last row is newer than this release of the studio, which says so rather than guess a date.
The fast suite fails a year before the last row's date, and whenever the Postgres image the live suite runs is a
major the table lacks, so the table cannot go stale quietly.

**19 is planned.** The versioning page does not list it before it ships. Its general availability is planned for
2026-11-19 ([PostgreSQL 19 RC 1 Released!](https://www.postgresql.org/about/news/postgresql-19-rc-1-released-3409/),
2026-10-15), and the policy's five years end with 2031's November minor release, 2031-11-13. A 19 server is quiet
and says its dates are the plan's. Once 19 ships, the row takes the versioning page's dates.

**Below the supported floor.** The studio supports PostgreSQL 14 and later ([versions](../postgres-versions.md)).
On an older server S01 fires anyway (13 reached its end of life on 2025-11-13), and its finding adds that the
server is below the supported floor (14), with the floor as evidence: the studio still reads such a server, read by
read, but is tested only on 14 and later.

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
  checks have nothing to read. Also `autovacuum = off` (0.5): nothing vacuums or analyses a table by itself any more,
  whatever its own storage parameters say, and the only vacuum still coming is the forced anti-wraparound one, which
  scans a whole table at once. Its remedy is `autovacuum = on` (a reload, no restart) where the platform changes
  server settings; a table switched off by its own storage parameter is T24's.
- **Warning**: `track_activities = off` (0.5). `pg_stat_activity` then records no state, no statement and no times for
  a session, so the session checks C04, C05 and T14 cannot see sessions' state or age, and Database › Sessions shows
  sessions without what they are doing.
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
the server's memory cries wolf (R1 §D). The eight settings above - `track_counts`, `autovacuum`, `track_activities`,
`fsync`, `full_page_writes`, `zero_damaged_pages`, `ignore_checksum_failure` and `synchronous_commit` - and the
settings waiting for a restart are the whole of what S02 judges.

Variants: `setting`, one finding per watched setting at a dangerous value, whose subject is the setting's name (key
`S02|setting|fsync`); and `pending-restart`, one finding for every setting that waits for a restart, with no subject (key
`S02|pending-restart`, as before 0.5, when the kind was its subject). Both studios run it.

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

**Not applicable on Amazon Aurora** (issue #8 A2): the cluster volume is Aurora's storage layer - copies across three
Availability Zones (*Amazon Aurora storage*), "distributed, fault-tolerant, and self-healing" (*Amazon Aurora FAQ*) - so
whether Postgres' own page checksums are on is not the host's to choose, and nobody runs `initdb` or `pg_checksums` for
an Aurora cluster. S03 is listed with that reason and never run. See [Aurora](../aurora.md).

## S04 Connecting as a superuser

**Warning in Production, Info anywhere else.** Both studios. The role the data source logs in as - `session_user` on a
connection of the store's or data source's own data source, before any `SET ROLE` - is one of three kinds, its variant
(worst first):

- **`superuser`**: `rolsuper`. It passes every permission check, so one SQL injection in the application, or one mistaken
  statement, can read and change every database on the server, drop any table, read and write the server's files and run
  programs on it (`COPY … PROGRAM`). No managed provider hands one out.
- **`provider-admin`**: a member of `rds_superuser` (Amazon RDS and Aurora), `azure_pg_admin` (Azure), `cloudsqlsuperuser`
  (Cloud SQL) or `neon_superuser` (Neon). It is never called a superuser: it is a provider admin role, the platform's
  administrator, which creates roles and databases and can grant itself every monitoring role.
- **`file-access`**: a member of `pg_read_server_files`, `pg_write_server_files` or `pg_execute_server_program`, which let
  `COPY` read and write the server's files or run programs as the server's operating-system user.

Membership is `MEMBER`, not `USAGE`: a role that may `SET ROLE` to one holds its power whether or not it inherits it. A
role the application switches to later is not seen. The environment is read as K01 reads it, and a throwaway development
server's superuser - the sample's, and every Testcontainers server's - is Info.

There is no remedy line. The change is a role that owns the application's schema and nothing more, which is the DBA's to
create and the data source's to log in as; the finding says so in words. `SqlConsoleRole` does not change what S04 sees.

## S05 Connection without TLS

**Warning in Production, Info anywhere else.** Both studios. `pg_stat_ssl` for the backend of a connection from the data
source says `ssl = false`, and the connection came over TCP from another machine: `inet_client_addr()` is set and is not a
loopback address (`127.0.0.0/8`, `::1`). A Unix-domain socket, which has no client address, and a loopback connection never
leave the server's machine and are quiet.

The connection is the data source's own, so it negotiates TLS exactly as the application's connections do. Without TLS,
every statement, every document and event, and the login itself (where the server does not ask for SCRAM) cross the
network in plain text.

What to change, in the connection string the data source is built from - a client setting, not a server one:

```text
SSL Mode=VerifyFull
```

`VerifyFull` encrypts and checks that the server's certificate chains to a trusted authority and names the host; `Require`
encrypts without checking either, which stops a listener but not an impostor. A server with `ssl = off` must be given a
certificate first, which the finding says when it reads that setting.

## S06 Anyone may create in an application schema

**Warning**, one finding per schema. Both studios. `PUBLIC` - every role on the server, now and later - holds `CREATE` on
one of the source's own schemas: a Marten store's schemas, or Postgres Studio's admitted `BrowsableSchemas`. It is read from
the schema's ACL with `aclexplode`, or from its owner's default when it has none, which grants `PUBLIC` nothing. The finding
names the schema, which is structure the source's own visitors are shown already.

Any role can then put a table, a view, a function or an operator there, and one named like something the application calls
unqualified, earlier on its `search_path`, is called in its place with the application's rights - the pattern of
CVE-2018-1058.

**Fires on 14, quiet on 15 and later, by default.** A cluster created on PostgreSQL 14 gives every database's `public`
schema this grant; from 15 `initdb` makes `pg_database_owner` its owner and grants `PUBLIC` nothing (measured by B0: `t` on
14, `f` on 17 and 18). A cluster upgraded from 14 keeps the grant.

The remedy is words, because it is a privilege change and no remedy may be one: whoever owns the schema revokes `CREATE` on
it from `PUBLIC`, and grants it to the roles that create objects there.

## S07 Outdated extension

**Info**, one finding per extension. Both studios. An extension installed in this database is at another version
(`pg_extension.extversion`) than the one the server's files would install now (`pg_available_extensions.default_version`).

A package upgrade on the server replaces an extension's library at once, and the SQL objects the extension created in each
database stay at their old version until somebody updates them there: its functions, views and types are the old version's
while the library behind them is the new one. An extension whose control file is gone (no default version) is not judged,
and the status line names it.

The remedy is words, because it is DDL: the extension's owner - or a superuser, or the platform's admin role - runs
`ALTER EXTENSION … UPDATE` in this database after reading its release notes.

## S08 Connecting role's password expires

**Warning** within 30 days, **Critical** within 7 days and once it has passed. Both studios. No variant. The role the data
source logs in as has a `VALID UNTIL` (`pg_roles.rolvaliduntil`). Seven days is PostgreSQL 19's default
`password_expiration_warning_threshold`.

Once the password has expired, Postgres refuses every new password login as that role. The connections already open go on
working, but the pool can open no new one, so as connections are recycled, or the application restarts, it stops reaching
the database.

**It applies only if the role logs in with a password**; an IAM, Entra ID or certificate login ignores `VALID UNTIL`, and
the studio cannot tell how the data source authenticates. The remedy is words: whoever administers the role gives it a
later expiry or a new password (`ALTER ROLE … VALID UNTIL …`) - a DBA's change.

## S09 Planner method switched off

**Warning**, one finding per setting. Both studios. A `pg_settings` row whose name is `enable_…` reads `off` while its
built-in value (`boot_val`) is `on`, read on a connection of the data source's own, so a value set for its role, its
database or in its connection string counts. A switch a release ships off is never a finding.

An `enable_*` switched off is a workaround for one query's plan applied to every query of every session it reaches: a query
whose best plan needs that method gets a slower one, and nothing in the query or its plan's cost says why. The evidence is
the setting and its `source`.

The remedy follows the source: a server setting through the platform's own place for it (`enable_x = on`, or remove the
line) when the server's configuration set it; words for a role's or a database's (`ALTER ROLE … RESET`,
`ALTER DATABASE … RESET`), whose owner resets it there; and words for the connection string's startup options.

## S10 Store connections cannot write

Both studios. Two variants:

- **`read-only`, Critical**: `default_transaction_read_only = on` as the data source's own sessions start with it, and its
  `source` says who set it. A transaction that does not ask otherwise is read-only, and Marten never asks, so every
  `SaveChanges`, append and projection write fails with `25006`. **On Azure Database for PostgreSQL** the server sets it
  itself when its storage is about 95% used or has under 5 GiB free, so the remedy there is the platform's: grow the
  storage, or turn on storage autogrow (Microsoft Learn, *Storage options in Azure Database for PostgreSQL*). Elsewhere it
  is the server setting, or words for a role's, a database's or the connection's own.
- **`standby`, Warning**: the connection the advisor opens for writing - the host a multi-host data source writes to -
  answered `pg_is_in_recovery() = true`. A standby accepts no write. The remedy is the connection string's
  `Target Session Attributes=read-write`, with every host of the cluster listed, so Npgsql opens connections only to the one
  that writes. If the store only ever reads there, mute it.
