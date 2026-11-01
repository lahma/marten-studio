# Authorization and capabilities

This is the full text of D4 and D5 (see also D27's *Reach, not scope* in sql-and-catalog.md), moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D4

**D4 — Capabilities are a class of booleans, all defaulting to `false`.** A freshly mapped studio is a
read-only browser; `o.Capabilities = MartenStudioCapabilities.All()` is the one-line, greppable opt-in, and
every disabled control names the exact property a host would have to set. This is Hangfire's
GHSA-7rq6-7gv8-c37h applied to the write axis: the dashboard that is dangerous by default is the one
someone maps without reading the docs. `PostgresStudioCapabilities` has four, all reads authorized like writes
(`BrowseDatabase`, `ViewDefinitions`, `ViewDatabaseActivity`, `RunSql`), on the same terms.

## D5

**D5 — Three authorization layers, plus two process-wide switches.** `AuthorizationPolicy` decides who
gets in; `StoreAuthorizationPolicy` decides which store, database and tenant, resource-based and enforced
on **every** data call rather than only at the selector; `WriteAuthorizationPolicy` decides each mutating
call, against the same resource with the capability name attached. `ReadOnly` and `Capabilities` sit on top
of all three. A startup guard refuses a mapping that answered none of it, because "I forgot to add
`RequireAuthorization`" must not be a silent state. Postgres Studio has the same three layers and the same guard, with
`SourceAuthorizationPolicy` asked about a `PostgresStudioResource` in place of the store policy (D30).
Known limit: under a policy refusing by registered name, a case variant tells a refused source or database from a missing
one, as matching ignores case.
