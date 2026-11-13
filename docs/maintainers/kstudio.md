# KStudio

KStudio for Apache Kafka® (working name) is the family's Kafka module. Apache Kafka is a trademark of the Apache Software
Foundation. KStudio is not affiliated with or endorsed by the Apache Software Foundation. AGENTS.md states each rule; this document holds its
full text and wins where they differ. D34 and D35 are the shell's half, D36 to D41 the engine's, D45 to D48 M5's.

## D34

**D34 — A module joins the registered studio's shell: KStudio is one.** KStudio is not a third studio. One studio per
process still holds (D30): `AddKafkaStudio()` adds a *module* to the studio the host registered, and the module shares
that studio's mount, circuit, rail, header, audit ring and capability chip.

*Registration.* `AddKafkaStudio()` runs `KStudioVersionGuard` before it names a PostgresStudio or KStudio.Core type
(its public body names none, and `KStudioVersionGuardTests` scans its IL), then registers `KafkaStudioModule` as an
`IStudioModule` with `TryAddEnumerable`, and its options, services and startup guard with `TryAdd`. It reads nothing
the host studio registered, so it may come before or after `AddMartenStudio()` or `AddPostgresStudio()`, and calling it
twice adds one module. The shell's `StudioDescriptor` singleton is the host's static descriptor composed with the
registered modules when it is first resolved (`StudioModuleComposition`); with no module it is that very instance,
which is the proof that a host without KStudio is unchanged. `KafkaStudioStartupGuard` refuses to start a host that
registered no studio, and one that enables a Kafka capability no policy answers (D35). There is no `MapKafkaStudio()`:
a second map call would be a second circuit, and a second place to forget authorization.

*Routes.* A module's page assembly keeps its root (`StudioPageAssembly.KeepsRoot`): `@page "/kafka/topics"` is served
at `{mount}/kafka/topics`, so a module never lands over the host's routes, and the route table still refuses a route two
pages claim. Every id travels in the query string, as D9 says of every id: `?cluster=`, `?topic=`, `?partition=`,
`?offset=`. A topic name is case-sensitive and may look like a file name (`orders.json`), so it is never a path segment.
A search's predicate travels in the fragment (`#q=`): no HTTP request line, access log or Referer carries it, and the circuit receives it once, over its own connection, to fill the box.

*Rail.* A module's sections go before the host's last section (System), in module key order. Every section title and
item label on the composed rail is unique, ignoring case, because the rail keys its entries on them. A module's links
carry no host scope, and its entries' `IsShown` read its own options. The rail's credit names each module.

*Scope.* The layout knows a module's page by the matched page's assembly, never by the address. On it, the header draws
the module's scope selector (KStudio's `ClusterSelector`) and no Find box, and the layout neither gates the body on the
host's scope nor reads or writes `?store=&db=&tenant=`. The host's scope stays in `StudioState`, untouched, and the
rail's host links carry it back. KStudio's own scope is `KafkaState`'s cluster, read from `?cluster=` and written back
only once interactive and after it was read. A broker-per-tenant cluster is one entry per tenant, carrying its tenant id;
no Kafka address names a tenant.

*Audit.* A module's ring entry carries `ScopeKind`, `ScopeLabel` and its own `ModuleScope`, and
`StudioActionLog.GetVisibleAsync` judges it only through that kind's `IStudioAuditScopeGate`, never through the host's
store policy; an entry of a kind no gate answers is hidden. KStudio logs its own events (9600-9649) with its own
properties, never 9200 or 9201, whose templates name a store. A predicate reaches the audit only through the engine's
audit printer, with every literal on a redacted field written `<redacted>`; the visitor's typed text never does.

*Words and assets.* The host's `StudioText` does not change. The anonymous banner adds the module's own sentence. Every
Kafka page ends with the trademark line, and no Kafka logo is drawn. KStudio ships no static web asset: its CSS is in
`studio.css`, in sections headed `KS-…` with classes prefixed `ms-k-`, and a JS helper, if one is ever needed, is a
module-neutral function in `studio.js`.

*Links (M4).* `IStudioRouteLinks` (implemented by MartenStudio) and `IStudioRecordSearchLinks` (implemented by KStudio)
are owned by PostgresStudio, so neither module references the other. A link is pure, escapes every id, and grants
nothing: the destination authorizes again.

## D35

**D35 — KStudio is authorized on a resource of its own, by policies of its own.** D5's three layers hold, on
`KafkaTopicResource(Cluster, TenantId, Topic, Capability)`. *Who gets in* is the host studio's `AuthorizationPolicy`
(or the map builder's `RequireAuthorization()`), because Kafka pages are endpoints of that mapping, at page load and at
circuit connect alike. *Which cluster and topic* is `KafkaStudioOptions.ClusterAuthorizationPolicy`, asked by every
service call at cluster level (`Topic = null`) and once per topic it touches. *Each capability use* is
`CapabilityAuthorizationPolicy`, asked once per topic with the capability named, falling back to the cluster policy when
it is blank. Neither falls back to the host studio's policies: `StoreAuthorizationPolicy`, `SourceAuthorizationPolicy`
and `WriteAuthorizationPolicy` have handlers written for `MartenStoreResource` and `PostgresStudioResource`, and a
handler written for one resource must never be handed another (D30). That is why KStudio has its own
`KafkaAuthorization` rather than a second resource factory on `StudioAuthorization`.

A topic the cluster policy refuses is absent from every list. A request that names one is refused whole, with a refusal
that names nothing, because a search silently narrowed to the allowed topics would report a false negative. The audit
names the topic, and shows it only to a visitor who may read it.

`KafkaStudioCapabilities` default to `false` (D4); metadata needs none (K5). The host's `ReadOnly` is the one master
switch: it turns every Kafka capability off, and KStudio has no switch of its own. Each disabled control names
`KafkaStudioOptions.Capabilities.<name>`, or the host's `ReadOnly` option. A startup guard refuses to start a host that
enables a Kafka capability, with `ReadOnly` off, while both Kafka policies are blank: every admitted visitor could then
read every topic the application's credentials can read. There is no escape hatch; a development host states a
one-line policy.

### The pages (M2)

The Kafka section is Topics (`kafka`), the topic overview (`kafka/topic`), Search (`kafka/search`), the record page
(`kafka/record`) and Cluster settings (`kafka/settings`). Each renders inside `KafkaPageFrame` and reads nothing before
its `Ready`, so prerendering makes no broker call; each service method asks `KafkaAuthorization` itself. Catalog reads
are not audited; scans (9600, 9601) and record views (9604, once per circuit per record) are, through `KafkaActionLog`,
on failure as well as success. A search link's predicate is `KafkaScanPlan.AuditText` in the fragment, never the typed
text, so a literal on a redacted field reaches no address; a pasted link fills the form and never runs by itself. A
record view is a scan of at most 21 offsets (`IKafkaScanner.ReadRecordAsync`) and takes one of the owner's slots. The
settings page reads `KafkaSettingsReader`, and `KStudioReadsNoRawSettingsTests` holds that KStudio never touches the
definition's raw dictionaries. "No match" is always drawn beside its count and every caveat the result carries (D38).

## D36

**KStudio.Core is the Kafka engine: Confluent.Kafka alone, behind a wire seam that exists for the fake.**

KStudio.Core holds everything KStudio knows about Kafka: the catalog of clusters and topics, the topic overview, window
resolution, the scan engine, the search predicates, the decoders, the governor and redaction. It has no ASP.NET Core
(no `FrameworkReference`), references no studio, no Npgsql, no Marten family and no Wolverine (`KStudioCoreFenceTests`,
which also holds the reverse: PostgresStudio, PostgresStudio.Diagnostics and MartenStudio reference no Confluent,
librdkafka, Wolverine or KStudio package or project), and is not packed before M4 (plan K8). Its public types are in namespace `KStudio`: `KafkaEngineOptions` (the base of
KStudio's options), `KafkaClusterBuilder`, `KafkaTopicBuilder`, `KafkaKeyFormat`, `KafkaScanLimits`, and the decoder
extension point (`IKafkaRecordDecoder`, `KafkaDecodeContext`, `KafkaDecodeResult`, `KafkaHeader`, `KafkaDecoders`). Later
milestones add `KafkaRedactionBuilder` (M2, M3), `KafkaPartitioner` (M3) and the correlation and index seams (M4, M5),
each a reviewed baseline change. Everything else is internal, with `InternalsVisibleTo` for KStudio and the two test
projects.

**One client.** Confluent.Kafka is the only Kafka client (plan K2): floor 2.14.0, the version WolverineFx.Kafka 6.61 pins,
latest tested 2.16.0. It brings librdkafka's native binaries (`librdkafka.redist`) at its own version.

**The lag read has a consumer group.** The lag read uses a per-cluster consumer that is never subscribed, through a dependent admin
client, because an admin handle has no consumer group and librdkafka 2.16.0 and earlier crash (#5040) on `NOT_COORDINATOR` in
`ListConsumerGroupOffsets` (a NULL `rk_cgrp` in `rd_kafka_handle_OffsetFetch`: SIGSEGV, exit 139, just after a broker auto-creates
`__consumer_offsets` or whenever a group's coordinator moves). Remove it once the Confluent.Kafka floor contains librdkafka's fix
(PR #5611, merged 2026-10-14; no release has it as of 2.16.0). Every other admin call stays on the plain admin client.

**The wire seam.** Every broker call goes through `IKafkaWire` (`KStudio.Core.Wire`): metadata for all topics, describe
topics with their authorized operations, describe cluster, list offsets (earliest, latest, max timestamp, for a timestamp;
one kind per call, each partition once), describe a topic's configs, a declared group's offsets and description, and a
scan reader (assign with explicit offsets, pause, resume, seek, read, close). It exists so that the fast suite can run the
engine on a hand-written fake broker (the family uses no mocking library), not for a second client. **The fake is held to
Kafka**: `WireContract` and `WindowContract` run on the fake in the fast suite and on `apache/kafka` 3.9 and 4.x in the
live suite, and a fast test may rely on a Kafka behaviour only if the contract proves it on both.

No Confluent type crosses the seam. A record is a `WireRecord` that owns copies of its key, value and headers (a null key
and a tombstone stay null) and never leaves KStudio.Core. Errors are values - `NotAuthorized(operation, resource type,
name)`, `AuthenticationFailed`, `Unsupported(api)`, `OffsetOutOfRange(partition)`, `NotFound`, `Unavailable`, `Failed` -
mapped from Confluent's `ErrorCode`s by one table (`ConfluentErrors`). A refusal by an ACL is an expected state: it is
shown as "Not available: needs `DESCRIBE_CONFIGS` on topic `orders.v1`" and logged once at Debug.

| Call | Needs | Refused shows |
|---|---|---|
| Topic list, partitions, offsets | `DESCRIBE` on the topic (implied by `READ`) | the topic is absent |
| Fetch | `READ` on the topic | "needs `READ` on topic …" |
| Topic configs | `DESCRIBE_CONFIGS` on the topic | "needs `DESCRIBE_CONFIGS` on topic …" |
| A declared group's offsets and state | `DESCRIBE` on the group | "needs `DESCRIBE` on group …" |
| Cluster description | `DESCRIBE` on the cluster | "needs `DESCRIBE` on the cluster" |

**Lifecycle.** Nothing connects when KStudio is registered or the application starts, and nothing is a hosted service.
One admin client per cluster is created by the first read that needs it and disposed when it has been idle for
`AdminIdleTimeout` (5 minutes). A client a call still holds is never destroyed: the pool retires it and disposes it when the last
lease is released. The group-offsets (lag) read is the one admin call that does not run on that client: it runs on a per-cluster
**offsets reader**, a consumer that is never subscribed and never assigned, so it never fetches, with a dependent admin client built
over its handle (`DependentAdminClientBuilder`), pooled, leased and idle-disposed exactly as the admin client is (the pool's log lines
say `admin` or `offsets`), and disposed in the order dependent admin client, `Close`, `Dispose`. It is built as a scan consumer is
(the clone, the overrides, a group id of its own that is never joined, no commit, no offset store, the engine's log and error
handlers). Its queue is drained **before and after** each read, with a poll of zero wait: that is where the host's OAuth
token-refresh hook runs, because librdkafka queues the refresh on the main queue inside `rd_kafka_new`, a consumer redirects that
queue to its poll queue, and the dependent admin client polls only its own result queue; without the drain before, the first lag read
on an OAUTHBEARER cluster would wait out its timeout (and so would the first after every idle disposal). The drain after keeps log
and error events from piling up. The reader commits nothing, joins no group and creates no topic (`ReadNeverWritesMetadataLiveTests`).
Each scan gets one consumer, built, read and closed on a dedicated thread; the scan's
reads wait at most 100 ms, so a cancel, the deadline and the governor are seen within 100 ms. At host shutdown the engine
cancels every scan, waits up to 10 s, and disposes its clients. Catalog reads are single-flight per cluster and key, for
the host studio's `RefreshInterval` (configs, authorized operations and the cluster description for a minute).

**Logging.** Engine events are 9650 to 9699, under the `KStudio.Core.*` categories; librdkafka's own log lines go to
9656 at Debug, never to stderr. An event carries cluster keys, topic names, partitions, offsets, counts, codes and
exception kinds only - never a predicate, a literal, a key, a header or a value - and every string through `KafkaLogValue`,
a port of `StudioLogValue` held equal to it by `KafkaLogValueParityTests`.

## D37

**In KStudio, a read never writes.**

The engine clones the host's configuration when a cluster is registered and never writes the host's instance. Each
client it builds is that clone with these overrides, applied last; each overridden host key is logged at Debug by name,
never by value.

| Setting | Scan consumer | Offsets reader | Admin client | Why |
|---|---|---|---|---|
| `allow.auto.create.topics` | `false` | `false` | `false` | a metadata request must never create a topic |
| `enable.auto.commit`, `enable.auto.offset.store` | `false` | `false` | - | nothing is committed or stored |
| `group.id` | `{GroupIdPrefix}{scan id}`, `kstudio-` by default | `{GroupIdPrefix}{reader id}` | - | Confluent's consumer requires one; it is never subscribed and never committed |
| `group.instance.id` | removed | removed | - | no static membership |
| `group.protocol` | `classic` | `classic` | - | one tested path |
| `client.id` | `{host client.id or rdkafka}.kstudio` | the same | the same | broker metrics and client-id quotas tell the studio apart |
| `isolation.level` | `read_committed`, or `read_uncommitted` when a scan includes uncommitted records | always `read_committed` | - | explicit, never inherited |
| `enable.partition.eof` | `true` | `true` | - | the engine must know a partition ran out |
| `auto.offset.reset` | `error` | `error` | - | an offset deleted during a scan is reported, never skipped |
| `statistics.interval.ms` | `0` | `0` | `0` | no statistics |
| `enable.metrics.push` | `false` | `false` | `false` | the studio's clients never appear in the cluster's client metrics |
| `queued.max.messages.kbytes`, `fetch.max.bytes`, `max.partition.fetch.bytes` | the engine's limits (D41) | 1 MiB queue and 1 MiB fetch (it never fetches) | - | the clients' defaults prefetch 64 MiB per consumer |
| `debug` | removed | removed | removed | the host's debug contexts are not the studio's |
| `plugin.library.paths`, `confluent.monitoring.interceptor.*` | removed | removed | removed | a host's interceptor would make the studio's clients produce to the host's monitoring topic |
| `fetch.message.max.bytes` (librdkafka's own name for `max.partition.fetch.bytes`), `auto.commit.enable` (the legacy topic property that `enable.auto.commit` also names) | removed | removed | - | one setting under two names never reaches the client twice |

Every assign carries explicit offsets, so the consumer never fetches committed offsets, which would need a group ACL.
The engine sets its own log and error handlers on both builders, so librdkafka never writes to the host's stderr, and
never sets a statistics handler or any rebalance or commit handler. The host's builder delegates are never run; the one
builder hook a host may give is `OAuthBearerTokenRefresh`, and the engine applies it to its own clients only.

What the engine never does: send a single-topic metadata request (the request that can auto-create a topic), list all
consumer groups, or call any produce, transaction, commit, create, alter or delete API. The wire seam has no such method,
so neither the Confluent driver nor the fake can be asked for one. `ConfluentOverridesTests` asserts the table key by
key against a host configuration that sets the opposite of every value; `ReadNeverWritesMetadataLiveTests` asks for a
topic that does not exist on a broker that auto-creates topics, as a principal allowed to create them, and finds it still
absent; `ReadNeverWritesScanLiveTests` finds no committed offset and no `kstudio-` group after scans.

Of the admin client's requests, only the single-topic metadata request creates a topic (measured on 3.9.2 and 4.4.0,
even for a default client); the engine never sends it (`ForbiddenConfluentCallsTests`), and
`allow.auto.create.topics=false` is the second line, which `ReadNeverWritesMetadataLiveTests` proves by sending that
request through the engine's own admin client. librdkafka creates a topic a consumer with
`allow.auto.create.topics=true` is assigned (measured on both images by the same class's control), so the scan
consumer's `false` is the only line there, and the same class builds a reader from a host configuration that says `true`
and finds the topic still absent.

**The group coordinator on a pristine cluster.** The scan consumer's group-coordinator lookup makes a broker that has
never hosted a consumer group create its internal `__consumer_offsets` topic (seen on a fresh test broker, CI-FIX-2,
2026-11-11). It is the broker's own internal topic and holds nothing of the studio's, and every cluster with a consumer
group already has it; `ReadNeverWrites*` assert on the topics a host could see, so a test that runs first on a fresh
broker must ignore `__`-prefixed internal topics.

**A key without a group ACL.** Confluent requires a `group.id`, so every scan consumer has one, `{GroupIdPrefix}{scan
id}`, and librdkafka looks up that group's coordinator although the consumer only assigns. Measured (spike S1,
2026-11-10, the same on `apache/kafka` 3.9.2 and 4.4.0 and on Confluent.Kafka 2.14.0 and 2.16.0): a principal with
`READ` on the topics and no group ACL reads every record, from explicit, `Beginning` and `End` offsets alike. Its
consumer sees exactly one non-fatal `GroupAuthorizationFailed` (30), thrown from `Consume` as a `ConsumeException`
within about half a second of the assign, possibly after the first records; the error and log handlers never see it. The
reader returns it as a failure naming the scan's group, and the scan counts it, logs 9671 once at Debug and goes on: it
is never a stop and never a warning. Without a group ACL `Offset.Stored` stalls with no record, no end and no error, so
the reader refuses every negative offset and the engine always assigns a concrete one. A `PREFIXED` `READ` ACL on group
`kstudio-` (or on the host's own `GroupIdPrefix`) silences the one refusal on a scan, and reading never needs it. It matters more
for the **offsets reader** (D36), which lives as long as its pool slot, up to `AdminIdleTimeout` after the last lag read: with no
`DESCRIBE` on its own group, librdkafka keeps looking that group's coordinator up for as long as the reader exists. Measured on
`apache/kafka` 4.4.0 and Confluent.Kafka 2.16.0 with the `nogroup` principal (`debug=cgrp,protocol` on a reader built from the
engine's own table, ten lag reads over 30 s): 42 `FindCoordinator` requests, one a second for the reader's own group on top of the
reads' own (the principal has no ACL on that group, so each is a refused lookup). With a `PREFIXED` ACL on `kstudio-` (`DESCRIBE` is enough, `READ` implies it) the same
run sent 5, all in the first 100 ms, and none after. Grant it where lookups in the broker's authorizer log matter. The consumer groups a host declares for the lag panel are another matter: without `DESCRIBE` on the group,
`DescribeConsumerGroups` and `ListConsumerGroupOffsets` fail with `GroupAuthorizationFailed`, which the overview shows
as "Not available: needs `DESCRIBE` on group `orders-service`" while the topic's own fields still load.
`ListConsumerGroups`, which the engine never calls, answers such a principal with an empty list and no error, one more
reason a group the engine cannot see is never shown as "no groups".

## D38

**KStudio's windows and negatives are truthful.**

A window is half-open, `[from, to)`. The engine resolves it per partition before reading anything, with one list-offsets
call per kind across every partition of every topic:

1. The **start** is offsets-for-times at `from`: the earliest offset whose timestamp is at or after `from`. No earlier
   offset can match, so the start is exact.
2. The **end** is offsets-for-times at `to + margin` under `CreateTime` (later offsets can carry earlier timestamps) and
   at `to` under `LogAppendTime` (monotonic per partition). The margin is 2 minutes by default. The timestamp type comes
   from the topic's `message.timestamp.type`; when the configs cannot be read, the topic is treated as `CreateTime`, so
   never fewer records are read. A record is a **match only if its own timestamp is inside the window**: the margin widens
   the scan, never the window, and the result says that a record stamped more than the margin late is not found after
   the end offset.
3. Under `read_committed` (the default) the end is at most the **last stable offset**: "Stopped at offset L: an open
   transaction holds offsets up to H." Including uncommitted records switches the scan to `read_uncommitted`, and the
   result says so.
4. The end is fixed for the whole scan, which is what makes the cursor exact.

What is reported as a value, never as silence: **retention** before the scan ("records before T are gone"); offsets
**deleted during** the scan (`auto.offset.reset=error` raises the gap, the engine counts the lost offsets and reads on
from the new log start); records **outside the window** or **without a timestamp**; **undecidable** records, by reason;
**stalled** or **refused** partitions; a **compacted** topic ("a time-window search can miss superseded keys and sees
tombstones"); **tiered storage** ("older records are slower and may cost more"); pruned partitions (M3). "No match" is
said only of what was read.

A scan ends for exactly one reason:

| `KafkaScanStopReason` | When |
|---|---|
| `Completed` | every partition reached its end, or ended stalled, refused or with a gap |
| `MatchLimit` | the most matches were kept |
| `ByteBudget`, `RecordBudget` | the scan's bytes or records were spent |
| `OwnerBudget` | the owner's hourly bytes were spent |
| `Deadline` | the scan's longest duration passed |
| `Cancelled` | the visitor stopped it |
| `CallerGone` | its owner went away (a closed circuit) |
| `Shutdown` | the application is stopping |
| `NotAuthorized` | every partition refused `READ` |
| `Unavailable` | the cluster stopped answering |
| `Failed` | a fatal client error or an unexpected exception |

It keeps the next offset of every partition, and "continue from here" reads from those offsets to the original ends, so
two scans read exactly what one would have.

## D39

**KStudio's search predicates are one hand-written grammar, three-valued, and compared as bytes where they can be.**

The grammar is the engine's own parser - no package, and no expression language behind it (spec open decision 6):
conditions on `key`, `header.<name>` (or `header['…']`), `ce.<attribute>` and `any`, with `=`, `^=` (prefix), `~`
(contains, ignoring case; slow), `in (…)` and `is [not] null`, joined by `and`, `or`, `not` and parentheses. M3 adds
`value` and JSON paths (`$.a.b`, `$.a[0]`, `$.a[*].b`) with `<`, `<=`, `>` and `>=` on numbers, behind `ReadPayloads` (see *Values and JSON paths* below).
Strings are quoted with `'` or `"`, a quote doubled inside; there are no backslash escapes. The text form is canonical
and round-trips, and KStudio carries it in the URL's fragment.

**Keys are bytes.** A key literal is compiled through the topic's key format (`Utf8String`, the default, said "assumed"
when undeclared; `JsonString`, which tries both common escapings; `Int64BigEndian`; `Int32BigEndian`; `GuidRfc4122`;
`GuidDotNet`; `Bytes`) into every byte form a producer could have written, and compared without decoding. A
`JsonString` key that no form matched and that contains a backslash is decoded once as JSON (counted as a key decode) and
its text compared for `=`, `in` and `^=`, because serializers escape differently (Python's `json.dumps` writes `é`
for é); the clause's verdict says "compared as bytes; a key with escapes is decoded". Headers compare as the UTF-8 bytes
of the literal.

**Three values.** A condition that needs a decode it cannot make - `~` on a key or header that is not valid text, a
CloudEvents attribute of a value that is not JSON, M3's value conditions on an undecodable value - is *undecidable*, never
false. `any ~ …` is undecidable when no field was true and a key or header that is not masked was not text. Only a
predicate that is true matches; undecidable records are counted by reason (the first reason among the clauses that left the
result unknown) and shown beside the result.

**Cheapest first.** Conditions run in cost order (key bytes, header bytes, binary CloudEvents attributes, text matches,
structured CloudEvents attributes, `any`, then M3's value and JSON paths) and short-circuit; each record decodes each
part at most once, and only when a condition asks. A decode counter proves it in tests. Each condition's verdict ("scan",
"slow scan", from M3 "partition-pruned") is shown before a search runs.

**Errors** name a position and never repeat a literal, because an error message is a sink like any other (D40).

**Decoding.** A value is a tombstone (null), empty, a CloudEvent (binary, by its `ce_specversion` header, or structured,
by its content type), the topic's declared decoder's result, the Confluent wire format ("schema id 42"), JSON, UTF-8 text
or hex, in that order. Nothing is deserialized into a CLR type: JSON is a `JsonElement`, and a host decoder returns JSON
or text that the engine parses under its own caps. Under `SearchRecords` the engine reads the value's kind, its length
and its CloudEvents attributes, and nothing else of it.

### Values and JSON paths

`value` and JSON paths need `ReadPayloads` (the engine's `Payload` grant); a search that names one without it does not
compile. `value is null` and `value is not null` look at the record, not at its content (true of a tombstone only; an empty
value is not null), cost nothing and need only `SearchRecords`.

**The value is a CloudEvent's data** when the record is one: in binary mode the record's value; in structured mode the
`data` member (its raw JSON when `datacontenttype` is absent or contains `json`, a JSON string's text when it is `text/*`),
or `data_base64` decoded and typed by `datacontenttype`; in a batch each event's data, a clause being true if it is true of
any event. A CloudEvent without data has no value, and every value clause is false. Attributes keep their own `ce.` field.

**A JSON path** is `$`, then up to 32 steps: `.name` (letters, digits and underscores, starting with a letter or an
underscore, at most 128 characters), `['name']` (any name, a quote doubled; this is how `$['customer-email']` and
`$['$type']` are written), `[n]` (0 to 999,999,999, no sign, no leading zero) or `[*]` (any element of an array; on an
object it selects nothing). There are no filters and no recursive descent: a path selects by structure, never by content,
so a redaction rule can say exactly which paths it covers. A bad path fails the parse with a sentence that names its
position and says what to write. Member names are compared exactly by a search and ignoring case by a rule.

| Field | `=` and `in` | `^=` | `~` | `<` `<=` `>` `>=` | `is [not] null` |
|---|---|---|---|---|---|
| `value` | the decoded text equals a string literal (ordinal) | prefix (ordinal) | contains, ordinal ignoring case | refused: `numbers compare only JSON paths` | `is null` only (above) |
| `$…` | a selected leaf equals a literal of its own type: a string (ordinal), a number (as `decimal`: `1.5 = 1.50`), `true`, `false`, `null` (a JSON null; an absent member is not null) | a string leaf has the string literal's prefix | a string leaf contains the literal, ignoring case | a number leaf compares with a number literal | refused: write `$.a = null` |
| `any` (with `ReadPayloads`) | the M1 fields, then every string leaf of a JSON value (member names excluded) or the whole text of a text value | as `=` | as `=` | refused | refused |

A selection is existential: `$.items[*].sku = 'A'` is true when any element's `sku` is `'A'`, and `not` negates the
whole clause. A value of another type never matches (`'42'` against `42` is false).

| The value | `value` `=` `^=` | `value` `~` | `$…` | `any`, value part |
|---|---|---|---|---|
| JSON (or CloudEvent data that is JSON) | the UTF-8 text of the bytes, never reformatted | the same | the path's selection; none: false | its string leaves |
| Text | the text | the same | false | the text |
| Empty (0 bytes) | the text `""` | `""` | false | nothing |
| Tombstone, or an event without data | false | false | false | nothing |
| Binary, Confluent wire format | false | **unknown** (`ValueNotText`) | false | nothing |
| Starts like JSON and does not parse | the text | the same | **unknown** (`InvalidJson`) | its text |
| Undecodable, a text over the decode limit, a decoder that failed, rules that could not be applied | **unknown**, with its reason | the same | the same | the same |

Cost classes, cheapest first: 0 key bytes and `key is null`; 1 header bytes; 2 binary CloudEvents attributes; 3 `~` on a
key or header; 4 structured CloudEvents attributes; 5 `any` without `ReadPayloads`; **6 `value`; 7 JSON path; 8 `any` with
`ReadPayloads`**. A record's value is detected and parsed once however many clauses read it, and a match made from the
record reuses it, so a host decoder runs once per matching record; counters prove both.

A number is read with `decimal.TryParse` (invariant culture); a leaf outside `decimal`'s range makes the clause unknown
(`NumberOutOfRange`) unless another selected leaf decides it. The reasons a record is counted as undecidable beside a "no
match" are, with the sentence the page completes as "{n} records …": `could not be decoded as JSON`, `were larger than the
decode limit`, `had a value that could not be read as text`, `had a number too large to compare`, `could not be decoded by
the topic's decoder`, `had CloudEvents data_base64 that is not valid base64`, `could not be searched, because a redaction
rule could not be applied to their value` (and the M2 reasons for keys, headers and CloudEvents).

### Partition pruning

A key search may read fewer partitions, but only when the host declared how its producers choose one
(`topic.Partitioner(KafkaPartitioner.Murmur2)`) and sampling the window proves the claim. A declared partitioner is a
claim, not a fact: records produced with an explicit partition, a different partitioner or a different key serializer,
and partitions added later, all show up as a mismatch when the sample reaches them; the sample reads the start and the end of
each partition's range, so a change in the middle of a window that is undone before its end can go unseen. One mismatch
turns pruning off for the topic.

| Partitioner | Hash | Partition | Deterministic for | Known answers (source) |
|---|---|---|---|---|
| `Murmur2` | MurmurHash2, seed `0x9747b28c`, `m = 0x5bd1e995`, `r = 24`, `h = seed ^ length`, four-byte little-endian words, the tail, then `h ^= h >> 13; h *= m; h ^= h >> 15` | `(h & 0x7fffffff) % n` | every non-null key, an empty one included (`murmur2_random` randomises only a null key) | librdkafka `v2.14.0`, `src/rdmurmur2.c`, `unittest_murmur2` (the Java client's results for eleven keys); Apache Kafka `3.9.0`, `UtilsTest.testMurmur2` |
| `Crc32` | CRC-32/ISO-HDLC (zlib's): reflected polynomial `0xEDB88320`, initial and final xor `0xFFFFFFFF` | `crc % n` (`rd_kafka_msg_partitioner_consistent`) | a non-empty key (`consistent_random` randomises an empty key as well as a null one) | librdkafka `src/rdcrc32.c` (its table); the CRC catalogue's check value for `"123456789"`, `0xCBF43926` |
| `Fnv1a` | FNV-1a 32-bit, taken as a signed value and made positive by negation (`int.MinValue` stays itself), as librdkafka does to match Sarama | `h % n`, unsigned | every non-null key | librdkafka `v2.14.0`, `src/rdfnv1a.c`, `unittest_fnv1a` ("acquired via Go's hash/fnv") |

The hashes are hand-written (`System.IO.Hashing` is not in the budget, hard rule 1). A null key is never deterministic:
the producer picks its partition at random, stickily or in turn. A host may subclass `KafkaPartitioner`; its claim is
verified in the same way.

**When the engine prunes**, per topic, all of these: the request does not ask for every partition (`DisablePruning`); the
topic declared a partitioner; the search **fixes the key** (a key `=` or `in` clause gives its literals; an `and` gives
the smallest set among its children that have one; an `or` gives the union only when every child has one; `not` and
every other clause give none); the key's byte forms are **complete** for the format (every built-in format and a host's
own are, except `JsonString`, which is pruned only when every literal is ASCII letters, digits, space or one of `-_.:@`,
because other characters are escaped differently by different serializers); every form is deterministic for the
partitioner; the keys do not map to every partition (a key set that reaches all of them has nothing to skip, and is not
sampled); the topic's window holds at least 400 offsets (below that, reading it whole is cheaper than proving the
claim); and verification passed. The partitions read are the union of `Partition(form, n)` over every form of every
literal, with $n$ the topic's partition count when the window was resolved.

**Verification** reads, inside the plan and before the estimate, a target of $S = \min(1000, \lfloor o / 4 \rfloor)$ keyed
records ($o$ the topic's offsets in the window, so verification never reads more than a quarter of what it may save),
spread over the $r$ non-empty ranges at $\lceil S / r \rceil$ records each (at least 4). It reads half of each range's
quota from its **start**, where records produced before a partition-count change sit, and the other half from its **end**
(the consumer moves with `Reposition` to the greater of the next offset and the range's end less half the quota), where a
change made late in the window sits, with a short consumer like the size sampler's (1 MiB queue and fetch, explicit
offsets, the scan's isolation), at most 4 MiB (or what is left of the owner's budget, if less) and 5 seconds in all. The
bytes count against the owner's budget, and an owner with none left gets no verification. A range stops at its quota, so a fast partition cannot use up the sample, and records past a
range's end are ignored. Each record whose key the partitioner calls deterministic must be in the partition the
partitioner maps it to; null keys and non-deterministic keys are skipped.

| Outcome | When | Pruning |
|---|---|---|
| `Verified(checked)` | at least 100 keyed records checked, at least one from every non-empty range, no mismatch | on |
| `Mismatch(partition, offset, expected)` | the first record that disagrees; the read stops there | off for the topic; the answer is kept for 10 minutes per (cluster, topic, partitioner name, partition count) |
| `TooFewKeys(checked)` | fewer than 100 | off |
| `RangeWithoutKeys(partition)` | a non-empty range gave no keyed record | off |
| `NotReadable` | `READ` refused, a reader failure, a host partitioner that threw, or the time or the bytes ran out before a pass | off; logged at Debug by kind (event 9676) |

**No positive cache.** Each pruned plan proves the claim on its own window: partitions added since an earlier window, or
a producer that changed, would make an earlier proof wrong. The one exception is **Continue**: a scan's cursor carries the
pruned entries its scan proved, and a continued scan over the same search keeps them (same partitions, same verdict,
same caveat and audit text) without sampling again, whatever is left to read. A cursor read back from its text form carries
positions alone, so its plan decides for itself.

**What a visitor sees.** The plan's ranges keep only the pruned partitions of a pruned topic, so the estimate, the read
and the cursor cover exactly what is read, and Continue reads only those partitions. The key clause's verdict says what
was proved (`PointLookup`: "one partition: murmur2 maps the key to partition 3 of 6 (verified on 1,002 records)";
`PartitionPruned`: "pruned to partitions 1, 3 and 4 of 6 (…)") or why not (appended: "; not pruned: …", for a topic that
declares none, a search that does not fix the key, the JSON-string characters, an empty key, a window of fewer than 400
offsets, keys that map to every partition, the first record that disagrees, too few keyed records, a partition without one, an unreadable sample, or a
visitor who asked for every partition). The plan's notes and the result's caveats say which partitions were read, and
the audit message adds "; pruned to partition 3 of orders" (on a topic whose key is masked, "; pruned to 1 of 6 partitions of
orders": a count, never a partition). A mismatch names the record's partition and offset and never
its key; on a topic whose key a redaction rule masks, it does not say which partition the key maps to either.

## D40

**KStudio redacts inside the engine, before any sink.**

A record leaves KStudio.Core only as a `KafkaMatch`, and `KafkaMatchFactory` is the only way to make one. The factory
runs the topic's rules and the built-in deny-list - the headers `authorization`, `proxy-authorization`, `cookie`,
`set-cookie` and `x-api-key`, masked unless the host opts one out by name - before the match exists, and the match keeps
no unredacted copy: a masked field is `••• (redacted by rule)`. Header, key and CloudEvents rules arrived in M2, JSON-path and
pattern rules in M3, each with the first surface it protects. A rule that cannot be applied, to an undecodable value for example,
withholds the whole value. The wire record never leaves the engine, and a test reads KStudio's compiled references to
prove it, so the view, facets, chips, "copy as", exports and every later sink are built from redacted matches.

A redacted field takes only `=` and `in`: `^=`, `~`, `<`, `>` and `any` would let a visitor read it one character at a
time, and `any` never looks at a redacted field. Every try is a scan, and counts against the owner's budget. The texts the
engine writes for other sinks show a literal on a redacted field as `<redacted>`: the audit's predicate, and a search
permalink's predicate (which then asks the person who opens it to type the value in).

Redaction rule names are compared ignoring case. A CloudEvents attribute rule also masks its `ce_` header (and
`datacontenttype` masks `content-type`), and a header rule on the carrier masks the attribute. A header on the
built-in deny-list can never be searchable; the options validation refuses it. The trace context is read only from
unmasked headers.

### Payload rules

`JsonPath(path)` masks the leaf, or the whole subtree, at a JSON path of the value (of a CloudEvent's data); `Pattern(regex,
name)` masks every match of a regular expression in the value's text. The masking runs in the match factory and where a
clause needs the masked form, only under the `Payload` grant and always **before** the value is cut to what a match keeps,
so a secret past the cut is masked, never lost and never leaked.

| The value | JSON-path rules | Pattern rules |
|---|---|---|
| JSON (or CloudEvent data that is JSON) | each node a rule covers is written as the string `••• (redacted by {rule})`, whatever its type; its concrete path is kept (at most 64, the rest counted) | each string and number leaf: every match replaced by `••• (redacted by pattern {name})`; **a member name that matches withholds the whole value** |
| Text | the value is **withheld** (a path cannot be found in text) | every match replaced in the text |
| Text cut at the decode limit | withheld | withheld (what is left of a match at the cut is not masked) |
| Binary, Confluent wire format | withheld | withheld |
| Tombstone, empty, undecodable | nothing is kept anyway | nothing is kept anyway |

The masked JSON is written compactly, for display and copying (so on a topic with payload rules `value` compares that
compact form, not the producer's spacing). A pattern runs without backtracking, under a timeout of its own: backreferences,
lookarounds, atomic groups and conditionals are refused when the options are validated, as are a pattern over 500
characters, one that matches the empty text, more than 8 patterns or 32 paths on a topic, and a path declared twice.

**Fail closed.** A pattern that times out, a value whose rules use more than 250 ms in all, a member name a pattern matches,
and a path or pattern rule that cannot be applied to the kind of value withhold the value whole: the match keeps no text and
no preview, names the rule in `ValueWithheldBy`, and every value clause on that record is unknown
(`RedactionNotApplied`). The engine logs it once per cluster, topic and rule per ten minutes at Debug (event 9675), with
the rule's kind and the exception's type name and nothing else.

**What a search can do with a masked payload.**

- A clause whose path a rule **covers** (the rule's path, or an ancestor of it, with member names ignoring case; a `[*]` in
  the clause is covered only by a `[*]` in the rule) is a redacted field: only `=` and `in`, and only when the rule is
  `Searchable()`. It compares the **raw** leaf, an exact match only, so no character is ever read at a time, and its literal
  is `<redacted>` in the audit text and in a link. Where several rules cover a path, one that is not searchable wins.
- A clause on an **ancestor** of a masked node is allowed: it can only compare a container with a scalar, and `[*]` never
  selects an object's members, so no clause reaches the masked leaf through it.
- `value`, `any` and every other path compare **what the visitor would see**, masks included. A pattern is never
  searchable, so no exact match on pattern-masked content exists.

**A host decoder is handed only the headers no rule masks**, the built-in deny-list included: a decoder that echoed a header
into its text would otherwise put a masked value into a match.

**Redaction hides content, not size.** A value's byte length stays visible (it is envelope metadata the results show under
`SearchRecords`), so the length of a masked leaf can be bounded from the rest.

## D41

**KStudio bounds the cost of a scan in the engine.**

| Limit | Default | Scope |
|---|---|---|
| Concurrent scans | 2 per process, 1 per owner | refused, never queued |
| Byte rate | 10 MiB/s | per process, shared by every scan |
| Bytes, records, duration | 256 MiB, 5 000 000, 2 minutes | per scan |
| Matches kept, bytes kept per match | 500, 64 KiB | per scan |
| Bytes per owner | 2 GiB in any hour | per owner |
| Confirm before reading | above 64 MiB or 1 000 000 offsets | per scan |

The byte rate is a token bucket the scan thread pays for each record it reads; in debt, it stops consuming, and
librdkafka's bounded queue stops fetching, so the broker sends little more than the engine reads. Broker quotas apply per
principal, and the studio shares the host's: keep the ceiling well below the principal's quota, or a scan throttles the
host's own consumers. On managed clusters bytes fetched are billed, so the per-scan budget is a cost control first.

Memory is stated: $M \approx S \cdot (C + 2mc)$, where $c$ bounds everything a kept match holds (key, headers, CloudEvents
attributes and value, cut in that order) and the 2 is .NET's UTF-16; the defaults ($S = 2$, $C = 64$ MiB, $m = 500$, $c = 64$ KiB)
give about 253 MiB. Here $S$ is the concurrent scans, $C$ a constant for each consumer's own overhead and $m$ the kept
matches. A consumer's prefetch queue $Q$ and fetch size $F$ (8 MiB each at the defaults) sit inside $C$, which was
measured at about 64 MiB for a consumer over six partitions (about 51 MiB of private bytes and 59 MiB of working set,
three times $Q + F$, because librdkafka has overhead per consumer); with the clients' own defaults the same consumer grew
by 154 MiB. The memory is the private memory the process holds: `PrivateMemorySize64` on Windows and `RssAnon`
(anonymous resident pages) on Linux, because .NET's Linux `PrivateMemorySize64` is `VmData`, the size of the mappings,
which counts arenas librdkafka's threads reserve and never touch. $C \approx 64$ MiB holds on both: two scans grew by 204
to 267 MiB against the ceiling of 253 MiB and the live test's allowance of 316 MiB (CI-FIX-2, 2026-11-11). A match spends one budget of $c$ bytes of UTF-8, in order: its key (at most 1 KiB), its headers (at most 64,
4 KiB each, names cut at 256 characters), its CloudEvents attributes (at most 64, 1 KiB each, names cut at 256
characters) and then its value, each cut with a count of what was left out; so a record of 64 headers of 4 KiB keeps the
first of them and none of its value. The formula is a ceiling only because of that budget. The record-size sampler's
consumer is outside $S$ and the governor, so it is small: 1 MiB of queue and 1 MiB of fetch, for at most three seconds
at a time per cluster. The **offsets reader** (D36) is outside $S$ and the scan count too, and lives for as long as its pool slot, up
to `AdminIdleTimeout` after the last lag read, one per cluster: measured on Linux (`apache/kafka` 4.4.0, Confluent.Kafka 2.16.0, eight
readers at once, three runs, `RssAnon` and `/proc/self/status` threads after a GC), it costs about 0.3 MiB of private memory
(0.1 to 0.4 MiB, at the limit of what `RssAnon` resolves), 5 threads and 2 broker connections each, in addition to what the admin client
costs. The live suite allows $1.25 \cdot S(C + 2mc)$ for two scans over records of a 96 KiB value and 64
headers of 4 KiB, and prints what they actually held. Broker egress is bounded separately: with these limits the broker
sent at most $Q + F$ more than the engine had read.

A record read keeps one more value: the record asked for may hold up to $F_v$ bytes of value (KStudio passes the decode limit, 1 MiB by default) whatever is left of its match budget, and its neighbours keep $c$, so a record read holds at most $21c + F_v$, twice that in UTF-16, within one scan slot of $S(C + 2mc)$. Masking never grows a value past twice the decode limit; a value it would is withheld.

**Profiles and the cluster's own buckets (D47).** A cluster's profile caps the limits above in the engine (`min(host, ceiling)`, the table in `docs/kstudio.md`), and a
profiled cluster whose capped rate or hourly allowance is below the host's gets a token bucket and a rolling-hour budget of its own, beside the process-wide ones:
the scan is paced by the slower and stops when either allowance is spent. The byte rate and the allowance guard the cluster's quota and egress; concurrency and the
consumer's sizes guard the process's memory, so they stay the host's. Exports in flight (D45) add $2 \cdot 2 \cdot$ `MaxExportBytes` to the memory statement, 128 MiB at the defaults: the writer keeps each row as it makes it and copies
them once into a buffer of exactly the file's size, so an export peaks at twice its file (the rows and the buffer; no growing stream holds its old and new arrays
together), the file is sent from that buffer without another copy, and at most two exports are in flight.

A live tail (D46) is a scan in this statement: it holds a scan slot, and the matches it holds are its ring plus the one batch its batcher may have taken, at most  cdot$ `TailCapacity` (400 at the defaults, within a scan's 500).

Before reading, the engine estimates a scan: the offsets in its ranges ("about", since offsets are not records) and the
bytes, from a small sample of record sizes. Above the confirmation threshold, `Start` refuses unless the visitor's
confirmation travels with the call. A profile (M5) or a visitor can lower any limit; nothing raises one above the host's.

## D42

**KStudio correlates by declared fields, in one audited scan.**

**Fields and kinds.** A topic's correlation fields are headers, CloudEvents attributes, the key and JSON paths
(`KafkaTopicBuilder.Correlation`). Each has a **kind**, which says what its values mean, and fields of one kind are compared
across topics; fields of different kinds never are. A field's kind is the first that applies: what `As(kind)` names,
`document:{type}` for `Document(...)`, `stream` for `Stream(...)`, the kind of the default it replaces, or its own name (a
header's or attribute's folded to lower case, a path's canonical text). The registry validates every declaration with the rest
of the options and reports all problems together: a header name of 1 to 256 characters of `[A-Za-z0-9_.-]`, an attribute of
`[a-z0-9]{1,20}`, a path the search grammar reads (at most 32 steps), a kind of `[a-z][a-z0-9-]{0,39}` or `document:` and a
type, a document type of 1 to 200 characters with no control character, a store of 1 to 100, at most 16 fields a topic
(defaults included), one field per source and name, and not `Document` and `Stream` on one field. The kind `trace` is for
headers only.

**Defaults.** Unless `WithoutDefaults()` is called, a topic has Wolverine's and CloudEvents' conventions: header
`correlation-id` (`correlation`); headers `id`, `conversation-id` and `causation-id` and CloudEvents `id` (`message`); header
`saga-id` (`saga`); and the trace headers `traceparent`, `ce_traceparent` and `parent-id` (`trace`). A host field with the same
source and name replaces the default. `tenant-id` is not a kind: it is a scope, too broad to correlate on.

**Keys come only from what the anchor shows.** `CorrelationExtractor` reads the anchor's already-redacted fields and only a
literal: not null, not masked, not cut, text (a binary header is hex and is never a key), at most 1,000 characters and
holding no mask. `KafkaMatchField.IsLiteral` and `KafkaMatchMasks.IsMasked` are the one rule, in Core, which the chips, the
copied predicates and the extractor share. A header is read ignoring case and up to 4 times. A JSON path is read only from an
anchor read with the payload, whose JSON value is whole and not withheld, with no masked path left out of the match and the
node neither masked nor under a mask, and only a scalar (a string, a number or a boolean). The trace comes from the
anchor's trace context, and only when the header it came from is a literal. A set holds at most 10 values a kind and 24 in
all, and says how many it left out.

**One query a topic.** `CorrelationQueryBuilder` writes the search of one topic from the key set and that topic's own fields.
It leaves a field out, with a reason that names no value, when the header is on the deny-list, when the field is redacted and
not `Searchable()`, when a trace header is redacted (`^=` is refused on a redacted field), when a JSON path would be searched
without `ReadPayloads`, or when the topic's key format cannot hold the value. One value is `=`, several are `in`; a JSON
path also searches a value that reads as a number or a boolean in that type, because the same id is a string on one topic and
a number on another (not a text with a leading zero). Past the parser's 32 clauses or 2,000 characters whole kinds are
dropped, in the order trace, the host's kinds, documents and streams, saga, message, correlation, each said. A topic left with
no clause is not scanned. Every text it writes parses and compiles on the topic.

**One scan, a predicate and a grant per topic.** `KafkaScanRequest.PerTopic` gives each topic a `KafkaTopicQuery` (text and
grant). `PredicateText` is then `null`, the topics are exactly the queries' topics in order, and each grant includes `Envelope`
and is included in the request's. Each text is parsed and compiled for its own topic under its own grant, and every read of a
record asks the topic's grant (`TopicScanContext.Grant`), so a topic searched under `Envelope` never reads a value although
another topic of the scan does. An error is prefixed `On {topic}: `. Pruning uses each topic's own key clauses. The audit text
is `{topics}: {text}` for each distinct text, the topics that share one named together and the groups joined by `; ` (so 20
topics with one search are one entry that fits the log), and a literal is shown only where **no** topic redacts the field it is
on. A topic's text is never empty or blank: a topic with nothing to search is left out of the request. It is still one scan: one consumer, one slot, one estimate and confirmation, one stop reason, one budget, and
`ReadsValues` is true when a topic whose search reads values has the payload grant. Without `PerTopic` nothing changed.

**Related records, the rules the timeline follows.** The default topic set is the anchor's topic first, then the cluster's
declared topics in name order, each filtered through the cluster and capability policies, at most 20; a refused topic is absent
from a default set, and a visitor-chosen set with a refused topic is refused whole, never narrowed (D35). A topic with no query (`HasQuery` false)
is a lane that says why and is left out of `PerTopic`; a JSON path without `ReadPayloads` is noted on its lane. The
service asks `SearchRecords`, the cluster question, then both policies for every topic in `PerTopic` (after the cluster
question), then the `ReadPayloads` question once for each topic that has a path, and audits once for the scan, on failure as well as success, with the per-topic audit text.

**Duplicates.** `KafkaDuplicateFinder` groups the matches the visitor already holds; it reads nothing, asks no gate and records
no audit. Two records are copies when they are on the same topic and share an identity, which is the first of these a record
has: its `id` header, CloudEvents `source` and `id`, or its key and whole value (read with the payload, neither cut nor
withheld; a masked value is compared as shown, and the group says so). A masked or cut field is no identity. A group has at
least two copies. The pass keys its buckets on the strings the matches already hold, in parts, and copies no value. A header on
the deny-list is never a correlation field: validation refuses it, and the extractor skips it besides.

**Why.** One scan, because the owner may run one at a time (D41) and a timeline is one read with one estimate, one stop reason and
one audit entry; a query a topic, because a field one topic redacts must not fail the others. Kinds are the smallest vocabulary
that says which fields mean the same thing on topics that spell them differently, and keys come only from what the anchor
shows, so a link can reveal nothing the visitor could not already see (D40). Duplicates are same-topic, because a retry tier
re-publishes to another topic and the timeline already shows that as related, not as a copy.

**Tests.** `tests/KStudio.Tests/Correlation/` (validation, extractor, query builder, duplicate finder, with its 20 ms bound at
the largest values), `Scanning/PerTopicScanTests` on the fake and `PerTopicScanLiveTests` on Kafka 3.9 and 4.x (one consumer, a
group never joined), and `Redaction/MaskTextTests`.

## D43

**D43 — KStudio.Wolverine discovers configuration and nothing else.** `UseWolverineKafka()` reads the built `WolverineOptions` once, when
the cluster registry is first built, through KStudio.Core's internal `IKafkaClusterSource` (`InternalsVisibleTo`, an exact version pin and a
version guard). It starts, connects and writes nothing, runs no builder callback, and reads no message store or handler graph. Broker-per-tenant
clusters (`{cluster}/{tenant}`, `TenantId` on the resource) come from a reflection probe of three members that fails closed. A discovered topic
never replaces a host's descriptor, and discovery adds no redaction.

**The seam, and a reversal of D-ENGINE 1.4.** D-ENGINE 1.4 said the adapter would use the public registration API only. It cannot: the public
registration is an options delegate, which `KafkaStudioStartupGuard` reads at host start with no service provider, possibly before Wolverine's
runtime has connected its transports, and a broker-per-tenant registration has no configuration until it has (`KafkaTenant.Compile`, called by
`KafkaTransport.ConnectAsync`). So `KafkaEngineOptions` carries an internal list of source factories, and `KafkaClusterRegistry` - built lazily, on
the first Kafka page, link or catalog read, which is after ASP.NET Core started its hosted services - gets the container and runs every source once,
after the host's own clusters. The pattern is D30's: an internal interface, `InternalsVisibleTo` for the one assembly (and its tests), and
`KStudioWolverineVersionGuard`, which refuses to wire the adapter to any KStudio.Core but its own build before anything of KStudio.Core's
internals is touched (`UseWolverineKafka` names only the public `KafkaEngineOptions`; the body that adds the source is a `NoInlining` method of its
own, and `KStudioWolverineVersionGuardTests` scans the IL to keep it so). A public contributor interface would have frozen a seam with one
consumer into KStudio.Core's baseline.

**What a source may contribute.** Clusters, each with its consumer and admin configuration (cloned by the registry as `AddCluster` clones the
host's, so a source cannot write back), a builder, and the topics it found; and notes. A cluster that breaks a rule of the host's own (a key, a
display name, 50 groups, bootstrap servers, a topic name), or whose key a host cluster holds, is skipped and noted, never thrown: one bad discovery
must not take down the host's own clusters. A source that throws costs a note. The Settings page shows each note in its general words only (`What` and `Reason`, built by `KafkaRegistryNote.Generic`), never the note's own sentence,
which can name a tenant, a topic or a broker a visitor's policy refuses (D35); the log
carries the same general words as event 9677 - `a tenant cluster`, `Wolverine had not connected it` - Warning the first time within ten
minutes for the same source, item and reason and Debug after, and never a tenant id, a topic or a value. Event 9678 (Debug) is for Find in Kafka
failing to read the clusters.

**Tenant entries (decision 6).** An entry's key is `{cluster}/{tenant}`: the registry's key and the engine's `KafkaScanRequest.Cluster`. A host
cluster's key still cannot hold a `/`, so the entry key splits at the first one. `KafkaClusterDefinition` gains `BaseKey`, `TenantId` and `Source`;
`KafkaClusterEntry` keeps `Key` and `TenantId` and gains `Cluster`, the base key. **Every question a policy is asked and every audit scope and
studio log event carries `Cluster` and `TenantId`, never the entry key** (`KafkaAuthorization`, `KafkaActionLog`, `KafkaAuditGate`): a policy never
parses a key, and the gate judges an entry by the question its page asked. **The engine's own events (9650 to 9699) carry the entry key**, because
the engine knows only entries; the studio's events (9600 to 9649) and the Activity entries carry the base key and the tenant. The address carries the entry key escaped (`?cluster=wolverine%2Facme`), as D9 already
does for any id with a `/`.

**The tenant probe.** Wolverine 6.61 keeps `KafkaTransport.Tenants` and the `KafkaTenant` class internal; the public surface can only write them
(`AddTenant`). The probe is the only reflection in the adapter and touches exactly three members: `KafkaTransport.Tenants`, `KafkaTenant.TenantId`
and `KafkaTenant.Transport`. It fails closed. A shape that is not the one read - the property missing, a cache that is not enumerable, an item whose
type is not `Wolverine.Kafka.Internals.KafkaTenant`, a member of another type, a getter or an enumerator that throws - lists no tenant of that
transport and says so once; it never lists the parent's cluster in a tenant's place. A tenant whose child transport has no bootstrap servers is not
listed: Wolverine had not connected it. **Tenants are read only after the host has started.** The source lists the transports' own clusters at once
and, when `IHostApplicationLifetime` is registered and `ApplicationStarted` has not fired, no tenants: its contribution is marked incomplete (with the
token it waits on and the Settings page's sentence, "Tenant clusters are listed once the application has started."). The registry runs an incomplete
source once more, on the first access of the registry after the token has fired, and adds that answer's tenant entries as one new snapshot; it never
changes or removes an entry already listed and never runs the source a third time. With no host lifetime registered the source behaves as if the host had
started. A tenant is listed only under a parent cluster the same source listed (a parent that broke a rule, or whose key was taken, takes its tenants
with it), and two named brokers whose names make one slug list only the first, with its tenants. A tenant id that is blank, over 128 characters, holds a
control character or a `|` (the engine's log-once keys use it as a separator) is not listed, and its note does not repeat it. Wolverine keeps its tenants in a dictionary, so they are listed ordinal by id, at most 200 a transport.
`ForbiddenWolverineCallsTests` fails on any `GetProperty`, `GetField` or other member lookup whose name is not one of the three, and on any
reflection call that invokes or sets. Asking Wolverine for a public, read-only view of a transport's tenants is a decision for the user, not the
adapter's; until there is one, the probe is the guarded path.

**What a discovered topic becomes.** `KafkaTopicDescriptor` gains the source's facts beside the host's declaration. The host's descriptor
of that name - the cluster's own, which replaces a global one, or the global - stays the descriptor, and the source fills only what it left open:
the description; the key format (`Utf8String`, now declared and not assumed: Wolverine writes the envelope's group id as the key); the decoder
(`KafkaDecoders.CloudEvents` when the endpoint's serializer is Wolverine's `CloudEventsMapper`, whose structured events carry
`content-type: application/json` and so would not be detected as CloudEvents); and the partitioner claim from the producer configuration's
`partitioner` setting (`consistent_random` or unset: CRC32; `murmur2`, `murmur2_random`: murmur2; `fnv1a`, `fnv1a_random`: FNV-1a; anything else:
none), which is verified by sampling before any pruning (D39). **Redaction comes only from the host**: discovery adds no rule and removes none, and the
deny-list applies as always. A discovered topic is `Declared`, so it is in the catalog's declared group and in the timeline's default set.

**What the adapter never does.** It reads no message store, envelope table, dead-letter store, node table or runtime agent, and no handler
graph. It calls none of `GetEffectiveConsumerConfig` and `GetEffectiveProducerConfig` (which write the parent's values into a topic's own
configuration), `CreateConsumer`, `CreateProducer`, `CreateAdminClient`, `ConnectAsync`, `InitializeAsync`, `SetupAsync`, `CheckAsync`,
`TeardownAsync`, `BuildListenerAsync`, `Compile` or `ConfigureClient`. It enumerates `KafkaTransport.Topics` and never indexes it - the cache's
indexer creates an endpoint - and never reads the internal topic groups (it takes them from `Endpoints()`). It never runs, reads or keeps
`ConfigureConsumerBuilders`, `ConfigureProducerBuilders` or `ConfigureAdminClientBuilders`, and never uses a Wolverine group id to consume (D37
replaces it). Every configuration it touches is byte for byte unchanged afterwards. The tests: `WolverineNeverWritesTests` (a snapshot of every pair
of every transport, topic and tenant before and after; the builder callbacks replaced by recorders; the configurations handed over are copies),
`ForbiddenWolverineCallsTests` (the IL scan), and the live `WolverineDiscoveryLiveTests`, which also proves on both brokers that KStudio created no
topic, joined no group, committed no offset, and that Wolverine's own group offsets are as they were.

**Fences.** `KStudio.Wolverine` references KStudio.Core and WolverineFx.Kafka only: no studio, no KStudio, no Marten, Weasel or Npgsql
(`KStudioWolverineFenceTests`, by project file, restore graph, built references and its test project's output folder). JasperFx arrives through
WolverineFx, and its source generator is dropped from the adapter's compilation. `KStudio.Tests` does not reference the adapter, so the module's own
output folder stays free of Wolverine (`KStudioFenceTests`).

## D44

**D44 — Links between a studio and a module are pure, internal and grant nothing.** `IStudioRouteLinks` (MartenStudio implements it: a document
by type and id, a stream by id or key, in the record's tenant; for a store with a database per tenant only when the store keeps its rows per tenant and the host listed no `KnownTenantIds`) and
`IStudioRecordSearchLinks` (KStudio implements it: "Find in Kafka") live in PostgresStudio beside `IStudioLinks`, under its rules: no IO, no policy,
every id escaped, and the page a link opens resolves its own scope and asks its own policies again. Neither module names the other, a masked value
never enters a URL, a value travels in the fragment, and the destination authorizes again. External links are host templates with no credential,
opened with `noreferrer`.

**From a record to Marten (decision 7).** `KafkaRecordLinkBuilder` is pure. For each correlation field of the record's topic that names a
document (`Document(type, store)`) or a stream (`Stream(store)`), it takes the field's values from the record through `CorrelationExtractor` - so a
masked, cut, absent or over-long value is never an id - and asks the seam: `ToDocument(new StudioLinkScope(store, tenant), type, id)` or `ToStream`.
The address is the seam's, verbatim; a `null` answer draws the words and a reason ("not a document this studio shows") and no link. Without the
seam (a Postgres Studio host) nothing is drawn. MartenStudio answers `null` for a hidden
document type, for an ambiguous type, for a tenancy of the application's own (nothing says where its tenants are), and for a multi-tenanted store
with no tenant, whether its tenants share a database or each has one (a link would open the visitor's own tenant and say "not found", which is false).
A store that keeps each tenant in a database of its own gets a link with the tenant and no `db` only when it keeps its rows per tenant
(`StoreTenancy.KeepsRowsPerTenant`, the test behind its tenant selector) and the host listed no `KnownTenantIds`: `StudioState.SetScopeAsync` opens the
first database, among those the visitor may see, whose tenant listing carries that tenant (at most 32 searched, TENANT-DB), and no database lists tenants
where documents and events are single-tenanted (master table, one-server and static tenancies in Marten 9.57.0), so there the link would reopen the id
in the database on screen; such a store gets no link yet. **The tenant**, the first that applies: the record's `tenant-id` header when it is a literal; **no Marten link at all when the
header is present and redacted or cut** - the address would carry the hidden value, and the page says so without it; the cluster entry's own tenant
(a Wolverine broker-per-tenant cluster); none. The words cut an id at 24 characters, never the address.

**Find in Kafka (decision 8).** `KafkaRecordSearchLinks` is a singleton that reads the registry's declarations (no broker call, no policy). A link
is offered when some cluster has a declared topic with a correlation field that refers to the record: `Document(type, store)` where `type` equals any of
the request's names (ordinal, ignoring case) or `Stream(store)`, the field's store being unnamed or the screen's store (ordinal, ignoring case). One
cluster entry has such topics: it is named. Several: the entry whose tenant is the request's; otherwise no `cluster=`, and the page uses the visitor's
current cluster. The address is relative to the studio's base, `kafka/related?cluster=…&from=…&to=…#link=document&type=…&store=…&v=…`: every part
escaped, the window in the query when both ends are known, **the value only in the fragment** (a browser never sends one to a server, so an id that is
personal data stays out of server logs and referrers), and the type in the link is the matching field's own spelling, so that the timeline's comparison of
correlation kinds holds. A registry that cannot be built answers `null` and says so once at Debug (event 9678, the exception's type only): a Marten
page never fails because of the module. **A deviation from B.11.2's wording, recorded:** the reverse link opens the timeline's seed mode and not a
one-predicate search permalink. A search has one predicate over all its topics, but the fields that refer to a document differ per topic (the key on one,
`$.orderId` on another); one predicate would read values on every topic or be refused without `ReadPayloads`. Seed mode is still a permalink (the cluster
and window in the query, the value in the fragment), and it searches each topic by its own fields.

**External links (decision 9).** `o.ExternalLink(label, urlTemplate)`, at most eight. The placeholders are `{traceId}`, `{correlationId}`, `{from}`,
`{to}`, `{fromMs}` and `{toMs}`; the time forms are the record's timestamp 15 minutes either side, in ISO 8601 UTC with milliseconds and in Unix
milliseconds. `{traceId}` is the record's trace id only when the header it came from is a literal (the chip rule); `{correlationId}` is the first literal
`correlation-id` header. Every value is `Uri.EscapeDataString`-escaped wherever the placeholder stands, and **a masked, cut or absent value means no
link, never a link with a hole.** The options are validated at the first read, which the startup guard makes at host start (F3): a label of 1 to 40
characters, unique ignoring case; a template of at most 2,000 characters that, once each placeholder is replaced by `x`, is an absolute `http` or
`https` address; only the six placeholders; **no credential - no user info, and no query or fragment parameter (a fragment is unescaped and split on `?`, `&` and `;`, so `#/view?token=x` and `#%2Fview%3Ftoken=x` are seen) whose name, lower-cased and without `-`, `_` and `.`, is `key`, `sig`, `auth`, `pwd`, `passwd` or `code`, or ends in `token`, `secret`, `password`, `signature`, `apikey`, `accesskey` or `credential`** (`api-key`, `X-Amz-Signature`, `X-Amz-Security-Token` and `private_token` are refused; `tokenizer`, `author` and `keyword` are not); **and no placeholder before the end of the authority** (`https://{correlationId}/` would let a value decide the host). The reason is that a
template is rendered into every visitor's page and kept in their browser history. An error names the label and the rule and never the template, whose
text can hold what the rules refuse. A link is `<a target="_blank" rel="noopener noreferrer">`, so no referrer carries the studio's address (cluster,
topic, offset) to the other tool. It is neither gated nor audited: no record is read for it, the values are already on the visitor's screen, and a link
grants nothing.

**What the module shows of Wolverine (UI-E).** The record detail's **Envelope id**, **Sent at** and **Partition key** lines are Wolverine's `id`,
`sent-at` and `partition-key` headers as the record shows them (a redacted one reads redacted, a topic without them draws no line). The topic overview's
discovered facts - role, message types, consumer group, serializer and source - and the Settings page's section on what a source listed and could not
list are drawn from the registry's declarations and notes. **A note's own sentence can name a broker, a tenant or a topic its source did not list, and the visitor may be refused that tenant or topic (D35).
So the Settings page shows each note only in its general words (`Not listed: a tenant cluster, because Wolverine had not connected it.`), under the
same cluster gate as the settings beside it (`KafkaCatalogService.GetDiscoveryAsync`); `ClusterSourceTests` and `KafkaDiscoveredFactsTests` hold it.**

**The tests.** `KafkaRecordLinkBuilderTests` (tenant rules, masked key, header and tenant, cut value, the store, the bound), `KafkaRecordLinksTests`
(the section, the compact form, the envelope lines, the external link line), `KafkaRecordSearchLinksTests` (fragment-only value, store rule, cluster
choice with tenants, a throwing registry), `ExternalLinkTemplateTests` (each rule of the credential check, error text, escaping, both time forms),
`AddKafkaStudioTests` (the host refused at start), `KafkaDiscoveredFactsTests`, and `PublicApiTest_KStudio`, whose baseline moves by `ExternalLink`.

## D45

**D45 — KStudio exports what the visitor holds, redacted, through the circuit.** `ExportRecords` (off by default, needs `SearchRecords`) writes the
matches a search, a live tail or the record page already holds, as JSON Lines, CSV or one value, from `KafkaMatch` alone, so every mask is the engine's and
nothing is read again. The export is the page's convenience over data the visitor was already given; its gate is the service's, asked again at the click.

**What is exported: the records already in memory.** Every byte was already read under its gates, paced, charged to the owner and audited by the scan that
read it; the matches are `KafkaMatch` values, which by construction hold only redacted, capped copies (D40), so redaction in a file is the match factory's and
not a second implementation; memory stays inside D41's statement, since the matches are already counted in $2mc$; and the file is exactly what the visitor saw,
which is what a support hand-off needs. An export as a scan of its own (whole values, more than `MaxMatches` records, streamed) is a 1.x candidate; so is
an export from Related records, whose rows come from several topics with per-topic predicates and grants (D42).

**The writer** is `KafkaExportWriter` in `KStudio.Core/Export/` (pure; ENG-PROFILE): JSON Lines without a BOM, CSV with a UTF-8 BOM and `\r\n` (a
spreadsheet opens a BOM-less UTF-8 file as the local code page and mangles every non-ASCII character), a cell that starts with `=`, `+`, `-`, `@`, a tab or
a carriage return prefixed with `'` (OWASP's CSV injection rule; JSON Lines is never altered), whole rows only up to `MaxExportBytes`, and the file name
`kstudio-{cluster}-{part}-{yyyyMMdd-HHmmss}Z.{ext}` holding no predicate, key or value.

**The service** is `KafkaExportService` (scoped; `KafkaExportGate` is its singleton). The order is hard rule 5's, and every exit from the profile on is audited
once: the capability (`Require(ExportRecords)` requires `SearchRecords` first, so the refusal names the first missing option); the cluster's profile, asked of
the engine's registry and not of the entry the page holds (`Production` never exports, whatever the capability says, recorded as not run: 9609); the input's
own bounds (1 to 20 topics, every match belongs to one of them, `Value` only for exactly one record and only from the record page); the cluster policy at
cluster level and then, per topic, the cluster policy and the capability policy for `SearchRecords` and for `ExportRecords`, any refusal refusing the whole
export (9603); for records read with the payload grant, the grant again (`ForSearchAsync` over the topics that have values), because an export of records
read with values must not be written as envelope rows after access went down: it is refused whole with "Your access changed since these records were read";
two exports in flight at most, a third refused and never queued; the file, built in a `MemoryStream` so the audit knows exactly what was written; the
delivery; and the audit (`ExportRecords`, capability `ExportRecords`, 9606 written or 9609 not). An interop loss is a file that did not arrive, not a failure.
The audit carries the source plan's `AuditText`, in which a literal on a redacted field is already `<redacted>`; the visitor's typed text is not an input.

A record with no timestamp (`WireTimestampType.NotAvailable`) is written with `timestamp` `null` in JSON Lines and an empty cell in CSV, `timestampType`
`NotAvailable` in both, because the epoch the engine keeps in its place would read as 1970.

**Delivery** is a `DotNetStreamReference` to `martenStudio.download.stream` (PostgresStudio's `studio.js`, additive, beside `download.text`), sent in chunks
over the circuit's own connection, under the circuit's own authentication, with no new route, no token in a URL and no antiforgery question (D23). The
alternatives: `download.text` with the content as an interop argument is one JSON-escaped string of up to 32 MiB, a full copy on both sides and the circuit
blocked while it serializes, right for a snapshot of kilobytes and wrong here; a one-time endpoint would be a new HTTP surface outside the circuit, needing the
studio's `AuthorizationPolicy` re-applied, a token in history, proxies and logs, a store of pending files with an expiry, and the first data endpoint either
studio maps (D2, D31).

**`JsonView`'s Download stays off in KStudio** (`AllowDownload="false"` on every `JsonView`; PostgresStudio's `JsonView` is unchanged). Its link is a `data:`
URL built at render: the click never reaches the server, so it can be neither gated per use nor refused on a `Production` cluster after the page rendered nor
audited, and an export must be all three. The bytes are no secret (the page shows the value); the point is the gate and the audit. The record page's
**Export › Value** is the download. `KafkaExportMenuTests` holds that no `data:` link and no `a[download]` is in what the search and record pages draw.

**The control** is `KafkaExportMenu` (public parameters are `object?` where the type is internal, as `KafkaRecordDetail`'s are): a `MenuButton` on a
search's result banner (JSON Lines, CSV, each with "N records, as shown: masked parts stay masked"), on the record page (`This record (.jsonl)` and, when the
value has text, `Value (.json|.txt)`), and, from UI-TAIL, on a tail. With the capability off it is the disabled notice naming
`KafkaStudioOptions.Capabilities.ExportRecords`; on a `Production` cluster it is a disabled button whose title and text are
`KafkaStudioText.ProfileForbids(...)`, which names `KafkaClusterBuilder.Profile` and says that granting the capability is not enough (D4). Both M5
capabilities land with this packet so that the guard, the audit gate and the chip are written once: `Prerequisites(capability)` is `SearchRecords` for every
other capability, `Require` asks the prerequisites first, `KafkaAuditGate` judges an entry with each prerequisite (per topic and at cluster level) where it
used to hard-code `ReadPayloads → SearchRecords`, and `RequireOne` is the capability alone, for a caller that already passed its prerequisites' gate
(`KafkaReadGrants` asking whether a search that reads values may).

**The profile in the UI.** The cluster entry carries the cluster's `Profile` (from `KafkaClusterInfo`); the selector draws `Production profile` / `Test
profile` as a chip (Production in the danger colour), the Settings page a **Profile** section with the capped numbers and, for a cluster with no profile, the
warning "This cluster has no profile; its limits are the engine's defaults. Set KafkaClusterBuilder.Profile(...) to cap them." The search form's allowance
line names the allowance that is lower, the cluster's own or the process's (`KafkaClusterCost.BytesPerHour`), and a refused start names the allowance that ran
out, not the capped value of a plan whose own cluster still has room. The engine says which: `KafkaSpentBudget(Scope, BytesPerHour)` rides on
`KafkaScanStart` (for `OwnerBudgetSpent`), on `KafkaScanResult` (for the `OwnerBudget` stop) and on `KafkaRecordRead`'s sentence, chosen by
`KafkaClusterCost.Spent` (the process's allowance when it is spent, since it stops every cluster; otherwise the cluster's own); a refusal that carries none
says no number. The profile never-grants test (decision 4.7) is `KafkaProfileNeverGrantsTests`: every profile against all sixteen flag combinations, with
`ReadOnly` on and off, through the real export service.

**Tests.** `KafkaExportServiceTests` (each gate refuses, audits and writes nothing; a grant downgrade refuses whole; a third export is refused; a delivery
returning false or throwing; one ring entry per call; `<redacted>` and never the typed text; the size limit leaves whole rows out and counts them),
`KafkaExportCapabilityTests` (the flags, the prerequisites, the audit gate, and the sixteen flag combinations against every profile and `ReadOnly`: nothing is
delivered that the flags did not grant), `KafkaExportMenuTests` (bUnit: the menu, the notices, the record page, Settings, the selector chip, no `data:` link),
`KafkaProfileAllowanceTests`, and the writer's own `KafkaExportWriterTests`. `PublicApiTest_KStudio` moves by `ExportRecords` and `LiveTail` and nothing else.

## D46

**D46 — KStudio's live tail is a scan with no end.** `LiveTail` (off by default, needs `SearchRecords`) starts at each partition's end, keeps the newest
`MaxTailMatches` in a ring, and holds a scan slot and pays the owner's budget while it reads. The first part of this section is the engine's half; *The page* at its
end is UI-TAIL's: the page, the capability's gate, the audit (9607, 9608, 9609) and the hidden-tab pause.

**The plan.** `IKafkaScanner.PlanTailAsync(KafkaTailRequest)` parses and compiles the search per topic through the same `Compile` as `PlanAsync` (one method, so a tail
judges a record exactly as a search does: the same grammar, verdicts, `AuditText` and `ReadsValues`), applies the cluster's profile (D47) to the limits, and
resolves the topics once through `WindowResolver` with a placeholder window of one millisecond at the planning time. Only that read's log start, high watermark,
last stable offset, refusals and notes are used; a tail has no window, no pruning (a key pruning needs a verified sample of a window, D39), no index and no
confirmation. The plan's `Request` is the tail request's image as a scan request, `Tail` the request itself and `IsTail` true; its `Window` exists to carry the
topics' refusals and notes and its times mean nothing. Its estimate is `A live tail reads what arrives.`

**Where each partition starts.** At its end: under `read_committed` the last stable offset, otherwise the high watermark. A transaction open when the tail starts
holds records below the high watermark that commit later, and starting at the high watermark would skip them without a word, which is exactly the untruthful
gap D38 refuses; under `IncludeUncommitted` the high watermark is the end of what can be read. Every range ends at `long.MaxValue` (`KafkaScanCursor.OpenEnd`, written
`-` in a cursor's text, which `TryParse` accepts). With `ResumeFrom`, a partition the cursor names starts at its `Next`; one it does not name (added since) starts at
its end with the note `Partition {p} of {topic} was added after the tail started; it is read from its end.`; a position below the log start moves to it and the records
between are a `KafkaScanGap` (the plan's `Gaps`, which the run starts its own with), never a reset policy; a position past the high watermark (a recreated or
truncated partition) starts at the high watermark and the plan says so (`TailPositionPastEnd`: `Partition {p} of {t} is shorter than where the tail paused (recreated or
truncated); it is read from offset {hw}.`), because what the paused tail had read is not in that partition. A tail's plan carries no note about a window (a margin, a compacted
topic, a partition list fixed at a time): only `IncludesUncommitted` and an unreadable configuration (`ConfigNotReadable`) stay.

**`StartTail`.** The order of `Start`: `ShuttingDown`, `PredicateInvalid`, then `ProfileForbids` (`KafkaProfileCeilings.ForbidsTail`: a `Production` cluster, even when the
capability is granted; nothing is spent or taken), `OwnerBudgetSpent` (the process's and the cluster's own allowance, `KafkaClusterCostControls.For`, as `Start`), then
the limiter (`ProcessBusy`, `OwnerBusy`). **A tail holds one of `MaxConcurrentScans` and one of `MaxConcurrentScansPerOwner` while it reads**, on a thread of its own with
the same `ScanRun`, so D41's memory statement holds with tails counted in $S$ (a tail holds at most  cdot$ `TailCapacity` matches: the ring and one batch, 400 at the defaults, within the 500 a scan keeps; see *The ring*) and "two readers per process" stays true. A scan's
plan handed to `StartTail`, or a tail's to `Start`, is a defect and throws `ArgumentException`. A plan with no partition to follow (every topic refused) starts no
consumer and takes no slot; the run ends `NotAuthorized`.

**The run in tail mode** (`ScanRunInputs.Tail`, which must equal the plan's `IsTail`). It evaluates every record in range whatever its timestamp, and a record with
none is still evaluated and counted in `NoTimestamp`. A partition's end never finishes it: the partition is `CaughtUp` ("live") and its `Next` moves to the end
(the offsets between its last record and the end are control records and aborted batches); the next record clears `CaughtUp`, and librdkafka raises the end again only
after new records were read to it. The store is a ring and never stops the run for `MatchLimit`. Stall detection applies only to a partition that is **behind** (one
that has not reported its end since its last record): a caught-up partition is idle, never stalled, and a tail whose partitions are all caught up is never
`Unavailable` for silence. When every partition has finished (each stalled, refused or unable to go on past deleted offsets) the tail stops `NotAuthorized` if all
were refused, `Unavailable` if any stalled, `Failed` otherwise. The wire's `Unavailable` failures stop a tail as they stop a scan. A tail stops for exactly one reason
(D38): `Paused` (new: the caller paused it, or the tab hid), `Deadline` (the segment ran `TailPauseAfter`), `ByteBudget` or `RecordBudget` (this segment read
`MaxBytesPerScan` or `MaxRecordsPerScan`), `OwnerBudget`, `Cancelled`, `CallerGone`, `Shutdown`, `NotAuthorized`, `Unavailable` or `Failed`; `Completed` and `MatchLimit`
never end one, and `MaxScanDuration` does not apply (`TailPauseAfter` replaces it). The cursor is exact as for a scan: a record is accounted for before any limit is looked
at. The governor, both owner budgets, byte accounting, redaction (the match factory), the decode counters and hard rule 17's overrides are unchanged.

**The ring.** `KafkaMatchStore(capacity, ring: true)` never refuses: it evicts its oldest match, numbers every match from 0 and answers `From(sequence, out skipped)`,
the kept matches from a sequence on and how many with a sequence at or above it were evicted unread. The batcher tracks a sequence, not an index, and
`KafkaScanBatch.Skipped` carries what a page never saw between two batches, so a page can say `{n} matches arrived faster than they could be shown`. Without the ring
the store is the append-only store a search always had. The ring's size is `KafkaScanLimits.TailCapacity`, the smaller of `MaxTailMatches` and `MaxMatches`. A run's and a
progress's `Matches` is the number found (`Added`), which for a search equals the number kept. **The batcher takes matches only when the reader has room**
(`WaitToWriteAsync` on the one-slot channel before `From`): were it to take its batch and then wait, a slow reader would leave it holding a batch beside the ring and the
next pass taking another. A tail therefore holds at most the ring plus one batch,  cdot$ `TailCapacity` matches, which D41's statement counts; `TailCapacity` is not halved
for it. A batch is taken from the ring as it is when the reader is ready, so `Skipped` counts the matches that never reached a batch.

**Pausing releases the slot.** A tail pauses by ending its segment (`Deadline`, a segment's budget, `OwnerBudget`, or `Paused`), not by holding a paused consumer: the slot
and the consumer's memory ($C \approx 64$ MiB) go to whoever needs them, and the next segment starts from the exact cursor (`ResumeFrom`), paced, budgeted and slotted like any
segment. Nothing produced while paused is skipped. Jump to now is a new segment without the cursor.

**Hard rule 17.** The tail is the scan consumer (`ConfluentScanReader`) with the same overrides: assigned with explicit offsets, never subscribed, never committing, its own
group id never joined, `auto.offset.reset=error`. A cursor position deleted by retention while paused is a gap and is read from the log start, never by a reset policy.

**The tests.** Fast, on the fake broker and a manual clock: `TailPlanTests` (the start offsets under both isolations, resume, a partition added since, a gap, the plan's shape, the
same compilation as a scan, `ProfileForbids`, the cluster's allowance), `TailRunTests` (records after the start only, any timestamp, no timestamp, a partition end that never
finishes, no `MatchLimit` with three times the ring and `Evicted` and `Skipped` exact, caught-up and behind stalls, every stop reason, pause plus resume reading each offset once,
the slot held and released, the governor within 10 % of the rate, the consumer's overrides), `KafkaMatchStoreTests` and `KafkaScanCursorTests`. Live, on Kafka 3.9 and 4.x:
`TailLiveTests` (arrival within two seconds, an open transaction shown when it commits and an aborted one never, uncommitted records, pause and resume) and
`ReadNeverWritesScanLiveTests`' tail case (a minute with a pause and resume in the middle: no topic created, no offset committed, no group left).

**The page** (`/kafka/tail`, `KafkaTailPage` and `KafkaTailBody`, under `/kafka` by hard rule 16; rail entry *Live tail*, drawn whatever the capability so that the page can say
which option turns it on, and a link beside *Search this topic*). Its address is `?cluster=&topics=a,b&iso=` with the search text in the fragment (`#q=`), as Search's is (D9):
it fills the form and **never starts a tail**, so a pasted link holds no slot and spends no allowance. A page that can only say why it is off - the capability, `SearchRecords`
or a `Production` profile (`KafkaStudioText.ProfileForbids`, which names `KafkaClusterBuilder.Profile`) - reads nothing from the cluster.

**A tail is a session of segments.** `KafkaTailSession` is the visitor's one tail from Start to Stop; a segment (`KafkaTailSegment`) is one run of the engine, ended by a
pause. The session keeps what the segments leave, so that a pause loses nothing: the ring (the newest `TailCapacity` matches, newest first, built from the engine's
oldest-first batches in one pass), the exact cursor of the last segment, the sums, the gaps, the notes and the caveats. **Resume** plans the next segment with the cursor as
`ResumeFrom`; **Jump to now** plans without it and counts, over the partitions the tail followed, what was produced since the cursor (`KafkaTailService.SkippedByJumping`: the new
start minus the cursor's next offset), which the page states (`Skipped {n} offsets produced while paused.`) and the session's entry repeats. A pause is told apart by
`KafkaTailText.PauseOf`: `Paused` (the visitor, or the hidden tab, whichever the page asked for), `Deadline`, `ByteBudget`, `RecordBudget`, `OwnerBudget` - and **`Unavailable`**,
because a quiet, caught-up tail can end that way after the stall timeout and its cursor is exact, so Resume is offered after it too (`KafkaSearchText.CanContinue` is Search's
and does not list `Paused`). Every other reason ends the tail. The allowance sentence names the one that ran out (`KafkaStudioText.Allowance(result.SpentBudget)`: the process's,
or the profile's on this cluster). The page shows no "behind by N" figure: the engine's progress carries no high watermark, so a partition that is still reading shows
`{topic}/{p} behind`, and one that has reported its end counts in the `N live` chip.

**The gate** (`KafkaTailService`, scoped) is hard rule 5's order before every plan and **every segment**: `Require(LiveTail)` (with `SearchRecords` first), the cluster's profile,
the input's bounds, the cluster policy, per topic the cluster policy and the capability policy for `SearchRecords` and for `LiveTail`, then the payload grant (a tail whose
predicate reads values is refused without `ReadPayloads`). A refused topic refuses the whole tail and names nothing (D35). A resume whose grant is lower than the first
segment's is refused (`GrantChanged`). A policy that changes mid-segment takes effect at the segment's next pause, at most `TailPauseAfter` later, and the docs say so.

**The audit is one entry per session.** 9607 (Information) is logged when the first segment starts, with no ring entry; **9608 and one ring entry** are written when the session
ends - Stop, a terminal stop, a new Start, the cluster changing, or the page being left - by `KafkaTailSession.End`, which is idempotent (`Interlocked`) and takes the visitor's
name captured at the start, so recording needs nothing from a scope that may be disposing. The entry's message carries the segments, the elapsed reading time, the records,
bytes and matches the segments read, the stop reason, the predicate (`AuditText`: a literal on a redacted field is `<redacted>`; the typed text is never recorded), `; values read`,
`; jumped to now {n} times, skipping {m} offsets`, and `; {k} resumes refused: {reason}`. A tail whose first segment is refused or fails to start is one entry and **9609** and
leaves no session; a resume the gate or the engine refuses is counted in the session's entry and writes no 9609. (A capability or policy refusal of a resume still writes its
9602 or 9603 and the ring entry those events always have: a security refusal is an audit event of its own.) The segment's disposal is one shared task, so a page left
mid-segment writes the entry only after the segment's result was added.

**The tab.** `StudioLiveUpdates.VisibilityChanged` (`Func<bool, Task>`, raised on the pool, one change at a time in the order the browser sent them, only when the value changed,
never for the state the watch started in, a throwing handler handed to `Failed`) is how the page hears of it. The page starts a `StudioLiveUpdates` the first time a tail runs, with
a one-second tick that redraws `KafkaTailStatus`, the live line, and nothing else. Hidden: the running segment is paused and the page remembers why. Shown: a tail paused *for
the tab* goes on by itself from its cursor; a tail the visitor paused, or any other pause, waits for Resume. The handler never awaits the resumed read (the raise waits for its
handlers), and the page's reading loop (`DriveAsync`) goes on to the next segment in the same frame, so a tail that pauses every few minutes for a day does not nest a day of calls.

**The connection.** A tail whose browser vanished would read on and hold its owner's scan slot until Blazor tears the circuit down (`DisconnectedCircuitRetentionPeriod`, three
minutes by default), refusing that visitor's other tabs. `AddKafkaStudio` therefore adds `KafkaTailCircuitHandler`, a scoped `CircuitHandler` (`TryAddEnumerable`, the only
registration it changes), which hands `OnConnectionDownAsync` and `OnConnectionUpAsync` to `KafkaTailCircuitRegistry.ConnectionChangedAsync(up)` - the circuit's own scoped list of tail
pages, with no `Circuit` in it, so a test drives it. **The cost to a host is one scoped handler and one scoped registry per circuit, and on a circuit with no tail page both do
nothing: no log, no exception, no allocation beyond themselves.** A drop pauses the running segment through the page's dispatcher (`InvokeAsync`, as the tab does) with the pause cause
`ConnectionLost` - an ordinary pause: the slot is released, the cursor stays exact, nothing is audited, and the line reads `Paused: the connection to this page was lost.`;
`OnCircuitClosedAsync` is not handled, so the `CallerGone` end stays. A return resumes the tail **only** if its cause is still `ConnectionLost` and the tab is not hidden, through the
full gate exactly as the tab's resume (capability, profile, every policy, the planned grant, the slot); a refused resume stays paused with the refusal on screen and is not tried again
by itself. A tab that is hidden when the connection returns resumes when it is shown. A reconnect never resumes a tail the visitor paused or one that paused itself (the clock, a
budget, the cluster). Hard rules 6 and 15 hold: no `async void`, no `async` lambda as an `Action`, and interop loss is caught with `StudioLiveUpdates.IsInteropUnavailable`.

**Rendering.** A batch from the engine (at most four a second, never per record) redraws two child components and not the page: `KafkaTailStatus` (the live line and its chips) and
`KafkaTailRows` (the matches that arrived faster than they could be shown, what the tail does not cover, and the table with its open details). The page renders itself only when a
tail starts, pauses or ends, so the form, `VisibleTopics()` and the details the visitor has open are not drawn for a batch. An idle tail sends no batches, and the one-second tick
redraws only the live line (`KafkaTailBody.Renders` and a test hold both). The rows belong to the session's own cluster - their links, the check of a value's view and the settings
behind the kcat line - never to `State.ActiveCluster`, which the selector may have moved while the tail reads on.

**Leaving and resuming.** A page left while a tail was being started starts nothing (the service checks its token just before `StartTail`) and, if a segment did start, writes the
session's entry from the reading loop (`End(CallerGone)`, idempotent). A resume checks the grant it was *planned* under as well as the tail's first: access that fell between the plan and
the start refuses it. A resume the page makes by itself (the tab shown again) is tried once; if it is refused the page waits for the visitor's Resume. Segments that paused because the
cluster stopped answering are counted in the session's entry. A failed tick or tab handler is logged: Debug in `StudioLiveUpdates`, and throttled (Warning once per kind per ten minutes) by the page.

**Export** is the existing menu (`KafkaExportService`, D45) over the *session on screen*: its cluster, topics, audit text and ring, never the newest plan, which a refused or
failed Start replaces. It is offered when the tail is paused or over, not while it runs. The sample's `SampleTicker` writes a heartbeat to `heartbeats.v1` every two seconds, with the
deny-listed `x-api-key` header and a JSON value under the topic's `$.customer.email` rule, for the tail to follow; it is the sample's producer, never KStudio's.

**The tests** (fast): `KafkaTailServiceTests` (every gate refuses and audits, a refused topic names nothing, a session is one entry however many segments, a resume asks everything
again, a refused resume is counted, the jump arithmetic, a disposed session writes its entry after its segment), `KafkaTailTextTests`, `KafkaTailPageTests` (the disabled states, a link
that never starts, newest first, pause and resume without a gap, jump to now, the hidden and shown tab, the line that redraws alone, one entry per session, leaving the page, export
bound to the session, a dropped connection pausing and a returned one resuming without a gap, a refused resume staying paused, a visitor's pause and a hidden tab left alone,
a circuit with no tail ignoring both), `KafkaTailCircuitTests` (the handler relays to what registered, the empty registry finishes at once, the registration is added once and
scoped), `KafkaTailStylesheetTests` and the two log events in `KafkaStudioRefusalLogValuesTests`; in `PostgresStudio.Tests`, `StudioLiveUpdatesVisibilityTests`.

## D47

**D47 — A profile lowers a cluster's limits, only ever lowers, and is set explicitly.** `KafkaClusterBuilder.Profile(KafkaProfile)` says what kind of
cluster this is: `Development` (the host's limits, unchanged), `Test` (every per-scan limit capped at KStudio's defaults) or `Production` (lower caps, a slower
byte rate and a smaller hourly budget on this cluster, and no export and no live tail even when granted). A cluster with no call has **no profile** and the
host's limits exactly as before; the profile is never taken from `IHostEnvironment` and never from `Environment(...)`'s free text. ASP.NET Core's
environment defaults to `Production` when unset and describes the host process, not the cluster (a laptop host reads a production cluster; a production
host reads its own test cluster), and free text (`prod-eu`, `Prd`) would cap some hosts and silently not others; a host that sets nothing must be the
application it was. It is an enum with a fixed table, not a host-supplied ceiling set, because the host already has a ceiling set (its own `Limits`)
and a fixed, documented table is what makes the word mean the same in every host.

**The ceilings** are `KafkaProfileCeilings` (internal, data and one pure function): every limit is `min(host, ceiling)`, the confirmation thresholds are capped again at the
capped budgets so the result passes `Validate`, and the last row of the table (concurrent scans, the consumer's queue and fetch sizes, the `CreateTimeMargin`
and the stall timeout) stays the host's: concurrency and the sizes guard the **process's** memory, whatever cluster is read, and the margin and the timeout are
about correctness, not cost. `MaxExportBytes`, `MaxTailMatches` and `TailPauseAfter` are Test's values for a `Test` cluster; for `Production` the
export and the tail are forbidden (`KafkaProfileCeilings.ForbidsExport` and `ForbidsTail`, which the services and the engine's `StartTail` ask) and the three
are capped like Test's, because nothing reads them. The table is in `docs/kstudio.md`, *Profiles*. A test holds the table to the public properties of
`KafkaScanLimits` by reflection, so a limit added later fails by name until it has a row, and holds for three host settings (the defaults, every limit at its lowest
bound, every limit at its highest) that no value rises and the result validates.

**Where it applies.** In the engine, so no page can skip it: `PlanAsync` and `ReadRecordAsync` compute `Cap(definition.Profile, settings.Limits)` and only then
apply the request's `Lower`, which may go below the capped value and throws above it, as above the host's. `IKafkaScanner.LimitsFor(cluster)` hands the capped
copy to a caller that needs it (the export reads `MaxExportBytes` there). The correlation scan goes through `PlanAsync` and is covered.

**A bucket and an allowance of the cluster's own.** The byte rate protects the broker principal's quota and the egress bill, which are the **cluster's**, so a
profiled cluster whose capped `BytesPerSecond` is below the host's gets a `KafkaByteRateGovernor` of its own, and one whose capped `OwnerBytesPerHour` is below
the host's an `OwnerByteBudget` of its own: `KafkaClusterCostControls`, a singleton keyed by the cluster's entry key (`{cluster}/{tenant}` is a cluster with a bucket of its
own), made on first use and kept, because a rolling hour and a bucket's debt outlive the scan that started them. The scan thread pays both buckets for
every record, waits on whichever is in the deeper debt, adds to both budgets, and stops with `OwnerBudget` when either is spent; `Start`, `StartTail` and the record
read refuse `OwnerBudgetSpent` when either has nothing left. A cluster with no profile, or one whose profile lowers neither, has none of this and nothing is allocated.

**A profile never grants.** It has no capability in it: `ForbidsExport` and `ForbidsTail` can only refuse, and the limits above can only fall. `KafkaProfileTests`
and `ProfileEngineTests` hold the engine's half; the capability half (every profile over every combination of the four capabilities leaves the usable ones a subset
of those configured, and none under `ReadOnly`) is held where the capabilities live, in the services' tests.

## D48

**D48 — The record index is the host's answer to "where is this key?", and it only ever narrows.** `IKafkaRecordIndex` (public, in `KStudio.Core`, registered
with `KafkaEngineOptions.RecordIndex<TIndex>()`) lets an application that already knows where its records went (a document that stored the partition and offset
its producer got back) point a key or correlation search at offset ranges. KStudio ships **no implementation, keeps no index, and the sample has none**: K6 refuses
a sightings index and any persistent index of ours, and a sample index is the one people copy, so it would be that index one copy away. The docs show twelve lines of
a host implementing it, and the tests use a hand-written `FakeRecordIndex`, as the family uses no mocking library. One index per engine; a second call replaces the
first. It is made once, on first use, from the root provider with `ActivatorUtilities.CreateInstance<TIndex>` behind a `Lazy` that is `PublicationOnly`, exactly as a
topic's `Value<TDecoder>()` is (`KafkaRecordIndexResolver`), so a constructor that throws is tried again on the next plan and never cached as the answer. The options'
registration is read once into `KafkaEngineSnapshot`, like every other value, so nothing written to the options later reaches the engine.

**When it is asked.** Once per plan, in `PlanAsync`, after parsing, the gates and the partition pruning and before the estimate, when an index is registered and the
request is a plain search: no cursor (`ContinueFrom`), no search for each topic (`PerTopic`, which is the correlation scan), and `KafkaScanRequest.IgnoreIndex`
false. A tail and a record read have their own entry points and never reach it. `IndexClauseSelector` takes the search's **top-level conjunction** (an `and`, a
parenthesised `and` inside it, or a lone clause) and the equalities in it on `key`, `header.<name>` or `ce.<attribute>` with a **string literal**: **one literal per
question** (OPEN-7), so an `in` list, a prefix, a contains, a range, `any`, `value`, a JSON path, `is null`, and anything under an `or` or a `not` are never asked. The
first of those in this order is asked: `key`; a header or attribute that is a correlation field (D42, defaults included) on any of the topics; any other; the order typed
breaks ties. **A field any searched topic redacts is never a candidate, even a `Searchable()` one**: its literal never leaves the engine (D40), and then the next clause
is. The query carries the cluster's entry key and tenant, the request's topics (every one has passed every gate), the window, the field, the name, the correlation kind
the topics agree on (none when they disagree), and the literal as typed.

**Narrow, never trust (`IndexNarrower`, pure).** A hint about a topic the search did not name is dropped **before it is counted, logged or shown**, so it cannot even
use up the cap; so are a hint about a partition the plan does not read and one with an empty or reversed range. At most 64 of the rest are used (the number left
over is counted and said). Per (topic, partition) the hints become one range, from the least first offset to the greatest end, **intersected with the plan's own
range** there (the window's offsets, after pruning); an empty intersection drops the partition. The narrowed plan reads that, and every other partition an empty
range, and the estimate is of the narrowed ranges. The scan reads and judges every record in them as in any scan: **a match is a match the scan found, never a hint
the index gave**, and a wrong hint finds nothing. If no hint survives the plan is returned as it was, with a note.

**Negatives stay truthful (D38).** `KafkaIndexNarrowing` carries the engine's sentences: the plan note, the caveat beside matches and negatives ("Read only where the
host's record index pointed (60 of 2,000 offsets); other matches in the window were not looked for."), the narrowed negative, **said only of what this run read** ("No match in the 12 records read from the 2 ranges the host's
record index named. The rest of the window was not read." - never "no match in the ranges", because a run that continues another reads the rest of them and the
first may have found matches there), and the audit suffix (`; narrowed by the host's record index to 2 ranges of orders.v1 (60
offsets)`). The result banner never says "every offset in the window" of a narrowed scan. **Search the whole window** is the same input with `IgnoreIndex`, its own
plan, estimate, confirmation, scan and audit entry, and is bound to the run on screen (`shownRun`), never to the latest plan. **Continue** from a narrowed scan reads
the rest of the narrowed ranges: the cursor carries the narrowing in memory (like its pruning claim; the text form carries positions only), the continued plan keeps it
and never asks again, because its negatives are still about those ranges only.

**What the index cannot do to a search.** It is given the planning token linked with `RequestTimeout`, and the engine does not wait past that for an index that ignores
its token or blocks its thread before it returns a task: the call runs on the pool (`Task.Run`), so the wait starts at once. It is created once and shared by every
search, so it takes singletons (an `IDocumentStore`), never a session. At most 64 hints are kept as they come; the rest are only counted. An exception, a timeout, a constructor that throws, or an answer of `None` costs the narrowing and nothing else: the whole window is read, with the note
`The host's record index did not answer; the whole window is read.` (or `... pointed nowhere in this window ...`). Abandoning the planning is not a failure of the
index and propagates. Event **9679** (Debug, once per cluster per ten minutes) carries the index's type name, the cluster and the exception's **type** (`Timeout` for a
timeout): never the query, the literal, the message or a hint. The audit gains the narrowing suffix and nothing else: the call reads no record, so it is no entry of its
own. `IndexNarrowerTests`, `IndexClauseSelectorTests`, `IndexPlanTests` (with a sentinel literal and exception message that no log line may contain),
`KafkaSearchIndexTextTests` and `KafkaSearchIndexPageTests` hold all of this; `PublicApiTest_KStudioCore` holds the surface (the interface, `RecordIndex<T>()`, and the four
types the interface's one method takes and returns).
