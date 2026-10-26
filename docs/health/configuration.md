# Health: configuration (K)

These checks read the store's configuration and the host's environment, and nothing else: tier 0, no I/O.
They are shown to anybody the store policy lets see the scope they selected, because they say nothing the
Configuration page does not: its store card shows `AutoCreate`, its Events card both tracking switches, the
daemon mode and the leadership lock's scope, and its Runtime card the host's environment name. The one exception
is K04's host and port, which are evidence at the database-wide gate - the store policy for the database as a
whole - because that is what the Configuration page asks before it lists a database's server and port. K05-K07 read
the studio's own options the same way, in both studios. K08 is the one that reads the server too: three timeouts in
`pg_settings`, a catalog read, so it is shown at the database-wide gate. Back to the [index](../health.md).

## K01 AutoCreate in Production

**Warning.** The database's `AutoCreate` is `All` or `CreateOrUpdate` and the host's `IHostEnvironment`
says Production.

With either mode, the first request that touches a document or event table whose shape differs from the
configuration migrates it there and then, inside that request and as the application's own database role.
`AutoCreate.CreateOrUpdate` is **not additive**: Weasel drops every index on a Marten table that the
configuration does not declare, drops extra columns and alters changed ones. `AutoCreate.All` also lets it
drop and recreate an object whose change cannot be applied in place. In Production that is a schema change
nobody reviewed, run by whichever request came first.

`CreateOnly` (create what is missing, never alter) and `None` are not flagged. A host that registers no
`IHostEnvironment`, or one that names no environment, gets no finding: the check cannot tell which
environment it is in, and says so in its status row rather than guessing.

What to change:

```csharp
opts.AutoCreateSchemaObjects = AutoCreate.None;

builder.Services.AddMarten(opts => { /* ... */ }).ApplyAllDatabaseChangesOnStartup();
// or, with app.RunJasperFxCommands(args): dotnet run -- db-patch, review it, then db-apply
```

The apply still runs Weasel's `CreateOrUpdate`, which is not additive either - but it runs once, under
Marten's apply lock, at a moment you chose and can review with `db-patch`, rather than inside a request.

## K02 Extended progression tracking

**Info.** The store has async projections or subscriptions and
`opts.Events.EnableExtendedProgressionTracking` is off.

With it off, the daemon writes each shard's position to `mt_event_progression` and nothing else. With it
on, it also writes - whenever a shard changes state - the shard's status, why it was paused, the node
running it and, for a shard that failed, the kind of failure and the event it failed on. That is what the
Projections screen shows in a shard's status column, and the only record of why a shard stopped that
outlives the process that ran it.

What it does not do, so that nothing here promises it (checked in Marten 9.39.0 and 9.50.2, JasperFx 2.74.0
and 2.87.1):

- It does not make anything write `warning_behind_threshold` or `critical_behind_threshold`. Marten creates
  those two columns and neither writes nor reads them (marten#5173), so there are no per-projection lag
  thresholds to read.
- It does not make the `heartbeat` column a liveness signal. The heartbeat is written at state transitions
  only; JasperFx writes it periodically only when `opts.Projections.ExtendedProgressionHeartbeatInterval` is
  set (jasperfx#622), which its documentation calls a compatibility hatch rather than the recommended
  shape - each beat costs a pooled connection and a transaction per database per node, and, it says,
  nothing in JasperFx, Marten or CritterWatch reads the persisted heartbeat. The advisor does not use it to
  tell an idle shard from a stopped one, and this check does not suggest the interval.

Marten leaves the flag off by default on purpose - turning it on changes what every deployment writes - and
since Marten 9.31 the columns exist either way; the flag only decides whether they are written, so turning
it on migrates nothing. A store with no async projection or subscription gets no finding, and neither does
one whose projections the studio could not read.

```csharp
opts.Events.EnableExtendedProgressionTracking = true;
```

**K02 fires on Marten's defaults, by design.** A new store with an async projection and nothing else configured
gets this Info. It is an Info, not a Warning: nothing is wrong, and muting it in the browser is a reasonable
answer. `samples/MartenStudio.Sample` turns the switch on, so that the sample starts healthy and its landing
page's "Make it unhealthy" switches are what makes the advisor warn - the throwaway server's own
[S02](server.md#s02-settings-that-blind-or-endanger-the-store) Info aside.

## K03 Advanced async tracking

**Info.** The store has async projections or subscriptions and `opts.Events.EnableAdvancedAsyncTracking`
is off.

When the high-water mark is held up by a gap in the event sequence for long enough, the daemon decides the
gap will never fill and moves past it. With advanced async tracking on, Marten writes every skipped range to
`mt_high_water_skips` - the only way to find out later that an async projection never saw events whose
transaction committed after the daemon gave up on them. With it off, the mark simply moves.

Marten 9 turns it on by default, so a store with it off turned it off, or called
`StoreOptions.RestoreV8Defaults()`.

```csharp
opts.Events.EnableAdvancedAsyncTracking = true;
```

Turning it on adds `mt_high_water_skips` and its bookkeeping to the event schema, created by the next schema
apply.

## K04 HotCold daemon behind a transaction pooler

**Warning**, worded "looks like". This process runs the async daemon `HotCold`, its leadership lock is
session-scoped (`opts.Events.UseAdvisoryLockTransaction` is false), and the host or port of the database's data
source looks like a transaction-mode pooler:

- a host with `-pooler.` in it - Neon's pooled endpoint is the direct one with `-pooler` after the endpoint
  id, and runs PgBouncer with `pool_mode=transaction` (Neon, *Connection pooling*);
- port `6543` - "Port 6543 reaches PgBouncer for the dedicated pooler and Supavisor for shared transaction
  mode" (Supabase, *Connect to your database*). Supabase's shared pooler on 5432 is session mode, and is not
  flagged;
- port `6432` - PgBouncer's default `listen_port` (pgbouncer.org, *Configuration*), and the port of Azure's
  built-in PgBouncer, whose `pgbouncer.pool_mode` defaults to `transaction` (Microsoft Learn, *PgBouncer in
  Azure Database for PostgreSQL*). A PgBouncer of your own pools by session unless told otherwise.

HotCold leadership is a Postgres advisory lock. A session-scoped one (`pg_try_advisory_lock`) belongs to the
server process that took it, and a transaction-mode pooler hands that process to another client as soon as the
transaction that took the lock ends. The lock then belongs to whoever uses that server connection next, the
leader's release lands on another connection and fails, and two nodes can end up running the same projection,
or none. PgBouncer's feature map says session-level advisory locks never work in transaction pooling.

**What changed in Marten 9.41.** `UseAdvisoryLockTransaction` defaults to `false` from 9.41.0, so the lock is
session-scoped unless the host says otherwise. Marten's migration guide (V9.50.2, *Key Changes in 9.41.0*) says
"The one deployment that needs action is a daemon running behind PgBouncer in transaction pooling mode". On
9.39 and 9.40 (the studio's floor and the release after it) the default is `true`, so the lock is
transaction-scoped and this fires only for a host that turned it off. From 9.41 it fires for every HotCold host
behind such a pooler that has not turned it on. The finding says which default applies to the Marten the host
runs.

What to change:

```csharp
opts.Events.UseAdvisoryLockTransaction = true;

// ...and, on the node that runs the daemon, a direct connection to Postgres rather than the pooler's:
// opts.Connection(directConnectionString);
```

With the transaction-scoped lock the leader keeps one transaction open for as long as it leads. A
transaction-mode pooler keeps that transaction on one server connection, at the cost of the connection and of
an open transaction - which with `Events.UseMonitoredAdvisoryLock` on (the default) at read committed holds a
snapshot about a minute at a time, and with the monitor off or under repeatable read holds back vacuum for as
long as it leads (C04, T14). A direct connection for the daemon's node avoids the pooler altogether; Neon's and
Supabase's documentation both send anything session-scoped to the direct connection string.

The finding, the daemon mode and the lock setting are shown to anybody the store policy passes for the scope. The
host and port that look pooled are shown only at the database-wide gate, as the Configuration page shows a
database's server and port, and the status row names neither.

Tier 0, no I/O: the host and port are all it has to go on, so it is a likeness and not a proof, and a pooler on a
port of its own choosing is not recognised. [C10](connections.md#c10-hotcold-daemon-behind-a-transaction-pooler-proven)
tests the connection itself. Never raised for `Solo` (it takes no lock), `ExternallyManaged` (the lock is not
Marten's) or a process that runs no daemon.

## K05 SQL console without a narrowed role

**Warning.** Both studios. `Capabilities.RunSql` is on, `ReadOnly` is off, and `SqlConsoleRole` is not set - so every
statement typed into the console runs as the role the data source logs in as, the role that owns the application's tables.

The console's read-only transaction stops writes, never reads (D13): whoever is granted `RunSql` reads every row that role
can, in every schema, whatever `BrowsableSchemas` says. The role the console runs as is its real boundary. Like K01-K04 it
reads only the studio's options, with no I/O, and is shown to anybody the scope admits; unlike them it runs in both studios,
and names the option in the studio's own type (`MartenStudioOptions.SqlConsoleRole`, `PostgresStudioOptions.SqlConsoleRole`).
`ReadOnly` mutes it.

What to change:

```csharp
// In AddMartenStudio(options => ...): the role the SQL console and the database browser read as
options.SqlConsoleRole = "studio_reader";
```

A role the data source's login is a member of, granted `SELECT` on what the console and the browser should read and nothing
more; each statement switches to it with `SET LOCAL ROLE`. Creating it and granting it are a DBA's - the studio never runs
either.

## K06 Database browser open to every schema without a narrowed role

**Warning.** Both studios. `Capabilities.BrowseDatabase` is on, `ReadOnly` is off, `BrowsableSchemas` contains `"*"`, and
`SqlConsoleRole` is not set: event 9230's condition, now a finding too (D27). `"*"` admits every schema the reading role
has `USAGE` on but Postgres' own and extensions', and without `SqlConsoleRole` the reading role is the data source's own
login, so every table it may select from is browsable, row by row, by anyone granted `BrowseDatabase`.

Event 9230 is said once at start-up whether or not the capability is on; K06 is raised only while it is, because only then
does a visitor read through it. `ReadOnly` mutes it. The remedy is K05's: a `SqlConsoleRole`.

## K07 Studio open to anonymous visitors with a capability on

**Critical in Production, Warning anywhere else.** Both studios. The studio's pages are served to anybody - they carry
`AllowAnonymous`, from the mapping's own `AllowAnonymous()` or a group above it, observed on the finished endpoints the
start-up guard reads, the same fact the layout's banner draws - and at least one capability is on (and `ReadOnly` off).

A studio served anonymously with every capability off is a read-only browser, which the banner says already; with one on,
anyone who can reach its address can use it. A store or write policy, if the host set one, still decides what an anonymous
visitor reaches. The environment is read as K01 reads it, and a host that names none is "anywhere else". A process that maps
no studio says so in the status line.

What to change:

```csharp
// Ask who the visitor is:
app.MapMartenStudio().RequireAuthorization();

// ...or keep it anonymous, with every capability off:
options.Capabilities = new MartenStudioCapabilities();
```

## K08 Server timeouts end the daemon's leadership

**Warning**, one finding per setting. Marten Studio only. This process runs the async daemon `HotCold`, and a server
timeout the store's role is subject to ends the leader's session or transaction - and with it the leadership. Another node
takes the lead and every projection the leader ran restarts there, over and over as each new leader is ended in turn.
Three variants, read from the Server page's settings read (`pg_settings` on a connection of the store's own data source,
so a value set for the store's role or database counts):

- **`idle-in-transaction`**: the leadership lock is transaction-scoped (`Events.UseAdvisoryLockTransaction`, the default
  at Marten 9.39 and 9.40) and `idle_in_transaction_session_timeout` is above zero and shorter than three times the
  leader's idle stretch in its leadership transaction - or above zero at all with `Events.UseMonitoredAdvisoryLock` off,
  when the leader is idle for as long as it leads. The studio's own read-only transaction sets this setting for itself, so
  it is read as a new session starts with it.
- **`transaction-timeout`**: the lock is transaction-scoped and `transaction_timeout` (PostgreSQL 17) is above zero. It
  ends any transaction that has lasted that long, idle or not.
- **`idle-session`**: the lock is session-scoped (the default from Marten 9.41) and `idle_session_timeout` is above zero
  and shorter than three times the minute the lock connection sits idle between its monitor's statements - or above zero
  at all with the monitor off.

**The leader's idle stretch is measured, not assumed** (`HotColdLeaderSessionLiveTests`, `DaemonTimeoutsLiveTests`, at
Marten 9.39.0, 9.45.0 and the latest tested). With a transaction-scoped lock and its monitor on, from Weasel 9.34 (Marten
9.39.1), whose Patched DistributedLock.Postgres 1.3.3 the check reads off the loaded assembly rather than from a version
number, DistributedLock's keepalive runs in the leadership transaction about once a minute (61 s), the session idle in
between; with the upstream DistributedLock.Postgres 1.3.0 (Weasel 9.33, Marten 9.39.0) - and wherever the connection string
sets Npgsql's `Keepalive` or `Multiplexing` -
the monitor's `pg_sleep` keeps the session active once its first leadership poll has passed, so it is idle only about
`LeadershipPollingTime` plus a second after taking the lead. Three times the stretch is C04's margin for a keepalive a
loaded host runs late. A server `statement_timeout` shorter than that sleep's minute does not end it, and there is no
variant for it: DistributedLock runs `SET LOCAL statement_timeout = 0` in the leadership transaction before it sleeps, so the
leader kept its lock under a 10 s `statement_timeout` (measured at Marten 9.39).

**C04, T14 and D01 advise `idle_in_transaction_session_timeout` as a backstop**, and each says the same: for a store whose
HotCold daemon leads from a transaction, it must be longer than the leader's idle stretch, and `transaction_timeout` unset
for the store's role.

The remedy is in words for the timeout - whoever owns the store's role raises or unsets it for that role
(`ALTER ROLE … SET … = 0`), so the application's other sessions keep the server's value - and a configuration line for
the lock's scope:

```csharp
// In AddMarten(...): hold the leadership lock on its own session, which no transaction timeout ends.
opts.Events.UseAdvisoryLockTransaction = false;
```

A session-scoped lock cannot work behind a transaction-mode pooler (K04), so there the timeout is what changes; and for
`idle-session` the line is the other way round.
