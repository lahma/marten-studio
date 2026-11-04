# Database › Connection

Since 1.1, both studios have a page that shows how your application actually connects to its database, and what you would
change: **Database › Connection** (`/postgres/database/connection`, `/marten/database/connection`). It is for the people
who run the application, not for the people who browse its data, and it is built so that it can never show a password.

It reads and changes nothing. It is in the Overview too, as one line - "Connection: TLS verified · pool 100 · 2 things
worth a look" - that links to it.

## What it shows

**The strip of facts** at the top gives, each linking to its detail further down:

- the server, the database and the role the application connects as;
- whether the connection is **encrypted** and whether the **server's certificate is checked** (TLS verified, encrypted but
  not verified, or not encrypted; for a GSSAPI connection the tile is labelled "Encryption" instead of "TLS" and reads
  "GSSAPI", and the Overview's line says "encrypted with GSSAPI (not TLS)"). An unencrypted connection
  is "(local)", in green, only when the data source's host is a socket or loopback **and** the server also sees the
  connection come over a socket or from loopback. Otherwise it is amber - "not encrypted · the server sees a TCP peer", with
  "(likely a container network)" added when the host is loopback but the server's peer is not (Docker, NAT) and no pooler
  is in front of it. The address is never printed. The Overview's line says the same words. S05 reads the same server-side
  peer but judges only the server side: a remote host whose peer the server sees as loopback is amber on the tile and quiet
  in S05;
- the **pool**: `Maximum Pool Size`, and the peak of this application's sessions on the server;
- the **Npgsql** version loaded, and the **platform** or **pooler** in front of the server when one is recognised (Amazon
  RDS and Aurora, Azure, Cloud SQL, Neon, Supabase, PgBouncer-family poolers and Amazon RDS Proxy).

**Worth a look** lists the health advisor's findings about the connection (C13 to C18, S05, S11 to S13, K04, K08) and the
page's own hints, most severe first. Each is a card with the settings it is about and the change to make, written twice: as
a connection string and as `NpgsqlDataSourceBuilder` code (in Marten Studio, inside `opts.ConfigureNpgsqlDataSourceBuilder`),
with a copy button for each. A hint is a sentence, never a finding: hints are not counted, ranked or exported by the advisor.
Where there are "Good to know" notes the heading counts each kind apart - "1 finding · 2 hints · 2 notes" (a kind with none is
left out); with none it is the number of things worth a look.

**Settings** lists every keyword of the Npgsql that is loaded, by category, with:

- the **value in effect**;
- **where it came from**: the connection string, an environment variable Npgsql falls back to (`PGUSER`, `PGSSLMODE`, ...,
  as that Npgsql major reads them), or its own default;
- **Npgsql's default**.

"Only what differs from the default" is on by default; it, and which groups are open, are remembered per browser. Every group
has the same column widths. Below 720 px each setting is a card (the setting, its value, then "From:" and "Default:") instead
of a table that scrolls sideways. Where `PGAPPNAME` (or `PGGSSENCMODE` or `PGREQUIREAUTH`) is set in the process and the loaded
Npgsql is below 10, the row says Npgsql does not read it and names the connection-string keyword to set; the variable's value is
never shown.

**What the server saw** is what a new session of the login starts with (`statement_timeout`, `idle_session_timeout`,
`password_encryption`, ...) and the TLS row the server holds for the studio's own connection. The studio's connection uses
the data source's settings as the application's do, so this is the nearest thing to asking the server how the application
connects.

**This application's sessions** groups the sessions of this login by application name, with the oldest connection and the
share on TLS. It is behind `ViewDatabaseActivity` (see "The gates").

**Not visible from here** says what no page can see: settings made in code on the `NpgsqlDataSourceBuilder` (a callback, a
password provider, a logger factory), and the default password file. It is a closed disclosure with a one-line summary; open
or closed is remembered per browser.

**In Marten Studio**, "How Marten uses this connection" adds:

- the command timeout Marten applies to its sessions, worked out as Marten does - or, where only a numeric default and a
  different connection-string value are left to tell apart, both numbers, with a sentence that says Marten does not say
  which applies;
- whether the data source is Marten's own or the application's (one it handed to Marten, found without building any data
  source), and a note when Marten builds a pool of its own beside the application's;
- the store options that bear on connections, each with its `StoreOptions` line and its default: sticky connections,
  session preferences on several hosts, Npgsql logging for the data sources Marten builds, connection tracing, the hi-lo
  block size and the update batch size;
- the connections the hosted async daemon needs.

The Configuration page lists the same options as plain values and links the timeout and logging rows here.

## What it never shows

The page is built so that a connection string never reaches it ([D33 in the maintainer notes](maintainers/architecture.md#d33)):

- a **password** (`Password`, its aliases, `SSL Password`, `PGPASSWORD`) is only ever "set" or "not set";
- a **keyword the studio does not know** is listed as present, with its value withheld;
- an environment variable such as `PGUSER` is shown on the same terms as its keyword;
- an **`Options` value** (`-c name=value`) is shown only for real server settings;
- **paths** (a certificate, a key, the root certificate) are shown, and the studio never opens the file;
- the **raw connection string**, and the names Npgsql gives its pools (built from it), never leave the reader. Nothing a
  check, a hint card, the snapshot download, a log line or the audit log holds can contain them.

A data source you gave a `Name` (`NpgsqlDataSourceBuilder.Name`; Aspire's Npgsql integration and keyed registrations do) is
reported to Npgsql's meter under that name, not its connection string. C18 identifies such a pool in memory, from the data sources
the studio already resolves - the one this page is about, Postgres Studio's listed sources (`AddDataSource`, `AddDatabaseSource`)
and Marten Studio's stores' databases - and keeps only counts, as above. A named pool the studio does not resolve is **not
judged**: C18 says "Not judged for N named pool(s)" rather than "nothing found". See
[C18](health/connections.md#c18-several-pools-to-one-database-in-this-process).

Tests run a data source made entirely of sentinel strings through every one of those places and fail if one appears.

## The hints

Each hint names the setting and gives its fix. They appear only when they apply.

| Hint | Says | Fix |
|---|---|---|
| TLS | The server's certificate is not checked (`SSL Mode` below `VerifyCA`), or the connection is never encrypted. Names the provider's CA bundle where the studio has a source (Amazon RDS, Azure); behind an Amazon RDS Proxy it advises no `Root Certificate`. | `SSL Mode=VerifyFull` (with `Root Certificate`) |
| LIFETIME | `Connection Lifetime=0` never recycles connections, so they do not rebalance after a failover or a scale-out; behind an Amazon RDS Proxy, a lifetime of 0 or over 24 hours outlives the proxy's 24-hour limit. | `Connection Lifetime=3600` |
| MULTIHOST | Several hosts and no `Target Session Attributes`: a write can land on a standby. In Marten Studio it says which host Marten picks for reads and writes. | `Target Session Attributes=primary` |
| KEEPALIVE | No `Keepalive` where something cuts idle connections (a managed platform, a pooler, the server's `idle_session_timeout`). | `Keepalive=` half the shortest cut |
| POOLSIZE | Sessions with this `Application Name` peaked at N on the server, against the pool's maximum. Counted by name across every process and database using it. | none: weigh `Maximum Pool Size` |
| BUFFER | The average row or document read is larger than `Read Buffer Size`. In Marten Studio the size comes from the deeper checks E04 and T09. | `Read Buffer Size=` a little above the row |
| PREPARE | `Max Auto Prepare` on Supabase's transaction pooler (port 6543) can break; or `Max Auto Prepare=0` elsewhere leaves planning on the table. | `Max Auto Prepare=0` or `20` |
| RESET | Npgsql's reset on release against PgBouncer (its compatibility page) or an Amazon RDS Proxy (the reset pins the connection). | `No Reset On Close=true` |
| ENLIST | `Enlist=true` makes Npgsql look for an ambient transaction on every open (shown in the full settings list). | `Enlist=false` where `System.Transactions` is unused |
| CMDTIMEOUT | Which timeout cancels a long command first: Npgsql's `Command Timeout` or the server's `statement_timeout` / `transaction_timeout`. In Marten Studio, the one that counts is Marten's. | none |
| COLDSTART | A `Timeout` under 5 s on Neon, which scales a compute to zero. | `Timeout=15` |
| AUTH | The server's `password_encryption` is `md5`, or `Channel Binding=Disable`. | `Channel Binding=Prefer` |
| MUX | `Multiplexing` is experimental and changes connection semantics. | `Multiplexing=false` |
| Obsolete / Local | An obsolete keyword is set; the host is a Unix socket. | Remove the keyword |

## The checks it surfaces

The advisor's checks about the connection run on the application's real data source, once per sweep, with the other
checks. Their pages are in [health/](health/):

- [C16](health/connections.md#c16-server-or-proxy-ends-idle-pooled-connections-first), a server or proxy ends idle pooled
  connections first;
- [C17](health/connections.md#c17-connection-pooling-switched-off), connection pooling switched off;
- [C18](health/connections.md#c18-several-pools-to-one-database-in-this-process), several pools to one database in this
  process;
- [S05](health/server.md#s05-connection-without-tls), connection without TLS (not applicable behind a pooler);
- [S11](health/server.md#s11-server-certificate-not-verified), server certificate not verified;
- [S12](health/server.md#s12-a-credential-is-readable-from-configuration), a credential is readable from configuration;
- [S13](health/server.md#s13-driver-settings-that-copy-values-into-logs-or-exceptions), driver settings that copy values
  into logs or exceptions;
- [C13 to C15](health/connections.md#c13-idle-connections-hoarded-near-the-limit), the server's connection limit;
- [K04](health/configuration.md#k04-hotcold-daemon-behind-a-transaction-pooler) and
  [K08](health/configuration.md#k08-server-timeouts-end-the-daemons-leadership), in Marten Studio.

## Compare a connection string

Under "Compare a connection string" you can paste another Npgsql connection string - the one in a new configuration, one a
colleague sent - to see which of its settings differ from the connection in effect and what the page's advice would be if
the application switched to it: what is new, what is also true today, and what no longer applies.

The result opens with one sentence made only from what the comparison computed: whether the pasted settings check the
server's certificate or encrypt at all, whether they keep the password readable (`Persist Security Info`), the advice that is new
and the advice that no longer applies - or, when the page's hints do not change, "The page's hints are the same for both.
Health findings such as S05 or S12 are judged only for the connection in effect." The comparison judges the page's hints
only; a health finding needs the real data source, so a finding that is gone for the pasted string is not reported. It
never says the pasted settings are better. The text you pasted stays in the box until you press Clear or leave the page; what the studio reads has the
passwords taken out.

- Passwords (`Password`, its aliases and `SSL Password`) are removed on the server before anything is read, an option's value
  is never shown, and a keyword Npgsql rejects is named and its value never repeated. A password with a `;` in it must be
  quoted: otherwise the keyword straight after it is refused without a name, because it may be the rest of the password.
- The studio keeps, logs, audits and connects to nothing you paste. It is read in this page only, so it is not part of the
  audit log, and no connection is opened.
- The advice is the page's own hints, judged against what the server reported about the connection in effect. The health
  advisor's findings run on the application's real data source only.
- What you paste travels to the server over SignalR, like every other input in a Blazor Server page. If your application
  logs `Microsoft.AspNetCore.SignalR` at Debug level, that log records it (typed SQL in the console included): keep that
  category at Information or above in production ([security.md](security.md)).

## The gates

- The page, the Overview's line and paste-to-compare are D29's `Database` gate: the store (Marten Studio) or source
  (Postgres Studio) authorization policy, asked about the database as a whole with `TenantId = null`. A visitor the policy
  refuses sees nothing of it.
- "This application's sessions", and the pool peak counted by application name, are D29's `Activity` gate: the
  `ViewDatabaseActivity` capability, then the policies, with the write policy asked with that capability named. Without
  it the sessions part says it is not shown, and the rest of the page is unchanged. These reads are audited like Sessions'
  details.
- The page itself reads configuration and is not audited.

## The snapshot

The Health page's Snapshot download gains a `connection` section: the page's settings, as the visitor's page shows them,
passwords never among them. `schemaVersion` stays 1. A snapshot a host asks `IPostgresDiagnostics` for writes
`"connection": null`; see [diagnostics.md](diagnostics.md).
