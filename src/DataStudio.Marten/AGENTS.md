# AGENTS.md: the Marten module

The [root AGENTS.md](../../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the Marten module. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** Documents, events, the daemon, projections, sessions, schema, LINQ mode: [marten.md](../../docs/maintainers/marten.md) (D6, D7, D28); reach, not scope (rebuild, rewind, skip, high water, apply): [sql-and-catalog.md](../../docs/maintainers/sql-and-catalog.md#d27); lag samples and dead-letter groups: [architecture.md](../../docs/maintainers/architecture.md#d10); the Marten API surface test and the sibling source checkouts: [testing.md](../../docs/maintainers/testing.md); [hard-rules.md](../../docs/maintainers/hard-rules.md) rules 10, 11, 14 and 15. The Marten module is built on the Postgres module: [its file](../../src/DataStudio.Postgres/AGENTS.md) applies to the pages and checks it shares.

## Hard rules

10. **In the Marten module, Marten 9 is async-only, and its optional parts are genuinely optional.** There are no synchronous
    LINQ terminals — they throw. Serialize and deserialize through `store.Options.Serializer()`, never your own
    `JsonSerializer`. Never touch `StoreOptions.Storage.AllDocumentMappings` (internal). Every metadata column is
    optional: build the column set with `DocumentTableInfo.FromDocumentType(...).WithPhysicalColumns(...)`, never
    `select *`, and enumerate `MartenDuplicatedFields.Physical`, because `IDocumentType.DuplicatedFields` is not a list
    of columns. Full text: hard rule 10.

11. **In the Marten module, never call `store.BuildProjectionDaemonAsync()`.** It starts a *second* daemon alongside the
    host's own, and the two fight over the same advisory locks until one hangs. The only supported way to reach a
    running daemon is the DI-registered coordinator, and its absence is a value — `DaemonHosting.NotHostedInThisProcess`
    — not an error. Full text: hard rule 11.

14. **In the Marten module, a read or navigation path never calls `AllSchemaNames()`, `AllObjects()`, `ToDatabaseScript()` or
    `CreateMigrationAsync()` — and nothing may describe `CreateOrUpdate` as additive.** (The Postgres module's read paths
    write nothing, as `FirstNavigationLiveTests` proves for every page.) Below Marten 9.50.3 the first two *apply
    migrations* when a document type has a numeric or HiLo id; the rule stands at every version. **Nor does a read path
    run a Marten LINQ terminal**, `ToCommand`, `ExplainAsync`, `ToPreviewCommand`, `LoadAsync`, `LoadManyAsync` or a
    `session.Events` read, nor (from 9.52) `FetchProjectionFloorAsync`, its setters or the high-water advance: each
    ensures storage, which creates or migrates it. Reads use the studio's own SQL (`StorageEnsuringCallTests`; LINQ
    mode's composition builds its statement without ensuring storage, and that covers `QueryRawEvents()`, never
    passed a tenant), and schema names, declared indexes and what Marten owns come from `SchemaDeclarationReader`,
    without a connection. Enumerating a dynamic tenancy's databases is not read-only either
    (`TenancyEnumerationCallTests`). Time travel and
    the stepper fold only through `IAggregateFolder.FoldAsync`. **`AutoCreate.CreateOrUpdate` is not additive**; no
    screen, comment or doc may say an apply leaves a hand-made index alone. Apply and its preview run under
    `CreateOrUpdate`, never `AutoCreate.All`; an apply is authorized as `(db, null, ApplySchemaChanges)` whatever tenant
    is selected, and its typed confirmation travels to the service, never re-supplied by the page. Everything
    schema-related is per database, never per store. **The database browser reads the catalog, never Weasel**:
    `pg_catalog` (never `information_schema`), filtered by `nspname = any(@schemas)`, inside `ReadOnlySqlSession`. Full
    text: hard rule 14.

15. **In the Marten module, open Marten sessions only with `SessionOptions.ForDatabase(resolved.TenantId,
    resolved.Database)`** (or `ForDatabase(resolved.Database)` where the scope has no tenant), never
    `Store.QuerySession(tenantId)` / `LightweightSession(tenantId)`. **Never run a user-supplied clause through
    `Query<T>(string)` / `QueryAsync(Type, string)`**: Marten's `UserSuppliedQueryHandler` adds no tenant or soft-delete
    predicate. A document type Marten registers `SingleTenanted()` whatever the store (`DeadLetterEvent` is one) has no
    `tenant_id` column and no Marten predicate: filter on its own `TenantId` property, and prove a sequence is in scope
    before handing it to an API with no tenant predicate. **In every module, catch interop loss with `catch (Exception
    e) when (StudioLiveUpdates.IsInteropUnavailable(e))`**, never a ladder of `catch` clauses. Full text: hard rule 15.

## Design decisions

**D6 — Reads are raw parameterised SQL, writes go through a Marten session.** Reads are built from `IDocumentType`
metadata against `IMartenDatabase.CreateConnection(ConnectionUsage.Read)`. Writes go through a Marten session
(`StoreObjects` / `DeleteObjects`); the studio never hand-writes document DML. Full text:
[marten.md](../../docs/maintainers/marten.md#d6).

**D7 — Document edit shows a round-trip diff before saving.** Saving an edited document means deserializing into the CLR
type and serializing it back, so any property the type does not have is gone: the dialog lists dropped, changed and
added properties before the save. An edit keeps its document's id; preview and save refuse a JSON id that differs from
the row's. Full text: [marten.md](../../docs/maintainers/marten.md#d7).

**D9 — Document and stream ids travel in the query string.** The hand-rolled router has no catch-all segment, and Marten
ids are frequently strings that contain `/`. Scope (`?store=&db=&tenant=`) lives in the URL for the same reason a
document id does: a link someone pastes into a chat has to reopen the same thing. A link that names a tenant and no
database opens the database, among those the visitor may see, that holds the tenant: a static tenancy places it from its
configuration (the usual database included, with no tenancy enumeration, and nowhere when it is declared in more than one
database), every other kind finds it in the databases' tenant listings (at most 32 searched, one enumeration). Where a
static tenancy cannot place it, or is still placing it, the scope stays where it was and the pages a link opens show their
not-found wording without reading by id; elsewhere a tenant found nowhere falls back to all tenants. A link that names
both is reopened exactly.

**D14 — No live SQL tail.** It would require replacing the host's `StoreOptions.Logger`, which is exactly the
"registration changes host behaviour" failure this project refuses, and Marten 9 defaults `DisableNpgsqlLogging=true`
anyway. Every studio-generated grid carries a "Show SQL" disclosure instead, which answers the question people actually
have.

**D28 — LINQ mode is its own capability, parsed by JasperFx's Dynamic LINQ, built without `ToCommand`, and unconditional
since the 9.57 floor.** It sits behind `RunLinqQueries` (off by default, authorized like a write), parsed by JasperFx's
`DynamicQuery` under `DynamicQueryPolicy` behind a lexical screen and the studio's own walk of the parsed tree, and
built from `BuildLinqParser()` and `BuildDiagnosticCommand`, never `ToCommand`. The adapter is reached by reflection and
fails closed on a JasperFx shape it does not recognise. It also targets the event log through `QueryRawEvents()`, never
passed a tenant. `docs/linq-mode.md` is the threat model. Full text:
[marten.md](../../docs/maintainers/marten.md#d28).
