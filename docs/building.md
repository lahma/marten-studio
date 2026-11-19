# Building from source

How to build, test and pack the repository. [`AGENTS.md`](../AGENTS.md) has the rules.

```powershell
.\build.ps1 Test                 # restore, compile, run all eight test projects
.\build.ps1 Test --playwright    # ... and install the Chromium build the browser suites drive first
.\build.ps1 Pack                 # the seven NuGet packages into artifacts/packages
```

The fast suites need nothing:

```powershell
dotnet test tests\DataStudio.Tests
dotnet test tests\DataStudio.Marten.Tests
dotnet test tests\DataStudio.Postgres.Tests
dotnet test tests\DataStudio.Kafka.Tests
dotnet test tests\DataStudio.Kafka.Wolverine.Tests
```

The integration suite needs Docker — run `docker info` first. It starts one `postgres:17-alpine` through
Testcontainers and reuses it between runs locally; a few classes start a server of their own, never
reused: a `postgres:17-alpine` with settings of its own, and `pgvector/pgvector:0.8.1-pg17` for the
pgvector tests. **Any run that might overlap another must opt out**, because schema names derive from test
class names and two runs on one reused container drop each other's tables:

```powershell
$env:MARTENSTUDIO_PG_REUSE='false'
dotnet test tests\DataStudio.Marten.Integration.Tests
dotnet test tests\DataStudio.Postgres.Integration.Tests
dotnet test tests\DataStudio.Kafka.Integration.Tests
```

The Postgres module's live suite has a container reuse identity of its own and never attaches to the Marten suite's; its
version matrix starts one server per PostgreSQL major, 15 to 19, 19 on its release candidate.

(`TESTCONTAINERS_REUSE_ENABLE` is not read by Testcontainers 4.x and must not be used.)

`.\build.ps1 Browsers` installs the Chromium build the browser tests drive. It is deliberately opt-in
locally — a developer should not pay a 150 MB download for a test run — and automatic on CI, where a
missing browser has to be a failure rather than a silent skip.
