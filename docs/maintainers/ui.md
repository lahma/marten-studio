# UI, CSS, JS and Blazor

This is the full text of D22 to D26, moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D22

**D22 — `blazor.web.js` reaches the host through a `PackageReference`, not through a copy in this
package.** On .NET 10 the file is only a static web asset of `Microsoft.AspNetCore.App.Internal.Assets`, resolved only
for a project whose `RequiresAspNetWebAssets` is true (by default, one with `.razor` Content items *of its own*), and
**the package cannot set the property on the consumer's behalf**: NuGet excludes package-provided `.props`/`.targets`
from restore evaluation (`ExcludeRestorePackageImports`). A plain `PackageReference` does reach the consumer: the
pack's `buildTransitive` targets run in any Web SDK executable that references the studio, directly or transitively,
and make `_framework/blazor.web.js` **that project's own** static web asset, served by its `MapStaticAssets()` and
forwarded to by the studio's `{Path}/_framework/blazor.web.js` endpoint. `MapStudioFrameworkScript` tries the host
web root, then the host endpoint, then a normally absent packaged copy
(`_content/PostgresStudio/_framework/blazor.web.js`), as the host's copy must always win. Since 1.2 the same
studio-rooted `<base href>` is answered for the rest of the `_framework` tree by `MapStudioFrameworkFiles`
(`{Path}/_framework/{**file}`): it forwards to whatever the host serves at `/_framework/{file}` (the exact-route endpoint
`MapStaticAssets` registers per file), else to the web root's `_framework/`, behind the studio's own authorization like
the other mirrored endpoints, and answers 404 for a missing file or a path containing `..`. A Debug build with the .NET 11
SDK lists a hot-reload JS initializer (`Microsoft.NET.Sdk.BlazorWeb.DotNetWatch.<hash>.lib.module.js`) among the page's
Blazor initializers, and a 404 on it stops `Blazor.start`. D24 holds: the packages ship no initializer, this serves only
the host's own files. Measurements, and the
vendored copy that was tried and dropped: decision-notes.md, D22.

## D23

**D23 — The studio has no forms, so it disables antiforgery on its own endpoints.** Every mutation is a
Blazor event over the circuit; there is no `<form>` and no `<EditForm>` anywhere in `Components/`, and
SignalR's own same-origin check is what stands between a cross-site page and that circuit. The
`<AntiforgeryToken />` in the shell was therefore protecting nothing — and it cost the host a hard
requirement, because `MapRazorComponents` stamps `IAntiforgeryMetadata` that *requires* validation and
the framework refuses to serve such an endpoint when the middleware is absent: a host that never called
`app.UseAntiforgery()` got a 500 for `GET /marten`. "Registering the studio must never change host
behaviour" includes not dictating the host's middleware order, so the token is gone from the shell and
the studio's component endpoints say `.DisableAntiforgery()`. A host that *does* use the middleware is
unaffected — it simply has nothing to validate here. If a screen ever grows a real form, it brings the
token back with it, scoped to that endpoint rather than to the whole studio.
`StudioEndpointsTest.A_host_that_never_calls_UseAntiforgery_still_serves_the_studio` is the regression.

## D24

**D24 — The packages ship no Blazor JS initializer; the shell loads `js/studio.js` itself.** A file named
`*.lib.module.js` in an RCL's `wwwroot` *is* a JS initializer, and Blazor loads every referenced RCL's into **every**
Blazor app in the process, so a host's own app fetched the studio's from the **site root** on every page and, behind
`AuthorizationPolicy`, logged a MIME-type error on each (issue #2). The script is a classic one, PostgresStudio's
`wwwroot/js/studio.js`, loaded by the shared shell document `StudioDocument.razor` at the end of `<body>`, before
`blazor.web.js`, with a **relative** `src` that resolves against the studio-rooted `<base href>` to the
`_content/PostgresStudio` mirror under the mount path; its global keeps the name `window.martenStudio`. So the
remembered theme applies to the prerendered markup, and every global exists before the circuit starts.
`NoJsInitializerTests` holds both halves: no `*.lib.module.js` under `wwwroot`, and a shell that still asks for the
script. A studio navigation resets the `<html>` element's attributes, so state the script keeps there lives in JS and an
observer re-applies it. History: decision-notes.md, D24.

## D25

**D25 — Every data table sits in its own labelled scroll region, and the shell never scrolls sideways.**
`.ms-main` hides horizontal overflow on purpose, so the studio never grows a page-level scrollbar inside
a host page; the cost was that a table wider than the content column was simply cut off with no cue and
no way to reach the columns on the right (measured: decision-notes.md, D25). So a `<table class="ms-table">` is always
inside `<TableScroll Label="…">` (`Components/Shared/TableScroll.razor`: `role="region"`, an accessible
name, `tabindex="0"`, `overflow-x: auto`) or, for the documents list, its own `.ms-table-wrap` carrying
the same three attributes. A document-wide observer in `js/studio.js` stamps
`data-scroll-start`/`data-scroll-end` on a region that can scroll further and the stylesheet fades that
edge with a `mask-image`; the mask is lifted while the region has `:focus-visible`, because a mask clips
the element's painted output to its border box and the focus ring is painted outside it — with both on,
the ~20 tab stops the regions add were invisible. `TablesAreScrollableTests` walks every `.razor` in
`src/` with a tag stack and fails on a bare table, with an anti-vacuity theory of its own. Where a table
is wide because of what a column *prints* (an assembly-qualified type name, three constant scope
columns, a wrapped timestamp), fix the column first — the region is the floor, not the answer. The
Relationships diagram and the database browser's tab rows use the same region and edge cue: the diagram is
drawn at its natural size and scrolls sideways rather than shrinking until its labels are unreadable.

## D26

**D26 — The stylesheet's UX sections are appended in cascade order, and later wins on purpose.** Each packet
puts its new rules in a titled section appended to PostgresStudio's `wwwroot/css/studio.css`, which holds both studios'
rules (a Marten-only sheet, if one is ever needed, is linked *after* it), and edits an existing rule in place only
where its report says so. Sections that re-declare an earlier selector (`.ms-event-card`, `.ms-projection-note`,
`.ms-overview-list-stream`, `.ms-doc-detail-actions`, and `UX-6`'s notices' contrast and heading focus ring) win by
source order: keep that when touching those selectors, as a rule added *before* the section that overrides it does
nothing. `StylesheetTests` (plus its `.Shell` part) pins the ones that were measured, `StylesheetTests.DatabasePolish`
`UX-6`, `StylesheetTests.FinalPolish` `UX-7` and `StylesheetTests.QueryRefusal` `UX-8`; from 0.5.0 each section's own
stylesheet test reads it out of `studio.css` by its header. 0.6.0 appended `M1`, `X3`, `M3` (tested in
`DeadLetterGroupsTests`), `M2`, `X1`, `X2`, `M47` and `UI-0.6` (tested in
`StylesheetTests.UiSweep`), in that order. 0.6.1 appended `UI-0.6.1`, tested there too. 0.8.0 appended `0.8-B` and `UI-0.8`; 0.9.0 appended `0.9-A` (the plan analyzer, tested in `PlanAnalyzerStylesheetTests`). `UI-0.6` holds the file's one rule for a bare link, `:where(.ms-studio a)`:
no specificity, so any rule that names a link wins over it. Earlier releases' sections: decision-notes.md, D26.
0.10.0 appended `0.10-Q35` (accepted checks) `0.10-A` (the plan facts panel, tested in `PlanFactsStylesheetTests`) and `0.10-B` (Compare and the settings what-if,
tested in `PlanCompareStylesheetTests`) and `UI-0.10` (the sweep's wrap for a node's values, tested in
`PlanUiSweepStylesheetTests`), in that order.
1.0.0 appended `1.0-UIFIX` (menus and the rail's credit), described next.

## Menus and the rail (1.0-UIFIX)

**A popover is placed by its own anchor, never by the user agent.** A `popover` is a top-layer element, and an unplaced one is
centred in the viewport over whatever is there: the Health page's Snapshot menu (a `MenuButton` with only `.ms-menu-popover`)
and the JSON viewer's copy menu (`.ms-copy-menu`) opened that way, because neither class had a `:popover-open` rule. So
`MenuButton` itself gives its button `anchor-name` and its popover `position-anchor`, inline, by a name made from the button's
id (`--<id>-button`), and the stylesheet's placement rules (`.ms-menu-popover`, `.ms-doc-copy-popover`, `.ms-db-copy-popover`)
anchor to that name and flip when there is no room; a new class given to `MenuButton` needs no rule of its own to be placed,
and two menus on a page never share a name. The JSON viewer has one menu for every row, so `studio.js` gives
`--ms-menu-invoker` to the key button that asked for it (or, from the keyboard, to the focused row's key) and
`.ms-copy-menu:popover-open` anchors to that. A popover in the top layer still needs a Playwright check: where it lands is the
browser's layout, and no bUnit test sees it (`PostgresStudioMenuScenarioTests` measures each menu against its button). The
item detail of the Snapshot menu is prose and takes the UI font; a copy menu's details are values (names, statements) and
stay in the code font.

**The rail's own column sticks to the window.** `NavMenu` wraps its brand, list and credit in `.ms-sidebar-inner`, which is
`position: sticky`, as tall as the window and scrolls inside itself; the `aside` round it keeps the page's full height for the
rail's background and border and is therefore `overflow: visible` (a scroll container would be what the column stuck inside).
The credit is the column's last child with `margin-top: auto`: at the foot when the list is shorter than the window, after the
list when it is longer, and never over it. It is three `.ms-rail-credit-line` blocks (product and version, licence, author).
On the 56 px rail (at 1024 px and below) it is clipped out of sight the way the entries' names are, and stays in the
accessibility tree.
