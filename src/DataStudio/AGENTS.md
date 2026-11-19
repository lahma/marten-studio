# AGENTS.md: the shell, and the UI of every module

The [root AGENTS.md](../../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the shell, and the UI of every module. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** UI, CSS, JS, Blazor components and circuits, in any module: [ui.md](../../docs/maintainers/ui.md) (D22-D26, the time zone, dialogs); hard rule 6 (no `async void`) is in the root file and the interop-loss catch of hard rule 15 in [the Marten file](../../src/DataStudio.Marten/AGENTS.md); the shell and the modules, routes, assets, words and logs: [architecture.md](../../docs/maintainers/architecture.md) (D2, D10, D11, D30, D49); the module seam and links between modules: [kafka.md](../../docs/maintainers/kafka.md#d34) (D34, D44); [hard-rules.md](../../docs/maintainers/hard-rules.md) rules 3, 6, 15 and 16.

## Hard rules

3. **No MudBlazor, and no third-party UI, CSS or icon library, ever.** Styling is hand-rolled, prefixed `ms-`, and
   driven by the design tokens in `src/DataStudio/wwwroot/css/studio.css`. There is still exactly one stylesheet,
   and it holds every module's sections (D26). Full text: hard rule 3.

## Design decisions

**D2 — Interactive Server render mode only, and `IsTrimmable=false` said out loud.** Blazor Server is what lets the
studio run against the host's in-process `IDocumentStore`; WebAssembly would need an API surface that is itself the
security problem. `IsTrimmable=false` is stated in every shipped csproj, because components are bound by name and the document
viewer deserializes into types found at run time. Full text: [architecture.md](../../docs/maintainers/architecture.md#d2).

**D10 — No SignalR hub.** Pages poll with a `PeriodicTimer` at `DataStudioOptions.RefreshInterval`, paused on hidden tabs, through a
single-flight snapshot cache so that N circuits cost one query per interval; there is no SignalR hub. Where the daemon
is hosted in this process, a lazy observer on `ShardStateTracker` gives sub-second projection state. Full text:
[architecture.md](../../docs/maintainers/architecture.md#d10).

**D11 — Sub-path mounting is ported from Quartz.Dashboard, and the shell does it for every module.** Compile-time routes are re-rooted at
map time, from each assembly's own root (`/postgres`, `/marten`, `/kafka`), so a module's pages are served at `{mount}/{segment}/…` and
the shell's own (the front page, `{mount}/activity`) at the mount; and `/_blazor`, `blazor.web.js`, `opaque-redirect` and
`_content/DataStudio` are mirrored under the mount path (`StudioEndpointMapper`). Do not rewrite it; there is no
"integrate into the host's own Blazor app" mode. Full text: [architecture.md](../../docs/maintainers/architecture.md#d11).

**D22 — `blazor.web.js` reaches the host through a `PackageReference`, not through a copy in this package.** The assets
pack's `buildTransitive` targets make `_framework/blazor.web.js` the consuming project's own static web asset, and
`MapStudioFrameworkScript` tries the host web root, then the host endpoint, then a normally absent packaged copy: the
host's copy must always win. Full text: [ui.md](../../docs/maintainers/ui.md#d22).

**D23 — The studio has no forms, so it disables antiforgery on its own endpoints.** There is no `<form>` or `<EditForm>`
in `Components/`, so the component endpoints say `.DisableAntiforgery()` and a host that never calls
`app.UseAntiforgery()` still serves them. A screen that grows a real form brings the token back, scoped to that
endpoint. Full text: [ui.md](../../docs/maintainers/ui.md#d23).

**D24 — The packages ship no Blazor JS initializer; the shell loads `js/studio.js` itself.** A file named
`*.lib.module.js` in a package's `wwwroot` would be loaded into every Blazor app in the process (issue #2), so the shell
document `StudioDocument.razor` loads the shell's classic `js/studio.js` itself, with a relative `src`.
`NoJsInitializerTests` holds both halves. Full text: [ui.md](../../docs/maintainers/ui.md#d24).

**D25 — Every data table sits in its own labelled scroll region, and the shell never scrolls sideways.** A `<table
class="ms-table">` is always inside `<TableScroll Label="…">` (or the documents list's own `.ms-table-wrap` carrying the
same three attributes); `TablesAreScrollableTests` fails on a bare table. Fix a too-wide column first: the region is the
floor, not the answer. Full text: [ui.md](../../docs/maintainers/ui.md#d25).

**D26 — The stylesheet's UX sections are appended in cascade order, and later wins on purpose.** Each packet's new rules
go in a titled section appended to `src/DataStudio/wwwroot/css/studio.css`, and a section that re-declares an earlier selector wins by source order
on purpose. Edit an existing rule in place only where the packet's report says so. Full text:
[ui.md](../../docs/maintainers/ui.md#d26).

**D34 — A module joins the shell: the Kafka module is one, and so are the database modules.** `AddKafka()`, `AddPostgres()` and
`AddMarten()` each add a module to the builder `AddDataStudio()` returned, and none maps anything: `MapDataStudio()` serves every
module's pages, so there is no second map call to forget authorization on, and a module cannot be added without a shell. A module is
an internal `IStudioModule` that the shell composes into its descriptor at first resolution, so what is refused does not depend on
registration order and a host with no module is refused at map time; **modules are shown in registration order** (D49). **There is
no root module**: every module's pages sit below a segment of its own under the mount (`{mount}/kafka/…`, `{mount}/marten/…`,
`{mount}/postgres/…`), a page assembly's root is one unique segment and never one the shell uses, and the shell's own
pages - the front page (one tile per module, or a redirect to its home when there is one) and Activity (`{mount}/activity`) -
are pages of no module, with no scope selector, no header search and no scope gate. Every section title and item label on the composed rail is
unique. On a module's page, known by the matched page's assembly and never by the address, the header draws that module's scope selector (and
the Find box only on a database module's); on the Kafka module's pages the layout neither gates the body on a database module's scope nor reads
or writes `?store=&db=&tenant=`, and that scope waits in memory; ids and scope travel in the query string (D9). A module's audit entries carry a
scope kind and are judged only by the module's own gate; its words, capabilities and trademark line are its own. Full text:
[kafka.md](../../docs/maintainers/kafka.md#d34).

**D44 — Links between modules are pure, internal and grant nothing.** `IStudioRouteLinks` (the Marten module implements it: a
document by type and id, a stream by id or key, in the record's tenant: a static tenancy from its
configuration, unless its rows are tenanted and it lists `KnownTenantIds`; a master-table, sharded or one-server tenancy only
when it keeps its rows per tenant and lists no `KnownTenantIds`, so a master-table or one-server tenancy with single-tenanted
rows gets no link) and `IStudioRecordSearchLinks` (the Kafka module implements it: "Find in Kafka") live in the shell
beside the module seam, so neither module references the other (`IStudioLinks`, the database module's own, is in the Postgres module). A masked value never enters a URL, a value travels
in the fragment, and the destination authorizes again. External links are host templates with no credential, opened with
`noreferrer`. Full text: [kafka.md](../../docs/maintainers/kafka.md#d44).
