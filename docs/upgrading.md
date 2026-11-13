# Upgrading to 1.3

## 1.2 → 1.3: the navigation switch is gone

- **`PostgresStudio.DisableNavigationInterception` is removed.** 1.2 introduced it as a temporary AppContext switch that
  restored 1.1's navigation (an enhanced-navigation fetch per page) and promised to remove it in 1.3. A host that still
  sets it gets 1.2's default navigation, handled inside the studio's circuit: the setting is read by nothing and is
  harmless, and the `AppContext.SetSwitch(...)` call can be deleted. No option, type or public API changed.

## 1.1 → 1.2: what changes without a setting

1.2 moves no floor (Marten 9.57.0, Npgsql 9.0.4). Move all three packages to 1.2.0 together, as for any release. The
public API only gains members: `ApplicationPath` on both options classes, `MartenStudioOptions.ProjectionThresholds` and
the record `ProjectionLagThreshold`, and `PostgresStudioPage.Connection`. What a 1.1 host notices without changing a
setting:

- **`CorrectProgression` can also remove an orphaned progression row.** In 1.1 it advanced the high-water mark and
  corrected progression; 1.2 adds Remove on a progression row the daemon no longer reads, after a confirmation, re-checked
  at the click and audited (events 9225 and 9226). The write policy is asked with capability `CorrectProgression` about the
  database as a whole (`TenantId = null`), as for its other operations, and cannot tell them apart: a host that granted
  `CorrectProgression` (or `All()`) and does not want removals refuses `CorrectProgression` in its
  `WriteAuthorizationPolicy`.
- **Navigation stays inside the circuit.** Links, Back and Forward inside the studio are no longer page fetches, so host
  JavaScript initializers no longer receive `enhancedload` for moves inside it. Back or Forward to an address that names
  another store, database or tenant moves the studio there, as a click does, when the visitor's listings carry it.
  `AppContext.SetSwitch("PostgresStudio.DisableNavigationInterception", true)` restored 1.1's navigation until 1.3, which
  removed the switch (see "1.2 → 1.3" above).
- **`AcceptedChecks` (all three packages) and `ProjectionThresholds` are read once**, when the options are validated at
  startup; a change made to the resolved options afterwards is not seen. 1.1 read `AcceptedChecks` live.
- **The Overview shows the selected store's card**, and the new Stores page lists every store when you may see two or
  more.
- **C18 can now report pools of data sources the host named** (`NpgsqlDataSourceBuilder.Name`), which 1.1 left out. It
  is Info, so the opt-in health check's status does not change because of it.
- **Evidence spells every moment one way** (`2026-11-04 08:57:03 UTC`). Match on codes, never on text.
- **The .NET 11 Debug workaround is no longer needed** (`-p:EnableHotReloadInRuntimeConfigDevFile=false`). At a sub-path
  mount the studio maps `{Path}/_framework/{**file}`, which serves the host's own framework files to a visitor the host
  would serve them to; at a root mount it maps nothing.

# Upgrading to 1.1

## 1.0 → 1.1: the floors moved

1.1 needs **Marten 9.57.0 or later** and **Npgsql 9.0.4 or later**. Move all three packages to 1.1.0 together, as for any
release, and move the two dependencies first if your application is below either floor.

- **Why Marten 9.57.0.** Marten 9.57.0 fixes stream compaction replacing the whole history of a stream that folds to
  nothing with an empty snapshot (JasperFx/marten#5797), which Marten Studio's Compact stream exposed on every host below
  it. Raising the floor removes the hazard instead of guarding it. The floors now follow the latest releases, on the expectation that
  consumers keep up; a Marten 9.x above 9.57.0 is fine, and the package still states no upper bound. Because the floor has
  everything the studio ever lit up at run time (Marten 9.31 to 9.53), the Configuration page's "Newer than the studio's
  floor" table is empty and the screens no longer say "needs Marten 9.xx" or "this host runs ...".
- **Why Npgsql 9.0.4.** It is the Npgsql Marten 9.57.0 itself references, so a Marten Studio host never has two. A
  **Postgres Studio host that registers its own `NpgsqlDataSource` on Npgsql 8 must move to 9.0.4 or later**; Npgsql 9 keeps
  the connection string keywords and the pool metrics Postgres Studio reads (`db.client.connections.*`; Npgsql 10 renamed
  them and is read too), and from 9.0 its `Connection Lifetime` defaults to 3600 seconds.
- **K10 is retired.** The check (a schema name that breaks every apply after the first) went with the Marten 9.57 floor,
  whose Weasel reads such an index back. A host that accepted it in `AcceptedChecks` keeps starting: the entry is ignored,
  with one Information line saying it can be removed. Its row stays in [health/codes.md](health/codes.md).
- **New checks may fire on upgrade.** C16 (a server or proxy ends idle pooled connections first), C17 (pooling switched
  off), C18 (several pools to one database in this process), S11 (server certificate not verified), S12 (a credential is
  readable from configuration) and S13 (driver settings that copy values into logs or exceptions), all described on
  [Database › Connection](connection.md#the-checks-it-surfaces), and, in Marten Studio, E09 (a stream compacted into an
  empty snapshot, behind "Run deeper checks"). They are findings about your configuration, not regressions; accept one with
  `AcceptedChecks` and a reason, or fix it.
- **The opt-in health check may turn `Degraded` on upgrade.** If you registered the ASP.NET Core health check
  ([health.md](health.md#export)), any Warning from a new check changes its status from `Healthy`
  to `Degraded`, as for any Warning ([stability.md](stability.md)). S11 `prefer` is a Warning on a detected cloud
  platform and fires at Npgsql's default `SSL Mode=Prefer`, so most Amazon RDS and Azure hosts will see it; C16, C17 and
  S12 can do the same. Accept the check or fix the setting before the first probe that gates a deployment on the status.
- **S05 now stands down behind a pooler, except for `SSL Mode=Disable`.** Behind a Neon pooled endpoint, Supabase's
  port 6543, any port 6432 (PgBouncer, Azure's built-in included) or an Amazon RDS Proxy, the server's TLS row is the
  pooler's own connection, so S05 is "not applicable" there; the Connection page's TLS hint judges `SSL Mode` instead. A
  port alone only means the data source may reach a pooler: with `SSL Mode=Disable` and a remote host S05 still fires,
  from the setting, and S11's `require` variant likewise fires from `SSL Mode=Require` behind a pooler.
- **PostgreSQL 14** is no longer tested. It still works.
- **The connection history (#54).** A process that stops being the sampler now offers the samples it still holds to the
  store, oldest first, instead of leaving them. A custom `IPostgresConnectionHistoryStore` should make its append
  idempotent (`on conflict (server, taken_at) do nothing`), because the same sample can reach it again.
- **Nothing else you need to act on changes.** No public API moved.

# Upgrading to 1.0

1.0 is the first release with a stability promise ([stability.md](stability.md)); the API it ships is 0.11's, extended.
Nothing was renamed or removed, so an application on 0.11 upgrades by moving all three packages to 1.0.0 together - and
any central `PackageVersion` for `PostgresStudio` or `PostgresStudio.Diagnostics` with them, or `AddMartenStudio()` and
`AddPostgresStudio()` refuse to start and say which version to pin. What follows is what an upgrade can notice.

## What behaves differently

- **The studio uses at most two of each pool's connections at a time** (`MaxStudioConnections`, new, 2 by default, 1 to
  100). A visitor who starts several heavy reads at once - a deep health sweep beside the SQL console beside the database
  browser - now waits for one of the two, at most `QueryTimeout`, instead of taking a third; a read that waits longer fails
  with a sentence that names the option. Raise it if your pool is large and your visitors are many; it never needs to be
  larger than the application can spare.
- **The studio's sessions have a name of their own.** For the length of each of its read-only transactions, a session the
  studio uses carries `<your Application Name> (Marten Studio)` or `(Postgres Studio)` in `pg_stat_activity`. A monitor or
  a dashboard that matches your application's exact `application_name` sees the studio's sessions under the new name;
  set `StudioApplicationName = ""` to keep your own, or give it a name of your choosing. A pooled connection always goes
  back to your application with its own name.
- **Pasting a plan is open to every visitor the studio admits.** Query › Plan is in both studios' rails whatever `RunSql`
  and `ReadOnly` say: a pasted plan, in JSON or `EXPLAIN`'s text format, is only read, and nothing runs against your
  database. Running a statement for its plan keeps the console's gate.
- **Marten Studio's Schema screen** shows sizes from the catalog's page counts, marked `~`, rather than measured, so it
  never waits behind a migration's lock.
- **New plan insights** on Query › Plan, P22-P25 judged from the statement's text before anything runs, and the others
  the changelog lists. A plan insight is never a health check: the Health page, its badge and the opt-in health check do
  not count them.

## What is new and opt in

- `AddPostgresConnectionHistory()`: a history of the server's connections by application, for Database › Sessions ›
  Holders ([diagnostics.md](diagnostics.md#connection-history)). Its sampler keeps **one connection per server open**,
  outside `MaxStudioConnections`: count it in your pool size before you turn it on.
- `PostgresDiagnosticsSnapshot.FindingsByCheck()` and the single-database `GetSnapshotAsync(request, cancellationToken)`
  on `IPostgresDiagnostics` ([diagnostics.md](diagnostics.md)).

## From a release older than 0.11

Read the changelog's sections from your release up; the ones that need action are:

- **0.11** split the diagnostics engine into `PostgresStudio.Diagnostics`. A host that compiled against 0.10's
  `IPostgresDiagnostics` or its read model recompiles (the types kept their names and namespace, in another assembly);
  `AddPostgresDiagnostics()` is refused beside either studio, which registers the engine itself.
- **0.5** made Marten Studio two packages; `MartenStudio` brings `PostgresStudio` at exactly its own version. Upgrading
  from before 0.5 also resets the health page's mutes once: a list kept by an earlier release is dropped, not migrated.
