# Testing

This is the full text of D16, D19 and the description of the seven kinds of test, moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D16

**D16 — xunit.v3 3.2.2 and Verify.XunitV3 32.0.0, seven test projects all matching `*.Tests`.**
`tests/MartenStudio.Tests` and `tests/PostgresStudio.Tests` are fast and need no Docker;
`tests/MartenStudio.Integration.Tests` and `tests/PostgresStudio.Integration.Tests` need Testcontainers and
Playwright. PostgresStudio's two have no Marten anywhere in their graph. `tests/KStudio.Tests` (fast) and
`tests/KStudio.Integration.Tests` (Testcontainers, Kafka) are KStudio's, with no Marten in their graph either, and
`tests/KStudio.Wolverine.Tests` (fast, no Docker) is KStudio.Wolverine's. All seven match the glob in
`ITest.TestProjects`, so one `dotnet fallout Test` runs everything. The pins are load-bearing — see *Package budget
changes*.

## D19

**D19 — Playwright browsers are installed by a Fallout `Browsers` target; locally the browser suite skips
with a reason, on CI a missing browser throws.** A developer should not pay a 150 MB Chromium download for
`dotnet fallout Test`, and CI must never report a green run whose browser tests quietly did not happen. The
skip message names the exact command that would install them.

## The seven kinds of test

There are seven kinds of test, in seven projects. Each kind below names MartenStudio's folder; PostgresStudio's two
projects mirror it for the shared studio - its fast suite (public API baseline, the assembly fence, the shared pages,
the 0.5 pages' services and evaluators, the diagnostics API) and its live suite - with no Marten anywhere in their
graph:

1. **Unit** (`tests/MartenStudio.Tests/Unit/`) — SQL builder quoting and parameters, the read-only SQL
   guard theory, id parsing per column type, JSON path and Postgres-expression generation, the search
   grammar and its index verdict, the route table, options validation, the capability × `ReadOnly` truth
   table, the C# record generator, the round-trip differ.
2. **Component** (`tests/MartenStudio.Tests/Components/`) — bUnit over `StudioComponentContext`
   (`JSInterop.Mode = Loose`, UTC time zone, a pinned serializer, real services except hand-written
   `Fake*DataService`s). The JSON viewer, document list, dialogs, layout authorization and read-only
   states, selectors.
3. **Endpoint and metadata** (`tests/MartenStudio.Tests/Endpoints/`) — the authorization matrix, sub-path
   re-rooting at `/ops/marten`, the startup guard (it throws; each of four mappings satisfies it), and
   real `TestServer` requests for the page, its CSS and `blazor.web.js`.
4. **Public API snapshot** (`PublicApiTest.cs`) — Verify + PublicApiGenerator, `ComponentBase` types
   excluded (D20). Hard rule 7 governs the baseline.
5. **Conventions** (`tests/MartenStudio.Tests/Conventions/`) — rules too easy to break silently to leave to
   review: the workflows are the generated ones and still carry the permissions they need; the layout tree
   (`docs/maintainers/layout.md`) matches disk; no `async void` in `src/`; README snippets equal the `#region`
   bodies in the sample's `Program.cs`; every `.ms-table` in `src/` is inside a scroll region (D25); the stylesheet
   still carries the shell rules the 0.2.0 UX pass measured (`StylesheetTests` and its `.Shell` part);
   `MartenStudio.dll` carries no `JasperFxSourceGeneratorApplied` marker while the test assembly does
   wherever JasperFx stamps one (`JasperFxSourceGeneratorTests`, D3).
   `NoAsyncVoidTests` and `TablesAreScrollableTests` both strip comments before scanning, which is
   exactly what makes them capable of being silently vacuous, so each carries an anti-vacuity theory that
   proves the scanner still catches a synthetic offender and still ignores one in prose. 0.5 adds:
   `PostgresStudioFenceTests` and `CoreFenceTests` (hard rule 16); `PostgresStudioWordsTests`, which renders every
   shared health check in Postgres Studio's words and fails on a store word outside its reasoned allow-list, with an
   anti-vacuity fact in Marten's words; `RefusalLogValuesTests`, which holds every audit and refusal log value, and
   every direct Warning that names a route value, to `StudioLogValue`, and holds 9204 to the whole statement that ran;
   `MuteKeyHashTests`, which holds the mute list to the HMAC; `LogLevelsTests`, which reads every partial of
   `StudioLog`, `PostgresStudioLog` and `MartenLog` (a throttled event has no product dispatcher, because the level
   must be the throttle's at the call); `PublicApiShapeTests` and `DiagnosticsApiIsNotServedTests` (D31); `PostgresStudioDiagnosticsVersionGuardTests`
   (PostgresStudio.Tests: the engine's version guard, which refuses a newer or differently built `PostgresStudio.Diagnostics` (a
   missing or older one fails in the loader first), and an IL scan that keeps `AddPostgresStudio`'s own body free of engine
   types so that a newer engine whose members moved still reaches the guard, as MartenStudio.Tests'
   `PostgresStudioVersionGuardTests` does for PostgresStudio and the engine one package up);
   `HealthCodesTests` (hard rule 7's codes table); `NpgsqlVersionTests` and `PostgresStudioSolutionFilterTests`
   (*Build*); and `ReleasePushOrderTests` (*Release engineering*). `ReadmeSnippetTests` holds both READMEs to their
   samples: `README.md` to `samples/MartenStudio.Sample/Program.cs` and `src/PostgresStudio/README.md` to
   `samples/PostgresStudio.Sample/Program.cs`. `NoUnboundedGateAwaitTests` holds every awaited gate to a bound. A
   singleton cache never resolves a scoped, authorization-bearing service: it is handed the scope the caller resolved
   under its own visitor (`DeadLetterGroupCache`, `EventTypeListingCache`), because the rigs share one
   `AuthenticationStateProvider` across DI scopes and so cannot catch a fresh scope with no user; a regression test of
   such a cache registers the real scoped `ServerAuthenticationStateProvider`.
6. **Marten API surface** (`tests/MartenStudio.Tests/Marten/MartenApiSurfaceTest.cs`) — the contract with
   Marten 9.35, pinned two ways at once. A `CompileOnlyAsync` method that is never executed puts every
   member the design depends on into real C#, so the *compiler* checks signatures, generic constraints and
   optional-argument shapes; reflection facts then assert member by member, so a failure names the one
   member that moved rather than producing forty CS1061s. `Marten` is a floor, not a pin — this test is
   what makes that safe. Its doc comments also record the corrections found while verifying the design
   against the real assemblies (2026-09-14), which is why they are worth reading before calling a Marten
   API you have not used here yet. **Marten sources: sibling checkout `D:\Work\marten`** (fetch
   `upstream` tags, check out the tag matching `Directory.Packages.props`) — with JasperFx at
   `D:\Work\jasperfx` and Wolverine, a current consumer, at `D:\Work\wolverine`; read them before
   decompiling. All three are read-only: never modify them.
7. **Integration and browser** (`tests/MartenStudio.Integration.Tests/`) — one `postgres:17-alpine` via an assembly
   fixture (reuse enabled locally unless `MARTENSTUDIO_PG_REUSE=false`; `PostgresFixtureTests` pins the variable's
   name), one Marten schema per collection, everything driven *through* the studio's own services so the gating is
   exercised, not bypassed. A class that needs a server setting of its own (`pg_stat_statements` preloaded, a lowered
   `autovacuum_freeze_max_age`, `max_connections`, `wal_level=logical`, data checksums) starts a `postgres:17-alpine` of
   its own, never reused. `PgVectorLiveTests` starts `pgvector/pgvector:0.8.1-pg17` (`PgVectorServerFixture.Image`), the
   release whose defaults the studio quotes; an image that cannot be pulled skips the class locally, naming the
   `docker pull` command, and fails on CI (D19's rule). `PostgresVersionMatrix`
   (`tests/PostgresStudio.Integration.Tests/Versions/`, an assembly fixture) starts one never-reused server per major on
   first use, `postgres:15.19-alpine` to `18.6-alpine` and `19rc1-alpine` (19 is a supported leg that fails a run; `MatrixVersion`
   says outright whether a leg is a `Preview`, one that reports and never fails - none is today - and a later patch swaps
   19's tag for its GA image; 14 left the matrix in 1.1, its code paths untested), with
   `pg_stat_statements` and B0's `app`/`probe`/`stats`/`mon` roles; `VersionMatrixLiveTests` runs the shared studio's
   checks, every `MatrixReads` read, the held-lock read and the gate table on each (cost: decision-notes.md, *Testing*).
   A W5 packet appends its facts reads to `MatrixReads`. A test that creates, grants or alters a role on the matrix
   takes `PostgresVersionMatrix.IsolatedServerAsync`, never `ServerAsync`: roles are the cluster's, and the matrix's
   no-DDL fingerprint lists them. VERS's Aurora shim (`Versions/AuroraShim.cs`, Aurora's functions from AWS's signatures
   in `pg_catalog`) goes only into a database of its own on the matrix's PostgreSQL 17, never one a self-hosted test
   reads, which the detector would call Aurora; `Aurora/AuroraShimLiveTests` proves A1-A8 on it, and the marker rule D29
   states. `tests/PostgresStudio.Integration.Tests/` is this suite's Postgres Studio twin: the same rules (D19's gate,
   wait on the condition, `MARTENSTUDIO_PG_REUSE`), a container labelled
   `marten-studio.tests=PostgresStudio.Integration.Tests` so it never attaches to this suite's.
   A live test whose assertion needs a `VACUUM` to remove rows works in a database of its own, as `IndexLintLiveTests`'
   T25 test does: a snapshot or transaction id that any other session of the database holds keeps the rows dead but not
   removable (from PostgreSQL 14, sessions of other databases do not count). It also runs `VACUUM (FREEZE)`, not a plain
   one: a plain VACUUM skips a heap page another backend has pinned at that instant (a checkpoint writing out the pages
   the delete dirtied) and leaves its deleted rows' index entries, while an aggressive one waits for the page's cleanup lock.
   A test that needs a `VACUUM` to mark pages all-visible (or frozen) loads with `set synchronous_commit = on`: the
   matrix and the shared container run with it off, a row cannot be hinted committed until the WAL writer has flushed its
   commit, and a VACUUM within that moment sets no page (`BaselineLiveTests`, measured on 14 to 18).
   A live test that resets statistics (`pg_stat_reset()`, `pg_stat_reset_shared()`, and `pg_stat_reset_single_table_counters()`
   too, which moves the database's `stats_reset` from null to a time) works in a database of its own, or on a server of its
   own, as `MaintenanceLiveTests`' T08 test and `SinceRestartLiveTests` do: the statistics window reads a reset time that
   moves between two sweeps as a reset, and every usage verdict (T01, T05, T06) of a class judging the shared `postgres`
   database withholds itself.
   Waits are always wait-on-condition, **never `Task.Delay`**. A test that holds a gate shut awaits it through `Bounded()`
   (`TestBounds`, one copy per fast suite); `NoUnboundedGateAwaitTests` fails on an unbounded await of a gate in either
   fast suite, and CI's fast suites run with a ten-minute blame-hang timeout, so a hang fails with a dump naming the test.
   The Playwright scenarios live in `Browser/`
   (and the Health page's first one in `Health/`): locally they skip with the install command in the
   reason when Chromium is missing; on CI a missing browser throws (D19). The browser suites record enhanced
   navigations (`StudioPage.EnhancedNavigations`) and assert none (`AssertNoPageFetch`, 1.2). The 1.2 `MARTENSTUDIO_TEST_NAV_INTERCEPTION_OFF` run is gone with the switch it set
   (1.3).

## KStudio

`tests/KStudio.Tests` runs the engine on `FakeKafkaCluster`, a hand-written in-memory broker (D36), with no Docker; `tests/KStudio.Integration.Tests` runs it on `apache/kafka:3.9.2` and `apache/kafka:4.4.0`, one broker per image for the whole run (the collections `kafka-3.9` and `kafka-4`). Each broker is KRaft with SASL/PLAIN on the mapped listener, the `StandardAuthorizer`, `allow.everyone.if.no.acl.found=false` and topic auto-creation on, so "no topic created" is proved against a principal that could have created one. The fixture creates its ACLs from the host with `CreateAclsAsync` as `admin` (`kafka-acls.sh` inside the container cannot reach the advertised listener): `reader` has `READ` on topics `ks.` and groups `kstudio-`, `configreader` adds `DESCRIBE_CONFIGS`, `nogroup` has `READ` on `ks.` and no group ACL, and `describeonly` has `DESCRIBE` on `ks.` and `READ` on the groups but no `READ` on a topic. `[KafkaFact]` and `DockerAvailability` skip with a reason locally and fail on CI, as the Postgres suites do. `DescribeCluster` is allowed for every principal on these brokers, so a cluster refusal is proved on the fake only.

**The contract suites.** `WireContract`, `WindowContract` and `ReaderAclContract` (`tests/KStudio.Tests/Contract/`) are abstract classes over `IContractCluster`, a wire plus a seeder; `ReaderAclContract` runs the reader as `nogroup` and as `describeonly`. The fast suite runs them on the fake; the live suite compiles the same files by link (`<Compile Include="../KStudio.Tests/Contract/…" Link="Contract/…" />`), links no fake, and runs them on both images (`WireContractLiveTests`, `WindowResolutionLiveTests`, `ReaderAclLiveTests`). The fake is trusted only where a contract test holds on both images: a fast test that relies on a Kafka behaviour relies on the contract test that proves it, and a fake behaviour the contract does not prove is not one a fast test may lean on. A new fake behaviour comes with its contract test in the same pull request. Seeding is test code, as `admin`; every timestamp is in the past, because Kafka 4 refuses a record stamped more than an hour ahead; topics are named `ks.{label}.{runId}.{n}` and deleted when the cluster is disposed. A transaction's markers reach the partition a moment after `CommitTransaction` or `AbortTransaction` returns, so `LiveContractCluster` waits, bounded at 30 s, until the last stable offset equals the high watermark before the test reads on.

**`KSTUDIO_PACKET` and reuse.** Reuse follows `MARTENSTUDIO_PG_REUSE`, as in the Postgres suites: on locally, off on CI and when the variable is `false` or `0`. The label `marten-studio.tests=KStudio.Integration.Tests.{image tag}` gives each image a reuse identity of its own. An agent run also sets `KSTUDIO_PACKET` to its packet id; the fixture then labels the broker `kstudio.packet={id}`, and labels are part of Testcontainers' reuse hash, so two packets never attach to each other's broker (`KafkaFixtureTests` proves the hashes differ). With the variable unset, on CI or for a developer, nothing changes. Agent runs set `MARTENSTUDIO_PG_REUSE=false` as well, so the broker and its volumes go when the run ends.

**The loopback listener.** Testcontainers.Kafka 4.15.0's startup script ends `KAFKA_ADVERTISED_LISTENERS` with a comma when the container has no additional listener. Kafka 3.9 ignores the empty entry; 4.4 refuses to start ("advertised.listeners values must not be empty"). The fixture therefore adds `.WithListener("localhost:9095")`, a `PLAINTEXT` listener bound to the container's own loopback and mapped to no host port, which fills the empty slot. No test connects to it. Testcontainers 4.16.0 fixes the script ([testcontainers-dotnet#1772](https://github.com/testcontainers/testcontainers-dotnet/pull/1772)): drop the listener in the same pull request that moves the repository's Testcontainers rows to 4.16.0 or later.

**Volumes.** The `apache/kafka` image declares six anonymous volumes, and removing the container with `docker rm -f` leaves them behind. The fixture records the container's volumes when it starts and removes them with the container when it does not reuse it. `KafkaFixtureTests.A_disposed_broker_leaves_no_volume_of_ours` starts and disposes a throwaway 4.4 broker and asserts that none of its volumes is left; it skips, saying why, while reuse is on, because a reused broker keeps its volumes by design. Remove a reused broker by hand with `docker rm -f -v <container>`, which takes its volumes too. Never prune volumes by filter on a shared machine: the other sessions' volumes match the same filter.

**`KStudio.Wolverine.Tests`.** It is fast and runs no Wolverine host: the adapter over a `WolverineOptions` built in the test (Wolverine's fluent endpoint configuration is deferred until its runtime compiles it, so the tests shape the endpoints through their public members, which is what the adapter reads), the tenant probe against the real 6.61 types and against fakes with the probe's three member names, an IL scan of every method of the adapter for what it must never call, and the fences, with an output folder that holds no studio. The live Wolverine test is in `KStudio.Integration.Tests/Wolverine` and starts a real host on the 3.9 and 4.x brokers together (the collection `kafka-wolverine-pair` holds both fixtures): 3.9 for the application's own cluster and 4.x for its tenant `acme`, so a scan of `wolverine/acme` that finds the 4.x broker's record and not the 3.9 broker's proves which broker was read. Wolverine 6.61 does not start in its dynamic code mode without a runtime compiler package, so that host has no handler and runs in `TypeLoadMode.Static`.
