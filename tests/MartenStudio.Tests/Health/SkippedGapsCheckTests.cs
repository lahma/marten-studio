using JasperFx.Events;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>D07's judgement over stated rows of <c>mt_high_water_skips</c> and the store's gap-skipping configuration.</summary>
public class SkippedGapsCheckTests
{
    private static SkippedGap Skip(TimeSpan ago, long starting = 100, long ending = 120) => new(starting, ending, Now - ago);

    private static SkippedGapsFacts Facts(
        IEnumerable<SkippedGap>? skips = null,
        bool evidence = true,
        TimeSpan? cap = null,
        EventAppendMode mode = EventAppendMode.QuickWithServerTimestamps,
        bool exclusive = false,
        bool tenantPartitioned = false,
        bool asyncWork = true) =>
        new(asyncWork, [.. skips ?? []], Now, evidence, cap, mode, exclusive, tenantPartitioned);

    private static HealthVerdict Evaluate(SkippedGapsFacts facts, string marten = "9.50.2") =>
        new SkippedGapsCheck().Evaluate(
            facts,
            HealthEvaluation.At(Now, MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = marten })));

    [Fact]
    public void A_store_with_nothing_async_is_quiet()
    {
        Evaluate(Facts([Skip(TimeSpan.FromMinutes(1))], asyncWork: false)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void An_empty_table_is_quiet_and_says_so()
    {
        HealthVerdict verdict = Evaluate(Facts());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("never skipped");
    }

    [Fact]
    public void Skips_older_than_a_day_are_quiet_and_the_note_dates_the_latest()
    {
        HealthVerdict verdict = Evaluate(Facts([Skip(TimeSpan.FromDays(2)), Skip(TimeSpan.FromDays(3), 50, 60)]));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("more than a day ago");
    }

    [Fact]
    public void Under_the_defaults_a_skip_in_the_last_hour_is_an_info_about_lost_numbers_not_lost_events()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5)), Skip(TimeSpan.FromHours(3), 80, 90)]))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D07");
        finding.Severity.Should().Be(HealthSeverity.Info, "with the transaction evidence on, Marten skips only what nobody alive could fill");
        finding.Title.Should().Be("The daemon skipped past gaps in the event sequence 1 time in the last hour");
        finding.WhyItMatters.Should().Contain("lost numbers, not lost events");
        finding.Evidence.Should().Contain(new HealthEvidence("Skips in the last hour", "1"));
        finding.Evidence.Should().Contain(new HealthEvidence("Skips in the last day", "2"));
        finding.Evidence.Should().Contain(static x => x.Label == "Latest skip" && x.Value.StartsWith("#100 to #120 (20 sequence numbers)", StringComparison.Ordinal));
        finding.Link!.Route.Should().Be("projections", "D01's Explain sits on the projections screen, beside a held mark");
    }

    [Fact]
    public void Under_the_defaults_on_a_Quick_store_the_remedy_is_the_exclusive_lock()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5))])).Findings.Should().ContainSingle().Subject;

        finding.Remedy!.Code.Should().Be("opts.Events.UseExclusiveLockOnConcurrentAppends = true;");
    }

    [Fact]
    public void Under_the_defaults_on_a_Rich_store_there_is_no_line_to_change()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5))], mode: EventAppendMode.Rich)).Findings.Should().ContainSingle().Subject;

        finding.Remedy.Should().BeNull();
        finding.WhyItMatters.Should().Contain("tombstones (E02)");
    }

    [Fact]
    public void With_the_evidence_off_a_skip_in_the_last_hour_is_a_warning_that_events_may_be_lost()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5))], evidence: false)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.WhyItMatters.Should().Contain("none of them will ever see those events").And.Contain("on the clock alone");
        finding.Remedy!.Code.Should().Be("opts.Projections.UseTransactionEvidenceForGapSkipping = true;");
    }

    [Fact]
    public void With_the_cap_set_a_skip_in_the_last_hour_is_a_warning_and_the_remedy_is_the_cap()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5))], cap: TimeSpan.FromMinutes(30))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Projections.SkipStaleGapsDespiteLiveTransactionsAfter", "30 min"));
        finding.Remedy!.Code.Should().Be("opts.Projections.SkipStaleGapsDespiteLiveTransactionsAfter = null;");
    }

    [Fact]
    public void A_risky_configuration_with_only_older_skips_today_is_an_info()
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromHours(5))], evidence: false)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info, "a Warning is for a skip in the last hour");
        finding.Title.Should().EndWith("in the last day");
    }

    [Theory]
    [InlineData("9.39.0", true)]
    [InlineData("9.46.0", true)]
    [InlineData("9.47.0", false)]
    [InlineData("9.50.2", false)]
    public void Below_Marten_9_47_the_caveat_says_the_evidence_is_server_wide_and_errs_toward_holding(string marten, bool said)
    {
        HealthFinding finding = Evaluate(Facts([Skip(TimeSpan.FromMinutes(5))]), marten).Findings.Should().ContainSingle().Subject;

        finding.Caveat!.Contains("every database on the server (#5605)", StringComparison.Ordinal).Should().Be(said);
        finding.Caveat.Should().NotContain("can be skipped even under the defaults", "the old caveat had the direction backwards");

        if (said)
        {
            finding.Caveat.Should().Contain("errs toward holding the mark");
        }
    }

    [Fact]
    public void A_tenant_partitioned_store_is_told_its_per_tenant_marks_record_no_skips()
    {
        Evaluate(Facts(tenantPartitioned: true)).Note.Should().Contain("per-tenant marks record no skips");
    }

    [Fact]
    public void A_full_read_says_or_more()
    {
        IEnumerable<SkippedGap> hundred = Enumerable.Range(0, 100).Select(static x => Skip(TimeSpan.FromSeconds(x + 1), x * 10, x * 10 + 5));

        HealthFinding finding = Evaluate(Facts(hundred)).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Contain("100 or more times");
    }
}
