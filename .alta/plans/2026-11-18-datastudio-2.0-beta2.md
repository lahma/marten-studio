---
status: in-progress
---

# Data Studio 2.0.0-beta.2: the first feedback from dogfooding

The owner tried 2.0.0-beta.1 in real hosts and filed #83 to #88; #80 and #81 were open from 1.3.1. This plan fixes all
eight and ships `2.0.0-beta.2`. #58 (a JSON API for the Kafka module) is a decision, skipped for now.

## What each issue gets

| Issue | Finding | Fix |
|---|---|---|
| #83 | On a Kafka page the rail opens at its top, so the active item is out of view | After each navigation the shell rail reveals its active item inside its own scroll box (the helper the database browser's rail already uses) |
| #84 | `GetStudioPath(Overview)` in a Marten host returns an address that redirects | The path is the module's own home: `marten?…` in a Marten host, `postgres?…` in a Postgres host and for the engine alone |
| #85 | The Kafka tile promises live tail when it is off; a JSON record's value row says "Json, JSON" | The tile names no capability; the row shows the format once when kind and format agree |
| #86 | Sessions and Connection do not match a named data source's pool | The pool is matched by the data source's `Name`, as C18 does since #56 |
| #87 | A Critical finding sits below every Warning of the areas before it | A line at the top of Health names each critical finding the visitor may see and links its card; the areas keep their order |
| #88 | Query reached by address with `RunSql` off names options and shows the Queries panel | One sentence, no option name, and no Queries panel, as #74 settled for Kafka |
| #81 | Aurora is detected as RDS, so S02 is a false Critical | Aurora is any one of several markers beside the RDS marker, none tied to `pg_catalog` |
| #80 | Four Kafka nits: Windows time zone ids, topic count wording, error footer, error order | IANA ids on Windows; "1 selected (a search reads at most 20)"; an error count; the earliest position first |

## Steps

- [ ] 1. Two packets in parallel, files disjoint: **B2-A** (Postgres, engine, Marten: #81, #84, #86, #87, #88) and
      **B2-B** (shell, Kafka: #83, #85, #80). Sonnet implements, Opus reviews each.
- [ ] 2. Integrate; `CHANGELOG.md` `# 2.0.0-beta.2` (one bullet an issue), written by the coordinator.
- [ ] 3. Full `Test` at the floors and at the latest-tested versions; the Postgres projects at Npgsql 10.0.3.
- [ ] 4. Pull request, green CI, merge (rebase-merge: the commits are few and each says what it fixes), tag
      `v2.0.0-beta.2`, confirm the seven packages on nuget.org, close the issues with the release named.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| #87 | A top line, not a reorder | The areas' fixed order is how the page is learned; the line is one click to the card |
| #88 | Only the SQL console reached with `RunSql` off | The issue's scope. D4 still says a disabled control names its option; whether every page should follow #74 is a separate question for the owner |
| #84 | The module's home, said by the module | The engine must not name Marten (hard rule 16) |
| #58 | Skipped | The owner's call |

## Out of scope

Tasks `kh77`, `hpjq` (the flaky Kafka 3.9 live class), `cx59` (post-beta tidy) and `2zrh`, unless a packet touches the same lines.
