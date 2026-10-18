using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.StatementBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>T23 with no Postgres: a fifth of a statement's time spent compiling it, over a hundred calls.</summary>
public class JitShareCheckTests
{
    private static readonly JitShareCheck Check = new();

    private static HealthVerdict Judge(StatementRead read, HealthHistoryView? history = null) =>
        Check.Evaluate(new JitFacts(read), TableCheckBuilders.Evaluation(history));

    [Fact]
    public void A_store_statement_that_spends_over_a_fifth_of_its_time_compiling_is_a_warning_with_the_data_source_line()
    {
        HealthFinding finding = Judge(Read(Statement(9, SelectOnDoc, [Doc], 150, 1500, jitMs: 600))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().BeNull("one finding for the database: the remedy is the store's connections, not one statement");
        finding.Title.Should().Be("JIT compilation takes 40% of a store statement's time");
        finding.DocumentTypes.Should().Equal(typeof(StatementDoc));

        HealthEvidence statement = finding.Evidence.Single(static x => x.Label == "Statement 1");
        statement.Statement!.Text.Should().Be(SelectOnDoc);
        statement.Value.Should().Be(HealthEvidence.StatementPending);
        finding.Evidence.Single(static x => x.Label == "Statement 1 JIT").Value.Should().StartWith("40% of its 1.5 s over 150 calls");

        finding.Remedy!.Code.Should().Be(JitShareCheck.RemedyCode);
        finding.Remedy.Code.Should().Contain("ConfigureNpgsqlDataSourceBuilder").And.Contain("ConnectionStringBuilder.Options").And.Contain("-c jit=off");
    }

    [Theory]
    [InlineData(150, 1500, 150)] // 10%: compiling, but not the statement's cost
    [InlineData(50, 1500, 900)] // 60%, over too few calls to judge
    [InlineData(150, 1500, 300)] // exactly 20% is not over it
    public void Under_the_share_or_the_calls_it_is_quiet(long calls, double execMs, double jitMs)
    {
        HealthVerdict verdict = Judge(Read(Statement(9, SelectOnDoc, [Doc], calls, execMs, jitMs: jitMs)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("more than 20%");
    }

    [Fact]
    public void Only_the_stores_statements_with_jit_time_recorded_are_judged()
    {
        StatementRead read = Read(
            Statement(1, "select * from other_app.reports", [], 1000, 1000, jitMs: 900),
            Statement(2, SelectOnDoc, [Doc], 1000, 1000, jitMs: null));

        JitShareCheck.Candidates(read).Should().BeEmpty();
        Judge(read).Findings.Should().BeEmpty();
    }

    [Fact]
    public void The_finding_names_the_three_that_compile_the_most()
    {
        StatementRead read = Read([.. Enumerable.Range(1, 5).Select(i => Statement(i, SelectOnDoc + " limit $" + (i + 2), [Doc], 200, 1000, jitMs: 300 + (i * 50)))]);

        HealthFinding finding = Judge(read).Findings.Single();

        finding.Title.Should().Be("JIT compilation takes more than 20% of the time of 5 store statements");
        finding.Evidence.Where(static x => x.Statement is not null).Should().HaveCount(JitShareCheck.MaxNamed);
        finding.Evidence.Single(static x => x.Label == "Statement 1").Statement!.Text.Should().EndWith("limit $7", "the most JIT time first");
    }

    [Fact]
    public void The_window_between_two_statement_checks_is_what_is_judged_once_it_exists()
    {
        // 5% over its whole life; 50% in the last ten minutes.
        StatementRead read = Read(Statement(9, SelectOnDoc, [Doc], 10_200, 100_000, jitMs: 5_000));
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(10), [(JitShareCheck.CallsSeries + "9", 10_000), (JitShareCheck.ExecSeries + "9", 99_000), (JitShareCheck.JitSeries + "9", 4_500)]),
            (TimeSpan.Zero, [(JitShareCheck.CallsSeries + "9", 10_200), (JitShareCheck.ExecSeries + "9", 100_000), (JitShareCheck.JitSeries + "9", 5_000)]));

        Judge(read).Findings.Should().BeEmpty();
        Judge(read, history).Findings.Single().Evidence.Single(static x => x.Label == "Statement 1 JIT").Value.Should().StartWith("50% of its 1.0 s over 200 calls");
    }

    [Fact]
    public void It_samples_its_own_three_series_for_each_statement_it_judges()
    {
        StatementRead read = Read(Statement(9, SelectOnDoc, [Doc], 150, 1500, jitMs: 600), Statement(1, "select 1", [], 10, 1, jitMs: 1));

        Check.Sample(new JitFacts(read)).Select(static x => x.Series).Should().BeEquivalentTo(
            [JitShareCheck.CallsSeries + "9", JitShareCheck.ExecSeries + "9", JitShareCheck.JitSeries + "9"]);
    }
}
