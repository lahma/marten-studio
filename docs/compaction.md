# Stream compaction

Marten Studio can compact one event stream from its Stream detail page. Compaction is off by default.
`MartenStudioOptions.Capabilities.CompactStreams` turns it on (it is part of `MartenStudioCapabilities.All()`),
and `ReadOnly` turns it off again. The write policy is asked about the scope as selected, tenant included,
with `CompactStreams` named. Every attempt is written to the audit log, whether it succeeds or fails.

This page is the longer version of what the confirmation dialog says. Everything here was read from
Marten's `EventStore.StreamCompacting.cs`, which is the same file at 9.39.0 (the studio's floor) and 9.50.2.

## What it does

The studio calls Marten's typed `session.Events.CompactStreamAsync<T>(streamId, …)` and then `SaveChangesAsync`,
on a session opened with `SessionOptions.ForDatabase(tenant, database)`. `T` is the stream's aggregate type.
The request's `Version` is set to the version the dialog showed, so Marten folds through that version and no
further.

1. Marten reads the stream's events with `FetchStreamAsync`, up to and including that version. An event
   appended after the dialog opened is not read, so it is neither folded nor deleted: it stays in the stream
   after the snapshot. Archived events are never returned, and on a store that records skipped events
   (`EnableEventSkippingInProjectionsOrSubscriptions`) skipped events are not returned either.
2. If there are no events, or the stream is already exactly one `Compacted<T>`, it stops having done
   nothing.
3. It folds the events into `T` with `Projections.AggregatorFor<T>()`, which uses the registered projection
   first.
4. If the request carries an `IEventsArchiver`, Marten calls it. The studio has none to give, so **the
   folded events are deleted, not copied anywhere.** Take a backup or an export first if you may need them.
5. Three statements then run in one transaction:
   - The last event's row is rewritten as a `Compacted<T>` holding the aggregate. It keeps the same
     `seq_id` and the same version. It gets a new id and `timestamp = now()`, and its headers, causation id,
     correlation id and user name are cleared.
   - Every earlier event's row is deleted from `mt_events`.
   - `mt_streams.compacted_version` is set to `greatest(compacted_version, version)`.

The stream's own version does not change. Marten does not rewrite the stored aggregate document, if a
projection keeps one, and it does not tell the async daemon anything.

After the save, the studio reads `mt_streams` again and compares `compacted_version` with its own reading
from just before the save.

Both of these reads go to the primary. They use `ConnectionUsage.ReadWrite`, which Marten maps to
`MultiHostSettings.WriteSessionPreference`, so they reach the host the write runs on. Marten's own write
session reads the same way. An ordinary page read uses `ConnectionUsage.Read`, which follows
`ReadSessionPreference`. On a multi-host data source set to `PreferStandby`, that read can land on an
asynchronous standby that has not caught up yet. Such a standby would show the old `compacted_version`
after the save, and the studio would report "nothing was compacted" for events the primary has already
deleted. A stale reading before the save would also weaken the version checks. The page itself still reads
with `Read`, so on a lagging standby it can show the old stream for a moment after it reloads. The audit
entry comes from the primary.

- **The watermark moved.** The stream is compacted through the new `compacted_version`, and that is the
  version the studio reports and audits. Marten folds through the last event its fetch *returned*, not
  through the version it was given. On a store with `EnableEventSkippingInProjectionsOrSubscriptions`, a
  skipped event is never returned. So if the event at the dialog's version is skipped, the stream is
  compacted through an earlier version. The studio's own dead-letter Skip marks an event skipped, and so
  does the daemon when it skips an apply error. The message then says which versions were left out and
  why. Those events stay in the stream after the snapshot.
- **The watermark did not move.** Nothing was compacted, and the studio reports and audits a failure. Marten
  found nothing to fold: the stream was archived in between, or every event after its last snapshot is a
  skipped one.
- **The result cannot be read back**, or the watermark is past the dialog's version, which means that
  another compaction ran at the same time. The studio records "Compaction was saved, not confirmed". The
  audit log has only two outcomes, so the entry is a failure whose message says this. The page shows it
  as a warning, not an error.

In every case the page reads the stream again, so it never goes on showing events that are gone.

Comparing against the studio's own reading means another operator's earlier compaction is never recorded
as this one. A compaction that runs between that reading and this save is the one window that remains.

## What it means afterwards

- **Async projections.** A shard that has already processed the folded events is not affected. A shard
  that had not reached them yet never will, because they are gone. When it reaches the last position, it
  finds a `Compacted<T>` there.
- **Rebuilds.** A rebuild replays only the snapshot. `T`'s own single-stream projection fast-forwards from
  `Compacted<T>`. Every other projection that reads this stream's events loses them.
- **Time travel.** Replaying the stream to any version below the compacted one, on Stream detail or with
  `AggregateStreamAsync`, finds nothing.
- **Skipped events** are neither folded nor deleted, so they stay in the stream.

## When the page offers it

The Compact section appears only when all of these are true:

- the stream exists in the scope, and it is not archived;
- its id is not the empty GUID;
- its `mt_streams.type` is the alias of a single-stream aggregate the store registers, and
  `IsDocumentTypeVisible` does not hide that aggregate;
- it holds more than one event by its version, and an earlier compaction has not already folded it to that
  version.

If two registered aggregates answer to the same alias, the page does not guess between them.

The service checks each of these again when the compaction is confirmed. It does not rely on what the page
showed. It refuses a version past the stream's end, and one an earlier compaction has already folded. A
stream that has grown since the dialog opened is not refused. It is compacted through the version the
dialog showed, and the newer events stay after the snapshot.

The plan counts events by version. It cannot tell a skipped event from any other, so a stream whose events
after its snapshot are all skipped is still offered. Compacting it then finds nothing to fold, and the
studio says so.

**The empty GUID.** Marten's stream fetch adds its stream predicate only when the id is not empty. A stream
whose id is `00000000-0000-0000-0000-000000000000` would therefore fold every event of the tenant, or of the
whole store, into one snapshot, and delete all the others. The studio never compacts that stream, and it
does not replay it on the time-travel panel either.

**Hidden aggregates.** A compaction writes the aggregate's state into the event log. For an aggregate the
host keeps off the screen, the Events screens would then show that state as the snapshot event's data. The
studio refuses such a stream, and the reason it gives does not name the type.

The studio resolves the aggregate type itself. It asks Marten for the alias of each registered aggregate
(`EventGraph.AggregateAliasFor`), rather than asking Marten which type an alias means
(`AggregateTypeFor`). The second lookup caches an unknown alias as "no type" for the life of the process,
and a later `StartStream<T>` cannot correct that entry. A studio that made that lookup could therefore
change how the host's own stream-state reads behave.

## Tenants

Marten narrows its compaction reads and writes to the session's tenant only on a conjoined event store
(#5234). There the scope must name the stream's tenant, and only that tenant's stream with that id is
touched. This holds from the 9.39.0 floor, and with `UseTenantPartitionedEvents`, where two tenants' events
can share a `seq_id`. On an event store that is not conjoined, Marten fetches the stream by id alone and
deletes what it folded with no tenant predicate, although every append records its session's tenant in
`mt_streams.tenant_id`. So from a tenant's scope the studio first checks that the stream is that tenant's -
another tenant's stream gets the answer a missing one gets - and compacts a stream that also holds an event
another tenant's session appended only for a visitor the write policy allows for the database as a whole
(`TenantId = null`), because Marten folds and deletes every event of it. The studio does not use the
store-level `IEventStore.CompactStreamAsync` overloads, including the tenant ones added in 9.41.0. They open
`LightweightSession()` or `LightweightSession(tenantId)`, and neither pins the database the scope selector
chose.

## Streams that cannot be compacted

**Tagged streams.** In the default tag-table mode, each tag row references its event through a foreign key
to `mt_events(seq_id)`, and that key has no cascade. Compaction does not delete the tag rows first. So if any
event before the last one carries a DCB tag, Postgres refuses the delete with `23503` and nothing changes.
The studio reports this in its own words. In hstore mode the tags are kept on the event row and leave with
it.

**Migrations.** Like every Marten session write, `FetchStreamAsync` first applies any pending event-store
migration under the host's own `AutoCreate`. That is a property of the write, not of the page: nothing
happens until somebody types the stream id and confirms.
