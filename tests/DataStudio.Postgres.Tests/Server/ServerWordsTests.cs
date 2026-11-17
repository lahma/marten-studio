using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Health.Checks;
using DataStudio.Postgres.Services.Postgres;

using static DataStudio.Postgres.Tests.Server.ServerTestFacts;

namespace DataStudio.Postgres.Tests.Server;

/// <summary>
/// W5-S2-fix: the sentences S10 and T14 say differently in each module - the Postgres module has no Marten writes to name and no
/// HotCold daemon, and so no K08, to point at. The Marten module's own are held by its <c>WordsTests</c> snapshot.
/// </summary>
public class ServerWordsTests
{
    [Fact]
    public void Postgres_Studios_words_name_no_Marten_write_and_no_daemon()
    {
        PostgresStudioText.Instance.ReadOnlyWrites.Should().Be("write the application makes");
        PostgresStudioText.Instance.ReadOnlyNeverAsks.Should().Be("and most data access never asks");
        PostgresStudioText.Instance.LeaderTimeoutClause.Should().BeEmpty("a data source has no HotCold daemon, and Postgres Studio no K08");
    }

    [Fact]
    public void S10_says_whose_writes_fail_in_the_Postgres_modules_words()
    {
        ReadOnlyStoreFacts facts = new(Setting("default_transaction_read_only", "on", source: "user", boot: "off"), true, SelfHosted);

        HealthVerdict postgres = new ReadOnlyStoreCheck().Evaluate(facts, Evaluation(postgres: true));

        HealthFinding readOnly = postgres.Findings.Single(x => x.Variant == "read-only");
        readOnly.WhyItMatters.Should().StartWith(
            "With default_transaction_read_only = on, a transaction that does not ask otherwise is read-only - and most data access "
            + "never asks: every write the application makes fails with 25006");
        postgres.Findings.Should().OnlyContain(static x => !x.WhyItMatters.Contains("Marten", StringComparison.Ordinal)
            && !x.WhyItMatters.Contains("SaveChanges", StringComparison.Ordinal)
            && !x.WhyItMatters.Contains("store", StringComparison.Ordinal));
    }

    [Fact]
    public void T14s_timeout_backstop_names_no_K08_in_the_Postgres_module()
    {
        ("is the DBA's backstop" + PostgresStudioText.Instance.LeaderTimeoutClause + ".").Should().Be("is the DBA's backstop.");
    }
}
