# Marten documents, events, the daemon and LINQ mode

This is the full text of D6, D7 and D28, moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D6

**D6 — Reads are raw parameterised SQL, writes go through a Marten session.** The studio only knows
document types as runtime `Type`s, so a generic `session.Query<T>()` is not available to it; reads are
built from `IDocumentType` metadata against `IMartenDatabase.CreateConnection(ConnectionUsage.Read)`.
Writes are the opposite: `StoreObjects(IEnumerable<object>)` / `DeleteObjects(IEnumerable<object>)` (both
verified non-generic) keep metadata columns, soft-delete semantics and tenancy exactly as Marten defines
them. The studio never hand-writes document DML; writes go through the session — re-implementing Marten's
write path is how a UI corrupts a store (and there is no upsert function to call: decision-notes.md, D6).

The event flow (1.2) reads the projection graph by JasperFx's own rule (`SubscriptionDescriptor.appliedEventsFor`: an
aggregation's `AllEventTypes`, anything else's `IncludedEventTypes`), restated over public members; it never calls
`Describe`, `TryCreateUsage` or `CreateSchemaObjects`. An event type a projection declares that the registry has not met
yet is shown, marked not registered. `Identity<T>()` groups events and does not declare `T`. A hidden document type's
whole hierarchy is hidden, and when Marten will not give the store's document types the model fails closed: every
document type counts as hidden and no source lists what it writes.

## D7

**D7 — Document edit shows a round-trip diff before saving.** Marten has no untyped store API, so saving
an edited document means deserializing into the CLR type and serializing it back: any property the type
does not have is *gone*. The dialog lists dropped, changed and added properties before the save happens.
Making the loss visible is the honest answer; silently dropping data is not, and refusing to edit at all
would make the feature useless for the schemas that need it most. An edit keeps its document's id: preview and save
refuse a JSON id that differs from the row's. Since Marten 9.53 (unconditional since the 1.1 floor of 9.57) the dialog lays
Marten's own `PreviewDocumentJsonAsync` beside the round trip - no session, no connection - and holds a save it
disputes.

## D28

**D28 — LINQ mode is its own capability, parsed by JasperFx's Dynamic LINQ, built without `ToCommand`, and
lit up at run time from Marten 9.48.** The Query page's third mode hands the predicate a host's code would
write to Marten's own `Query<T>()` on a pinned session, so tenancy and soft deletes stay Marten's. It sits
behind `RunLinqQueries`, off by default and authorized like a write, rather than riding Mode A's
no-capability read, because its risk is of another kind: Marten's provider compiles and invokes every
subtree that does not reference the document, so anything past a literal that reaches it is code running in
the host. It is parsed by JasperFx's `DynamicQuery` under `DynamicQueryPolicy`, not by a parser of our own,
because that is the allow-list JasperFx keeps for this threat and the dialect Marten's own diagnostics read
speaks, and a C# expression parser written here would be one more parser to get exactly right
(System.Linq.Dynamic.Core is not ours to reference either: hard rule 1). No one allow-list is trusted alone:
a lexical screen in front of the parse and the studio's own walk of the parsed tree behind it leave Marten
nothing to evaluate but literals. The statement comes from `BuildLinqParser()` and `BuildDiagnosticCommand`,
never `ToCommand`, which runs `EnsureStorageExists` - DDL on a read path (hard rule 14); a Marten that moves
those internals makes the mode absent, never a fallback. And 9.48 (JasperFx 2.84, the first with
`DynamicQuery`) was a light-up, not a floor, while the floor was 9.39: a host on 9.39-9.47 was not dragged forward for
one mode. Since the floor moved to 9.57 (1.1, *Package budget changes* 2026-11-03) the `DynamicLinqQuery` probe is gone
and the mode is unconditional; the adapter is still reached by reflection and still fails closed, pinned by
`DynamicLinqAdapterTests` and `MartenLinqCommandTests`, and its absence is now only a JasperFx whose shape the studio
does not recognise. `docs/linq-mode.md` is the threat model.

Since 1.1 it also queries the event log through `IReadOnlyEventStore.QueryRawEvents()`, called with no tenant
argument on the same pinned session (the overload with one opens an unpinned `QuerySession`), built by the same
`BuildLinqParser`/`BuildDiagnosticCommand` path. The members are a closed list mirroring Marten's `EventQueryMapping`
columns from `IReadOnlyStoreOptions.Events`, because every other `IEvent` member reads the JSON body and matches
nothing. The statement must stay on the studio's session and read no `d.data`, or it is refused.
