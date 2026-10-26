# LINQ mode

LINQ mode is the third mode of the Query page. You type the predicate your own code would write, and an
optional ordering:

```text
Status == "Open" && Total > 100 && Customer.Name.StartsWith("A")
Total desc, Name
```

The studio parses it with JasperFx's Dynamic LINQ (`JasperFx.Linq.DynamicQuery`), applies it to Marten's own
`Query<T>()` for the document type you picked, and runs the SQL Marten builds from it. The tenant filter, the
soft-delete filter, a subclass's `mt_doc_type` filter, enum storage and duplicated fields are therefore Marten's,
exactly as your application gets them.

The page shows the rows, the statement Marten built, its plan (`EXPLAIN`, never `ANALYZE`), and index advice: the
member each condition reads, whether an index can serve it, and the `StoreOptions` line that would declare one,
such as `options.Schema.For<Order>().Duplicate(x => x.Customer.Name);`.

## Turning it on

LINQ mode is off by default.

```csharp
builder.Services.AddMartenStudio(options =>
{
    options.Capabilities.RunLinqQueries = true;   // or MartenStudioCapabilities.All()
});
```

- **It needs Marten 9.48 or later**, which brings JasperFx 2.84 and its `DynamicQuery`. On an older Marten the mode
  is absent, and the page says which Marten it needs and which one the host runs.
- **`ReadOnly` turns it off** with every other capability.
- **It is authorized like a write** (D13). `WriteAuthorizationPolicy` is asked with `RunLinqQueries` on the
  resource, after the store policy. See [Who may run what](#who-may-run-what).
- **Grant it to people trusted with the store's data.** It changes nothing, but it evaluates an expression somebody
  typed inside your application, under the allow-list below, and it reads any document of a type they can see.

## The dialect

| Want | Write |
|---|---|
| equality | `Status == "Open"` or `Status = "Open"` |
| inequality | `Quantity != 5` or `Quantity <> 5` |
| comparison | `Total > 100`, `PlacedAt >= "2024-01-01"` |
| logic | `and` / `or` / `not`, or `&&` / `\|\|` / `!` |
| null | `Notes == null`, `Notes != null` |
| text | `Name.StartsWith("A")`, `Name.EndsWith("x")`, `Name.Contains("tech")`, `Name.ToLower() == "ada"` |
| case-insensitive | `Name.Equals("ada", StringComparison.OrdinalIgnoreCase)` |
| a set | `Status in ("Open", "Shipped")` |
| nested | `Customer.City == "Austin"` |
| collections | `Tags.Any(it == "vip")`, `Lines.Any(Quantity > 5)`, `Lines.Count() > 2`, `Tags.Contains("vip")` |
| modulo | `Total % 2 == 0` |
| ordering | `PlacedAt desc, Id` |

- Member names match without case, against the C# type rather than the JSON.
- An enum member compares with its name as a string (`Status == "Shipped"`).
- A date literal is read as UTC unless it carries an offset (`"2024-01-01T09:00:00+02:00"`), whatever time zone the
  server is in. JasperFx 2.84 to 2.90.2 means the same but reads a date with no offset in the server's zone when it
  is compared with a `DateTime` member (its `DateTimeIsParsedAsUTC` setting hands Dynamic LINQ's own converter the
  job before JasperFx's UTC converter is asked), so the studio measures that once and puts each such date back to
  the one the text names. A predicate holding two dates the server's zone reads as one moment but that name
  different ones (`"2024-01-01"` beside `"2023-12-31T22:00:00Z"` on a server two hours east) is refused rather than
  guessed. JasperFx 2.91.0 (Marten 9.51.0, jasperfx#1024) reads them as UTC itself; the measurement then finds
  nothing to correct, and neither the correction nor that refusal happens.
- Values are literals: `"text"`, `'c'`, `42`, `3.5m`, `-1`, `true`, `null`.

There is no arithmetic (other than `%`), no lambda arrow, no method of your own, no type name, no conditional and no
`@0` placeholder. A string has no escapes: a backslash is refused.

### Shapes Marten refuses honestly

Marten's provider translates most of this, and refuses the rest with a message the page shows ("Marten cannot
translate this query: ..."). Two shapes would run and return the wrong rows, so the studio refuses them first:

- **An ordering comparison or an ordering on an enum stored as its name.** With `EnumStorage.AsString` (or a
  duplicated field stored as text), `Status > "Open"` would compare alphabetically. Use `==`, `!=` or `in`.
- **`!=`, or `not`, on a member that can be null**, with no word on what null should do: SQL leaves those rows out
  where C# keeps them. Write `Notes != "x" or Notes == null`, or `Notes != null and Notes != "x"`. This is JasperFx's
  own `SqlNullSemantics` rule.

## Paging

Pages are read with Marten's own `Skip` / `Take`, which is `OFFSET` paging. The identity column is added to the
ordering (and the tenant column when every tenant is read), as Marten's own diagnostics read does, so a page is the
same page every time.

A page may not start past row 10,000. Past that, narrow the predicate or order it so the documents you want come
first. There is no count: one more row than the page is read to know whether another page exists. That is the
difference from `IDocumentStoreDiagnostics.QueryDocumentsAsync`, which runs an exact count on every page (D8).

**Why not keyset paging.** Keyset paging needs the last row's value of every ordering key, compared in SQL with the
same null placement the ordering uses. Marten builds its `ORDER BY` from the mapping (`d.data ->> 'Name'`,
`CAST(... as numeric)`, a duplicated column). The studio would have to read those expressions back out of Marten's
statement text, or evaluate the ordering members in this process over deserialized documents. Neither is honest, so
it does neither.

Each document is shown up to `MaxInlineDocumentBytes` characters, and a page up to 8 MiB of document text. A
document past either limit is listed by its id, with a link to open it in Documents.

## Who may run what

| Question | Asked of | Why |
|---|---|---|
| May LINQ mode run at all? | `Capabilities.RunLinqQueries`, `ReadOnly` | Process-wide (D4). Refused before anything is resolved. |
| May this visitor read the rows of a **conjoined** collection? | Store policy, then write policy with `RunLinqQueries`, for `(store, database, tenant)` | The statement Marten builds is narrowed to that tenant. |
| May this visitor read a collection **not divided by tenant**, from a tenant scope? | The same two policies, for `(store, database, null)`, unless the database is that tenant's alone | Nothing narrows that table by tenant, so the read reaches the database as a whole (D27, "reach, not scope"). |
| May this visitor see the **plan**? | The same two policies, for `(store, database, null)` | A plan's estimates count every tenant's rows, and a table partitioned by tenant lists its partitions. That list is the tenant list (D27). |

Why the read may be asked with the tenant at all, when the SQL console, the Mode A lift and `EXPLAIN` are always
asked with `TenantId = null`:

- The session is opened with `SessionOptions.ForDatabase(tenant, database)` (hard rule 15).
- Marten writes its own `tenant_id = :p` with the session's tenant bound.
- Nothing in a predicate can widen it. `AnyTenant()`, `TenantIsOneOf()` and `MatchesSql()` are extension methods,
  and JasperFx's parser has no custom types and so no extension methods. The guard refuses every method not on its
  list anyway.
- With no tenant selected, the studio adds `AnyTenant()` itself. The question was then asked with
  `TenantId = null`, which is the database as a whole, which is what is read.

The plan needs `RunLinqQueries`, not `RunSql`, because the statement explained is Marten's, not text somebody typed.
A visitor the policies refuse the plan still gets the rows. The panel says why there is no plan.

## The audit

- **Every refusal is recorded**, at every stage: capability, policy, a scope that names no store, database or tenant
  the process has, a store that will not build, availability, type (a visibility delegate that throws is refused as
  the type it was asked about, and recorded), the screen, the parse, the guard, Marten's translation, Postgres.
- **A success is recorded once per signature** in a circuit, on whichever page it starts. A signature is the store,
  the database, the tenant, the type, the predicate, the ordering and the deleted filter. A page turn, or a re-run of
  the same query, is not a new opening. This follows the database browser's first-read rule (D27).
- **What is written down is the query, capped.** The Activity entry's target is the type's alias, the predicate and
  the ordering on one line, cut at 200 characters. The application log carries the same text cut the same way - the
  alias, the predicate and the ordering each at 200 characters - and never the whole of it:
  - event **9260**: a run;
  - event **9261**: a refusal, naming its stage;
  - event **9262**: a Postgres failure, by SQLSTATE;
  - event **9263**: an unexpected failure, with its stack;
  - event **9264** (Debug): index advice that could not be read, which costs the advice and nothing else.
- **The literals are part of it.** A value typed into a predicate - an email address, an order number, a name - is
  in the Activity entry and in 9260 and 9261, as a value typed into a SQL console statement is in that statement's
  entry and in 9204 and 9205. Grant `RunLinqQueries` with that in mind, and treat the application log as holding what
  people searched for.
- **A refusal at the capability stage records no text.** With `RunLinqQueries` off the page offers no Run button, so
  a run that reaches the service anyway was sent by a scripted circuit. Its entry names the alias only, and 9261 says
  `(text not recorded)` where the predicate and the ordering would be. Event 9202 names the capability, as it does for
  every capability refusal.
- An entry is recorded under `RunLinqQueries`, so the Activity screen shows it only to a visitor who may make the
  same read. For a collection not divided by tenant, read from a tenant scope, the entry is recorded against the
  database as a whole (`TenantId = null`), because that is the read it was.
- A refused plan is its own entry, `LinqExplain`, recorded against `(store, database, null)`.
- The read and its plan each run in a `ReadOnlySqlSession`, so both are counted in the Health page's studio-cost line.

## Error text

- **Screen and parse refusals** are written for the person who typed the text. They come with the character to put
  a caret under, when there is one. A refusal by a rule reads as a sentence about the predicate ("The predicate
  calls string.PadLeft, which LINQ mode does not allow."). A syntax error is Dynamic LINQ's own sentence, after
  "The predicate could not be parsed:".
- **Marten's translation refusals** are shown by their first line, capped at 300 characters.
- **Anything unexpected** is shown by its exception type only, whether the service caught it or the page did. The
  stack goes to event 9263.
- **No refusal names a type the graph leaves out or the host hides** (see the graph, below).
- **Postgres' words are masked as the database browser masks them** (D27). Every schema the visitor may not see is
  masked, except inside the echo of a value the statement bound. When the schema list cannot be read, Postgres'
  words are withheld outright and only the SQLSTATE is shown.

## Threat model

### What could go wrong

1. **Code execution in your application.** Marten's LINQ provider evaluates, in this process, every part of an
   expression that does not reference the document:
   - `SimpleExpression` sends any such operand to `LinqInternalExtensions.ReduceToConstant`;
   - that reads static fields and properties by reflection;
   - otherwise it compiles the subtree with FastExpressionCompiler and invokes it.

   This was read at Marten `V9.39.0`, `V9.50.2`, `v9.51.0` and `V9.54.1`, and a test runs it (see the tests below). From
   9.51.0 (#5712) a parameter-free array initialiser is sent the same way. If a parsed predicate
   could contain `Status == System.IO.File.ReadAllText("c:/x")`, your server would run it. That is the shape of
   CVE-2023-32571 in System.Linq.Dynamic.Core: code execution through reflection, fixed in 1.3.0. CVE-2024-51417
   (fixed in 1.6.0) let a parsed expression reach members of reflection types and static members.
2. **Code that runs while the text is parsed**, before any allow-list exists. System.Linq.Dynamic.Core 1.7.4 does
   some work during the parse itself:
   - it runs a `TypeConverter` on a string literal compared with a member, through
     `TypeDescriptor.GetConverter(memberType)`;
   - it runs the same for type-name syntax (`Guid("...")`, `Uri("...")`);
   - it compiles and invokes an indexer on a constant;
   - it parses enum names;
   - it looks up every public enum in the core library by name.
3. **DDL on a read.**
   - Marten's `ToCommand()` runs `EnsureStorageExists` for the queried type, and so does every LINQ execution path.
   - For a type this process has not used yet, under the default `AutoCreate.CreateOrUpdate`, that creates the schema.
   - It also calls `AllObjects()`, which provisions `mt_hilo` (hard rule 14).
   - It then applies a migration. `CreateOrUpdate` is not additive.
4. **Resource exhaustion.** Deep nesting in a recursive-descent parser can overflow the stack, which no .NET process
   can catch. Huge literals, huge trees, slow statements and huge documents are the other ways in.
5. **Reading data the visitor may not see.**
   - Another tenant's rows.
   - Soft-deleted rows.
   - A type the host hid with `IsDocumentTypeVisible`, directly or through a navigation. (A hidden *subclass*
     under a visible root is the exception, and is listed under what remains.)
6. **Leaking server internals** through error text.

### How each is closed

The pipeline, in order. Everything above Marten's translation happens before Marten sees the expression.

| Stage | What it does | Closes |
|---|---|---|
| Gate | Capability, then the store and write policies, then reach, all before any text is looked at | 5 |
| Availability | Marten 9.48+, a `DynamicQuery` and a Marten LINQ builder the studio recognises, run-time code generation, and System.Linq.Dynamic.Core 1.7.4 or later. Anything else is absent, never guessed at | 1, 2 |
| **Screen** | A lexical pass over the text, before any parser (below) | 1, 2, 4 |
| **Parse** | JasperFx's `DynamicQuery` under the strictest policy the feature can live with (below). The studio's guard is the policy's *first* shape rule | 1, 4 |
| **Guard** | The studio's own walk of the whole composed expression, independent of JasperFx (below) | 1, 5 |
| Translate | Marten's SQL, built through `IMartenLinqQueryable.BuildLinqParser()` and `LinqQueryParser.BuildDiagnosticCommand`. That is what `ToCommand` calls, minus `EnsureStorageExists`. The statement must read only the queried type | 3, 5 |
| Statement guard | Marten's statement must pass the console's `ReadOnlySqlGuard`: one statement, a read, no function that reaches outside the transaction | defence in depth |
| Run | `ReadOnlySqlSession`: `SET TRANSACTION READ ONLY`, `statement_timeout = QueryTimeout`, lock and idle timeouts, the optional `SqlConsoleRole`, rollback whatever happened. Sequential reads, per-document and per-page caps | 4 |

A link carries the query (`?mode=linq&type=…&predicate=…&orderby=…`) and fills the boxes. It never runs one: a
query runs only when the person looking at it presses Run, so a crafted link cannot run something in somebody
else's session.

#### The screen

The screen is stricter than the parser, never looser. Anything it cannot classify is refused. It is also the only
wall in front of the parse itself: every rule after it walks a tree Dynamic LINQ has already built.

- **Every name is bound where the parser will bind it, and must be the graph's member there.** A bare name binds on
  the current `it`: the document, or inside `Any(…)`, `All(…)` and `Count(…)` the collection's element. Inside
  `Contains(…)` it is still the document, because Dynamic LINQ reads that argument where the call is. A name after a
  `.` binds on the type of what is before it. The screen finds the member exactly as Dynamic LINQ's
  `FindPropertyOrField` does: on the receiver and then its base types, instance and static, without case. It then
  requires that member to be the graph's own. So:
  - a member the graph leaves out never passes because some other type in the graph has a member of the same name
    (`Code` on the document, beside an allowed `Holder.Code`);
  - a framework name passes only on a receiver of that framework type: a string's or an array's `Length`, a
    collection's `Count`, a nullable's `HasValue` and `Value`. `it.Value` on a document whose `Value` was left out
    is refused;
  - `it` on its own stands only for the element of a collection of scalars (`Tags.Any(it == "vip")`). The document
    or an object element compared with a literal would be a converter call on that type.

  This is what keeps a host `TypeConverter` from running on the visitor's text: Dynamic LINQ converts a string
  compared with a member through the *bound* member's type, while it parses. The F7 security review found the first
  version of this screen checking names against one flat set of every name in the graph. It proved, with a counting
  converter, that `Code > "probe"` and `it.Value > "probe"` ran the host's converter. Both are now in the hostile-input
  tests, with the converter's count held at zero, beside the proof that without the screen the same texts do run it.
- **The other identifiers allowed:**
  - a keyword on a short list (`and`, `or`, `not`, `null`, `true`, `false`, `it`, `in`; for an ordering, `asc`,
    `desc`, `ascending`, `descending`);
  - a method of the receiver's own type, directly after a `.` and before a `(`. On a string: `StartsWith`,
    `EndsWith`, `Contains`, `Equals`, `ToLower`, `ToUpper`, `ToLowerInvariant`, `ToUpperInvariant`. On a collection:
    `Any`, `All`, `Count`, `Contains`;
  - `StringComparison.<value>`.
- **No two values in a row.** A name, a string, a number or a `)` followed by another of those, or by a `(`, is
  refused. Dynamic LINQ reads a type name followed by a string as a conversion of the string (`Uri "http://…"`),
  which runs a framework converter at parse time.
- **An ordering** is member paths from the document, each step bound on the type of the step before it, each ending at
  a single value.
- **Refused even where the document has a member of that name**, because the parser would not read it as the member:
  - Dynamic LINQ's functions: `new`, `iif`, `isnull`, `np`, `is`, `as`, `cast`;
  - its context keywords: `parent`, `root`;
  - its operator aliases: `eq`, `ne`, `lt`, `mod`, and the rest.
- **Refused characters:** `@`, `$`, `^`, `~`, `[`, `]`, `{`, `}`, `?`, `=>`, `+`, `-` except as a sign, `*`, `/`,
  `&` and `|` on their own, `<<`, `>>`, and control characters.
- **No backslash in a string.** Dynamic LINQ's tokenizer skips the character after `\"` twice, so a screen that read
  escapes any differently would disagree with the parser about where a literal ends.
- **No string directly before `(`.** That is the cast-by-type-name syntax.
- **A single-quoted literal is one character.**
- **Size caps:** 1,000 characters for a predicate and 200 for an ordering, 200 tokens, parentheses 12 deep, and 3
  negations in a row. A ten-thousand-deep nesting never reaches the parser.

#### The policy handed to JasperFx

- `MaxTextLength` is 1,000 for a predicate and 200 for an ordering.
- `MaxNodes` is 200.
- `MaxArguments` is 0: the studio never passes an `@n` value.
- The shape rules, in order:
  1. the studio's guard;
  2. JasperFx's `SqlNullSemantics`, for the predicate.
- JasperFx's built-in allow-list cannot be turned off and still runs. It allows:
  - string `StartsWith` / `EndsWith` / `Contains` / `ToLower` / `ToUpper` / `Trim` / `IsNullOrEmpty`;
  - collection `Any` / `All` / `Contains` / `Count`, each of which must touch the document;
  - no static members, no conditionals, and no construction beyond dates, times and Guids.

  It is identical at JasperFx 2.84.0, 2.87.1, 2.89.0, 2.91.0 and 2.95.1 (2.91.0 changed only how dates are read).
- The parser itself runs with no custom types, no `new`, no type lookup by simple name and no casting to a type
  named in a string. `object` was removed from its predefined types for CVE-2024-51417, so `GetType()` cannot be
  called.
- The guard is first so that it sees each clause's whole body before JasperFx's own `SqlNullSemantics` rule looks at
  any part of it. That rule reads values out of member-of-constant shapes by reflection.
- **Every rule runs after Dynamic LINQ has parsed the text**, not during it: JasperFx parses, then walks the result.
  What Dynamic LINQ evaluates while it parses happens before any rule sees anything: a type's converter run on a
  literal compared with it, an enum name looked up, a constant indexed. That is why those shapes are kept out of the
  text before it reaches the parser - by the screen, and by the graph, which leaves out every type with a converter
  of its own - rather than refused afterwards.

#### The guard's allow-list

This is a closed list. Every other node is refused.

- **Member reads**, only in a chain that starts at a lambda parameter: the document, or the element inside
  `Any` / `All` / `Count`. Only members of the document's graph are allowed, plus a string's or an array's
  `Length`, a collection's `Count`, and a nullable's `HasValue` / `Value`. No static member, and no member of a
  constant.
- **Constants** of the literal types:
  - text, at most 256 characters;
  - the numbers;
  - `bool`, `char`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`;
  - an enum that is the type of a member in the graph;
  - `StringComparison`;
  - `null`.

  No closure, array, `Type`, delegate, queryable or other object.
- **Operators:**
  - `and`, `or`, `not` over booleans;
  - the six comparisons, each with at least one side that reads the document;
  - `%` of a member by a constant;
  - a conversion between scalar types with no user-defined operator;
  - a negated numeric constant;
  - an operator method only on the framework's own scalar types (`string`, `decimal`, the date and time types,
    `Guid`).
- **On a string that reads the document:**
  - `StartsWith`, `EndsWith`, `Contains`, `Equals` with a literal, and optionally a `StringComparison`;
  - `ToLower`, `ToUpper` and their invariant forms;
  - `CompareTo`;
  - `string.Compare` / `string.CompareOrdinal` of it with a literal. That is how Dynamic LINQ spells `<` on strings.
- **On a collection member:** `Any()`, `Any(…)`, `All(…)`, `Count()`, `Count(…)` and `Contains(literal)`. A lambda
  is allowed only as their argument, and its parameter is the element.
- **An in-list**, `Status in ("Open", "Shipped")`: Dynamic LINQ writes it as `Enumerable.Contains` over an array
  initialiser of the literals, with the member as the value looked for. Every element must be a literal of the
  member's own type and the member must read the document. Below Marten 9.51 Marten cannot translate an array
  initialiser; from 9.51 (#5712) it reduces a parameter-free one by compiling it (`ReduceToConstant`, through
  FastExpressionCompiler), which is the evaluation the studio's walk exists to keep out of Marten. So at every version
  the composer builds the array itself, setting each literal's value into it (nothing is compiled), and Marten binds
  it as one parameter (`= ANY(:p0)`). The guard walks the result again and accepts the array value in that one
  position.
- **The composed expression** may be nothing but `Queryable.Where`, `OrderBy`, `OrderByDescending`, `ThenBy` and
  `ThenByDescending` over quoted lambdas of the document type.

So the only parts of an accepted expression that do not reference the document are literals, conversions of
literals, negated literals and an in-list's array of literals. Nothing Marten evaluates in this process can do more
than read a constant.

#### The document's graph

The graph is the members a predicate may name:

- the document's public instance properties and fields;
- those of the objects and collections it holds, four levels down;
- at most 512 members and 64 types.

It leaves out:

- **Framework and library types:** `System.*`, `Microsoft.*`, Npgsql, Marten, JasperFx, Weasel, Newtonsoft. A
  `Type`, a `JsonElement`, a `Stream` or an `Expression` has members that are not data.
- **A type with a converter of the host's own**: declared with `TypeConverterAttribute`, added at run time with
  `TypeDescriptor.AddAttributes`, or supplied by a registered provider. Dynamic LINQ would run that converter at parse
  time on a literal compared with it, so the type is not offered, and the screen refuses any name that binds to a
  member of that type, on whichever receiver it binds. The graph asks
  `TypeDescriptor.GetConverter` exactly as Dynamic LINQ does, once per type, and keeps only the framework's own
  converters. One registered after a type's graph is first built is not seen until the process restarts.
- **Dictionaries, collections of collections, delegates, pointers, open generics, `object`, and names that differ
  only in case.**
- **Every document type the host hides, and everything reachable only through one.** A member is matched by metadata
  token, so a different member of the same name is not let through.

A refusal never names a type the graph leaves out or the host hides. It says "a type LINQ mode does not query"
instead, because the refusal is shown to whoever typed the text.

### What remains, and why it is accepted

- **The parse runs a few framework conversions on literals.** These are Guid, the date and time types, the numbers,
  enum parsing, a `Uri` object built from a string, and a string's own indexer on a string literal. Each only parses
  a literal: none does I/O or reaches a host type. The screen refuses the indexer (`[`), type-name syntax and the
  type-name shorthand (two values in a row) anyway, so these run only for shapes the dialect needs, like a Guid or a
  date compared with a member.
- **The parse instantiates the attributes of a member type it compares with a string.** `TypeDescriptor` reads them
  to find a converter. Marten and System.Text.Json already read the same attributes on every document type.
- **A `TypeConverter` the host registers at run time** through `TypeDescriptor.AddAttributes` or
  `TypeDescriptor.AddProvider` is detected when a type's graph is first built, because the graph asks
  `TypeDescriptor.GetConverter` as Dynamic LINQ does. One registered after that is not seen until the process
  restarts. Such a converter is already exposed to untrusted strings by ASP.NET Core model binding.
- **A hidden subclass under a visible root is read through the root.** In a hierarchy whose root is visible,
  `Query<Root>()` returns every row in the root's table, and a subclass the host hid is among them, documents and
  all. The hidden subclass is not offered, and cannot be chosen or named, but its rows are not filtered out of the
  root's. This is how the Documents screen lists a hierarchy too. A host that must keep a subclass's rows unseen hides
  the root.
- **An in-list's array is the one closed subtree accepted.** The composer builds it from the literals' values,
  without compiling anything, so Marten binds a value rather than evaluating an initialiser.
- **Dates are put right after the parse, not during it.** JasperFx 2.84 to 2.90.2 reads a date with no offset in the
  server's time zone (see [The dialect](#the-dialect)). The studio measures that once per process, with two probe
  dates, and corrects each such constant from the literal the screen saw. A predicate holding two literals the
  server's zone reads as one moment but that name different ones is refused, because the two cannot be told apart
  once parsed. Marten's own diagnostics read (9.48 to 9.50.x) has the same reading and no correction. From JasperFx
  2.91.0 (Marten 9.51.0) the measurement finds nothing to correct and the step does nothing - a test pins that, so a
  JasperFx that read server time again, or a probe that stopped reading at all, fails rather than double-corrects.
- **Marten's internal LINQ builder is reached by reflection.** A Marten that changes it makes the mode absent; it
  never falls back to `ToCommand`. The members are pinned by a test at the floor and at the latest.
- **System.Linq.Dynamic.Core is only as safe as its version.** The mode refuses to run below 1.7.4, which is what
  JasperFx 2.84 requires and NuGet will not let a host go under without suppressing a downgrade error.

### The tests that hold it

- `LinqTextScreenTests` covers every refused shape, and the caret position for each.
- `LinqExpressionGuardTests` covers every node shape, hand-built with no parser in front of it.
  `Marten_does_evaluate_a_closed_subtree_in_process_which_is_the_threat` is the anti-vacuity test: it proves Marten
  runs a probe method the guard refuses.
- `LinqHostileInputTests` runs one list of hostile inputs three ways:
  1. through the screen alone;
  2. with the screen taken away, through JasperFx's parse and the guard;
  3. through the whole pipeline.

  It also holds the parse-time converter shapes, each with a host converter that counts its calls:
  - a name allowed on a nested type but left out on the document (`Code > "probe"`);
  - a framework member's name on an object (`it.Value > "probe"`);
  - a strongly-typed id with a converter on the root, beside nested entities with a `Guid Id`;
  - a `Contains(…)` argument, which binds on the document rather than the element.

  Without the screen, each one runs the converter during the parse. That is the anti-vacuity half. Through the
  screen and the whole pipeline, the count stays at zero. The type-name shorthand (`Lines.Any(Uri "…" != null)`) is
  refused there as well.

  Every one stops before Marten. A control list of ordinary predicates goes all the way to SQL. The same class
  proves a date literal is bound as the UTC date it names on whatever zone the host is in, and that two dates the
  zone cannot tell apart are refused.
- `LinqDocumentGraphTests` proves a converter registered at run time keeps its type out of the graph.
- `MartenLinqCommandTests` proves:
  - `ToCommand` reaches for the database to create the table, and the studio's builder does not;
  - with nothing to create, both build the same statement;
  - Marten binds the session's tenant.
- `LinqQueryLiveTests` runs against a real Postgres. It covers:
  - tenant isolation, soft deletes and hierarchies;
  - no table created for a type nothing has stored;
  - the advice naming the filtered member and its `Duplicate(...)` line;
  - a tenant-scoped visitor refused the plan and a single-tenanted collection;
  - a single-tenanted read from a tenant scope audited against the whole database;
  - dates on the boundary, so a date read in the server's zone would move a row across it;
  - an in-list bound as one array, against enums stored as their names;
  - the read and the plan counted in the studio's own database time;
  - the audit by signature.

## Not yet

- **Saved and recent queries do not cover LINQ mode yet.** A LINQ query is two texts and the saved-query store keeps
  one. The address bar keeps the query (`?mode=linq&type=…&predicate=…&orderby=…`).
- **No `@n` arguments.** Values are literals, so `in @0` is written `in ("a", "b")`.
