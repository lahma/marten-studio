# KStudio

KStudio (working name) is the Kafka module of the studio family. AGENTS.md states each rule; this document holds its
full text and wins where they differ. D34 and D35, the shell seams and KStudio's authorization, land with the shell
(KStudio M2).

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
`AdminIdleTimeout` (5 minutes). Each scan gets one consumer, built, read and closed on a dedicated thread; the scan's
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

**A key without a group ACL.** Confluent requires a `group.id`, so every scan consumer has one, `{GroupIdPrefix}{scan id}`, and librdkafka looks up that group's coordinator although the consumer only assigns. Measured (spike S1, 2026-11-10, the same on `apache/kafka` 3.9.2 and 4.4.0 and on Confluent.Kafka 2.14.0 and 2.16.0): a principal with `READ` on the topics and no group ACL reads every record, from explicit, `Beginning` and `End` offsets alike. Its consumer sees exactly one non-fatal `GroupAuthorizationFailed` (30), thrown from `Consume` as a `ConsumeException` within about half a second of the assign, possibly after the first records; the error and log handlers never see it. The reader returns it as a failure naming the scan's group, and the scan counts it, logs 9671 once at Debug and goes on: it is never a stop and never a warning. Without a group ACL `Offset.Stored` stalls with no record, no end and no error, so the reader refuses every negative offset and the engine always assigns a concrete one. A `PREFIXED` `READ` ACL on group `kstudio-` (or on the host's own `GroupIdPrefix`) silences the one refusal and changes nothing else; reading never needs it. The consumer groups a host declares for the lag panel are another matter: without `DESCRIBE` on the group, `DescribeConsumerGroups` and `ListConsumerGroupOffsets` fail with `GroupAuthorizationFailed`, which the overview shows as "Not available: needs `DESCRIBE` on group `orders-service`" while the topic's own fields still load. `ListConsumerGroups`, which the engine never calls, answers such a principal with an empty list and no error, one more reason a group the engine cannot see is never shown as "no groups".

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
`value` and JSON paths (`$.a.b`, `$.a[0]`, `$.a[*].b`) with `<`, `<=`, `>` and `>=` on numbers, behind `ReadPayloads`.
Strings are quoted with `'` or `"`, a quote doubled inside; there are no backslash escapes. The text form is canonical
and round-trips, and KStudio carries it in the URL's fragment.

**Keys are bytes.** A key literal is compiled through the topic's key format (`Utf8String`, the default, said "assumed"
when undeclared; `JsonString`, which tries both common escapings; `Int64BigEndian`; `Int32BigEndian`; `GuidRfc4122`;
`GuidDotNet`; `Bytes`) into every byte form a producer could have written, and compared without decoding. Headers compare
as the UTF-8 bytes of the literal.

**Three values.** A condition that needs a decode it cannot make - `~` on a key or header that is not valid text, a
CloudEvents attribute of a value that is not JSON, M3's value conditions on an undecodable value - is *undecidable*, never
false. Only a predicate that is true matches; undecidable records are counted by reason and shown beside the result.

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

## D40

**KStudio redacts inside the engine, before any sink.**

A record leaves KStudio.Core only as a `KafkaMatch`, and `KafkaMatchFactory` is the only way to make one. The factory
runs the topic's rules and the built-in deny-list - the headers `authorization`, `proxy-authorization`, `cookie`,
`set-cookie` and `x-api-key`, masked unless the host opts one out by name - before the match exists, and the match keeps
no unredacted copy: a masked field is `••• (redacted by rule)`. Header and key rules arrive in M2, JSON-path and pattern
rules in M3, each with the first surface it protects. A rule that cannot be applied, to an undecodable value for example,
withholds the whole value. The wire record never leaves the engine, and a test reads KStudio's compiled references to
prove it, so the view, facets, chips, "copy as", exports and every later sink are built from redacted matches.

A redacted field takes only `=` and `in`: `^=`, `~`, `<`, `>` and `any` would let a visitor read it one character at a
time, and `any` never looks at a redacted field. Every try is a scan, and counts against the owner's budget. The texts the
engine writes for other sinks show a literal on a redacted field as `<redacted>`: the audit's predicate, and a search
permalink's predicate (which then asks the person who opens it to type the value in).

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

Memory is stated: with $S$ concurrent scans, a prefetch queue $Q$, a fetch size $F$, $m$ kept matches and $c$ bytes kept
per match, $M \approx S \cdot (Q + F + m \cdot c)$. The defaults ($S = 2$, $Q = F = 8$ MiB, $m = 500$, $c = 64$ KiB) give
about 95 MiB; the clients' own defaults would hold 228 MiB before a single match. The live suite measures it.

Before reading, the engine estimates a scan: the offsets in its ranges ("about", since offsets are not records) and the
bytes, from a small sample of record sizes. Above the confirmation threshold, `Start` refuses unless the visitor's
confirmation travels with the call. A profile (M5) or a visitor can lower any limit; nothing raises one above the host's.
