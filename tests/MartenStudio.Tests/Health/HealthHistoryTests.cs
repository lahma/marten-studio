using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Health;

/// <summary>The history: a bounded ring per database, rates over the window, and honesty before there is one.</summary>
public class HealthHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static HealthSample Sample(int minute, params (string Series, double Value)[] values) =>
        new(Start.AddMinutes(minute), values.ToDictionary(static x => x.Series, static x => x.Value));

    [Fact]
    public void A_database_keeps_sixty_samples_and_drops_the_oldest()
    {
        HealthHistory history = new();

        for (int minute = 0; minute < 75; minute++)
        {
            history.Append("default", "db", Sample(minute, ("s", minute)));
        }

        HealthHistoryView view = history.Read("default", "db");
        view.Samples.Should().HaveCount(HealthHistory.Capacity);
        view.Samples[0].At.Should().Be(Start.AddMinutes(15));
        view.Since.Should().Be(Start, "watching began at the first sweep, whatever the ring has since dropped");
    }

    /// <summary>
    /// Review item 2/17: a sample no newer than the newest one held - a sweep that finished out of order - and
    /// one closer than the spacing - a page and the health check sweeping the same database - are dropped.
    /// </summary>
    [Fact]
    public void A_sample_that_is_not_newer_or_too_close_is_dropped()
    {
        HealthHistory history = new();
        TimeSpan spacing = TimeSpan.FromSeconds(30);

        history.Append("default", "db", Sample(1, ("s", 1)), spacing).Should().BeTrue();
        history.Append("default", "db", Sample(0, ("s", 0)), spacing).Should().BeFalse("older than the newest");
        history.Append("default", "db", Sample(1, ("s", 2)), spacing).Should().BeFalse("not newer");
        history.Append("default", "db", new HealthSample(Start.AddMinutes(1).AddSeconds(20), new Dictionary<string, double> { ["s"] = 3 }), spacing)
            .Should().BeFalse("twenty seconds after the newest, under half an interval");
        history.Append("default", "db", Sample(2, ("s", 4)), spacing).Should().BeTrue();

        history.Read("default", "db").Samples.Select(static x => x.Values["s"]).Should().Equal(1, 4);
    }

    /// <summary>A sweep whose checks contribute no numbers marks when watching began, and adds no sample.</summary>
    [Fact]
    public void A_sample_with_no_numbers_only_marks_when_watching_began()
    {
        HealthHistory history = new();

        history.Append("default", "db", Sample(0)).Should().BeFalse();
        history.Append("default", "db", Sample(3)).Should().BeFalse();

        HealthHistoryView view = history.Read("default", "db");
        view.Samples.Should().BeEmpty();
        view.Since.Should().Be(Start);
        view.Describe(Start.AddMinutes(4)).Should().StartWith("Watching for 4 min").And.Contain("No check here measures a rate yet");
    }

    /// <summary>
    /// CL1 item 2, the fail-closed branch: a series whose writer's gate was never recorded is read as
    /// <see cref="HealthGate.Activity" />. A sample with numbers and no gates map at all is therefore invisible to an
    /// evaluator at <see cref="HealthGate.None" /> or <see cref="HealthGate.Database" /> - the sample itself is dropped,
    /// because nothing in it is left - and whole at <see cref="HealthGate.Activity" />.
    /// </summary>
    [Fact]
    public void A_series_with_no_recorded_gate_is_read_only_at_the_activity_gate()
    {
        HealthSample ungated = Sample(0, ("connections.by-role:reporting", 4), ("tables.rows", 1_000));
        ungated.Gates.Should().BeEmpty("the premise: nobody stamped a gate on this sample");
        ungated.GateOf("tables.rows").Should().Be(HealthGate.Activity, "an unknown writer is the strictest gate");

        HealthHistoryView history = new([ungated], Start);

        history.VisibleAt(HealthGate.None).Samples.Should().BeEmpty("no series in it is known to be readable at None");
        history.VisibleAt(HealthGate.Database).Samples.Should().BeEmpty("nor at Database");
        history.VisibleAt(HealthGate.None).Since.Should().Be(Start, "hiding the numbers does not move when watching began");

        HealthSample whole = history.VisibleAt(HealthGate.Activity).Samples.Should().ContainSingle().Subject;
        whole.Values.Should().BeEquivalentTo(ungated.Values);
    }

    /// <summary>The same rule series by series: the stamped series is read at its gate, the unstamped one is not.</summary>
    [Fact]
    public void Only_the_series_whose_gate_was_recorded_cross_a_looser_gate()
    {
        HealthSample mixed = Sample(0, ("tables.rows", 1_000), ("connections.by-role:reporting", 4)) with
        {
            Gates = new Dictionary<string, HealthGate>(StringComparer.Ordinal) { ["tables.rows"] = HealthGate.None },
        };

        HealthHistoryView history = new([mixed]);

        HealthSample atNone = history.VisibleAt(HealthGate.None).Samples.Should().ContainSingle().Subject;
        atNone.Values.Keys.Should().Equal("tables.rows");
        atNone.GateOf("tables.rows").Should().Be(HealthGate.None);

        history.VisibleAt(HealthGate.Database).Samples.Single().Values.Keys.Should().Equal("tables.rows");
        history.VisibleAt(HealthGate.Activity).Samples.Single().Values.Should().HaveCount(2);
    }

    [Fact]
    public void Two_databases_are_two_histories()
    {
        HealthHistory history = new();
        history.Append("default", "one", Sample(0));
        history.Append("default", "two", Sample(0));
        history.Append("other", "one", Sample(0));

        history.DatabaseCount.Should().Be(3);
        history.Read("default", "three").Should().BeSameAs(HealthHistoryView.Empty);
    }

    [Fact]
    public void A_rate_is_over_the_whole_window_and_needs_two_samples()
    {
        HealthHistoryView one = new([Sample(0, ("seq", 100))]);
        one.Trend("seq").HasRate.Should().BeFalse();
        one.Trend("seq").PerSecond.Should().BeNull();

        HealthHistoryView three = new([Sample(0, ("seq", 100)), Sample(1, ("seq", 100)), Sample(2, ("seq", 1_300))]);
        three.Trend("seq").PerSecond.Should().Be(10, "1,200 over two minutes - one quiet step must not read as a stall");
        three.Trend("seq").Watched.Should().Be(TimeSpan.FromMinutes(2));
        three.Trend("seq").Latest.Should().Be(1_300);
    }

    /// <summary>A sample from a sweep that did not run the check simply lacks the series, and is not a zero.</summary>
    [Fact]
    public void A_sample_without_the_series_is_skipped_rather_than_read_as_zero()
    {
        HealthHistoryView view = new([Sample(0, ("seq", 100)), Sample(1, ("other", 5)), Sample(2, ("seq", 220))]);

        view.Trend("seq").Points.Should().HaveCount(2);
        view.Trend("seq").PerSecond.Should().Be(1);
    }

    [Fact]
    public void Before_two_samples_the_page_says_how_long_it_has_watched()
    {
        HealthHistoryView.Empty.Describe(Start).Should().StartWith("Not watched yet");

        new HealthHistoryView([Sample(0)]).Describe(Start.AddSeconds(30))
            .Should().Contain("under a minute").And.Contain("one sample");

        new HealthHistoryView([Sample(0), Sample(2)]).Describe(Start.AddMinutes(4))
            .Should().StartWith("Watching for 4 min").And.Contain("2 samples").And.Contain("in memory");
    }

    [Fact]
    public void The_studio_cost_meter_sums_the_last_five_minutes_and_forgets_the_rest()
    {
        FakeTimeProvider clock = new(Start);
        StudioDatabaseTime meter = new(clock);

        meter.Record("db", TimeSpan.FromMilliseconds(300));
        clock.Advance(TimeSpan.FromMinutes(2));
        meter.Record("db", TimeSpan.FromMilliseconds(200));
        meter.Record("db", TimeSpan.Zero);

        StudioCost now = meter.Total("db");
        now.Total.Should().Be(TimeSpan.FromMilliseconds(500));
        now.Sessions.Should().Be(2, "a session that took no time adds no time");
        now.Window.Should().Be(TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(4));
        meter.Total("db").Should().Be(new StudioCost(TimeSpan.FromMilliseconds(200), 1, TimeSpan.FromMinutes(5)),
            "the first session is more than five minutes old");

        clock.Advance(TimeSpan.FromHours(1));
        meter.Total("db").Sessions.Should().Be(0);
    }

    [Fact]
    public void A_bucket_that_comes_round_again_is_reset_rather_than_added_to()
    {
        FakeTimeProvider clock = new(Start);
        StudioDatabaseTime meter = new(clock);

        meter.Record("db", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromMinutes(5));
        meter.Record("db", TimeSpan.FromSeconds(2));

        meter.Total("db").Should().Be(new StudioCost(TimeSpan.FromSeconds(2), 1, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// Review items 5 and 15: the cost is per database - a visitor of one does not see the studio's work in
    /// another - and the cumulative counts let a check subtract the studio's own sessions and rollbacks.
    /// </summary>
    [Fact]
    public void The_meter_is_per_database_and_counts_every_session_and_rollback_ever()
    {
        FakeTimeProvider clock = new(Start);
        StudioDatabaseTime meter = new(clock);

        meter.Record("one", TimeSpan.FromSeconds(1));
        meter.Record("one", TimeSpan.Zero);
        meter.Record("one", TimeSpan.FromSeconds(1), rolledBack: false);
        meter.Record("two", TimeSpan.FromSeconds(5));

        meter.Total("one").Total.Should().Be(TimeSpan.FromSeconds(2));
        meter.Total("two").Total.Should().Be(TimeSpan.FromSeconds(5));
        meter.Total("three").Sessions.Should().Be(0);

        clock.Advance(TimeSpan.FromHours(2));

        meter.SessionsEver("one").Should().Be(3, "cumulative, whatever the window has forgotten");
        meter.RollbacksEver("one").Should().Be(2, "one session's rollback never reached the server");
        meter.SessionsEver("three").Should().Be(0);
    }

    [Fact]
    public void A_database_is_keyed_by_host_port_and_name_and_never_by_its_password()
    {
        using Npgsql.NpgsqlConnection connection = new("Host=db.example;Port=5433;Database=orders;Username=app;Password=secret");

        string key = StudioDatabaseTime.KeyOf(connection);

        key.Should().Be("db.example:5433/orders");
        key.Should().NotContain("secret").And.NotContain("app");
    }
}
