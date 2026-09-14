using MartenStudio.Integration.Tests;

// One postgres:17-alpine for the whole assembly (plan §5.4). A container per collection would be correct
// and would also mean a dozen Postgres starts per run; the isolation that actually matters is the schema,
// which is created fresh per test class, so one server is enough and is what makes the suite usable
// locally. The fixture reuses the container between runs when it is not on GitHub Actions.
[assembly: AssemblyFixture(typeof(PostgresFixture))]

// Four at a time, not Environment.ProcessorCount. The suite is bound by one Postgres server and by Docker,
// not by CPU, and a machine that is running several agent sessions at once must not have this suite decide
// it owns every core.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
