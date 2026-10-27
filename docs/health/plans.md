# Plan insights (P)

Every place the studio shows a query plan draws it as a tree, and under the tree it lists what the plan says is wrong with
it: **plan insights**. A plan is drawn in three places:

- **the SQL console** (`RunSql`): its **Explain** and **Explain analyze** buttons run the statement in the editor as
  `EXPLAIN (FORMAT JSON)` or `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)`, and an `EXPLAIN (FORMAT JSON …)` typed by hand is
  drawn the same way;
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

**Five need `ANALYZE`.** P01, P02, P03, P04 and P08 read what only `EXPLAIN ANALYZE` prints - rows returned, a spill, a
lossy page, the rows a filter threw away, the workers given - so a plain plan lists them as not judged rather than as
quiet. P05, P06 and P07 read the planner's estimates when there is nothing better.

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
