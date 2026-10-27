using MartenStudio.Services.Projections;

using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// M2's memory: one sample per spacing however many circuits read, bounded per shard, per database and - through
/// <see cref="HealthDatabaseBound" /> - per process.
/// </summary>
public class ProjectionLagHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(5);

    private static void Read(ProjectionLagHistory history, int seconds, long highWater, params (string Shard, long Position)[] shards) =>
        history.Append("default", "db", Start + TimeSpan.FromSeconds(seconds), highWater, shards, Spacing);

    [Fact]
    public void A_read_records_each_shard_against_the_high_water()
    {
        ProjectionLagHistory history = new();

        Read(history, 0, 1_000, ("Orders:All", 400), ("Totals:All", 1_000));

        history.Read("default", "db", "Orders:All").Should().Equal(new LagPoint(Start, 400, 1_000));
        history.Read("default", "db", "Totals:All").Single().Lag.Should().Be(0);
        history.Read("default", "db", "Missing:All").Should().BeEmpty();
        history.Read("default", "other", "Orders:All").Should().BeEmpty("a database's samples are its own");
    }

    /// <summary>
    /// Five circuits polling out of step share one snapshot read a second: the history keeps one sample per spacing, and a
    /// poll that comes a little early is still the next one.
    /// </summary>
    [Fact]
    public void Reads_closer_together_than_four_fifths_of_the_spacing_keep_one_sample()
    {
        ProjectionLagHistory history = new();

        Read(history, 0, 100, ("Orders:All", 10));
        Read(history, 1, 100, ("Orders:All", 11));
        Read(history, 3, 100, ("Orders:All", 13));
        Read(history, 4, 100, ("Orders:All", 14));
        Read(history, 4, 100, ("Orders:All", 99));

        history.Read("default", "db", "Orders:All").Select(static x => x.Position).Should().Equal(10, 14);
    }

    [Fact]
    public void A_sample_older_than_the_newest_is_not_kept()
    {
        ProjectionLagHistory history = new();

        Read(history, 10, 100, ("Orders:All", 50));
        Read(history, 0, 100, ("Orders:All", 10));

        history.Read("default", "db", "Orders:All").Should().ContainSingle().Which.Position.Should().Be(50);
    }

    [Fact]
    public void A_shard_keeps_at_most_its_capacity_oldest_dropped_first()
    {
        ProjectionLagHistory history = new();

        for (int i = 0; i < ProjectionLagHistory.Capacity + 10; i++)
        {
            Read(history, 5 * i, 10_000, ("Orders:All", i));
        }

        IReadOnlyList<LagPoint> points = history.Read("default", "db", "Orders:All");
        points.Should().HaveCount(ProjectionLagHistory.Capacity);
        points[0].Position.Should().Be(10);
        points[^1].Position.Should().Be(ProjectionLagHistory.Capacity + 9);
    }

    /// <summary>Past its shard bound a database keeps the shards furthest behind - the ones worth a chart - and no more.</summary>
    [Fact]
    public void A_database_keeps_at_most_its_shard_bound_the_furthest_behind_first()
    {
        ProjectionLagHistory history = new();

        (string, long)[] shards = [.. Enumerable.Range(0, ProjectionLagHistory.MaxShards + 5).Select(static i => ("P" + i + ":All", (long) i))];
        Read(history, 0, 10_000, shards);

        history.Read("default", "db", "P0:All").Should().ContainSingle("position 0 is the furthest behind");
        history.Read("default", "db", "P" + (ProjectionLagHistory.MaxShards - 1) + ":All").Should().ContainSingle();
        history.Read("default", "db", "P" + (ProjectionLagHistory.MaxShards + 4) + ":All").Should().BeEmpty("the nearest are left out");
    }

    [Fact]
    public void A_shard_nobody_has_read_for_twice_the_window_is_dropped()
    {
        ProjectionLagHistory history = new();

        Read(history, 0, 100, ("Removed:All", 10), ("Orders:All", 10));
        int later = (int) (Spacing.TotalSeconds * ProjectionLagHistory.Capacity * 2) + 5;
        Read(history, later, 100, ("Orders:All", 20));

        history.Read("default", "db", "Removed:All").Should().BeEmpty();
        history.Read("default", "db", "Orders:All").Should().HaveCount(2);
    }

    [Fact]
    public void The_spacing_is_the_refresh_interval_and_never_less_than_its_floor()
    {
        ProjectionLagHistory history = new();

        history.SpacingFor(TimeSpan.FromSeconds(1)).Should().Be(TimeSpan.FromSeconds(5));
        history.SpacingFor(TimeSpan.FromSeconds(30)).Should().Be(TimeSpan.FromSeconds(30));
        new ProjectionLagHistory(null, TimeSpan.FromMilliseconds(500)).SpacingFor(TimeSpan.FromSeconds(1)).Should().Be(TimeSpan.FromSeconds(1));

        (ProjectionLagHistory.DefaultSpacingFloor * ProjectionLagHistory.Capacity).Should().Be(
            MartenStudio.Services.Health.Checks.DaemonThresholds.TrendWindow,
            "ten minutes of samples at the default: the daemon checks' own trend window");
    }

    /// <summary>A read touches the database in the process's bound, and the bound's eviction forgets its samples.</summary>
    [Fact]
    public void The_process_bound_forgets_the_least_recently_read_database()
    {
        HealthDatabaseBound bound = new(capacity: 2);
        ProjectionLagHistory history = new(bound);

        history.Append("default", "a", Start, 10, [("Orders:All", 1)], Spacing);
        history.Append("default", "b", Start, 10, [("Orders:All", 1)], Spacing);
        history.DatabaseCount.Should().Be(2);

        history.Append("default", "c", Start, 10, [("Orders:All", 1)], Spacing);

        bound.Count.Should().Be(2);
        history.DatabaseCount.Should().Be(2);
        history.Read("default", "a", "Orders:All").Should().BeEmpty("a was read least recently");
        history.Read("default", "c", "Orders:All").Should().ContainSingle();
    }
}
