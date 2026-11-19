# Layout

Where each kind of code lives. The root `AGENTS.md` points here, and an area with rules of its own has an `AGENTS.md` in its directory (and a `CLAUDE.md` that imports it), which the root's table *Where the rules live* lists: read it before adding a file, and keep it in step with the
tree in the same pull request.

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
  Build.Publish.cs                       Pack (the seven packages, one version) + the in-build nuget.org OIDC token exchange (D15)
  Build.Marten.cs                        --marten-version: the floor, the latest-tested Marten or an exact one (D3)
  Build.Npgsql.cs                        --npgsql-version and NpgsqlPass, the Postgres module's latest-Npgsql pass (D30)
  Build.Confluent.cs                     --confluent-version: the Kafka module's floor, latest-tested Confluent.Kafka or an exact one (D36)
build/Extensions/                        Vendored Cake helpers (StringExtensions) - do not edit
src/                                     The shipped libraries, and nothing else: the shell, the three modules and the engines
src/DataStudio.Postgres.Diagnostics/          The diagnostics engine, with no ASP.NET Core: the health sweep and its
                                         checks, the facts readers, the sources, the settings seam and IPostgresDiagnostics.
                                         References no Marten, no Marten module and no web stack (PostgresFenceTests,
                                         CoreFenceTests); its namespace is DataStudio.Postgres, shared with the Postgres module, which reaches its
                                         internals through InternalsVisibleTo
src/DataStudio/                          The shell: the chrome of the studio as a Razor Class Library of its own, in the DataStudio.*
                                         namespaces. It references ASP.NET Core and nothing else - no store, database, tenant, SQL, health, Npgsql,
                                         Marten or Kafka client (ShellFenceTests holds the reference graph) - and every module references it with an
                                         exact pin. Its _Imports.razor names only DataStudio, Microsoft and System
src/DataStudio/Components/               The shared document every root renders, the routes component, the route table and the links, and
                                         StudioDisplayComponent, the base of every component that spells a time (ui.md, Time zone)
src/DataStudio/Components/Json/          The JSON toolkit: viewer, raw view, editor, copy menu, diffs
src/DataStudio/Components/Layout/        The layout, the rail, the capability chip, the preferences popover, the copy-link button, the
                                         back-to-application link and the environment badge
src/DataStudio/Components/Pages/         Activity: the Activity ring as a page
src/DataStudio/Components/Shared/        The building blocks every module draws with: TableScroll (D25), CopyButton, the dialogs, menus, the
                                         empty and error states, pagers, ToastHost, ShortcutSheet
src/DataStudio/Internal/                 The endpoint mapper, its convention builder, the startup authorization guard, the endpoint marker and
                                         mapped-endpoint tracking (D11)
src/DataStudio/Services/                 Preferences, the Activity ring and its audit seam, toasts, the shortcut list, the live-update loop and its log
                                         words (StudioLiveLog), the capability denial reason
src/DataStudio/Services/Json/            Flat model, path expressions, pretty printer, canonicalizer, round-trip differ, C# record generator
src/DataStudio/Services/Settings/        The module seam: shell settings and descriptor, the module and its rail, route and record links, the
                                         navigation records, the registration marker
src/DataStudio/wwwroot/                  static web assets, packed under `_content/DataStudio/`
src/DataStudio/wwwroot/css/              the one stylesheet, studio.css
src/DataStudio/wwwroot/js/               the studio's browser helpers, studio.js, loaded by the shell (never a
                                         Blazor JS initializer - see StudioDocument.razor)
src/DataStudio.Postgres/                      The Postgres module's Razor Class Library: routes, scope,
                                         authorization, audit, the database browser, Sessions, the Health page, the SQL
                                         console and Relationships. Built on the shell and the diagnostics
                                         engine; references no Marten and no Marten module (PostgresFenceTests, CoreFenceTests)
src/DataStudio.Postgres/Components/           the root component and the scope query (the shell's components are in src/DataStudio/Components/)
src/DataStudio.Postgres/Components/Shared/    small building-block components that name a database type: the SQL views and disclosure, chips, the
                                         refusal and not-authorized notices (the neutral ones are in src/DataStudio/Components/Shared/)
src/DataStudio.Postgres/Internal/             the diagnostics and shell version guard (the endpoint mapper and its guard are in src/DataStudio/Internal/)
src/DataStudio.Postgres.Diagnostics/Internal/  Nothing but the engine's SQL builders, in Internal/Sql/ (hard rule 4)
src/DataStudio.Postgres.Diagnostics/Services/  The scope types, the log events every database module shares (StudioLog, one template each), the log throttle and the
                                         capping of a visitor's value in a log line
src/DataStudio.Postgres/Services/             state, authorization, the capability guard, the action log and toasts

src/DataStudio.Marten/                        The Marten Razor Class Library: public API, the store-facing services,
                                         pages and checks, and Internal/; references the Postgres module
src/DataStudio.Marten/Components/             The component bridges
src/DataStudio.Marten/Internal/               the duplicated-field enumeration, and the Marten SQL builders below
src/DataStudio.Marten/Services/               store registry, scope resolver, tenant discovery, the source provider,
                                         descriptor, settings and links, and the bridges onto the shared seams
samples/                                 Demo hosts and their demo data; never packed
samples/DataStudio.Sample.Marten/             The sample ASP.NET Core host (D18) - README snippet regions
samples/DataStudio.Sample.Marten.Domain/       The demo domain, shared with the integration tests; deliberately
                                         does NOT reference DataStudio.Marten (D17)
samples/DataStudio.Sample.Postgres.Data/       The demo data with no Marten in it, shared by both samples and the integration
                                         tests: Quartz.NET's job store (vendored, Apache-2.0), the "legacy" catalog
                                         edge-case schema, studio_sample.app_settings, and the tenant databases with
                                         their directory - all idempotent. References no studio (D17)
samples/DataStudio.Sample.Kafka/                  The Kafka sample: the Marten and Kafka modules over throwaway Postgres and Kafka (D18)
samples/DataStudio.Sample.Kafka/Kafka/            Its Kafka container, seeder and judge
samples/DataStudio.Sample.Kafka/Auth/             Its cookie sign-in, copied from the Marten sample
samples/DataStudio.Sample.Kafka/Wolverine/        Its Wolverine application: the checkout handler, document and startup sender (D-CORR decision 11)
samples/DataStudio.Sample.Kafka/Properties/       Its launch profile, port 5212
samples/DataStudio.Sample.Postgres/           The Postgres sample host (D18), no Marten: an admin database and three
                                         tenant databases through its own IPostgresDatabaseSource - README snippet regions
samples/DataStudio.Sample.Postgres/Auth/      Cookie auth, the three demo users, the studio policy and the per-capability one
samples/DataStudio.Sample.Postgres/Properties/  launchSettings.json, fixed at http://localhost:5211
samples/DataStudio.Sample.Postgres.Worker/           The diagnostics worker: a generic host that references
                                         src/DataStudio.Postgres.Diagnostics alone - no ASP.NET Core, no studio - and logs a
                                         summary of every database's findings at start and every Summary:Interval (D18)
tests/                                   All eight test projects match *.Tests, so one Test run covers them all
tests/DataStudio.Marten.Tests/                Fast suite: unit, bUnit, endpoint metadata, API snapshot,
                                         conventions. No Docker
tests/DataStudio.Marten.Tests/Conventions/    Rules too easy to break silently to leave to review
tests/DataStudio.Marten.Tests/Marten/         MartenApiSurfaceTest - the Marten 9.35 contract, pinned
tests/DataStudio.Marten.Integration.Tests/    Testcontainers Postgres + Playwright. Needs Docker
src/DataStudio.Postgres.Diagnostics/Services/Postgres/  The engine's plain-Postgres half: the source registry and provider over the application's
                                         NpgsqlDataSources and host database sources, the diagnostics settings and descriptor, and the
                                         seams' implementations (declarations, tables, remedies, locks, statements' words), and
                                         AddPostgresDiagnostics's seam registration
src/DataStudio.Postgres/Services/Postgres/    The Postgres module's own: its settings and descriptor, the links out of the browser
                                         and the Overview's server read
tests/DataStudio.Postgres.Tests/              The Postgres module's fast suite: no Docker, and no Marten anywhere in its graph
tests/DataStudio.Tests/                       The shell's fast suite: no Docker, no database. It references the DataStudio project and the one module that needs none, DataStudio.Kafka
tests/DataStudio.Tests/Conventions/           ShellFenceTests: the shell's project file, restore graph and built references hold no driver, Marten or Kafka; this output folder holds none of those but the Kafka module
tests/DataStudio.Tests/Endpoints/             MapDataStudio and the startup guard, and a host with only the Kafka module (its output folder holds no Npgsql and no DataStudio.Postgres*)
tests/DataStudio.Tests/Registration/          AddDataStudio: the shell's options and validation, its registrations, its live-update log
tests/DataStudio.Tests/Modules/               The route table's module rules (D34), on pages of the test assembly
tests/DataStudio.Tests/Verify/                The shell's approved API baseline; never hand-edited (hard rule 7)
tests/DataStudio.Postgres.Tests/Components/   bUnit: PostgresComponentContext and the Postgres pages' smoke tests, the plan view
                                         and the console's Explain buttons
tests/DataStudio.Postgres.Tests/Components/Json/  The JSON viewer's parameters a host that is not a database module sets (the Kafka module): no download, no
                                         Postgres copy items, and a host's "Copy as search"
tests/DataStudio.Postgres.Tests/Conventions/  The assembly fence: no Marten family in the assembly, the restore or the output folder
tests/DataStudio.Postgres.Tests/Endpoints/    An API-only host with one NpgsqlDataSource; the two registration guards
tests/DataStudio.Postgres.Tests/Shell/        Golden files of the prerendered rail, header and anonymous banner of the Postgres pages (the shell's
                                         markup stays byte for byte as it is restructured)
tests/DataStudio.Postgres.Tests/Sources/      The source provider over DI descriptors and host database sources
tests/DataStudio.Postgres.Tests/Support/      The Postgres module's own test doubles (the shared ones are linked from DataStudio.Marten.Tests)
tests/DataStudio.Postgres.Tests/Verify/       The Postgres module's and the engine's approved API baselines; never hand-edited (hard rule 7)
tests/DataStudio.Postgres.Integration.Tests/  The Postgres module's live suite: Testcontainers Postgres under a reuse label of its own
                                         (never the Marten suite's container) + Playwright. Needs Docker; no Marten
tests/DataStudio.Postgres.Integration.Tests/Browser/  Playwright over samples/DataStudio.Sample.Postgres on real Kestrel: the
                                         scenarios, where menus open, the D19 gate, and the zero-config start
tests/DataStudio.Postgres.Integration.Tests/Database/  Live Postgres: first navigation writes nothing, the D27 gate and its
                                         audit, Sessions, the pool panel at both Npgsql majors, the sample's tenant databases
tests/DataStudio.Postgres.Integration.Tests/Health/  Live Postgres: the table checks fire and stay quiet over the
                                         whole-database table source, and the export over a host source past its budget
tests/DataStudio.Postgres.Integration.Tests/Query/  Live Postgres: every SQL console example runs, and the Explain buttons on
                                         14 and 18; the what-if on a hypopg server of its own (postgres:17.11 +
                                         postgresql-17-hypopg)
tests/DataStudio.Postgres.Integration.Tests/Sources/  Live Postgres: discovery, and isolation between a host source's
                                         databases through every cache
src/DataStudio.Postgres.Diagnostics/Internal/Sql/  The engine's builders, SqlIdentifier and SqlLexer: the first of the three places a Postgres
                                         identifier becomes SQL text (hard rule 4) - the health sweep's and the facts readers'
                                         statements, the catalog's, the read-only guard and session; the blocking chain's
                                         hand-over text (X3); partition bounds and column skew (X4)
src/DataStudio.Postgres/Internal/Sql/         The Postgres module's own builders - the second place (hard rule 4): the SQL console's
                                         plans and their settings (X1), the facts beside a plan, hypopg's calls (X2),
                                         the browser's rows, Relationships and the console's examples; the same namespace
                                         as the engine's, each assembly exempt in its own folder only
src/DataStudio.Marten/Internal/Sql/           The Marten builders - documents, events, the daemon, projections: the
                                         third (hard rule 4); Marten's own DDL for a what-if (X2); the dead-letter
                                         groups (M3)
src/DataStudio.Postgres/Components/Layout/    The database's part of the chrome: scope selector, find box, the circuit host and the scope gate
                                         (the layout, rail and chip are in src/DataStudio/Components/Layout/)
src/DataStudio.Postgres/Components/Pages/     The shared routed pages; each @page must be rooted at /postgres
src/DataStudio.Marten/Components/Pages/       The Marten routed pages; each @page must be rooted at /marten
samples/DataStudio.Sample.Marten.Domain/Documents/  The demo document types
samples/DataStudio.Sample.Marten/Auth/        Cookie auth, the three demo users, the two policies
samples/DataStudio.Sample.Marten/Properties/  launchSettings.json, fixed at http://localhost:5210
tests/DataStudio.Marten.Tests/Endpoints/      Authorization matrix, sub-path re-rooting, the startup guard
tests/DataStudio.Marten.Tests/Shell/          Golden files of the prerendered chrome of the Marten pages; ShellGolden (Support/) cuts and scrubs
                                         the fragments for all three suites
tests/DataStudio.Marten.Tests/Links/          MartenRouteLinks, the record search button and the pages' scope from a link - no database
tests/DataStudio.Marten.Tests/Services/       Registry, scope resolver, audit log, tenant discovery
tests/DataStudio.Marten.Tests/Components/     bUnit: StudioComponentContext, StudioMarkup and the page tests
tests/DataStudio.Marten.Tests/Support/        Test doubles shared by the tiers - never a mocking library
tests/DataStudio.Marten.Tests/Verify/         The approved API baseline; never hand-edited (hard rule 7)

src/DataStudio.Marten/Services/Query/         Search grammar, index advisor, the document list request, LINQ mode
src/DataStudio.Postgres.Diagnostics/Services/Query/  The scan of an EXPLAIN plan for the relations it reads, which the statement checks take
src/DataStudio.Postgres/Services/Query/       The SQL console service, export, saved queries, error text, index
                                         verdicts and search chips; the plan reader and the plan insights (X1); the
                                         what-if engine and its hypothetical indexes (X2)
tests/DataStudio.Marten.Tests/Json/           Unit and bUnit tests for the JSON toolkit
tests/DataStudio.Marten.Tests/Sql/            SQL builders, guard theory, grammar, advisor - no database
tests/DataStudio.Marten.Integration.Tests/Sql/ Live Postgres: read-only session, builders, catalog, estimator
src/DataStudio.Marten/Services/Documents/      Document read and write services and their DTOs; the newest-first orders and
                                         Marten's edit preview (M5)
tests/DataStudio.Marten.Tests/Documents/       Document service unit tests - no database
tests/DataStudio.Marten.Integration.Tests/Documents/ Live Postgres: the document write path end to end
src/DataStudio.Marten/Services/Tenants/        The Tenants overview's service (TenantOverviewService), its models and TenantBoundText:
                                         read-only, statistics only, activity-gated
src/DataStudio.Marten/Services/Links/          The links a module's page makes into Documents and Streams (D-CORR 7): MartenRouteLinks, by type
                                         name and id, with a store and a tenant and never a database; pure, reads StoreOptions only
src/DataStudio.Marten/Services/Find/           Find by id's Marten kinds: the document, stream and natural-key, and event-sequence
                                         contributors, each asking the data service of the page its hit opens, and the helper (FindScopes)
                                         that reads the event store's shape once per Find and decides the tenant restriction (FindTenants:
                                         under a store policy, the tenants the header lists, whatever discovery found)
src/DataStudio.Marten/Services/Shapes/         The shape check's core, `EventShapeCheckService` among it: sampler limits and cache, the round-trip classifier, the
                                         failure describer, the aggregator and the sentences; the documents' check service
src/DataStudio.Marten/Components/Shapes/       The shape check's panels and the findings table
tests/DataStudio.Marten.Tests/Shapes/          The shape check's classifier, sentences, queries, cache and panels - no database
src/DataStudio.Marten/Services/Projections/    Projection data service, daemon accessor, operation tracker; the lag history
                                         (M2); the progression-row classifier and the daemon topology (M47)
src/DataStudio.Postgres.Diagnostics/Services/Live/  The single-flight snapshot cache every reader shares (D10)
src/DataStudio.Postgres/Services/Live/         The database's rail, the nav indicators, the per-circuit since-marks and the live loop's log words
                                         (the per-page polling helper, D10, is in Shell/Services/)
src/DataStudio.Marten/Services/Live/           The tracker observer: the in-process daemon's latest shard state (D10)
src/DataStudio.Postgres.Diagnostics/Services/Settings/  The engine's settings seam, descriptor, words, mount path, registration marker, option validation and
                                         core registration (AddPostgresDiagnostics and every Add*Studio call it)
src/DataStudio.Postgres/Services/Settings/     The studio's settings seam (it extends the engine's), descriptor, links, registration
                                         marker, option validation and core registration (PS-1a; moved whole in PS-2)
src/DataStudio.Postgres.Diagnostics/Services/Sources/  The source seam - databases, the source provider, the declarations, the table and lock seams
src/DataStudio.Postgres/Services/Sources/      The shared resolver and the seams the Marten module implements for a UI (PS-1a-1d; moved
                                         whole in PS-2)
src/DataStudio.Marten/Components/Pages/Projections/  The projections screen and its parts
samples/DataStudio.Sample.Marten.Domain/Events/ The demo events and the three projections
tests/DataStudio.Marten.Tests/Projections/     Daemon accessor, snapshot cache, live updates, operation tracker, page
tests/DataStudio.Marten.Integration.Tests/Projections/  Live Postgres + a real Solo daemon
tests/DataStudio.Marten.Integration.Tests/Logging/  Live hosts with no daemon and an externally managed one: no Warning from polling
src/DataStudio.Marten/Services/Events/         Event data service, aggregate invoker, event DTOs and links; the stepper's
                                         fold (M1); the dead-letter grouping and fingerprint (M3)
src/DataStudio.Marten/Components/Pages/Events/ Streams, stream detail and its stepper, feed, event types, dead letters and
                                         their groups by cause
tests/DataStudio.Marten.Tests/Events/          Event builder cases, links, the page tests, the stepper's fold and the
                                         dead-letter groups - no database
tests/DataStudio.Marten.Integration.Tests/Events/  Live Postgres: the event reads and the dead-letter writes, the stepper
                                         against Marten's own fold, and the dead-letter groups over a corpus
src/DataStudio.Marten/Services/Schema/         Schema data service, index advice, migration risk and their DTOs
src/DataStudio.Postgres.Diagnostics/Services/Schema/  The DDL tokenizer, schema declarations, storage parameters, index redundancy
                                         and foreign-key coverage the table checks judge by
src/DataStudio.Marten/Services/Configuration/  The read-only StoreOptions describer and its DTOs
src/DataStudio.Marten/Components/Pages/Schema/ The schema screen's five tabs and their parts
src/DataStudio.Marten/Components/Pages/Configuration/  The configuration dump screen
tests/DataStudio.Marten.Tests/Schema/          Index advice, the tokenizer, the schema page - no database
tests/DataStudio.Marten.Tests/Configuration/   The describer and the configuration page - no database
tests/DataStudio.Marten.Integration.Tests/Schema/  Live Postgres: drift, migration preview, catalog reads
src/DataStudio.Marten/Services/Connection/     Database > Connection's Marten half: the contributor, the command-timeout
                                         resolution, the data-source origin and the section's rows
tests/DataStudio.Marten.Tests/Connection/      The Marten connection section, its hint notes, the redaction sentinel and the page - no database
tests/DataStudio.Marten.Integration.Tests/Connection/  Live Postgres: UseNpgsqlDataSource() versus Connection(string), and the hosted
                                         daemon's connections
src/DataStudio.Marten/Components/Shared/       The Marten module's building blocks: RecordSearchLinkButton (D-CORR 8), the module's search from a record screen
src/DataStudio.Marten/Components/Pages/Documents/  The documents browser: collections rail, list, detail
src/DataStudio.Marten/Components/Pages/Query/  The Marten query body: the mode switch and LINQ mode
src/DataStudio.Postgres/Components/Pages/Query/  The shared query parts: editor, result grid, stats, saved queries,
                                         examples, the SQL console panel and the plan view, and the What if? panel
tests/DataStudio.Marten.Tests/Query/            Composer, guard, export, saved queries, the page - no database
tests/DataStudio.Marten.Integration.Tests/Query/  Live Postgres: Marten where-clause mode and the SQL console; LINQ's and
                                         Documents' what-if on a hypopg server
samples/DataStudio.Sample.Marten.Domain/Generation/  The demo data generator: size presets, the generator,
                                         truncation, the word lists and the run marker
samples/DataStudio.Sample.Marten/Generation/   The demo-data panel: hosted job, endpoints, progress, daemon lag; and the
                                         "make it unhealthy" switches, their board, endpoints and panel
tests/DataStudio.Marten.Integration.Tests/Generation/  Responsiveness budgets at 100k rows, and at 1.2M opt-in; the
                                         unhealthy switches' gates, markup, checks and clean-up
docs/                                    Long-form documentation linked from README.md; never packed
docs/screenshots/                        The fourteen README screenshots (ScreenshotCaptureTests retakes them)
docs/maintainers/                        Maintainer reference moved out of AGENTS.md: this tree, the dated package
                                         budget entries (package-budget-changes.md), the evidence and history
                                         behind the rules (decision-notes.md) and the full texts of the rules
                                         and decisions AGENTS.md summarises, by area: hard-rules.md,
                                         architecture.md, sql-and-catalog.md, marten.md, kafka.md,
                                         authorization.md, health-engine.md, ui.md, plan-analyzer.md,
                                         testing.md and build-and-release.md, and v2-public-api.md, the
                                         record of the 2.0 renames; never packed
src/DataStudio.Postgres/Services/Relationships/    The relationship graph service, its DTOs and the SVG layout
src/DataStudio.Marten/Services/Relationships/      The Marten half of the graph: what StoreOptions says of the document types
src/DataStudio.Postgres/Components/Pages/Relationships/  The relationships screen: the diagram, the table, the legend
tests/DataStudio.Marten.Tests/Relationships/       Layout, graph builder, SQL shape, page and panel - no database
tests/DataStudio.Marten.Integration.Tests/Relationships/  Live Postgres: drift both ways, visibility, bounded counts
tests/DataStudio.Marten.Integration.Tests/Browser/  Playwright over the sample host on real Kestrel: thirteen scenarios, the D19 gate, the large-data passes
src/DataStudio.Postgres.Diagnostics/Services/Database/  What the engine reads of a database: the schema matcher, the ownership classifier, the lock graph (X3),
                                         the sessions' reader, models and pool observer, and the statement screen
src/DataStudio.Postgres/Services/Database/     The database browser: the per-visitor gate, the catalog cache and the object
                                         service; Sessions' per-visitor service; the visitor's links
src/DataStudio.Postgres/Components/Pages/Database/  The database browser's screens: the rail, the kind tabs and grids,
                                         object detail and its tabs, the Rows and Relationships slots; Sessions and
                                         its Blocking tab (X3)
tests/DataStudio.Marten.Tests/Database/        Gate, matcher, classifier, catalog SQL shape, options - no database
tests/DataStudio.Marten.Integration.Tests/Database/  Live Postgres: catalog reads, the gate and its audit, no DDL,
                                         and the relational demo schemas' idempotency and demo-data flows
src/DataStudio.Postgres.Diagnostics/Services/Health/  The health advisor's engine: check model and registry, sweeper, sweep cache, history, the
                                         gate every reader applies, links and platform
src/DataStudio.Postgres/Services/Health/       The health advisor's visitor half: the per-visitor report and gates, mutes and toasts,
                                         and the finding actions' seam and runner
src/DataStudio.Postgres.Diagnostics/Services/Health/Checks/  One file per shared health check (attribute, gatherer, pure evaluator); a
                                         check is added by adding its file here, nothing else
src/DataStudio.Postgres/Services/Health/Checks/  The three checks that judge the studio's own exposure (K05-K07): they read the
                                         studio's settings and mapping, so they are run only where a studio is registered
src/DataStudio.Marten/Services/Health/         What the Marten module's checks read of the store (MartenHealthContext), and E06's
                                         on-demand action
src/DataStudio.Marten/Services/Health/Checks/  One file per Marten-only health check - the store, the event store, the
                                         daemon - run against Marten stores only; added the same way
src/DataStudio.Postgres.Diagnostics/Services/Health/Export/  The opt-in ASP.NET Core health check: one run per interval over every
                                         store and database, summarised as codes and counts, never names
src/DataStudio.Postgres/Components/Pages/Health/ The Health page and its presentation helpers
tests/DataStudio.Marten.Tests/Health/          Evaluators, sweeper, cache, history, gates, page, badge, toast, the
                                         check and docs conventions - no database
tests/DataStudio.Marten.Integration.Tests/Health/  Live Postgres: each check fires and stays quiet, the no-DDL /
                                         no-lock sweep helper, and the fires/quiet attribute convention
docs/health/                             One file per health area, one "## <code> <title>" anchor per check; plans.md for
                                         the plan insights (P)
tests/DataStudio.Marten.Integration.Tests/Tenancy/  Live Postgres: first navigation per tenancy shape changes nothing,
                                         and the enumerations that still apply a lookup table
src/DataStudio.Postgres.Diagnostics/Services/Server/  The server facts every W5 packet uses: the PostgreSQL version gates
                                         (PostgresFeatures), the reading role's grants and the "not checked"
                                         sentences (PostgresGrants), and server-setting remedies
src/DataStudio.Postgres/Services/Server/       The Server page's per-visitor service
tests/DataStudio.Postgres.Tests/Server/        The gate table, the grant sentence per platform and grant, the remedy kinds
tests/DataStudio.Postgres.Integration.Tests/Versions/  The version matrix (PostgreSQL 15-19, 19 on its RC),
                                         the Aurora shim, MatrixReads and VersionMatrixLiveTests: one never-reused
                                         server per major, started on first use (an assembly fixture)
src/DataStudio.Postgres.Diagnostics/Services/Maintenance/  The Maintenance page's facts (the regular sweep's own reads) and the page's route
src/DataStudio.Postgres/Services/Maintenance/  The Maintenance page's per-visitor view, and the on-demand bloat measure
src/DataStudio.Postgres/Components/Pages/Maintenance/  The Maintenance page: autovacuum's triggers per table, running
                                         maintenance, the workers, the freeze forecast and the vacuum horizon
tests/DataStudio.Postgres.Integration.Tests/Maintenance/  Live Postgres on autovacuum servers of their own: T07 against
                                         each table's triggers, T08, T16, T24, S02, the bloat measure, C06's VACUUM
                                         at the head and T14's standby through a slot
src/DataStudio.Postgres.Diagnostics/Services/Storage/  The Storage page's facts (sizes from relpages, growth over the statistics window, the largest
                                         tables) and the words for a growth not yet known (W5-CAP)
src/DataStudio.Postgres/Services/Storage/      The Storage page's per-visitor view
src/DataStudio.Postgres/Components/Pages/Storage/  The Storage page: the tiles and the largest tables
tests/DataStudio.Postgres.Tests/Storage/       The statistics window over hand-built histories, the growth arithmetic, the
                                         Storage builder's D27 fence - no database
tests/DataStudio.Marten.Tests/Storage/         bUnit: the Storage page and its per-visitor view in the Marten module's words
tests/DataStudio.Postgres.Integration.Tests/Storage/  Live Postgres on 15-19: the Storage facts, behind a held lock, no DDL,
                                         and the statistics window across a real crash
src/DataStudio.Postgres.Diagnostics/Services/Replication/  The Replication page's facts (recovery, standbys, slot statistics, the archiver,
                                         WAL, checkpoints, pg_stat_io, tablespaces), A2's not-applicable rules and the
                                         advisor's counter windows
src/DataStudio.Postgres/Services/Replication/  The Replication page's per-visitor service
src/DataStudio.Postgres/Components/Pages/Replication/  Operations › Replication: slots, standbys, the archiver, WAL,
                                         checkpoints, I/O by backend type and tablespaces
tests/DataStudio.Postgres.Tests/Replication/   R02-R04's evaluators at every edge, WAL file names, the facts' version shapes,
                                         the per-visitor view and the page
tests/DataStudio.Postgres.Integration.Tests/Replication/  Live primary and standby pairs (17 and 18): R02-R04 fire and stay
                                         quiet, the page's facts, and Aurora's not-applicable sections
src/DataStudio.Postgres/Components/Pages/Server/  System › Server: the server's facts, what the studio can see, the curated
                                         settings, the extension chips, the security posture and the platform slot
tests/DataStudio.Postgres.Integration.Tests/Server/  Live S04-S10 and K05-K07: each fires and stays quiet, the server facts on
                                         15-19 as app and probe, and their no-lock, no-DDL reads; also the connection
                                         live tests (ConnectionObservedLiveTests, ConnectionSettingChecksLiveTests,
                                         ConnectionTlsLiveTests, ConnectionCompareLiveTests, ConnectionPageLiveTests)
src/DataStudio.Postgres.Diagnostics/Services/Aurora/  Aurora (W5-AUR, issue #8 A1-A8): its own reads by name, the replica status, the wait
                                         profile and its samples, the parameter reference and the host's capacity reader
src/DataStudio.Postgres/Services/Aurora/       The Server page Aurora section's per-visitor view
src/DataStudio.Postgres/Services/Connection/   Database > Connection's per-visitor half: the service and its two gates, the page
                                         builder, the facts source (the sweep's shared read, or one per HealthInterval), the links and
                                         the internal IConnectionInsightContributor seam
tests/DataStudio.Postgres.Tests/Aurora/        By-name mapping of Aurora's functions, the wait windows, commit latency, the capacity
                                         reader, the parameter wording and R03's aurora-reader view - no database
tests/DataStudio.Postgres.Integration.Tests/Aurora/  Live on VERS's Aurora shim: the instance, R03 aurora-reader, the wait section, a
                                         failover, the not-applicable checks, C03's remedy and the spoof-proof markers
src/DataStudio.Postgres.Diagnostics/Services/Statements/  The Statements page's facts (the statement sweep's own read and history), the plain classifier,
                                         the page's route and the words for pg_stat_statements' state (W5-Q1)
src/DataStudio.Postgres/Services/Find/         Find by id: the typed value's parser (FindQuery), the seam (IFindContributor), the
                                         circuit's service (FindService: one Find at a time, a 10 s budget) and the Postgres module's own
                                         kinds, tables and views and statement queryids
src/DataStudio.Postgres/Services/Statements/   The Statements page's per-visitor view behind the activity gate, and the drill-in's planner
src/DataStudio.Postgres/Components/Pages/Statements/  Operations › Statements: the top statements by total time, and by group,
                                         and one statement's drill-in
tests/DataStudio.Postgres.Tests/Statements/    The window and share arithmetic, the view's ranking and groups, the gate, the
                                         classifier, the cost meter's connection tracker, and the page; the plan reader
                                         and insights over ExplainFixtures.json (PROBE's 108 plans), and the drill-in
                                         - no database
tests/DataStudio.Postgres.Tests/Statements/PlanCorpus/  The plan analyzer's corpus (X3, issue #28): one EXPLAIN (FORMAT JSON) fixture
                                         for each of P09 to P20 firing and quiet, and the shapes the views are tested on
tests/DataStudio.Marten.Tests/Statements/      The Marten module's classifier over Marten's own recorded SQL, and its metered connections
tests/DataStudio.Postgres.Integration.Tests/Statements/  Live pg_stat_statements on a server of its own, 14 and 18 through the
                                         matrix, and the Aurora shim's storage reads in the 15/16 and 17 shapes, and
                                         the drill-in's plan on 14, 16 and 18
tests/DataStudio.Postgres.Tests/Tables/        T15, T19, T20, T25 and T26 at their edges, the usage guards, foreign-key coverage,
                                         sequence headroom and the lint statements' D27 shape - no database
src/DataStudio.Postgres.Diagnostics/Diagnostics/  The public diagnostics read model (D31): IPostgresDiagnostics, its request, snapshot,
                                         findings, checks and sections - namespace DataStudio.Postgres, like the registration types
src/DataStudio.Postgres.Diagnostics/Services/Diagnostics/  The API's service, host gate, mapper, snapshot builder, read limiter and JSON writer (W5-API)
src/DataStudio.Postgres/Services/Diagnostics/  The Health page's visitor download builder, its Markdown report and the snapshot
                                         compare, which never reach the API (W5-API)
tests/DataStudio.Postgres.Tests/Diagnostics/   The mapper over every enum member, the host gate's sentinels, the JSON baselines, the
                                         limiter, the sections from hand-built facts, the visitor download and the Snapshot
                                         menu - no database
tests/DataStudio.Postgres.Integration.Tests/Diagnostics/  Live: G9 by application as app and probe, a full snapshot that
                                         creates nothing, three databases behind a database source, an API-only host, and
                                         the statements section on a pg_stat_statements server
tests/DataStudio.Marten.Integration.Tests/Diagnostics/  Live: the API over a conjoined store (a tenant's finding needs
                                         IncludeActivity) and a master-table tenancy that lists nothing
src/DataStudio.Postgres.Diagnostics/Services/ConnectionHistory/  The opt-in connection history: the sampler and its
                                         kept connection per server, the in-memory store and the Holders page's view of a history;
                                         the public options, store seam and sample are at the project's root, beside the registration
tests/DataStudio.Postgres.Tests/ConnectionHistory/  A sample from a holders reading, the server a connection string names, the in-memory
                                         store's window, the page's view and the registration - no database
src/DataStudio.Postgres.Diagnostics/Services/Connection/  The connection-insights engine: the catalogue of Npgsql keywords, the
                                         reader that redacts a data source's connection string, the pooler signs, the hints and the
                                         facts the connection checks read (what the server saw of the studio's connection, the
                                         platform, the pools that share the data source)
tests/DataStudio.Postgres.Tests/Connection/  The catalogue against the loaded Npgsql, the reader's provenance and redaction, every hint
                                         each way, the pooler signs and the sentinel test - no database
src/DataStudio.Kafka.Core/                        The Kafka engine (D36): catalog, window resolution, scan engine, predicates,
                                         decoders, governor and redaction, over Confluent.Kafka behind the internal wire
                                         seam. No ASP.NET Core, no shell, no Npgsql (KafkaCoreFenceTests)
src/DataStudio.Kafka/                             The Kafka module's Razor Class Library (D34, D35): AddKafka(), its options, capabilities
                                         and resource; no Marten (hard rule 16), no static web assets
src/DataStudio.Kafka/Components/                  The module's components, under _Imports.razor
src/DataStudio.Kafka/Components/Layout/           The cluster selector the shell draws in its header on Kafka pages
src/DataStudio.Kafka/Components/Pages/            The Kafka routed pages; each @page must be rooted at /kafka
src/DataStudio.Kafka/Components/Shared/           The page frame (cluster gate, trademark line) and the disabled-capability notice
src/DataStudio.Kafka/Internal/                    The version guard and the startup guard
src/DataStudio.Kafka/Services/                    The module, the cluster scope, authorization, the capability guard, the action log and its gate,
                                         the log events
tests/DataStudio.Kafka.Tests/                     The Kafka module's fast suite, a Kafka-only host: the engine against the hand-written fake broker, the fences
                                         and the public API baseline. No Docker
src/DataStudio.Kafka.Core/Wire/                   The wire seam (D36): IKafkaWire, the reader, the error values and the neutral records; no
                                         Confluent type crosses it
src/DataStudio.Kafka.Core/Wire/Confluent/         The one implementation over Confluent.Kafka: the overrides (D37), the role filter for host
                                         configuration, the error table, the admin pool, the wire and the scan reader
src/DataStudio.Kafka.Core/Clusters/               The registered clusters: keys and names, the cloned host configuration split by role, the
                                         option bounds and the container registration
src/DataStudio.Kafka.Core/Catalog/                The catalog and overview reads: the cached reads, the ACL table and its sentences, the
                                         overview records and the throughput samples
src/DataStudio.Kafka.Core/Windows/                Window resolution (D38): a time window to offsets per partition, and the notes that say what a
                                         "no match" could be missing
src/DataStudio.Kafka.Core/Logging/                The engine's log events 9650 to 9699, the bounded log values and the log-once helper
src/DataStudio.Kafka.Core/Predicates/             The search grammar (D39): lexer, parser, canonical text, the compiler per topic and the
                                         three-valued evaluator, with the verdict of every clause
src/DataStudio.Kafka.Core/Decoding/               Value and header decoding (D39): the detection order, CloudEvents, the Confluent wire format,
                                         trace contexts, the decode counters and the per-record evaluation context
src/DataStudio.Kafka.Core/Scanning/               The scan engine (D38, D41): plan and estimate, the scan's thread, the governor, the limiter and
                                         the owner budget, batches, the match store and the one factory that makes a match; the key
                                         set and the partitioner verification that let a key search read fewer partitions (D39)
src/DataStudio.Kafka.Core/Redaction/              Redaction inside the engine (D40): the built-in header deny-list, the topic's rules and the
                                         redactor every match is made through
src/DataStudio.Kafka.Core/Correlation/            Correlation (D42): a topic's declared fields and their kinds, the extractor that reads an
                                         anchor's literal fields, the per-topic query builder and the duplicate finder
src/DataStudio.Kafka.Core/Export/                 The export writer (D45): rows from redacted matches, JSON Lines, CSV and one value, and the file
                                         name; pure
src/DataStudio.Kafka.Core/Index/                  The record-index seam (D48): the host's interface, its query and hints, the narrower and the
                                         planner that asks it
tests/DataStudio.Kafka.Tests/Conventions/         The Kafka fences and convention scans
tests/DataStudio.Kafka.Tests/Registration/        AddKafka, the version guard, the startup guard
tests/DataStudio.Kafka.Tests/Endpoints/           Mounting, endpoint authorization and the shell's front page, on a Kafka-only host and beside a test-defined module
tests/DataStudio.Kafka.Tests/Shell/               Golden files of the prerendered chrome of a Kafka-only host (no database module is in this suite's output)
tests/DataStudio.Kafka.Tests/Components/          bUnit: the layout on a Kafka page, the rail, the chip, the frame, Activity
tests/DataStudio.Kafka.Tests/Services/            Authorization, the capability guard, the cluster scope, the audit gate and action log
tests/DataStudio.Postgres.Tests/Modules/      The module seam (D34): the composed descriptor, a module's page roots and its audit entries, with fake modules
tests/DataStudio.Marten.Tests/Settings/       The module seam over the Marten module's descriptor, with a fake module
tests/DataStudio.Kafka.Tests/Verify/              The public API baselines of DataStudio.Kafka and DataStudio.Kafka.Core, and the words baseline
tests/DataStudio.Kafka.Tests/Support/             Test helpers the fast suite shares
tests/DataStudio.Kafka.Tests/Support/FakeKafka/   The hand-written fake broker (D36): topics as logs, transactions, configs, groups and an ACL
                                         table, with a wire and a scan reader over it
tests/DataStudio.Kafka.Tests/Contract/            WireContract and WindowContract, run on the fake here and on real brokers by the live suite, and
                                         the harness they run over
tests/DataStudio.Kafka.Tests/Wire/                The overrides, the role filter, the error table, the forbidden-call IL scan and the reader
tests/DataStudio.Kafka.Tests/Clusters/            Registration, key formats and the admin pool
tests/DataStudio.Kafka.Tests/Catalog/             The overview reads, the ACL table, the snapshot cache and the throughput samples
tests/DataStudio.Kafka.Tests/Windows/             Window resolution on the fake
tests/DataStudio.Kafka.Tests/Logging/             The engine's log events (the parity of its log values with the diagnostics engine's is the live project's Composition)
tests/DataStudio.Kafka.Tests/Predicates/          The grammar, its messages and limits, the canonical text and the evaluation tables
tests/DataStudio.Kafka.Tests/Decoding/            The detection order, the caps, headers, trace contexts and CloudEvents
tests/DataStudio.Kafka.Tests/Redaction/           The deny-list, the topic's rules, the match caps and the one place a match is made
tests/DataStudio.Kafka.Tests/Correlation/         The declarations and their bounds, the extractor, the per-topic query and the duplicate finder
tests/DataStudio.Kafka.Tests/Scanning/            The scan engine on the fake: every stop reason and end state, the cursor, the governor, the
                                         limits, the estimate, batching and the lifecycle
tests/DataStudio.Kafka.Tests/Export/              Export writer, redaction and file name tests (D45)
tests/DataStudio.Kafka.Tests/Index/               Record-index narrowing, clause choice and planning tests (D48)
tests/DataStudio.Kafka.Tests/Partitioning/        The partitioners' known answers, the key set, the verification by sampling and the pruned scan
tests/DataStudio.Kafka.Integration.Tests/         The Kafka module's live suite: apache/kafka 3.9 and 4.x with SASL and ACLs, through
                                         Testcontainers.Kafka; needs Docker
tests/DataStudio.Kafka.Integration.Tests/Support/  The broker fixtures, one per image, the live contract cluster and the harness tests
tests/DataStudio.Kafka.Integration.Tests/Wire/    WireContract on real brokers
tests/DataStudio.Kafka.Integration.Tests/Windows/  WindowContract on real brokers
tests/DataStudio.Kafka.Integration.Tests/Catalog/  The catalog as principals with different ACLs, and metadata that creates nothing
tests/DataStudio.Kafka.Integration.Tests/Scanning/  The scan engine on real records: stop reasons, transactions, retention before and during a scan, the
                                         governor, the memory ceiling and that a scan writes nothing
tests/DataStudio.Kafka.Integration.Tests/Partitioning/ Each partitioner against a real producer's on two partition counts, and a pruned scan on real brokers
tests/DataStudio.Kafka.Integration.Tests/Redaction/ Payload redaction on real brokers: a scan, a record read, the logs and the exact match, with no masked value in any sink
tests/DataStudio.Kafka.Integration.Tests/Search/  Find the message with this id (the M1 exit), as restricted principals, and the search service against live brokers
tests/DataStudio.Kafka.Integration.Tests/Correlation/ Related records on real brokers (the M4 exit): an in and an out topic found by their own fields in one scan, and a topic the principal may not describe
tests/DataStudio.Kafka.Integration.Tests/Composition/ The Kafka module beside a database module, over the dependency-injection container and no broker: registration order, the banner, the rail, the
                                                       audit gates and the log-value parity with the diagnostics engine, which the Kafka-only fast suite cannot hold
tests/DataStudio.Kafka.Integration.Tests/Sample/  The sample host, started once, and the sample's seed, judge and policies without Docker
tests/DataStudio.Kafka.Integration.Tests/Shell/   Live Kafka and Postgres: first navigation creates no topic, commits no offset, writes no row, makes no broker call while prerendering
src/DataStudio.Kafka.Wolverine/                   DataStudio.Kafka.Wolverine (D43): UseWolverineKafka() and its options. It lists a Wolverine application's Kafka
                                         clusters and topics by reading its built configuration and nothing else. References DataStudio.Kafka.Core and
                                         WolverineFx.Kafka only (KafkaWolverineFenceTests)
src/DataStudio.Kafka.Wolverine/Internal/          The cluster source DataStudio.Kafka.Core runs, the topic reader, the tenant probe (the only reflection) and the version guard
tests/DataStudio.Kafka.Wolverine.Tests/           The adapter's fast suite (D16): no Docker and no Wolverine host. Its output folder holds no studio
tests/DataStudio.Kafka.Wolverine.Tests/Conventions/ The IL scan of what the adapter may call, the fences and the version guard
tests/DataStudio.Kafka.Wolverine.Tests/Discovery/ What a WolverineOptions becomes: clusters, topics, tenants through the probe, and that nothing is written
tests/DataStudio.Kafka.Wolverine.Tests/Support/   The rig that builds a WolverineOptions and runs the adapter over it
tests/DataStudio.Kafka.Wolverine.Tests/Verify/    DataStudio.Kafka.Wolverine's public API baseline
tests/DataStudio.Kafka.Integration.Tests/Wolverine/ A real Wolverine host on two brokers: the adapter lists its cluster and its tenant, and a scan reads the tenant's broker
```
