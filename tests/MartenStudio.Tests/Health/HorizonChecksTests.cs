using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>
/// T12's database half and its forecast, T13 and T14, over stated facts: every threshold and every sentence against
/// numbers a test chose, with no Postgres.
/// </summary>
public class HorizonChecksTests
{
    private const long FreezeMaxAge = 200_000_000;

    /// <summary>A healthy database's wraparound facts, which a case changes only where it is about.</summary>
    private static HealthWraparoundDatabase Database(Func<HealthWraparoundDatabase, HealthWraparoundDatabase>? shape = null)
    {
        HealthWraparoundDatabase database = new()
        {
            FreezeMaxAge = FreezeMaxAge,
            MultixactFreezeMaxAge = 400_000_000,
            FailsafeAge = 1_600_000_000,
            MultixactFailsafeAge = 1_600_000_000,
            ServerVersion = 170_000,
            DatabaseXidAge = 5_000,
            DatabaseMultixactAge = 10,
            ServerXidAge = 5_000,
            ServerMultixactAge = 10,
            XidCounter = 1_000_000,
            MultixactCounter = 100,
        };

        return shape is null ? database : shape(database);
    }

    private static WraparoundFacts Wraparound(Func<HealthWraparoundDatabase, HealthWraparoundDatabase>? shape = null) =>
        new(Database(shape), []);

    /// <summary>
    /// A history of a counter: <paramref name="perMinute" /> a minute over <paramref name="minutes" /> (two samples), with the
    /// series that say the age stayed past its forced line in both - so a forecast alone has persisted.
    /// </summary>
    private static HealthHistoryView XidRate(double perMinute, int minutes = 40, string series = MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries)
    {
        bool xid = series == MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries;
        string swept = xid ? MartenStudio.Services.Health.Checks.Wraparound.XidSweptSeries : MartenStudio.Services.Health.Checks.Wraparound.MultixactSweptSeries;
        string forced = xid ? MartenStudio.Services.Health.Checks.Wraparound.XidPastForcedSeries : MartenStudio.Services.Health.Checks.Wraparound.MultixactPastForcedSeries;

        return History(
            (TimeSpan.FromMinutes(minutes), [(series, 1_000_000), (swept, 1), (forced, 1)]),
            (TimeSpan.Zero, [(series, 1_000_000 + perMinute * minutes), (swept, 1), (forced, 1)]));
    }

    /// <summary>
    /// The transaction-id counter as T12 samples it: every <paramref name="every" /> over <paramref name="span" />, rising by
    /// <paramref name="perMinute" /> a minute and at <paramref name="latest" /> now - or, with a <paramref name="burst" />, quiet
    /// until the burst's last minutes and rising <paramref name="burstPerMinute" /> a minute in them. Never more samples than
    /// the history holds (<see cref="HealthHistory.Capacity" />): a bound from the samples that production could not reach
    /// proves nothing about production (H9 re-review 2, follow-up 2d).
    /// </summary>
    private static HealthHistoryView Counter(
        long latest,
        double perMinute,
        TimeSpan span,
        TimeSpan every,
        TimeSpan? burst = null,
        double burstPerMinute = 0)
    {
        List<(TimeSpan Ago, (string Series, double Value)[] Values)> samples = [];

        for (TimeSpan ago = span; ago >= TimeSpan.Zero; ago -= every)
        {
            double value = burst is { } recent
                ? latest - burstPerMinute * Math.Min(ago.TotalMinutes, recent.TotalMinutes) - perMinute * Math.Max(0, ago.TotalMinutes - recent.TotalMinutes)
                : latest - perMinute * ago.TotalMinutes;

            samples.Add((ago, [(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries, value)]));
        }

        samples.Count.Should().BeLessThanOrEqualTo(HealthHistory.Capacity, "the history holds no more samples than that");
        return History([.. samples]);
    }

    // -----------------------------------------------------------------------------------------------
    // The lines and the forecast
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_refusal_line_is_three_million_short_of_wraparound_from_14_and_one_million_on_13()
    {
        MartenStudio.Services.Health.Checks.Wraparound.RefusalAge(170_000).Should().Be(2_147_483_647 - 3_000_000);
        MartenStudio.Services.Health.Checks.Wraparound.RefusalAge(140_000).Should().Be(2_147_483_647 - 3_000_000);
        MartenStudio.Services.Health.Checks.Wraparound.RefusalAge(130_012).Should().Be(2_147_483_647 - 1_000_000);
    }

    [Fact]
    public void A_failsafe_below_105_percent_of_the_forced_line_is_raised_to_it_as_vacuum_does()
    {
        MartenStudio.Services.Health.Checks.Wraparound.EffectiveFailsafe(1_600_000_000, 400_000_000).Should().Be(1_600_000_000);
        MartenStudio.Services.Health.Checks.Wraparound.EffectiveFailsafe(100_000_000, 400_000_000).Should().Be(420_000_000);
        MartenStudio.Services.Health.Checks.Wraparound.EffectiveFailsafe(null, 400_000_000).Should().BeNull("PostgreSQL 13 has no failsafe");
    }

    [Fact]
    public void Below_two_samples_or_with_a_still_counter_there_is_no_forecast()
    {
        HorizonForecast.From(History((TimeSpan.Zero, [("tables.xid-counter", 5)])).Trend("tables.xid-counter")).Should().BeNull();
        HorizonForecast.From(XidRate(0).Trend("tables.xid-counter")).Should().BeNull("a counter that did not move gives no rate");
    }

    [Fact]
    public void A_counter_that_went_backwards_starts_the_window_again()
    {
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(20), [("tables.xid-counter", 9_000_000)]),
            (TimeSpan.FromMinutes(10), [("tables.xid-counter", 1_000)]),
            (TimeSpan.Zero, [("tables.xid-counter", 61_000)]));

        HorizonForecast forecast = HorizonForecast.From(history.Trend("tables.xid-counter"))!;

        forecast.Window.Should().Be(TimeSpan.FromMinutes(10), "a restore or a failover moved the counter back; the run starts there");
        forecast.PerSecond.Should().BeApproximately(100, 0.001);
        forecast.AtTheRate.Should().Be("at the rate of the last 10 minutes");
    }

    [Fact]
    public void A_forecast_says_nothing_beyond_ninety_days_and_never_more_than_whole_units()
    {
        HorizonForecast forecast = new(PerSecond: 100, Window: TimeSpan.FromMinutes(12));

        forecast.Until(0, 100 * 86_400 * 30).Should().Be(TimeSpan.FromDays(30));
        forecast.Until(0, 100L * 86_400 * 91).Should().BeNull("a window of minutes cannot support a forecast of months");
        forecast.Until(500, 400).Should().Be(TimeSpan.Zero);

        HorizonForecast.Span(TimeSpan.FromDays(6.6)).Should().Be("about 7 days");
        HorizonForecast.Span(TimeSpan.FromHours(30.2)).Should().Be("about 30 hours");
        HorizonForecast.Span(TimeSpan.FromMinutes(20)).Should().Be("under an hour");
        new HorizonForecast(1, TimeSpan.FromSeconds(45)).WindowText.Should().Be("the last 45 seconds");
        new HorizonForecast(1, TimeSpan.FromSeconds(90)).WindowText.Should().Be("the last minute");
    }

    // -----------------------------------------------------------------------------------------------
    // T12 Transaction-id age: the database's own age, and the forecast
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_counter_is_sampled_into_the_series_T12_forecasts_from()
    {
        new TransactionIdAgeCheck().Sample(new TransactionIdAgeFacts([], Wraparound(static x => x with { XidCounter = 4_242 })))
            .Should().Equal(new HealthSamplePart("tables.xid-counter", 4_242), new HealthSamplePart("tables.xid-forced.swept", 1));
        new TransactionIdAgeCheck().Sample(new TransactionIdAgeFacts([], Wraparound(static x => x with { XidCounter = 4_242, ServerXidAge = 210_000_000 })))
            .Should().Contain(new HealthSamplePart("tables.xid-forced", 1), "the server's oldest database is past autovacuum_freeze_max_age");
        new TransactionIdAgeCheck().Sample(new TransactionIdAgeFacts([])).Should().BeEmpty("facts without the database read sample nothing");
    }

    [Fact]
    public void A_database_held_up_by_a_table_that_is_not_the_stores_is_a_finding_that_names_no_table_but_the_stores()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        TransactionIdAgeFacts facts = new(
            [Table(orders, static row => row with { OldestXidAge = 30_000_000 })],
            Wraparound(static x => x with
            {
                DatabaseXidAge = 1_200_000_000,
                ServerXidAge = 1_200_000_000,
                OtherOldestXidAge = 1_200_000_000,
                OtherOldestIsTemporary = true,
                OtherPastForcedXid = 3,
                OtherTemporaryPastForcedXid = 1,
            }));

        HealthFinding finding = new TransactionIdAgeCheck().Evaluate(facts, Evaluation()).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().StartWith("This database (a table that is not the store's) is 1.2 billion transactions");
        finding.Evidence.Should().Contain(new HealthEvidence("This database's age(datfrozenxid)", "1,200,000,000"));
        finding.Evidence.Should().Contain(new HealthEvidence("Other tables past autovacuum_freeze_max_age",
            "3 (1 of them temporary), never named: they are not the store's"));
        finding.Evidence.Should().Contain(new HealthEvidence("Marten tables past the warning line", "0"));
        finding.WhyItMatters.Should().Contain("not one the store declares").And.Contain("only by the session that created it");
        finding.DocumentTypes.Should().BeEmpty("no table of the store's is past the line");
        finding.Subject.Should().BeNull();
    }

    /// <summary>
    /// XSO-fix2: a database-wide wraparound finding whose oldest table is an <c>ExtendedSchemaObjects</c> table in a schema of
    /// its own stays visible to every visitor of the check - a Critical "Postgres would stop accepting writes" must not
    /// vanish behind the database browser's gate - and names that table generically, never by its schema or name.
    /// </summary>
    [Theory]
    [InlineData("T12")]
    [InlineData("T13")]
    public void A_wraparound_finding_whose_oldest_table_is_outside_the_stores_schemas_stays_visible_and_names_it_generically(string code)
    {
        MartenTable gated = new("xso_own", "xso_things", MartenTableKind.Managed, OutsideStoreSchemas: true);

        HealthVerdict verdict = code == "T12"
            ? new TransactionIdAgeCheck().Evaluate(
                new TransactionIdAgeFacts([Table(gated, static row => row with { OldestXidAge = 1_700_000_000 })], Wraparound()),
                Evaluation())
            : new MultixactAgeCheck().Evaluate(Multixacts(null, new TableMultixactAge(gated, 1_700_000_000)), Evaluation());

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.GatedSchemas.Should().BeEmpty("the finding is the database's, and is shown at the check's own gate");
        finding.Title.Should().StartWith("A Marten-managed table outside the store's own schemas is ");

        string text = finding.Title + " " + finding.WhyItMatters + " " + finding.Subject + " " + finding.Caveat + " "
            + string.Join(" ", finding.Evidence.Select(static x => x.Label + " " + x.Value));
        text.Should().NotContain("xso_own").And.NotContain("xso_things");
    }

    [Fact]
    public void Another_database_on_the_server_past_the_critical_line_is_a_critical_finding_here_too()
    {
        HealthFinding finding = new TransactionIdAgeCheck()
            .Evaluate(new TransactionIdAgeFacts([], Wraparound(static x => x with { ServerXidAge = 1_700_000_000 })), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().StartWith("Another database on this server is 1.7 billion transactions");
        finding.WhyItMatters.Should().Contain("every database on the server, this one included");
        finding.Evidence.Should().Contain(new HealthEvidence("Oldest database on this server, age(datfrozenxid)", "1,700,000,000"));
    }

    [Fact]
    public void Past_the_forced_line_a_refusal_within_ninety_days_is_a_warning_worded_at_the_rate_of_the_window()
    {
        // 250 million, past the forced line: 1.89 billion to go at 10,000 a minute is about 130 days - quiet - and at
        // 20,000 a minute about 66 days.
        TransactionIdAgeFacts facts = new([], Wraparound(static x => x with { DatabaseXidAge = 250_000_000, ServerXidAge = 250_000_000 }));
        TransactionIdAgeCheck check = new();

        HealthVerdict slow = check.Evaluate(facts, Evaluation(XidRate(10_000)));
        slow.Findings.Should().BeEmpty();
        slow.Note.Should().Contain("past autovacuum_freeze_max_age").And.Contain("more than 90 days");

        HealthFinding finding = check.Evaluate(facts, Evaluation(XidRate(20_000))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().StartWith("At the rate of the last 40 minutes, Postgres would stop accepting writes in about 66 days");
        finding.Evidence.Should().Contain(static x => x.Label == "Writes refused in" && x.Value.StartsWith("about 66 days, at the rate of the last 40 minutes", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(static x => x.Label == "Transaction ids spent" && x.Value.Contains("about 1,200,000 transaction ids an hour", StringComparison.Ordinal));
        finding.Caveat.Should().Contain("linear").And.Contain("pg_snapshot_xmax(pg_current_snapshot())");
    }

    /// <summary>
    /// Follow-up 2: a forecast alone waits for the age to stay past the forced line for 30 minutes of samples. Routine
    /// anti-wraparound autovacuum - and template0 or postgres reaching the line, which ServerXidAge counts - crosses it and
    /// comes back, and a forecast that spoke on the first sample past it would flicker with every cycle.
    /// </summary>
    [Fact]
    public void A_forecast_alone_waits_for_the_age_to_stay_past_the_forced_line()
    {
        const string Counter = MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries;
        const string Swept = MartenStudio.Services.Health.Checks.Wraparound.XidSweptSeries;
        const string Forced = MartenStudio.Services.Health.Checks.Wraparound.XidPastForcedSeries;

        // template0 past the forced line at this sample, this database young: the server's oldest decides the age.
        TransactionIdAgeFacts facts = new([], Wraparound(static x => x with { DatabaseXidAge = 30_000_000, ServerXidAge = 210_000_000 }));
        TransactionIdAgeCheck check = new();

        // Ten minutes past it, after half an hour that was not.
        HealthHistoryView tenMinutes = History(
            (TimeSpan.FromMinutes(40), [(Counter, 1_000_000), (Swept, 1)]),
            (TimeSpan.FromMinutes(20), [(Counter, 1_400_000), (Swept, 1)]),
            (TimeSpan.FromMinutes(10), [(Counter, 1_600_000), (Swept, 1), (Forced, 1)]),
            (TimeSpan.Zero, [(Counter, 1_800_000), (Swept, 1), (Forced, 1)]));

        HealthVerdict waiting = check.Evaluate(facts, Evaluation(tenMinutes));
        waiting.Findings.Should().BeEmpty("ten minutes past the line is a routine anti-wraparound vacuum's span");
        waiting.Note.Should().Contain("past autovacuum_freeze_max_age for 10 min of samples").And.Contain("once it has been for 29 min 30 s")
            .And.Contain("writes would be refused in about 67 days")
            .And.NotContain("more than 90 days", "the refusal is 67 days away, and the note says so once (H9 re-review, fix 3)");

        // template0 crossing and coming back, sample after sample, for an hour: never thirty minutes in a row.
        HealthHistoryView flicker = History([.. Enumerable.Range(0, 7).Select(static i => (
            TimeSpan.FromMinutes(60 - (i * 10)),
            i % 2 == 0
                ? new[] { (Counter, 1_000_000 + (i * 200_000.0)), (Swept, 1.0), (Forced, 1.0) }
                : new[] { (Counter, 1_000_000 + (i * 200_000.0)), (Swept, 1.0) }))]);

        check.Evaluate(facts, Evaluation(flicker)).Findings.Should().BeEmpty("a line crossed and recrossed is a routine cycle, not a stuck freeze");

        // Past it for forty minutes: the forecast speaks.
        check.Evaluate(facts, Evaluation(XidRate(20_000))).Findings.Should().ContainSingle()
            .Which.Title.Should().StartWith("At the rate of the last 40 minutes, Postgres would stop accepting writes in");
    }

    /// <summary>
    /// Fix 4: how long a forecast alone waits is half what the history is sure to hold - its 60 samples, kept at least half an
    /// interval apart - and never more than 30 minutes, as D03's window is; a fixed 30 minutes would never be reached at a
    /// 15-second <c>HealthInterval</c>, whose ring may span under 15 minutes.
    /// </summary>
    [Theory]
    [InlineData(15, 442.5, "7 min 22 s")]
    [InlineData(30, 885, "14 min 45 s")]
    [InlineData(60, 1_770, "29 min 30 s")]
    [InlineData(120, 1_800, "30 min")]
    public void A_forecast_alone_waits_half_what_the_history_holds_and_at_most_thirty_minutes(int intervalSeconds, double seconds, string text)
    {
        TimeSpan persist = MartenStudio.Services.Health.Checks.Wraparound.ForecastPersistFor(TimeSpan.FromSeconds(intervalSeconds));

        persist.Should().Be(TimeSpan.FromSeconds(seconds));
        MartenStudio.Services.Health.Checks.Wraparound.PersistText(persist).Should().Be(text);
    }

    [Fact]
    public void At_a_short_interval_a_forecast_alone_speaks_once_it_has_held_for_half_the_ring()
    {
        const string Counter = MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries;
        const string Swept = MartenStudio.Services.Health.Checks.Wraparound.XidSweptSeries;
        const string Forced = MartenStudio.Services.Health.Checks.Wraparound.XidPastForcedSeries;

        // Past the forced line for twenty minutes, at 20,000 a minute: about 67 days to the refusal line.
        HealthHistoryView twentyMinutes = History(
            (TimeSpan.FromMinutes(20), [(Counter, 1_000_000), (Swept, 1), (Forced, 1)]),
            (TimeSpan.FromMinutes(10), [(Counter, 1_200_000), (Swept, 1), (Forced, 1)]),
            (TimeSpan.Zero, [(Counter, 1_400_000), (Swept, 1), (Forced, 1)]));

        HealthWraparoundDatabase past = Database(static x => x with { DatabaseXidAge = 210_000_000, ServerXidAge = 210_000_000 });
        TransactionIdAgeCheck check = new();

        HealthVerdict everyMinute = check.Evaluate(new TransactionIdAgeFacts([], new WraparoundFacts(past, [], TimeSpan.FromMinutes(1))), Evaluation(twentyMinutes));
        everyMinute.Findings.Should().BeEmpty("at a minute's interval it waits 29 min 30 s");
        everyMinute.Note.Should().Contain("once it has been for 29 min 30 s");

        check.Evaluate(new TransactionIdAgeFacts([], new WraparoundFacts(past, [], TimeSpan.FromSeconds(30))), Evaluation(twentyMinutes))
            .Findings.Should().ContainSingle("at 30 seconds it waits 14 min 45 s, and twenty minutes is past that")
            .Which.Title.Should().StartWith("At the rate of the last 20 minutes, Postgres would stop accepting writes in");

        // T13 the same, at 15 seconds.
        const string Multixacts = MartenStudio.Services.Health.Checks.Wraparound.MultixactCounterSeries;
        const string MultixactSwept = MartenStudio.Services.Health.Checks.Wraparound.MultixactSweptSeries;
        const string MultixactForced = MartenStudio.Services.Health.Checks.Wraparound.MultixactPastForcedSeries;

        HealthHistoryView tenMinutes = History(
            (TimeSpan.FromMinutes(10), [(Multixacts, 1_000_000), (MultixactSwept, 1), (MultixactForced, 1)]),
            (TimeSpan.Zero, [(Multixacts, 1_400_000), (MultixactSwept, 1), (MultixactForced, 1)]));

        HealthWraparoundDatabase multixacts = Database(static x => x with { DatabaseMultixactAge = 450_000_000, ServerMultixactAge = 450_000_000 });

        new MultixactAgeCheck().Evaluate(new MultixactAgeFacts(multixacts, [], TimeSpan.FromMinutes(1)), Evaluation(tenMinutes))
            .Findings.Should().BeEmpty();
        new MultixactAgeCheck().Evaluate(new MultixactAgeFacts(multixacts, [], TimeSpan.FromSeconds(15)), Evaluation(tenMinutes))
            .Findings.Should().ContainSingle("at 15 seconds it waits 7 min 22 s");
    }

    [Fact]
    public void A_refusal_within_a_week_is_critical()
    {
        TransactionIdAgeFacts facts = new([], Wraparound(static x => x with { DatabaseXidAge = 250_000_000, ServerXidAge = 250_000_000 }));

        new TransactionIdAgeCheck().Evaluate(facts, Evaluation(XidRate(1_000_000))).Findings.Should().ContainSingle()
            .Which.Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void Before_the_forced_line_the_forecast_is_a_note_because_a_forced_freeze_is_routine()
    {
        TransactionIdAgeFacts facts = new([], Wraparound(static x => x with { DatabaseXidAge = 150_000_000, ServerXidAge = 150_000_000 }));

        HealthVerdict verdict = new TransactionIdAgeCheck().Evaluate(facts, Evaluation(XidRate(20_000)));

        verdict.Findings.Should().BeEmpty("a refusal 'in 70 days' that assumes no freeze before the forced one would cry wolf on every busy database");
        verdict.Note.Should().Contain("autovacuum forces a freeze in about 42 hours - routine").And.Contain("at the rate of the last 40 minutes");
    }

    [Fact]
    public void Below_two_samples_the_note_says_a_forecast_needs_two_and_an_age_finding_carries_none()
    {
        TransactionIdAgeCheck check = new();

        check.Evaluate(new TransactionIdAgeFacts([], Wraparound(static x => x with { DatabaseXidAge = 250_000_000 })), Evaluation())
            .Note.Should().Contain("A forecast needs two samples");

        HealthFinding finding = check.Evaluate(new TransactionIdAgeFacts([], Wraparound(static x => x with { DatabaseXidAge = 1_200_000_000 })), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Evidence.Should().NotContain(static x => x.Label == "Writes refused in" || x.Label == "Transaction ids spent");
        finding.Caveat.Should().Contain("No forecast yet");
    }

    [Fact]
    public void An_age_finding_shows_the_forecast_only_inside_its_horizon()
    {
        TransactionIdAgeCheck check = new();
        TransactionIdAgeFacts facts = new([], Wraparound(static x => x with { DatabaseXidAge = 1_200_000_000, ServerXidAge = 1_200_000_000 }));

        check.Evaluate(facts, Evaluation(XidRate(1_000))).Findings.Should().ContainSingle()
            .Which.Evidence.Should().NotContain(static x => x.Label == "Writes refused in", "nine hundred million at a thousand a minute is years: no forecast says so");

        check.Evaluate(facts, Evaluation(XidRate(20_000_000))).Findings.Should().ContainSingle()
            .Which.Evidence.Should().Contain(static x => x.Label == "Writes refused in");
    }

    // -----------------------------------------------------------------------------------------------
    // T13 Multixact age
    // -----------------------------------------------------------------------------------------------

    private static MultixactAgeFacts Multixacts(Func<HealthWraparoundDatabase, HealthWraparoundDatabase>? shape = null, params TableMultixactAge[] tables) =>
        new(Database(shape), tables);

    [Fact]
    public void The_multixact_critical_line_is_the_failsafe_when_it_comes_first()
    {
        HealthWraparoundDatabase standard = Database();
        MultixactAgeCheck.WarningAge(standard.MultixactFreezeMaxAge).Should().BeInRange(1_090_000_000, 1_110_000_000);
        MultixactAgeCheck.CriticalAge(standard).Should().BeInRange(1_550_000_000, 1_580_000_000, "two-thirds comes before the 1.6 billion failsafe");

        MultixactAgeCheck.CriticalAge(standard with { MultixactFailsafeAge = 1_200_000_000 }).Should().Be(1_200_000_000);
        MultixactAgeCheck.CriticalAge(standard with { MultixactFailsafeAge = null }).Should().Be(MultixactAgeCheck.CriticalAge(standard),
            "PostgreSQL 13 has no failsafe, and the two-thirds line stands");
    }

    [Fact]
    public void The_multixact_counter_is_sampled()
    {
        new MultixactAgeCheck().Sample(Multixacts(static x => x with { MultixactCounter = 77 }))
            .Should().Equal(new HealthSamplePart("tables.multixact-counter", 77), new HealthSamplePart("tables.multixact-forced.swept", 1));
    }

    [Theory]
    [InlineData(900_000_000L, null)]
    [InlineData(1_200_000_000L, nameof(HealthSeverity.Warning))]
    [InlineData(1_700_000_000L, nameof(HealthSeverity.Critical))]
    public void The_oldest_store_table_s_multixact_age_decides_one_finding(long age, string? expected)
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthVerdict verdict = new MultixactAgeCheck().Evaluate(
            Multixacts(null, new TableMultixactAge(orders, age), new TableMultixactAge(EventTable("mt_streams"), 50_000)),
            Evaluation());

        if (expected is null)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain(orders.Key).And.Contain("No multixact rate yet");
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T13");
        finding.Severity.Should().Be(Enum.Parse<HealthSeverity>(expected));
        finding.Title.Should().StartWith(orders.Key + " is ");
        finding.DocumentTypes.Should().Equal(typeof(HealthOrderDoc));
        finding.Remedy.Should().BeNull("VACUUM is a DBA's statement");
        finding.WhyItMatters.Should().Contain("foreign-key checks against one row").And.Contain("replication slots do not hold multixacts back");
        finding.Evidence.Should().Contain(new HealthEvidence("vacuum_multixact_failsafe_age", "1,600,000,000"));
    }

    [Fact]
    public void A_multixact_forecast_alone_waits_for_the_age_to_stay_past_the_forced_line()
    {
        const string Counter = MartenStudio.Services.Health.Checks.Wraparound.MultixactCounterSeries;
        const string Swept = MartenStudio.Services.Health.Checks.Wraparound.MultixactSweptSeries;
        const string Forced = MartenStudio.Services.Health.Checks.Wraparound.MultixactPastForcedSeries;

        MultixactAgeFacts facts = Multixacts(static x => x with { DatabaseMultixactAge = 450_000_000, ServerMultixactAge = 450_000_000 });

        HealthHistoryView justCrossed = History(
            (TimeSpan.FromMinutes(40), [(Counter, 1_000_000), (Swept, 1)]),
            (TimeSpan.Zero, [(Counter, 2_600_000), (Swept, 1), (Forced, 1)]));

        HealthVerdict verdict = new MultixactAgeCheck().Evaluate(facts, Evaluation(justCrossed));
        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("once it has been for 29 min 30 s")
            .And.NotContain("more than 90 days", "the refusal line is inside the horizon, and the note says when (H9 re-review, fix 3)");
    }

    [Fact]
    public void Past_the_forced_multixact_line_a_refusal_within_ninety_days_is_a_warning()
    {
        MultixactAgeFacts facts = Multixacts(static x => x with { DatabaseMultixactAge = 450_000_000, ServerMultixactAge = 450_000_000 });

        HealthFinding finding = new MultixactAgeCheck()
            .Evaluate(facts, Evaluation(XidRate(40_000, series: MartenStudio.Services.Health.Checks.Wraparound.MultixactCounterSeries)))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().StartWith("At the rate of the last 40 minutes, Postgres would stop creating multixacts in about 29 days");
        finding.Evidence.Should().Contain(static x => x.Label == "New multixacts refused in");
        finding.Evidence.Should().Contain(new HealthEvidence("Other tables past autovacuum_multixact_freeze_max_age", "0, never named: they are not the store's"));
    }

    [Fact]
    public void A_young_database_with_a_rate_says_how_fast_it_makes_multixacts_and_raises_nothing()
    {
        HealthVerdict verdict = new MultixactAgeCheck().Evaluate(
            Multixacts(),
            Evaluation(XidRate(600, series: MartenStudio.Services.Health.Checks.Wraparound.MultixactCounterSeries)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("About 36,000 multixacts an hour, at the rate of the last 40 minutes");
    }

    // -----------------------------------------------------------------------------------------------
    // T14 Vacuum held back
    // -----------------------------------------------------------------------------------------------

    private static HorizonFacts Horizon(
        IReadOnlyList<HorizonHolder> holders,
        SessionVisibility? visibility = null,
        Func<HealthWraparoundDatabase, HealthWraparoundDatabase>? database = null,
        IReadOnlyList<TableFacts>? tables = null,
        bool asyncShards = false) =>
        new(Now, visibility ?? SessionVisibility.Everything, holders, Database(database), tables ?? [], asyncShards, LeaderLockInTransaction: true);

    private static HorizonHolder Session(TimeSpan? atLeast, long xids = 50_000, TimeSpan? open = null, bool wrote = false) => new()
    {
        Kind = HealthHorizonHolderKind.Session,
        XidAge = xids,
        AtLeast = atLeast,
        AtMost = open ?? atLeast,
        ProcessId = 4242,
        User = "app_user",
        Application = "billing-api",
        Client = "10.1.2.3",
        BackendStart = Now.AddDays(-1),
        State = "idle in transaction",
        Bound = "idle",
        HasTransactionId = wrote,

        // A session that has written owns its transaction id; one that has not holds only a snapshot of somebody else's.
        OwnXidAge = wrote ? xids : null,
    };

    /// <summary>A replication slot in H10's shape, the shared read's one.</summary>
    private static ReplicationSlot Slot(
        string name,
        string type,
        int? xmin = null,
        int? catalogXmin = null,
        bool active = false,
        TimeSpan? inactive = null,
        string? plugin = null,
        string? database = null,
        bool thisDatabase = true,
        string walStatus = "reserved",
        long? retained = 16_777_216,
        string? invalidation = null,
        bool? conflicting = null) =>
        new(name, type, plugin, database, Temporary: false, active, retained, walStatus, SafeWalSize: null,
            xmin, catalogXmin, inactive, invalidation, conflicting, ThisDatabase: thisDatabase);

    private static HealthFinding Single(HorizonFacts facts, HealthHistoryView? history = null) =>
        new HorizonHeldCheck().Evaluate(facts, Evaluation(history)).Findings.Should().ContainSingle().Subject;

    [Theory]
    [InlineData(50, null)]
    [InlineData(61, nameof(HealthSeverity.Warning))]
    [InlineData(60 * 25, nameof(HealthSeverity.Critical))]
    public void A_session_that_has_held_the_horizon_for_an_hour_is_a_warning_and_for_a_day_critical(int minutes, string? expected)
    {
        HealthVerdict verdict = new HorizonHeldCheck().Evaluate(Horizon([Session(TimeSpan.FromMinutes(minutes))]), Evaluation());

        if (expected is null)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain("the oldest holder is a session's snapshot, 50,000 transactions back")
                .And.NotContain("4242").And.NotContain("app_user", "a note is shown at the check's gate, and names nobody");
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(Enum.Parse<HealthSeverity>(expected));
        finding.Title.Should().StartWith("A session has held back vacuum for at least ").And.EndWith("(50,000 transactions)");
        finding.Title.Should().NotContain("4242").And.NotContain("billing-api");
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's snapshot"));
        finding.Evidence.Should().Contain(new HealthEvidence("Transactions behind", "50,000"));
        finding.Evidence.Single(static x => x.Label == "Who it is").Should().Match<HealthEvidence>(static x =>
            x.Gate == HealthGate.Activity && x.Value.StartsWith("pid 4242, role app_user, application 'billing-api', from 10.1.2.3", StringComparison.Ordinal));
        finding.Evidence.Where(static x => x.Label != "Who it is").Should().OnlyContain(static x => x.Gate == HealthGate.None,
            "the kind and the age are the database's; the check's own gate is the database-wide one");
        finding.Link.Should().Be(SessionEvidence.SessionsLink);
        finding.Remedy!.Code.Should().Contain("await using var session");
    }

    [Fact]
    public void A_session_held_for_an_hour_while_the_database_is_near_T12s_line_is_critical()
    {
        Single(Horizon([Session(TimeSpan.FromMinutes(70))], database: static x => x with { DatabaseXidAge = 1_100_000_000 }))
            .Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void A_holder_further_back_than_the_forced_line_is_a_warning_whatever_its_age_in_time()
    {
        HealthFinding finding = Single(Horizon([Session(TimeSpan.FromSeconds(30), xids: 210_000_000)]));

        finding.Severity.Should().Be(HealthSeverity.Warning, "the forced freeze cannot get past it");
        Single(Horizon([Session(TimeSpan.FromSeconds(30), xids: 1_100_000_000)])).Severity.Should().Be(HealthSeverity.Critical);
    }

    /// <summary>
    /// The HotCold leader at Marten 9.39, as measured at read committed: a transaction open for hours, refreshing its snapshot
    /// every minute in a <c>pg_sleep(60)</c>. Its transaction's age is only the most the horizon can be held; Postgres' lower
    /// bound says forty seconds, it owns no id for the counter to date, and alone it is never old enough to raise.
    /// </summary>
    [Fact]
    public void A_transaction_open_for_hours_whose_snapshot_is_a_minute_old_is_never_raised()
    {
        HorizonHolder leader = Session(TimeSpan.FromSeconds(40), xids: 2_000, open: TimeSpan.FromHours(30)) with
        {
            State = "active",
            HoldsDaemonLock = true,
        };

        HorizonHeldCheck check = new();
        HorizonFacts facts = Horizon([leader], database: static x => x with { XidCounter = 5_000_000 });

        check.Evaluate(facts, Evaluation()).Findings.Should().BeEmpty("without samples the lower bound - forty seconds - is all there is");
        check.Evaluate(facts, Evaluation(Counter(5_000_000, 3_000, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(1))))
            .Findings.Should().BeEmpty("a snapshot owns no id for the samples to date, and its statement is forty seconds old");

        check.Evaluate(facts, Evaluation()).Note.Should().Contain("the store's own HotCold daemon leader");
    }

    /// <summary>
    /// Follow-up 1: a burst of transaction ids at the end of a quiet ring made a rate estimate say hours of a forty-second
    /// snapshot - its 666,667 ids, taken at a million a minute, divided by the ring's quiet average - and raise it. The
    /// counter's samples bound an owned id instead: the first sample past it is this one, so the samples say nothing longer
    /// than Postgres' own forty seconds - and a snapshot owns no id for them to date at all. A full ring of 60 samples, at the
    /// default HealthInterval's minute and at five minutes.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public void A_leader_after_a_burst_in_a_quiet_ring_is_never_raised(int everyMinutes, bool ownsItsId)
    {
        // The leader holds a snapshot and is never dated by the counter; a writer owns its id and is - and still says forty seconds.
        HorizonHolder leader = Session(TimeSpan.FromSeconds(40), xids: 666_667, open: TimeSpan.FromHours(3), wrote: ownsItsId) with
        {
            State = "active",
            Bound = "statement",
            HoldsDaemonLock = !ownsItsId,
        };

        HealthHistoryView burst = Counter(
            5_000_000, perMinute: 100, TimeSpan.FromMinutes(59 * everyMinutes), TimeSpan.FromMinutes(everyMinutes),
            burst: TimeSpan.FromMinutes(1), burstPerMinute: 1_000_000);

        HorizonFacts facts = Horizon([leader], database: static x => x with { XidCounter = 5_000_000 });

        new HorizonHeldCheck().Evaluate(facts, Evaluation(burst)).Findings.Should().BeEmpty(
            "the first sample past the id it holds is the latest, so the samples bound its hold at none - and the lower bound is forty seconds");

        (TimeSpan? age, HorizonTimeKind kind) = HorizonHeldCheck.AgeOf(leader, facts, Evaluation(burst), burst.Trend(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries));
        age.Should().Be(TimeSpan.FromSeconds(40));
        kind.Should().Be(HorizonTimeKind.AtLeast);
    }

    /// <summary>B2: a lower bound longer than the transaction's own age is held to it - a DO that commits in a loop.</summary>
    [Fact]
    public void A_lower_bound_longer_than_its_transaction_is_held_to_the_transaction()
    {
        HorizonHolder loop = Session(TimeSpan.FromHours(3), xids: 500, open: TimeSpan.FromSeconds(1)) with { State = "active", Bound = "transaction" };

        new HorizonHeldCheck().Evaluate(Horizon([loop]), Evaluation()).Findings.Should().BeEmpty(
            "a three-hour statement whose transaction is a second old holds the horizon back by a second");

        HorizonHeldCheck.AgeOf(loop, Horizon([loop]), Evaluation(), HealthHistoryView.Empty.Trend(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries))
            .Age.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_bound_from_the_counters_samples_is_never_longer_than_the_transaction_has_been_open()
    {
        HorizonHolder session = Session(TimeSpan.FromSeconds(10), xids: 600_000, open: TimeSpan.FromMinutes(20), wrote: true) with { State = "active", Bound = "statement" };
        HealthHistoryView counter = Counter(5_000_000, 600, TimeSpan.FromHours(3), TimeSpan.FromMinutes(10));
        HealthTrend trend = counter.Trend(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries);

        (TimeSpan? age, HorizonTimeKind kind) = HorizonHeldCheck.AgeOf(
            session, Horizon([session], database: static x => x with { XidCounter = 5_000_000 }), Evaluation(counter), trend);

        age.Should().Be(TimeSpan.FromMinutes(20), "the counter was past its own id three hours ago, but its transaction began twenty minutes ago");
        kind.Should().Be(HorizonTimeKind.FromHistory);
    }

    /// <summary>
    /// Blocking 1: a snapshot's xmin is the oldest transaction running anywhere when it was taken - somebody else's id - so the
    /// counter's samples date whoever was handed it, not the snapshot. A session that owns no id is dated by Postgres'
    /// timestamps alone.
    /// </summary>
    [Fact]
    public void A_snapshot_is_never_dated_by_the_counter_because_its_id_is_somebody_elses()
    {
        HorizonHolder snapshot = Session(TimeSpan.FromSeconds(10), xids: 600_000, open: TimeSpan.FromHours(5)) with { State = "active", Bound = "statement" };
        HealthHistoryView counter = Counter(5_000_000, 600, TimeSpan.FromHours(3), TimeSpan.FromMinutes(10));
        HorizonFacts facts = Horizon([snapshot], database: static x => x with { XidCounter = 5_000_000 });

        HorizonHeldCheck.AgeOf(snapshot, facts, Evaluation(counter), counter.Trend(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries))
            .Should().Be(((TimeSpan?) TimeSpan.FromSeconds(10), HorizonTimeKind.AtLeast));

        new HorizonHeldCheck().Evaluate(facts, Evaluation(counter)).Findings.Should().BeEmpty("its statement is ten seconds old");
    }

    [Fact]
    public void The_first_sample_past_a_value_dates_it_and_a_counter_that_went_backwards_starts_again()
    {
        HealthTrend counter = History(
            (TimeSpan.FromMinutes(50), [("c", 9_000_000)]),
            (TimeSpan.FromMinutes(40), [("c", 100)]),
            (TimeSpan.FromMinutes(30), [("c", 200)]),
            (TimeSpan.FromMinutes(10), [("c", 300)]),
            (TimeSpan.Zero, [("c", 400)])).Trend("c");

        CounterHistory.PassedBy(counter, 250).Should().Be(Now.AddMinutes(-10), "the first sample past 250 is 300, ten minutes ago");
        CounterHistory.PassedBy(counter, 50).Should().Be(Now.AddMinutes(-40), "older than the run: passed by its first sample");
        CounterHistory.PassedBy(counter, 400).Should().BeNull("no sample is past it yet");
        CounterHistory.PassedBy(counter, 8_000_000).Should().BeNull("the sample before the reset is another timeline");
    }

    [Fact]
    public void A_session_of_another_role_is_judged_by_its_transaction_ids_and_the_caveat_says_what_cannot_be_seen()
    {
        HorizonHolder hidden = new()
        {
            Kind = HealthHorizonHolderKind.Session,
            XidAge = 1_800_000,
            OwnXidAge = 1_800_000,
            HasTransactionId = true,
            ProcessId = 777,
            User = "other_role",
            Hidden = true,
        };

        HorizonHeldCheck check = new();
        SessionVisibility narrowed = new(CanReadAllStats: false, HiddenSessions: 2);

        HorizonFacts facts = Horizon([hidden], narrowed, database: static x => x with { XidCounter = 5_000_000 });

        HealthVerdict noRate = check.Evaluate(facts, Evaluation());
        noRate.Findings.Should().BeEmpty("with no samples and no timestamp its age in time is unknown, and 1.8 million is not past the forced line");
        noRate.Note.Should().Contain("cannot see when 2 sessions of other roles").And.Contain("not known until");

        HealthFinding finding = Single(facts, Counter(5_000_000, 20_000, TimeSpan.FromHours(3), TimeSpan.FromMinutes(10)));

        finding.Severity.Should().Be(HealthSeverity.Warning, "the counter was already past the id it holds eighty minutes ago");
        finding.Title.Should().StartWith("A session has held back vacuum for at least 1 h 20 min (by the advisor's samples of the transaction-id counter)");
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session of another role (its state is not readable)"));
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().Contain("role other_role").And.Contain("(not readable)");
        finding.Caveat.Should().Contain("cannot see when 2 sessions of other roles in this database began their transactions")
            .And.Contain("dated, as a lower bound, from the advisor's samples of the transaction-id counter")
            .And.Contain("of one that holds only a snapshot it is not known");

        // The same session holding only a snapshot owns no id: nothing dates it, and 1.8 million is not past the forced line.
        HorizonHolder snapshot = hidden with { OwnXidAge = null, HasTransactionId = false };
        check.Evaluate(Horizon([snapshot], narrowed, database: static x => x with { XidCounter = 5_000_000 }),
                Evaluation(Counter(5_000_000, 20_000, TimeSpan.FromHours(3), TimeSpan.FromMinutes(10))))
            .Findings.Should().BeEmpty("a withheld snapshot has no timestamp and no id of its own to date");
    }

    [Fact]
    public void A_prepared_transaction_is_named_by_gid_and_owner_behind_the_activity_gate()
    {
        HorizonHolder prepared = new()
        {
            Kind = HealthHorizonHolderKind.Prepared,
            XidAge = 90_000,
            AtLeast = TimeSpan.FromHours(3),
            Name = "gid-orphan",
            User = "tx_owner",
            HasTransactionId = true,
        };

        HealthFinding finding = Single(Horizon([prepared]));

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("A prepared transaction has held back vacuum for at least 3 h 0 min (it was prepared that long ago) (90,000 transactions)");
        finding.Evidence.Single(static x => x.Label == "Who it is").Should().Be(new HealthEvidence("Who it is", "gid 'gid-orphan', owner tx_owner", HealthGate.Activity));
        finding.Remedy.Should().BeNull("COMMIT PREPARED is a transaction manager's, or a DBA's");
        finding.Link.Should().Be(SessionEvidence.SessionsLink);

        Single(Horizon([prepared], asyncShards: true)).Link.Should().Be(HorizonHeldCheck.ExplainLink,
            "a transaction that has written can pin the high-water mark too, and D01's Explain names it");
    }

    [Fact]
    public void A_logical_slot_that_holds_the_catalogs_past_the_forced_line_is_named_only_behind_the_gate()
    {
        ReplicationSlot slot = Slot("cdc_events", "logical", catalogXmin: 230_000_000, inactive: TimeSpan.FromDays(3), plugin: "pgoutput", database: "orders");

        HealthFinding finding = Single(Horizon([.. HorizonHolder.From(slot, 0)]));

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().StartWith("A logical replication slot has held back vacuum of the system catalogs, and every database's freeze, for at least 3 d 0 h");
        finding.Title.Should().NotContain("cdc_events");
        finding.Evidence.Should().Contain(new HealthEvidence("Who it is", "slot 'cdc_events', plugin pgoutput, database orders, inactive", HealthGate.Activity));
        finding.Evidence.Should().NotContain(static x => x.Label == "Dead rows in the store's tables", "a catalog holder keeps no dead rows in the store's tables");
        finding.WhyItMatters.Should().Contain("logical slot");
    }

    /// <summary>
    /// A replication slot read is the server's, so a logical slot of another database - possibly another tenant's - is
    /// described by its kind and its age and nothing else, at every gate: its age still counts, its name, plugin and
    /// database are not even kept in the facts.
    /// </summary>
    [Fact]
    public void A_logical_slot_of_another_database_counts_and_is_never_named()
    {
        ReplicationSlot slot = Slot("tenant_b_cdc", "logical", catalogXmin: 230_000_000, inactive: TimeSpan.FromDays(3),
            plugin: "wal2json", database: "tenant_b", thisDatabase: false);

        HorizonHolder holder = HorizonHolder.From(slot, 4).Should().ContainSingle().Subject;
        holder.Should().Match<HorizonHolder>(static x => x.OtherDatabase && x.Name == null && x.Plugin == null && x.SlotDatabase == null);

        HealthFinding finding = Single(Horizon([holder]));

        finding.Severity.Should().Be(HealthSeverity.Warning, "its hold on the catalogs still stops the forced freeze");
        finding.Title.Should().StartWith("A logical slot of another database on this server has held back vacuum of the system catalogs");
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a logical slot of another database on this server (its hold on the system catalogs)"));
        finding.Evidence.Should().NotContain(static x => x.Label == "Who it is", "nothing about it is named at any gate");
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 replication slot"));

        string all = AllText(finding);
        foreach (string detail in new[] { "tenant_b_cdc", "wal2json", "tenant_b" })
        {
            all.Should().NotContain(detail);
        }

        // A physical slot has no database, and is this database's to describe: named behind the activity gate.
        HorizonHolder physical = HorizonHolder.From(Slot("standby_b", "physical", xmin: 230_000_000, database: null), 5).Single();
        physical.OtherDatabase.Should().BeFalse();
        Single(Horizon([physical])).Evidence.Single(static x => x.Label == "Who it is").Should()
            .Be(new HealthEvidence("Who it is", "slot 'standby_b', inactive", HealthGate.Activity));
    }

    [Fact]
    public void A_catalog_holder_is_judged_by_how_far_back_it_holds_and_not_by_how_long()
    {
        ReplicationSlot slot = Slot("idle_slot", "logical", catalogXmin: 5_000_000, inactive: TimeSpan.FromDays(9));

        new HorizonHeldCheck().Evaluate(Horizon([.. HorizonHolder.From(slot, 0)]), Evaluation()).Findings
            .Should().BeEmpty("a catalog that waits nine days for vacuum is a slot's WAL problem (R01), and freezing is not stopped yet");
    }

    /// <summary>
    /// Blocking 1: a physical slot's xmin and a standby's feedback are the oldest transaction running when the standby took its
    /// snapshot - not an id either owns - so the counter's samples, which date an id, are not theirs. Without an inactive slot's
    /// <c>inactive_since</c> they have no age in time, and are judged by how far back they hold.
    /// </summary>
    [Fact]
    public void A_physical_slots_xmin_and_a_standbys_feedback_own_no_id_and_are_judged_by_how_far_back_they_hold()
    {
        ReplicationSlot slot = Slot("standby_a", "physical", xmin: 1_500_000, active: true);
        HorizonHolder standby = new()
        {
            Kind = HealthHorizonHolderKind.Standby,
            XidAge = 1_400_000,
            ProcessId = 99,
            User = "replicator",
            Application = "walreceiver",
            Client = "10.0.0.7",
            State = "streaming",
        };

        HealthHistoryView counter = Counter(5_000_000, 20_000, TimeSpan.FromHours(3), TimeSpan.FromMinutes(10));
        HorizonHeldCheck check = new();

        HealthVerdict quiet = check.Evaluate(
            Horizon([.. HorizonHolder.From(slot, 0), standby], database: static x => x with { XidCounter = 5_000_000 }), Evaluation(counter));

        quiet.Findings.Should().BeEmpty("the counter passed their ids over an hour ago, which dates whoever was handed them - not the slot");
        quiet.Note.Should().Contain("the oldest holder is a physical replication slot, 1,500,000 transactions back - for how long is not known: "
            + "Postgres records no time for it");

        HealthFinding finding = Single(
            Horizon([.. HorizonHolder.From(Slot("standby_a", "physical", xmin: 210_000_000, active: true), 0), standby],
                database: static x => x with { XidCounter = 500_000_000 }),
            counter);

        finding.Severity.Should().Be(HealthSeverity.Warning, "the forced freeze cannot get past it");
        finding.Title.Should().Be("A replication slot holds back vacuum by 210 million transactions");
        finding.Evidence.Should().Contain(static x => x.Label == "Held for" && x.Value.Contains("cannot date it", StringComparison.Ordinal));
        finding.Evidence.Should().NotContain(static x => x.Label == "Oldest holder of all", "the slot binds, and is the oldest itself");
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 replication slot, 1 standby"));
        finding.Caveat.Should().NotContain("samples of the transaction-id counter");
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().Be("slot 'standby_a', active");
    }

    /// <summary>
    /// F2: Postgres leaves an invalidated slot's xmin and catalog_xmin in the view, and skips the slot when it computes the
    /// horizon - so it is no holder. A slot reported lost that still has a restart_lsn is not invalidated yet, and holds.
    /// </summary>
    [Fact]
    public void An_invalidated_slot_holds_nothing_and_a_lost_one_that_still_holds_is_to_be_dropped()
    {
        HorizonHolder.From(Slot("gone_17", "logical", catalogXmin: 230_000_000, walStatus: "lost", invalidation: "rows_removed", conflicting: true), 0)
            .Should().BeEmpty("PostgreSQL 17 says why it invalidated the slot");
        HorizonHolder.From(Slot("gone_16", "logical", catalogXmin: 230_000_000, walStatus: "lost", conflicting: true), 1)
            .Should().BeEmpty("PostgreSQL 16 says conflicting of a logical slot invalidated for any cause");
        HorizonHolder.From(Slot("gone_13", "physical", xmin: 230_000_000, walStatus: "lost", retained: null), 2)
            .Should().BeEmpty("invalidation for removed WAL clears restart_lsn, on 13 to 17");

        HorizonHolder lost = HorizonHolder.From(Slot("stuck", "physical", xmin: 230_000_000, walStatus: "lost"), 3).Should().ContainSingle().Subject;
        lost.Lost.Should().BeTrue();

        HealthFinding finding = Single(Horizon([lost]));

        finding.Title.Should().Be("A lost replication slot holds back vacuum by 230 million transactions");
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a lost physical replication slot"));
        finding.WhyItMatters.Should().Contain("A lost slot that still holds the horizon: drop it");
    }

    [Fact]
    public void Another_databases_session_is_never_named_and_matters_only_once_it_stops_the_forced_freeze()
    {
        HorizonHolder elsewhere = new()
        {
            Kind = HealthHorizonHolderKind.OtherDatabaseSession,
            XidAge = 210_000_000,
            AtLeast = TimeSpan.FromHours(30),
        };

        HealthFinding finding = Single(Horizon([elsewhere]));

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().NotContain(static x => x.Label == "Who it is");
        finding.WhyItMatters.Should().Contain("another database");

        new HorizonHeldCheck().Evaluate(Horizon([elsewhere with { XidAge = 1_000_000 }]), Evaluation()).Findings
            .Should().BeEmpty("thirty hours in another database hold back only its catalogs, until the freeze needs to get past it");
    }

    [Fact]
    public void A_holder_of_the_stores_tables_wins_a_tie_and_the_oldest_of_all_is_said_beside_it()
    {
        HorizonHolder session = Session(TimeSpan.FromHours(2), xids: 1_000_000);
        HorizonHolder elsewhere = new() { Kind = HealthHorizonHolderKind.OtherDatabaseSession, XidAge = 205_000_000 };

        HealthFinding finding = Single(Horizon([elsewhere, session]));

        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's snapshot"));
        finding.Evidence.Should().Contain(new HealthEvidence("Oldest holder of all", "a session in another database on this server, 205,000,000 transactions back"));
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 session"),
            "another database's holders are an age and nothing else - not even how many there are");
    }

    /// <summary>The HotCold leader's minute-old snapshot, pinned at the id a prepared transaction or a writer owns.</summary>
    private static HorizonHolder PinnedLeader() =>
        Session(TimeSpan.FromSeconds(40), xids: 600_000, open: TimeSpan.FromHours(30)) with { State = "active", Bound = "statement", HoldsDaemonLock = true };

    /// <summary>
    /// Four hours of the counter at 3,000 a minute, sampled every five minutes: 49 samples, what a five-minute
    /// <c>HealthInterval</c> holds - long enough for the samples to date an owned id past the hour a holder needs to be raised,
    /// which the default minute's 60 samples cannot (they reach back 59 minutes).
    /// </summary>
    private static HealthHistoryView FourHours() => Counter(5_000_000, 3_000, TimeSpan.FromHours(4), TimeSpan.FromMinutes(5));

    /// <summary>
    /// Blocking 1: a prepared transaction three hours old, and the HotCold leader's forty-second snapshot whose xmin is that
    /// transaction's id. Dating the leader's xmin by the counter gave it the prepared transaction's age, and a tie on
    /// transactions behind named the leader. The leader owns nothing; it is behind the prepared transaction, which is named.
    /// </summary>
    [Fact]
    public void A_leader_whose_snapshot_waits_on_a_prepared_transaction_is_never_the_holder_named()
    {
        HorizonHolder leader = PinnedLeader();
        HorizonHolder prepared = new()
        {
            Kind = HealthHorizonHolderKind.Prepared,
            XidAge = 600_000,
            AtLeast = TimeSpan.FromHours(3),
            HasTransactionId = true,
            Name = "h9-orphan",
            User = "tx_owner",
        };

        HorizonFacts facts = Horizon([leader, prepared], database: static x => x with { XidCounter = 5_000_000 });

        IReadOnlyDictionary<HorizonHolder, HorizonHolder> roots = HorizonHeldCheck.RootsOf(facts.Holders);
        roots.Count.Should().Be(1, "only the leader is behind another");
        roots[leader].Should().BeSameAs(prepared);

        HealthFinding finding = Single(facts, FourHours());

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a prepared transaction"));
        finding.Title.Should().Be("A prepared transaction has held back vacuum for at least 3 h 15 min (by the advisor's samples of the transaction-id counter) (600,000 transactions)");
        finding.Evidence.Should().Contain(new HealthEvidence("Behind it, at the same point",
            "the store's own HotCold daemon leader - its snapshot's oldest running transaction is this one, so it moves on once this one ends"));
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 prepared transaction; and 1 session behind it, at the same point"));
        finding.Evidence.Should().NotContain(static x => x.Label == "Oldest holder of all", "the leader is no further back than what it waits on");
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().Be("gid 'h9-orphan', owner tx_owner");
        finding.WhyItMatters.Should().Contain("it is the one to end").And.NotContain("HotCold daemon leader. With");
        finding.Remedy.Should().BeNull("the leader's remedy would send the host to the wrong setting");

        HorizonHeldCheck.AgeOf(leader, facts, Evaluation(FourHours()), FourHours().Trend(MartenStudio.Services.Health.Checks.Wraparound.XidCounterSeries))
            .Should().Be(((TimeSpan?) TimeSpan.FromSeconds(40), HorizonTimeKind.AtLeast), "a snapshot is dated by Postgres' timestamps alone");

        new HorizonHeldCheck().Evaluate(Horizon([leader], database: static x => x with { XidCounter = 5_000_000 }), Evaluation(FourHours()))
            .Findings.Should().BeEmpty("alone, the leader's snapshot is forty seconds old");
    }

    [Fact]
    public void A_leader_whose_snapshot_waits_on_a_writer_is_never_the_holder_named()
    {
        HorizonHolder writer = Session(TimeSpan.FromMinutes(2), xids: 600_000, open: TimeSpan.FromHours(5), wrote: true) with
        {
            ProcessId = 5151,
            Application = "report-job",
            Total = 2,
        };

        HealthFinding finding = Single(
            Horizon([PinnedLeader() with { Total = 2 }, writer], database: static x => x with { XidCounter = 5_000_000 }), FourHours());

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's open transaction that has written"));
        finding.Title.Should().StartWith("A session has held back vacuum for at least 3 h 15 min (by the advisor's samples of the transaction-id counter)");
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().StartWith("pid 5151, role app_user, application 'report-job'");
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 session; and 1 session behind it, at the same point"));
        finding.Remedy!.Code.Should().Contain("await using var session", "the writer's remedy, never the leader's lock setting");
    }

    /// <summary>
    /// Blocking 1, across databases: every snapshot taken here while another database's transaction runs counts it as running,
    /// so the leader's snapshot here is pinned at that transaction's id - and through it, that transaction holds back this
    /// database's tables. It is named, as another database's session, and never identified.
    /// </summary>
    [Fact]
    public void A_leader_whose_snapshot_waits_on_another_databases_writer_names_that_writer_and_nothing_about_it()
    {
        HorizonHolder elsewhere = new()
        {
            Kind = HealthHorizonHolderKind.OtherDatabaseSession,
            XidAge = 600_000,
            OwnXidAge = 600_000,
            HasTransactionId = true,
            AtLeast = TimeSpan.FromMinutes(2),
            AtMost = TimeSpan.FromHours(5),
        };

        HealthFinding finding = Single(
            Horizon([PinnedLeader(), elsewhere], database: static x => x with { XidCounter = 5_000_000 }), FourHours());

        finding.Severity.Should().Be(HealthSeverity.Warning, "it holds this database's tables through the leader's snapshot, for over three hours");
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session in another database on this server"));
        finding.Title.Should().StartWith("A session in another database has held back vacuum for at least 3 h 15 min");
        finding.Evidence.Should().Contain(new HealthEvidence("What it holds back",
            "vacuum of every table in this database, through the snapshots behind it here: dead rows stay, and freezing stops at it"));
        finding.Evidence.Should().Contain(new HealthEvidence("Holders found", "1 session, behind it at the same point"));
        finding.Evidence.Should().NotContain(static x => x.Label == "Who it is");
        finding.WhyItMatters.Should().Contain("every snapshot a session here takes while it runs counts it as running");

        new HorizonHeldCheck().Evaluate(Horizon([elsewhere], database: static x => x with { XidCounter = 5_000_000 }), Evaluation(FourHours()))
            .Findings.Should().BeEmpty("alone, another database's writer holds back only the catalogs, and is short of the forced line");
    }

    /// <summary>Among holders equally far back the one that owns its id wins, and the daemon's leader never wins over another.</summary>
    [Fact]
    public void Among_holders_equally_far_back_the_daemons_leader_is_never_preferred()
    {
        HorizonHolder leader = Session(TimeSpan.FromHours(2)) with { HoldsDaemonLock = true, Total = 2 };
        HorizonHolder other = Session(TimeSpan.FromHours(2)) with { ProcessId = 6262, Total = 2 };

        HealthFinding finding = Single(Horizon([leader, other]));

        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's snapshot"));
        finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().StartWith("pid 6262");
    }

    [Fact]
    public void A_writer_with_async_projections_links_D01s_explain_and_the_dead_rows_join_T07()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();

        TableFacts vacuumedSince = Table(orders, static row => row with { DeadRows = 40_000, LastAutovacuum = Now.AddMinutes(-30) });
        TableFacts notSince = Table(events, static row => row with { DeadRows = 2_000, LastAutovacuum = Now.AddHours(-5) });

        HealthFinding finding = Single(Horizon([Session(TimeSpan.FromHours(2), wrote: true)], tables: [vacuumedSince, notSince], asyncShards: true));

        finding.Link.Should().Be(HorizonHeldCheck.ExplainLink);
        finding.WhyItMatters.Should().Contain("D01");
        finding.Evidence.Should().Contain(new HealthEvidence("Dead rows in the store's tables",
            "42,000 in 2 tables; 40,000 of them in 1 table vacuumed since it began - rows that vacuum could not remove (T07)"));
        finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a session's open transaction that has written"));
    }

    /// <summary>
    /// Follow-up 1: a quiet note claims only what is known. A standby's feedback records no time, so nothing says it has not
    /// held the horizon for an hour - only that no holder is known to have.
    /// </summary>
    [Fact]
    public void A_quiet_note_claims_only_what_is_known_and_says_when_Postgres_records_no_time()
    {
        HorizonHolder standby = new()
        {
            Kind = HealthHorizonHolderKind.Standby,
            XidAge = 1_400_000,
            ProcessId = 99,
            User = "replicator",
            State = "streaming",
        };

        HealthVerdict verdict = new HorizonHeldCheck().Evaluate(Horizon([standby]), Evaluation());

        verdict.Findings.Should().BeEmpty("1.4 million is short of the forced line, and a standby is judged by how far back it holds");
        verdict.Note.Should().StartWith("No holder is known to have held the vacuum horizon back for an hour, or past autovacuum_freeze_max_age")
            .And.Contain("the oldest holder is a standby's hot_standby_feedback, 1,400,000 transactions back - for how long is not known: "
                + "Postgres records no time for it")
            .And.NotContain("Nothing has held");

        new HorizonHeldCheck().Evaluate(Horizon([Session(TimeSpan.FromMinutes(5))]), Evaluation()).Note
            .Should().Contain("the oldest holder is a session's snapshot, 50,000 transactions back, for at least 5 min (it has been idle in its transaction that long).");
    }

    [Fact]
    public void Nothing_holding_the_horizon_is_a_quiet_note()
    {
        new HorizonHeldCheck().Evaluate(Horizon([]), Evaluation()).Note.Should().StartWith("Nothing holds the vacuum horizon back");
    }
}
