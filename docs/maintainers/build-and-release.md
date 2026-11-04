# Build, packages and release

This is the full text of D3, D21, the *Package budget changes* and *Held deliberately* subsections, and the *Build* and *Release engineering* sections (their command blocks and the CHANGELOG paragraph stay in AGENTS.md), moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D3

**D3 — Few dependencies a package, and no upper bound on any: MartenStudio (`Marten`, floor `9.57.0`, and PostgresStudio
at exactly its own version), PostgresStudio (PostgresStudio.Diagnostics at exactly its own version, and
`Microsoft.AspNetCore.App.Internal.Assets`, floor `10.0.12`) - each plus `FrameworkReference
Microsoft.AspNetCore.App` - and PostgresStudio.Diagnostics (`Npgsql`, floor `9.0.4`, and four `Microsoft.Extensions.*`
abstractions, floor `10.0.0`), with no framework reference at all.** Everything else either RCL needs (Blazor, SignalR,
authorization, endpoint routing) is in the shared framework, so neither pins an ASP.NET Core patch onto its host; nor does
the assets pack, three `.js` files and an MSBuild target with no `lib/`, no `ref/` and no runtime assembly (D22); and the
engine, which a worker host takes alone, needs no ASP.NET Core at all (hard rule 16). No `Marten.AspNetCore`: its value is
ETag'd HTTP JSON writers generic over `T`, and the studio only ever has a runtime `Type`. MartenStudio has no direct
`Npgsql`, which arrives through `Weasel.Postgresql` and must not drift from it; PostgresStudio.Diagnostics, with no Marten
to bring it, references `Npgsql` directly, and PostgresStudio takes it through that reference (*Build*; since 0.11-C, with
the engine in a package of its own - *Package budget changes* 2026-11-01). The floors follow the latest releases (*Package budget changes*, 2026-11-03: the user's policy of
2026-11-02 is that consumers keep up with Marten and Npgsql), tested and packed as *Build* says. The studio compiles without the JasperFx source generator Marten bundles, so its public API
is the same whichever Marten built it.

## D21

**D21 — Agent routing (revised 2026-10-31: Sonnet implements, Opus validates).** A packet goes to `studio-grunt`
(Sonnet, medium) only when it is mechanical *and* fully specified: verbatim copies and ports with listed renames, boilerplate emitted from a table in the packet,
scripted runs and artefact collection. Design-bearing packets go to `studio-implementer` (Sonnet, high), but the
design itself is not delegated: the orchestrator (Opus) writes every security, SQL, authorization and Marten-API
decision into the packet, and the implementer stops on one the packet leaves open. Final validation is Opus:
`studio-reviewer` (xhigh) reads every packet that touches `src/`, scoped to its diff, and the orchestrator reviews
authorization code itself. Agents read AGENTS.md from context (CLAUDE.md imports it), never with a fresh Read.

## Package budget changes

The dated entries are in `docs/maintainers/package-budget-changes.md`, oldest first. Each says what a package or a
floor move was, what it replaced, what was measured and its exit condition. A reference to "*Package budget changes*
<date>" names an entry there. Hard rule 1's decision is a new entry in that file, and so is every floor or
latest-tested move; read a package's entries before changing its row. What they settled:

- **Marten's floor is 9.57.0, and the floors follow the latest releases** (2026-11-03; the user's policy of 2026-11-02:
  "we can also target and require newer npgsql and marten if that helps, we can expect consumers to keep up with latest
  development"). 9.57.0 fixes JasperFx/marten#5797 (compacting a stream that folds to null replaced its whole history with an empty
  snapshot), which the studio's `CompactStreams` exposed on every host below it; every run-time light-up from 9.31 to 9.53
  became unconditional. History, all recorded in the entries: the 9.39.0 security floor (GHSA-q4xm-rhx9-xjm4 and
  GHSA-frqq-p5g3-8jq5, 2026-10-15), the code's own need of 9.31.0 (2026-09-19), and the walls below that
  (GHSA-rfx3-98h7-v3xp up to 9.12.0, `ShardFailure` from 9.20.0, `ShardName.HighWaterMarkFor` from 9.22.4, the
  generator's dispatcher from 9.24.0 and the extended progression columns from 9.31.0). Each latest-tested bump's entry
  records every behavioural difference and every light-up.
- **PostgresStudio's Npgsql floor is 9.0.4** (2026-11-03), the Npgsql Marten 9.57.0 itself references, so a Marten host is
  never moved off Marten's own (`NpgsqlVersionTests`). It was 8.0.3 (2026-10-26, PS-7: 7.x has no pool instruments, and
  8.0.0–8.0.2 carry CVE-2024-32655).
- **The JasperFx source generator is removed from MartenStudio's compilation by a target**, because `ExcludeAssets`
  does nothing for analyzers in SDK 10.0.402 (2026-10-15).
- **The rule for floors, since 2026-11-03.** Floors follow the latest releases: at a minor release the Marten and Npgsql
  floors may move to the latest tested versions (user, 2026-11-02), and a floor must still rise when a member the studio
  needs is absent below the new value or a published advisory makes restore at the floor fail (`NU1901`-`NU1904`). Say
  which in a new entry. Keep a Marten host on Marten's own Npgsql (`NpgsqlVersionTests`). Bump a latest-tested version in
  a pull request of its own, with the suites it covers green at it; between releases it equals the floor, and the
  `RuntimeFeature` table is empty until a bump past the floor puts a feature in it.

**Held deliberately, and not stale.**

- `xunit.v3` / `xunit.runner.visualstudio` stay at **3.2.2 / 3.1.5** because xunit.v3 4.0.0 drops
  VSTest-mode support, which is not incidental to Fallout 10.4.0's `ITest` — it *is* the interface. `ITest`
  passes `logger trx;LogFileName=`, probes each test project for a `GitHubActionsTestLogger` reference
  before adding `logger GitHubActions`, and parses the resulting TRX against the VSTest `TeamTest/2010`
  schema. Taking 4.x would silently degrade CI reporting to "the run passed or it did not". *Exit:* when
  Fallout's `ITest` speaks Microsoft.Testing.Platform natively.
- `Verify.XunitV3` stays at **32.0.0** because 33.x depends on `xunit.v3.extensibility.core` 4.0.1; 32.0.0
  is the last release that depends on 3.2.2. Moving it forward silently forces xunit.v3 4.x and breaks the
  line above. *Exit:* the same one.
- `Fallout.Common` / `Fallout.Components` stay at **10.4.0** because 11.0.x is the *edge* channel and sorts
  higher only as a versioning artifact — see *Build*. *Exit:* when the stable channel moves past 10.4.
- `NuGet.Frameworks` stays pinned at **7.9.0** in `build/_build.csproj`. Fallout evaluates project files
  in-process (`ITest` asks each test project whether it references `GitHubActionsTestLogger`), which runs
  the installed SDK's own targets inside the Fallout process; those targets bind `NuGet.Frameworks` by
  strong name and SDK 10.0.4xx wants 7.9.0.0, while `Fallout.Components` 10.4.0 resolves 6.14.3
  transitively through `NuGet.Packaging`. App-local assemblies win, so without the pin every `Test` run
  dies in `[MSBuild]::GetTargetFrameworkIdentifier(net10.0)` — a build that compiles and packs fine but
  cannot run a single test. Pinned *forward*, because the runtime binds a higher assembly version than the
  one requested. *Exit:* when Fallout ships a `NuGet.Packaging` new enough to bring 7.9.0 in on its own.

The trusted-publishing exchange uses `Fallout.Common.Utilities.Net` (`CreateRequest`,
`WithBearerAuthentication`, `WithJsonContent`, `GetResponse`, `AssertResponse`, `GetBodyAsJsonObject`),
which arrives with `Fallout.Common` 10.4.0 — no new `PackageReference` anywhere.

## Build

**10.4.0 is not stale, and 11.0.x is not an upgrade.** Fallout runs two channels: `11.0.x` is *edge* and
`10.4.0` is *stable*, which is newer and a feature superset despite sorting lower. The edge CLI ships under
a different package id (`Fallout.Cli`), which is why `fallout.globaltool` has no 11.x and nothing is
behind. Moving `build/` to 11.0.x would fail to compile — `GitHubActionsAttribute.Env`, which `NUGET_USER`
depends on, does not exist there — and would regenerate the workflows backwards to older action versions.
Do not "update" it.

**The Marten version is `Directory.Packages.props`'s, and the build only chooses between two.**
`MartenStudioMartenFloor` (what the package depends on) and `MartenStudioMartenLatest` (the newest 9.x CI
tests, an exact version) are defined there once; `Marten`'s `PackageVersion` reads
`$(MartenStudioMartenVersion)`, which defaults to the floor. `build/Build.Marten.cs` resolves
`--marten-version` (env `MARTEN_VERSION`) — `floor`, the default; `latest`; or an exact version such as
`9.57.0`, never a range — in `OnBuildInitialized`, and passes the result as
`-p:MartenStudioMartenVersion=` to Restore, Compile and Test, the floor included, so an environment
variable of that name cannot change what a run tests. Without Fallout, `dotnet test …
-p:MartenStudioMartenVersion=9.57.0` does the same. **`Pack` packs at the floor and refuses anything
else**, twice: the Fallout target asserts it, and `MartenStudio.csproj` fails a bare `dotnet pack` before
the nuspec is written unless both `MartenStudioMartenVersion` is the floor *and* `project.assets.json`
holds the `"Marten/<floor>"` library — because NuGet writes the dependency range from the assets file,
so `dotnet test … -p:MartenStudioMartenVersion=9.57.0` followed by `dotnet pack --no-restore` would
otherwise pass a property check and still ship `Marten >= 9.57.0`. A missing assets file is a refusal,
not a pass (a package packed at a newer Marten would raise every host's floor). To bump the
latest-tested version: change `MartenStudioMartenLatest`, run both suites with `--marten-version latest`,
and fix or record every difference in the same pull request. Read the release's "Action needed after upgrading" notes,
and turn each that leaves state a host may still carry into a health check or a page note (E09 is the first, from Marten
9.57.0). Raising the floor is a *Package budget changes* entry, never a side effect.

**PostgresStudio's Npgsql is `Directory.Packages.props`'s too, and anything but its floor gets a pass of its own.**
`PostgresStudioNpgsqlFloor` (9.0.4, what the PostgresStudio package depends on; never above the Npgsql that Marten's
own packages already require at the Marten floor, which `NpgsqlVersionTests` reads from MartenStudio's restore) and
`PostgresStudioNpgsqlLatest` (10.0.3) are defined there once, and the `Npgsql` row reads `$(PostgresStudioNpgsqlVersion)`
only for a project that sets `PostgresStudioPinsNpgsql`. `build/Build.Npgsql.cs` resolves `--npgsql-version` (env
`NPGSQL_VERSION`) - `floor`, the default; `latest`; or an exact version - but the whole-solution Restore, Compile and
Test always get `-p:PostgresStudioNpgsqlVersion=<floor>`: handed to the solution, a higher Npgsql reaches MartenStudio
and its tests through the `ProjectReference` with no diagnostic (PS-0), and MartenStudio on Npgsql 10 is not a tested
combination. Anything but the floor adds `NpgsqlPass`, which `Test` triggers: a separate restore, build and test over
`postgres-studio.slnf`, which holds exactly the projects that set the property and reaches nothing outside itself
(`PostgresStudioSolutionFilterTests`); each test project's TRX is `<project>.npgsql-<version>.trx`, beside the floor
pass's. `marten-latest.yml` sets `MARTEN_VERSION: latest` and `NPGSQL_VERSION: latest`, so one workflow runs both
latest legs. Without Fallout: `dotnet test postgres-studio.slnf -p:PostgresStudioNpgsqlVersion=10.0.3` - never the
`.slnx`. A project that sets `PostgresStudioPinsNpgsql` goes into `postgres-studio.slnf` in the same pull request.

**Three packages, one version.** `Pack` packs `src/PostgresStudio.Diagnostics`, then `src/PostgresStudio`, then
`src/MartenStudio`, one after the other, all at the CHANGELOG version and all at the floors - it refuses a
`--marten-version` or `--npgsql-version` that is not the floor, and each csproj refuses a bare `dotnet pack` off its
floor, by property and by `project.assets.json` (`PostgresStudio.Diagnostics` and `PostgresStudio` for Npgsql, MartenStudio
for Marten) - and then asserts all three nuspecs, each dependency at exactly the range NuGet wrote. MartenStudio depends on
exactly `PostgresStudio [<version>]`, on `Marten <floor>` and on `Microsoft.AspNetCore.App.Internal.Assets` (written there
by transitive pinning), and not on the engine, which a host gets through PostgresStudio's pin. PostgresStudio depends on
exactly `PostgresStudio.Diagnostics [<version>]`, `Npgsql <floor>` (it no longer references Npgsql itself, but it still sets
`PostgresStudioPinsNpgsql`, so the central row pins the Npgsql that arrives through the engine and NuGet writes that pinned
transitive package into the nuspec) and `Microsoft.AspNetCore.App.Internal.Assets`, and nothing else.
PostgresStudio.Diagnostics depends on `Npgsql <floor>` and the four `Microsoft.Extensions.*` abstractions at their central
rows, and on no ASP.NET Core package and no part of the Marten family. Each exact range is a target that brackets
NuGet-private item metadata: `MartenStudioPinsPostgresStudioExactly` in `MartenStudio.csproj` and
`PostgresStudioPinsDiagnosticsExactly` in `PostgresStudio.csproj`; the nuspec assertion is what turns a NuGet that renamed
it into a red build rather than a shipped `>=`. NuGet enforces a pin everywhere but one place: with
`CentralPackageTransitivePinningEnabled` and a central `PackageVersion` for the pinned package at another version, the SDK
drops it from the graph without a warning (PS-0; NuGet/Home#14698). So `AddMartenStudio` first runs
`PostgresStudioVersionGuard` (D30), which throws an `InvalidOperationException` naming the central pin when PostgresStudio
or PostgresStudio.Diagnostics is missing or its informational version (before the `+`) differs, and `AddPostgresStudio`
first runs `PostgresStudioDiagnosticsVersionGuard`, which does the same for the engine - but only for a newer engine or
another commit of the same version: `AddPostgresStudio`'s parameter type derives from an engine type, so a missing or older
engine fails in the loader when it is first called, with a `FileNotFoundException` that names the engine and the version
(measured 2026-11-01). `AddPostgresDiagnostics` needs no guard: nothing is below it.

`build/_build.csproj` restores with `NU1902`/`NU1903` warnings for `System.Security.Cryptography.Xml` and
`System.Security.Cryptography.Pkcs` 10.0.10, which `Fallout.Components` 10.4.0 brings transitively (seen
2026-10-15). They are warnings in the orchestrator only — it does not inherit `TreatWarningsAsErrors`, and
none of it ships — and pinning either package forward would be a budget decision this file has not taken.

The build needs at least one commit: `IHasGitRepository` injects `GitRepository` from the working tree and
fails with `Could not find commit information` on an empty repository.

## Release engineering

A release is cut by pushing a `v*.*.*` tag, which starts **two independent workflows**: `publish.yml`
(nuget.org) and `release.yml` (the GitHub Release). Neither depends on the other, deliberately — a
nuget.org outage must not hold up the release notes, and a release-API hiccup must not hold up the package.

They are separate files because of what each is allowed to hold. A nuget.org trusted publishing policy is
scoped by repository plus workflow **file name** (plus environment), and the nuget.org UI offers no branch
or tag filter — so whichever file the policy names can mint a publishing key on *every* run of that file.
`publish.yml` therefore runs only on a `v*.*.*` tag and holds `id-token: write` with `contents: read`;
`release.yml` holds `contents: write` and `GITHUB_TOKEN` and is not the file the policy names. **The one
workflow that can mint a key must not also hold repository write.** `WorkflowGenerationTests` asserts both
halves, in both directions.

`build.yml` and `marten-latest.yml` — every push and pull request to `main`, at the Marten floor and at
the latest-tested Marten — cannot mint anything, because neither is the file in the policy; both hold
`contents: read` and nothing else, and `WorkflowGenerationTests` asserts that no workflow but `publish.yml`
so much as mentions `id-token`. Neither packs: the package that `publish.yml` pushes is packed at the
floor, and `Pack` refuses any other Marten.

`CHANGELOG.md` stays the version authority here too. `CreateGitHubRelease` and `Publish` both refuse unless
`GITHUB_REF_NAME` equals `v{version parsed from CHANGELOG.md}`. To release: land the changelog entry first,
then tag that commit.

**Package validation (1.0).** Each shipped project has `EnablePackageValidation`, and `Directory.Build.props`'s
`PackageValidationBaselineVersion` names the release the packages are compared with at `Pack` (0.11.0 for 1.0.0): `Pack`
restores that version of each package from nuget.org and fails on a breaking change to the public surface (a removed or
changed member, an interface member without a default body). In the pull request that lands a release's CHANGELOG section,
move the baseline to the release before it, so the next release is compared with the last one shipped. A deliberate break
is a major version, and its `CompatibilitySuppressions.xml` is a reviewed file like a Verify baseline. A change to a Razor
component's members is not a break: components are public only because Razor makes them so, and no baseline lists them
(D20, `docs/stability.md`). Package validation still reports one, so suppress it in the project's
`CompatibilitySuppressions.xml` (generate it with `-p:GenerateCompatibilitySuppressionFile=true`, then make it LF without a
BOM and say why in its comment), reviewed the same way. 1.1.0 suppresses `LinqQueryPanel.OnChanged` (user, 2026-11-03). 1.2.0 validates against 1.1.0, so the suppression is gone: `ApiCompatPermitUnnecessarySuppressions=false` fails `Pack` on a suppression that is no longer needed.

The GitHub Release carries **no assets** (`ICreateGitHubRelease.AssetFiles => []`). All three packages are consumed
by `PackageReference`; there is nothing a user could usefully download, and attaching a copy of a .nupkg
would only invite installing a file that never went through the trusted-publishing path. The release body
is written by `Build.WriteReleaseNotes` into `artifacts/release-notes.md`: `ICreateGitHubRelease` composes
bodies with `ChangelogTasks.ExtractChangelogSectionNotes`, which only recognises `## ` headings and ends a
section at the first non-bullet line, so pointed straight at our `#`-headed, wrap-bulleted CHANGELOG.md it
returns nothing and the release ships empty. Keep changelog entries as `- ` bullets with wrapped
continuation lines and this keeps working.

`Prerelease` follows the version string (`Version.Contains('-')`), so `0.1.0-rc.1` publishes as a
prerelease and never displaces a latest stable release.

### The NuGet leg

One Ubuntu job runs `dotnet fallout Compile Test Pack Publish` with
`permissions: { contents: read, id-token: write }`, `environment: nuget` and `env: NUGET_USER: lahma`.
One job, not several: more jobs would mean more OIDC exchanges racing the one-key-per-30-s rate limit. No
`paths:` filter: a path filter alongside `tags:` evaluates over the tag commit's diff and can silently skip
the release run.

`Publish` is `.OnlyWhenDynamic(() => ShouldPublish())` and refuses in ordered steps: a non-tag invocation is
a logged *skip* (there is no preview feed), and once past that gate it asserts, in order, that it is on
GitHub Actions, that `NUGET_USER` is set, and that `GITHUB_REF_NAME` matches the changelog version. Only
then does it mint the key, immediately before pushing — the key lives 15–60 minutes and one OIDC token
mints exactly one key. Before it mints the key, it asserts that `artifacts/packages` holds exactly
`PostgresStudio.Diagnostics.<version>.nupkg`, `PostgresStudio.<version>.nupkg` and `MartenStudio.<version>.nupkg`, and it
pushes them with that one key one at a time, the engine first and MartenStudio last, the first failure ending the run, so a
push that fails never lets a package that depends on exactly it reach nuget.org. `--skip-duplicate` keeps a re-run after a
partial failure safe. nuget.org still validates and indexes each package on its own, so for a few minutes after a release
PostgresStudio can be listed while its PostgresStudio.Diagnostics is not yet restorable, and MartenStudio while its
PostgresStudio is not. `ReleasePushOrderTests` pins the order and the stop.

The whole ladder is exercisable locally, and should be before any tag:

```powershell
dotnet fallout Pack                                # the package the next steps would push
dotnet fallout Publish --skip                      # skips, and says why
$env:GITHUB_ACTIONS='true'; $env:NUGET_USER='lahma'; $env:GITHUB_REF_NAME='v0.1.0'
dotnet fallout Publish --skip                      # must fail with "GitHub OIDC is unavailable"
```

`Pack` comes first because it is the only step that can fail for a reason the token exchange will never
tell you about: a missing README, a static web asset that did not make it into the nupkg, a package
validation error. Getting a minted key and *then* discovering the package is wrong wastes the one thing
in this path that expires.

That last failure is the success signal: it proves the tagged path routed all the way to the token
exchange. Clear the three variables afterwards.

Three things about the exchange are worth not rediscovering:

- **The POST to `https://www.nuget.org/api/v2/token` must carry a `User-Agent`.** The endpoint sits behind
  Azure Front Door, which answers `400 {"error":"A User-Agent header is required."}` to a request without
  one, *before* looking at the token or the policy — and a bare `HttpClient` sends none. So **a 400 means
  the request never reached the policy**; stop looking at nuget.org settings.
- The OIDC audience is `https://www.nuget.org` (not `nuget`) and the response field is `apiKey` (not
  `api_key`; the build accepts either). The minted key is `add-mask`ed.
- `NUGET_USER` is the nuget.org **profile name of whoever created the trusted publishing policy**
  (`lahma`) — not an email, not an organisation. It is public information and is deliberately a plain
  workflow-level `env:` value, not a secret: a secret would be masked out of exactly the error message that
  names it. Getting it wrong is the most common 401.

### Manual setup, done once, before the first tag

None of these can be done from this repository, and the first tag will fail without the first two:

1. **nuget.org → Trusted Publishing → new policy.** Policy owner `lahma`; Repository Owner `lahma`;
   Repository `marten-studio`; Workflow File `publish.yml` (**filename only** — not `release.yml`, and not
   the `.github/workflows/` path); Environment `nuget`. The policy is not limited to one package id: the key it
   mints pushes both `MartenStudio` and `PostgresStudio`, including an id that does not exist yet (confirmed by the
   policy owner, 2026-10-26).
2. **GitHub → Settings → Environments → `nuget`**, with a deployment branch/tag policy of `v*`. This is the
   second claim the nuget.org policy matches on, and it is what keeps a workflow run from a branch out of
   the publishing path.
3. **The `PostgresStudio` package id** needs nothing more: the policy covers it, so its first version goes out through
   `publish.yml` like every other. Never push a release version by hand - `--skip-duplicate` would then pair packages
   built from different commits; if an id ever had to be claimed by hand, push a `0.0.1-reserve` and unlist it.

Both package ids are names the code carries: a rename would touch the assembly names, the root namespaces,
`_content/PostgresStudio`, the `InternalsVisibleTo` entries and both READMEs.
