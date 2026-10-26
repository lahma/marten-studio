using PostgresStudio.Integration.Tests;

// One postgres:17-alpine for the whole assembly, as in MartenStudio.Integration.Tests: the isolation that matters is a
// schema or a database of each test class's own, so one server is enough. It is never the Marten suite's container - see
// PostgresFixture.ReuseLabel.
[assembly: AssemblyFixture(typeof(PostgresFixture))]

// Four at a time, not Environment.ProcessorCount: the suite is bound by one Postgres server and by Docker, and this
// machine runs several agent sessions at once.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
