# The sample applications

The demo hosts in `samples/`, and what they seed.

## Sample application

```shell
dotnet run --project samples/DataStudio.Sample.Marten
```

Then open <http://localhost:5210>. With no connection string configured, the sample starts a throwaway
PostgreSQL container, prints a loud banner saying it did, and seeds a demo domain chosen to make the
studio's screens worth looking at: a duplicated unique column, a soft-deleted type with a foreign key and
optimistic concurrency, a strong-typed id, a subclass hierarchy in one table, application-assigned string
keys, one type with every optional metadata column on and one with all of them off, a two-megabyte
document, conjoined multi-tenant invoices, and an event store with three projections, one archived stream
and one deliberately poisoned stream that the async daemon turns into dead letters. It never falls back
to `localhost` silently; without Docker it throws with guidance. Set `ConnectionStrings:Marten` to point
it at a database of your own. On its throwaway database (or one `--allow-data-generation` names) it creates a
narrowed role, `studio_sample_reader`, and sets `SqlConsoleRole` to it, so the SQL console, the database browser and
the health checks read as that role; it creates no role and grants nothing anywhere else.

The Postgres module has a sample of its own, with no Marten: `dotnet run --project samples/DataStudio.Sample.Postgres`, then
<http://localhost:5211> - an admin database and three tenant databases listed through the host's own
`IPostgresDatabaseSource`; see [`samples/DataStudio.Sample.Postgres/README.md`](../samples/DataStudio.Sample.Postgres/README.md).

Sign in as `admin`, `ops` or `viewer` — the password is the user name. They differ by claim: `admin` has
read, write and destroy, `ops` read and write, `viewer` read only, which is how you can see what each
layer of the authorization contract does.

| Switch | |
|---|---|
| `--anonymous` | Map the studio with `AllowAnonymous()` instead of a policy, to show what an unauthenticated studio looks like. The landing page draws a red banner saying so. Never do this anywhere real. |
| `--readonly` | Set `DataStudioOptions.ReadOnly`, which turns every mutating capability off however they were configured. |
| `--path /ops` | Mount the studio somewhere other than `/studio`, which is what exercises the sub-path re-rooting. |
| `--no-daemon` | Host no async daemon (`AddAsyncDaemon(DaemonMode.Disabled)`), the shape of a production host whose projections run elsewhere. The studio shows progress from the database and logs nothing about it above Debug. |

> **Argument order does not matter.** The sample's boolean switches — `--anonymous`, `--readonly`,
> `--allow-data-generation`, `--no-daemon` — are switches, not `--key value` pairs: writing one means `true` and the
> token after it is left alone, so `dotnet run -- --anonymous --urls http://localhost:5000` and
> `dotnet run -- --urls http://localhost:5000 --anonymous` do the same thing. An explicit value is
> still accepted where a script wants to pass a variable (`--readonly false`, `--readonly=false`), and
> only a literal `true` or `false` counts as one. `--path /ops` is a pair and does take the
> token after it. This needs saying because .NET's own command-line configuration provider does *not*
> work that way — it reads `--key value` pairs, so a bare switch would otherwise swallow your `--urls`
> and then drop the URL without a word; `SampleOptions.HostArguments` reconciles the two before the
> host sees the arguments.

### Demo data generator

In the `Development` environment (or with `--allow-data-generation` elsewhere) the sample's landing page
carries a **Demo data** panel for the `admin` user — presets **Small**, **Medium** (about 100 k documents
and 100 k events), **Large** (about 1.2 M documents, 1.25 M events across 250 k streams) and **Custom** —
that runs as a cancellable background job with a progress bar, rows per second, an ETA and the async
daemon's lag, plus a truncate that removes exactly what was generated. Documents go in through a binary
`COPY`; order streams go through Marten sessions so the inline projection runs and the async ones have
something to catch up on, with one poisoned stream per 10 000 so the dead-letter screen has real content.
The studio's own services are measured against the Medium set in every integration run; the Large set
runs under `MARTENSTUDIO_LARGE=1`. See [`samples/DataStudio.Sample.Marten/README.md`](../samples/DataStudio.Sample.Marten/README.md).
The same panel has **Make it unhealthy** switches - a pinned gap, a session idle in a transaction, a bloated
table, a hogged pool - each turning itself off after ten minutes; see the sample's README.
