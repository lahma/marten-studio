using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// T19's formula on B0's numbers (200 partitions × (1 + 2 indexes) → 601, and 603 from 16) and T20's runway at its edges,
/// with and without a default partition, on a time and an integer key.
/// </summary>
public class PartitionChecksTests
{
    private static readonly StudioTable Events = Host("events");

    private static double Epoch(DateTimeOffset at) => at.ToUnixTimeMilliseconds() / 1000d;

    private static HealthPartitionedTableRow Monthly(DateTimeOffset newestLower, DateTimeOffset newestUpper, bool hasDefault = false, double defaultRows = 0) => new()
    {
        Schema = "app",
        Table = "events",
        Strategy = "r",
        KeyColumns = 1,
        KeyType = "timestamp with time zone",
        Leaves = 12,
        BoundedPartitions = 12,
        NewestLower = Epoch(newestLower),
        NewestUpper = Epoch(newestUpper),
        HasDefault = hasDefault,
        DefaultRows = defaultRows,
    };

    private static HealthVerdict Range(HealthPartitionedTableRow row) =>
        new RangePartitionsCheck().Evaluate(new PartitionFacts(Now, [row], [Events]), Evaluation());

    // ---- T20 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void With_no_default_a_bound_in_the_past_or_within_one_width_is_critical_and_within_two_a_warning()
    {
        // Now is 2026-10-26: a monthly newest partition [2026-10-01, 2026-11-01) is six days from its end, within one width.
        HealthFinding withinOne = Range(Monthly(new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))).Findings.Single();
        withinOne.Severity.Should().Be(HealthSeverity.Critical);
        withinOne.Variant.Should().Be("running-out");
        withinOne.WhyItMatters.Should().Contain("no partition of relation found for row");
        withinOne.Remedy!.Code.Should().Be(
            "CREATE TABLE \"app\".\"events_p20261101\" PARTITION OF \"app\".\"events\" FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');",
            "a calendar month on from the newest bound, never 31 days");

        Range(Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))).Findings.Single()
            .Severity.Should().Be(HealthSeverity.Critical, "the bound has passed");

        // [2026-11-01, 2026-12-01): 36 days ahead, more than one width (30 days) and fewer than two.
        Range(Monthly(new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))).Findings.Single()
            .Severity.Should().Be(HealthSeverity.Warning);

        Range(Monthly(new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero))).Findings
            .Should().BeEmpty("67 days ahead is two widths and more");
    }

    [Fact]
    public void With_a_default_partition_running_out_is_a_warning_and_rows_in_the_default_are_one_of_their_own()
    {
        HealthVerdict verdict = Range(Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), hasDefault: true, defaultRows: 1_500));

        verdict.Findings.Should().HaveCount(2);
        HealthFinding runningOut = verdict.Findings.Single(static x => x.Variant == "running-out");
        runningOut.Severity.Should().Be(HealthSeverity.Warning, "the default catches the rows: nothing is rejected");
        runningOut.Remedy!.Explanation.Should().Contain("DEFAULT partition under an ACCESS EXCLUSIVE lock");

        HealthFinding rows = verdict.Findings.Single(static x => x.Variant == "default-partition-rows");
        rows.Severity.Should().Be(HealthSeverity.Warning);
        rows.Title.Should().Contain("about 1,500 rows");
        rows.Remedy.Should().BeNull("which range the rows belong to is in the rows, which the sweep never reads");
    }

    [Fact]
    public void A_range_its_source_keeps_ahead_is_warned_of_only_within_one_width_and_every_other_keeps_two()
    {
        StudioTable managed = Events with { RangePartitionsManaged = true };
        static HealthVerdict Judge(StudioTable table, HealthPartitionedTableRow row) =>
            new RangePartitionsCheck().Evaluate(new PartitionFacts(Now, [row], [table]), Evaluation());

        // ByRollingRange(Month, periodsAhead: 1) on 2026-10-26: October and November, the newest bound 36 days ahead.
        HealthPartitionedTableRow oneAhead = Monthly(new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), hasDefault: true);
        Judge(managed, oneAhead).Findings.Should().BeEmpty("Marten's recommended periodsAhead: 1 is always more than one width ahead once rolled");
        Judge(Events, oneAhead).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning, "a range nobody keeps ahead keeps P1's two widths");
        Judge(Events, oneAhead with { HasDefault = false }).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);

        // The roll-forward fell behind: November was never created, and October's bound is six days off.
        HealthPartitionedTableRow behind = Monthly(new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), hasDefault: true);
        Judge(managed, behind).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning, "the DEFAULT catches the rows");

        // Ran out: Critical stays where it was - no DEFAULT and within one width, managed or not.
        HealthPartitionedTableRow ranOut = Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Judge(managed, ranOut).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
        Judge(Events, ranOut).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
    }

    [Fact]
    public void An_integer_key_with_no_rows_counted_is_judged_on_its_default_partition_alone_and_maxvalue_never_runs_out()
    {
        HealthPartitionedTableRow integers = Monthly(default, default) with
        {
            KeyType = "bigint",
            NewestLower = 1_000,
            NewestUpper = 2_000,
            HasDefault = true,
            DefaultRows = 10,
        };

        Range(integers).Findings.Should().ContainSingle().Which.Variant.Should().Be("default-partition-rows");
        Range(integers with { DefaultRows = 0 }).Findings.Should().BeEmpty("no partition holds rows by the catalog's count, so there is no data to judge against");

        Range(Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)) with { UnboundedAbove = true })
            .Findings.Should().BeEmpty("a MAXVALUE partition takes every later row");
        Range(integers with { DefaultRows = 0, PopulatedUpper = 2_000, UnboundedAbove = true })
            .Findings.Should().BeEmpty("an integer range with a MAXVALUE partition never runs out either");

        Range(Monthly(default, default) with { Strategy = "l" }).Note.Should().Contain("partitioned by range");
    }

    // ---- T20 on an integer key, against where its rows stop (X4) --------------------------------------------------------

    private static HealthPartitionedTableRow Numbered(double populatedUpper, int emptyAbove, bool hasDefault = false, string keyType = "integer") => new()
    {
        Schema = "app",
        Table = "events",
        Strategy = "r",
        KeyColumns = 1,
        KeyType = keyType,
        Leaves = 4,
        BoundedPartitions = 4,
        NewestLower = 3_000_000,
        NewestUpper = 4_000_000,
        PopulatedUpper = populatedUpper,
        EmptyAbove = emptyAbove,
        HasDefault = hasDefault,
    };

    [Fact]
    public void Rows_in_the_newest_integer_partition_or_one_empty_one_above_them_are_a_warning_never_critical()
    {
        HealthFinding inNewest = Range(Numbered(4_000_000, 0)).Findings.Single();
        inNewest.Severity.Should().Be(HealthSeverity.Warning, "an integer key has no clock: how soon the rows get there is the application's pace");
        inNewest.Variant.Should().Be("running-out");
        inNewest.Title.Should().Be("The rows of app.events reach its newest range partition, which ends at 4,000,000");
        inNewest.WhyItMatters.Should().Contain("no partition of relation found for row").And.Contain("has no clock");
        inNewest.Evidence.Should().Contain(new HealthEvidence("Empty partitions above the rows", "0"))
            .And.Contain(new HealthEvidence("Partition width", "1,000,000 (the newest partition's)"));
        inNewest.Caveat.Should().Contain("never the highest value").And.Contain("No row was read");
        inNewest.Remedy!.Code.Should().Be(
            "CREATE TABLE \"app\".\"events_p4000000\" PARTITION OF \"app\".\"events\" FOR VALUES FROM (4000000) TO (5000000);",
            "the next partition, the newest one's width on");
        inNewest.Remedy.Explanation.Should().NotContain("each period up to now", "an integer key has no periods to catch up on");

        HealthFinding oneLeft = Range(Numbered(3_000_000, 1, hasDefault: true)).Findings.Single();
        oneLeft.Severity.Should().Be(HealthSeverity.Warning);
        oneLeft.Title.Should().Be("One empty range partition is left above the rows of app.events, up to 4,000,000");
        oneLeft.WhyItMatters.Should().Contain("land in the DEFAULT partition");

        Range(Numbered(2_000_000, 2)).Findings.Should().BeEmpty("two empty partitions above the rows are two widths of room");
    }

    [Fact]
    public void An_integer_partition_past_what_its_type_holds_gets_no_statement()
    {
        HealthPartitionedTableRow full = Numbered(2_147_483_000, 0) with { NewestLower = 2_000_000_000, NewestUpper = 2_147_483_000 };

        RangePartitionsCheck.Next(full).Should().BeNull("an integer key cannot hold a partition from 2,147,483,000 to 2,294,966,000");
        Range(full).Findings.Single().Remedy.Should().BeNull();
        RangePartitionsCheck.Next(full with { KeyType = "bigint" })!.To.Should().Be(2_294_966_000);
        MigrationSqlText.RangeBoundLiteral("bigint", 9_007_199_254_740_994d).Should().BeNull("past 2^53 a double holds no exact whole number");
    }

    [Fact]
    public void A_time_range_written_in_a_DateStyle_other_than_ISO_is_said_to_be_unjudged()
    {
        HealthPartitionedTableRow styled = Monthly(default, default) with
        {
            NewestLower = null,
            NewestUpper = null,
            BoundedPartitions = 0,
            UnreadBounds = 12,
            DateStyle = "SQL, DMY",
        };

        HealthVerdict verdict = Range(styled);
        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("1 table partitioned by range on a time column was not judged: its bounds are written in the session's "
            + "DateStyle, SQL, DMY, and the studio reads time bounds only in ISO, Postgres' default.");
    }

    [Fact]
    public void The_next_partition_is_named_and_bounded_for_the_keys_type()
    {
        RangePartitionsCheck.Next(Monthly(new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero)))!
            .Should().BeEquivalentTo(new RangePartitionNext("timestamp with time zone", Epoch(new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero)), Epoch(new(2027, 3, 1, 0, 0, 0, TimeSpan.Zero)), "p20270201", false));

        RangePartitionNext daily = RangePartitionsCheck.Next(
            Monthly(new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero)) with { KeyType = "date" })!;
        MigrationSqlText.CreateRangePartition("app", "events", "events_" + daily.Suffix, daily.KeyType, daily.From, daily.To)
            .Should().Be("CREATE TABLE \"app\".\"events_p20261026\" PARTITION OF \"app\".\"events\" FOR VALUES FROM ('2026-10-26') TO ('2026-10-27');");

        RangePartitionNext numbers = RangePartitionsCheck.Next(Monthly(default, default) with { KeyType = "integer", NewestLower = 0, NewestUpper = 1_000 })!;
        MigrationSqlText.CreateRangePartition("app", "events", "events_" + numbers.Suffix, numbers.KeyType, numbers.From, numbers.To)
            .Should().Be("CREATE TABLE \"app\".\"events_p1000\" PARTITION OF \"app\".\"events\" FOR VALUES FROM (1000) TO (2000);");
    }

    [Fact]
    public void A_timestamptz_range_cut_at_local_midnights_is_months_in_the_sessions_time_zone()
    {
        // Helsinki's months: 2026-10-01 00:00+03 is 09-30 21:00 UTC, 2026-11-01 00:00+02 is 10-31 22:00 UTC (W5-B1 review F10).
        HealthPartitionedTableRow helsinki = Monthly(new(2026, 9, 30, 21, 0, 0, TimeSpan.Zero), new(2026, 10, 31, 22, 0, 0, TimeSpan.Zero)) with { TimeZone = "Europe/Helsinki" };

        RangePartitionNext next = RangePartitionsCheck.Next(helsinki)!;
        next.Suffix.Should().Be("p20261101", "named for the local date it starts on");
        next.To.Should().Be(Epoch(new(2026, 11, 30, 22, 0, 0, TimeSpan.Zero)), "December starts at 00:00+02 in Helsinki");
        MigrationSqlText.CreateRangePartition("app", "events", "events_" + next.Suffix, next.KeyType, next.From, next.To)
            .Should().Be("CREATE TABLE \"app\".\"events_p20261101\" PARTITION OF \"app\".\"events\" FOR VALUES FROM ('2026-10-31 22:00:00+00') TO ('2026-11-30 22:00:00+00');");

        // Read in UTC, the same bounds are no month's: the next one is the newest one's width.
        RangePartitionsCheck.Next(helsinki with { TimeZone = "UTC" })!.To.Should().Be(Epoch(new(2026, 12, 1, 23, 0, 0, TimeSpan.Zero)));
        RangePartitionsCheck.UnknownZone(helsinki).Should().BeFalse();

        HealthPartitionedTableRow unknown = helsinki with { TimeZone = "Mars/Olympus_Mons" };
        RangePartitionsCheck.UnknownZone(unknown).Should().BeTrue();
        Range(unknown).Findings.Single().Caveat.Should().Contain("Mars/Olympus_Mons is not a zone this host knows, so they were looked for in UTC");
    }

    [Fact]
    public void A_bound_several_periods_past_is_remedied_with_the_next_one_and_each_period_up_to_now()
    {
        // [2026-06-01, 2026-07-01) on 2026-10-26: July is next, and August, September and October have passed too.
        HealthFinding finding = Range(Monthly(new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero))).Findings.Single();

        finding.Remedy!.Code.Should().Contain("\"events_p20260701\"");
        finding.Remedy.Explanation.Should().Contain("create this one and each period up to now - 3 more after it");

        Range(Monthly(new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero))).Findings.Single().Remedy!.Explanation
            .Should().NotContain("each period up to now", "October's partition is the one now needs");
    }

    [Fact]
    public void A_partition_name_postgres_would_cut_or_a_hostile_one_gets_no_statement()
    {
        MigrationSqlText.CreateRangePartition("app", "events", new string('e', 64), "integer", 0, 1).Should().BeNull("Postgres would cut it at 63 bytes without a word");
        MigrationSqlText.CreateRangePartition("app", "events", "events_\"x\"", "integer", 0, 1).Should().BeNull();
        MigrationSqlText.CreateRangePartition("app", "events", "events_p1", "text", 0, 1).Should().BeNull("only time and integer bounds are written");
        MigrationSqlText.CreateRangePartition("Sales Data", "Events; drop", "Events; drop_p1", "integer", 0, 1)
            .Should().Be("CREATE TABLE \"Sales Data\".\"Events; drop_p1\" PARTITION OF \"Sales Data\".\"Events; drop\" FOR VALUES FROM (0) TO (1);");
        MigrationSqlText.RangeBoundLiteral("timestamp without time zone", Epoch(new(2026, 12, 1, 6, 30, 0, TimeSpan.Zero))).Should().Be("'2026-12-01 06:30:00'");
        MigrationSqlText.RangeBoundLiteral("integer", 1.5).Should().BeNull();
        MigrationSqlText.RangeBoundLiteral("date", double.PositiveInfinity).Should().BeNull();
    }

    // ---- T19 ------------------------------------------------------------------------------------------------------------

    private static HealthPartitionedTableRow TwoHundred(int serverVersion, int maxLocks = 64, int maxConnections = 100) => new()
    {
        Schema = "app",
        Table = "events",
        Strategy = "l",
        KeyColumns = 1,
        Leaves = 200,
        LeafIndexes = 400,
        PartitionedIndexes = 2,
        MaxLocksPerTransaction = maxLocks,
        MaxConnections = maxConnections,
        ServerVersion = serverVersion,
    };

    private static HealthVerdict Locks(HealthPartitionedTableRow row) =>
        new PartitionLockBudgetCheck().Evaluate(new PartitionLockFacts(new PartitionFacts(Now, [row], [Events]), PostgresPlatform.SelfHosted), Evaluation());

    [Theory]
    [InlineData(150_008, 601, 16)]
    [InlineData(160_004, 603, 16)]
    [InlineData(170_011, 603, 16)]
    [InlineData(180_006, 603, 64)]
    public void B0s_two_hundred_partitions_of_two_indexes_each(int serverVersion, long locks, int fastPath)
    {
        PartitionLockBudget budget = PartitionLockBudgetCheck.Budget(TwoHundred(serverVersion));

        budget.Locks.Should().Be(locks);
        budget.Capacity.Should().Be(6_400, "64 × (100 + 0)");
        budget.FastPathSlots.Should().Be(fastPath);
    }

    [Theory]
    [InlineData(10, 16)]
    [InlineData(16, 16)]
    [InlineData(64, 64)]
    [InlineData(100, 128)]
    [InlineData(128, 128)]
    [InlineData(1_000_000, 16_384)]
    public void From_18_a_backends_fast_path_slots_are_the_power_of_two_at_or_above_max_locks_per_transaction(int maxLocks, int slots)
    {
        // pg_nextpower2_32(max_locks_per_transaction) / 16 groups of 16, 1 to 1,024 groups: 18.6 measured 128 at 100 (W5-B1 review F7).
        PartitionLockBudgetCheck.FastPathSlots(maxLocks).Should().Be(slots);
        PartitionLockBudgetCheck.Budget(TwoHundred(180_006, maxLocks: maxLocks)).FastPathSlots.Should().Be(slots);
        PartitionLockBudgetCheck.Budget(TwoHundred(170_011, maxLocks: maxLocks)).FastPathSlots.Should().Be(16, "16 a backend up to 17");
    }

    [Fact]
    public void Past_max_locks_per_transaction_is_info_and_past_half_the_capacity_a_warning()
    {
        HealthFinding info = Locks(TwoHundred(170_011)).Findings.Single();
        info.Severity.Should().Be(HealthSeverity.Info);
        info.Variant.Should().Be("per-transaction");
        info.Evidence.Should().Contain(x => x.Label == "Locks one unpruned query takes" && x.Value.EndsWith("= 603", StringComparison.Ordinal));
        info.Evidence.Should().Contain(x => x.Label == "Documented capacity" && x.Value.Contains("a floor: one backend measured about twice it", StringComparison.Ordinal));
        info.Caveat.Should().Contain("generic plan");
        info.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        info.Remedy.Code.Should().EndWith("max_locks_per_transaction = 1024", "the smallest power of two that holds 603");
        info.Remedy.Explanation.Should().Contain("restarts");

        HealthFinding warning = Locks(TwoHundred(170_011, maxConnections: 10)).Findings.Single();
        warning.Severity.Should().Be(HealthSeverity.Warning, "603 is past half of 64 × 10");
        warning.Variant.Should().Be("capacity");
        warning.Remedy!.Code.Should().EndWith("max_locks_per_transaction = 128", "603 / 0.5 / 10 needs 121 a connection");

        Locks(TwoHundred(170_011) with { Leaves = 20, LeafIndexes = 20 }).Findings.Should().BeEmpty("43 locks fit");
    }

    [Fact]
    public void On_19_the_documented_capacity_is_the_real_one_and_a_carried_over_value_holds_half()
    {
        HealthFinding finding = Locks(TwoHundred(190_000, maxLocks: 128, maxConnections: 8)).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Documented capacity" && x.Value.Contains("from PostgreSQL 19 the lock table keeps no margin", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("Fast-path slots a backend", "128"));
        finding.Remedy!.Explanation.Should().Contain("holds about half the locks it did");
    }
}
