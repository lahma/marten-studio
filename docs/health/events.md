# Health: events (E)

These checks look at the event store's traffic and shape. Back to the [index](../health.md).

**Studios.** Every check here reads a Marten store's event store, and runs only in Marten Studio
([codes.md](codes.md)).

They read in two ways, and say which in every finding's caveat:

- **E02, E03 and E04** share one **recent-traffic profile**, read only behind **Run deeper checks** (tier 2) and
  cached for five minutes. It is a single statement: the newest 100,000 events of `mt_events`, read as one
  backward scan of its primary key cut by a `limit` (a Merge Append of one per partition on an
  archive-partitioned store), under the studio's `statement_timeout`.
  The database folds the window into its totals, its event types, its ten busiest streams and - on a conjoined
  store - its twenty busiest tenants. Payload sizes are `pg_column_size` percentiles computed there; no payload
  leaves the database.
- **E05, E06 and E07** read the planner's statistics - `pg_stats` for `mt_events` and `mt_streams`, and
  `pg_class.reltuples` - with no scan of either table, at every interval (tier 1). That makes them estimates
  from the sample the last `ANALYZE` took: only as fresh as autovacuum keeps it, and blind to anything rarer
  than the sample. **E08** reads the statistics collector's counters for `mt_dcb_tag_version`.

Each finding is about the database as a whole, so it is shown to a visitor the store policy allows for the
database with no tenant selected. A stream id is data: on a conjoined event store a stream belongs to one
tenant, so a finding that names one names the tenant too and is shown only to a visitor through
`ViewDatabaseActivity` whom the store policy lets see that tenant.

A store with `Events.UseTenantPartitionedEvents` numbers every tenant's events from one, so it has no single
recent window: E02-E04 say "not checked" there. The statistics checks still apply. What else is not covered is
listed at the [end of this page](#not-covered).

## E02 Tombstones

**Warning.** More than 1% of the recent window is tombstones, over a window of at least 1,000 events.

When `SaveChanges` fails after its events were given sequence numbers, Marten appends a
`JasperFx.Events.Tombstone` event into each of those numbers - in a stream of its own, with the type
`tombstone` under the default naming style - so that the sequence has no hole for the async daemon to wait out
(read at `V9.39.0` and `V9.50.2`, `EventGraph.TryCreateTombstoneBatch`). Each tombstone is a sequence number a
failed save spent. A share above 1% means appends are failing steadily, most often on a concurrency conflict over
a stream's version - which a busy stream (E03) and an application that retries without backing off make worse.

Under **Rich** appends every event's number is drawn before the insert, so every failed save leaves tombstones and
the share counts them all. Under the **Quick** modes (Marten 9 defaults to `QuickWithServerTimestamps`) the
numbers come back from `mt_quick_append_events`, so a failure later in the same batch still leaves tombstones, but
one inside the function after it drew its numbers - two appends racing past the same version check, the loser
failing on the duplicate version - leaves a gap instead, which the daemon later skips (D07): the share undercounts
the failures there.

There is no `StoreOptions` line for this. Find what is conflicting - E03 names a hot stream - and check what the
application does when a save throws.

## E03 Hot stream

**Warning** when at least ten transactions wrote that stream, at one or more a second; **Info** otherwise. One
stream took more than 20% of the recent window's appends, with at least 200 of them, over a window of at least 1,000
appends. Tombstones are left out of both counts, and a stream that has since been archived is not reported.

The rate counts **transactions, not events**: the distinct `xmin` of the stream's rows in the window - the
transaction that wrote each one - over the span between its first and last append, taken as at least a second. One
`SaveChanges` of a thousand events is one writer, so a bulk import is an Info ("all in one transaction") rather
than a contention Warning for as long as it stays in the window. Several fast transactions from one writer still
look like several writers; the rate cannot tell them apart.

Every append bumps its stream's version in `mt_streams` inside the appending transaction - Rich appends with an
`update ... where version = ...`, Quick ones inside `mt_quick_append_events` - so appends to one stream queue on
that row, and `FetchForExclusiveWriting` takes it `for update` on purpose. One stream with a fifth of the store's
traffic is where that queue forms: its appends serialize, and the ones that lose a race for the same version fail
(E02, D07). A share is not a queue, though: on a quiet store a ledger or a settings stream can carry a fifth of
the traffic at a few transactions a minute, and a bulk import can carry it in one, with nothing waiting behind
either - that is the Info, worth knowing if many writers start appending to it, and because a stream that only
grows is replayed whole by every live aggregation of it (E06).

The finding names the stream. On a conjoined event store it also names the stream's tenant and that tenant's
share of the window, and is shown only to a visitor who may see that tenant (see above).

There is no `StoreOptions` line for this either: the fix is in what the stream models - a stream per order rather
than per customer, a new stream per period - so that fewer writers want the same row.

## E04 Large event payloads

**Warning.** The 95th percentile of the recent events' stored size is over 64 KB, over a window of at least 100
events.

The size is `pg_column_size(data)` (plus `bdata` where the store has binary events): what PostgreSQL stores, after
TOAST compression, so the JSON the application serialises is usually larger still. The finding names the event
types whose own 95th percentile is over the line.

An event is read again whenever anything replays it: every projection rebuild reads every event, a live
aggregation reads the whole stream on each fetch, and the async daemon reads each one once per projection. At this
size each of those reads is mostly payload, and the event store grows by it for good. An event that carries a
document, a file or a whole aggregate is usually better as a reference to it, with the large part stored once
beside the stream.

## E05 Unregistered event types

**Warning** when something registered in this process would stop on them; otherwise an **Info**. The statistics
of `mt_events` show an event type alias this process cannot read events by, and beside it the `.NET` type name it
came from, which this process cannot load.

Marten reads an event by its `type` alias first - a registered mapping, or an upcaster's old name - and, failing
that, by the assembly-qualified name stored in `mt_dotnet_type`, which works whenever that type can still be
loaded (`EventDocumentStorage`, `V9.39.0` and `V9.50.2`). Only when both fail does it throw
`UnknownEventTypeException`. Marten registers event types lazily, on first append, so an alias with no mapping in
this process is ordinary and is not reported on its own.

**An alias is named only with evidence.** The statistics of `type` and of `mt_dotnet_type` are sampled and sorted
separately, so nothing in them pairs a row's alias with that row's type name. The check pairs them by the name
itself: an unloadable `mt_dotnet_type` gives the alias a type of that name would be stored under in the store's
naming style (`ClassicTypeName`: the type's own name in snake case; `SmarterTypeName`: its declaring type's too;
`FullTypeName`: the full name). Only an alias with such a name in the statistics is named, with the name beside
it; any other unmapped alias "could not be matched to a .NET type in the statistics", and the status row or the
finding says so without calling it unreadable. A generic type's alias cannot be told from its name and is never
paired.

**A Warning only when something here stops on them.** A rebuild or the daemon reads an event only for a
projection or subscription that reads its type: Marten filters a shard's fetch by its `IncludedEventTypes`, which
an aggregation fills from its own `Apply` and `Create` methods and an event projection from its `Project` methods,
so one with a filter never meets an unknown type. A projection written as one `ApplyAsync` override, or filtered on
an interface or a base class, reads everything. Two services can share one store, each registering the events it
appends, so the finding is a Warning only when a projection registered here reads every type and
`Projections.RebuildErrors.SkipUnknownEvents` is off (a rebuild stops part-way), or when an async projection or a
subscription here reads every type and `Projections.Errors.SkipUnknownEvents` is off (the daemon stops). Otherwise
it is an Info: a live aggregation of a stream that holds one still throws, and another service may be the one that
appends them. The evidence lists the projections and subscriptions here that read every type; one that writes a
hidden document type is counted, never named.

The check reads the planner's statistics of `mt_events` - `most_common_vals` and `histogram_bounds` of `type` and
`mt_dotnet_type` - rather than the table, which is what lets it run at every interval and what the Event types
screen's banner reads. So a type rarer than the statistics sample is not seen at all; the Event types screen counts
every type exactly, on its own. A `.NET` type name is looked for only among the assemblies this process has
already loaded - these names come from the database, and the advisor never loads an assembly because of one - so a
type Marten could still load by name, from an assembly nothing has touched yet, is reported as missing. The
upcasters' aliases are read from Marten's own alias cache; on a Marten where that cannot be read, the caveat says
an upcast alias may be reported too.

```csharp
// Register the type each alias belongs to, in every process that rebuilds or aggregates:
opts.Events.AddEventType<YourEventType>();

// ...or map an old alias onto today's type, or upcast it:
opts.Events.MapEventType<YourEventType>("the_old_alias");

// ...or let rebuilds skip what they cannot read (the projection then lacks those events):
opts.Projections.RebuildErrors.SkipUnknownEvents = true;
```

## E06 Long streams without a snapshot

**Info.** An aggregate is only ever built live - nothing inline or async keeps it - and the statistics of
`mt_streams` show streams longer than 1,000 events.

A live aggregation - `AggregateStreamAsync<T>`, or `FetchForWriting` / `FetchLatest` of a type no inline or async
projection keeps - replays the stream from its first event every time it is asked, so its cost grows with the
stream on every request. The aggregates come from the configuration and from what this process has aggregated
live (`ProjectionOptions.AllAggregateTypes()`); a hidden document type is never named. The stream lengths come
from `histogram_bounds` of `mt_streams.version`, which describe every stream together: the statistics cannot say
which aggregate a long stream belongs to - it may be one that is already snapshotted - which is why this is an
Info. The 20 longest streams would need a scan of `mt_streams`, which the check never runs.

```csharp
opts.Projections.Snapshot<YourAggregate>(SnapshotLifecycle.Inline);
```

That keeps the aggregate as a document, updated in the appending transaction, so a fetch reads one row and only
the events after it; rebuild the snapshot projection once to fill it on an existing store. `SnapshotLifecycle.Async`
keeps it off the append path instead. Stream compaction, on a stream's page behind `CompactStreams`, folds an old
stream's history into one event.

## E07 Archived share without partitioning

**Info.** More than half of `mt_events` belongs to archived streams, the store has at least 100,000 events, and
`Events.UseArchivedStreamPartitioning` is off.

Archiving a stream only marks it: its row and its events stay where they were, flagged `is_archived`. Queries for
live streams and events then filter them out of tables that are mostly rows they skip, a projection rebuild reads
through their whole sequence range, and every index on `mt_events` and `mt_streams` carries them. With
archived-stream partitioning both tables are list-partitioned on the flag and archiving moves a stream's rows into
the archived partition, which the planner leaves out of every read that asks for live data. The share is
`most_common_freqs` of `is_archived`.

```csharp
opts.Events.UseArchivedStreamPartitioning = true;
```

**The migration is not small.** On an existing store the next schema apply turns `mt_events` and `mt_streams` from
plain tables into partitioned ones, which means copying every row out to a temporary table, dropping and
recreating each table partitioned, and copying every row back - an outage proportional to the event store's size.
Marten's own documentation recommends exporting that script with `db-patch` and running it by hand, in a
maintenance window. Afterwards a reused archived stream id is allowed in the live partition; add
`Events.EnableStrictStreamIdentityEnforcement` if stream ids come from outside Marten. Marten refuses to combine
it with `UseTenantPartitionedEvents`, so such a store is never told to.

## E08 DCB tag-version churn

**Warning.** The store registers DCB tag types, `mt_dcb_tag_version` has more than 100,000 rows, and fewer than
90% of its updates are heap-only (HOT), over at least 10,000 updates.

`mt_dcb_tag_version` (#4591) takes an `UPDATE` on every `FetchForWritingByTags` boundary save and is built for
PostgreSQL's HOT path: `version`, the only column that changes, is deliberately unindexed. HOT also needs free space
in the same heap page, which the default fillfactor of 100 does not leave once a large tag set has packed the
pages; each update then writes a new tuple elsewhere, adds an entry to the primary key and leaves the old one for
vacuum. Marten measured it (#5622): 100% HOT at 50 rows whatever the fillfactor; at 200,000 rows 92.9% HOT on the
default and 100% at fillfactor 70, for 45% more disk.

The share is measured over the advisor's own window once two samples a minute apart exist, and over the counters
since the statistics were last reset until then.

When the store already declares a fillfactor that the table does not have yet, the line is not repeated: the
finding says the schema change that sets it is pending. **Applying it is not limited to this table.**
`ApplyAllDatabaseChangesOnStartup()` and `db-apply` apply every pending change in the database under
`AutoCreate.CreateOrUpdate`, which is not additive: it drops indexes and columns the configuration does not declare,
alters a changed column's type, drops and recreates a changed primary key, and copies a table out and back to
change its partitioning. So the finding says to review the whole patch with `db-patch` first, and only then apply
it, at deploy or at startup.

When the fillfactor is already declared and on the table, the finding is an Info instead: `ALTER TABLE ... SET
(fillfactor)` does not rewrite the pages already written, so the
free space appears as pages are rewritten, and fully only after the table is rewritten once (`VACUUM FULL` or
`CLUSTER`, run by whoever owns the database - both lock the table).

```csharp
opts.Events.DcbTagVersionFillFactor = 70;
```

The option arrived in **Marten 9.47** (#5622); on an older host the page prints the version this host runs beside
it. An older Marten has no way to declare it, and a fillfactor set on the table by hand is schema the configuration
does not know about.

## Not covered

- **`Events.UseTenantPartitionedEvents`.** Every tenant numbers its events from one, so there is no single recent
  window and no store-global mark: E02, E03 and E04 say "not checked", and so does D08 on the daemon page. The
  statistics checks (E05-E07) and E08 still run.
- **E06's longest streams.** Naming the 20 longest streams, and which aggregate each belongs to, needs a scan of
  `mt_streams`, which no check runs; the finding says how many streams are long, not which. A screen that lists
  them is a separate, on-demand read, and is not part of this release.
- **E05's unmatched aliases.** An unmapped alias whose own type name the statistics did not sample is reported as
  unmatched, not as unreadable. The Event types screen counts every type exactly.
- **E03 and E04 on old traffic.** The window is the newest 100,000 events; a stream that was hot an hour ago and
  quiet since, or a large payload older than the window, is not seen.
