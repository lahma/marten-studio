# Layout

Where each kind of code lives. `AGENTS.md` points here: read it before adding a file, and keep it in step with the
tree in the same pull request. Moved from `AGENTS.md` on 2026-10-26.

`AgentsLayoutTests` parses the block below and fails if it names a directory that does not exist, or
misses one that does. Keep it in step with the tree.

```text
.claude/                                 Agent definitions and local Claude Code configuration
.claude/agents/                          studio-implementer, studio-reviewer, studio-ui-verifier,
                                         studio-grunt (hard rule 12 - the only way work is delegated)
build/                                   The Fallout orchestrator. `build/` is a resolver convention and
                                         .gitignore must never untrack it (hard rule 8)
  Build.cs                               CHANGELOG-driven version, Clean, test projects, release notes
  Build.CI.GitHubActions.cs              The four [GitHubActions] attributes every workflow comes from
  Build.Browsers.cs                      The Playwright install target, and Test's dependency on it (D19)
  Build.Publish.cs                       Pack (both packages) + the in-build nuget.org OIDC token exchange (D15)
  Build.Marten.cs                        --marten-version: the floor, the latest-tested Marten or an exact one (D3)
  Build.Npgsql.cs                        --npgsql-version and NpgsqlPass, PostgresStudio's latest-Npgsql pass (D30)
build/Extensions/                        Vendored Cake helpers (StringExtensions) - do not edit
src/                                     The shipped libraries, and nothing else: the shared studio and MartenStudio
src/PostgresStudio/                      The shared studio's Razor Class Library (0.5, PS-2): shell, routes, scope,
                                         authorization, audit, the database browser, Sessions, Health, the SQL
                                         console, Relationships, the JSON toolkit and wwwroot/. References no Marten
                                         and no MartenStudio (PostgresStudioFenceTests, CoreFenceTests)
src/PostgresStudio/Components/           the shared document every root renders, route table, links, scope query
src/PostgresStudio/Components/Shared/    small building-block components: TableScroll (D25), CopyButton,
                                         dialogs, chips, the empty and error states
src/PostgresStudio/Internal/             startup authorization guard, endpoint marker, mapped-endpoint
                                         tracking and the endpoint mapper (D11)
src/PostgresStudio/Services/             scope, state, authorization, the capability guard, the action log,
                                         toasts and the shared log events
src/PostgresStudio/wwwroot/              static web assets, packed under `_content/PostgresStudio/`
src/PostgresStudio/wwwroot/css/          the one stylesheet, studio.css
src/PostgresStudio/wwwroot/js/           the studio's browser helpers, studio.js, loaded by the shell (never a
                                         Blazor JS initializer - see StudioDocument.razor)
src/MartenStudio/                        The Marten Razor Class Library: public API, the store-facing services,
                                         pages and checks, and Internal/; references PostgresStudio
src/MartenStudio/Components/             MartenStudioApp (the root component) and the component bridges
src/MartenStudio/Internal/               the duplicated-field enumeration, and the Marten SQL builders below
src/MartenStudio/Services/               store registry, scope resolver, tenant discovery, the source provider,
                                         descriptor, settings and links, and the bridges onto the shared seams
samples/                                 Demo hosts and their demo data; never packed
samples/MartenStudio.Sample/             The sample ASP.NET Core host (D18) - README snippet regions
samples/MartenStudio.SampleDomain/       The demo domain, shared with the integration tests; deliberately
                                         does NOT reference MartenStudio (D17)
samples/PostgresStudio.SampleData/       The demo data with no Marten in it, shared by both samples and the integration
                                         tests: Quartz.NET's job store (vendored, Apache-2.0), the "legacy" catalog
                                         edge-case schema, studio_sample.app_settings, and the tenant databases with
                                         their directory - all idempotent. References no studio (D17)
samples/PostgresStudio.Sample/           The Postgres Studio sample host (D18), no Marten: an admin database and three
                                         tenant databases through its own IPostgresDatabaseSource - README snippet regions
samples/PostgresStudio.Sample/Auth/      Cookie auth, the three demo users, the studio policy and the per-capability one
samples/PostgresStudio.Sample/Properties/  launchSettings.json, fixed at http://localhost:5211
tests/                                   All four test projects match *.Tests, so one Test run covers them all
tests/MartenStudio.Tests/                Fast suite: unit, bUnit, endpoint metadata, API snapshot,
                                         conventions. No Docker
tests/MartenStudio.Tests/Conventions/    Rules too easy to break silently to leave to review
tests/MartenStudio.Tests/Marten/         MartenApiSurfaceTest - the Marten 9.35 contract, pinned
tests/MartenStudio.Integration.Tests/    Testcontainers Postgres + Playwright. Needs Docker
src/PostgresStudio/Services/Postgres/    PostgresStudio's own: the source provider over the application's NpgsqlDataSources
                                         and host database sources, settings, descriptor, the Overview's server read, and
                                         the seams' plain-Postgres implementations (declarations, tables, remedies, locks, links)
tests/PostgresStudio.Tests/              PostgresStudio's fast suite: no Docker, and no Marten anywhere in its graph
tests/PostgresStudio.Tests/Components/   bUnit: PostgresStudioComponentContext and the core pages' smoke tests
tests/PostgresStudio.Tests/Conventions/  The assembly fence: no Marten family in the assembly, the restore or the output folder
tests/PostgresStudio.Tests/Endpoints/    An API-only host with one NpgsqlDataSource; the two registration guards
tests/PostgresStudio.Tests/Sources/      The source provider over DI descriptors and host database sources
tests/PostgresStudio.Tests/Support/      PostgresStudio's own test doubles (the shared ones are linked from MartenStudio.Tests)
tests/PostgresStudio.Tests/Verify/       PostgresStudio's approved API baseline; never hand-edited (hard rule 7)
tests/PostgresStudio.Integration.Tests/  PostgresStudio's live suite: Testcontainers Postgres under a reuse label of its own
                                         (never the Marten suite's container) + Playwright. Needs Docker; no Marten
tests/PostgresStudio.Integration.Tests/Browser/  Playwright over samples/PostgresStudio.Sample on real Kestrel: six
                                         scenarios, the D19 gate, and the zero-config start
tests/PostgresStudio.Integration.Tests/Database/  Live Postgres: first navigation writes nothing, the D27 gate and its
                                         audit, Sessions, the pool panel at both Npgsql majors, the sample's tenant databases
tests/PostgresStudio.Integration.Tests/Health/  Live Postgres: the table checks fire and stay quiet over the
                                         whole-database table source, and the export over a host source past its budget
tests/PostgresStudio.Integration.Tests/Query/  Live Postgres: every SQL console example runs
tests/PostgresStudio.Integration.Tests/Sources/  Live Postgres: discovery, and isolation between a host source's
                                         databases through every cache
src/PostgresStudio/Internal/Sql/         The shared builders, SqlIdentifier and SqlLexer: one of the two places a
                                         Postgres identifier becomes SQL text (hard rule 4)
src/MartenStudio/Internal/Sql/           The Marten builders - documents, events, the daemon, projections: the
                                         other (hard rule 4)
src/PostgresStudio/Components/Layout/    Shell chrome: layout, nav, scope selector, capability chip, the
                                         preferences popover (theme and time zone, behind the gear)
src/PostgresStudio/Components/Pages/     The shared routed pages; each @page must be rooted at /postgres
src/MartenStudio/Components/Pages/       The Marten routed pages; each @page must be rooted at /marten
samples/MartenStudio.SampleDomain/Documents/  The demo document types
samples/MartenStudio.Sample/Auth/        Cookie auth, the three demo users, the two policies
samples/MartenStudio.Sample/Properties/  launchSettings.json, fixed at http://localhost:5210
tests/MartenStudio.Tests/Endpoints/      Authorization matrix, sub-path re-rooting, the startup guard
tests/MartenStudio.Tests/Services/       Registry, scope resolver, audit log, tenant discovery
tests/MartenStudio.Tests/Components/     bUnit: StudioComponentContext, StudioMarkup and the page tests
tests/MartenStudio.Tests/Support/        Test doubles shared by the tiers - never a mocking library
tests/MartenStudio.Tests/Verify/         The approved API baseline; never hand-edited (hard rule 7)
src/PostgresStudio/Components/Json/      The JSON toolkit: viewer, raw view, editor, copy menu, diffs
src/PostgresStudio/Services/Json/        Flat model, path expressions, pretty printer, canonicalizer,
                                         round-trip differ, C# record generator
src/MartenStudio/Services/Query/         Search grammar, index advisor, the document list request, LINQ mode
src/PostgresStudio/Services/Query/       The SQL console service, export, saved queries, error text, index
                                         verdicts and search chips
tests/MartenStudio.Tests/Json/           Unit and bUnit tests for the JSON toolkit
tests/MartenStudio.Tests/Sql/            SQL builders, guard theory, grammar, advisor - no database
tests/MartenStudio.Integration.Tests/Sql/ Live Postgres: read-only session, builders, catalog, estimator
src/MartenStudio/Services/Documents/      Document read and write services and their DTOs
tests/MartenStudio.Tests/Documents/       Document service unit tests - no database
tests/MartenStudio.Integration.Tests/Documents/ Live Postgres: the document write path end to end
src/MartenStudio/Services/Projections/    Projection data service, daemon accessor, operation tracker
src/PostgresStudio/Services/Live/         Snapshot cache, the per-page polling helper (D10), the nav indicators
src/MartenStudio/Services/Live/           The tracker observer: the in-process daemon's latest shard state (D10)
src/PostgresStudio/Services/Settings/     The shared studio's settings seam, descriptor, links, mount path, registration
                                         marker and core registrations (PS-1a; moved whole in PS-2)
src/PostgresStudio/Services/Sources/      The source seam - databases, the source provider, the shared resolver - and the
                                         seams MartenStudio implements (PS-1a-1d; moved whole in PS-2)
src/MartenStudio/Components/Pages/Projections/  The projections screen and its parts
samples/MartenStudio.SampleDomain/Events/ The demo events and the three projections
tests/MartenStudio.Tests/Projections/     Daemon accessor, snapshot cache, live updates, operation tracker, page
tests/MartenStudio.Integration.Tests/Projections/  Live Postgres + a real Solo daemon
tests/MartenStudio.Integration.Tests/Logging/  Live hosts with no daemon and an externally managed one: no Warning from polling
src/MartenStudio/Services/Events/         Event data service, aggregate invoker, event DTOs and links
src/MartenStudio/Components/Pages/Events/ Streams, stream detail, feed, event types, dead letters
tests/MartenStudio.Tests/Events/          Event builder cases, links, and the five page tests - no database
tests/MartenStudio.Integration.Tests/Events/  Live Postgres: the event reads and the dead-letter writes
src/MartenStudio/Services/Schema/         Schema data service, index advice, migration risk and their DTOs
src/PostgresStudio/Services/Schema/       The DDL tokenizer, schema declarations, storage parameters, index redundancy
src/MartenStudio/Services/Configuration/  The read-only StoreOptions describer and its DTOs
src/MartenStudio/Components/Pages/Schema/ The schema screen's five tabs and their parts
src/MartenStudio/Components/Pages/Configuration/  The configuration dump screen
tests/MartenStudio.Tests/Schema/          Index advice, the tokenizer, the schema page - no database
tests/MartenStudio.Tests/Configuration/   The describer and the configuration page - no database
tests/MartenStudio.Integration.Tests/Schema/  Live Postgres: drift, migration preview, catalog reads
src/MartenStudio/Components/Pages/Documents/  The documents browser: collections rail, list, detail
src/MartenStudio/Components/Pages/Query/  The Marten query body: the mode switch and LINQ mode
src/PostgresStudio/Components/Pages/Query/  The shared query parts: editor, result grid, stats, saved queries,
                                         examples and the SQL console panel
tests/MartenStudio.Tests/Query/            Composer, guard, export, saved queries, the page - no database
tests/MartenStudio.Integration.Tests/Query/  Live Postgres: Marten where-clause mode and the SQL console
samples/MartenStudio.SampleDomain/Generation/  The demo data generator: size presets, the generator,
                                         truncation, the word lists and the run marker
samples/MartenStudio.Sample/Generation/   The demo-data panel: hosted job, endpoints, progress, daemon lag; and the
                                         "make it unhealthy" switches, their board, endpoints and panel
tests/MartenStudio.Integration.Tests/Generation/  Responsiveness budgets at 100k rows, and at 1.2M opt-in; the
                                         unhealthy switches' gates, markup, checks and clean-up
docs/                                    Long-form documentation linked from README.md; never packed
docs/screenshots/                        The fourteen README screenshots (ScreenshotCaptureTests retakes them)
docs/maintainers/                        Maintainer reference moved out of AGENTS.md: this tree, the dated package
                                         budget entries (package-budget-changes.md) and the evidence and history
                                         behind the rules (decision-notes.md); never packed
src/PostgresStudio/Services/Relationships/    The relationship graph service, its DTOs and the SVG layout
src/MartenStudio/Services/Relationships/      The Marten half of the graph: what StoreOptions says of the document types
src/PostgresStudio/Components/Pages/Relationships/  The relationships screen: the diagram, the table, the legend
tests/MartenStudio.Tests/Relationships/       Layout, graph builder, SQL shape, page and panel - no database
tests/MartenStudio.Integration.Tests/Relationships/  Live Postgres: drift both ways, visibility, bounded counts
tests/MartenStudio.Integration.Tests/Browser/  Playwright over the sample host on real Kestrel: thirteen scenarios, the D19 gate, the large-data passes
src/PostgresStudio/Services/Database/     The database browser: the per-visitor gate, the schema matcher, the
                                         ownership classifier, the catalog cache and the object service
src/PostgresStudio/Components/Pages/Database/  The database browser's screens: the rail, the kind tabs and grids,
                                         object detail and its tabs, the Rows and Relationships slots
tests/MartenStudio.Tests/Database/        Gate, matcher, classifier, catalog SQL shape, options - no database
tests/MartenStudio.Integration.Tests/Database/  Live Postgres: catalog reads, the gate and its audit, no DDL,
                                         and the relational demo schemas' idempotency and demo-data flows
src/PostgresStudio/Services/Health/       The health advisor: check model and registry, sweeper, sweep cache,
                                         history, per-visitor report and gates, mutes and toasts
src/PostgresStudio/Services/Health/Checks/  One file per shared health check (attribute, gatherer, pure evaluator); a
                                         check is added by adding its file here, nothing else
src/MartenStudio/Services/Health/         What MartenStudio's checks read of the store (MartenHealthContext)
src/MartenStudio/Services/Health/Checks/  One file per Marten-only health check - the store, the event store, the
                                         daemon - run against Marten stores only; added the same way
src/PostgresStudio/Services/Health/Export/  The opt-in ASP.NET Core health check: one run per interval over every
                                         store and database, summarised as codes and counts, never names
src/PostgresStudio/Components/Pages/Health/ The Health page and its presentation helpers
tests/MartenStudio.Tests/Health/          Evaluators, sweeper, cache, history, gates, page, badge, toast, the
                                         check and docs conventions - no database
tests/MartenStudio.Integration.Tests/Health/  Live Postgres: each check fires and stays quiet, the no-DDL /
                                         no-lock sweep helper, and the fires/quiet attribute convention
docs/health/                             One file per health area, one "## <code> <title>" anchor per check
tests/MartenStudio.Integration.Tests/Tenancy/  Live Postgres: first navigation per tenancy shape changes nothing,
                                         and the enumerations that still apply a lookup table
src/PostgresStudio/Services/Server/       The server facts every W5 packet uses: the PostgreSQL version gates
                                         (PostgresFeatures), the reading role's grants and the "not checked"
                                         sentences (PostgresGrants), and server-setting remedies
tests/PostgresStudio.Tests/Server/        The gate table, the grant sentence per platform and grant, the remedy kinds
tests/PostgresStudio.Integration.Tests/Versions/  The version matrix (PostgreSQL 14-18, 19's RC as a preview leg),
                                         the Aurora shim, MatrixReads and VersionMatrixLiveTests: one never-reused
                                         server per major, started on first use (an assembly fixture)
src/PostgresStudio/Services/Maintenance/  The Maintenance page's facts (the regular sweep's own reads), its
                                         per-visitor view, and the on-demand bloat measure
src/PostgresStudio/Components/Pages/Maintenance/  The Maintenance page: autovacuum's triggers per table, running
                                         maintenance, the workers, the freeze forecast and the vacuum horizon
tests/PostgresStudio.Integration.Tests/Maintenance/  Live Postgres on autovacuum servers of their own: T07 against
                                         each table's triggers, T08, T16, T24, S02, the bloat measure, C06's VACUUM
                                         at the head and T14's standby through a slot
src/PostgresStudio/Services/Storage/      The Storage page's facts (sizes from relpages, growth over the statistics
                                         window, the largest tables) and its per-visitor view (W5-CAP)
src/PostgresStudio/Components/Pages/Storage/  The Storage page: the tiles and the largest tables
tests/PostgresStudio.Tests/Storage/       The statistics window over hand-built histories, the growth arithmetic, the
                                         Storage builder's D27 fence - no database
tests/MartenStudio.Tests/Storage/         bUnit: the Storage page and its per-visitor view in MartenStudio's words
tests/PostgresStudio.Integration.Tests/Storage/  Live Postgres on 14-18: the Storage facts, behind a held lock, no DDL,
                                         and the statistics window across a real crash
src/PostgresStudio/Services/Replication/  The Replication page's facts (recovery, standbys, slot statistics, the archiver,
                                         WAL, checkpoints, pg_stat_io, tablespaces), A2's not-applicable rules, the
                                         advisor's counter windows, and the per-visitor service
src/PostgresStudio/Components/Pages/Replication/  Operations › Replication: slots, standbys, the archiver, WAL,
                                         checkpoints, I/O by backend type and tablespaces
tests/PostgresStudio.Tests/Replication/   R02-R04's evaluators at every edge, WAL file names, the facts' version shapes,
                                         the per-visitor view and the page
tests/PostgresStudio.Integration.Tests/Replication/  Live primary and standby pairs (17 and 18): R02-R04 fire and stay
                                         quiet, the page's facts, and Aurora's not-applicable sections
src/PostgresStudio/Components/Pages/Server/  System › Server: the server's facts, what the studio can see, the curated
                                         settings, the extension chips, the security posture and the platform slot
tests/PostgresStudio.Integration.Tests/Server/  Live S04-S10 and K05-K07: each fires and stays quiet, the server facts on
                                         14-18 as app and probe, and their no-lock, no-DDL reads
src/PostgresStudio/Services/Aurora/       Aurora (W5-AUR, issue #8 A1-A8): its own reads by name, the replica status, the wait
                                         profile and its samples, the parameter reference, the host's capacity reader, and
                                         the Server page section's facts and per-visitor view
tests/PostgresStudio.Tests/Aurora/        By-name mapping of Aurora's functions, the wait windows, commit latency, the capacity
                                         reader, the parameter wording and R03's aurora-reader view - no database
tests/PostgresStudio.Integration.Tests/Aurora/  Live on VERS's Aurora shim: the instance, R03 aurora-reader, the wait section, a
                                         failover, the not-applicable checks, C03's remedy and the spoof-proof markers
src/PostgresStudio/Services/Statements/   The Statements page (W5-Q1): its facts (the statement sweep's own read and
                                         history), the per-visitor view behind the activity gate, the plain classifier
src/PostgresStudio/Components/Pages/Statements/  Operations › Statements: the top statements by total time, and by group
tests/PostgresStudio.Tests/Statements/    The window and share arithmetic, the view's ranking and groups, the gate, the
                                         classifier, the cost meter's connection tracker, and the page - no database
tests/MartenStudio.Tests/Statements/      MartenStudio's classifier over Marten's own recorded SQL, and its metered connections
tests/PostgresStudio.Integration.Tests/Statements/  Live pg_stat_statements on a server of its own, 14 and 18 through the
                                         matrix, and the Aurora shim's storage reads in the 15/16 and 17 shapes
tests/PostgresStudio.Tests/Tables/        T15, T19, T20, T25 and T26 at their edges, the usage guards, foreign-key coverage,
                                         sequence headroom and the lint statements' D27 shape - no database
src/PostgresStudio/Diagnostics/          The public diagnostics read model (D31): IPostgresDiagnostics, its request, snapshot,
                                         findings, checks and sections - namespace PostgresStudio, like the registration types
src/PostgresStudio/Services/Diagnostics/  The API's service, host gate, mapper, snapshot builders, read limiter and JSON
                                         writer, and the Health page's visitor download builder (W5-API)
tests/PostgresStudio.Tests/Diagnostics/   The mapper over every enum member, the host gate's sentinels, the JSON baselines, the
                                         limiter, the sections from hand-built facts, the visitor download and the Snapshot
                                         menu - no database
tests/PostgresStudio.Integration.Tests/Diagnostics/  Live: G9 by application as app and probe, a full snapshot that
                                         creates nothing, three databases behind a database source, an API-only host, and
                                         the statements section on a pg_stat_statements server
tests/MartenStudio.Integration.Tests/Diagnostics/  Live: the API over a conjoined store (a tenant's finding needs
                                         IncludeActivity) and a master-table tenancy that lists nothing
```
