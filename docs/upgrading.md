# Upgrading to 2.0

## 1.x → 2.0: one product, Data Studio

<!-- beta: remove at 2.0.0 -->
**2.0 is in beta** (`2.0.0-beta.1`), published for trial: names, addresses and options may still move before `2.0.0`
([stability.md](stability.md)). Every package is a prerelease, so install with `--prerelease`, and pin the beta version
exactly.

2.0 turns Marten Studio, Postgres Studio and KStudio into one product, Data Studio, with three modules: Marten, Postgres
and Kafka. Packages, registration, options, types, addresses, log categories and the studio's wording all changed, on
purpose and without a bridge: no 1.x name, call or address is kept as an alias, and no redirect leads from an old address
to a new one. [docs/stability.md](stability.md) says what is still promised (capabilities off by default, authorization
never widened, finding codes and log event ids that keep their meaning), and the tables below name every break. What a
host relies on for safety did not change, with one exception it must act on: a module's own `ReadOnly` no longer turns the
Kafka module's capabilities off (set `DataStudioOptions.ReadOnly`; see "The read-only switches"). There is no
step-by-step path through 1.3.x: go to 2.0.0 in one move.

### What to do, in order

1. **Replace the packages** (the table under "Packages"). Remove `MartenStudio`, `PostgresStudio`,
   `PostgresStudio.Diagnostics`, `KStudio`, `KStudio.Core` and `KStudio.Wolverine` and add the 2.0 ids. If your repository
   keeps a central `PackageVersion` for any of them, rename it and keep every `DataStudio*` package at one version: each
   depends on exactly its own version of the ones below it and checks that at startup. While 2.0 is in beta that version
   is `2.0.0-beta.1` and installing needs `--prerelease`. <!-- beta: remove at 2.0.0 -->
2. **Replace the registration.** One call registers the shell, and one call per module adds a module to it:

   ```csharp
   using DataStudio;
   using DataStudio.Marten;

   // 1.x: builder.Services.AddMartenStudio(o => { o.Path = "/ops"; o.AuthorizationPolicy = "ops"; o.Capabilities = ... });
   builder.Services
       .AddDataStudio(studio => { studio.Path = "/ops"; studio.AuthorizationPolicy = "ops"; })
       .AddMarten(marten => marten.Capabilities = MartenCapabilities.All());
   ```

   `AddPostgresStudio(...)` becomes `.AddPostgres(...)` (with `using DataStudio.Postgres;`) and `AddKafkaStudio(...)` becomes
   `.AddKafka(...)` (with `using DataStudio.Kafka;`), in any order: the modules appear in the order you wrote the calls.
   `AddMarten()` brings the Postgres module's pages over the stores' databases, as part of the Marten module (one tile, one
   rail), so a Marten host does not call `.AddPostgres()`, and the two together are refused at startup. A worker with no UI
   keeps `services.AddPostgresDiagnostics()`.
3. **Replace the mapping.** `app.MapMartenStudio(...)` and `app.MapPostgresStudio(...)` become `app.MapDataStudio(...)`, which
   serves every module; there is no map call per module, and `RequireAuthorization()` and `AllowAnonymous()` on what it
   returns work as before. The default mount is `/studio`. A pattern you gave `MapMartenStudio("/ops/marten")` is the
   mount now and each module adds its own segment below it, so `MapDataStudio("/ops")` serves `/ops/marten/...`; keeping the
   old pattern would give `/ops/marten/marten/...`.
4. **Move the options.** The shell's options moved to `DataStudioOptions`; a module's options keep the rest (the table
   under "Options"). Code that set `MartenStudioOptions.Path`, `Title`, `ApplicationPath`, `AuthorizationPolicy` or
   `RefreshInterval` sets them on `DataStudioOptions`, and a Marten and Kafka host now has one `AuthorizationPolicy` and one
   `Path` where it had two.
5. **Rename the types you name** (the table under "Types"). The changes are mechanical: `MartenStudioCapabilities` is
   `MartenCapabilities`, `PostgresStudioResource` is `PostgresResource`, and so on.
6. **Decide the read-only switch** (below). A host that relied on a module's `ReadOnly` to keep Kafka read-only sets
   `DataStudioOptions.ReadOnly`; without it, Kafka's capabilities are on as the host configured them, and a Kafka module
   with a capability on and neither Kafka policy set refuses to start.
7. **Update everything that holds an address:** bookmarks, runbooks, links in alerts and tickets, proxy and
   Content-Security-Policy rules, health-check probes and the `ApplicationPath` or return links your own pages build (the
   table under "Addresses").
8. **Update what matches the studio's names:** a log filter on a category, a rule that reads the studio's application name in
   `pg_stat_activity` (the tables under "Logs" and "Application name"), the health-check registration (the table under
   "Registration and mapping") and a probe or `HealthCheckOptions.Predicate` that selects the health check by its default
   name.
9. **Run it** and open `{mount}`. With one module it opens that module's home; with two or more it shows one tile per
   module. A Marten host's Postgres pages are part of the Marten module and have no tile of their own.

### Packages

| 1.x | 2.0 | What it is |
|---|---|---|
| (inside `PostgresStudio`) | `DataStudio` | The shell: mount, layout, rail, front page, Activity and the static assets. ASP.NET Core and nothing else |
| `PostgresStudio` | `DataStudio.Postgres` | The Postgres module: the database pages, the SQL console and the health pages |
| `PostgresStudio.Diagnostics` | `DataStudio.Postgres.Diagnostics` | The diagnostics engine, with no UI and no ASP.NET Core; usable alone |
| `MartenStudio` | `DataStudio.Marten` | The Marten module; it brings the Postgres module over the stores' databases |
| `KStudio` | `DataStudio.Kafka` | The Kafka module |
| `KStudio.Core` | `DataStudio.Kafka.Core` | The Kafka engine |
| `KStudio.Wolverine` | `DataStudio.Kafka.Wolverine` | Wolverine cluster discovery for the Kafka module |

The 1.x ids get no release after 1.3.1. Once 2.0.0 is published, each is marked deprecated on nuget.org, with its 2.0 id as
the alternative. References:
`DataStudio.Marten` → `DataStudio.Postgres` → `DataStudio` and `DataStudio.Postgres.Diagnostics`;
`DataStudio.Kafka` → `DataStudio` and `DataStudio.Kafka.Core`; `DataStudio.Kafka.Wolverine` → `DataStudio.Kafka.Core`. The
Kafka module references no Postgres module, no engine and no Npgsql: a host with only Kafka gets none of them. The static
assets moved with the shell, from `_content/PostgresStudio/` to `_content/DataStudio/` (`css/studio.css`, `js/studio.js`).

### Registration and mapping

| 1.x | 2.0 |
|---|---|
| `services.AddMartenStudio(o => ...)` | `services.AddDataStudio(studio => ...).AddMarten(marten => ...)` |
| `services.AddPostgresStudio(o => ...)` | `services.AddDataStudio(studio => ...).AddPostgres(postgres => ...)` |
| `services.AddKafkaStudio(o => ...)` | `...AddKafka(kafka => ...)` on the same builder |
| `app.MapMartenStudio(pattern?)`, `app.MapPostgresStudio(pattern?)` | `app.MapDataStudio(pattern?)` |
| `services.AddPostgresDiagnostics(...)` | unchanged: the engine alone, refused beside `AddPostgres()` or `AddMarten()` |
| `services.AddHealthChecks().AddMartenStudio(...)` | `services.AddHealthChecks().AddMartenDiagnostics(...)`; the default check name is `data-studio-marten` (1.x: `marten-studio`) |
| `services.AddHealthChecks().AddPostgresStudio(...)` | `services.AddHealthChecks().AddPostgresDiagnostics(...)`; the default check name is `data-studio-postgres` (1.x: `postgres-studio`) |
| `k.UseWolverineKafka(...)` in `AddKafkaStudio` | unchanged: `kafka.UseWolverineKafka(...)` in `AddKafka` (namespace `DataStudio.Kafka`) |

One shell serves an application, and `AddDataStudio()` and each `Add…()` are idempotent. There is no `MapKafkaStudio()`, and
there never was.

### Types and options

| 1.x | 2.0 |
|---|---|
| `MartenStudioOptions` | `MartenOptions` (`DataStudio.Marten`) |
| `MartenStudioCapabilities` | `MartenCapabilities` |
| `PostgresStudioOptions` | `PostgresOptions` (`DataStudio.Postgres`) |
| `PostgresStudioCapabilities` | `PostgresCapabilities` |
| `PostgresStudioResource` | `PostgresResource` |
| `PostgresStudioLink`, `PostgresStudioPage` | `PostgresPageLink`, `PostgresPage` |
| `KafkaStudioOptions`, `KafkaStudioCapabilities` | `KafkaOptions`, `KafkaCapabilities` (`DataStudio.Kafka`) |
| `MartenStudioOptions.MaxInlineDocumentBytes` | `MartenOptions.MaxInlineValueBytes` |
| the `KStudio` namespace (`KafkaClusterBuilder`, `KafkaProfile`, `KafkaScanLimits`, ...) | `DataStudio.Kafka`, for all three Kafka assemblies |
| the `MartenStudio` and `PostgresStudio` namespaces | `DataStudio.Marten` and `DataStudio.Postgres` (the module and the engine share the second) |

`MartenStoreResource`, `ProjectionLagThreshold`, `KafkaTopicResource`, `PostgresDiagnosticsOptions`, `IPostgresDiagnostics`
and the diagnostics read model keep their names, and only their namespaces change. The calls on the builder (`AddDataStudio`,
`AddMarten`, `AddPostgres`, `AddKafka`) are all in namespace `DataStudio`, whichever package declares them.

What the shell's `DataStudioOptions` holds. A module's options no longer have these, except `ReadOnly`, which a module keeps
for itself (below):

| Option | Default | Was on |
|---|---|---|
| `Path` | `/studio` | each studio's options (`/marten`, `/postgres`) |
| `Title` | `Data Studio` | each studio's options |
| `ApplicationPath` | `null` | each studio's options |
| `AuthorizationPolicy` | `null` | each studio's options, and `KafkaStudioOptions` had none of its own |
| `ReadOnly` | `false` | each studio's options, with a different meaning: see below |
| `RefreshInterval` | 5 seconds | each studio's options |

Everything else (`StoreAuthorizationPolicy`, `SourceAuthorizationPolicy`, `WriteAuthorizationPolicy`, `Capabilities`, the page
sizes, `QueryTimeout`, `SqlConsoleRole`, `HealthInterval`, `AcceptedChecks`, the Kafka clusters and topics, and so on)
stays where it was, under the new type's name. A module's options no longer have `Path`, `Title`, `ApplicationPath`,
`AuthorizationPolicy` or `RefreshInterval`.

### The read-only switches

1.x had one `ReadOnly` per studio, and `MartenStudioOptions.ReadOnly` also turned the Kafka capabilities off, because Kafka
shared its studio's switch. 2.0 has two:

- **`DataStudioOptions.ReadOnly`** turns every module's capabilities off, the mutating ones and the reads authorized like
  writes. It is the switch for the whole application.
- **A module's own `ReadOnly`** (`MartenOptions.ReadOnly`, `PostgresOptions.ReadOnly`) turns off that module's capabilities
  only. **It no longer turns Kafka off.** Kafka has no switch of its own and obeys the shell's.

A host that set `MartenStudioOptions.ReadOnly` or `PostgresStudioOptions.ReadOnly` to `true` and runs Kafka beside it sets
`DataStudioOptions.ReadOnly = true` as well. Otherwise Kafka's capabilities (`SearchRecords`, `ReadPayloads`,
`ExportRecords`, `LiveTail`), if the host turned them on, are on: under the Kafka policies when the host set them, and with
neither Kafka policy set the application now refuses to start (the Kafka startup guard reads only the shell's switch). The
capability chip and each disabled control name the switch that is on.

### Addresses

Every module's pages sit under one segment of its own below the mount (default `/studio`):

| Page | 1.x | 2.0 |
|---|---|---|
| Front page | none | `{mount}`: one tile per module; with one module, a redirect to its home |
| Activity | `/marten/activity`, `/postgres/activity` | `{mount}/activity` (the shell's, with no scope selector) |
| Postgres pages | `/postgres/health`, `/postgres/query`, ... in a Postgres host; `/marten/health`, `/marten/query`, ... in a Marten host | `{mount}/postgres/health`, `{mount}/postgres/query`, `{mount}/postgres/database`, ... in both |
| Marten pages | `/marten/documents`, `/marten/events/streams`, `/marten/projections`, ... | `{mount}/marten/documents`, `{mount}/marten/events/streams`, `{mount}/marten/projections`, ...; the Marten overview is `{mount}/marten` |
| Kafka pages | `/marten/kafka/...` or `/postgres/kafka/...` | `{mount}/kafka/...` |

**A 1.x address is a 404.** There is no redirect from the old addresses, so bookmarks, links in tickets and alerts, and any
allow-list that names a page must be made again. In a Marten host the Postgres module's home, `{mount}/postgres`,
redirects to `{mount}/marten`. Scope and ids still travel in the query string (`?store=&db=&tenant=`). A page assembly has one
root segment, never `activity`, `_blazor`, `_framework`, `_content` or `opaque-redirect`. The diagnostics API's page paths
(`IPostgresDiagnostics.GetStudioPath`, `PostgresPageLink` and a finding's `StudioPath`) are relative to the mount, with no
leading slash, and start with `postgres/` (`postgres/health`): a host that builds a link joins its own mount to them
(`/studio/` + `postgres/health`), where 1.x paths were relative to the mount and started at the page (`health`).

### Logs

Event ids, levels and property names did not change, with three exceptions: event 9230 gained three option-name properties
(`BrowsableSchemasOption`, `SqlConsoleRoleOption` and `BrowseDatabaseOption`), fourteen Debug lines no longer carry a
`{Product}` property, and a Postgres source's events now carry `{TenantId}` with the value `(all)`. What changed is the text
and the category:

| | 1.x | 2.0 |
|---|---|---|
| Wording | one template per studio for most events, naming the product ("Marten Studio", "Postgres Studio", "KStudio") and a database a "database" or a "store" | one template per event, naming "Data Studio" and calling a source a "source" |
| Categories | `PostgresStudio.*`, `MartenStudio.*`, `KStudio.Core.*`, and so on | `DataStudio.*` (`DataStudio.Postgres.Services...`, `DataStudio.Kafka.Core.Scans`) |

**Match on the event id, never on the text or the category's old root.** A `Logging:LogLevel` entry for a 1.x category has to
name the new one, or it silently stops applying: `PostgresStudio` becomes `DataStudio.Postgres`, `MartenStudio` becomes
`DataStudio.Marten`, `KStudio` becomes `DataStudio.Kafka` (and `KStudio.Core` `DataStudio.Kafka.Core`). The shell logs under
`DataStudio.Services.*`, so a `PostgresStudio` filter that also covered the shell's lines needs `DataStudio` to keep doing
so.
Debug lines that logged an address's scope (a store, database or tenant a link named) did so without a cap: they are capped
now, like every other value a visitor can shape.

### Application name

The studio's own database sessions carry the application-name suffix **"Data Studio"** ("Data Studio Diagnostics" for the
engine alone, with no studio): `orders-api (Data Studio)` where 1.x said `orders-api (Marten Studio)` or
`orders-api (Postgres Studio)`. A monitor, an alert or a `pg_stat_activity` filter keyed on the old suffix no longer matches,
and `StudioApplicationName` still sets the name yourself. A worker with the engine alone shows `(Data Studio Diagnostics)`
where 1.x showed `(Postgres Studio Diagnostics)`, and the connection history's own session shows `(Data Studio history)`
(1.x: `(Marten Studio history)`, `(Postgres Studio history)`). A health snapshot's producer is
`Data Studio {release}`; a snapshot a 1.x studio wrote still compares, with a notice that its producer was 1.x.

### What a Kafka broker sees

The Kafka module's clients carry Data Studio's names: a scan consumer's group id starts with `data-studio-` (1.x: `kstudio-`),
a client id ends with `.data-studio` (1.x: `.kstudio`) and an exported file is named `data-studio-{cluster}-{part}-{timestamp}Z.{ext}`
(1.x: `kstudio-…`). An operator whose ACLs name the `kstudio-` group prefix adds a `PREFIXED` ACL for `data-studio-`, and
`DESCRIBE` is enough. Reading needs no group ACL: without it the broker's authorizer log shows one refused lookup per scan,
and about one a second while a consumer-lag read's reader is alive (up to `AdminIdleTimeout`). A host
that set `GroupIdPrefix` itself is unaffected, and a quota or monitor keyed on the old client id suffix must name the new one.
The Kafka sample's reuse variable is `DATASTUDIO_KAFKA_SAMPLE_REUSE` (1.x: `KSTUDIO_SAMPLE_REUSE`).

### Wording, assets and what the studio does not rename

The studio's own sentences say Data Studio, name the module a sentence belongs to, and tell the option that turns something on
by its 2.0 name (`MartenOptions.Capabilities.EditDocuments`), so a screenshot or a runbook quoting a sentence needs updating.
The Health page and codes still say what they said. The static assets are at `_content/DataStudio/`, and at a sub-path mount
`{Path}/_content/DataStudio/`: a Content-Security-Policy, WAF rule or proxy allow-list that names `_content/PostgresStudio/` must
name the new path. Not renamed: the `ms-` CSS prefix, the `martenStudio` script global, the `ms_theme` and `ms_tz` cookies and
the storage key of the Health page's mutes (so a visitor's theme and mutes survive), and the `schema` value of a health
snapshot's JSON (`postgresstudio-diagnostics`, so a 1.x snapshot still compares).

### What needs no change

Finding codes and [health/codes.md](health/codes.md) (including `AcceptedChecks` entries, the opt-in health check's codes and
every code a host's own alerting keys on); capability names and what each allows; the three authorization layers and the
policies you wrote (`MartenStoreResource` and `KafkaTopicResource` are unchanged, and a handler for `PostgresStudioResource`
takes `PostgresResource`); the audit and its event ids; the diagnostics API and its read model, apart from the namespace and
the page paths above; the Kafka engine's behaviour; the floors (Marten 9.57.0, Npgsql 9.0.4, Confluent.Kafka 2.14.0,
WolverineFx.Kafka 6.61.0); `net10.0` as the only target; PostgreSQL 15 to 19. No package was added to or removed from the
package budget: `Microsoft.AspNetCore.App.Internal.Assets` moved from the Postgres module to the shell, which is the one package
every host gets.

# Upgrading to 1.3

## 1.2 → 1.3: the navigation switch is gone

- **`PostgresStudio.DisableNavigationInterception` is removed.** 1.2 introduced it as a temporary AppContext switch that
  restored 1.1's navigation (an enhanced-navigation fetch per page) and promised to remove it in 1.3. A host that still
  sets it gets 1.2's default navigation, handled inside the studio's circuit: the setting is read by nothing and is
  harmless, and the `AppContext.SetSwitch(...)` call can be deleted. Removing it changed no option, type or public API.

# Upgrading to 1.2

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
