# KStudio

KStudio for Apache Kafka® (working name) is the family's Kafka module. Apache Kafka is a trademark of the Apache Software
Foundation. KStudio is not affiliated with or endorsed by the Apache Software Foundation. AGENTS.md states each rule; this document holds its
full text and wins where they differ. D34 and D35 are the shell's half, D36 to D41 the engine's.

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
lease is released. Each scan gets one consumer, built, read and closed on a dedicated thread; the scan's
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

| Setting | Scan consumer | Admin client | Why |
|---|---|---|---|
| `allow.auto.create.topics` | `false` | `false` | a metadata request must never create a topic |
| `enable.auto.commit`, `enable.auto.offset.store` | `false` | - | nothing is committed or stored |
| `group.id` | `{GroupIdPrefix}{scan id}`, `kstudio-` by default | - | Confluent's consumer requires one; it is never subscribed and never committed |
| `group.instance.id` | removed | - | no static membership |
| `group.protocol` | `classic` | - | one tested path |
| `client.id` | `{host client.id or rdkafka}.kstudio` | the same | broker metrics and client-id quotas tell the studio apart |
| `isolation.level` | `read_committed`, or `read_uncommitted` when a scan includes uncommitted records | - | explicit, never inherited |
| `enable.partition.eof` | `true` | - | the engine must know a partition ran out |
| `auto.offset.reset` | `error` | - | an offset deleted during a scan is reported, never skipped |
| `statistics.interval.ms` | `0` | `0` | no statistics |
| `enable.metrics.push` | `false` | `false` | the studio's clients never appear in the cluster's client metrics |
| `queued.max.messages.kbytes`, `fetch.max.bytes`, `max.partition.fetch.bytes` | the engine's limits (D41) | - | the clients' defaults prefetch 64 MiB per consumer |
| `debug` | removed | removed | the host's debug contexts are not the studio's |
| `plugin.library.paths`, `confluent.monitoring.interceptor.*` | removed | removed | a host's interceptor would make the studio's clients produce to the host's monitoring topic |
| `fetch.message.max.bytes` (librdkafka's own name for `max.partition.fetch.bytes`), `auto.commit.enable` (the legacy topic property that `enable.auto.commit` also names) | removed | - | one setting under two names never reaches the client twice |

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
`kstudio-` (or on the host's own `GroupIdPrefix`) silences the one refusal and changes nothing else; reading never needs
it. The consumer groups a host declares for the lag panel are another matter: without `DESCRIBE` on the group,
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
at a time per cluster. The live suite allows $1.25 \cdot S(C + 2mc)$ for two scans over records of a 96 KiB value and 64
headers of 4 KiB, and prints what they actually held. Broker egress is bounded separately: with these limits the broker
sent at most $Q + F$ more than the engine had read.

A record read keeps one more value: the record asked for may hold up to $F_v$ bytes of value (KStudio passes the decode limit, 1 MiB by default) whatever is left of its match budget, and its neighbours keep $c$, so a record read holds at most $21c + F_v$, twice that in UTF-16, within one scan slot of $S(C + 2mc)$. Masking never grows a value past twice the decode limit; a value it would is withheld.

Before reading, the engine estimates a scan: the offsets in its ranges ("about", since offsets are not records) and the
bytes, from a small sample of record sizes. Above the confirmation threshold, `Start` refuses unless the visitor's
confirmation travels with the call. A profile (M5) or a visitor can lower any limit; nothing raises one above the host's.
