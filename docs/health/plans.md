# Plan insights (P)

Every place the studio shows a query plan draws it as a tree, and under the tree it lists what the plan says is wrong with
it: **plan insights**. A plan is drawn in four places:

- **Query › Plan** (`RunSql`), the plan analyzer: write a statement, or paste a plan, and see it as a tree and as a table of
  every node, with its summary, where the time went and what to fix - [below](#query--plan-the-plan-analyzer);
- **the SQL console** (`RunSql`): its **Explain** and **Explain analyze** buttons run the statement in the editor as
  `EXPLAIN (FORMAT JSON)` or `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`, and an `EXPLAIN (FORMAT JSON …)` typed by hand is
  drawn the same way; **Open in Plan** carries the statement to the Plan page;
- **Query › Marten where clause** (Marten Studio only): the plan of the statement the clause composed, as before, now drawn
  as a tree;
- **Operations › Statements**: open a statement's **Plan** to see the plan of its text as `pg_stat_statements` recorded it
  (`?queryid=` in the address, so the link reopens the same statement).

Back to the [index](../health.md). Every code, its area and its studios: [codes.md](codes.md).

## What the tree shows

Each node shows what it is and what it reads (`Index Scan using orders_pkey on public.orders o`), the rows the planner
expected, and - under `ANALYZE` - the rows it returned and how many times it ran, its share of the plan's cost (or of its
time, when it was analyzed with timing on), and its buffers and I/O time when Postgres printed them. A node's own cost is
its total less its children's, as Postgres' costs are cumulative; its own time is the wall time its per-loop average
implies, less its children's, divided among the processes that shared it when it ran in parallel workers. Both are
clamped at zero, so a nested loop's repeated inner side is charged to the loop. PostgreSQL 18 prints buffers under
`ANALYZE` without being asked, and four keys of its own - `Disabled` (a planner setting disabled this kind of node and it
was used anyway), `Index Searches`, and a materialising node's `Storage` and `Maximum Storage` - which are shown where
they are printed and nowhere else.

The tree is drawn from `EXPLAIN (FORMAT JSON)`, read defensively: a key the studio does not know is ignored, one it
expects and does not find is left blank, and text that is not a plan says so. At most 2,000 nodes are read and 200 levels
walked, and a plan larger than 4 MB of JSON is not drawn; the console and Mode A still offer the raw JSON.

## Why these are not health checks

A health check is the database's: a sweep reads its facts once per interval for everybody, keeps an hour of history, and
its findings are gated, muted and returned by the diagnostics API. An insight is about one plan that one visitor asked
for, and is gone with the plan. So insights have a registry of their own and a letter of their own, `P`, in the same
[codes.md](codes.md) contract: a code is never reused, renamed or moved. They are never returned by the diagnostics API,
never muted and never toasted.

**Who sees an insight is who sees the plan.** The console's and Mode A's plans are `RunSql` output: the visitor ran the
statement, and the plan is no more masked than the console's rows are. The Statements drill-in's plan is of another
program's text, behind the same gate as the page (`ViewDatabaseActivity`, the source or store policy and the write policy,
asked with no tenant), and masked for the visitor - see [below](#the-statements-drill-ins-plan).

**Fourteen need `ANALYZE`.** P01, P02, P03, P04, P08, from 0.9 P09 to P14 and from 0.10 P15, P16 and P20 read what only
`EXPLAIN ANALYZE` prints - rows returned, a spill, a lossy page, the rows a filter threw away, the workers given, the heap
fetches, a hash's batches, JIT and execution time, a memoize cache's hits, the time the triggers took - so a plain plan lists
them as not judged rather than as quiet. P05, P06, P07, P17, P18 and P19 read the planner's estimates when there is nothing
better.

**Four need the database's facts.** From 0.10, P15, P16, P17 and P18 judge a plan against what the database knows about the
relations it names - [facts beside the plan](#facts-beside-the-plan) - which only a plan the Query › Plan page ran has. A plan
pasted there lists them as not judged, never as quiet; the console's, Mode A's and the Statements drill-in's plans read no facts in
0.10 and do not name them.

**At most five findings an insight**, the worst first.

## P01 Row estimate off tenfold or more

**Warning**, under `ANALYZE`. A node returned ten times more, or ten times fewer, rows than the planner expected - per
loop, with at least 1,000 rows on the larger side over all its loops (a 1 against 12 is nobody's problem). The join and
the scan above it were then chosen for a different statement than the one that ran: a nested loop for what was supposed
to be a handful of rows, a hash sized for far fewer.

**Remedy.** `ANALYZE` the table it reads, so its statistics are current, and plan it again. Where one column's values are
skewed, a higher statistics target (`ALTER TABLE … ALTER COLUMN … SET STATISTICS`) lets the planner see the skew; where
columns move together, extended statistics (`CREATE STATISTICS`) let it see that.

## P02 Sort or hash spilled to disk

**Warning**, under `ANALYZE`. A sort sorted on disk (an external sort, or one of its parallel workers did), a hash split
into more than one batch, or a hash aggregate wrote to disk: each ran out of `work_mem` and used temporary files.

**Remedy.** A larger `work_mem` for the role or the session that runs the statement (`SET work_mem`) keeps it in memory;
so do fewer rows reaching it - a more selective filter, or an index that returns them already in order - or a narrower
select list.

## P03 Lossy bitmap heap scan

**Info**, under `ANALYZE`. A bitmap heap scan's bitmap outgrew `work_mem`, so it kept some heap pages only as whole pages
(`Lossy Heap Blocks`) and checked every row on them again.

**Remedy.** A larger `work_mem` keeps the bitmap exact; a more selective index condition makes it smaller.

## P04 Sequential scan with a selective filter

**Warning**, under `ANALYZE`. A sequential scan read 100,000 rows or more - T01's large table - and its filter kept a
tenth of them or fewer: the shape an index serves. A plain plan prints the rows a scan keeps, not the rows it reads, so it
is not judged.

**Remedy.** An index whose leading column the filter compares. In Marten Studio, for a document table, it is declared in
the store's options - a computed index, or a duplicated field - because an index made by hand on a Marten table is
dropped by the next `CreateOrUpdate`; the Query page's index advisor writes the line for a where clause. For any other
table, it goes in your own migrations, built `CONCURRENTLY` on a table in use. The studio never creates it.

## P05 Nested loop over a large outer side

**Info**. A nested loop's outer side has 1,000 rows or more - returned, under `ANALYZE`; estimated, otherwise - so its
inner side runs that many times.

**Remedy.** A hash or a merge join reads each side once. A nested loop is the right join for a few outer rows; for many,
it usually means the outer side was expected to be small (P01 says so under `ANALYZE`), or the inner side has no index on
the join key.

## P06 Large OFFSET

**Info**. A `Limit` reads and throws away 1,000 rows or more before the first one it returns. Under `ANALYZE` that is its
input's rows less its own; in a plain plan it is read back from the planner's own arithmetic (the limit's startup cost
less its input's, over its input's run cost, times its input's rows), except where the planner was guessing - an `OFFSET`
it cannot see, such as a parameter in a generic plan, is assumed to skip a tenth of the input, and that guess is not
reported.

**Remedy.** Postgres reads every row an `OFFSET` skips, so each page costs more than the one before. Paging by the last
key seen (`WHERE key > :last ORDER BY key LIMIT n` - keyset pagination) reads only the page, given an index on that key.

## P07 Leading-wildcard LIKE

**Info**. A filter tests a `LIKE` or `ILIKE` pattern that starts with `%` or `_` - which no btree index can serve - so
every row it reads is tested. Only a filter (or a join filter) counts: a pattern in an index condition or a recheck is
already served by an index, a trigram one. A pattern that is a parameter (`~~ $1`) says nothing and is not judged, and
neither is `NOT LIKE`, which no index serves anyway.

**Remedy.** A trigram index (the `pg_trgm` extension's GIN index, `gin_trgm_ops`) serves it - in Marten Studio declared in
the store's options for a document table, for the same reason as P04 - and so does searching for a prefix instead, or
full-text search.

## P08 Parallel workers short of the plan

**Info**, under `ANALYZE`. A `Gather` or `Gather Merge` was given fewer parallel workers than it was planned with, so the
rest of its work fell to the processes it got. The Statements page says the same of a statement's runs on PostgreSQL 18
(`parallel_workers_to_launch` against `parallel_workers_launched`).

**Remedy.** Parallel workers come from `max_parallel_workers` and `max_worker_processes`, which other parallel queries
were holding when it ran: raise them in the server's settings, or run fewer parallel queries at once.

## P09 Index-only scan fetching the heap

**Warning**, under `ANALYZE`. An index-only scan visited the table (`Heap Fetches`) for 10% or more of the rows it returned, and
at least 1,000 times - over all its loops, as Postgres prints the counter. An index-only scan skips the table only for a page
the visibility map marks all-visible; for any other it reads the row from the table to check it, so it was no cheaper than an
index scan.

**Remedy.** `VACUUM` sets the visibility-map bits: vacuum the table, and let autovacuum do it again by lowering
`autovacuum_vacuum_scale_factor` (or `autovacuum_vacuum_insert_scale_factor` for a table that is only inserted into) for it.
Operations › Maintenance shows when autovacuum will next reach each table, and the finding links to it.

## P10 Filter discarding most rows after an index

**Warning**, under `ANALYZE`. An index scan, index-only scan or bitmap heap scan threw away ten times the rows it returned or
more with its filter, and 10,000 or more - each counted over all its loops. The index found rows the filter then rejected, so
the table was visited for rows nobody wanted: the filter's column is not in the index.

**Remedy.** The same as P04's, with the placement each studio needs: an index whose leading column the filter compares, or the
filter's column added to the index this node used, or - when every query has the same condition - a partial index. In Marten
Studio an index on a document table is declared in the store's options; elsewhere it goes in your own migrations, built
`CONCURRENTLY` on a table in use. What if? is offered for a sequential scan's filter (P04); an index scan's plan does not give it a
column to imagine an index on, so P10 offers none.

## P11 Hash built in batches

**Info**, under `ANALYZE`. A `Hash` node was built in more than one batch, or in more batches than it was planned with: its table
did not fit in `work_mem` (times `hash_mem_multiplier`), so every batch but the first went through temporary files. P02 says that a
hash spilled; P11 says how much memory it would have needed.

**Remedy.** `work_mem` of about the hash's peak memory times its batches, rounded up to a megabyte - the finding prints the
number, and the `SET work_mem` to try - for the role or the session that runs the statement, never the whole server: every sort
and hash of the statement may use that much. A `hash_mem_multiplier` above 1 needs correspondingly less.

## P12 JIT costing more than it saves

**Info**, under `ANALYZE`. JIT took 20% or more of the execution time, and the statement ran for less than a second: compiling
cost more than it could save on a run that short.

**Remedy.** Raise `jit_above_cost` (100000 unless somebody changed it), so that only statements the planner costs higher are
compiled, or turn JIT off for the role that runs these statements: `ALTER ROLE … SET jit = off`.

## P13 Planning slower than execution

**Info**, under `ANALYZE`. Postgres spent longer planning the statement than running it, and more than 10 ms planning.

**Remedy.** A prepared statement is planned once and its plan reused (`PREPARE`, or the driver's own prepared statements -
Npgsql's `Max Auto Prepare`), so a statement that runs often stops paying for planning each time. Fewer joins, or fewer
partitions for the planner to weigh (a filter on the partition key, so pruning drops them at plan time), also shorten it.

## P14 Memoize mostly missing

**Info**, under `ANALYZE`. A `Memoize` node's cache missed at least as often as it hit, and evicted cached results to make room:
it was too small for the parameters it was asked about, so it cost its memory and saved little.

**Remedy.** A Memoize cache is bounded by `work_mem` times `hash_mem_multiplier`, and when it fills it throws results away: a
larger `work_mem` for the role or the session that runs the statement keeps more. If nearly every lookup's parameters differ,
no cache size helps - compare the statement with `SET enable_memoize = off` in the SQL console to see whether it is faster
without.

## P15 Stale statistics behind a misestimate

**Warning**, under `ANALYZE`, on a plan the Plan page ran. A node [P01](#p01-row-estimate-off-tenfold-or-more) flags - or a node below
it - reads a table that has never been analyzed, or that has had at least 10% of its rows (`reltuples`) inserted, updated or
deleted since it was last analyzed (`n_mod_since_analyze`): the planner's row counts for it are out of date. One finding for each
such table, on the node with the worst misestimate that reads it. **Not judged** without the database's facts (a pasted plan has none).

**Remedy.** `ANALYZE schema.table;` - shown as text for your own migrations or a maintenance window, never run by the studio. It
takes only a `SHARE UPDATE EXCLUSIVE` lock, so reads and writes carry on. To keep the statistics current, give the table a lower
`autovacuum_analyze_scale_factor` (autovacuum analyzes a table once 10% of its rows changed unless somebody set otherwise).
Operations › Maintenance shows when autovacuum will next reach it.

## P16 Correlated columns without extended statistics

**Warning**, under `ANALYZE`, on a plan the Plan page ran. A scan [P01](#p01-row-estimate-off-tenfold-or-more) flags compares two or
more columns of the table in its `Filter` or `Index Cond`, and the table has no extended statistics (`pg_statistic_ext`) covering
any two of them. The planner multiplies each column's selectivity as if the columns were independent, which is wrong wherever one
tells you something about the other. A condition that joins the columns with `OR` is not read as one. **Not judged** without facts.

**Remedy.** `CREATE STATISTICS schema.table_a_b_stats (dependencies, ndistinct, mcv) ON a, b FROM schema.table;` and then `ANALYZE`
the table, shown as text for your own migrations, never run. The statistics' name is built from the table's and the columns'
(PostgreSQL 14 requires a name) and goes in the table's schema; where a name cannot be printed honestly - it would pass 63 bytes,
or hold a character the studio will not quote - the finding names the columns and gives no statement.

## P17 Function or cast on an indexed column

**Warning.** A `Filter` or `Join Filter` wraps a column of a table in a function (`lower(email)`, `date_trunc(...)`) or casts it
to a type that is not its own (`(created_at)::date`), and that column leads a valid index of its table: an index serves a condition
on the column, not on a function or a cast of it. A cast between `varchar` and `text` is the same bytes and is no wrapper. An
`Index Cond` is not read: an index that serves a condition is already being used. **Not judged** without facts.

The condition is found in the text Postgres deparsed (the plan's own `Filter`), by a bounded scan that skips string literals and
reads quoted names (`PlanConditionText`); it never reads the statement as typed and runs nothing. A column a function takes as its
second argument is not seen as wrapped: a false negative, never a false finding.

**Remedy.** Compare the column as it is - move the function to the value it is compared with - or build an index on the expression
the filter uses. For a cast, cast the other side (the constant or the parameter) to the column's type, or index the cast
expression; a range on the column itself (`created_at >= ... and created_at < ...`) uses the plain index where a cast to `date`
cannot.

## P18 No partition pruning

**Warning.** An `Append` or `Merge Append` reads at least four partitions of one partitioned table, more than half of the partitions
that table has, and Postgres removed none of them (no `Subplans Removed`): nothing narrowed the partitions, at plan time or at run
time. The finding says whether any scanned partition's condition names a column of the partition key. **Not judged** without facts.

**Remedy.** Postgres prunes partitions on a condition that compares the partition key as it is with a value - a constant at plan
time, a parameter at run time. A function or a cast of the key (`date_trunc('day', ts)`, `ts::date`), an `OR` across other columns
or a comparison with another column defeats it; for a timestamp key use a range (`ts >= ... and ts < ...`). With no condition on
the key at all, add one - or accept that every partition is needed, when it really is.

## P19 Default row estimate on a function

**Info.** A `Function Scan` is expected to return exactly 1,000 rows, which is what Postgres assumes of any set-returning function
it has no better figure for (`pg_proc.prorows`). Under `ANALYZE`, a function that really did return 1,000 rows is not flagged: the
guess was right. Every join and sort above the scan was planned for that number.

**Remedy.** A function you wrote takes its estimate from its `ROWS` clause: `ALTER FUNCTION ... ROWS n` (or `CREATE FUNCTION ...
ROWS n`) with the typical result size. For a function of Postgres' or an extension's, put its result in a subquery or a CTE the
planner can see through, or filter and join in a form that does not depend on the estimate.

## P20 Triggers dominating the statement

**Warning**, under `ANALYZE`. The triggers that fired took at least 30% of the execution time: the statement spends its time in
them, not in the plan the tree draws. The Plan page plans a write only as a *pasted* plan - it never runs `EXPLAIN ANALYZE` of
one - so this is judged for plans from `auto_explain`, a log or another session.

**Remedy.** Trigger time is in no node's time. Look at what the slowest trigger's function does for each row it fires on - a
lookup without an index on its key, a write to another table, an audit insert - and whether a statement-level trigger (`FOR EACH
STATEMENT`, with a transition table) can do it once for the whole statement. A foreign key is a trigger too: an unindexed
referencing column makes every delete or update of the referenced row scan the referencing table.

## P21 Generic plan far costlier than the custom plan

**Warning**, from **Compare generic and custom** on Query › Plan - the one insight that judges a *pair* of plans, so it is evaluated
only when the page has both and never on a single plan (a single plan has nothing to be judged against, so it is never listed as
not judged either). The generic plan of the statement - planned with no values - has an estimated total cost of at least ten times
the custom plan's, planned for the values typed in.

A prepared statement runs on a custom plan for its first five executions and then on the generic plan, when Postgres judges the
generic plan's cost not much above the custom plans' average. A generic plan ten times dearer is one a server on
`plan_cache_mode = auto` would still avoid - and one `force_generic_plan` (which a driver or a pooler can impose) would not.

**Remedy.** `plan_cache_mode = force_custom_plan` for the role or the session (`ALTER ROLE … SET plan_cache_mode`) plans every
execution for its values; or give the planner what it lacks - an index, extended statistics, a statement that does not depend on
one skewed value - so the generic plan is a good one.

## Query › Plan: the plan analyzer

The **Plan** page (`plan`, below Query in the rail; `/marten/plan` in Marten Studio) is the console's Explain buttons as a page of
its own. It is behind **`RunSql`** and the write policy, resolved against the database as a whole, exactly as the console is, a
pasted plan included; with `RunSql` off it shows the console's own disabled state, naming the property.

**Three ways to plan.** *Explain* plans the statement and runs nothing. *Explain analyze* runs it, only through the console's
guard, in its read-only transaction and under its statement timeout - a write is refused by the guard, and one the guard misses
(`with d as (delete … returning *) select * from d`) by Postgres (`25006`), so nothing is ever changed. *Generic plan* is for a
statement with `$1`, `$2` placeholders: prepared and explained under `plan_cache_mode = force_generic_plan` (never `EXPLAIN
(GENERIC_PLAN)`, which Npgsql breaks - see below), as a prepared statement is planned with no values. With values typed in for the
placeholders, Explain and Explain analyze plan the statement **for those values**: the statement is prepared, only to read the
types Postgres inferred for the placeholders from `pg_prepared_statements` (shown beside the boxes), and deallocated at once; then
the statement itself is explained with each value bound to it as a parameter - never written into the statement - and the planner,
given a bound value, plans for it as a constant. (A parameter cannot be bound to an `explain … execute`: Postgres does not analyze
a utility statement's arguments with the command's own parameters and answers "could not determine data type of parameter $1",
measured on 14, 17 and 18.) All of it is one read-only transaction; a run that stopped while it held the prepared statement - an
error, a cancel - deallocates it in a transaction of its own. Every run and every refusal is audited as a `RunSql` run, as the
console's are, with `$1` where the value was: a value is never in the audit or the log.

**Options.** `BUFFERS`, `TIMING` and (from 0.10) `VERBOSE` (on by default: it changes the output, never the plan, and it is what gives every relation its schema, so the [facts beside the plan](#facts-beside-the-plan) can be read; an address with no `options` means these three), `SETTINGS`, `WAL`, and from PostgreSQL 17 `MEMORY` and `SERIALIZE`.
The ones that count what a statement does while it runs - `BUFFERS`, `TIMING`, `WAL`, `SERIALIZE` - need Explain analyze, and are
disabled without it with a title that says why; a server older than 17 is told apart after its first plan, and `MEMORY` and
`SERIALIZE` are left out of the statement - and named on the page - on it.

**Paste a plan.** The JSON of `EXPLAIN (FORMAT JSON)`, one plan or the array Postgres prints, from `auto_explain`, a log or another
server, is read by the same reader with its same bounds (2,000 nodes, 200 levels, 4 MB) and drawn with no database read: nothing
is sent, nothing is audited, and the page says the database's own facts are not available for it. The text goes from the browser
to the server in chunks, because a Blazor Server circuit accepts only 32 KB a message by default and a plan of thirty nodes is
more than that. Text-format plans are not read.

**The address** carries the statement, the mode and the options (`?sql=…&mode=analyze&options=buffers,timing,verbose`), so a link
reopens the page with the editor filled - and never runs it: a run needs a click. The plan and the values of the placeholders are
never in it. The console's **Open in Plan** link carries the statement there; the console keeps its inline plan panel.

**Views of one plan.** The tree stays the default. *Node table*: every node as a row - its own time (or cost), rows, estimate,
the misestimate as `×N`, loops, shared hit and read, temp read and written and I/O time - sortable by clicking a header. A
node's buffers and I/O are its **own**, its total less its children's, because Postgres prints them cumulatively. Selecting a row
selects the node in the tree and the other way round. *Summary strip*: planning and execution time, JIT and its share, the
triggers' time, rows returned, shared hits and reads and the cache hit ratio, temp written, and - when asked for - WAL, planning
memory and serialization; blank where Postgres printed nothing. *Critical path*: from the root, repeatedly the child with the
largest inclusive time (or cost, when the plan was not timed), marked `critical` in the tree and the table. *Heat*: a node's bar
coloured by its own time, its row misestimate, its buffers read or its temp I/O, from a four-step scale with a dark theme of its
own - and the value is printed as text, so colour is never the only signal. *Node detail*: every key Postgres printed for the
selected node, grouped as the node, estimate, actual, buffers, I/O and WAL, workers, sort/hash/memoize and JIT, each with a
one-line explanation of what it means.

**Export.** *Copy JSON*, *Copy as Markdown* (the summary, then the node table, as GitHub tables, for a pull request or an
issue) and *Download plan.json*. *Download for speedscope* saves the plan as `plan.speedscope.json`, in
[speedscope](https://www.speedscope.app)'s file format: one frame for each node (named as the tree labels it, plus its index in
pre-order, `[#3]`), and one sampled profile for each measure the plan has - *Self time* (analyzed with timing), *Self cost*,
*Shared blocks read* and *Temp blocks written* (each when buffers were printed and not all zero) - the weight of a node being its
own value, the same self time or cost the tree shows. The file is saved by the browser; there is no link to the viewer, which is
opened by hand at speedscope.app (the file stays in the browser). A plan from Operations › Statements gets no export, as its
masked form is not offered raw.

## Comparing plans

From 0.10 the Plan page keeps at most two plans, **Before** and **After**, and draws what changed between them. *Keep as Before*
pins the plan on screen; the next plan - one this page runs, or one pasted - becomes After, and the **Compare** section opens
under it. *Clear Before* returns to the page with one plan. Nothing is kept across circuits, and a plan is dropped when the
database in the header changes.

**The diff.** The two plans are lined up node by node by a pure function that reads nothing from a database - so a pasted plan
compares as well as a run one. Nodes are paired by *path* first: the roots, and then two children at the same child index
whenever they are alike (the same node kind, the same relation, function or CTE, or both joins - a `Nested Loop` that became a
`Hash Join` is one node whose kind changed). The nodes still unpaired are lined up by a longest common subsequence over their
(node type, target) pairs, so a subtree that moved or was wrapped in a new node still finds its scans; then any two nodes the
sequence could not keep in order - siblings that swapped places - are paired with their equal; what is left is added or removed. For each pair the table shows the kind (and when it changed), the change of total cost, of inclusive time (when both plans
were analyzed with timing) and of the planner's row estimate. At most 2,000 nodes of either plan are read, as the reader reads.

**Four things fill Before and After for you:**

- *What if? in place.* Where a plan suggests an index (P04's sequential scan, P05's nested loop, P10's filter that discards most of
  what an index found), the panel under the insights offers it, and **What if?** plans the page's own statement, with its values,
  before and after that one hypothetical index (hypopg, as [What if? A hypothetical index](#what-if-a-hypothetical-index) below) -
  Before is the plan now, After the plan with the index. Only an index the studio's builder writes from a table and columns is
  imagined, built again on the server; both plans are plain plans (a hypothetical index serves `EXPLAIN` alone, so *Explain analyze*
  is planned plain here); `hypopg_reset()` runs on the same connection before it goes back to the pool. Without hypopg the panel
  says so. P17's remedy - an expression index, or the parameter's type - is not an index the builder writes, so it is not offered here.
- *Settings what-if.* A **Settings** panel lists the planner settings the server has from a fixed list - `work_mem`,
  `hash_mem_multiplier`, `random_page_cost`, `effective_io_concurrency`, `jit`, `max_parallel_workers_per_gather` and the `enable_*`
  family - never a name typed in. Pick up to eight and give each a value (checked here by the setting's type and range, and by
  Postgres, which has the last word: a value it refuses is an ordinary error - but see the ceilings below); **Plan with these settings** keeps the plan on
  screen as Before and plans the same statement again, with `select set_config(name, value, true)` - name and value both bound as
  parameters - inside the same read-only transaction, before `EXPLAIN`, as After. The settings last for that transaction and are
  rolled back with it. They are named in the audit entry of the run.
  **Two memory settings have a ceiling, in every mode:** `work_mem` is at most **1 GB** and `hash_mem_multiplier` at most **8**.
  Under *Explain analyze* a huge `work_mem` or multiplier lets a hash join or a sort that would have spilled to disk stay in memory,
  and the server can run out of memory before the statement timeout stops it. A value above its ceiling is **refused before a
  connection is opened**, with a message that names the ceiling - never silently reduced - and the page says it before anything is
  sent. The studio reads a memory value as Postgres does - a whole number and an optional unit, `B`, `kB`, `MB`, `GB` or `TB` (the
  units are case-sensitive; a bare number is kilobytes) - and a value it cannot read (`1.5GB`, `1gb`, a word, anything with a
  statement in it) is refused rather than passed on to be read. The ceiling is the studio's, not Postgres's: nothing on this page
  goes above it, and the same statement run in the SQL console is the console's own business.
- *Compare generic and custom.* For a statement with `$1`, `$2` placeholders, one action plans it generically and for the values
  typed in (two runs, two audit entries), with the generic plan as Before and the custom plan as After, and judges the pair with
  [P21](#p21-generic-plan-far-costlier-than-the-custom-plan).
- *Statement timeout.* A field beside the options takes seconds for **this run's** `statement_timeout`; it is clamped on the server
  to the host's `QueryTimeout` and never above it, and empty means the host's own. The run says which timeout it held.

None of this adds a capability or a connection: every plan on the page is one `RunSql` run through the console's gate, guard and
read-only transaction, audited as one.

## Facts beside the plan

Under the tree, the **Facts** panel lists what the database knows about the relations the plan names and the columns it uses, and
four insights ([P15](#p15-stale-statistics-behind-a-misestimate) to [P18](#p18-no-partition-pruning)) judge the plan against them.
They are read **once, after the plan**, for a plan this page ran - never for a pasted plan, which reads nothing, and never on a
poll - in the console's own read-only transaction, under its statement timeout and `lock_timeout`, as the console's role
(`SqlConsoleRole`). They are the page's second step, not a second gate: the whole read is part of the plan's `RunSql` operation,
audited as one more `RunSql` entry ("RunSql plan facts") that names no relation.

**What is read, and how.** Everything is `pg_catalog` and the statistics views, by oid, and **locks nothing**: no size function and
no deparse (`pg_get_indexdef`, `pg_get_expr` with a relation and `pg_get_partkeydef` open the relation and queue behind a
migration's `ACCESS EXCLUSIVE`, D27). A relation is named by the schema and name the plan printed - which only a plan run with
`VERBOSE` carries - which Query › Plan asks for by default, and a visitor who unticks it is told the facts need it: a scan that names none says so and is not resolved - and resolved by a join of `pg_namespace` and `pg_class` on the exact names, which takes no lock and, unlike `to_regclass`, never fails
for a schema your role has no `USAGE` on (a view's table, say);
the names go in as parameters, never into the statement. At most 50 relations are read; the rest say "not read".

- **Per relation:** `reltuples`, `relpages`, `relallvisible`; when it was last analyzed and vacuumed, by hand and by autovacuum,
  rows changed since the last analyze and dead tuples; its indexes with the columns the Schema screens show (keys, access method,
  size, scans, last scan from PostgreSQL 16); its extended statistics; and, for a partition, its partitioned parent, how many
  partitions that has and its partition key.
- **Per column** the plan filters, joins, sorts or groups on (at most 32 for each relation): its type and `n_distinct`, `null_frac`
  and `correlation` from `pg_stats`. A column with no statistics yet says "not analyzed".
- **Settings:** the plan's own `SETTINGS` list first, then the server's planner settings and any `enable_*` switched off, as the
  Server page reads them.

**What a relation is judged by.** The relation itself, and **every table above it** in `pg_inherits`, walked to the root inside the same
statement (never with `pg_partition_root`, which opens the relation). A hidden document type's per-tenant partition is named
`mt_doc_<type>_<tenant>`, which by its name alone is Marten infrastructure and no hidden table, so it is its root that is hidden - and
a partition whose root is in a schema you may not see is withheld however its own schema reads. A withheld record keeps neither name.
A column's type is masked as the database browser masks one (`‹withheld›` for a schema you may not see), and so is an extended
statistics object in such a schema.

**The gate, in this order.** `RunSql` and the write policy first, as for the plan itself. Then, before any fact is read, every
relation is resolved to its oid and judged: **a relation in a schema whose structure you may not see** (the studio's
`BrowsableSchemas` and the database browser's rules, D27), a Postgres system schema included, **is shown as `‹withheld›`, with
no facts and no count**, and so is a hidden document type's table. Only then are the facts of the rest read. A plan's own text -
its `Filter` and `Index Cond` - is what you wrote and ran, and is unmasked as the console's plans are; the facts are what the
database adds to it, and so are gated.

**Values are data, and a gate of their own.** `most_common_vals`, `most_common_freqs` and `histogram_bounds` are values from the
table's rows. They are read only for a relation the database browser's **row gate** admits - `BrowseDatabase` on, the write policy
asked with no tenant, the schema browsable, the relation one the browser reads rows of - and each such read is audited as a
`BrowseDatabase` read naming the relation, once per relation per plan. Without it the panel says "values need `BrowseDatabase`".
**A Marten document or event table's values are never shown** (D27: Marten's tables are never read raw; their rows are a `data`
jsonb and Marten's columns): its structure and its column statistics are, its values are not. At most ten relations' values are read
for one plan.

**Per node.** The node table gains a **Facts** column - for a scan, its relation's estimate and when it was analyzed - and the
selected node's detail lists its relation and, for each column it filters, joins, sorts or groups on, that column's statistics (and
its values, where they were read). The console's inline plan and the Statements drill-in show no facts in 0.10.

## The console's EXPLAIN ANALYZE

`EXPLAIN ANALYZE` runs the statement it explains. The console runs it only under `RunSql`, for a statement its guard
accepts (`select`, `with`, `table`, `values`), inside the same read-only transaction and `statement_timeout` as any other
statement (D13). A write the guard does not spot - `with d as (delete … returning *) select * from d` - comes back from
Postgres as `25006: cannot execute DELETE in a read-only transaction` and changes nothing, on every major from 14 to 19.
Each run is a `RunSql` run in the audit, refusals included, logged as event 9204 or 9205 with the statement it sent. It is
never run from the Statements page or from Mode A, whose plans are plain.

## The Statements drill-in's plan

The plan of a `pg_stat_statements` text is PostgreSQL's **generic plan**: the text, whose constants are `$n` placeholders,
is prepared as `marten_studio_plan` and explained (`VERBOSE`, `FORMAT JSON`) under `plan_cache_mode = force_generic_plan`,
then deallocated, inside a read-only transaction under `statement_timeout` and `lock_timeout`, as the console's role
(`SqlConsoleRole`, as D29 says). It is T02's mechanism, and it works on every supported major.

**Why not `EXPLAIN (GENERIC_PLAN)`.** PostgreSQL 16 added it, and in `psql` it plans a `$1` text on 16 to 19 (and fails
`42P02` on 14 and 15). Through Npgsql it plans nothing: Npgsql sends every command through the extended protocol, so
the server counts the text's placeholders as the command's own parameters, and the bind fails with `08P01` when none is
supplied and folds the values in as constants when any is. Prepared under `force_generic_plan`, the statement is planned
with no values whatever `EXECUTE` passes - the same generic plan `GENERIC_PLAN` prints.

**No plan, and why.** A text withheld when it was read (it still carries a literal as written, or is not a plain query or
write), withheld from you (a hidden document type, another store's table), one that names a schema you may not see, cut at
4,096 characters, not a single plain `select`, or one that locks the rows it reads gets no plan, and the drill-in says
which. Planning a write takes the lock its execution would, which no read of this page may take.

**A text that names a schema you may not see is never planned** (0.6.0), nor is a What if? asked of it. A schema-qualified
name resolves whatever the `search_path` is, and the planner expands a view, and inlines a `LANGUAGE sql` function, into
the tables they read: `select … from hr.v_vip`, where `hr.v_vip` is `select … from public.orders where tier = 'vip'`, plans
as a scan of `public.orders` with `Filter: (orders.tier = 'vip'::text)` - the definition of a view the database browser
never shows you. Even a `plpgsql` function's call prints its default arguments. A text is "one that names" a schema when
your own view of it shows `‹withheld›` anywhere: as a qualifier, quoted or not, but also inside a string literal (which can
be a reference, as `'hr.v_vip'::regclass` is) or as a word that happens to spell the schema's name. While any schema is
withheld from you, a text that spells a name in Unicode escapes (`U&"…"`) is not planned either, because the mask cannot
read it.

**Masked for you.** The plan is explained with `VERBOSE`, so every relation it reads carries its schema. A plan that reads
a table in a schema you may not see - through a view, say - is not shown at all, and nor is one that names a hidden
document type's table or another store's you may not see. Otherwise every name and condition in it is masked as the
statement's text is (`‹withheld›` for a withheld schema), and the transaction's `search_path` is the console role's own
less every schema you may not see, so a name the plan prints from such a schema carries its schema and is masked too. A
statement that names a table only through one of those schemas then fails to plan, and says so.

**Known limit.** A view or a `LANGUAGE sql` function in a schema you *may* see, that itself reads or calls into one you may
not, is expanded or inlined like any other: a plan of a statement that names it shows its conditions, and a scan of a
withheld schema's table among them still hides the whole plan. The database browser refuses such a view's query, so a host
whose views cross schemas this way should keep `ViewDatabaseActivity` for people who may read both.

**Audited.** Each plan is an Activity entry, "Plan database statement", naming the `queryid` and never the text.

## What if? A hypothetical index

Where the studio suggests an index, [hypopg](https://github.com/HypoPG/hypopg) can show whether it would help. Under a plan
that suggests one, **What if?** plans the same statement again with that one index imagined - an index the planner sees,
in this one session, and the table never gets - and draws the plan now and the plan with the index side by side, with the
estimated cost of each and whether Postgres used the index (it is named `<oid>btree_…` in the plan). Both are plain plans:
a hypothetical index serves `EXPLAIN`, never `EXPLAIN ANALYZE`.

**Which index.** Under a plan, the index on the plain columns a sequential scan's filter compares, equality first (P04's
shape), and on the join key a nested loop's inner side reads whole (P05's) - written by the same builder as every
migration remedy, every name quoted. A filter that compares an expression, a JSON member or a `LIKE` pattern suggests
none: no btree on its columns would serve it, and hypopg imagines btree, BRIN, hash and bloom indexes, never GIN or GiST.
For a Marten statement the index is exactly the one Marten would create for the advisor's `StoreOptions` line, as Marten
writes it: LINQ mode's computed index on the member (`Duplicate` adds a column hypopg cannot imagine, so it stands in
with the computed index `Index(x => x.Member)` would create, and says so), and on Documents `IndexLastModified()`,
`SoftDeletedWithIndex()` or the index on a duplicated field that has lost its own. The Documents list reads a JSON member
with `#>>`, which no index Marten declares serves, so it offers none for a JSON member: LINQ mode plans Marten's own
statement for that. The server only ever imagines an index it would have offered for that statement itself.

**Where, and behind which gate.** The gate of the plan it accompanies:

- the SQL console's Explain plans (`RunSql`, audited as a `RunSql` run by the statement);
- Query's Marten where clause (Mode A's plan, through the console's gate);
- the Statements drill-in (the page's gate, planned as `SqlConsoleRole` under your own `search_path`, masked like the
  drill-in's plan, and audited by `queryid`);
- LINQ mode's index advice and the Documents list's (`RunLinqQueries` for the database as a whole, as LINQ mode's plan is).

**Nothing is created, and nothing is left behind.** It runs in a read-only transaction under `statement_timeout` and
`lock_timeout`, and never runs `CREATE EXTENSION` or creates an index. A hypothetical index lives in the backend and
survives `ROLLBACK`, and a pooled backend is the host's next caller's, so `hypopg_reset()` is the transaction's last
statement; a what-if that fails or is cancelled resets it again on the same connection before it goes back; and one whose
reset fails ends its own session (`pg_terminate_backend(pg_backend_pid())`), so the backend - and the index with it -
never returns to the pool. The studio does not clear the pool: that would close the host's idle connections.

**When hypopg is not there.** The studio reads `pg_extension` and the privileges of the role it plans as, once a minute
per database, and says which: hypopg is not installed (installing it - its package on the server, then
`CREATE EXTENSION hypopg` - is the host's decision), or it is installed and that role may not use it (it needs `USAGE`
on hypopg's schema and `EXECUTE` on `hypopg_create_index` and `hypopg_reset`). On a managed platform, whether hypopg is
offered at all is in the provider's list of supported extensions.
