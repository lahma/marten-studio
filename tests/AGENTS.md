# AGENTS.md: the test projects

The [root AGENTS.md](../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the test projects. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** What each kind of test holds and the live-suite rules: [testing.md](../docs/maintainers/testing.md) (D16, D19).

## Design decisions

**D16 — xunit.v3 3.2.2 and Verify.XunitV3 32.0.0, eight test projects all matching `*.Tests`.**
`tests/DataStudio.Tests` (the shell), `tests/DataStudio.Postgres.Tests`, `tests/DataStudio.Marten.Tests`, `tests/DataStudio.Kafka.Tests` (a
Kafka-only host) and `tests/DataStudio.Kafka.Wolverine.Tests` are fast and need no Docker; the three `*.Integration.Tests` (Marten,
Postgres, Kafka) need Testcontainers, and the Marten and Postgres ones also need Playwright. All eight match the glob in
`ITest.TestProjects`. The pins are load-bearing: see the package budget in [build/AGENTS.md](../build/AGENTS.md). Full text: [testing.md](../docs/maintainers/testing.md#d16).

**D19 — Playwright browsers are installed by a Fallout `Browsers` target; locally the browser suite skips with a reason,
on CI a missing browser throws.** CI must never report a green run whose browser tests quietly did not happen; the skip
message names the exact command that would install them. Full text: [testing.md](../docs/maintainers/testing.md#d19).

## Testing

```powershell
.\build.ps1 Test                                          # everything, through the orchestrator
.\build.ps1 Test --marten-version latest                  # everything, at the latest-tested Marten
dotnet test tests\DataStudio.Marten.Tests                       # a fast suite alone
$env:MARTENSTUDIO_PG_REUSE='false'                         # a container of this run's own; see PostgresFixture
dotnet test tests\DataStudio.Marten.Integration.Tests           # needs Docker - run `docker info` first
dotnet test tests\DataStudio.Marten.Tests -p:DataStudioMartenVersion=9.57.0   # one suite at another Marten
dotnet test tests\DataStudio.Marten.Tests --filter "FullyQualifiedName~ApiSurface"
dotnet test tests\DataStudio.Tests                              # the shell's fast suite
dotnet test tests\DataStudio.Postgres.Tests                     # the Postgres module's fast suite
dotnet test tests\DataStudio.Postgres.Integration.Tests         # its live suite - needs Docker
dotnet test tests\DataStudio.Kafka.Tests                        # the Kafka module's fast suite: a Kafka-only host
.\build.ps1 Test --npgsql-version latest                  # ... and then the Postgres module's projects at the latest-tested Npgsql
dotnet test data-studio-postgres.slnf -p:DataStudioNpgsqlVersion=11.0.0 -p:DataStudioExtensionsVersion=11.0.0   # that pass alone (Npgsql 11 needs the extensions property too); never the .slnx
```

**The suites run at two Marten versions** (both 9.57.0 until the next latest-tested bump): the floor (`build.yml`) and
the pinned latest-tested 9.x (`marten-latest.yml`, which runs `dotnet fallout Test` with `MARTEN_VERSION: latest`). A
change is green only when both are. A fix for a behavioural difference between them must hold at both ends; one that
needs a member only the newer side has is run-time light-up behind a probe, unless the floor itself moves to that
version (allowed since the user's 2026-11-02 policy; *Package budget changes* 2026-11-03). `PublicApiTest` must pass at
both without a baseline edit, which is one reason `JasperFxSourceGeneratorTests` exists.

Container reuse is on by default locally (a second run attaches to the same Postgres) and off on CI.
`MARTENSTUDIO_PG_REUSE=false` is how one run opts out (the variable keeps the name the repository started with, and it governs
every container the suites start), which every run that might overlap another — parallel agent
packets, two terminals — must do, because schema names derive from test class names and two runs on one reused container
drop each other's tables. `TESTCONTAINERS_REUSE_ENABLE` is **not** read by Testcontainers 4.x (verified by
decompilation, 2026-09-14) and must not be used.

The five fast suites run in `fi-FI` (`SuiteCultureTests` sets it for the whole process), so a number formatted in the server's
culture fails a test; write an expected value out, never with the current culture. Full text:
[testing.md](../docs/maintainers/testing.md#the-fast-suites-run-in-fi-fi).

There are seven kinds of test, in eight projects: unit, component, endpoint and metadata, public API snapshot,
conventions, Marten API surface, and integration and browser. Waits are always wait-on-condition, **never
`Task.Delay`**. Integration tests drive everything through the studio's own services, so the gating is exercised, not
bypassed. A class that needs a server setting of its own starts a `postgres:17-alpine` of its own, never reused; a test
that creates, grants or alters a role on the matrix takes `PostgresVersionMatrix.IsolatedServerAsync`, never
`ServerAsync`; a live test that needs a `VACUUM` to remove rows, or resets statistics, works in a database of its own.
Marten, JasperFx and Wolverine sources are the sibling checkouts `D:\Work\marten`, `D:\Work\jasperfx` and
`D:\Work\wolverine` (check out the tag `Directory.Packages.props` pins); read them before decompiling, and never modify
them. What each kind holds and the live-suite rules: [testing.md](../docs/maintainers/testing.md).

Run `docker info` before the integration suite. If Docker is not running, say so — a skipped Docker suite reported as a
pass is worse than a failure.

To approve a Verify baseline: run the test, open the `.received.*` next to the `.verified.*`, read the diff line by
line, then accept it (`dotnet test` with `DiffEngine` open, or rename the file). `*.received.*` is gitignored and must
never be committed; `*.verified.txt` is checked out LF and UTF-8 by `.gitattributes` because it is compared byte for
byte.
