using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.EventHealthFacts;

namespace MartenStudio.Tests.Health;

/// <summary>D08's judgement over a stated progression row, the advisor's history of it and what this studio did.</summary>
public class RebuiltElsewhereCheckTests
{
    private const string Shard = "DailySales:All";

    private static readonly string Series = RebuiltElsewhereCheck.Series(Shard);

    private static ShardProgressReading Reading(long? position, string? mode = "none", IReadOnlyList<Type>? types = null) =>
        new(Shard, "DailySales", position, Now.AddMinutes(-1), mode, types ?? []);

    private static RebuildFacts Facts(
        ShardProgressReading reading,
        long? mark = 1_000,
        IEnumerable<StudioRebuild>? rebuilds = null,
        IEnumerable<StudioRewind>? rewinds = null,
        bool rebuildingHere = false) =>
        new(
            true,
            [reading],
            mark,
            Now,
            [.. rebuilds ?? []],
            [.. rewinds ?? []],
            rebuildingHere ? new HashSet<string>(StringComparer.Ordinal) { Shard } : new HashSet<string>(StringComparer.Ordinal),
            AskedDaemon: false);

    private static HealthVerdict Evaluate(RebuildFacts facts, params (DateTimeOffset At, double Value)[] points) =>
        new RebuiltElsewhereCheck().Evaluate(facts, At(History(Series, points)));

    private static DateTimeOffset Ago(int minutes) => Now.AddMinutes(-minutes);

    [Fact]
    public void A_store_with_nothing_async_is_quiet()
    {
        new RebuiltElsewhereCheck()
            .Evaluate(new RebuildFacts(false, [], null, Now, [], [], new HashSet<string>(), false), At())
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void One_sample_is_watching_rather_than_fine()
    {
        HealthVerdict verdict = Evaluate(Facts(Reading(10)), (Now, 10));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("two samples");
    }

    [Fact]
    public void A_position_that_only_ever_grows_is_quiet()
    {
        Evaluate(Facts(Reading(900)), (Ago(3), 500), (Ago(2), 700), (Now, 900)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_fall_nothing_here_explains_is_an_info_that_names_the_shard()
    {
        HealthFinding finding = Evaluate(Facts(Reading(null)), (Ago(2), 900), (Now, 0)).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D08");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be(Shard);
        finding.Title.Should().Be("Shard DailySales:All went back, and nothing in this studio did it");
        finding.WhyItMatters.Should().Contain("would race it");
        finding.Evidence.Should().Contain(static x => x.Label == "Position fell" && x.Value.StartsWith("from #900 to #0", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("Position now", "no progression row"));
        finding.Caveat.Should().Contain("only while a Health page or the Overview is open");
    }

    [Fact]
    public void A_fall_that_climbs_again_is_being_replayed_somewhere()
    {
        HealthFinding finding = Evaluate(Facts(Reading(400)), (Ago(3), 900), (Ago(2), 0), (Now, 400)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Contain("is being replayed");
    }

    [Fact]
    public void A_fall_that_has_stood_still_below_the_mark_for_ten_minutes_stopped_half_way()
    {
        HealthFinding finding = Evaluate(
                Facts(Reading(400)),
                (Ago(30), 900), (Ago(25), 0), (Ago(20), 400), (Ago(10), 400), (Now, 400))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("stopped part-way");
        finding.WhyItMatters.Should().Contain("partial projection").And.Contain("20 min");
    }

    [Fact]
    public void A_shard_back_at_the_mark_is_quiet_however_it_got_there()
    {
        Evaluate(Facts(Reading(1_000)), (Ago(3), 900), (Ago(2), 0), (Now, 1_000)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_rebuild_this_process_is_running_explains_the_fall()
    {
        Evaluate(Facts(Reading(0), rebuildingHere: true), (Ago(2), 900), (Now, 0)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_studio_rebuild_still_running_explains_the_fall()
    {
        StudioRebuild running = new("DailySales", Ago(3), null, StudioOperationState.Running);

        Evaluate(Facts(Reading(0), rebuilds: [running]), (Ago(2), 900), (Now, 0)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_studio_rebuild_that_succeeded_around_the_fall_explains_it()
    {
        StudioRebuild done = new("dailysales", Ago(3), Ago(1), StudioOperationState.Succeeded);

        Evaluate(Facts(Reading(500), rebuilds: [done]), (Ago(4), 900), (Ago(2), 0), (Now, 500))
            .Findings.Should().BeEmpty("projection names are matched without regard to case, as the studio's operations name them");
    }

    [Theory]
    [InlineData(nameof(StudioOperationState.Failed))]
    [InlineData(nameof(StudioOperationState.Cancelled))]
    public void A_studio_rebuild_that_ended_short_of_the_mark_is_a_warning_naming_it(string ended)
    {
        StudioOperationState state = Enum.Parse<StudioOperationState>(ended);
        StudioRebuild rebuild = new("DailySales", Ago(3), Ago(1), state);

        HealthFinding finding = Evaluate(Facts(Reading(500), rebuilds: [rebuild]), (Ago(4), 900), (Ago(2), 0), (Now, 500))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("The rebuild of DailySales started from this studio ended " + state + ", short of the high-water mark");
        finding.Evidence.Should().Contain(static x => x.Label == "Studio rebuild");
    }

    [Fact]
    public void A_studio_rebuild_long_before_the_fall_does_not_explain_it()
    {
        StudioRebuild old = new("DailySales", Ago(60), Ago(50), StudioOperationState.Succeeded);

        Evaluate(Facts(Reading(0), rebuilds: [old]), (Ago(2), 900), (Now, 0)).Findings.Should().ContainSingle();
    }

    [Fact]
    public void A_studio_rewind_around_the_fall_explains_it()
    {
        StudioRewind rewind = new("DailySales", Ago(1));

        Evaluate(Facts(Reading(300), rewinds: [rewind]), (Ago(2), 900), (Now, 300)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_row_whose_mode_says_rebuilding_is_believed_without_any_history()
    {
        HealthFinding finding = new RebuiltElsewhereCheck().Evaluate(Facts(Reading(10, mode: "rebuilding")), At())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Contain("marked as rebuilding");
        finding.WhyItMatters.Should().Contain("No Marten release writes that column");
        finding.Evidence.Should().Contain(new HealthEvidence("mode", "rebuilding"));
    }

    [Fact]
    public void The_finding_carries_the_projections_document_types_so_a_hidden_one_takes_it_with_it()
    {
        HealthFinding finding = Evaluate(Facts(Reading(null, types: [typeof(HiddenHealthThing)])), (Ago(2), 900), (Now, 0))
            .Findings.Should().ContainSingle().Subject;

        finding.DocumentTypes.Should().Equal(typeof(HiddenHealthThing));
    }

    [Fact]
    public void A_shard_that_fell_and_climbed_back_past_where_it_was_is_quiet_though_still_below_the_mark()
    {
        // Under steady appends a healthy shard sits a few events behind the mark, so "below the mark" alone would keep
        // calling a finished replay unfinished. Back at or past its pre-fall position, whatever replayed it is done.
        Evaluate(Facts(Reading(950), mark: 1_000), (Ago(3), 900), (Ago(2), 0), (Now, 950)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_fall_in_the_same_interval_as_the_marks_is_a_progression_correction_not_a_rebuild()
    {
        // Marten's TryCorrectProgressInDatabaseAsync rewrites every progression row, the mark's included, when the mark
        // is ahead of the event sequence: shard and mark go back together.
        HealthHistoryView history = History(
            Series,
            RebuiltElsewhereCheck.MarkSeries,
            (Ago(3), 900, 1_000),
            (Ago(2), 500, 500),
            (Now, 500, 500));

        new RebuiltElsewhereCheck().Evaluate(Facts(Reading(480), mark: 500), At(history)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_mark_that_kept_rising_while_the_shard_fell_explains_nothing()
    {
        HealthHistoryView history = History(
            Series,
            RebuiltElsewhereCheck.MarkSeries,
            (Ago(3), 900, 1_000),
            (Ago(2), 0, 1_050),
            (Now, 300, 1_100));

        new RebuiltElsewhereCheck().Evaluate(Facts(Reading(300), mark: 1_100), At(history))
            .Findings.Should().ContainSingle().Which.Title.Should().Contain("is being replayed");
    }

    [Fact]
    public void A_mark_that_fell_in_another_interval_explains_nothing()
    {
        HealthHistoryView history = History(
            Series,
            RebuiltElsewhereCheck.MarkSeries,
            (Ago(4), 900, 1_000),
            (Ago(3), 900, 600),
            (Ago(2), 0, 650),
            (Now, 0, 700));

        new RebuiltElsewhereCheck().Evaluate(Facts(Reading(0), mark: 700), At(history)).Findings.Should().ContainSingle();
    }

    [Fact]
    public void Its_sample_is_the_position_with_a_missing_row_at_nothing_and_the_mark_beside_it()
    {
        new RebuiltElsewhereCheck().Sample(Facts(Reading(null))).Should()
            .Equal(new HealthSamplePart(Series, 0), new HealthSamplePart(RebuiltElsewhereCheck.MarkSeries, 1_000));
        new RebuiltElsewhereCheck().Sample(Facts(Reading(42), mark: null)).Should()
            .Equal([new HealthSamplePart(Series, 42)], "a mark that has no row yet is not sampled as nothing, which would read as a fall");
    }

    [Fact]
    public void The_latest_fall_is_the_one_judged_and_its_last_change_is_tracked()
    {
        HealthTrend trend = History(Series, (Ago(10), 900), (Ago(8), 100), (Ago(6), 600), (Ago(4), 50), (Ago(2), 70), (Now, 70))
            .Trend(Series);

        RebuiltElsewhereCheck.PositionFall fall = RebuiltElsewhereCheck.Fall(trend)!;

        fall.Before.Should().Be(600);
        fall.After.Should().Be(50);
        fall.Now.Should().Be(70);
        fall.Climbing.Should().BeTrue();
        fall.LastChange.Should().Be(Ago(2));
    }
}
