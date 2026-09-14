# Marten Studio: the security model

Marten Studio is an admin UI that runs *inside* your application, with your application's own
`IDocumentStore` and your application's own database credentials. It can read and edit every document in
every Marten store in the process, archive event streams, control the projection daemon, apply schema
migrations and run SQL. Nothing about that is incidental — it is what an admin UI is for — so the whole
design is about making every one of those powers something you turned on deliberately, refused in the
place where the operation happens, and recorded whether it succeeded or not.

This page is the long form of the README's *Security model and limits*. It says what the studio
guarantees, what it merely makes harder, and what you have to configure yourself.

## Authentication is never the studio's

The studio authenticates nobody and ships no login page. It asks your application's authorization stack
questions, using the visitor your application already established. In a rendered studio that visitor
comes from `AuthenticationStateProvider` rather than from an `HttpContext`, because a rendered studio is
a Blazor circuit and the request that created it is long gone by the time a page asks for anything.

## Three layers, two switches

| | Option | Decides | Asked |
|---|---|---|---|
| 1 | `AuthorizationPolicy` | Who may open the studio at all | Once, by the authorization middleware, on every studio endpoint |
| 2 | `StoreAuthorizationPolicy` | Which store, database and tenant | On **every** data call, resource-based against `MartenStoreResource` |
| 3 | `WriteAuthorizationPolicy` | Each mutating call | Before every mutation, against the same resource with the capability named |
| — | `Capabilities` | Which mutating operations exist at all in this process | In the service layer, before layer 3 |
| — | `ReadOnly` | Nothing mutating exists | Before everything |

Layer 1 can equally be `RequireAuthorization()` on the builder `MapMartenStudio()` returns. Both cover
the studio's pages **and** its Blazor circuit, which is the half that matters most: a page that answers
401 while `POST /_blazor/negotiate` answers 200 is a studio anyone can drive. The studio therefore stamps
no `AllowAnonymous` of its own on the circuit; only the static assets under `_content/MartenStudio/` opt
out, and only when no studio policy is configured, so that an application with a fail-closed
`FallbackPolicy` can still load a stylesheet.

Layer 2 filters listings rather than annotating them. A tenant you may not see is one you never learn
exists — the *count* of tenants in a process is itself something a tenant should not learn.

With no store policy configured, layers 2 and 3 ask nothing and everything passes. That is deliberate:
an application that never set the option must be exactly the application it was.

### `MartenStoreResource`

```csharp
public sealed record MartenStoreResource(
    string StoreName,           // "default", or the marker interface name of an ancillary store
    string DatabaseIdentifier,  // Marten's database identity - never a connection string
    string? TenantId,           // the selected tenant, or null for a view spanning all of them
    string? Capability);        // "EditDocuments", "RunSql", ... - or null for a read
```

One `AuthorizationHandler<TRequirement, MartenStoreResource>` answers for the scope selector, the page
frame and every data call. `Capability` is `null` for a read and carries the capability's own name for a
write, so a single handler can be as coarse or as fine as you want.

### The startup guard

An application that maps the studio and says nothing about authorization **does not start**. The check
runs in `IHostedLifecycleService.StartingAsync` — before the web host binds a listener — and runs again
in `StartedAsync` to catch the hosting shapes that map their endpoints from inside the pipeline
(`Startup.Configure`, `UseEndpoints`, a `MapGroup`). An endpoint passes if it carries `IAuthorizeData`
or `IAllowAnonymous`; the whole check passes if the application has a non-null
`AuthorizationOptions.FallbackPolicy`.

The message names the three ways to answer, and `AllowAnonymous()` is one of them — saying "serve this
to anyone" out loud is allowed, and is what the sample's `--anonymous` switch demonstrates. What is not
allowed is saying nothing.

One case is out of scope and always will be: a `MapMartenStudio()` made *after* the host has started —
from a hosted service, or from a lazily built `EndpointDataSource`. Both of the guard's passes have run
by then. There is no hook that would catch it without inspecting every endpoint on every request, which
is a cost the whole application would pay for a mistake nobody has made. Map the studio while the
application is being built.

## The capability model

Nine flags on `MartenStudioCapabilities`, every one `false` by default:

`EditDocuments`, `DeleteDocuments`, `ArchiveStreams`, `ManageDeadLetters`, `ControlDaemon`,
`RebuildProjections`, `CorrectProgression`, `ApplySchemaChanges`, `RunSql`.

`MartenStudioCapabilities.All()` is the one-line, greppable opt-in. `MartenStudioOptions.ReadOnly`
overrides all nine whatever they say.

This is Hangfire's [GHSA-7rq6-7gv8-c37h](https://github.com/advisories/GHSA-7rq6-7gv8-c37h) applied to
the write axis: the dashboard that is dangerous by default is the one somebody maps without reading the
docs. A freshly mapped Marten Studio is a read-only browser.

**The refusal lives in the service layer, not in the markup.** Hiding a button is a UI convenience; a
Blazor circuit is a long-lived object a client can drive, so every mutating service method begins with
`StudioCapabilityGuard.Require(…)` and throws `StudioCapabilityDeniedException` — whose message names
the exact option a host would set — before anything else happens. The order is always the same:

```text
capability guard  →  scope resolution (with the write policy, capability named)  →  the operation  →  audit
```

and the audit record is written on the failing paths too. A refusal you cannot see in a log is a
refusal you cannot investigate.

Every disabled control in the UI names the property that would enable it
(`MartenStudioOptions.Capabilities.EditDocuments`), because "not permitted" with nothing to act on is
the failure mode of every feature-flagged admin UI.

## The SQL console

`RunSql` is off by default like every other capability. What makes the console safe when it *is* on is
the transaction, not the parser.

### What the database guarantees

Every statement runs on a connection of its own as:

```sql
BEGIN;
  SET TRANSACTION READ ONLY;
  SET LOCAL statement_timeout = …;                    -- MartenStudioOptions.QueryTimeout
  SET LOCAL lock_timeout = …;                         -- 3 seconds
  SET LOCAL idle_in_transaction_session_timeout = …;  -- 60 seconds
  SET LOCAL ROLE …;                                   -- MartenStudioOptions.SqlConsoleRole, when set
  <your statement>
ROLLBACK;                                             -- always, on its own cancellation token
```

Every GUC — the role included — is set through `select set_config(@name, @value, true)` rather than a
`SET` statement, because `SET` takes no parameters and would mean interpolating text into SQL. The role
is *also* validated at startup against the shape of a plain Postgres identifier; both, not either.

So: **no `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE` or DDL survives**, whatever the parser thought. Three
timeouts, because there are three ways to hang: the query itself, a lock queue in front of somebody's
migration, and a transaction outliving the circuit that started it. And the row cap
(`MaxSqlConsoleRows`) stops the *reader* — it never appends a `LIMIT` to your statement, because
rewriting somebody's SQL would silently change an aggregate, a window function or a `LIMIT` they wrote
themselves.

### What the parser is for

`ReadOnlySqlGuard` allows only statements whose first significant token is `select`, `with`, `explain`,
`table` or `values`, and only one statement — a `;` outside a string, a quoted identifier, a
dollar-quoted body or a comment ends it, and anything but whitespace and comments after that is a second
statement. `EXPLAIN` gets one extra check, because `EXPLAIN ANALYZE` *runs* what it is given, so the
guard looks past the explain options and holds what follows to the same list.

**This is advisory and must never be treated as the security boundary.** It exists so that
`delete from mt_doc_customer` produces a sentence instead of Postgres' `25006: cannot execute DELETE in
a read-only transaction`. Every statement the parser lets through and the database refuses is working as
designed: `WITH x AS (DELETE … RETURNING *) SELECT * FROM x` is exactly that statement, and the
integration tests assert it comes back as `25006` with the table unchanged.

### The function denylist, and why it is not a guarantee

A read-only transaction is not a sandbox. Verified against PostgreSQL 17,
`select pg_terminate_backend(pg_backend_pid())` runs happily inside one and kills the backend; so do
`pg_read_file`, `lo_export`, `pg_ls_dir`, `dblink` and the session-level advisory-lock family, because
none of them is a *write* as far as SQLSTATE 25006 is concerned.

The console therefore refuses them **by name**. This is the complete list, from
`ReadOnlySqlGuard.DisallowedFunctions`, with the reason each one is given to the user. Matching is
case-insensitive against every identifier token in the statement, which also catches the
schema-qualified spelling — `pg_catalog.pg_terminate_backend` reaches the scanner as three tokens and it
is the bare name that is looked up. Quoted strings, quoted identifiers, dollar-quoted bodies and
comments are skipped, so they cannot trip it either.

| Function | Refused because it… |
|---|---|
| `pg_terminate_backend` | terminates other sessions |
| `pg_cancel_backend` | cancels other sessions' running statements |
| `pg_log_backend_memory_contexts` | makes another session dump its memory contexts to the server log |
| `pg_reload_conf` | makes the server re-read its configuration files |
| `pg_rotate_logfile` | rotates the server's log file |
| `pg_stat_reset` | throws away the statistics the planner and everyone else depends on |
| `pg_switch_wal` | forces a write-ahead-log switch |
| `pg_create_restore_point` | writes a named restore point into the write-ahead log |
| `pg_backup_start` | puts the server into backup mode |
| `pg_backup_stop` | ends the server's backup mode |
| `pg_promote` | promotes a standby to be a primary |
| `pg_read_file` | reads files from the server's filesystem |
| `pg_read_binary_file` | reads files from the server's filesystem |
| `pg_stat_file` | reads file metadata from the server's filesystem |
| `pg_ls_dir` | lists the server's filesystem |
| `pg_ls_logdir` | lists the server's log directory |
| `pg_ls_waldir` | lists the server's write-ahead-log directory |
| `lo_export` | writes a file to the server's filesystem |
| `lo_import` | reads a file from the server's filesystem into a large object |
| `lo_unlink` | deletes a large object, which no read-only transaction prevents |
| `dblink` | opens a connection to another database, outside this transaction and its read-only flag |
| `dblink_exec` | runs a statement on another database, outside this transaction and its read-only flag |
| `dblink_connect` | opens a connection to another database, outside this transaction and its read-only flag |
| `pg_advisory_lock` | takes a session-level advisory lock that outlives this transaction |
| `pg_advisory_lock_shared` | takes a session-level advisory lock that outlives this transaction |
| `pg_advisory_xact_lock` | takes an advisory lock Marten's own daemon contends for |
| `pg_advisory_xact_lock_shared` | takes an advisory lock Marten's own daemon contends for |
| `pg_sleep_for` | parks the session for an interval rather than doing work |
| `pg_sleep_until` | parks the session until a wall-clock time the statement timeout does not describe |
| `set_config` | changes a server setting, which is how every other limit here would be undone |
| `pg_notify` | sends a notification to other sessions listening on this database |

Two deliberate omissions: `pg_sleep` is allowed, because `statement_timeout` bounds it and it is how the
tests prove the timeout fires; `txid_current` is allowed, because it assigns a transaction id and
nothing more.

**That refusal is worth exactly what a parser's refusal is worth.** A determined caller can reach the
same function through a view, a `SECURITY DEFINER` wrapper or an alias, and this list will not see it.
The list is here so that a host which configured no role is still not one paste away from terminating
its own connections.

### The only true narrowing

**`MartenStudioOptions.SqlConsoleRole`.** A Postgres role that was never granted `EXECUTE` on those
functions cannot call them however they are spelled, and a role granted `SELECT` on only the tables you
meant can read only those. That is a guarantee the database makes; everything above it is a guarantee
the studio makes. If you enable `RunSql` on anything that matters, configure a role.

Without one, the console reads everything the store's own Postgres role can read — every table in the
database, Marten's or not, including tables belonging to other applications sharing it.

### Residual risks, stated plainly

- Reading is the point, and reading is not narrowed except by the role. The console is a read of the
  whole database.
- Denylisted functions are refused by name, not by permission. Assume the denylist is bypassable.
- `EXPLAIN` without `ANALYZE` is offered for a Mode A clause when `RunSql` is granted; it plans rather
  than runs, but it is still a statement sent to Postgres under this capability, and it is audited as
  one.
- A long-running `select` can still consume a connection and CPU until `statement_timeout` fires.
  `QueryTimeout` is the bound; set it to something you would tolerate.
- The statement text itself is written to the audit ring and to your `ILogger` (event ids 9204 and
  9205). If a query would contain a secret, that secret is now in your logs.

**Grant `RunSql` only to people you would give `psql` to.**

## Mode A: the Marten `where` clause

The Query page's other mode needs *no* capability, because it is a read against one document type — the
same thing the documents list does with a search box. The studio composes the statement itself:

```sql
select d."id", d."data"::text /*, d."tenant_id", d."mt_deleted" when the table has them */
from <schema>."mt_doc_<alias>" as d
where 1 = 1 and d."tenant_id" = @tenant and d."mt_deleted" = false
  and ( <your clause> )
  and d."tenant_id" = @tenant and d."mt_deleted" = false
order by … limit @limit
```

The tenant predicate and the soft-delete predicate are the Documents browser's, decided from the
document type's configuration reconciled against the physical columns, and the SQL the page shows is the
SQL that ran. Marten's own string query (`Query<T>("where …")`) is never used for this: it applies
neither predicate. The studio's terms are repeated *after* your predicate as well as in front of it,
because a parenthesised predicate only contains an `or` for as long as the parentheses hold — and
`1 = 1) or (1 = 1` was measured closing the studio's own bracket and returning four rows on a scope
narrowed to one tenant. The clause is held to a stricter shape than the console for the same reason it
needs no capability, and it runs inside the same read-only transaction the console does, with the same
`statement_timeout`, `lock_timeout`, `idle_in_transaction_session_timeout` and optional
`SqlConsoleRole`.

Six rules, all structural rather than semantic:

1. **Always:** no `;` outside a string, quoted identifier, dollar-quoted body or comment.
2. **Always:** the clause does not begin a statement of its own — `select`, `with`, `insert`, `update`,
   `delete`, `merge`, `truncate`, `drop`, `alter`, `create`, `grant`, `revoke`, `copy`, `explain`,
   `call`, `do`, `vacuum`, `analyze`, `set`, `reset`, `table`, `values`, `begin`, `commit`, `rollback`,
   `listen`, `notify`, `lock`, `refresh`, `reindex`, `cluster`, `comment`, `security`, `prepare`,
   `execute`, `deallocate`, `discard`, `import`, `checkpoint`.
3. **Always:** the clause's parentheses balance — a `)` that closes nothing would close the studio's own
   wrapper, and a `(` left open would swallow it.
4. **Always:** the tail must then be `[order by <sort list>] [limit <integer>] [offset <integer>]` and
   nothing else — each at most once, `order by` first, both counts plain non-negative integers, and the
   sort list free of `for`, `fetch`, `into`, `union`, `intersect` and `except` at parenthesis depth zero.
   Postgres accepts a locking clause between `ORDER BY` and `LIMIT`, and `order by 1 for update` is not a
   read.
5. **Always:** no function that reads, writes or waits outside the row — the console's denylist above,
   plus `pg_sleep`, `current_setting`, `query_to_xml`, `query_to_xml_and_xmlschema`, `table_to_xml`,
   `xpath`, `xpath_exists`, `nextval`, `setval` — and no `::regclass` or `::regproc` cast.
6. **Without `RunSql`:** no word that reaches another relation (`select`, `from`, `union`, `intersect`,
   `except`, `join`, `into`, `with`, `lateral`, `returning`, `copy`, `do`, `call`, `execute`).

A visitor who *may* run SQL — `RunSql` enabled **and** allowed by the write policy against this very
scope — gets rule 6 lifted, and only rule 6, because everything it refuses they could type into the
console instead. Asking only the capability and not the policy would hand subqueries to somebody the
write policy refuses the console to, which is the hole the policy exists to close.

Rule 6 has deliberate false positives: `from` is in the list, so `extract(year from …)`,
`substring(x from 1)` and `trim(both ' ' from x)` are refused. The list is structural, the escape hatch
is the capability, and a parser treated as a security boundary is how these features get CVEs. Every
refusal names `MartenStudioOptions.Capabilities.RunSql` as the thing that would lift it.

### What Mode A does **not** do

It does not make the clause guard the only thing between a visitor and the database, and it never did
the opposite either. The statement runs inside `BEGIN; SET TRANSACTION READ ONLY; …; ROLLBACK`, so a
clause that reaches a writing function through a view or a `SECURITY DEFINER` wrapper comes back as
`25006` and changes nothing. What a read-only transaction does *not* refuse is an advisory lock, a
`setval`, a `pg_terminate_backend` or a `pg_read_file` — which is precisely why the function denylist
applies to a `RunSql` holder here as well, unlike the nested-read rule.

It does not let you bind parameters. The clause is SQL text, so a literal you paste in is SQL too.

Every successful run is audited under 9204 `SqlExecuted` with the statement that ran; every refusal
under 9205 `SqlRejected`, and a scope refusal under 9203 `ScopeAuthorizationDenied` — so a clause that
should not have been typed is on the record either way.

## What the studio never does

- **Never starts a second projection daemon.** `store.BuildProjectionDaemonAsync()` is never called: it
  would start a daemon alongside the host's own and the two would fight over the same advisory locks
  until one hung. The only supported way to reach a running daemon is the DI-registered coordinator, and
  its absence is a *value* — "not hosted in this process" — rather than an error.
- **Never executes DDL on a read or navigation path.** `IMartenDatabase.AllSchemaNames()` and
  `AllObjects()` *apply migrations* — a single `AllSchemaNames()` call on an empty schema creates
  `mt_hilo` and `mt_get_next_hi`, proven live — so the studio calls neither. Schema names, declared
  indexes, managed tables and installed functions come from `IReadOnlyStoreOptions`, `IDocumentType` and
  the event store's own feature schema, and touch no connection. `Check`, `Preview` and `DDL` are behind
  buttons that say on the button what they may create. An apply is gated on `ApplySchemaChanges`, runs
  under `AutoCreate.CreateOrUpdate` and never `AutoCreate.All`, lists its destructive statements, and
  requires a typed confirmation that travels to the service rather than being re-supplied by the page.
- **Never writes document DML of its own.** There is no `mt_upsert_*` function to call — Marten 9.35
  installs none, and the upsert is inline SQL in its generated write path — and re-implementing that
  write path is how a UI corrupts a store. Writes go through a Marten session (`StoreObjects`,
  `UpdateExpectedVersion<T>`, `UpdateRevision<T>`, `Delete<T>(id)`, `UndoDeleteWhere<T>`), so metadata
  columns, soft-delete semantics, hierarchy discriminators and tenancy stay exactly as Marten defines
  them. Reads are parameterised SQL built in one place, `Internal/Sql/`, with identifiers quoted through
  a builder and values always parameters.
- **Never replaces your `StoreOptions.Logger`.** A live SQL tail would require it, which is exactly the
  "registration changes host behaviour" failure this project refuses. Every generated grid carries a
  "Show SQL" disclosure instead.
- **Never shows a connection string or a credential.** A database is identified by server, port, name
  and schema, everywhere — in the Overview, in the Configuration dump, in the audit log and in
  `MartenStoreResource.DatabaseIdentifier`.
- **Never serializes with a serializer of its own.** Everything round-trips through
  `store.Options.Serializer()`, so a document is written back with the settings Marten wrote it with.
- **Never adds a form.** There is no `<form>` and no `<EditForm>` anywhere in the studio's components;
  every mutation is a Blazor event over the circuit, and SignalR's own same-origin check is what stands
  between a cross-site page and that circuit. The studio's component endpoints therefore say
  `.DisableAntiforgery()`, so it imposes no middleware-ordering requirement on your application.

## What you have to configure

A checklist for a deployment that is not a demo:

1. **Authentication**, by your application. The studio adds none.
2. **`AuthorizationPolicy`, or `RequireAuthorization()` on the mapping.** The startup guard will not let
   you forget, but it will accept `AllowAnonymous()` — do not give it that answer.
3. **`StoreAuthorizationPolicy`**, if this process has more than one store, more than one database or
   more than one tenant and not everyone may see all of them.
4. **`WriteAuthorizationPolicy`**, if the people who may look are not exactly the people who may change.
5. **`Capabilities`**, one flag at a time, to the smallest set that makes the studio useful to the people
   who have it. `ReadOnly = true` in production and a separate read-write deployment is a legitimate
   shape.
6. **`SqlConsoleRole`**, if `RunSql` is on at all.
7. **`QueryTimeout`, `MaxPageSize`, `MaxSqlConsoleRows`, `ExactCountThreshold`** — the limits that keep
   an admin UI from becoming a denial of service against the database it exists to help you understand.
8. **`RebuildShardTimeout`**, if `RebuildProjections` is on. A rebuild drops the projection's tables
   *before* the timeout starts applying to the replay, so a timeout that is too short leaves the tables
   empty and the operation failed.
9. **Log retention for event ids 9200–9211.** The Activity page is a 500-entry in-memory ring that dies
   with the process; your own `ILogger` is the copy that survives a deployment.
10. **Browser security headers** — CSP, HSTS, frame options — are your application's, as they are for
    every other page it serves. The studio sets none and would be wrong to.

## Audit events

Ids **9200–9299** are reserved for Marten Studio. Twelve are in use, and the numbers are fixed: an
operator's log query is written against the number, and renumbering one later would silently change what
a saved query matches.

| Id | Event | Level |
|---|---|---|
| 9200 | `ActionPerformed` — user, action, target, store, database, tenant, outcome | Information |
| 9201 | `ActionFailed` — the same, with the reason | Information |
| 9202 | `CapabilityDenied` — the capability, and the option that would have allowed it | Warning |
| 9203 | `ScopeAuthorizationDenied` — the scope and the policy that refused it | Warning |
| 9204 | `SqlExecuted` — the statement, elapsed milliseconds, row count | Information |
| 9205 | `SqlRejected` — the statement and why it was refused | Information |
| 9206 | `SchemaChangeApplied` — the summary of what was applied | Information |
| 9207 | `ProjectionRebuildStarted` | Information |
| 9208 | `ProjectionRebuildFinished` — elapsed milliseconds and the outcome | Information |
| 9209 | `DaemonControlRequested` — the operation and its target | Information |
| 9210 | `StoreUnavailable` — a registered store that would not build, and why | Warning |
| 9211 | `DocumentWriteRoundTripDropped` — a save whose round trip dropped properties, counted and named | Warning |

## Reporting a vulnerability

Open a private security advisory on the repository rather than a public issue.
