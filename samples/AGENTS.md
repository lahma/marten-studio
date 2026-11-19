# AGENTS.md: the sample hosts

The [root AGENTS.md](../AGENTS.md) applies here as everywhere: what the product is, its two constraints, the hard rules and decisions that hold in every directory, and the index of all of them. This file holds what applies only to the sample hosts. Rules and decisions keep their numbers wherever they live; each one's full text is in the topic document it names, which wins where they differ.

**Read first.** The samples and their data: [architecture.md](../docs/maintainers/architecture.md#d17) (D17, D18).

## Design decisions

**D17 — The demo domain lives in `samples/DataStudio.Sample.Marten.Domain` and does not reference `DataStudio.Marten`.** The
missing reference is deliberate and structural: the studio must work against a store that knows nothing about it. The
demo data with no Marten in it is `samples/DataStudio.Sample.Postgres.Data`, which references no studio either. Full text:
[architecture.md](../docs/maintainers/architecture.md#d17).

**D18 — The sample host starts a throwaway Postgres when no connection string is configured.** Zero-config `dotnet run`
starts a throwaway Postgres with a loud banner and throws with guidance when Docker is absent; it must never fall back
to `localhost` silently and appear to work against somebody's real database. The Marten sample judges a reused database
before Marten touches it (`SampleDatabase`). Full text: [architecture.md](../docs/maintainers/architecture.md#d18).
