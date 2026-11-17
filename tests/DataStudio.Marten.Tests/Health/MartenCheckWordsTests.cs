using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Health.Checks;

namespace DataStudio.Marten.Tests.Health;

/// <summary>
/// The shared checks that print a caveat or a remedy that names an option or a noun, evaluated in a Marten host's words (REVIEW-V2-D2, finding 6):
/// a check that fell back to another module's words - as T22's caveat did, reading "the data source's own role" in a Marten host - is seen here,
/// because the Postgres suite cannot see the Marten module's words (the fence between them). T22's own caveat is held in
/// <see cref="ConcurrencyConflictCheckTests" />, over the facts only it builds.
/// </summary>
public class MartenCheckWordsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>What the Marten module says in place of the Postgres module's nouns and options.</summary>
    private const string ActivityOption = "MartenOptions.Capabilities.ViewDatabaseActivity";

    /// <summary>The words a Marten host must never read in a shared finding.</summary>
    private static readonly string[] PostgresWords = ["data source", "PostgresOptions", "SourceAuthorizationPolicy"];

    private static HealthEvaluation Marten => HealthEvaluation.At(At);

    private static string Rendered(HealthFinding finding) =>
        string.Join(
            '\n',
            [
                finding.Title,
                finding.WhyItMatters,
                finding.Caveat ?? string.Empty,
                .. finding.Evidence.Select(static x => x.Label + " " + x.Value),
                finding.Remedy?.Code ?? string.Empty,
                finding.Remedy?.Explanation ?? string.Empty,
            ]);

    [Fact]
    public void C04_names_the_Marten_option_behind_the_statements_and_has_no_Postgres_word()
    {
        IdleSession idle = new(
            4242, "app_user", "billing-api", "10.1.2.3", At.AddHours(-1), "idle in transaction", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10), true, true, 2, false);

        HealthFinding finding = new IdleInTransactionCheck()
            .Evaluate(new IdleInTransactionFacts(At, SessionVisibility.Everything, 1, [idle]), Marten)
            .Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain(ActivityOption).And.Contain("behind " + ActivityOption);
        Rendered(finding).Should().NotContainAny(PostgresWords);
    }

    [Fact]
    public void C05_names_the_Marten_option_behind_the_statements_and_has_no_Postgres_word()
    {
        RunningStatement running = new(4242, "app_user", "billing-api", "10.1.2.3", At.AddHours(-1), null, TimeSpan.FromMinutes(20));

        HealthFinding finding = new LongRunningStatementCheck()
            .Evaluate(new LongRunningFacts(At, SessionVisibility.Everything, 1, [running]), Marten)
            .Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain("behind " + ActivityOption);
        Rendered(finding).Should().NotContainAny(PostgresWords);
    }

    [Fact]
    public void C06_names_the_Marten_option_behind_the_statements_and_has_no_Postgres_word()
    {
        LockTarget relation = new(LockTargetKind.OtherRelation, "a table", "public", "orders");
        LockWaiter waiter = new(4242, "app_user", "billing-api", "10.1.2.3", "AccessExclusiveLock", relation, true, TimeSpan.FromMinutes(2), [5151]);
        LockBlocker blocker = new(5151, true, "idle in transaction", "app_user", "billing-api", "10.1.2.3", TimeSpan.FromMinutes(4), true, false);

        HealthFinding finding = new LockWaitCheck()
            .Evaluate(new LockWaitFacts(At, SessionVisibility.Everything, 1, [waiter], [blocker]), Marten)
            .Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain("behind " + ActivityOption);
        Rendered(finding).Should().NotContainAny(PostgresWords);
    }

    [Fact]
    public void C07_names_the_Marten_option_that_shows_the_owner_and_has_no_Postgres_word()
    {
        PreparedRow orphan = new("gid-orphan", "tx_owner", At.AddHours(-3), TimeSpan.FromHours(3), "1234");

        HealthFinding finding = new PreparedTransactionCheck()
            .Evaluate(new PreparedTransactionFacts(At, 10, [orphan]), Marten)
            .Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain("shown only with " + ActivityOption);
        Rendered(finding).Should().NotContainAny(PostgresWords);
    }

    /// <summary>
    /// The anti-vacuity half: the same facts in the Postgres module's words say the Postgres option, so the assertions above are about the words
    /// and not about facts that never reach the sentence.
    /// </summary>
    [Fact]
    public void The_same_facts_in_the_Postgres_modules_words_say_something_else()
    {
        PreparedRow orphan = new("gid-orphan", "tx_owner", At.AddHours(-3), TimeSpan.FromHours(3), "1234");
        HealthEvaluation postgres = HealthEvaluation.At(At) with { Text = DataStudio.Postgres.Services.Postgres.PostgresText.Instance };

        HealthFinding finding = new PreparedTransactionCheck()
            .Evaluate(new PreparedTransactionFacts(At, 10, [orphan]), postgres)
            .Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain("shown only with PostgresOptions.Capabilities.ViewDatabaseActivity").And.NotContain("MartenOptions");
    }
}
