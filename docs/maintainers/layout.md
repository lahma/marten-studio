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
  Build.Publish.cs                       Pack (the three packages) + the in-build nuget.org OIDC token exchange (D15)
  Build.Marten.cs                        --marten-version: the floor, the latest-tested Marten or an exact one (D3)
  Build.Npgsql.cs                        --npgsql-version and NpgsqlPass, PostgresStudio's latest-Npgsql pass (D30)
  Build.Confluent.cs                     --confluent-version: KStudio's floor, latest-tested Confluent.Kafka or an exact one (D36)
build/Extensions/                        Vendored Cake helpers (StringExtensions) - do not edit
src/                                     The shipped libraries, and nothing else: the shared studio and MartenStudio
src/PostgresStudio.Diagnostics/          The diagnostics engine, with no ASP.NET Core (0.11-C, issue #29): the health sweep and its
                                         checks, the facts readers, the sources, the settings seam and IPostgresDiagnostics.
                                         References no Marten, no MartenStudio and no web stack (PostgresStudioFenceTests,
                                         CoreFenceTests); namespaces stay PostgresStudio.*, and the UI reaches its internals
                                         through InternalsVisibleTo
src/PostgresStudio/                      The shared studio's Razor Class Library (0.5, PS-2): shell, routes, scope,
                                         authorization, audit, the database browser, Sessions, the Health page, the SQL
                                         console, Relationships, the JSON toolkit and wwwroot/. Built on the diagnostics
                                         engine; references no Marten and no MartenStudio (PostgresStudioFenceTests, CoreFenceTests)
src/PostgresStudio/Components/           the shared document every root renders, route table, links, scope query
src/PostgresStudio/Components/Shared/    small building-block components: TableScroll (D25), CopyButton,
                                         dialogs, chips, the empty and error states
src/PostgresStudio/Internal/             startup authorization guard, endpoint marker, mapped-endpoint
                                         tracking and the endpoint mapper (D11)
src/PostgresStudio.Diagnostics/Internal/  Nothing but the engine's SQL builders, in Internal/Sql/ (hard rule 4)
src/PostgresStudio.Diagnostics/Services/  The scope types, the shared log events (StudioLog, PostgresStudioLog), the log throttle and the
                                         capping of a visitor's value in a log line
src/PostgresStudio/Services/             state, authorization, the capability guard, the action log and toasts
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
samples/KStudio.Sample/                  The KStudio sample: Marten Studio and KStudio over throwaway Postgres and Kafka (D18)
samples/KStudio.Sample/Kafka/            Its Kafka container, seeder and judge
samples/KStudio.Sample/Auth/             Its cookie sign-in, copied from the Marten sample
samples/KStudio.Sample/Wolverine/        Its Wolverine application: the checkout handler, document and startup sender (D-CORR decision 11)
samples/KStudio.Sample/Properties/       Its launch profile, port 5212
samples/PostgresStudio.Sample/           The Postgres Studio sample host (D18), no Marten: an admin database and three
                                         tenant databases through its own IPostgresDatabaseSource - README snippet regions
samples/PostgresStudio.Sample/Auth/      Cookie auth, the three demo users, the studio policy and the per-capability one
samples/PostgresStudio.Sample/Properties/  launchSettings.json, fixed at http://localhost:5211
samples/PostgresStudio.Worker/           The diagnostics worker (0.11-D, issue #29): a generic host that references
                                         src/PostgresStudio.Diagnostics alone - no ASP.NET Core, no studio - and logs a
                                         summary of every database's findings at start and every Summary:Interval (D18)
tests/                                   All seven test projects match *.Tests, so one Test run covers them all
tests/MartenStudio.Tests/                Fast suite: unit, bUnit, endpoint metadata, API snapshot,
                                         conventions. No Docker
tests/MartenStudio.Tests/Conventions/    Rules too easy to break silently to leave to review
tests/MartenStudio.Tests/Marten/         MartenApiSurfaceTest - the Marten 9.35 contract, pinned
tests/MartenStudio.Integration.Tests/    Testcontainers Postgres + Playwright. Needs Docker
src/PostgresStudio.Diagnostics/Services/Postgres/  The engine's plain-Postgres half: the source registry and provider over the application's
                                         NpgsqlDataSources and host database sources, the diagnostics settings and descriptor, and the
                                         seams' implementations (declarations, tables, remedies, locks, statements' words), and
                                         AddPostgresDiagnostics's seam registration
src/PostgresStudio/Services/Postgres/    PostgresStudio's own: the studio's settings and descriptor, the links out of the browser
                                         and the Overview's server read
tests/PostgresStudio.Tests/              PostgresStudio's fast suite: no Docker, and no Marten anywhere in its graph
tests/PostgresStudio.Tests/Components/   bUnit: PostgresStudioComponentContext and the core pages' smoke tests, the plan view
                                         and the console's Explain buttons
tests/PostgresStudio.Tests/Components/Json/  The JSON viewer's parameters a host that is not a Postgres studio sets (KStudio): no download, no
                                         Postgres copy items, and a host's "Copy as search"
tests/PostgresStudio.Tests/Conventions/  The assembly fence: no Marten family in the assembly, the restore or the output folder
tests/PostgresStudio.Tests/Endpoints/    An API-only host with one NpgsqlDataSource; the two registration guards
tests/PostgresStudio.Tests/Sources/      The source provider over DI descriptors and host database sources
tests/PostgresStudio.Tests/Support/      PostgresStudio's own test doubles (the shared ones are linked from MartenStudio.Tests)
tests/PostgresStudio.Tests/Verify/       PostgresStudio's approved API baseline; never hand-edited (hard rule 7)
tests/PostgresStudio.Integration.Tests/  PostgresStudio's live suite: Testcontainers Postgres under a reuse label of its own
                                         (never the Marten suite's container) + Playwright. Needs Docker; no Marten
tests/PostgresStudio.Integration.Tests/Browser/  Playwright over samples/PostgresStudio.Sample on real Kestrel: the
                                         scenarios, where menus open (1.0), the D19 gate, and the zero-config start
tests/PostgresStudio.Integration.Tests/Database/  Live Postgres: first navigation writes nothing, the D27 gate and its
                                         audit, Sessions, the pool panel at both Npgsql majors, the sample's tenant databases
tests/PostgresStudio.Integration.Tests/Health/  Live Postgres: the table checks fire and stay quiet over the
                                         whole-database table source, and the export over a host source past its budget
tests/PostgresStudio.Integration.Tests/Query/  Live Postgres: every SQL console example runs, and the Explain buttons on
                                         14 and 18; the what-if on a hypopg server of its own (postgres:17.11 +
                                         postgresql-17-hypopg)
tests/PostgresStudio.Integration.Tests/Sources/  Live Postgres: discovery, and isolation between a host source's
                                         databases through every cache
src/PostgresStudio.Diagnostics/Internal/Sql/  The engine's builders, SqlIdentifier and SqlLexer: the first of the three places a Postgres
                                         identifier becomes SQL text (hard rule 4) - the health sweep's and the facts readers'
                                         statements, the catalog's, the read-only guard and session; the blocking chain's
                                         hand-over text (X3); partition bounds and column skew (X4)
src/PostgresStudio/Internal/Sql/         The shared studio's own builders - the second place (hard rule 4): the SQL console's
                                         plans and their settings (X1), the facts beside a plan (0.10), hypopg's calls (X2),
                                         the browser's rows, Relationships and the console's examples; the same namespace
                                         as the engine's, each assembly exempt in its own folder only
src/MartenStudio/Internal/Sql/           The Marten builders - documents, events, the daemon, projections: the
                                         third (hard rule 4); Marten's own DDL for a what-if (X2); the dead-letter
                                         groups (M3)
src/PostgresStudio/Components/Layout/    Shell chrome: layout, nav, scope selector, capability chip, the
                                         preferences popover (theme and time zone, behind the gear)
src/PostgresStudio/Components/Pages/     The shared routed pages; each @page must be rooted at /postgres
src/MartenStudio/Components/Pages/       The Marten routed pages; each @page must be rooted at /marten
samples/MartenStudio.SampleDomain/Documents/  The demo document types
samples/MartenStudio.Sample/Auth/        Cookie auth, the three demo users, the two policies
samples/MartenStudio.Sample/Properties/  launchSettings.json, fixed at http://localhost:5210
tests/MartenStudio.Tests/Endpoints/      Authorization matrix, sub-path re-rooting, the startup guard
tests/MartenStudio.Tests/Links/          MartenStudioRouteLinks, the record search button and the pages' scope from a link - no database
tests/MartenStudio.Tests/Services/       Registry, scope resolver, audit log, tenant discovery
tests/MartenStudio.Tests/Components/     bUnit: StudioComponentContext, StudioMarkup and the page tests
tests/MartenStudio.Tests/Support/        Test doubles shared by the tiers - never a mocking library
tests/MartenStudio.Tests/Verify/         The approved API baseline; never hand-edited (hard rule 7)
src/PostgresStudio/Components/Json/      The JSON toolkit: viewer, raw view, editor, copy menu, diffs
src/PostgresStudio/Services/Json/        Flat model, path expressions, pretty printer, canonicalizer,
                                         round-trip differ, C# record generator
src/MartenStudio/Services/Query/         Search grammar, index advisor, the document list request, LINQ mode
src/PostgresStudio.Diagnostics/Services/Query/  The scan of an EXPLAIN plan for the relations it reads, which the statement checks take
src/PostgresStudio/Services/Query/       The SQL console service, export, saved queries, error text, index
                                         verdicts and search chips; the plan reader and the plan insights (X1); the
                                         what-if engine and its hypothetical indexes (X2)
tests/MartenStudio.Tests/Json/           Unit and bUnit tests for the JSON toolkit
tests/MartenStudio.Tests/Sql/            SQL builders, guard theory, grammar, advisor - no database
tests/MartenStudio.Integration.Tests/Sql/ Live Postgres: read-only session, builders, catalog, estimator
src/MartenStudio/Services/Documents/      Document read and write services and their DTOs; the newest-first orders and
                                         Marten's edit preview (M5)
tests/MartenStudio.Tests/Documents/       Document service unit tests - no database
tests/MartenStudio.Integration.Tests/Documents/ Live Postgres: the document write path end to end
src/MartenStudio/Services/Tenants/        The Tenants overview's service (TenantOverviewService), its models and TenantBoundText:
                                         read-only, statistics only, activity-gated
src/MartenStudio/Services/Links/          The links a module's page makes into Documents and Streams (D-CORR 7): MartenStudioRouteLinks, by type
                                         name and id, with a store and a tenant and never a database; pure, reads StoreOptions only
src/MartenStudio/Services/Find/           Find by id's Marten kinds (1.2-SCOPE-3): the document, stream and natural-key, and event-sequence
                                         contributors, each asking the data service of the page its hit opens, and the helper (FindScopes)
                                         that reads the event store's shape once per Find and decides the tenant restriction (FindTenants:
                                         under a store policy, the tenants the header lists, whatever discovery found)
src/MartenStudio/Services/Shapes/         The shape check's core (1.2-SHAPE), `EventShapeCheckService` among it: sampler limits and cache, the round-trip classifier, the
                                         failure describer, the aggregator and the sentences; the documents' check service
src/MartenStudio/Components/Shapes/       The shape check's panels and the findings table
tests/MartenStudio.Tests/Shapes/          The shape check's classifier, sentences, queries, cache and panels - no database
src/MartenStudio/Services/Projections/    Projection data service, daemon accessor, operation tracker; the lag history
                                         (M2); the progression-row classifier and the daemon topology (M47)
src/PostgresStudio.Diagnostics/Services/Live/  The single-flight snapshot cache every reader shares (D10)
src/PostgresStudio/Services/Live/         The per-page polling helper (D10) and the nav indicators, and the per-circuit
                                         since-marks
src/MartenStudio/Services/Live/           The tracker observer: the in-process daemon's latest shard state (D10)
src/PostgresStudio.Diagnostics/Services/Settings/  The engine's settings seam, descriptor, words, mount path, registration marker, option validation and
                                         core registration (AddPostgresDiagnostics and every Add*Studio call it)
src/PostgresStudio/Services/Settings/     The studio's settings seam (it extends the engine's), descriptor, links, registration
                                         marker, option validation and core registration (PS-1a; moved whole in PS-2)
src/PostgresStudio.Diagnostics/Services/Sources/  The source seam - databases, the source provider, the declarations, the table and lock seams
src/PostgresStudio/Services/Sources/      The shared resolver and the seams MartenStudio implements for a UI (PS-1a-1d; moved
                                         whole in PS-2)
src/MartenStudio/Components/Pages/Projections/  The projections screen and its parts
samples/MartenStudio.SampleDomain/Events/ The demo events and the three projections
tests/MartenStudio.Tests/Projections/     Daemon accessor, snapshot cache, live updates, operation tracker, page
tests/MartenStudio.Integration.Tests/Projections/  Live Postgres + a real Solo daemon
tests/MartenStudio.Integration.Tests/Logging/  Live hosts with no daemon and an externally managed one: no Warning from polling
src/MartenStudio/Services/Events/         Event data service, aggregate invoker, event DTOs and links; the stepper's
                                         fold (M1); the dead-letter grouping and fingerprint (M3)
src/MartenStudio/Components/Pages/Events/ Streams, stream detail and its stepper, feed, event types, dead letters and
                                         their groups by cause
tests/MartenStudio.Tests/Events/          Event builder cases, links, the page tests, the stepper's fold and the
                                         dead-letter groups - no database
tests/MartenStudio.Integration.Tests/Events/  Live Postgres: the event reads and the dead-letter writes, the stepper
                                         against Marten's own fold, and the dead-letter groups over a corpus
src/MartenStudio/Services/Schema/         Schema data service, index advice, migration risk and their DTOs
src/PostgresStudio.Diagnostics/Services/Schema/  The DDL tokenizer, schema declarations, storage parameters, index redundancy
                                         and foreign-key coverage the table checks judge by
src/MartenStudio/Services/Configuration/  The read-only StoreOptions describer and its DTOs
src/MartenStudio/Components/Pages/Schema/ The schema screen's five tabs and their parts
src/MartenStudio/Components/Pages/Configuration/  The configuration dump screen
tests/MartenStudio.Tests/Schema/          Index advice, the tokenizer, the schema page - no database
tests/MartenStudio.Tests/Configuration/   The describer and the configuration page - no database
tests/MartenStudio.Integration.Tests/Schema/  Live Postgres: drift, migration preview, catalog reads
src/MartenStudio/Services/Connection/     Database > Connection's Marten half (1.1, NP4): the contributor, the command-timeout
                                         resolution, the data-source origin and the section's rows
tests/MartenStudio.Tests/Connection/      The Marten connection section, its hint notes, the redaction sentinel and the page - no database
tests/MartenStudio.Integration.Tests/Connection/  Live Postgres: UseNpgsqlDataSource() versus Connection(string), and the hosted
                                         daemon's connections
src/MartenStudio/Components/Shared/       MartenStudio's building blocks: RecordSearchLinkButton (D-CORR 8), the module's search from a record screen
src/MartenStudio/Components/Pages/Documents/  The documents browser: collections rail, list, detail
src/MartenStudio/Components/Pages/Query/  The Marten query body: the mode switch and LINQ mode
src/PostgresStudio/Components/Pages/Query/  The shared query parts: editor, result grid, stats, saved queries,
                                         examples, the SQL console panel and the plan view, and the What if? panel
tests/MartenStudio.Tests/Query/            Composer, guard, export, saved queries, the page - no database
tests/MartenStudio.Integration.Tests/Query/  Live Postgres: Marten where-clause mode and the SQL console; LINQ's and
                                         Documents' what-if on a hypopg server
samples/MartenStudio.SampleDomain/Generation/  The demo data generator: size presets, the generator,
                                         truncation, the word lists and the run marker
samples/MartenStudio.Sample/Generation/   The demo-data panel: hosted job, endpoints, progress, daemon lag; and the
                                         "make it unhealthy" switches, their board, endpoints and panel
tests/MartenStudio.Integration.Tests/Generation/  Responsiveness budgets at 100k rows, and at 1.2M opt-in; the
                                         unhealthy switches' gates, markup, checks and clean-up
docs/                                    Long-form documentation linked from README.md; never packed
docs/screenshots/                        The fourteen README screenshots (ScreenshotCaptureTests retakes them)
docs/maintainers/                        Maintainer reference moved out of AGENTS.md: this tree, the dated package
                                         budget entries (package-budget-changes.md), the evidence and history
                                         behind the rules (decision-notes.md) and the full texts of the rules
                                         and decisions AGENTS.md summarises, by area: hard-rules.md,
                                         architecture.md, sql-and-catalog.md, marten.md, authorization.md,
                                         health-engine.md, ui.md, plan-analyzer.md, testing.md and
                                         build-and-release.md; never packed
src/PostgresStudio/Services/Relationships/    The relationship graph service, its DTOs and the SVG layout
src/MartenStudio/Services/Relationships/      The Marten half of the graph: what StoreOptions says of the document types
src/PostgresStudio/Components/Pages/Relationships/  The relationships screen: the diagram, the table, the legend
tests/MartenStudio.Tests/Relationships/       Layout, graph builder, SQL shape, page and panel - no database
tests/MartenStudio.Integration.Tests/Relationships/  Live Postgres: drift both ways, visibility, bounded counts
tests/MartenStudio.Integration.Tests/Browser/  Playwright over the sample host on real Kestrel: thirteen scenarios, the D19 gate, the large-data passes
src/PostgresStudio.Diagnostics/Services/Database/  What the engine reads of a database: the schema matcher, the ownership classifier, the lock graph (X3),
                                         the sessions' reader, models and pool observer, and the statement screen
src/PostgresStudio/Services/Database/     The database browser: the per-visitor gate, the catalog cache and the object
                                         service; Sessions' per-visitor service; the visitor's links
src/PostgresStudio/Components/Pages/Database/  The database browser's screens: the rail, the kind tabs and grids,
                                         object detail and its tabs, the Rows and Relationships slots; Sessions and
                                         its Blocking tab (X3)
tests/MartenStudio.Tests/Database/        Gate, matcher, classifier, catalog SQL shape, options - no database
tests/MartenStudio.Integration.Tests/Database/  Live Postgres: catalog reads, the gate and its audit, no DDL,
                                         and the relational demo schemas' idempotency and demo-data flows
src/PostgresStudio.Diagnostics/Services/Health/  The health advisor's engine: check model and registry, sweeper, sweep cache, history, the
                                         gate every reader applies, links and platform
src/PostgresStudio/Services/Health/       The health advisor's visitor half: the per-visitor report and gates, mutes and toasts,
                                         and the finding actions' seam and runner
src/PostgresStudio.Diagnostics/Services/Health/Checks/  One file per shared health check (attribute, gatherer, pure evaluator); a
                                         check is added by adding its file here, nothing else
src/PostgresStudio/Services/Health/Checks/  The three checks that judge the studio's own exposure (K05-K07): they read the
                                         studio's settings and mapping, so they are run only where a studio is registered
src/MartenStudio/Services/Health/         What MartenStudio's checks read of the store (MartenHealthContext), and E06's
                                         on-demand action
src/MartenStudio/Services/Health/Checks/  One file per Marten-only health check - the store, the event store, the
                                         daemon - run against Marten stores only; added the same way
src/PostgresStudio.Diagnostics/Services/Health/Export/  The opt-in ASP.NET Core health check: one run per interval over every
                                         store and database, summarised as codes and counts, never names
src/PostgresStudio/Components/Pages/Health/ The Health page and its presentation helpers
tests/MartenStudio.Tests/Health/          Evaluators, sweeper, cache, history, gates, page, badge, toast, the
                                         check and docs conventions - no database
tests/MartenStudio.Integration.Tests/Health/  Live Postgres: each check fires and stays quiet, the no-DDL /
                                         no-lock sweep helper, and the fires/quiet attribute convention
docs/health/                             One file per health area, one "## <code> <title>" anchor per check; plans.md for
                                         the plan insights (P)
tests/MartenStudio.Integration.Tests/Tenancy/  Live Postgres: first navigation per tenancy shape changes nothing,
                                         and the enumerations that still apply a lookup table
src/PostgresStudio.Diagnostics/Services/Server/  The server facts every W5 packet uses: the PostgreSQL version gates
                                         (PostgresFeatures), the reading role's grants and the "not checked"
                                         sentences (PostgresGrants), and server-setting remedies
src/PostgresStudio/Services/Server/       The Server page's per-visitor service
tests/PostgresStudio.Tests/Server/        The gate table, the grant sentence per platform and grant, the remedy kinds
tests/PostgresStudio.Integration.Tests/Versions/  The version matrix (PostgreSQL 15-19, 19 on its RC),
                                         the Aurora shim, MatrixReads and VersionMatrixLiveTests: one never-reused
                                         server per major, started on first use (an assembly fixture)
src/PostgresStudio.Diagnostics/Services/Maintenance/  The Maintenance page's facts (the regular sweep's own reads) and the page's route
src/PostgresStudio/Services/Maintenance/  The Maintenance page's per-visitor view, and the on-demand bloat measure
src/PostgresStudio/Components/Pages/Maintenance/  The Maintenance page: autovacuum's triggers per table, running
                                         maintenance, the workers, the freeze forecast and the vacuum horizon
tests/PostgresStudio.Integration.Tests/Maintenance/  Live Postgres on autovacuum servers of their own: T07 against
                                         each table's triggers, T08, T16, T24, S02, the bloat measure, C06's VACUUM
                                         at the head and T14's standby through a slot
src/PostgresStudio.Diagnostics/Services/Storage/  The Storage page's facts (sizes from relpages, growth over the statistics window, the largest
                                         tables) and the words for a growth not yet known (W5-CAP)
src/PostgresStudio/Services/Storage/      The Storage page's per-visitor view
src/PostgresStudio/Components/Pages/Storage/  The Storage page: the tiles and the largest tables
tests/PostgresStudio.Tests/Storage/       The statistics window over hand-built histories, the growth arithmetic, the
                                         Storage builder's D27 fence - no database
tests/MartenStudio.Tests/Storage/         bUnit: the Storage page and its per-visitor view in MartenStudio's words
tests/PostgresStudio.Integration.Tests/Storage/  Live Postgres on 15-19: the Storage facts, behind a held lock, no DDL,
                                         and the statistics window across a real crash
src/PostgresStudio.Diagnostics/Services/Replication/  The Replication page's facts (recovery, standbys, slot statistics, the archiver,
                                         WAL, checkpoints, pg_stat_io, tablespaces), A2's not-applicable rules and the
                                         advisor's counter windows
src/PostgresStudio/Services/Replication/  The Replication page's per-visitor service
src/PostgresStudio/Components/Pages/Replication/  Operations › Replication: slots, standbys, the archiver, WAL,
                                         checkpoints, I/O by backend type and tablespaces
tests/PostgresStudio.Tests/Replication/   R02-R04's evaluators at every edge, WAL file names, the facts' version shapes,
                                         the per-visitor view and the page
tests/PostgresStudio.Integration.Tests/Replication/  Live primary and standby pairs (17 and 18): R02-R04 fire and stay
                                         quiet, the page's facts, and Aurora's not-applicable sections
src/PostgresStudio/Components/Pages/Server/  System › Server: the server's facts, what the studio can see, the curated
                                         settings, the extension chips, the security posture and the platform slot
tests/PostgresStudio.Integration.Tests/Server/  Live S04-S10 and K05-K07: each fires and stays quiet, the server facts on
                                         15-19 as app and probe, and their no-lock, no-DDL reads; also the connection
                                         live tests (ConnectionObservedLiveTests, ConnectionSettingChecksLiveTests,
                                         ConnectionTlsLiveTests, ConnectionCompareLiveTests, ConnectionPageLiveTests)
src/PostgresStudio.Diagnostics/Services/Aurora/  Aurora (W5-AUR, issue #8 A1-A8): its own reads by name, the replica status, the wait
                                         profile and its samples, the parameter reference and the host's capacity reader
src/PostgresStudio/Services/Aurora/       The Server page Aurora section's per-visitor view
src/PostgresStudio/Services/Connection/   Database > Connection's per-visitor half (1.1, NP3): the service and its two gates, the page
                                         builder, the facts source (the sweep's shared read, or one per HealthInterval), the links and
                                         the internal IConnectionInsightContributor seam
tests/PostgresStudio.Tests/Aurora/        By-name mapping of Aurora's functions, the wait windows, commit latency, the capacity
                                         reader, the parameter wording and R03's aurora-reader view - no database
tests/PostgresStudio.Integration.Tests/Aurora/  Live on VERS's Aurora shim: the instance, R03 aurora-reader, the wait section, a
                                         failover, the not-applicable checks, C03's remedy and the spoof-proof markers
src/PostgresStudio.Diagnostics/Services/Statements/  The Statements page's facts (the statement sweep's own read and history), the plain classifier,
                                         the page's route and the words for pg_stat_statements' state (W5-Q1)
src/PostgresStudio/Services/Find/         Find by id (1.2-SCOPE-3): the typed value's parser (FindQuery), the seam (IFindContributor), the
                                         circuit's service (FindService: one Find at a time, a 10 s budget) and Postgres Studio's own
                                         kinds, tables and views and statement queryids
src/PostgresStudio/Services/Statements/   The Statements page's per-visitor view behind the activity gate, and the drill-in's planner
src/PostgresStudio/Components/Pages/Statements/  Operations › Statements: the top statements by total time, and by group,
                                         and one statement's drill-in
tests/PostgresStudio.Tests/Statements/    The window and share arithmetic, the view's ranking and groups, the gate, the
                                         classifier, the cost meter's connection tracker, and the page; the plan reader
                                         and insights over ExplainFixtures.json (PROBE's 108 plans), and the drill-in
                                         - no database
tests/PostgresStudio.Tests/Statements/PlanCorpus/  The plan analyzer's corpus (X3, issue #28): one EXPLAIN (FORMAT JSON) fixture
                                         for each of P09 to P20 firing and quiet, and the shapes the views are tested on
tests/MartenStudio.Tests/Statements/      MartenStudio's classifier over Marten's own recorded SQL, and its metered connections
tests/PostgresStudio.Integration.Tests/Statements/  Live pg_stat_statements on a server of its own, 14 and 18 through the
                                         matrix, and the Aurora shim's storage reads in the 15/16 and 17 shapes, and
                                         the drill-in's plan on 14, 16 and 18
tests/PostgresStudio.Tests/Tables/        T15, T19, T20, T25 and T26 at their edges, the usage guards, foreign-key coverage,
                                         sequence headroom and the lint statements' D27 shape - no database
src/PostgresStudio.Diagnostics/Diagnostics/  The public diagnostics read model (D31): IPostgresDiagnostics, its request, snapshot,
                                         findings, checks and sections - namespace PostgresStudio, like the registration types
src/PostgresStudio.Diagnostics/Services/Diagnostics/  The API's service, host gate, mapper, snapshot builder, read limiter and JSON writer (W5-API)
src/PostgresStudio/Services/Diagnostics/  The Health page's visitor download builder, its Markdown report and the snapshot
                                         compare, which never reach the API (W5-API)
tests/PostgresStudio.Tests/Diagnostics/   The mapper over every enum member, the host gate's sentinels, the JSON baselines, the
                                         limiter, the sections from hand-built facts, the visitor download and the Snapshot
                                         menu - no database
tests/PostgresStudio.Integration.Tests/Diagnostics/  Live: G9 by application as app and probe, a full snapshot that
                                         creates nothing, three databases behind a database source, an API-only host, and
                                         the statements section on a pg_stat_statements server
tests/MartenStudio.Integration.Tests/Diagnostics/  Live: the API over a conjoined store (a tenant's finding needs
                                         IncludeActivity) and a master-table tenancy that lists nothing
src/PostgresStudio.Diagnostics/Services/ConnectionHistory/  The opt-in connection history (1.0, issue #43): the sampler and its
                                         kept connection per server, the in-memory store and the Holders page's view of a history;
                                         the public options, store seam and sample are at the project's root, beside the registration
tests/PostgresStudio.Tests/ConnectionHistory/  A sample from a holders reading, the server a connection string names, the in-memory
                                         store's window, the page's view and the registration - no database
src/PostgresStudio.Diagnostics/Services/Connection/  The connection-insights engine (1.1, NP1): the catalogue of Npgsql keywords, the
                                         reader that redacts a data source's connection string, the pooler signs, the hints and the
                                         facts the connection checks read (what the server saw of the studio's connection, the
                                         platform, the pools that share the data source)
tests/PostgresStudio.Tests/Connection/  The catalogue against the loaded Npgsql, the reader's provenance and redaction, every hint
                                         each way, the pooler signs and the sentinel test - no database
src/KStudio.Core/                        KStudio's engine (D36): catalog, window resolution, scan engine, predicates,
                                         decoders, governor and redaction, over Confluent.Kafka behind the internal wire
                                         seam. No ASP.NET Core, no studio, no Npgsql (KStudioCoreFenceTests); not packed (K8)
src/KStudio/                             The Kafka module's Razor Class Library (D34, D35): AddKafkaStudio(), its options, capabilities
                                         and resource; no Marten (hard rule 16), no static web assets; not packed (K8)
src/KStudio/Components/                  The module's components, under _Imports.razor
src/KStudio/Components/Layout/           The cluster selector the shell draws in its header on Kafka pages
src/KStudio/Components/Pages/            The Kafka routed pages; each @page must be rooted at /kafka
src/KStudio/Components/Shared/           The page frame (cluster gate, trademark line) and the disabled-capability notice
src/KStudio/Internal/                    The version guard and the startup guard
src/KStudio/Services/                    The module, the cluster scope, authorization, the capability guard, the action log and its gate,
                                         the log events
tests/KStudio.Tests/                     KStudio's fast suite: the engine against the hand-written fake broker, the fences
                                         and the public API baseline. No Docker
src/KStudio.Core/Wire/                   The wire seam (D36): IKafkaWire, the reader, the error values and the neutral records; no
                                         Confluent type crosses it
src/KStudio.Core/Wire/Confluent/         The one implementation over Confluent.Kafka: the overrides (D37), the role filter for host
                                         configuration, the error table, the admin pool, the wire and the scan reader
src/KStudio.Core/Clusters/               The registered clusters: keys and names, the cloned host configuration split by role, the
                                         option bounds and the container registration
src/KStudio.Core/Catalog/                The catalog and overview reads: the cached reads, the ACL table and its sentences, the
                                         overview records and the throughput samples
src/KStudio.Core/Windows/                Window resolution (D38): a time window to offsets per partition, and the notes that say what a
                                         "no match" could be missing
src/KStudio.Core/Logging/                The engine's log events 9650 to 9699, the bounded log values and the log-once helper
src/KStudio.Core/Predicates/             The search grammar (D39): lexer, parser, canonical text, the compiler per topic and the
                                         three-valued evaluator, with the verdict of every clause
src/KStudio.Core/Decoding/               Value and header decoding (D39): the detection order, CloudEvents, the Confluent wire format,
                                         trace contexts, the decode counters and the per-record evaluation context
src/KStudio.Core/Scanning/               The scan engine (D38, D41): plan and estimate, the scan's thread, the governor, the limiter and
                                         the owner budget, batches, the match store and the one factory that makes a match; the key
                                         set and the partitioner verification that let a key search read fewer partitions (D39)
src/KStudio.Core/Redaction/              Redaction inside the engine (D40): the built-in header deny-list, the topic's rules and the
                                         redactor every match is made through
src/KStudio.Core/Correlation/            Correlation (D42): a topic's declared fields and their kinds, the extractor that reads an
                                         anchor's literal fields, the per-topic query builder and the duplicate finder
src/KStudio.Core/Export/                 The export writer (D45): rows from redacted matches, JSON Lines, CSV and one value, and the file
                                         name; pure
src/KStudio.Core/Index/                  The record-index seam (D48): the host's interface, its query and hints, the narrower and the
                                         planner that asks it
tests/KStudio.Tests/Conventions/         KStudio's fences and convention scans
tests/KStudio.Tests/Registration/        AddKafkaStudio, the version guard, the startup guard
tests/KStudio.Tests/Endpoints/           Mounting, endpoint authorization, a host with no data source
tests/KStudio.Tests/Components/          bUnit: the layout on a Kafka page, the rail, the chip, the frame, Activity
tests/KStudio.Tests/Services/            Authorization, the capability guard, the cluster scope, the audit gate and action log
tests/PostgresStudio.Tests/Modules/      The module seam (D34): the composed descriptor, root-keeping routes and a module's audit entries, with fake modules
tests/MartenStudio.Tests/Settings/       The module seam over Marten Studio's descriptor, with a fake module
tests/KStudio.Tests/Verify/              KStudio.Core's public API baseline
tests/KStudio.Tests/Support/             Test helpers the fast suite shares
tests/KStudio.Tests/Support/FakeKafka/   The hand-written fake broker (D36): topics as logs, transactions, configs, groups and an ACL
                                         table, with a wire and a scan reader over it
tests/KStudio.Tests/Contract/            WireContract and WindowContract, run on the fake here and on real brokers by the live suite, and
                                         the harness they run over
tests/KStudio.Tests/Wire/                The overrides, the role filter, the error table, the forbidden-call IL scan and the reader
tests/KStudio.Tests/Clusters/            Registration, key formats and the admin pool
tests/KStudio.Tests/Catalog/             The overview reads, the ACL table, the snapshot cache and the throughput samples
tests/KStudio.Tests/Windows/             Window resolution on the fake
tests/KStudio.Tests/Logging/             The engine's log events and the parity of its log values with the studios'
tests/KStudio.Tests/Predicates/          The grammar, its messages and limits, the canonical text and the evaluation tables
tests/KStudio.Tests/Decoding/            The detection order, the caps, headers, trace contexts and CloudEvents
tests/KStudio.Tests/Redaction/           The deny-list, the topic's rules, the match caps and the one place a match is made
tests/KStudio.Tests/Correlation/         The declarations and their bounds, the extractor, the per-topic query and the duplicate finder
tests/KStudio.Tests/Scanning/            The scan engine on the fake: every stop reason and end state, the cursor, the governor, the
                                         limits, the estimate, batching and the lifecycle
tests/KStudio.Tests/Export/              Export writer, redaction and file name tests (D45)
tests/KStudio.Tests/Index/               Record-index narrowing, clause choice and planning tests (D48)
tests/KStudio.Tests/Partitioning/        The partitioners' known answers, the key set, the verification by sampling and the pruned scan
tests/KStudio.Integration.Tests/         KStudio's live suite: apache/kafka 3.9 and 4.x with SASL and ACLs, through
                                         Testcontainers.Kafka; needs Docker
tests/KStudio.Integration.Tests/Support/  The broker fixtures, one per image, the live contract cluster and the harness tests
tests/KStudio.Integration.Tests/Wire/    WireContract on real brokers
tests/KStudio.Integration.Tests/Windows/  WindowContract on real brokers
tests/KStudio.Integration.Tests/Catalog/  The catalog as principals with different ACLs, and metadata that creates nothing
tests/KStudio.Integration.Tests/Scanning/  The scan engine on real records: stop reasons, transactions, retention before and during a scan, the
                                         governor, the memory ceiling and that a scan writes nothing
tests/KStudio.Integration.Tests/Partitioning/ Each partitioner against a real producer's on two partition counts, and a pruned scan on real brokers
tests/KStudio.Integration.Tests/Redaction/ Payload redaction on real brokers: a scan, a record read, the logs and the exact match, with no masked value in any sink
tests/KStudio.Integration.Tests/Search/  Find the message with this id (the M1 exit), as restricted principals, and the search service against live brokers
tests/KStudio.Integration.Tests/Correlation/ Related records on real brokers (the M4 exit): an in and an out topic found by their own fields in one scan, and a topic the principal may not describe
tests/KStudio.Integration.Tests/Sample/  The sample host, started once, and the sample's seed, judge and policies without Docker
tests/KStudio.Integration.Tests/Shell/   Live Kafka and Postgres: first navigation creates no topic, commits no offset, writes no row, makes no broker call while prerendering
src/KStudio.Wolverine/                   KStudio.Wolverine (D43): UseWolverineKafka() and its options. It lists a Wolverine application's Kafka
                                         clusters and topics by reading its built configuration and nothing else. References KStudio.Core and
                                         WolverineFx.Kafka only (KStudioWolverineFenceTests); not packed (K8)
src/KStudio.Wolverine/Internal/          The cluster source KStudio.Core runs, the topic reader, the tenant probe (the only reflection) and the version guard
tests/KStudio.Wolverine.Tests/           The adapter's fast suite (D16): no Docker and no Wolverine host. Its output folder holds no studio
tests/KStudio.Wolverine.Tests/Conventions/ The IL scan of what the adapter may call, the fences and the version guard
tests/KStudio.Wolverine.Tests/Discovery/ What a WolverineOptions becomes: clusters, topics, tenants through the probe, and that nothing is written
tests/KStudio.Wolverine.Tests/Support/   The rig that builds a WolverineOptions and runs the adapter over it
tests/KStudio.Wolverine.Tests/Verify/    KStudio.Wolverine's public API baseline
tests/KStudio.Integration.Tests/Wolverine/ A real Wolverine host on two brokers: the adapter lists its cluster and its tenant, and a scan reads the tenant's broker
```
