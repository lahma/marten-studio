using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C11's judgement at each of the hosts' boundaries (issue #8 G5, W5-CAP), with no Postgres: below 95 % with at least a million
/// blocks read a day, over a statistics window of at least a day that does not look reset.
/// </summary>
public class CacheHitCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static HealthVerdict Evaluate(long hit, long read, TimeSpan window, bool looksReset = false)
    {
        StatisticsReading reading = new() { ReadAt = Now, BlocksHit = hit, BlocksRead = read };
        StatisticsWindow statistics = new()
        {
            MeasuredAt = Now,
            Since = Now - window,
            Basis = StatisticsBasis.ServerReset,
            LooksReset = looksReset,
            Signal = looksReset ? "the timeline went from 1 to 2" : null,
        };

        return new CacheHitCheck().Evaluate(
            new CacheHitFacts(reading, PostgresPlatform.SelfHosted),
            HealthEvaluation.At(Now) with { Statistics = statistics });
    }

    [Fact]
    public void Below_95_percent_with_a_million_blocks_read_a_day_is_a_warning()
    {
        // Two days: 2 000 000 read, 37 000 000 hit - 94.9 % found, a million read a day.
        HealthFinding finding = Evaluate(hit: 37_000_000, read: 2_000_000, TimeSpan.FromDays(2)).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("C11");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().BeNull("one finding per database, about no object");
        finding.Title.Should().StartWith("94.9% of this database's block reads were found in shared buffers since 2026-10-24 12:00 UTC");
        finding.Evidence.Should().Contain(new HealthEvidence("Cache hit ratio", "94.9%"));
        finding.Evidence.Should().Contain(new HealthEvidence("Blocks read a day", "1,000,000"));
        finding.Evidence.Select(static x => x.Label).Should().Contain(["Window", "Counting since"]);
        finding.Remedy.Should().BeNull("a remedy in words: no setting is suggested (R1)");
        finding.WhyItMatters.Should().Contain("more memory for the database, or less to hold in it");
    }

    [Fact]
    public void Exactly_95_percent_is_quiet()
    {
        Evaluate(hit: 38_000_000, read: 2_000_000, TimeSpan.FromDays(2)).Findings.Should().BeEmpty("the rule is below 95 %");
    }

    [Fact]
    public void Under_a_million_blocks_read_a_day_is_quiet_however_low_the_ratio()
    {
        HealthVerdict verdict = Evaluate(hit: 10, read: 1_999_998, TimeSpan.FromDays(2));

        verdict.Findings.Should().BeEmpty("999 999 a day is under the hosts' volume");
        verdict.Note.Should().Contain("judged only below 95.0% with at least 1,000,000 blocks read a day");
    }

    [Fact]
    public void Under_a_day_of_statistics_it_watches_and_finds_nothing()
    {
        HealthVerdict verdict = Evaluate(hit: 0, read: 50_000_000, TimeSpan.FromHours(23));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().StartWith("Watching: 23 h of statistics so far").And.Contain("the cache hit ratio is judged once they have counted for a day");
    }

    [Fact]
    public void While_the_statistics_look_reset_it_watches_whatever_the_window()
    {
        HealthVerdict verdict = Evaluate(hit: 0, read: 50_000_000, TimeSpan.FromDays(30), looksReset: true);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("look reset").And.Contain("the timeline went from 1 to 2");
    }

    [Fact]
    public void Nothing_read_is_quiet_and_says_so()
    {
        Evaluate(hit: 0, read: 0, TimeSpan.FromDays(3)).Note.Should().StartWith("Nothing was read in this database since");
    }

    [Fact]
    public void Without_a_window_from_the_sweep_it_computes_one_from_its_own_reading()
    {
        StatisticsReading reading = new()
        {
            ReadAt = Now,
            SharedReset = Now.AddDays(-3),
            ServerStartedAt = Now.AddDays(-3),
            BlocksHit = 1,
            BlocksRead = 9_000_000,
        };

        HealthVerdict verdict = new CacheHitCheck().Evaluate(new CacheHitFacts(reading, PostgresPlatform.SelfHosted), HealthEvaluation.At(Now));

        verdict.Findings.Should().ContainSingle().Which.Evidence.Should().Contain(new HealthEvidence("Blocks read a day", "3,000,000"));
    }

    [Fact]
    public void It_is_the_connections_areas_catalog_check_behind_the_database_wide_gate()
    {
        HealthCheckAttribute attribute = (HealthCheckAttribute) Attribute.GetCustomAttribute(typeof(CacheHitCheck), typeof(HealthCheckAttribute))!;

        attribute.Code.Should().Be("C11");
        attribute.Area.Should().Be(HealthArea.Connections);
        attribute.Tier.Should().Be(HealthCostTier.Catalog);
        attribute.Gate.Should().Be(HealthGate.Database);
        attribute.Title.Should().Be("Low cache hit ratio");
    }
}
