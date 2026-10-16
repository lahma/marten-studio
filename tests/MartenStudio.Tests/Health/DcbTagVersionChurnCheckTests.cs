using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;

namespace MartenStudio.Tests.Health;

/// <summary>E08's judgement over stated collector counters, and its remedy at the floor and at the latest Marten.</summary>
public class DcbTagVersionChurnCheckTests
{
    private static DcbTagVersionFacts Facts(
        long rows = 200_000,
        long updates = 50_000,
        long hot = 40_000,
        string[]? options = null,
        int? declared = null,
        int tagTypes = 1) =>
        new(tagTypes, new DcbTagVersionRow(rows, rows, updates, hot, options ?? [], Now.AddDays(-2)), declared);

    private static HealthVerdict Evaluate(DcbTagVersionFacts facts, HealthHistoryView? history = null) =>
        new DcbTagVersionChurnCheck().Evaluate(facts, At(history));

    [Fact]
    public void No_tag_types_is_quiet()
    {
        HealthVerdict verdict = Evaluate(new DcbTagVersionFacts(0, null, null));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("no DCB tag types");
    }

    [Fact]
    public void A_small_tag_set_is_quiet_because_the_fillfactor_makes_no_difference_there()
    {
        Evaluate(Facts(rows: 99_000, hot: 0)).Findings.Should().BeEmpty("#5622 measured 100% HOT at 50 rows whatever the fillfactor");
    }

    [Fact]
    public void Too_few_updates_to_judge_is_quiet()
    {
        Evaluate(Facts(updates: 9_999, hot: 0)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_HOT_share_of_ninety_percent_is_quiet()
    {
        Evaluate(Facts(updates: 50_000, hot: 45_000)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Churn_is_a_warning_whose_remedy_needs_Marten_9_47()
    {
        HealthFinding finding = Evaluate(Facts()).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E08");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("80%");
        finding.WhyItMatters.Should().Contain("92.9%").And.Contain("#5622");
        finding.Evidence.Should().Contain(new HealthEvidence("Table fillfactor", "100 (PostgreSQL's default)"));

        finding.Remedy!.Code.Should().Be("opts.Events.DcbTagVersionFillFactor = 70;");
        finding.Remedy.Requires.Should().BeSameAs(RuntimeFeature.DcbTagVersionFillFactor);
        RuntimeFeature.DcbTagVersionFillFactor.Requirement.Should().Be("Marten 9.47+");
    }

    /// <summary>The page prints "Marten 9.47+" and, on an older host, what it runs - decided by the feature, at both ends.</summary>
    [Theory]
    [InlineData("9.39.0", false)]
    [InlineData("9.46.0", false)]
    [InlineData("9.47.0", true)]
    [InlineData("9.50.2", true)]
    public void The_remedy_is_usable_from_Marten_9_47(string marten, bool usable)
    {
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = marten });

        RuntimeFeature.DcbTagVersionFillFactor.IsAvailable(runtime).Should().Be(usable);
    }

    [Fact]
    public void Two_samples_measure_the_window_rather_than_the_counters_since_the_reset()
    {
        // Since the reset: 50,000 updates at 80% HOT. In the window: 20,000 more, every one of them HOT.
        HealthHistoryView history = new(
        [
            Sample(Now.AddMinutes(-10), updates: 30_000, hot: 20_000),
            Sample(Now, updates: 50_000, hot: 40_000),
        ]);

        (double updates, double hot, string over) = DcbTagVersionChurnCheck.Measure(Facts().Table!, At(history));

        updates.Should().Be(20_000);
        hot.Should().Be(20_000);
        over.Should().Be("in the last 10 min");
        Evaluate(Facts(), history).Findings.Should().BeEmpty("the window's updates were all heap-only");
    }

    [Fact]
    public void A_counter_reset_between_samples_falls_back_to_the_counters_since_the_reset()
    {
        HealthHistoryView history = new(
        [
            Sample(Now.AddMinutes(-10), updates: 900_000, hot: 900_000),
            Sample(Now, updates: 50_000, hot: 40_000),
        ]);

        (double updates, _, string over) = DcbTagVersionChurnCheck.Measure(Facts().Table!, At(history));

        updates.Should().Be(50_000);
        over.Should().StartWith("since the statistics were reset");
    }

    [Fact]
    public void A_declared_fillfactor_already_on_the_table_is_an_info_about_unrewritten_pages()
    {
        HealthFinding finding = Evaluate(Facts(options: ["fillfactor=70"], declared: 70)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Remedy.Should().BeNull("the line is already there");
        finding.WhyItMatters.Should().Contain("does not rewrite the pages already written");
        finding.Evidence.Should().Contain(new HealthEvidence("Table fillfactor", "70"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_declared_fillfactor_not_yet_on_the_table_is_a_warning_to_apply_the_migration(bool tableSetsAnother)
    {
        string[]? options = tableSetsAnother ? ["fillfactor=100"] : null;

        HealthFinding finding = Evaluate(Facts(options: options, declared: 70)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "the store asks for the fillfactor and the table does not have it yet");
        finding.Title.Should().Contain("has not been applied");
        finding.Remedy!.Code.Should().Contain("ApplyAllDatabaseChangesOnStartup").And.Contain("db-apply")
            .And.NotContain("DcbTagVersionFillFactor =", "the line is already there - what is missing is the migration");
        finding.WhyItMatters.Should().Contain("the schema change that sets it is pending");
        finding.Remedy.Code.Should().NotContainEquivalentOf("alter table", "the studio prints no DDL for a table Marten manages");
    }

    [Theory]
    [InlineData(new[] { "fillfactor=70" }, 70)]
    [InlineData(new[] { "autovacuum_enabled=false", "FILLFACTOR = 80" }, 80)]
    [InlineData(new[] { "autovacuum_enabled=false" }, null)]
    public void The_tables_fillfactor_is_read_from_reloptions(string[] options, int? expected)
    {
        Facts(options: options).TableFillFactor.Should().Be(expected);
    }

    [Fact]
    public void Its_samples_are_the_two_counters()
    {
        new DcbTagVersionChurnCheck().Sample(Facts()).Should().BeEquivalentTo(
        [
            new HealthSamplePart(DcbTagVersionChurnCheck.UpdatesSeries, 50_000),
            new HealthSamplePart(DcbTagVersionChurnCheck.HotUpdatesSeries, 40_000),
        ]);
    }

    private static HealthSample Sample(DateTimeOffset at, double updates, double hot) =>
        new(at, new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [DcbTagVersionChurnCheck.UpdatesSeries] = updates,
            [DcbTagVersionChurnCheck.HotUpdatesSeries] = hot,
        });
}
