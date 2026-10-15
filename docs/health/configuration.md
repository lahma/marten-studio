# Health: configuration (K)

These checks read the store's configuration and the host's environment, and nothing else: tier 0, no I/O.
They are shown to anybody the store policy lets see the scope they selected, because they say nothing the
Configuration page does not: its store card shows `AutoCreate`, its Events card both tracking switches, and
its Runtime card the host's environment name. Back to the [index](../health.md).

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
