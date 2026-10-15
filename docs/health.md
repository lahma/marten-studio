# Marten Studio: the health advisor

The Health page (`/marten/health`) says what is wrong with the database a studio is pointed at, how sure it
is, and which line of the host's `StoreOptions` would change it. It is an advisor, not a monitor: it pages
nobody, it keeps an hour of history in memory and no more, and it runs only while somebody is looking.

This page is the index. Each area has a file of its own with one section per check, and each section's
heading is the anchor the page's "About" link opens:

| Area | Codes | File |
|---|---|---|
| Daemon and high water | `D` | [`health/daemon.md`](health/daemon.md) |
| Events | `E` | [`health/events.md`](health/events.md) |
| Tables and indexes | `T` | [`health/tables.md`](health/tables.md) |
| Connections and sessions | `C` | [`health/connections.md`](health/connections.md) |
| Configuration | `K` | [`health/configuration.md`](health/configuration.md) |

An area's file is written by the packet that adds its first check; until then its link has nothing behind it.

## What a finding is

Every finding has a code, a severity, a title, the evidence behind it with the time it was measured, the SQL
that read it (Show SQL - parameter names, never values), a caveat that says how sure it is ("counters since
`stats_reset` at 09:14", "cannot see sessions of other roles"), and a remedy.

- **Critical**: something is broken now, or will be soon.
- **Warning**: something is wrong or risky and is worth fixing on purpose.
- **Info**: a setting that would let the store - or the advisor - tell you more.

A remedy is always a `StoreOptions` line for the host's own registration, never DDL. An index, a storage
parameter or a setting the studio created itself would be schema the host's configuration does not know
about, and the next `CreateOrUpdate` would take it away again. A remedy that needs a Marten newer than the
studio's floor says so ("Marten 9.47+") and, on a host that runs an older one, what that host runs.

"Mute in this browser" hides one finding on one database for you, in your browser's `localStorage`. It is
never written to the database and never hides anything from anybody else.

## What it costs

Checks come in four cost tiers:

| Tier | Reads | Runs |
|---|---|---|
| 0 | The store's configuration and the process's own state. No I/O. | With every sweep |
| 1 | The catalog and the statistics views (`pg_class`, `pg_stat_*`, `pg_locks`, `pg_stat_activity`). | With every sweep |
| 2 | A bounded slice of the data itself. | Only behind **Run deeper checks**, for a visitor the store policy allows the database as a whole; cached for five minutes |
| 3 | `pg_stat_statements` and `EXPLAIN`. | Only for a visitor through the activity gate, cached for five minutes |

Who may *start* a costlier sweep is decided by the studio's service, not by the page's button: a tier-2 sweep
reads a slice of every table it checks, so a visitor the store policy admits for one tenant only cannot start
one, whatever their browser sends. They see the button off, the policy it needs, and the results of a run
somebody else started - through the same per-finding gates as everything else.

A sweep runs **only when the Health page or the Overview asks**, and its result is shared by everybody
watching that database until `MartenStudioOptions.HealthInterval` (a minute by default) has passed: ten open
tabs cost one sweep per interval, not ten. There is no hosted service and no timer of the advisor's own - a
studio nobody has open checks nothing. The navigation badge reads the last sweep and never starts one.

Every statement runs in the studio's read-only transaction under `statement_timeout` and `lock_timeout`,
reads the catalog and takes no lock beyond what a catalog read takes, and never runs DDL. A check that fails
is a "could not check" row on the page; it never hides the other checks. A check that cannot run here for a
reason that is the host's choice - no extension, no privilege, an old Postgres - says so and is logged at
Debug, never as a warning.

The page's **Studio cost** line is the database time the studio's own read-only sessions took in the
selected database in the last five minutes: the health checks, the SQL console, the Query page's Marten mode,
the database browser and the schema and relationship reads. The documents, events and projections screens'
own reads are not counted yet. It is a database-wide number, so it is shown at the same gate as one.

## Who sees what

The sweep is about the database; what each visitor is shown of it is decided per visitor:

- A configuration finding (tier 0) is shown to anybody the store policy lets see the scope they selected -
  it says nothing the Configuration page does not. That is a rule the page is kept to rather than a hope:
  what K01 prints (the database's `AutoCreate` and the host's environment name) and what K02 and K03 print
  (the two tracking switches) are on the Configuration page's store, Events and Runtime cards.
- A database-wide number needs the store policy for the database as a whole, asked with `TenantId = null`.
- Anything that names a session, a process id, a role, a client address, an application, a statement or a
  tenant needs `MartenStudioOptions.Capabilities.ViewDatabaseActivity` - off by default, off under
  `ReadOnly` - and the write policy, asked with `Capability = "ViewDatabaseActivity"` and `TenantId = null`.
  Without it a visitor still sees the counts and ages, never the names.
- A document type `IsDocumentTypeVisible` hides never appears in a finding.
- A number one check measures can become another's evidence through the history, and only downwards: each
  number keeps the gate of the check that read it, and a check is handed only the numbers read at or below
  its own gate. A count of sessions by role never turns up in a finding every visitor sees.

A check that could not check says why in the studio's own words - which name a code, a count, a type or a
SQLSTATE, never a subject, a tenant or a table. After that, and only under its own label:

- **Postgres said:** Postgres' own message, from a Postgres error and nothing else, with every schema the
  visitor may not see masked out of it by the same rule the database browser uses - or withheld outright
  when that cannot be decided.
- **The connection error said:** the words of a connection failure (Npgsql's or the network's), which name
  the database's address, so only for a visitor through the database-wide gate.

Any other exception's message - a bug in a check, a library's own complaint - is not on the page at all. It
is the author's of whatever threw, it may name something the page withholds, and it is in the server log
under event 9222.
