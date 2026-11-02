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
sql` function is expanded or inlined into visible tables and a `plpgsql` call prints its defaults. Nor (1.0) one that
names a view or SQL function in a visible schema that reaches a withheld schema's view or function
(`StatementCrossing`: a `pg_depend` walk from visible views and SQL-standard bodies, string-bodied SQL functions lexed,
to a fixed point, bounded, failing closed; matched by whole word). A plan reading a withheld schema is not
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
over in chunks, since the circuit accepts 32 KB a message. P09 to P14 (0.9) and P15 to P20 (0.10) join the plan insight registry.

**Paste for every visitor, text-format plans and the static lint (1.0, issue #28 phase 3).** The page's gate is split: running
a plan keeps `RunSql`, the write policy and everything above, while pasting one - which reads nothing, runs nothing and is not
audited - needs only what opening the studio needs, so Plan is in both studios' rails for every visitor and the paste box is
drawn with `RunSql` off, under `ReadOnly` and when the write policy refuses running. `QueryPlanReader.ReadAny` reads a paste as
JSON or as `EXPLAIN`'s default text format (`QueryPlanTextReader`: the same bounds, linear scans, never throws, unread lines kept
verbatim and dropped under a mask) into the same `QueryPlan`, held to the JSON reader by `PlanTextParityLiveTests` on the whole
matrix. P22 to P25 are insights judged from the statement's text (`PlanInsightInfo.FromText`, `PlanInsights.EvaluateText`, the
studio's `SqlLexer`), shown under the editor before any run and never evaluated on a plan.

**The icicle view and the drill-in's exports (1.0, issue #28 phase 3, part B).** `PlanIcicleLayout` is a pure layout over `QueryPlan`:
a frame's width is the node's inclusive value (the sum of the `ExclusiveMs`, or `ExclusiveCost` for a plan that is not timed, over
its subtree) and its own part is that node's own value, read through `PlanSpeedscope.Clamp` - the same computation the speedscope
profiles use over the same `PlanNode.Exclusive*` the tree's bars and the node table show, never a second one. A node whose own
value is zero is still as wide as what is below it; frames narrower than `PlanIcicleLayout.MinWidth` (0.002 of the root) are one
merged frame for their parent, with a negative id and no selection. `PlanIcicle` draws it as hand-made SVG (hard rule 3) in
percentages inside a `TableScroll` (D25), shares the Plan page's one selected node and heat, and gives every frame a focusable
`g` with the arrow-key moves; `studio.js`'s `icicleKeys` listener only keeps the browser from scrolling under those keys. The
Statements drill-in's `PlanView` takes `MaskedExport`: its `PlanExport` then offers Markdown and speedscope built from the masked
`QueryPlan` it draws (`PlanTable.Markdown`, `PlanSpeedscope.Build`), and never a JSON or text export, because there is no masked
`EXPLAIN` JSON and a JSON of the studio's own masked tree could be mistaken for one. A refused or planless drill-in offers none.

**Facts beside the plan (0.10, issue #28).** After a plan the Plan page ran - never a pasted one, never on a poll - `PlanFactsService`
reads what the database knows about the relations it names, in the console's read-only transaction as `SqlConsoleRole`, behind
the console's own gate whole and then, in this order: each relation's schema and every table above it in `pg_inherits`, walked
inside `ResolveSql` (`DatabaseGate.CanSeeStructure`, a hidden type's table; a relation that fails is `‹withheld›`, with no facts,
no count and no name in the record or the audit), then the values' row gate (`DatabaseAccess.RequireRowAccessAsync`, with the
page's own `BrowseDatabase` question asked first and unaudited). Relations are named by the schema and name a `VERBOSE` plan
prints (the page's default, `PlanOptions.PageDefault`) and resolved by a catalog join - never `to_regclass`, which raises 42501
for a schema the role has no USAGE on; every statement (`PlanFactsQueries`) reads `pg_catalog` and the statistics views by oid,
with no size and no deparse function, held lock-free by `MatrixReads` on 14 to 19. Column types are masked like the browser's.
`most_common_vals` and `histogram_bounds` are data: read only for a relation the row gate admits, never a Marten table's, and
audited per relation as a `BrowseDatabase` read, a failed read included; the facts read itself is one `RunSql` entry naming no
relation. P15 to P18 (`NeedsFacts`) judge the plan against the facts and are listed as not judged without them; P19 and P20
read the plan alone. Condition texts are read for column names by `PlanConditionText`, a bounded scan that returns names only,
which are afterwards only ever bound as parameters.

**Comparing plans (0.10-B).** Compare draws `PlanDiff`, a pure function over two parsed plans that reads nothing. The
settings what-if changes only names on the allow-list in `PlanSettingsQueries`, refused before a connection is opened, with
`work_mem` held to 1 GB and `hash_mem_multiplier` to 8 in every mode (refused, never reduced), and with name and value bound
to `pg_catalog.set_config(..., true)` inside the console's read-only transaction, which always rolls back; the audit entry
names them. The per-run timeout is clamped to the host's `QueryTimeout`. What if? in place is `ISqlConsoleService.PlanWhatIfAsync`:
X2's engine behind the console's gate, for an index `MigrationSqlText` writes only (so not P17's expression index). P21
judges a generic and a custom plan of one statement, so it runs in the compare path only.
