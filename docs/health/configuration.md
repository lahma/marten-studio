# Health: configuration (K)

These checks read the store's configuration and the host's environment, and nothing else: tier 0, no I/O.
They are shown to anybody the store policy lets see the scope they selected, because they say nothing the
Configuration page does not: its store card shows `AutoCreate`, its Events card both tracking switches, the
daemon mode and the leadership lock's scope, and its Runtime card the host's environment name. The one exception
is K04's host and port, which are evidence at the database-wide gate - the store policy for the database as a
whole - because that is what the Configuration page asks before it lists a database's server and port. Back to the
[index](../health.md).

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

**K02 fires on Marten's defaults, and on the sample host, by design.** A new store with an async projection
and nothing else configured gets this Info, and so does `samples/MartenStudio.Sample`, which keeps Marten's
defaults on purpose so that what the advisor says about it is what it says about a new host. It is an Info,
not a Warning: nothing is wrong, and muting it in the browser is a reasonable answer.

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
