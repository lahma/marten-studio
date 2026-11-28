# AGENTS.md: build, packages, versions, CI and release

The [root AGENTS.md](../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to build, packages, versions, CI and release. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** Build, packages, versions, CI, release, agent routing: [build-and-release.md](../docs/maintainers/build-and-release.md) (D3, D15, D21); the dated budget entries: [package-budget-changes.md](../docs/maintainers/package-budget-changes.md); [hard-rules.md](../docs/maintainers/hard-rules.md) rules 1, 2, 8, 9 and 16.

## Hard rules

2. **Never hand-edit `.github/workflows/*.yml`.** All four — `build.yml`, `marten-latest.yml`, `publish.yml`,
   `release.yml` — are generated from the `[GitHubActions]` attributes in `build/Build.CI.GitHubActions.cs`, and any
   build run overwrites them. Change the attribute and re-run the build (`dotnet fallout --generate-configuration
   GitHubActions_publish --host GitHubActions` regenerates just one); a run whose generated output differs *aborts*, so
   run it again and commit the regenerated YAML. Full text: hard rule 2.

8. **`.gitignore` must never contain a bare `build` rule.** The Fallout orchestrator lives in `build/`, and a stock
   Visual Studio .gitignore silently untracks the whole thing. Full text: hard rule 8.

## Design decisions

**D15 — Fallout's stable channel (10.5.0), CHANGELOG.md as the version authority, all four workflows generated, trusted publishing
for nuget.org.** See *Build* and *Release engineering* below. The important half is that the version lives in one file
that a human edits and no tool rewrites, and that the only credential anywhere in the release path lives for minutes.
All seven packages take that one version, a prerelease suffix (`2.0.0-beta.1`) included: the CHANGELOG's header carries it.

## Package budget

Complete. Versions are centrally pinned in `Directory.Packages.props`, with
`CentralPackageTransitivePinningEnabled` on.

| Where | Package | Version | Why it is here |
|---|---|---|---|
| `src/DataStudio.Marten` | `Marten` | `9.57.0` (latest tested `9.57.0`) | One of the two `PackageReference`s the shipped package makes (the other is the Postgres module, a project reference). Floor, not exact pin — a consumer on a newer Marten 9.x must not be dragged back. Since 2026-11-03 the floor follows the latest release (the user's policy of 2026-11-02: consumers keep up with Marten): 9.57.0 fixes #5797, where compacting a stream that folds to null replaced its whole history with an empty snapshot, which `CompactStreams` exposed on every host below it, and it makes the run-time light-ups from 9.31 to 9.53 unconditional (see *Package budget changes*, 2026-11-03; the 9.39.0 security floor of 2026-10-15 is history). Both suites also run at a pinned latest-tested version, `DataStudioMartenLatest`, which equals the floor until the next Marten release; the package is packed only at the floor. JasperFx / JasperFx.Events 2.98.0, Weasel.Core / Weasel.Postgresql / Weasel.Storage 9.48.0 and Npgsql 9.0.4 arrive transitively at the floor. The module's own compilation drops the JasperFx source generator Marten bundles (`DataStudio.Marten.csproj`). |
| `src/DataStudio` | `Microsoft.AspNetCore.App.Internal.Assets` | `10.0.12` | `blazor.web.js`, which as of .NET 10 lives nowhere else (D22); it is the shell's reference, and it reaches a Kafka host through the shell and a Marten host through the Postgres module and the shell, three hops (PS-0 measured two, *Package budget changes* 2026-10-20). Three `.js` files and one MSBuild target: no `lib/`, no `ref/`, no runtime assembly, so it pins no ASP.NET Core patch on the host. Floor, not exact pin — a host whose own SDK already resolves this pack keeps its version, because NuGet takes the higher of the two. Its `buildTransitive` targets are what put the script in the *consumer's* static web assets. The shell's nuspec lists it, and so do the Postgres, Marten and Kafka modules' (under `CentralPackageTransitivePinningEnabled` NuGet writes a centrally pinned transitive package into the nuspec), and `Pack` asserts each line. *Package budget changes* 2026-11-17. |
| `src/DataStudio.Postgres.Diagnostics` | `Npgsql` | `9.0.4` (latest tested `11.0.0`) | The diagnostics engine's database driver, and the Postgres module's: `src/DataStudio.Postgres` takes it through its `ProjectReference` to this project and does not reference it directly (*Package budget changes* 2026-11-01). The Postgres module has no Marten to bring it. A central row that only projects setting `DataStudioPostgresPinsNpgsql` see, so the Marten module keeps taking Npgsql through Marten and the two never drift there (D3; *Package budget changes* 2026-10-20). Floor, not exact pin; 9.0.4 is Marten 9.57.0's own Npgsql, so a Marten host resolves Marten's Npgsql, which is never lower (`NpgsqlVersionTests`; *Package budget changes* 2026-11-03, which moved it from the 8.0.3 that PS-7 measured on 2026-10-26). `samples/DataStudio.Sample.Postgres.Data` references it directly too, and `samples/DataStudio.Sample.Postgres`, the worker sample, both Postgres test projects and `src/DataStudio.Postgres` itself set `DataStudioPostgresPinsNpgsql` so the same row pins it transitively; all of them are in `data-studio-postgres.slnf`, so the latest-Npgsql pass builds them at 11.0.0, which removed `NpgsqlConnectionStringBuilder.Multiplexing`: the studio reads that keyword by name (`NpgsqlMultiplexing`, *Package budget changes* 2026-11-28). |
| `src/DataStudio.Postgres.Diagnostics` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Diagnostics.HealthChecks` | `10.0.0` each (`11.0.0` in the latest-Npgsql pass) | The engine has no `FrameworkReference` to `Microsoft.AspNetCore.App` (hard rule 16), so the abstractions it registers into, logs through and exports a health check with come as packages. Four conditional central rows, visible only to projects that set `DataStudioDiagnosticsPinsExtensions` (this one, and the worker sample), so Marten's, JasperFx's and Weasel's own Microsoft.Extensions.* stay unpinned in the Marten module and its tests. Floors, not exact pins. 10.0.0 and not 8.0.x because Npgsql 10.0.3's net10.0 asset asks for `Logging.Abstractions` 10.0.0 and an older pin would silently win (measured: no NU code, 8.0.0 resolved), which is not a floor (D1: the library is net10.0 only). The rows read `$(DataStudioExtensionsVersion)`: the floor, 10.0.0, everywhere but the latest-Npgsql pass, which sets `DataStudioExtensionsLatest` (11.0.0), the minimum Npgsql 11.0.0's net10.0 asset asks for. *Package budget changes* 2026-11-01 and 2026-11-28. |
| `src/DataStudio.Kafka.Core` | `Confluent.Kafka` | `2.14.0` (latest tested `2.16.0`) | The Kafka module's only Kafka client (plan K2), behind the engine's internal wire seam (D36). Floor, not exact pin: 2.14.0 is what WolverineFx.Kafka 6.61 pins, so a Wolverine host is never raised. It brings `librdkafka.redist` (native librdkafka for Windows, Linux and macOS) at its own version, unpinned. Unconditional row: only the Kafka projects reference it, and the Kafka sample transitively, for its seeder. Packed with this floor in its nuspec. *Package budget changes* 2026-11-09. |
| `src/DataStudio.Kafka.Core` | `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options` | `10.0.0` each | The diagnostics engine's conditional rows, seen by `DataStudio.Kafka.Core` through `DataStudioKafkaCorePinsExtensions`: the Kafka engine has no ASP.NET Core either (D36). |
| `tests/DataStudio.Kafka.Integration.Tests`, `samples/DataStudio.Sample.Kafka` | `Testcontainers.Kafka` | `4.15.0` | The live suite's brokers, `apache/kafka:3.9.2` and `4.4.0` in KRaft mode, one per image with SASL/PLAIN and ACLs; and the Kafka sample's throwaway `apache/kafka:4.4.0` (D18). |
| `src/DataStudio.Kafka.Wolverine` | `WolverineFx.Kafka` | `6.61.0` | Wolverine's Kafka transport, for configuration discovery only (D43): clusters, tenants and topics read from the built `WolverineOptions`, nothing started or written. Floor, not exact pin: 6.61.0 pins Confluent.Kafka 2.14.0, `DataStudio.Kafka.Core`'s floor, so a Wolverine host is never raised. It brings JasperFx 2.99.0, above Marten 9.57.0's 2.98.0. `tests/DataStudio.Kafka.Wolverine.Tests` takes it through the project reference; `tests/DataStudio.Kafka.Integration.Tests` and `samples/DataStudio.Sample.Kafka` reference it for a real Wolverine host. *Package budget changes* 2026-11-12. |
| `samples/DataStudio.Sample.Kafka` | `WolverineFx.RuntimeCompilation` | `6.61.0` | Wolverine's opt-in Roslyn code generation, for the sample's Wolverine application only (D-CORR decision 11): Wolverine 6.61 no longer ships its runtime compiler, and a host with a handler (`PlaceCheckoutHandler`, which stores a Marten document) needs it, or pre-generated code, to start. It matches `WolverineFx.Kafka` 6.61.0 and registers itself when referenced. One central row in `Directory.Packages.props`; no shipped package, no Kafka module or engine and no test project references it. *Package budget changes* 2026-11-12. |
| the four sample hosts, `tests/DataStudio.Marten.Integration.Tests`, `tests/DataStudio.Postgres.Integration.Tests` | `Testcontainers.PostgreSql` | `4.15.0` | D18's throwaway Postgres (the Marten, Postgres and Kafka sample hosts and the diagnostics worker), and the container each database integration suite runs against. |
| `samples/DataStudio.Sample.Postgres.Worker` | `Microsoft.Extensions.Hosting` | `10.0.0` | The generic host the diagnostics worker sample runs in (*Package budget changes* 2026-11-01). A fifth row of the conditional `DataStudioDiagnosticsPinsExtensions` group: no shipped package references it directly. `DataStudio.Kafka.Wolverine`'s nuspec carries it as a minimum, through `DataStudioKafkaCorePinsExtensions` and transitive pinning; it equals WolverineFx 6.61's own net10.0 minimum and raises nothing. |
| all eight test projects | `Microsoft.NET.Test.Sdk` | `18.10.0` | The VSTest host Fallout's `ITest` drives. |
| all eight test projects | `xunit.v3` | `3.2.2` | Held — see below. |
| all eight test projects | `xunit.runner.visualstudio` | `3.1.5` | The VSTest bridge. |
| all eight test projects | `GitHubActionsTestLogger` | `2.4.1` | Held — see below. |
| all eight test projects | `AwesomeAssertions` | `9.6.0` | The MIT fork of FluentAssertions. One assertion library, and **no mocking library**: the data seam is our own set of interfaces, and hand-written fakes are smaller and more auditable than a mocking framework's call-matching DSL. Its namespace is `AwesomeAssertions`, not `FluentAssertions` (verified 2026-09-14); every test project declares it as a global using. |
| the three component suites (`tests/DataStudio.Marten.Tests`, `tests/DataStudio.Postgres.Tests`, `tests/DataStudio.Kafka.Tests`) | `bunit` | `2.13.3` | Component tests. |
| the five fast suites | `Verify.XunitV3` | `32.0.0` | Held — see below. Also the source of each fast suite's `global using Xunit;`, which is why none of them declares one. |
| the five fast suites | `PublicApiGenerator` | `11.5.4` | The public API snapshots (D20), one per package. |
| `tests/DataStudio.Marten.Tests`, `tests/DataStudio.Postgres.Tests`, `tests/DataStudio.Kafka.Tests` | `Microsoft.AspNetCore.TestHost` | `10.0.12` | `UseTestServer()` for the endpoint-metadata and startup-guard tests. TestHost rather than Mvc.Testing: those tests never need a `WebApplicationFactory`. |
| `tests/DataStudio.Marten.Integration.Tests` | `Microsoft.AspNetCore.Mvc.Testing` | `10.0.12` | `WebApplicationFactory` over the real sample host. |
| `tests/DataStudio.Marten.Integration.Tests`, `tests/DataStudio.Postgres.Integration.Tests` | `Microsoft.Playwright` | `1.62.0` | The browser suites (D19). |
| `analyzers/DataStudio.Analyzers`, `tests/DataStudio.Marten.Tests` | `Microsoft.CodeAnalysis.CSharp` | `5.0.0` | The compiler API of DS1305, the culture guard every project under `src/` compiles with (`src/Directory.Build.props`: an analyzer reference with `ReferenceOutputAssembly="false"` and `PrivateAssets="all"`), and of `CurrentCultureTextAnalyzerTests`, which runs it over code. Build time only: no package of ours depends on it. 5.0.0 is the compiler of the 10.0.100 SDK, the oldest `global.json` allows, and the version `WolverineFx.RuntimeCompilation` brings to the Kafka sample. A conditional row, seen only by the projects that set `DataStudioAnalyzersPinsRoslyn`. *Package budget changes* 2026-11-22. |
| `build/` | `Fallout.Common`, `Fallout.Components` | `10.5.0` | The orchestrator; the build project opts out of CPM. 10.5.0 brings `NuGet.Packaging` and `NuGet.Frameworks` 7.9.0 and `System.Security.Cryptography.Pkcs`/`.Xml` 10.0.13 itself, so `build/` pins nothing else (*Package budget changes* 2026-11-12). |

SourceLink needs no package reference: it is in-SDK on .NET 8 and later.

### Package budget changes

Every new package, floor move and latest-tested bump needs a dated entry in `docs/maintainers/package-budget-changes.md`
first. What those entries settled, and the versions held deliberately (xunit.v3 3.2.2, Verify.XunitV3 32.0.0, Fallout
10.5.0), are in
[build-and-release.md](../docs/maintainers/build-and-release.md#package-budget-changes). Floors follow the latest releases: at a minor
release the Marten and Npgsql floors may move to the latest tested versions (user, 2026-11-02). Keep a Marten host on
Marten's own Npgsql (`NpgsqlVersionTests`). Read a package's entries before changing its row.

## Build

The orchestrator is [Fallout](https://fallout.build) 10.5.0 (stable channel), the maintained hard fork of NUKE. The CLI
is pinned in `.config/dotnet-tools.json` as `fallout.globaltool` (command `fallout`) and resolves `build/_build.csproj`
by folder-name convention.

**Follow the stable channel (10.x), never 11.0.x.** 11.0.x is the edge channel and sorts higher only as a versioning artifact;
it would fail to compile (`GitHubActionsAttribute.Env`). Move to a new stable release when it ships. Full text: [build-and-release.md](../docs/maintainers/build-and-release.md#build).

```powershell
.\build.ps1 Test                           # restore, compile, install browsers if asked, run all eight test projects
.\build.ps1 Test --playwright              # ... and install Chromium first (D19)
.\build.ps1 Test --marten-version latest   # ... at the latest-tested Marten instead of the floor
.\build.ps1 Test --npgsql-version latest   # ... and then the Postgres module's projects at the latest-tested Npgsql
.\build.ps1 Test --confluent-version latest   # ... and the Kafka module at the latest-tested Confluent.Kafka
.\build.ps1 Pack                           # the seven NuGet packages into artifacts/packages (always at the floors)
.\build.ps1 Browsers                       # just the Chromium install
```

`CHANGELOG.md` is the **version authority**: its top section is parsed in `OnBuildInitialized` and passed to every
compile, test and pack as `-p:Version=`. The file is never mutated by the build, and its first line must parse as a
version header (`# 2.0.0`) — **do not add a `# Changelog` title**, it would abort the build. `Directory.Build.props`'s
`VersionPrefix` is only the fallback for a plain `dotnet build`, and `VersionFallbackTests` holds it to the CHANGELOG's
top version (without a prerelease suffix): a new CHANGELOG section moves it in the same pull request.

Keep changelog entries as `- ` bullets with wrapped continuation lines: the GitHub Release body is built from them, and
anything else ships it empty (*Release engineering*).

The Marten version is `Directory.Packages.props`'s: `DataStudioMartenFloor` and `DataStudioMartenLatest`, chosen by
`--marten-version` (`floor`, `latest` or an exact version, never a range). `Pack` packs at the floors only and refuses
anything else. The Postgres module's Npgsql follows the same scheme with `--npgsql-version` (`DataStudioNpgsqlFloor`,
`DataStudioNpgsqlLatest`); anything but its floor gets a pass of its own over `data-studio-postgres.slnf`, never the `.slnx`.
The Kafka module's Confluent.Kafka follows it with `--confluent-version` (`DataStudioConfluentFloor`, `DataStudioConfluentLatest`).
`Pack` packs `src/DataStudio.Postgres.Diagnostics`, then `src/DataStudio`, then `src/DataStudio.Postgres`, then
`src/DataStudio.Marten`, all at the CHANGELOG version, each depending on exactly the ones below it
(`DataStudio.Postgres.Diagnostics [<version>]`, `DataStudio [<version>]`, `DataStudio.Postgres [<version>]`), then
`src/DataStudio.Kafka.Core`, `src/DataStudio.Kafka` and `src/DataStudio.Kafka.Wolverine` at the same version
(`DataStudio.Kafka` on exactly `DataStudio [<version>]` and that `DataStudio.Kafka.Core`, `DataStudio.Kafka.Wolverine` on exactly
that `DataStudio.Kafka.Core`), and `Publish` pushes the seven in that order. Package validation compares each package with the last release
(`PackageValidationBaselineVersion`, moved in the pull request that lands a release's CHANGELOG section). The
build needs at least one commit. To bump the latest-tested Marten, change `DataStudioMartenLatest`, run the suites
with `--marten-version latest` and fix or record every difference, in a pull request of its own. A project that sets
`DataStudioPostgresPinsNpgsql` goes into `data-studio-postgres.slnf` in the same pull request. Full text:
[build-and-release.md](../docs/maintainers/build-and-release.md#build).

## Release engineering

A release is cut by pushing a `v*.*.*` tag, which starts **two independent workflows**: `publish.yml` (nuget.org) and
`release.yml` (the GitHub Release). `CHANGELOG.md` stays the version authority: land the changelog entry first, then tag
that commit; `CreateGitHubRelease` and `Publish` both refuse unless `GITHUB_REF_NAME` equals `v{version parsed from
CHANGELOG.md}`. The one workflow that can mint a nuget.org key (`publish.yml`, `id-token: write`) must not also hold
repository write. Never push a release version by hand. Run the ladder locally (`dotnet fallout Pack`, then `dotnet
fallout Publish --skip`) before any tag. The NuGet leg, the token exchange's pitfalls and the one-time setup (the one existing
trusted-publishing policy covers every id of the repository, the seven 2.0 ids included; its owner confirms that before the first 2.0 tag):
[build-and-release.md](../docs/maintainers/build-and-release.md#release-engineering).
