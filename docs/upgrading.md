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
