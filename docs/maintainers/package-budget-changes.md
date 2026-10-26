# Package budget changes

The dated entries behind `AGENTS.md`'s *Package budget* table, oldest first. Hard rule 1 requires a new entry here
before any new package is referenced, and every floor or latest-tested move adds one. Moved from `AGENTS.md` on 2026-10-26.

**2026-09-14 — initial budget.** Everything in the table above, taken at once as the bootstrap commit.
Three things were verified rather than assumed:

- `dotnet restore marten-studio.slnx` produces **zero NU warnings**. The anticipated `NU1608` from bUnit
  2.11.3 asking for `Microsoft.AspNetCore.*` 10.0.10 under central package management did **not** occur —
  the explicit `10.0.12` rows for `Microsoft.AspNetCore.TestHost` and `Microsoft.AspNetCore.Mvc.Testing`
  are enough on their own, and no `NoWarn` was added. If a future bump reintroduces it, the fix is more
  explicit `PackageVersion` rows in a labelled "framework floor alignment" group — never `NoWarn`.
- The `11.0.0-rc` releases of the `Microsoft.AspNetCore.*` test packages are deliberately not taken.
- `Marten` is a floor rather than an exact pin. The repository's own contract with Marten's API is pinned
  instead, by `tests/MartenStudio.Tests/Marten/MartenApiSurfaceTest.cs` — see *Testing*.

**2026-09-14 — `Microsoft.AspNetCore.App.Internal.Assets` 10.0.12 added to `src/MartenStudio`, and a
vendored copy of `blazor.web.js` removed.** It is the only place that file exists on .NET 10, and nothing
already in the budget can produce it: the shared framework does not carry it and no ASP.NET Core assembly
embeds it (checked — `Microsoft.AspNetCore.Components.Server`/`Web`/`Endpoints` have no manifest
resources). It replaces the build-time vendoring P1.2-fix shipped, which resolved the same pack *for the
RCL* and republished the file as `_content/MartenStudio/_framework/blazor.web.js` — working, but frozen at
whatever ASP.NET Core patch built the package. The reference puts the script in the **consumer's** static
web assets instead, through the pack's `buildTransitive` targets. See D22.

Measured before it was taken, with a nupkg in a local feed and a scratch **API-only** host that has no
`.razor` file, does not set `RequiresAspNetWebAssets`, calls `MapStaticAssets()` and never calls
`UseAntiforgery()` (2026-09-14): `_framework/blazor.web.js` appears in the host's own
`*.staticwebassets.endpoints.json`, `GET /_framework/blazor.web.js` and
`GET /marten/_framework/blazor.web.js` both answer 200 with the real 200 645-byte script,
`GET /marten` is 200, the JS-initializer URL the page advertises is 200 under the studio path, and
`POST /marten/_blazor/negotiate?negotiateVersion=1` answers 200 with a connection token. A second scratch
host with **central package management, transitive pinning and a `.razor` file of its own** also builds
clean — worth checking, because the SDK adds an *implicit* `PackageReference` to this pack for such a
project and a `PackageVersion` row beside an implicit reference is `NU1009`. That is exactly why
`samples/MartenStudio.Sample` no longer sets `RequiresAspNetWebAssets`: with the row in
`Directory.Packages.props`, saying it there was NU1009 — and the sample is a better demonstration
without it, because it is now the shape a real host has.

**2026-09-14 — `GitHubActionsTestLogger` held at 2.4.1, not 3.0.5.** 3.x is a Microsoft.Testing.Platform
logger: it is compiled against `Microsoft.Testing.Platform` 2.0.0, while `xunit.v3` 3.2.2 resolves 1.9.1,
and the two are not assembly-version compatible — **both** test projects failed to compile with `CS1705`
("…uses Microsoft.Testing.Platform … which has a higher version than referenced assembly"). 2.4.1 is the
last VSTest-only release and depends on nothing but `Microsoft.TestPlatform.ObjectModel`, which is exactly
the bridge Fallout 10.4.0's `ITest` drives. Pinning `Microsoft.Testing.Platform` forward instead would
fight xunit.v3's own resolution and was not done. Verified the same day that `dotnet fallout Test` with
`GITHUB_ACTIONS=true` loads the logger and emits its GitHub Actions annotations, and that the TRX still
lands in `artifacts/test-results`. **Exit condition:** revisit when xunit.v3 and Fallout's `ITest` both
speak Microsoft.Testing.Platform v2.

**2026-09-19 — the `Marten` floor moved *down*, from `9.35.0` to `9.31.0`.** A floor should be the oldest
version that works, not the newest one that happened to be current when the row was written: a host on
Marten 9.31 must not be forced to upgrade to take this package, and NuGet takes the higher of the two
anyway, so nothing on a newer Marten is affected. Bisected over every stable 9.x on nuget.org, all four
gates run at each candidate (`dotnet restore marten-studio.slnx` with `TreatWarningsAsErrors`,
`dotnet build marten-studio.slnx`, `dotnet test tests/MartenStudio.Tests`, and
`MARTENSTUDIO_PG_REUSE=false dotnet test tests/MartenStudio.Integration.Tests` against a real
`postgres:17-alpine`). 9.31.0 is clean on all four; **9.30.0 is not** — three
`ExtendedProgressionLiveTests` fail in their fixture with
`Npgsql.PostgresException : 42703: column "heartbeat" of relation "mt_event_progression" does not exist`,
because Marten only stopped gating `EventProgressionTable`'s extended columns on
`Events.EnableExtendedProgressionTracking` in 9.31.0 (#5309, commit `d26169ff`, whose lowest containing
tag is `V9.31.0`), and every pre-9.31 store with the flag off therefore migrates a table those three
tests cannot write their row into — probed live at 9.24.0 and 9.30.0, which fail identically, and the
commit is what says the whole 9.24–9.30 span does. Four other walls were found on the way down, and each
one is a reason no lower floor is reachable:

- **Everything up to and including 9.12.0 carries a critical advisory** and never reaches the compiler:
  restore fails with `NU1904` for GHSA-rfx3-98h7-v3xp, "Marten's LINQ provider has SQL injection via
  unescaped string literals", whose range is `>= 7.0.0, <= 9.12.0` and whose first patched version is
  9.13.0. Probed at 9.0.0.
- **Below 9.20.0 there is no `JasperFx.Events.Daemon.ShardFailure`**, which `ProjectionDataService`
  builds out of `mt_event_progression`'s four nullable failure columns: `CS0246`, twice, in `src/`.
  Probed at 9.13.0 and 9.19.0; present from 9.20.0.
- **9.20.0 through 9.22.3 have no `ShardName.HighWaterMarkFor`**, which `MartenApiSurfaceTest` uses to
  tell a bookkeeping progression row from a real shard: `CS0117`, three times. Probed at 9.20.0, 9.21.0,
  9.22.0, 9.22.2 and 9.22.3; it appears in 9.22.4.
- **9.22.4 through 9.23.0 compile clean but fail three `AggregateStreamInvokerTests`** with
  `InvalidProjectionException: No source-generated dispatcher found for …TimeTravelSummaryProjection`,
  because the `JasperFx.Events.SourceGenerator` shipped in those packages emits no dispatcher for a
  non-`partial` `SingleStreamProjection<T, TId>` subclass and the runtime fallback is gone. Probed at
  9.22.4, 9.22.6 and 9.23.0; fixed by 9.24.0.

None of those tests were touched to make an older version pass; a behavioural difference in an older
Marten is what a floor is *for*. At the floor the transitive graph is JasperFx / JasperFx.Events
**2.60.0**, Weasel.Core / Weasel.Postgresql / Weasel.Storage **9.29.0** and Npgsql /
Npgsql.NetTopologySuite **9.0.4** — behind what 9.35.0 brought on the first two (2.69.3 and 9.32.0) and
the same Npgsql, and restore is still free of NU warnings. The "verified against Marten 9.35" notes
elsewhere in this file and throughout `src/` are deliberately left alone: they record what was read on
2026-09-14 and are still true of 9.35, and a floor is a minimum rather than a statement about what a
host resolves. **Exit condition:** raise the floor only when a member the studio needs is genuinely
absent below the new value, and say here which one.

**2026-10-15 — the `Marten` floor moved *up*, from `9.31.0` to `9.39.0`, for two advisories and not for
an API; and both suites now also run at a pinned latest-tested `9.50.2`.** Marten 9.39.0 fixes
**GHSA-q4xm-rhx9-xjm4** (SQL injection through a `GroupBy(…).Where(…)` HAVING operand, critical, affects
9.13.0–9.38.0) and **GHSA-frqq-p5g3-8jq5** (SQL injection through `regConfig` on the full-text ranking
path, critical, affects 9.31.0–9.38.0) — both disclosed in its release notes on 2026-09-22, and neither in
the GitHub advisory database on 2026-10-15 (`GET /advisories/{id}` answers 404 for both, and
`JasperFx/marten`'s published advisories are still only GHSA-rfx3-98h7-v3xp and GHSA-vmw2-qwm8-x84c). The
day they are published, restore at any floor below 9.39.0 fails with `NU1904` under
`TreatWarningsAsErrors` — the same wall as GHSA-rfx3 at ≤ 9.12.0, arriving on a CI run nobody changed.
Moving first means the break never happens, and a package that sets a floor should not set it on an
injectable Marten anyway. The studio itself reaches neither sink: its reads are raw parameterised SQL and
its writes go through `StoreObjects` (D6). Nothing in the code needed the move — the 2026-09-19 entry
above still records what it needs (9.31.0) — so this is the first floor raised for a reason other than a
missing member, and the exit condition widens to say so.

All four gates were run at 9.39.0 exactly as on 2026-09-19 — `dotnet restore marten-studio.slnx` with
`TreatWarningsAsErrors` (zero NU warnings), `dotnet build marten-studio.slnx` (0 warnings), the fast
suite (3 417 passed) and `MARTENSTUDIO_PG_REUSE=false dotnet test tests/MartenStudio.Integration.Tests`
against `postgres:17-alpine` (729 passed, 9 opt-in skips) — and then again at 9.50.2 through
`-p:MartenStudioMartenVersion=9.50.2` and through `dotnet fallout Test --marten-version latest`: zero NU
warnings, 0 build warnings, 3 417 fast and 729 live passed with the same 9 opt-in skips (the 1.2M-row
passes and the screenshot retakes). No test was touched to make either end pass, and `PublicApiTest`
passes at both against an untouched baseline. The transitive graph:

- **At the floor, 9.39.0:** JasperFx / JasperFx.Events **2.74.0**, Weasel.Core / Weasel.Postgresql /
  Weasel.Storage **9.33.0**, Npgsql / Npgsql.NetTopologySuite **9.0.4**, DistributedLock.Core 1.0.8 /
  DistributedLock.Postgres 1.3.0, Polly.Core 8.6.5.
- **At the latest tested, 9.50.2:** JasperFx / JasperFx.Events **2.87.1**, Weasel.* **9.45.0**, Npgsql
  **9.0.4** (unchanged), and three packages the floor does not have, all **transitive-only** — the studio
  references none of them directly and must not start to, because none is in this budget:
  **System.Linq.Dynamic.Core 1.7.4** (JasperFx has depended on it since 2.84, for Dynamic LINQ document
  queries; two advisories in its history, both behind 1.7.4: CVE-2023-32571 / GHSA-w65q-jcmv-28gj,
  critical, `< 1.3.0`, and CVE-2024-51417 / GHSA-4cv2-4hjh-77rx, high, `< 1.6.0`), and
  **Patched.DistributedLock.Core 1.0.11 / Patched.DistributedLock.Postgres 1.3.3**, which replace
  DistributedLock.Core / DistributedLock.Postgres from Weasel.Postgresql 9.34 on - Marten 9.39.1, not 9.45 as this
  entry first said (corrected by W5-S2-fix, 2026-10-26: `git show 9.34.0:src/Weasel.Postgresql/Weasel.Postgresql.csproj`
  references `Patched.DistributedLock.Postgres`, 9.33.0 the upstream package) - (JasperFx/weasel#614,
  "Use patched advisory lock", merged 2026-09-24). They are a JasperFx-maintained fork, owned on
  nuget.org by **Hawxy**, a JasperFx contributor — not packages from DistributedLock's own author, even
  though their nuspec still carries the upstream authors and repository URL.

Three things changed in how the repository consumes Marten, all measured rather than assumed:

- **The version is a property, defined once.** `MartenStudioMartenFloor` and `MartenStudioMartenLatest`
  in `Directory.Packages.props`; `Marten`'s `PackageVersion` reads `$(MartenStudioMartenVersion)`, which
  defaults to the floor; `build/Build.Marten.cs` resolves `--marten-version floor|latest|<exact>` and
  passes it to Restore, Compile and Test; `marten-latest.yml` runs both suites with `MARTEN_VERSION:
  latest` on every push and pull request to `main`. The latest-tested version is exact and bumped by a
  pull request of its own, never floating. Pack refuses anything but the floor, in the build and in
  `MartenStudio.csproj` — which checks both the property and what the last restore resolved
  (`"Marten/<floor>"` in `project.assets.json`, which is where NuGet takes the nuspec's range from), and
  refuses when there is no assets file.
- **The shipped assembly is compiled without the JasperFx source generator.** Marten bundles
  `JasperFx.Events.SourceGenerator` in its own `analyzers/`, and from JasperFx 2.75 (Marten 9.40) it emits
  `[assembly: JasperFxSourceGeneratorApplied]` into every assembly it processes — so `MartenStudio.dll`
  built at 9.50.2 carried it and `PublicApiTest`'s received file gained the line. The obvious fix,
  `ExcludeAssets="analyzers"` on the reference, **does nothing**: NuGet records the
  exclusion (the include flags in `project.assets.json` drop `Analyzers`), but SDK 10.0.402's
  `ResolvePackageAssets` adds the analyzers of every package in the lock file without consulting them,
  and the generator still ran — in this project and in a minimal one without central package management
  (decompiled `ResolvePackageAssets.CacheWriter.WriteAnalyzers`; built with `EmitCompilerGeneratedFiles`).
  The removal is therefore a target in `MartenStudio.csproj`, `BeforeTargets="CoreCompile"`, that drops
  the one `Analyzer` item, the same item-level surgery JasperFx.Events' own dedupe target does. Measured:
  the nuspec is **byte-identical** with `ExcludeAssets` and without (`<dependency id="Marten"
  version="9.39.0" exclude="Build,Analyzers" />` either way — analyzers are in the default
  `PrivateAssets` already), and the target touches nothing NuGet sees; at 9.39.0 the generator emitted no
  file for the studio at all, and at 9.50.2 the marker was the only one, so nothing in `src/` relied on
  generated code. The sample and both test projects keep the generator, which still flows to them through
  the same unfiltered resolution; `JasperFxSourceGeneratorTests` pins both halves. That flow is not a
  promise: dotnet/sdk `main` has an opt-in `RestoreEnableAnalyzerAssets` path that does honour the include
  flags, and if it becomes the default, `MartenStudio.Tests` — which reaches Marten only through its
  `ProjectReference` to `src/` — would lose the generator (so would the integration suite, whose
  references to Marten are project references too), and `JasperFxSourceGeneratorTests` on the latest leg
  is what would say so. The fix then is a direct `Marten` reference in the test projects, not a looser
  test.
- **Behavioural differences between 9.39.0 and 9.50.2, read from the release notes and the source at
  `V9.50.2` / JasperFx `V2.87.1`, none of which fails a test at either end:**
  `DefaultTenantUsageDisabledException` now derives from `JasperFx.Events.DefaultTenantUsageDisabledException`
  rather than `MartenException` (9.41, #5514) — nothing in `src/` catches through `MartenException`. The
  read resilience pipeline no longer retries a timeout or `57014` (9.46, #5566) and a commit has its own,
  narrower pipeline — `55P03` is still retried on reads, so the reason `DocumentWriteService` runs its
  lock statements on `IQuerySession.Connection` still holds. Marten's progression statement gained
  `last_updated` (9.43, #5541) — the studio's own read does not select it yet, and the two bookkeeping row
  names it excludes are unchanged. During a rebuild, `StartAgentAsync(string)` on that projection's shard
  now returns without starting anything (logged at Information, jasperfx#1009, JasperFx 2.86) where the
  floor's JasperFx 2.74 has no such guard and can start a continuous agent beside the replay, whose agent
  it cannot see; and a second rebuild of the same projection throws
  `ProjectionAlreadyRebuildingException`, an `InvalidOperationException` that the operation tracker
  reports — both safer than the floor, and both left to the rebuild-awareness work, since the studio's
  Start button does not yet know a rebuild is running. Drains are bounded by `StopAndDrainTimeout`
  (9.47); the HotCold leadership lock is session-scoped again (9.41) — the studio reads no daemon lock
  today. #5665 (`AllObjects()` offline) is on Marten `master` but in no tag up to `V9.50.2`; hard rule 14
  is unchanged.

**Exit condition:** raise the floor when a member the studio needs is genuinely absent below the new
value, *or* when a published advisory makes restore at the floor fail (`NU1901`–`NU1904` under
`TreatWarningsAsErrors`) — and say here which. Bump `MartenStudioMartenLatest` when a newer stable 9.x
ships, in its own pull request, with both suites green at it and every difference recorded.

**2026-10-20 — the latest-tested Marten moves from `9.50.2` to `9.51.0`; the floor stays at `9.39.0`.** Marten 9.51.0
was released 2026-10-19. Its tag is `v9.51.0`, with a lower-case `v` (every earlier tag is upper-case), and it contains
all of 9.50.3 (2026-10-15). Nothing in the studio needs it, so the floor and its reasons are unchanged. It does bring
what the studio refused without, and those light up at run time behind probes in `RuntimeFeature` (D28's pattern), each
pinned at both ends by `MartenRuntimeProbeTests`: Skip event and Rewind subscription for one tenant under
`UseTenantPartitionedEvents`, compaction of a stream whose events carry DCB tags, and the Schema screen's wording of
what its buttons may create.

The gates were run at both ends:

- `dotnet restore marten-studio.slnx -p:MartenStudioMartenVersion=9.51.0` with `TreatWarningsAsErrors`: zero NU
  warnings, so no `NU1901`–`NU1904` for anything in the graph.
- `dotnet build marten-studio.slnx` at 9.39.0 and at 9.51.0: 0 warnings at both.
- The fast suite at both: 5 987 tests; 5 873 passed with 114 version-gated skips at the floor (every one a LINQ-mode
  test, which needs Marten 9.48), 5 986 passed with 1 skip at 9.51.0 (its mirror, the LINQ-mode test of a Marten older
  than 9.48).
- Every live class under `Events`, `Schema` and `Query`, with `DaemonHealthLiveTests`, `EventHealthSweepLiveTests` and
  `EnumerationAppliesLiveTests`, at both, with `MARTENSTUDIO_PG_REUSE=false`.
- `PublicApiTest` passes at both against an untouched baseline, and so does `JasperFxSourceGeneratorTests`. At 9.51.0
  (JasperFx 2.91.0) the generator, when allowed to run, still writes only its marker for `MartenStudio.dll` (measured
  with the removal target redefined empty and `EmitCompilerGeneratedFiles` on).

The transitive graph at 9.51.0, compared with 9.50.2's in `project.assets.json`:

- JasperFx / JasperFx.Events **2.91.0** (from 2.87.1); Weasel.Core / Weasel.Postgresql / Weasel.Storage **9.46.0**
  (from 9.45.0).
- Unchanged: Npgsql / Npgsql.NetTopologySuite 9.0.4, Patched.DistributedLock.Core 1.0.11 /
  Patched.DistributedLock.Postgres 1.3.3, System.Linq.Dynamic.Core 1.7.4, Polly.Core 8.6.5, FastExpressionCompiler
  5.4.1, ImTools 4.0.0, Spectre.Console / Spectre.Console.Ansi 0.55.0, Microsoft.Bcl.TimeProvider 10.0.0,
  Microsoft.IO.RecyclableMemoryStream 3.0.1.
- **No package is new**: the two resolved lists differ in those five version moves and nothing else.

Behavioural differences between 9.50.2 and 9.51.0, read from the source at `V9.50.2`, `V9.50.3` and `v9.51.0`, JasperFx
`V2.87.1` to `v2.91.0` and Weasel `9.46.0`, and what the studio does with each:

- **`IMartenDatabase.MarkEventsAsSkipped(tenantId, sequences)`** (#5698) adds `and tenant_id = :tenant_id` on a
  conjoined event store, and the tenant-less overload now refuses under `UseTenantPartitionedEvents`. The studio calls
  the tenant overload there, by reflection because the floor has no such member, for the scope's tenant and after the
  same tenant proof as before. `TenantEventSkipping` asks for Marten's implementation, not the throwing interface
  default.
- **Rewind per tenant** (#5700, jasperfx#1025). The daemon's per-tenant `RewindSubscriptionAsync` stops, rewinds and
  restarts one tenant's agents; Marten rewrites only that tenant's shard rows, through `findTenantShard`, which 9.50
  lacked, and deletes only that tenant's dead letters of the shard.
  - The studio makes the call only for an agent the daemon in this process runs, because the overload starts the
    tenant's agents wherever it is asked, so a cold node or a distributed host would run a second one.
  - It passes that agent's own names, because JasperFx stops agents by an ordinal comparison.
  - It authorizes the call as `(store, database, tenant)`, D27's one exception.
  - The store-global rewind is never made on such a store; from 2.91 it rewinds each tenant this daemon runs and
    refuses a floor above zero there.
- **Compaction deletes the folded events' DCB tag rows** (#5694), and every tag table declares
  `idx_mt_event_tag_<suffix>_seq_id`. A tagged stream now compacts (`TaggedStreamCompaction`, which probes #5694's
  `TenantDataCleaner.deleteTagRowsAsync`). A store whose tag tables were built below 9.51 shows the index as drift: one
  plain `CREATE INDEX` per tag table in Preview, which holds a SHARE lock while it builds so tagged appends wait, now a
  `MigrationRisk` note.
- **Empty stream ids** (#5695): the fetch reads only that stream and `CompactStreamAsync` refuses an empty id. The
  studio's refusal stays at every version, and its reason names both ends.
- **#5665 has been in a release since 9.50.3**, so the 2026-10-15 entry's "in no tag up to `V9.50.2`" is superseded.
  `AllActiveFeatures` yields `SequenceFeature`, described and never applied, so `AllObjects()`, `AllSchemaNames()`,
  `ToDatabaseScript()` and the ensure path's post-processing no longer create `mt_hilo`. Hard rule 14 stays, because
  the floor has the hazard, and `OfflineSchemaEnumeration` words the Schema buttons. `SchemaNoDdlLiveTests`' and
  `ReadPathNoDdlLiveTests`' anti-vacuity theories went red at 9.51.0, as they exist to, and now assert each end.
- **The tenancy's own `AutoCreate` picks the apply mode** (#5692), and `ApplyAllConfiguredChangesToDatabaseAsync(@override)`
  passes its override on (#5693); D27 is reworded, and enumeration is still not read-only.
  `ShardedTenancy.FindOrCreateDatabase` resolves a pooled database id (#5702). `FindRebuildTenantsAsync` answers `[]`
  before the first tenant (#5701), so Rebuild and Start stop failing on a partitioned store with no tenant yet.
- **JasperFx:**
  - jasperfx#1024 (2.91.0) reads offset-less date literals as UTC for `DateTime` members, so LINQ mode's correction
    goes idle by itself, measured and pinned by `DynamicLinqAdapterTests`.
  - #1019 (2.90.1) retries transient projection failures.
  - #1016's `TransientFailures.IsTransient` (2.89.0) first ships in 9.50.3, which is why `RuntimeFeature.Requirement`
    now prints a non-zero patch ("Marten 9.50.3+").
  - #1027/#1028 (2.91.0) add `HighWaterProgressStallThreshold`, shown on the Configuration page.
  - #1022 and #1026 are inert here.
- **Weasel 9.46:** #718 resumes a stopped partitioned concurrent index build with `DROP INDEX CONCURRENTLY IF EXISTS
  <child>` before each partition's build, no longer counted as "drops an index" when that exact build and attach
  follow it. #719/#725 scope the advisory-lock holder read to the database, as the studio's own reads already were.
- **Also in range, with no studio change:**
  - #5675: archiving a tagged stream under archived partitioning moves its tag rows.
  - #5672: long FK and index names are hashed, and a custom FK name survives the conjoined reorder, so such a store
    shows drop/add drift, already a destructive `MigrationRisk` line.
  - #5688: rollbacks run with `CancellationToken.None`, and a failed save's tombstones are written off its path.
  - #5712: inline array literals in LINQ are compiled; LINQ mode keeps building its own array.
  - #5681 and #5717.

**Exit condition:** unchanged.

**2026-10-20 — `src/PostgresStudio` takes a direct `Npgsql` `PackageReference`, and
`Microsoft.AspNetCore.App.Internal.Assets` moves to it from `src/MartenStudio`.** The 0.5 move (PS-2) split the
shared studio out of MartenStudio into `src/PostgresStudio`, a Razor Class Library MartenStudio references through a
`ProjectReference` and which references neither Marten nor MartenStudio. `PostgresStudioFenceTests` holds that line at
the project file, at the restore graph (`obj/project.assets.json`) and at the built assembly's references, and
`CoreFenceTests` at the text the compiler does not bind.

- **`Npgsql`, direct, for PostgresStudio only.** The shared studio talks to Postgres through Npgsql and has no Marten to
  bring it, so it references the package itself. The version is one central row in `Directory.Packages.props`,
  `<PackageVersion Include="Npgsql" Version="9.0.4" Condition="'$(PostgresStudioPinsNpgsql)' == 'true'" />`, which only
  a project that sets `PostgresStudioPinsNpgsql` sees - `PostgresStudio.csproj` alone today - so no other project gains
  a direct reference or a row. 9.0.4 is what Marten's floor already resolves through Weasel.Postgresql 9.33.0, so the
  move changes no resolved version anywhere; it is a placeholder, and PS-7 measures the lowest Npgsql the shared studio
  passes at. D3's "no direct `Npgsql`" stands, for MartenStudio: its reason is that a second, direct reference would
  let Npgsql and Weasel.Postgresql drift apart, and that is true only of a project that has Weasel. PostgresStudio has
  none, so for it there is nothing to drift from. MartenStudio still declares no `Npgsql` of its own; it reaches Npgsql
  through Marten and through PostgresStudio, and NuGet takes the higher of the two.
- **The assets pack, moved with the shell.** The component that writes the `blazor.web.js` script tag
  (`Components/StudioDocument.razor`) and the static web assets (`_content/PostgresStudio/css/studio.css`,
  `_content/PostgresStudio/js/studio.js`) are PostgresStudio's now, so the `Microsoft.AspNetCore.App.Internal.Assets`
  10.0.12 `PackageReference` moved with them; MartenStudio no longer references it. D22's mechanism is unchanged - the
  pack's `buildTransitive` targets define `_framework/blazor.web.js` as the consuming executable's own static web asset -
  but a MartenStudio host now reaches it through two hops (host, MartenStudio, PostgresStudio, the pack). PS-0 measured
  that on 2026-10-20 with an API-only scratch host referencing only a MartenStudio nupkg (design Appendix E, E3):
  `GET /_framework/blazor.web.js` and `GET {Path}/_framework/blazor.web.js` answer 200 with the real script, and
  negotiate answers 200. PS-2f re-checked the sample host after the move.
- **Restore** is still free of NU warnings at both Marten ends (`TreatWarningsAsErrors`), and the shared studio's
  restore graph is exactly `Npgsql` 9.0.4 and `Microsoft.AspNetCore.App.Internal.Assets` 10.0.12.
- The budget table above is rewritten with D30 (PS-8): the assets pack's row moves to `src/PostgresStudio`, and a
  `src/PostgresStudio` | `Npgsql` row joins it.

**Exit condition:** move the `Npgsql` row only on PS-7's measurement, in an entry of its own.

**2026-10-26 — the latest-tested Marten moves from `9.51.0` to `9.54.1`; the floor stays at `9.39.0`.** Marten 9.54.1
was released 2026-10-25. It is the fourth release since 9.51.0, after 9.52.0 and 9.53.0 (both 2026-10-23) and 9.54.0
(2026-10-24); the first two are tagged with a lower-case `v` (`v9.52.0`, `v9.53.0`), 9.54.x with an upper-case `V`.
Nothing in the studio needs any of them, so the floor and its reasons are unchanged. One light-up had to be re-anchored
and three are new, each behind a probe in `RuntimeFeature` pinned at both ends by `MartenRuntimeProbeTests`.

The gates were run at both ends:

- `dotnet restore marten-studio.slnx -p:MartenStudioMartenVersion=9.54.1` with `TreatWarningsAsErrors`: zero NU
  warnings.
- `dotnet build marten-studio.slnx` at 9.39.0 and at 9.54.1: 0 warnings at both.
- The fast suite at both: 6 237 tests; 6 123 passed with 114 version-gated skips at the floor (every one a LINQ-mode
  test, which needs Marten 9.48), 6 236 passed with 1 skip at 9.54.1 (its mirror).
- Every live class under `Events`, `Projections`, `Schema`, `Health` and `Query`, and every `*NoDdl*` class, at both,
  with `MARTENSTUDIO_PG_REUSE=false`: 547 tests; 531 passed with 16 LINQ-mode skips at the floor, 546 passed with 1 skip
  at 9.54.1.
- `MartenRuntimeProbeTests` at 9.50.3 and 9.51.0 as well, either side of the re-anchored probe.
- `PublicApiTest` passes at both against an untouched baseline, and so does `JasperFxSourceGeneratorTests`. At 9.54.1
  (JasperFx 2.95.1) the generator, when allowed to run, still writes only its marker for `MartenStudio.dll` (measured
  with the removal target redefined empty and `EmitCompilerGeneratedFiles` on).

The transitive graph at 9.54.1, compared with 9.51.0's in `project.assets.json`:

- JasperFx / JasperFx.Events **2.95.1** (from 2.91.0).
- Unchanged: Weasel.Core / Weasel.Postgresql / Weasel.Storage 9.46.0, Npgsql / Npgsql.NetTopologySuite 9.0.4,
  Patched.DistributedLock.Core 1.0.11 / Patched.DistributedLock.Postgres 1.3.3, System.Linq.Dynamic.Core 1.7.4, Polly.Core
  8.6.5, FastExpressionCompiler 5.4.1, ImTools 4.0.0, Spectre.Console / Spectre.Console.Ansi 0.55.0,
  Microsoft.Bcl.TimeProvider 10.0.0, Microsoft.IO.RecyclableMemoryStream 3.0.1.
- **No package is new.** On 2026-10-26 Marten's advisories are still GHSA-rfx3-98h7-v3xp and GHSA-vmw2-qwm8-x84c alone,
  and none covers JasperFx, JasperFx.Events or Weasel.

Behavioural differences between 9.51.0 and 9.54.1, read from the source at `v9.51.0`, `v9.52.0` and `V9.54.1`, JasperFx
`v2.91.0` to `V2.95.1` and Weasel `9.46.0`, and what the studio does with each:

- **`TaggedStreamCompaction` lost its anchor** (marten#5720, 9.52.0): `TenantDataCleaner.deleteTagRowsAsync` moved to
  `PerTenantPartitionedCleanup.DeleteSideTableRowsAsync`, and the probe answered "absent" at every Marten from 9.52
  while compaction - `EventStore.StreamCompacting.cs` is identical at `v9.51.0` and `V9.54.1` - went on deleting the
  tag rows. It now probes what #5694 declares, `idx_mt_event_tag_<suffix>_seq_id` on a tag table of a throwaway
  `StoreOptions`, through the event store's feature schema and with no connection. The 2026-10-20 entry's "which probes
  #5694's `TenantDataCleaner.deleteTagRowsAsync`" is superseded.
- **Projection start floors** (jasperfx#809/#1068 in JasperFx.Events 2.93.0, #5740 in 9.52.0; `ProjectionStartFloors`,
  both halves). A rebuild replays from `AsyncOptions.DetermineRebuildFloorAsync` - a stored floor, then a configured
  `SubscribeFromSequence` / `SubscribeFromTime` floor, else 0 - while its teardown still empties the projection for the
  whole database.
  - The rebuild dialog reads `mt_event_projection_floors` with the studio's own SQL, never `FetchProjectionFloorAsync`,
    which opens with `EnsureStorageExistsAsync(typeof(IEvent))`.
  - It takes the stored floor off the events to replay, says that a document only the events at or below it built is
    deleted and not rebuilt, and where none is stored, that a floor in code may still apply.
  - Per-tenant floors are counted, never named.
  - The table is not gated on any option, so a store migrated below 9.52 previews one non-destructive `CREATE TABLE`
    with no note (`ProjectionFloorsDriftLiveTests`), and `SchemaDeclarationReader` classifies it as an `EventTable`.
- **Systemic database failures** (jasperfx#1029/#1064, JasperFx.Events 2.93.0; #5741, 9.52.0; `SystemicDatabaseFailure`).
  A non-transient `DbException` in projection code is attributed to its event as `ApplyEvent` and never dead-lettered,
  `SkipApplyErrors` or not. D05 drops that remedy where the daemon here says the failure is one, keeps it where it says
  it is not, and names the exception where nothing can tell - every row read back from `mt_event_progression`.
- **The high-water advance** (#5738, jasperfx#1031, 9.52.0; `PerDatabaseHighWaterAdvance`) opens with
  `EnsureStorageExistsAsync(typeof(IEvent))`, locks the mark `FOR UPDATE`, and under `UseTenantPartitionedEvents` moves
  `HighWaterMark:{tenant}` - the tenant's, or every managed tenant's - where below 9.52 it wrote only the store-global
  row, which the daemon never reads there.
  - The studio's call is unchanged: `CorrectProgression`, the reach authorization and the audit.
  - The dialog and the button say what it may create, and which marks it moves.
  - The dialog's old sentence, that projections treat the events they have not processed as already gone, was wrong at
    every version (the advance targets the highest committed `seq_id`, #4953; what is lost is the gap's sequence numbers)
    and is replaced.
  - The per-database compare-and-set overload on `IEventStore`, and its preview, are 0.6's. `MartenApiSurfaceTest`'s
    no-per-database-form test now says where that form lives.
- **What ensures storage from 9.52.**
  - `StorageEnsuringCallTests` knows `AdvanceHighWaterMarkToLatestAsync` (both `AdvancedOperations` overloads and
    `IEventStore`'s), `PreviewHighWaterAdvanceAsync`, the floor seam, `QueryEventData` and
    `QueryStreamStatesForAllTenants`. The studio's one call, `ProjectionDataService.AdvanceHighWaterMarkAsync`, is an
    allowed gated write.
  - The daemon reads a stored floor through `FetchProjectionFloorAsync` on every continuous start and every rebuild, so
    Start and Rebuild - both gated and audited - ensure the event storage inside the daemon. Every `*NoDdl*` live class
    stays green at both ends.
- **Composite members**, pre-existing at every version: each member writes a progression row its composite's
  `ShardNames()` does not list, so the projections screen drew them as "Unregistered shards". They are now drawn under
  the composite, floor-safe, through `CompositeProjection<,>.AllProjections()` and `ShardName.Compose` (public at
  `V2.74.0`). JasperFx 2.95.0 put members into `IEventStore.RegisteredShardNames()` and 2.95.1 moved them behind
  `RegisteredShardNames(true)` (jasperfx#1080); the studio calls neither.
- **D27 still holds at 9.54.1.** The only tenancy change in range is `ShardedTenancy.dropPartitionsForTenant`'s side-table
  clean-up, and Weasel is unchanged, so enumerating a dynamic tenancy's databases is still not read-only.
- **Also in range, with no studio change:**
  - #5682: `Advanced.ExceptionTransforms` - a studio write's failure may surface as a type the host chose;
    `ConcurrencyException` still comes from the operations first.
  - #5743: `StatementOperation` is a `NoDataReturnedCall`; the studio queues `UndoDeleteWhere` alone.
  - #5723: a projection's revision is no longer cut to `int`.
  - #5720/#5742: tenant removal deletes the tenant's side-table rows; the studio removes no tenant.
  - jasperfx#1079 (every tenant's stream states) and jasperfx#1073 (`QueryEventData`): LINQ, so they ensure storage, and
    the latter's tenant form opens `QuerySession(tenantId)` (hard rules 14 and 15).
  - CritterWatch's diagnostics members (2.93-2.95, marten#5768), which reach `FetchStreamAsync` or LINQ.
- **Known limit: Weasel 9.46.1 is not consumed.** weasel#730, released in Weasel 9.46.1 on 2026-10-23, rebuilds
  `ForeignKey.TryToCorrectForLink` so that two migrations running `PostProcess` over the same `Table` cannot widen a
  conjoined key twice. No Marten through 9.54.1 depends on 9.46.1 - Marten's `Directory.Packages.props` at `V9.54.1`
  pins 9.46.0 - so two circuits previewing or applying two databases of one store at the same moment can render a key
  linked to a wider primary key as `FOREIGN KEY(seq_id, tenant_id, tenant_id)`, as with a DCB tag table under per-tenant
  partitioning. It is not pinned here: a transitive `Weasel.Postgresql` row would be a budget decision, would raise the
  Weasel every host resolves - floor hosts on Marten 9.39.0, which ships Weasel 9.33.0, included - and would test the
  studio against a Weasel no Marten ships. It arrives with the first latest-tested Marten that depends on it. A per-store
  lock around the studio's own Check, Preview and Apply would remove the circuit-against-circuit race without a package;
  it would not remove one against the host's own migrations.

**Exit condition:** unchanged.

**2026-10-26 — PostgresStudio's `Npgsql` floor moves *down*, from the `9.0.4` placeholder to `8.0.3`.** PS-7 measured it
as the 2026-09-19 entry measured Marten's. At each candidate it ran four gates over `postgres-studio.slnf`, with
`-p:PostgresStudioNpgsqlVersion=<v>` on every command:

- `dotnet restore` with `TreatWarningsAsErrors`, confirming `"Npgsql/<v>"` in `src/PostgresStudio/obj/project.assets.json`;
- `dotnet build`;
- `tests/PostgresStudio.Tests`;
- `tests/PostgresStudio.Integration.Tests`, with `MARTENSTUDIO_PG_REUSE=false`.

The base was `a46e257c`.

| Npgsql | restore | build | fast | live | pool instruments |
|---|---|---|---|---|---|
| **8.0.3** | 0 NU warnings | 0 warnings | 332 of 332 | 72 of 72, no skips | read (`NpgsqlPoolLiveTests`) |
| 7.0.7 | 0 NU warnings | 0 warnings | 3 failed | 1 failed | absent |
| 10.0.3 (latest tested) | 0 NU warnings | 0 warnings | 332 of 332 | 72 of 72, no skips | read |

There are two walls, and each is a reason no lower floor is reachable:

- **7.x has no `db.client.connections.usage`/`.max` instruments.** Npgsql reported its pool through EventCounters
  before 8.0, so at 7.0.7 the pool observer finds no pool. Three `NpgsqlPoolInstrumentsTests` fail, and so does
  `NpgsqlPoolLiveTests.The_Sessions_pool_panel_and_C01_read_a_real_pool`: C01 reports "This database's Npgsql pool is
  not among the pools Npgsql reports in this process", and Sessions' pool panel is empty. Everything else passes at
  7.0.7, so the pool reading is the one member the studio needs from 8.0.
- **8.0.0 through 8.0.2 carry CVE-2024-32655** (GHSA-x9vc-6hfv-hg8c, fixed in 8.0.3), so they were not taken as
  candidates. A package should not set its floor on a vulnerable driver.

Nothing changes for a MartenStudio host. Marten's floor resolves Npgsql 9.0.4 through Weasel.Postgresql, and NuGet takes
the higher of that and PostgresStudio's `>= 8.0.3`; `NpgsqlVersionTests` keeps the floor at or below what the Marten
floor resolves. A host without Marten may now stay on Npgsql 8.0.3 or later. Of 8.0.3 against 10.0.3, the live suite
takes 21.6 s against 28.8 s, so the latest leg costs CI little.

**Exit condition:** raise the floor when a member the studio needs is absent below the new value, or when a published
advisory makes restore at the floor fail (`NU1901`-`NU1904`), and say here which. Bump `PostgresStudioNpgsqlLatest` in a
pull request of its own, with both PostgresStudio suites green at it.
