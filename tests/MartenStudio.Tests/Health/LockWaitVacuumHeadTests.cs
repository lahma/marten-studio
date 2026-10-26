using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C06's head of a chain when it is a VACUUM (W5-V1, the 2026-10-26 health-gap analysis, 7a): named for what it is - and
/// for an anti-wraparound autovacuum, why it never gives way - with what to do in words.
/// </summary>
public class LockWaitVacuumHeadTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly LockTarget Table = new(LockTargetKind.DocumentTable, "the order document table", "sample", "mt_doc_order");

    private static LockWaitFacts Facts(LockBlocker head) => new(
        Now,
        SessionVisibility.Everything,
        1,
        [new LockWaiter(4242, "app_user", "migrator", "10.1.2.3", "AccessExclusiveLock", Table, true, TimeSpan.FromMinutes(1), [head.ProcessId])],
        [head]);

    private static LockBlocker Head(bool vacuum, bool autovacuum, bool wraparound) =>
        new(5151, true, "active", null, null, null, TimeSpan.FromMinutes(20), false, false)
        {
            Vacuum = vacuum,
            Autovacuum = autovacuum,
            PreventsWraparound = wraparound,
        };

    [Theory]
    [InlineData(true, false, false, "a VACUUM")]
    [InlineData(true, true, false, "an autovacuum")]
    [InlineData(true, true, true, "an autovacuum to prevent wraparound")]
    public void A_VACUUM_at_the_head_is_named_for_what_it_is(bool vacuum, bool autovacuum, bool wraparound, string label)
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(Facts(Head(vacuum, autovacuum, wraparound)), HealthEvaluation.At(Now))
            .Findings.Should().ContainSingle().Subject;

        finding.Evidence.Should().Contain(x => x.Label == "Held up by" && x.Value.StartsWith("1 session " + label, StringComparison.Ordinal));
        finding.WhyItMatters.Should().Contain("The head of the chain is " + label + ".")
            .And.Contain("Let the vacuum finish and retry the migration")
            .And.Contain("never cancel the vacuum while T12 is near its line");
    }

    [Fact]
    public void An_anti_wraparound_autovacuum_is_said_never_to_give_way_and_a_Marten_migration_is_named()
    {
        string advice = LockWaitCheck.VacuumHeadAdvice(Head(true, true, true), StudioText.Marten);

        advice.Should().Contain("An anti-wraparound autovacuum does not give way to a lock request")
            .And.Contain("ApplyAllDatabaseChangesOnStartup, db-apply");
    }

    [Fact]
    public void An_ordinary_autovacuum_is_said_to_give_way_after_deadlock_timeout_and_Postgres_Studio_names_no_Marten()
    {
        string advice = LockWaitCheck.VacuumHeadAdvice(Head(true, true, false), PostgresStudio.Services.Postgres.PostgresStudioText.Instance);

        advice.Should().Contain("cancelled by a lock request after deadlock_timeout").And.NotContain("Marten").And.NotContain("db-apply");
    }

    [Fact]
    public void A_head_that_is_no_VACUUM_gets_no_vacuum_advice()
    {
        HealthFinding finding = new LockWaitCheck().Evaluate(Facts(Head(false, false, false)), HealthEvaluation.At(Now))
            .Findings.Should().ContainSingle().Subject;

        finding.WhyItMatters.Should().NotContain("Let the vacuum finish");
    }
}
