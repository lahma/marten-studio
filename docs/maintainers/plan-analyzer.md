# The plan analyzer

This is the full text of D32, moved word for word from AGENTS.md on 2026-10-31. AGENTS.md states each rule in brief; this document holds the detail and wins where they differ.

## D32

**D32 — The studio draws every plan, plan insights judge it, and EXPLAIN ANALYZE is the console's alone.** The console's
Explain buttons (and a typed `EXPLAIN (FORMAT JSON)`), Mode A and the Statements drill-in (`?queryid=`) all go through
`QueryPlanReader` and `PlanView` (`ms-plan-`, in a D25 region). Insights are pure evaluators with stable `P` codes in
`codes.md` (area Plans), in a registry of their own: never a health check, sweep, mute or diagnostics-API result.
`EXPLAIN ANALYZE` runs only under `RunSql`, for a statement the guard accepts, in the read-only transaction. The
drill-in plans a `pg_stat_statements` text as its generic plan by `prepare` + `force_generic_plan`, never
`GENERIC_PLAN`, which Npgsql's extended protocol breaks. It runs as `SqlConsoleRole`, `VERBOSE`, under the visitor's
`search_path` less withheld schemas, and never plans a write, a withheld text, or a text
that names a withheld schema (`StatementsService.TextNamesWithheld`: one the visitor's mask changes, or a `U&"` name
while anything is withheld; asked again by the reader before any `prepare`), because a qualified view or `LANGUAGE
sql` function is expanded or inlined into visible tables and a `plpgsql` call prints its defaults. Known limit: a view
or SQL function in a visible schema that reads a withheld one is expanded like any other. A plan reading a withheld
schema is not
shown; the rest is masked like the text, and each plan is audited by `queryid`. Console and Mode A plans are `RunSql`
output, unmasked (D27). What if? (X2) imagines one index with hypopg behind the gate of the plan it accompanies, only
one a builder writes (`MigrationSqlText`, or Marten's own `ToDDL`), re-derived on the server. Read-only; never
`CREATE EXTENSION` or a real index; `hypopg_reset()` on the same connection, and a session whose reset fails ends
itself, so no pooled backend keeps a hypothetical index. **Query › Plan (0.9, issue #28)** is the console's gate as a page of its
own, and nothing new: `ISqlConsoleService.PlanAsync` takes the same capability, write policy, guard, read-only transaction
and timeout, audited as `RunSql` (the matrix classifies it with `RunSqlAsync`), planned, analyzed, or as a generic or custom plan
of a statement with `$n` placeholders - a custom plan binds the typed values to the statement `EXPLAIN` explains, because a
protocol parameter cannot reach `EXECUTE`, and a prepared statement only reads the inferred types - with `PlanView`'s
`Analyzer` mode drawing it (unmasked: only `RunSql` output keeps every key of a node). A pasted plan reads nothing and goes
over in chunks, since the circuit accepts 32 KB a message. P09 to P14 join the plan insight registry.
