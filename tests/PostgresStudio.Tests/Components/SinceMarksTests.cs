using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The circuit's since-marks (1.2-H-A, X6): per instance (so per circuit), keyed by page, store and database with the tenant
/// ignored, at most sixteen with the least recently set dropped first, and never reaching anything outside the instance.
/// </summary>
public class SinceMarksTests
{
    private sealed record Mark(int Value);

    private sealed record OtherMark(int Value);

    private static readonly StudioScope Here = new("default", "app", null);

    [Fact]
    public void A_mark_is_kept_per_instance_so_another_circuit_sees_none()
    {
        SinceMarks mine = new();
        SinceMarks theirs = new();

        mine.Set("statements", Here, new Mark(1));

        mine.Get<Mark>("statements", Here).Should().Be(new Mark(1));
        theirs.Get<Mark>("statements", Here).Should().BeNull();
    }

    [Fact]
    public void A_mark_is_keyed_by_page_store_and_database_and_the_tenant_is_ignored()
    {
        SinceMarks marks = new();
        marks.Set("statements", Here, new Mark(1));

        marks.Get<Mark>("statements", Here with { TenantId = "acme" }).Should().Be(new Mark(1), "all three pages are database-wide");
        marks.Get<Mark>("storage", Here).Should().BeNull();
        marks.Get<Mark>("statements", Here with { DatabaseId = "other" }).Should().BeNull();
        marks.Get<Mark>("statements", Here with { StoreKey = "other" }).Should().BeNull();
        marks.Get<OtherMark>("statements", Here).Should().BeNull("a mark of another type is not this page's");
    }

    [Fact]
    public void Setting_again_replaces_and_clear_forgets()
    {
        SinceMarks marks = new();
        marks.Set("statements", Here, new Mark(1));
        marks.Set("statements", Here, new Mark(2));

        marks.Get<Mark>("statements", Here).Should().Be(new Mark(2));

        marks.Clear("statements", Here with { TenantId = "acme" });

        marks.Get<Mark>("statements", Here).Should().BeNull();
        marks.Clear("statements", Here);
    }

    [Fact]
    public void At_most_sixteen_are_kept_and_the_least_recently_set_goes_first()
    {
        SinceMarks marks = new();

        for (int i = 0; i < SinceMarks.Capacity; i++)
        {
            marks.Set("statements", Here with { DatabaseId = "db" + i }, new Mark(i));
        }

        // Setting db0 again makes db1 the least recently set.
        marks.Set("statements", Here with { DatabaseId = "db0" }, new Mark(100));
        marks.Set("statements", Here with { DatabaseId = "extra" }, new Mark(200));

        SinceMarks.Capacity.Should().Be(16);
        marks.Get<Mark>("statements", Here with { DatabaseId = "db1" }).Should().BeNull();
        marks.Get<Mark>("statements", Here with { DatabaseId = "db0" }).Should().Be(new Mark(100));
        marks.Get<Mark>("statements", Here with { DatabaseId = "extra" }).Should().Be(new Mark(200));

        Enumerable.Range(0, SinceMarks.Capacity)
            .Count(i => marks.Get<Mark>("statements", Here with { DatabaseId = "db" + i }) is not null)
            .Should().Be(SinceMarks.Capacity - 1);
    }

    [Fact]
    public void A_mark_ends_when_the_statistics_window_now_begins_after_it()
    {
        DateTimeOffset at = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        StatisticsWindow Window(DateTimeOffset since) => new()
        {
            MeasuredAt = at.AddHours(1),
            Since = since,
            Basis = StatisticsBasis.ServerReset,
            Reason = StatisticsResetReason.Failover,
            SharedView = "pg_stat_checkpointer",
        };

        static string Spelt(SinceMarkEnd? ended) => ended!.Text(static moment => moment.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));

        SinceMarks.Ended(at, at.AddDays(-1), Window(at.AddDays(-1))).Should().BeNull();
        SinceMarks.Ended(at, null, Window(at)).Should().BeNull("a window that began at the mark's own moment is the mark's own");

        SinceMarkEnd? began = SinceMarks.Ended(at, at.AddDays(-1), Window(at.AddSeconds(1)));
        Spelt(began).Should().Be("the statistics began again at 2026-10-26 09:00 (" + Window(at).ReasonText + ")");
        began!.At.Should().Be(at.AddSeconds(1), "the service returns the moment; the page spells it in its visitor's zone");

        SinceMarks.Ended(at, at.AddDays(-1), null).Should().BeNull();
        SinceMarks.Caveat(null).Should().Contain("could not be read");
        SinceMarks.Caveat(Window(at)).Should().BeNull();
    }

    [Fact]
    public void A_mark_also_ends_when_the_windows_start_moved_by_more_than_the_same_events_slack_in_either_direction()
    {
        DateTimeOffset at = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        DateTimeOffset since = at.AddDays(-2);

        StatisticsWindow Window(DateTimeOffset start) => new()
        {
            MeasuredAt = at.AddHours(1),
            Since = start,
            Basis = StatisticsBasis.ServerReset,
            Reason = StatisticsResetReason.OtherInstance,
            SharedView = "pg_stat_checkpointer",
        };

        SinceMarks.Ended(at, since, Window(since.AddDays(-3))).Should().NotBeNull("an earlier start: a failover or an instance switch");
        SinceMarks.Ended(at, since, Window(since.AddSeconds(-2))).Should().BeNull();
        SinceMarks.Ended(at, since, Window(since.AddSeconds(2))).Should().BeNull();
        SinceMarks.Ended(at, since, Window(since.AddSeconds(3))).Should().NotBeNull();
        SinceMarks.Ended(at, null, Window(since.AddDays(-3))).Should().BeNull("a mark taken with no window read has none to compare");
        SinceMarks.MovedSince(since, Window(since.AddDays(-3))).Should().BeTrue();
    }

    [Fact]
    public void Nothing_in_it_is_static_so_nothing_outlives_or_crosses_a_circuit()
    {
        typeof(SinceMarks).GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Where(static x => !x.IsLiteral)
            .Should().BeEmpty();
    }
}
