# AGENTS.md: the Kafka module, its engine and its Wolverine adapter

The [root AGENTS.md](../../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the Kafka module, its engine and its Wolverine adapter. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** The engine (`DataStudio.Kafka.Core`), reads, scans, predicates, decoders, redaction, the seams, the pages, authorization, capabilities and audit: [kafka.md](../../docs/maintainers/kafka.md) (D34-D48, D50); the Kafka suites: [testing.md](../../docs/maintainers/testing.md); [hard-rules.md](../../docs/maintainers/hard-rules.md) rules 5, 6, 7, 16 and 17.

## Hard rules

17. **In the Kafka module, a read never writes.** No client the engine builds may create a topic, commit or store an offset, join
    or subscribe a group, or run a host's builder delegate; every assign carries explicit offsets, and the wire seam has
    no produce, commit, delete or alter method. D37 has the overrides; `ConfluentOverridesTests`,
    `ReadNeverWritesMetadataLiveTests` and `ReadNeverWritesScanLiveTests` hold them. Full text: hard rule 17.

## Design decisions

**D35 — The Kafka module is authorized on a resource of its own, by policies of its own.** `KafkaTopicResource(Cluster,
TenantId, Topic, Capability)` is unrelated to `MartenStoreResource` and `PostgresResource`.
`ClusterAuthorizationPolicy` is asked on every call, once per topic; `CapabilityAuthorizationPolicy` is asked for each
capability use, once per topic, and falls back to the cluster policy. Neither ever falls back to the database modules'
policies, which are written for their resources. The shell's `AuthorizationPolicy` decides who gets in, and
its `ReadOnly` turns every Kafka capability off (the Kafka module has no switch of its own). `KafkaCapabilities` default to `false`, metadata needs none, and
a startup guard refuses a capability that no policy answers. A request naming a refused topic is refused whole, never
silently narrowed. Full text: [kafka.md](../../docs/maintainers/kafka.md#d35).

**D36 — `DataStudio.Kafka.Core` is the Kafka engine: Confluent.Kafka alone, behind a wire seam that exists for the fake.** It holds
the catalog, window resolution, the scan engine, predicates, decoders, the governor and redaction, with no ASP.NET Core
and no studio type, and Confluent.Kafka (floor 2.14.0) is its only client (plan K2). Every broker call goes through the
internal `IKafkaWire`, whose only other implementation is the fast suite's hand-written fake broker; one contract suite
runs on the fake and on real brokers (Kafka 3.9 and 4.x), so the fake cannot drift from Kafka. No Confluent type crosses
the seam, and errors are values. Nothing connects at startup and nothing is a hosted service: one admin client per
cluster, created on first use and disposed when idle, and one consumer per scan, on a thread of its own. Engine log events
are 9650 to 9699. The lag read (group offsets) uses a per-cluster consumer that is never subscribed, through a dependent admin client
drained before and after each read, because an admin handle has no consumer group and librdkafka 2.16.0 and earlier crash
(#5040) on `NOT_COORDINATOR` in `ListConsumerGroupOffsets`; remove it once the Confluent.Kafka floor contains librdkafka's fix
(PR #5611). Full text: [kafka.md](../../docs/maintainers/kafka.md#d36).

**D37 — In the Kafka module, a read never writes.** Every client the engine builds is a clone of the host's configuration with the
engine's overrides: no topic auto-creation, no commit and no offset store, explicit offsets on every assign, a group id of
its own that is never subscribed, `auto.offset.reset=error`, an explicit isolation level, the engine's own fetch and queue
sizes, no statistics, no metrics push, and the engine's own log and error handlers. The host's builder delegates and
handlers are never run or inherited; the one hook a host may give is OAuth token refresh, applied to the engine's own
clients only. The engine sends no single-topic metadata request, lists no consumer groups, and has no produce, commit,
delete or alter call at all: the seam does not have one. Live tests prove on the broker that scans created no topic,
committed no offset and left no group. Full text: [kafka.md](../../docs/maintainers/kafka.md#d37).

**D38 — The Kafka module's windows and negatives are truthful.** A window is `[from, to)`. Per partition, its start is
offsets-for-times at `from`, which is exact; its end is offsets-for-times at `to` plus a margin under `CreateTime`, and at
`to` under `LogAppendTime`; a record matches only if its own timestamp is in the window, so the margin widens the scan,
never the window. Under `read_committed` a partition stops at its last stable offset and says so. Records lost to
retention before or during a scan, records past the margin, undecidable records, pruned partitions and every stop are
reported as values, so "no match" is said only of what was read. A scan stops for exactly one `KafkaScanStopReason` and
keeps an exact per-partition cursor. Full text: [kafka.md](../../docs/maintainers/kafka.md#d38).

**D39 — The Kafka module's search predicates are one hand-written grammar, three-valued, and compared as bytes where they can
be.** The engine parses the grammar itself (no package and no expression escape hatch): `key`, `header.<name>`, `ce.<attribute>`
and `any`, then, behind `ReadPayloads`, `value` and JSON paths (`$.a.b`, `$.a[0]`, `$.a[*].b`), which read a CloudEvent's
data. A key literal is compiled to bytes through the topic's key format and compared without decoding; numbers compare as
decimals and a type mismatch is false. A clause that needs a decode it cannot make is undecidable, never false, and
undecidable records are counted. Clauses run cheapest first and short-circuit, which a decode counter proves. A key search
reads only the partitions a declared partitioner maps the key to, and only after sampling the window verified the claim;
one mismatch turns pruning off for that topic. An error names a position and never repeats a literal. Nothing is
deserialized into a CLR type. Full text: [kafka.md](../../docs/maintainers/kafka.md#d39).

**D40 — The Kafka module redacts inside the engine, before any sink.** A record leaves the engine only as a `KafkaMatch`, built by
one factory after the topic's rules and the built-in header deny-list (`authorization`, `proxy-authorization`, `cookie`,
`set-cookie`, `x-api-key`) have run; the wire record never leaves `DataStudio.Kafka.Core`. A rule that cannot be applied withholds
the value. JSON-path and pattern rules mask parts of a value; a value they cannot be applied to is withheld, and value clauses compare
what the visitor would see. A redacted field takes only `=` and `in`, `any` never reads one, and every text the engine writes for a sink -
the audit's predicate, a permalink's predicate, an error - shows a literal on a redacted field as `<redacted>`. Full
text: [kafka.md](../../docs/maintainers/kafka.md#d40).

**D41 — The Kafka module bounds the cost of a scan in the engine.** Concurrency (two scans per process, one per owner), a byte rate
(10 MiB/s per process, paced by consumption), per-scan bytes, records, duration and matches, and an owner's rolling
hourly byte budget. Memory is stated, about $S(C + 2mc)$: $C \approx 64$ MiB per consumer (measured), and a kept match holds at most $c$ bytes of key, headers, attributes and value together, kept as UTF-16 text; about 253 MiB at the defaults, as private memory (`RssAnon` on Linux, where .NET's
`PrivateMemorySize64` counts untouched mappings). A record read holds at most 21 matches and the value of the record asked for, up to MaxDecodeBytes; masking never grows a value past twice that. A scan above the confirmation
threshold starts only with a confirmation the engine checks. A profile or a visitor can only lower a limit. An export peaks at twice its file, so two in flight add $4 \cdot$ `MaxExportBytes` (D45), and a live tail holds at most $2 \cdot$ `TailCapacity` matches, its ring and one batch (D46), so the statement holds with tails counted in $S$. Full text:
[kafka.md](../../docs/maintainers/kafka.md#d41).

**D42 — The Kafka module correlates by declared fields, in one audited scan.** A topic's correlation fields (headers,
CloudEvents attributes, the key, JSON paths under `ReadPayloads`) each have a kind, and fields of one kind are compared
across topics. Wolverine's and CloudEvents' fields are the defaults. Keys come only from an anchor's literal,
unmasked fields. Related records are one scan over the declared topics the visitor may read, each topic with its own
predicate and grant (`KafkaTopicQuery`), estimated, confirmed, audited once with every topic's predicate, and refused
whole when the visitor names a refused topic. Duplicates are found among the matches already held, and read nothing.
Full text: [kafka.md](../../docs/maintainers/kafka.md#d42).

**D43 — `DataStudio.Kafka.Wolverine` discovers configuration and nothing else.** `UseWolverineKafka()` reads the built
`WolverineOptions` when the cluster registry is first built, through `DataStudio.Kafka.Core`'s internal `IKafkaClusterSource`
(`InternalsVisibleTo`, an exact pin and a version guard), and its tenants once more after the host has started. It starts,
connects and writes nothing, runs no builder callback, and reads no message store or handler graph. Broker-per-tenant
clusters (`{cluster}/{tenant}`, `TenantId` on the resource, listed only under their listed cluster) come from a
reflection probe of three members that fails closed. A discovered topic never replaces a host's descriptor, and
discovery adds no redaction. Full text: [kafka.md](../../docs/maintainers/kafka.md#d43).

**D45 — The Kafka module exports what the visitor holds, redacted, through the circuit.** `ExportRecords` (off by default, needs
`SearchRecords`) writes the matches a search, a live tail or the record page already holds, as JSON Lines, CSV or one value, from
`KafkaMatch` alone, so every mask is the engine's and nothing is read again; a JSON value is written compact on one line, and a CSV
cell a spreadsheet would run is prefixed with `'`. The file is at most `MaxExportBytes`, written before it is sent, two at a time,
and streamed to the browser with `DotNetStreamReference`: no endpoint, no token, nothing kept. The service asks the capability, the
profile, every policy per topic and the payload grant again, refuses whole, is bound to the run on screen, and audits once however
it ends (9606, 9609). `JsonView`'s Download stays off in the Kafka module, because a `data:` link cannot be gated or audited. Full text:
[kafka.md](../../docs/maintainers/kafka.md#d45).

**D46 — The Kafka module's live tail is a scan with no end.** `LiveTail` (off by default, needs `SearchRecords`) starts at each
partition's end (the last stable offset under `read_committed`), keeps the newest `TailCapacity` matches in a ring, renders at most
four times a second, and holds a scan slot and pays the owner's budgets while it reads. It pauses by ending its segment - after
`TailPauseAfter`, a segment's byte or record budget, an owner's budget, a hidden tab, or a cluster that stopped answering - and
resumes from its exact cursor, the gate and the planned grant asked again. It never stops for silence and a caught-up partition is
never stalled. A tail whose circuit loses its connection pauses (`ConnectionLost`, an ordinary pause) through the module's own
scoped `CircuitHandler`, a no-op on a circuit with no tail and the only thing `AddKafka` adds to the host's circuits; on
reconnect it resumes only if still paused for the drop and the browser, asked once, says the tab is shown, through the full
gate with the identity the circuit held when the connection returned, so a changed identity takes effect at the next pause. One audit entry per tail session however it ends (9608), its start logged (9607). Full text:
[kafka.md](../../docs/maintainers/kafka.md#d46).

**D47 — The Kafka module's profiles lower limits per cluster, explicitly, and never grant.** `KafkaClusterBuilder.Profile(KafkaProfile.
Development | Test | Production)` caps a cluster's per-scan limits from a fixed table, gives a profiled cluster its own byte rate
and hourly owner budget (planning reads included), and under `Production` forbids export and live tail even when granted; the
pages leave those controls out and Cluster settings names the setting (#74). A cluster with no profile uses the host's limits, and the Settings page says so; the profile
is never inferred from `IHostEnvironment` or `Environment`. Concurrency and consumer sizes stay process-wide. The ceilings apply in
the engine's plan, and tests prove no profile raises a limit or grants a capability. Full text:
[kafka.md](../../docs/maintainers/kafka.md#d47).

**D48 — The record-index seam narrows a search and proves nothing.** `IKafkaRecordIndex` (public, registered with
`RecordIndex<T>()`, no implementation shipped, none in the sample: K6) is asked once per plain search for one unredacted
equality on the key, a header or a CloudEvents attribute, about the visitor's authorized topics only. Its hints narrow each
partition's range inside the window; every record there is still judged, a hint about another topic is dropped unseen, a failure
reads the whole window (9679, kind only), and a negative is said only of the ranges read, with "Search the whole window" beside it.
Full text: [kafka.md](../../docs/maintainers/kafka.md#d48).

**D50 — The Kafka diagnostics API: what the engine reads, for the host's own code, and never served by the studio.** `IKafkaDiagnostics`
(public, in `DataStudio.Kafka.Core`, registered by `AddKafka()`) answers a cluster's broker count and listed topics, a topic's offsets and retention, and a group's
members, assignments, committed and end offsets and lag (`PlaceOf` says where one member stands), through the pages' cached reads and pooled clients, with every
refusal a value and no visitor's policy asked. A group call answered `NotCoordinatorForGroup` or `GroupCoordinatorNotAvailable` retires the client it ran on (9680); a
call is event 9681 at Debug and is never audited or served. Full text: [kafka.md](../../docs/maintainers/kafka.md#d50).
