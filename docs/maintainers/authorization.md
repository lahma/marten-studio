# Authorization and capabilities

This is the full text of D4 and D5 (see also D27's *Reach, not scope* in sql-and-catalog.md, and D35 in kafka.md for the Kafka module's own policies). AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D4

**D4 — Capabilities are a class of booleans, all defaulting to `false`.** A freshly mapped studio is a
read-only browser; `marten.Capabilities = MartenCapabilities.All()` is the one-line, greppable opt-in, and
every disabled control names the exact property a host would have to set. This is Hangfire's
GHSA-7rq6-7gv8-c37h applied to the write axis: the dashboard that is dangerous by default is the one
someone maps without reading the docs. Each module has its own class: `MartenCapabilities`, `PostgresCapabilities` and
`KafkaCapabilities`. `PostgresCapabilities` has four, all reads authorized like writes
(`BrowseDatabase`, `ViewDefinitions`, `ViewDatabaseActivity`, `RunSql`), on the same terms; `KafkaCapabilities` has four too
(`SearchRecords`, `ReadPayloads`, `ExportRecords`, `LiveTail`).

## D5

**D5 — Three authorization layers, plus two process-wide switches.** The shell's `AuthorizationPolicy`
(`DataStudioOptions`) decides who gets in, on every endpoint including the front page; the module's store policy
(`MartenOptions.StoreAuthorizationPolicy`) decides which store, database and tenant, resource-based and enforced
on **every** data call rather than only at the selector; `WriteAuthorizationPolicy` decides each mutating
call, against the same resource with the capability name attached. Two switches and the capabilities sit on top
of all three: the shell's `ReadOnly` turns every module's capabilities off, and a module's own (`MartenOptions.ReadOnly`,
`PostgresOptions.ReadOnly`) turns off that module's only - the Kafka module has none of its own and obeys the shell's. A startup guard
refuses a mapping that answered none of it, because "I forgot to add
`RequireAuthorization`" must not be a silent state. The Postgres module has the same layers with
`PostgresOptions.SourceAuthorizationPolicy` asked about a `PostgresResource` in place of the store policy (D30), and the
Kafka module has `ClusterAuthorizationPolicy` and `CapabilityAuthorizationPolicy` on a `KafkaTopicResource` (D35). A module's options have no
`AuthorizationPolicy` of their own.
Known limit: under a policy refusing by registered name, a case variant tells a refused source or database from a missing
one, as matching ignores case.
