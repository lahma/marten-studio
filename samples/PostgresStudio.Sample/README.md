# Postgres Studio sample host

A small ASP.NET Core host with no Marten that registers its own `NpgsqlDataSource`, adds Postgres Studio, and mounts
it at `/postgres`. It has the shape of a host that keeps one database per tenant beside an admin database (issue #8's
host B):

- **The admin database** is the application's unkeyed `NpgsqlDataSource`, which Postgres Studio finds by itself and
  lists as the source `default`. It holds Quartz.NET's job store (`quartz`), a schema of catalog edge cases
  (`legacy`) and the application's own schema, `studio_sample`, with `app_settings` and a directory of the tenants.
- **Three tenant databases**, `tenant_a`, `tenant_b` and `tenant_c`, created on the same server. Each has
  `customers`, `orders` and `order_lines` in `public`, of three different sizes, and one foreign key with **no index on
  purpose** (`orders.customer_id`), so the health checks have something real to find. They are listed under the source
  `tenants` by the host's own `IPostgresDatabaseSource` (`SampleTenantDatabases`), which reads the directory.

```powershell
dotnet run --project samples/PostgresStudio.Sample --urls http://localhost:5211
```

With no `ConnectionStrings:Postgres` configured it starts a throwaway `postgres:17-alpine` through Testcontainers and
prints a banner saying so. It never falls back to `localhost` silently. Unlike the Marten sample it starts a **fresh**
container every run; set `MARTENSTUDIO_PG_REUSE=true` to reuse one (a container of its own: it never attaches to the
Marten sample's or the integration suite's).

Pointed at a database of your own, that database is the admin database, and the sample writes nothing to it unless it
is started with `--allow-data-generation`. Without the switch it starts read-only, says so in one line, and the studio
shows only what is already there: no tenants, unless an earlier start with the switch created them. With the switch it
creates the three tenant databases beside it, which needs a role with `CREATEDB`, and the `quartz`, `legacy` and
`studio_sample` schemas in it (`dotnet run --project samples/PostgresStudio.Sample -- --allow-data-generation
--ConnectionStrings:Postgres "…"`). A database of one of those names that the sample did not create is left alone and
not listed; so is a `quartz` or `legacy` schema it did not create.

Sign in at `/login` as `admin`, `ops` or `viewer` - the password is the user name.

| User | May open the studio | `BrowseDatabase` | `ViewDatabaseActivity` | `RunSql` |
|---|---|---|---|---|
| `admin` | yes | yes | yes | yes |
| `ops` | yes | yes | yes | no |
| `viewer` | yes | no | no | no |

All the capabilities are on (`PostgresStudioCapabilities.All()`); the write policy, `PostgresStudioCapability`, is
asked about each one by name against a `PostgresStudioResource` and answers from the user's scopes - `BrowseDatabase`,
`ViewDatabaseActivity` and `RunSql` each have a scope of their own, and `ViewDefinitions`, which the demo's users hold
no scope for, is refused: they read definitions through `BrowseDatabase`, which implies it. The seeding is
idempotent: a second start against the same server writes nothing.
