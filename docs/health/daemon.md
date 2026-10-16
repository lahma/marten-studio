# Health: daemon and high water (D)

These checks answer the question asked most often: **why aren't my projections moving?** They read
`mt_event_progression` (a few dozen rows), the event sequence, one primary-key probe of `mt_events`, the
dead-letter table, and - only when there is work and no healthy daemon in this process - `pg_locks`. Every
read runs in the advisor's read-only transaction at the health cadence (`HealthInterval`, 60 s by default),
never on a page's poll. Each finding is about the database as a whole, so it is shown to a visitor the store
policy allows for the database with no tenant selected; a name of a session or a tenant sits behind
`ViewDatabaseActivity`. Back to the [index](../health.md).

Every age of a row below is measured in the **database server's** time (`transaction_timestamp()`), against
timestamps the server wrote, so a clock that differs between the application and the database changes nothing.
A rate, a "for the whole window" and how long a committed event has waited behind a gap come from the advisor's
own samples, which exist only while somebody has the Health page or the Overview open; before two samples a minute
apart exist, the checks that need them say they are watching and report nothing.

A finding that names a projection - D03, D04, D05 and D06 - carries the document types that projection writes, so a
projection of a type `IsDocumentTypeVisible` hides is never named, in a finding or in a check's status line.

A store with `Events.UseTenantPartitionedEvents` keeps a high-water mark per tenant and no durable record of when
one was pinned: D01-D04 say "not checked" there rather than read store-global rows nothing maintains. D05 and
D06 still apply.

## D01 High-water mark pinned behind a gap

**Warning** once a committed event has waited behind the gap for 1 minute - or at first sight, when Marten's record
of the pin says the mark has already been held for more than a minute - and **Critical** once the advisor has seen
one waiting for 10. The daemon's high-water mark is held below a sequence number that was handed out and not
committed, and an event above it is committed and waiting.

The high-water mark is the point below which every event is committed and safely ordered; no async projection
processes anything above it. It moves contiguously, so it stops under any hole in the sequence. Most holes fill
within milliseconds - appends that drew a number and have not committed yet - and Marten holds every gap for
`StaleSequenceThreshold` (3 s) before asking whether any transaction could still fill it, then skips it as soon as
none could. A gap that keeps a committed event waiting for a minute is being held open by a transaction that may
still commit: an append in flight, a session left `idle in transaction`, a prepared transaction.

How the advisor knows. Since Marten 9.39 the daemon keeps a durable clock for a pin, the `HighWaterStuckGap`
progression row: its `last_seq_id` is the mark it first saw pinned and its `last_updated` is when. Read at
`V9.39.0` and `V9.50.2`, Marten **upserts that row only when the mark changes and never deletes it** - it
describes the current pin only while its mark equals the `HighWaterMark` row's own. So the mark is pinned when the
two marks are equal, the sequence has handed out more than the mark, and the event just above the mark is not
committed (one `min(seq_id)` probe of `mt_events`' primary key). When the next event *is* committed, nothing holds
the mark - if it is not moving, nothing is advancing it, which is D02 or D03.

**A pin that holds nothing back is not a finding.** Marten records the pin whether or not anything committed waits
above it: its `GapDetector` returns the mark itself when nothing above is committed, and `trackStuckGap` still writes
the row (both tags). That is every long append transaction on an idle store - a bulk import - every long transaction
beside a rolled-back append, and, under `UseTransactionEvidenceForGapSkipping = false`, every tail gap under 32,
permanently (#3865). No event is late in any of them, so D01 stays quiet and its status line says which shape it saw.
How long a committed event has waited is measured from the advisor's first sample that saw one waiting behind this
mark - "at least" that long - and that is what makes D01 Critical, never the row's time, which can predate anything
waiting by hours. The row's age does make the first sweep that sees a committed event behind an old pin a Warning,
so a stall that has lasted all morning is not left for two more sweeps.

**Explain**, on the projections screen and only when asked, puts Marten's own `GapLivenessProbe` questions to
the database: transactions in this database open since before the pin (and which of them hold a
`RowExclusiveLock` on `mt_events` or have written), the ones Marten rules out (idle in transaction since before
the allocation fence; the daemon's own leadership-lock sessions), how many write transactions of other roles it
cannot see the start of, and `pg_prepared_xacts` - the one reserver no session view can show. 9.47 (#5605) scoped
the write-transaction question to the current database; the Explain asks it that way at every version. Counts and
ages are shown to anybody who can see the finding; the sessions themselves (pid, role, application, client
address, statement) only with `ViewDatabaseActivity` and the write policy passing. The database is asked once a
minute at most, however many people press Explain - the read is shared across circuits and each visitor's gate is
applied to it afresh - and a session's statement is masked for that visitor's withheld schemas (D27), or withheld
outright when those cannot be read.

What to do: end the transaction that holds it - Explain names it - or use **Advance high water mark** on the
projections screen (`CorrectProgression`), which moves the mark to the latest committed event; anything that later
commits inside the gap is then never projected. To bound future stalls, at the same cost:

```csharp
opts.Projections.SkipStaleGapsDespiteLiveTransactionsAfter = TimeSpan.FromMinutes(30);
```

PostgreSQL's `idle_in_transaction_session_timeout` removes the cause instead.

Marten 9.38 fixed a failed `StartStream` under `QuickWithServerTimestamps` leaving a permanent gap (#5454). The
studio's floor is 9.39, so that cause cannot apply to a host running the studio, and D01 does not look for it.

## D02 Async projections behind, and nothing runs the daemon

**Critical.** A registered async shard is behind the highest sequence handed out, no session in this database
holds any of the store's daemon leadership locks, nothing has written to `mt_event_progression` for more than
`2 x SlowPollingTime + 1 min`, and no daemon runs in this process.

The leadership locks, pinned per distributor at JasperFx `V2.74.0` and `V2.87.1` (the distributors are
byte-identical): **HotCold over one database** takes one advisory lock per shard,
`ProjectionLockIds.Compute(eventSchema, shard, DaemonLockId)`; **HotCold over several databases** takes
`DaemonLockId` in each; **Solo takes none**. Weasel takes each as one 64-bit key, so `pg_locks` shows it with
`objsubid = 1`, transaction-scoped at Marten 9.39 and session-scoped from 9.41 (#5526) - the same row either way.
So for Solo the lock says nothing and the progression table carries the argument: a running daemon writes it, each
shard as it advances, the allocation fence on every high-water poll while caught up (#5108), and from Marten 9.43
the `HighWaterMark` row on every poll (#5541). The one running state that writes nothing below 9.43 is a mark
pinned behind a gap, and below 9.43 D02 stands down while the pin holds. From 9.43 a running daemon restamps the
mark's row even while it is pinned, so D02 stands down on a pin only while that row is fresh: a daemon that died
pinned is reported, where otherwise D01 would stay Critical with nothing saying why.

A second finding of the same check, **Warning**: a session *holds* a leadership lock while nothing has been
written for that long - a hung daemon, or a lock that outlived the process. Who holds it (pid, role, application,
client address) is shown only with `ViewDatabaseActivity`.

Never raised for `DaemonMode.ExternallyManaged` (Wolverine's leadership uses lock ids of its own: D03 alone
applies), for a daemon hosted in this process that says it is running, while Marten Studio's own coordinator
pause is recorded, or within `2 x SlowPollingTime + 1 min` of the start of a process that hosts a daemon - a
HotCold node just restarted holds no lock and has written nothing yet, and is starting, not dead. Another store
sharing this database with the same `DaemonLockId` would count as a holder; Marten asks for a lock id unique to
each application on a database.

```csharp
builder.Services.AddMarten(opts => { /* ... */ })
    .AddAsyncDaemon(DaemonMode.HotCold);
```

## D03 Shard alive but not moving

**Warning.** The daemon is alive, the high-water mark is ahead of a shard, and the shard has written no progress
for longer than its window - and the advisor has seen the mark ahead of it, with the shard where it is, for that
whole window.

**The window is the shard's own:** at least 5 minutes (and at least `5 x SlowPollingTime`), and three times its
typical page - the median of the runs in which the advisor saw it hold still with work to do before it moved on. A
healthy shard is still for as long as a page takes, and a page of a slow projection - a handler that calls out, a
large `BatchSize` - can take minutes, so a shard is not called stalled for taking its usual time. A run longer than
the window in force when it ended was not a page - a stall, a pause, an outage, a rebuild writing nothing - and is
dropped, and a run the advisor did not watch end to end (a gap of more than 5 minutes between samples) measures
nothing. The window is never more than 20 minutes, nor half of what the advisor's history spans (60 samples at
`HealthInterval`), because "the mark has been ahead for the whole window" has to be something the history can show.

A shard's `last_updated` is stamped on every progress write (#4981): it is when the shard last moved, not whether
its agent is alive. "Not moving" alone is a shard on an idle store, so D03 also needs the daemon to be alive and the
mark to have been ahead of the shard for the whole window, which a mark that moved a second ago is not.

**The 9.43 rule** changes what is believed, not only what is said. From Marten 9.43 the high-water agent restamps
its own row on every poll (#5541, `HighWaterMarkLivenessStamp` in the probe table), so that row's age *is* the
agent's liveness: fresh is alive, old is stopped - D02's question, not D03's - whatever the allocation fence or the
advisor's history of the mark say, because those are only older news from the same agent. Below 9.43 that row is the
mark's last move and freezes on a caught-up idle store, so an old one says nothing: the daemon is believed alive if
it is hosted here and running, its row or allocation fence was written within `2 x SlowPollingTime + 1 min`, or its
mark moved while the advisor watched - and without any of them D03 says it cannot tell instead of reporting a stall.

**Not a heartbeat.** The extended tracking's `heartbeat` column is written when a shard's status changes, as JasperFx
documents it, and is never read as a sign of life (K02 reads it the same way). A shard the extended tracking or the
daemon here says is paused or failed is D05's; one being rebuilt is moving in a replay of its own.

## D04 Falling behind, with an ETA

**Warning.** A shard's lag is past its warning threshold and grew at each of the last two steps between the
advisor's samples: events arrived faster than the shard processed them, twice running.

Tier 0: it runs no statement, and judges the sequence, the mark and the shard positions D01 and D03 recorded in the
same sweep, with what this process knows of the daemon. A burst of appends lands in one step, however large, and a
shard that keeps up drains it by the next, so a single burst is never a finding - it takes three samples, over at
least a minute, to say "falling behind". A lag that is high and draining is not a finding either - it is "draining
at N ev/s, caught up in ~T", which the check's status row says and the projections screen draws as its arrow and ETA.

**Only a shard that runs, moves, and is slower than its arrivals.** A lag grows for reasons that have nothing to do
with speed, and each is somebody else's, so D04 - and the projections screen's arrow - say nothing of a shard while
Marten Studio's coordinator pause is recorded (every lag grows, by design), while its projection is known to be
rebuilt (the position starts again from zero), while its agent here is paused or stopped (D05), when it has not moved
while the advisor watched (stalled, D03, or not run, D02), or when the high-water mark has not moved (held behind a
gap, D01). The status line says which.

Thresholds: a progression row's `warning_behind_threshold` and `critical_behind_threshold` win when set. Marten
creates those columns and **writes nothing to them** (#5173, at `V9.39.0` and `V9.50.2`), so in practice they are the
projections screen's 1,000 and 100,000; a host that writes its own is honoured.

## D05 Shard failed or paused

**Critical** for a shard that failed and stopped - the error policy gave up and nothing restarts it. **Warning** for
a paused one - Marten restarts it after the policy's pause, so a pause the advisor sees is one still in effect.

Two sources: the daemon hosted in this process says what each agent is doing now; the progression table says what
the node that ran the shard last wrote, under `Events.EnableExtendedProgressionTracking` only (off by default:
K02). A stop with no failure - the studio's own coordinator pause, a host shutting down - is not reported, and
whether anything runs the daemon at all is D02's.

Classified where JasperFx classifies: a start Marten Studio asked for and the daemon refused carries
`ShardStartException.Reason` (`AgentPaused`, `HighWaterNotRunning`, `ShardNotRegistered`, `StartRace`, `Faulted`,
`Rebuilding`) and whether a retry is expected to help (JasperFx 2.76+, Marten 9.41+); a live failure is classified
as transient or permanent by `TransientFailures.IsTransient` where it exists (JasperFx 2.89, in no Marten release
yet). A tenant's shard names that tenant, so its finding needs `ViewDatabaseActivity` and the store policy for that
tenant.

What to change depends on the failure:

```csharp
opts.Projections.Errors.SkipApplyErrors = true;          // ApplyEvent: the event becomes a dead letter
opts.Projections.Errors.SkipSerializationErrors = true;  // EventSerialization
opts.Projections.Errors.SkipUnknownEvents = true;        // UnknownEventType - or register the type
```

Each trades a stopped shard for a skipped event: the projection never reflects it until it is rebuilt.

**Starting a shard during a rebuild.** From Marten 9.50 (JasperFx 2.86) the daemon refuses a continuous start while
the shard's projection is being rebuilt, and Marten Studio says it was refused and records why. Below 9.50 a rebuild
started outside Marten Studio is invisible to it, nothing refuses the start, and the continuous agent it starts writes
the shard's progression row beside the replay: whichever inserts second fails on `pk_mt_event_progression` (23505),
and the rebuild can be the one that fails (measured at 9.39). The Start button says so on those versions; make sure no
rebuild is running before pressing it. A rebuild Marten Studio itself is running refuses the start at every version.

**Stopping.** Pause, Stop and Rebuild give each agent `Projections.StopAndDrainTimeout` (5 s by default) to finish
the page it is processing, and cancel what is still running then. JasperFx applies that bound only when it is greater
than zero: a zero, negative or infinite timeout lets a stopping agent finish however long its page takes, and the
projections screen says "no time limit" rather than quoting it.

## D06 Dead letters accumulating

**Warning.** A projection's dead-letter count rose across the advisor's window (ten minutes, at least two samples a
minute apart).

A dead letter is an event a projection could not apply and skipped. A store that has some had a bad event once -
the dead-letter screen and the navigation badge already say so, and the sample store has one on purpose. A count
that keeps rising is a projection failing on events as they arrive, and every one of them is an event its readers
never see. The finding names the projection; the dead-letter screen, filtered to it, shows each failure.

Counted raw, grouped by the JSON property the serializer's casing stores `ProjectionName` under: Marten's own
`FetchDeadLetterCountsAsync` applies the table's migration first, which a health read never does (hard rule 14).
`DeadLetterEvent` is single-tenanted, so the count is every tenant's. A host that hides `DeadLetterEvent` with
`IsDocumentTypeVisible` gets no read at all.

## D07 Gaps were skipped

**Info**, or **Warning** for a skip in the last hour on a store configured to skip past live appends. The
daemon's high-water detector moved the mark past a gap in the event sequence in the last day.

When the mark has been held under a gap for `StaleSequenceThreshold` (3 s) and the transaction evidence shows
nothing alive that could still fill it, the detector moves the mark past it - and, under
`Events.EnableAdvancedAsyncTracking`, records the move in `mt_high_water_skips` (`HighWaterDetector.DetectInSafeZone`
and `mt_mark_progression_with_skip`, read at `V9.39.0` and `V9.50.2`). One row is one move: it spans every number
between the old mark and the new one, the committed ones included, and the detector clears a whole field of dead
gaps in one move (#5108).

Under Marten's defaults - `Projections.UseTransactionEvidenceForGapSkipping` on and
`SkipStaleGapsDespiteLiveTransactionsAfter` unset - a skip is a range of numbers that rolled-back appends drew: lost
numbers, not lost events, so the finding is an Info. Many of them mean many failed appends. Under Rich appends a
failed save normally leaves tombstones (E02) instead of a gap; under the Quick modes an append that fails inside
`mt_quick_append_events` after drawing its numbers - two appends racing past one version check, most often - leaves
a gap.

Where the configuration lets the detector skip past a live append - the evidence switched off, or the cap set - a
skip in the last hour is a Warning: if the append that held the gap committed afterwards, its events sit inside a
range every async projection and subscription has already passed, and none of them will ever see them. **Explain**,
on the projections screen while the mark is held (D01), names the transactions that could be holding it. Below
Marten 9.47 the evidence counts in-progress transactions in every database on the server, prepared ones included
(`pg_snapshot_xip(pg_current_snapshot())` is server-wide; #5605 scoped it to this database and added
`pg_prepared_xacts` back), so an older host errs toward holding the mark rather than skipping; the caveat says so.

The check reads the newest 100 rows of `mt_high_water_skips` by its primary key - `ending_sequence` only grows -
with the server's clock, so every age is server time. The table exists only under advanced async tracking: without
it the check is not run, and K03 says how to turn it on. Only the store-global mark records skips; a store with
`Events.UseTenantPartitionedEvents` keeps a mark per tenant whose skips are recorded nowhere, and the caveat says so.

What to change depends on why:

```csharp
// The evidence is off: skip a gap only once nothing alive could still fill it (Marten's default).
opts.Projections.UseTransactionEvidenceForGapSkipping = true;

// The cap overrules the evidence: without it, a live append's gap is never skipped (the mark can stay
// pinned behind a forgotten transaction instead, D01).
opts.Projections.SkipStaleGapsDespiteLiveTransactionsAfter = null;

// Quick appends with an expected version: the loser of a race waits at the version check and fails
// before it draws a number, so it leaves no gap.
opts.Events.UseExclusiveLockOnConcurrentAppends = true;
```

## D08 Rebuilt elsewhere, or stopped half-way

**Info** while a shard is being replayed by something other than this studio, **Warning** once it has stopped
part-way. An async shard's position fell, nothing in this studio did it, and it has not yet reached the high-water
mark again.

What a database shows of a rebuild, read at `V9.39.0` and `V9.50.2`: JasperFx's `rebuildProjection` tears the
projection down first - Marten's `TeardownExistingProjectionStateAsync` deletes its documents and its shards'
progression rows - and then replays from the first event, writing progress as it goes. So the shard's position falls
to nothing and climbs back to the mark, and until it gets there readers see a partial projection. Starting a rebuild
here while that runs would race it: two rebuilds of one projection tear down and refill the same tables.

**The `mode` column does not say so.** `mt_event_progression.mode` exists on every table Marten has created since
9.31 (#5309), with `ShardMode.none` as its default, and it is read under `UseOptimizedProjectionRebuilds` - but no
Marten release writes it. `ShardMode.rebuilding` is stamped only on the in-memory states a running agent publishes
(jasperfx#681). The check still reads the column, and believes a `rebuilding` that a host's own tooling wrote.

What it watches instead is the position itself, sampled into the advisor's history at every regular sweep. A fall
between two samples is explained away when this process's studio rebuilt or rewound that projection around then, or
when this process is rebuilding it now - its tracker's latest state says so, or, on Marten 9.50+,
`IProjectionDaemon.IsRebuilding` does (a daemon that does not track rebuilds answers `false`, which proves nothing,
so that answer only ever explains a finding away). A fall is also explained away when the store-global high-water
mark, sampled beside the shards, fell in the same interval: Marten's `TryCorrectProgressInDatabaseAsync` - the
studio's Correct progression, Marten's CLI, another process - rewrites every progression row, the mark's included,
when the mark is ahead of the event sequence, and that is a correction, not a rebuild. What is left was run by
another process, or by hand: while the shard climbs it is being replayed there (Info); once it has stood still
below the mark for ten minutes the replay stopped half-way (Warning). A rebuild started from this studio that
failed or was cancelled below the mark is named as such (Warning). Nothing is said once the shard is back at the
mark, or back past where the replay began - the high-water mark sampled with the fall, or without that sample the
highest position the history holds before it. A healthy shard under steady appends sits a few events behind the
current mark, so that mark alone would keep calling a finished replay unfinished; and a rebuild restarted part-way
falls a second time from below where the first began, so the latest fall's own starting point would call it
finished half-way through.

The history exists only while a Health page or the Overview is open, so a fall that happened while nobody watched
is not seen; before two samples exist the check says it is watching. A shard whose projection writes a document
type `IsDocumentTypeVisible` hides is never named. Like D01-D04, a store with `Events.UseTenantPartitionedEvents`
is not covered.

There is no `StoreOptions` line. If another process is rebuilding, let it finish; if a rebuild stopped part-way,
restarting the shard continues it from its position, and a rebuild here starts it again from the first event.
