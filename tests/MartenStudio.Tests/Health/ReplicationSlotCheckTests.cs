using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>R01's judgement over stated slots and a stated history, and no Postgres.</summary>
public class ReplicationSlotCheckTests
{
    private const long GiB = 1L << 30;
    private const long MiB = 1L << 20;

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static ReplicationSlot Slot(
        string name,
        long? retained,
        bool active = false,
        string? walStatus = "reserved",
        string type = "physical",
        TimeSpan? inactive = null,
        string? invalidation = null,
        long? safe = null,
        bool thisDatabase = true) =>
        new(name, type, type == "logical" ? "pgoutput" : null, type == "logical" ? (thisDatabase ? "orders" : "tenant_acme") : null, false, active,
            retained, walStatus, safe, type == "logical" ? null : 1_234, type == "logical" ? 5_678 : null, inactive, invalidation, null, thisDatabase);

    private static ReplicationSlotSnapshot Snapshot(DateTimeOffset at, long? cap, params ReplicationSlot[] slots) =>
        new(at, 160004, false, cap, 0, slots.Length, slots);

    private static ReplicationSlotFacts Facts(DateTimeOffset at, long? cap, params ReplicationSlot[] slots) =>
        new(Snapshot(at, cap, slots), new PostgresServerFacts(at, 160004, "16.4", PostgresPlatform.SelfHosted));

    /// <summary>A history of R01's own samples: each entry is a minute and the slots read then.</summary>
    private static HealthHistoryView History(params (int Minute, ReplicationSlot[] Slots)[] samples) =>
        new([
            .. samples.Select(static x => new HealthSample(
                Start.AddMinutes(x.Minute),
                new ReplicationSlotCheck().Sample(Facts(Start.AddMinutes(x.Minute), -1, x.Slots))
                    .ToDictionary(static p => p.Series, static p => p.Value, StringComparer.Ordinal))),
        ]);

    private static HealthVerdict Evaluate(ReplicationSlotFacts facts, HealthHistoryView? history = null) =>
        new ReplicationSlotCheck().Evaluate(facts, HealthEvaluation.At(facts.Slots.ReadAt, history: history));

    [Fact]
    public void No_slots_is_silence()
    {
        HealthVerdict verdict = Evaluate(Facts(Start, -1));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("This server has no replication slots.");
    }

    [Fact]
    public void Slots_holding_little_are_quiet_and_the_note_says_how_much_the_most_is()
    {
        HealthVerdict verdict = Evaluate(Facts(Start, -1, Slot("cdc", 40 * MiB), Slot("standby", 2 * MiB, active: true)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be("2 replication slots on this server, none of them forgotten or near a limit: the most WAL one holds is 40.0 MiB.");
    }

    [Fact]
    public void An_inactive_slot_over_a_gibibyte_for_ten_minutes_of_samples_is_a_warning()
    {
        ReplicationSlot slot = Slot("debezium_orders", 3 * GiB);
        HealthHistoryView history = History((0, [Slot("debezium_orders", 2 * GiB)]), (5, [Slot("debezium_orders", (long) (2.5 * GiB))]), (10, [slot]));

        HealthFinding finding = Evaluate(Facts(Start.AddMinutes(10), -1, slot), history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("inactive");
        finding.Title.Should().Be("An inactive replication slot has held 3.0 GiB of WAL for 10 min or more");
        finding.Evidence.Should().Contain(new HealthEvidence("Held for at least", "10 min"));
        finding.Evidence.Should().Contain(new HealthEvidence("max_slot_wal_keep_size", "unlimited (-1, the default)"));
        finding.WhyItMatters.Should().Contain("max_slot_wal_keep_size is unlimited on this server");
        finding.Remedy!.Code.Should().Contain("max_slot_wal_keep_size = 50GB");
        finding.Remedy.Explanation.Should().Contain("postgresql.conf");
    }

    [Fact]
    public void Under_ten_minutes_of_samples_it_is_watching_not_judging()
    {
        ReplicationSlot slot = Slot("debezium_orders", 3 * GiB);
        HealthHistoryView history = History((0, [Slot("debezium_orders", 2 * GiB)]), (7, [slot]));

        HealthVerdict verdict = Evaluate(Facts(Start.AddMinutes(7), -1, slot), history);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("1 inactive replication slot holds at least 1.0 GiB").And.Contain("has seen 7 min so far");
    }

    [Fact]
    public void A_sample_with_the_consumer_connected_breaks_the_run()
    {
        ReplicationSlot slot = Slot("debezium_orders", 3 * GiB);
        HealthHistoryView history = History(
            (0, [Slot("debezium_orders", 2 * GiB)]),
            (4, [Slot("debezium_orders", 2 * GiB, active: true)]),
            (8, [Slot("debezium_orders", 2 * GiB)]),
            (12, [slot]));

        HealthVerdict verdict = Evaluate(Facts(Start.AddMinutes(12), -1, slot), history);

        verdict.Findings.Should().BeEmpty("the run began at minute 8, after the consumer was seen connected");
        verdict.Note.Should().Contain("has seen 4 min so far");
    }

    [Fact]
    public void A_sample_below_a_gibibyte_breaks_the_run_too()
    {
        ReplicationSlot slot = Slot("cdc", 2 * GiB);
        HealthHistoryView history = History((0, [Slot("cdc", 512 * MiB)]), (6, [Slot("cdc", (long) (1.5 * GiB))]), (12, [slot]));

        Evaluate(Facts(Start.AddMinutes(12), -1, slot), history).Findings.Should().BeEmpty("only six minutes of it were over the line");
    }

    /// <summary>From PostgreSQL 17 the slot's own inactive_since is Postgres's clock, and needs no watching.</summary>
    [Fact]
    public void Postgres_17_s_inactive_since_counts_with_no_history_at_all()
    {
        ReplicationSlot slot = Slot("cdc", 3 * GiB, inactive: TimeSpan.FromHours(30));

        HealthFinding finding = Evaluate(Facts(Start, -1, slot)).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Held for at least", "1 d 6 h"));
    }

    /// <summary>A history of R01's samples against a cap, for the 80% rule.</summary>
    private static HealthHistoryView CapHistory(long cap, params (int Minute, ReplicationSlot[] Slots)[] samples) =>
        new([
            .. samples.Select(x => new HealthSample(
                Start.AddMinutes(x.Minute),
                new ReplicationSlotCheck().Sample(Facts(Start.AddMinutes(x.Minute), cap, x.Slots))
                    .ToDictionary(static p => p.Series, static p => p.Value, StringComparer.Ordinal))),
        ]);

    [Fact]
    public void An_inactive_slot_at_eighty_percent_of_max_slot_wal_keep_size_on_two_consecutive_samples_is_critical()
    {
        ReplicationSlot slot = Slot("subscription_orders", 85 * MiB, type: "logical", safe: 15 * MiB);
        HealthHistoryView history = CapHistory(100 * MiB, (0, [Slot("subscription_orders", 82 * MiB, type: "logical")]), (1, [slot]));

        HealthFinding finding = Evaluate(Facts(Start.AddMinutes(1), 100 * MiB, slot), history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("near-limit");
        finding.Title.Should().Be("An inactive replication slot holds 85% of max_slot_wal_keep_size");
        finding.Evidence.Should().Contain(new HealthEvidence("max_slot_wal_keep_size", "100 MiB"));
        finding.Evidence.Should().Contain(new HealthEvidence("WAL that may still be written before the first is lost", "15.0 MiB"));
    }

    /// <summary>
    /// A consumer that comes and goes: connected, then inactive at 85% for one sweep, then connected again. Without a
    /// window that is quiet, Critical (and a toast), quiet. With one, it is never Critical; a second sample at the line is.
    /// </summary>
    [Fact]
    public void A_reconnecting_consumer_at_eighty_percent_for_one_sample_does_not_flap_to_critical()
    {
        const long Cap = 100 * MiB;
        ReplicationSlot connected = Slot("subscription_orders", 85 * MiB, active: true, type: "logical");
        ReplicationSlot idle = Slot("subscription_orders", 85 * MiB, type: "logical");

        HealthVerdict once = Evaluate(Facts(Start.AddMinutes(1), Cap, idle), CapHistory(Cap, (0, [connected]), (1, [idle])));
        once.Findings.Should().BeEmpty("the sample before had the consumer connected");
        once.Note.Should().Contain("for the first time").And.Contain("if the next sample");

        HealthVerdict back = Evaluate(Facts(Start.AddMinutes(2), Cap, connected), CapHistory(Cap, (0, [connected]), (1, [idle]), (2, [connected])));
        back.Findings.Should().BeEmpty("a connected consumer is lag");

        HealthVerdict twice = Evaluate(Facts(Start.AddMinutes(2), Cap, idle), CapHistory(Cap, (0, [connected]), (1, [idle]), (2, [idle])));
        twice.Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical, "two consecutive samples at the line");
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(60, true)]
    [InlineData(600, true)]
    public void Postgres_17_s_inactive_since_is_the_cap_window_with_no_history(int seconds, bool critical)
    {
        ReplicationSlot slot = Slot("subscription_orders", 85 * MiB, type: "logical", inactive: TimeSpan.FromSeconds(seconds));

        Evaluate(Facts(Start, 100 * MiB, slot)).Findings.Should().HaveCount(critical ? 1 : 0);
    }

    [Fact]
    public void An_active_slot_at_eighty_percent_is_lag_and_quiet()
    {
        Evaluate(Facts(Start, 100 * MiB, Slot("standby", 90 * MiB, active: true))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void An_unreserved_slot_is_critical_even_while_a_consumer_is_connected()
    {
        HealthFinding finding = Evaluate(Facts(Start, 100 * MiB, Slot("standby", 120 * MiB, active: true, walStatus: "unreserved")))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("A replication slot is about to lose WAL it still needs");
        finding.Remedy!.Explanation.Should().Contain("before the next checkpoint");
    }

    [Theory]
    [InlineData("lost", null)]
    [InlineData(null, "wal_removed")]
    [InlineData("lost", "rows_removed")]
    public void A_lost_or_invalidated_slot_is_info(string? walStatus, string? reason)
    {
        HealthFinding finding = Evaluate(Facts(Start, 64 * MiB, Slot("old_replica", null, walStatus: walStatus, invalidation: reason)))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("lost");
        finding.Remedy.Should().BeNull("a lost slot's consumer is rebuilt, not reconfigured");
    }

    [Fact]
    public void Each_kind_is_one_finding_and_a_slot_is_in_the_worst_kind_only()
    {
        HealthVerdict verdict = Evaluate(Facts(
            Start,
            200 * GiB,
            Slot("near", 170 * GiB, inactive: TimeSpan.FromHours(1)),
            Slot("unreserved", 210 * GiB, walStatus: "unreserved"),
            Slot("idle_a", 2 * GiB, inactive: TimeSpan.FromHours(1)),
            Slot("idle_b", 3 * GiB, inactive: TimeSpan.FromHours(2)),
            Slot("gone", null, walStatus: "lost"),
            Slot("fine", 1 * MiB, active: true)));

        verdict.Findings.Select(static x => (x.Subject, x.Severity)).Should().Equal(
            ("near-limit", HealthSeverity.Critical), ("inactive", HealthSeverity.Warning), ("lost", HealthSeverity.Info));

        HealthFinding near = verdict.Findings[0];
        near.Title.Should().Be("2 replication slots are about to lose WAL they still need");
        near.Evidence.Should().Contain(new HealthEvidence("Slots past the line", "2 of 6 slots on this server"));

        HealthFinding inactive = verdict.Findings[1];
        inactive.Title.Should().Be("2 inactive replication slots hold 5.0 GiB of WAL between them, for 10 min or more");
        inactive.Evidence.Where(static x => x.Gate == HealthGate.Activity).Select(static x => x.Label)
            .Should().Equal("Slot 1 (most WAL)", "Slot 2");
    }

    /// <summary>A slot's name, plugin and database are evidence behind the activity gate, and nowhere else.</summary>
    [Fact]
    public void Names_plugins_and_databases_are_only_in_evidence_behind_the_activity_gate()
    {
        ReplicationSlot logical = Slot("debezium_orders", 90 * MiB, type: "logical", inactive: TimeSpan.FromMinutes(5));
        HealthFinding finding = Evaluate(Facts(Start, 100 * MiB, logical)).Findings.Single();

        HealthEvidence named = finding.Evidence.Should().ContainSingle(static x => x.Gate == HealthGate.Activity).Subject;
        named.Value.Should().Contain("'debezium_orders'").And.Contain("logical (pgoutput)").And.Contain("for database 'orders'")
            .And.Contain("catalog_xmin age 5,678");

        string open = finding.Title + "\n" + finding.WhyItMatters + "\n" + finding.Caveat + "\n" + finding.Subject + "\n"
            + string.Join("\n", finding.Evidence.Where(static x => x.Gate != HealthGate.Activity).Select(static x => x.Label + " " + x.Value))
            + "\n" + finding.Remedy?.Code + "\n" + finding.Remedy?.Explanation;

        open.Should().NotContain("debezium_orders").And.NotContain("pgoutput").And.NotContain("orders'");
    }

    /// <summary>
    /// B1: a logical slot of another database is counted and sized, and never named - not even behind the activity gate,
    /// because a visitor admitted to this database has not been admitted to that one.
    /// </summary>
    [Fact]
    public void A_logical_slot_of_another_database_is_counted_and_never_named_behind_any_gate()
    {
        ReplicationSlot theirs = Slot("debezium_acme", 90 * MiB, type: "logical", inactive: TimeSpan.FromMinutes(5), thisDatabase: false);
        ReplicationSlot physical = Slot("standby_replica", null, walStatus: "lost");

        HealthVerdict verdict = Evaluate(Facts(Start, 100 * MiB, theirs, physical));

        HealthFinding near = verdict.Findings.Single(static x => x.Subject == "near-limit");
        near.Evidence.Should().Contain(new HealthEvidence("Slots past the line", "1 of 2 slots on this server"), "it is counted");
        near.Evidence.Single(static x => x.Gate == HealthGate.Activity).Value
            .Should().StartWith("a logical slot of another database on this server, inactive");

        string everything = string.Join("\n", verdict.Findings.SelectMany(static x =>
            new[] { x.Title, x.WhyItMatters, x.Caveat, x.Subject, x.Key, x.Remedy?.Code, x.Remedy?.Explanation }
                .Concat(x.Evidence.Select(static e => e.Label + " " + e.Value))));

        everything.Should().NotContain("debezium_acme").And.NotContain("tenant_acme").And.NotContain("pgoutput",
            "neither the slot, its plugin nor its database is this database's to name");
        everything.Should().Contain("'standby_replica', physical", "a physical slot is the server's, and nameable at the activity gate");
    }

    [Fact]
    public void A_standby_is_measured_against_its_replay_position_and_says_so()
    {
        ReplicationSlotFacts facts = Facts(Start, 100 * MiB, Slot("cascade", 90 * MiB, inactive: TimeSpan.FromMinutes(5)));
        facts = facts with { Slots = facts.Slots with { InRecovery = true } };

        Evaluate(facts).Findings.Single().Evidence.Should().Contain(static x => x.Label == "Measured against" && x.Value.Contains("in recovery", StringComparison.Ordinal));
    }

    [Fact]
    public void The_remedy_names_where_the_platform_changes_a_setting()
    {
        ReplicationSlotFacts facts = Facts(Start, 100 * MiB, Slot("cdc", 90 * MiB, inactive: TimeSpan.FromMinutes(5))) with
        {
            Server = new PostgresServerFacts(Start, 160004, "16.4", new PostgresPlatform(PostgresPlatformKind.CloudSql, "flags")),
        };

        HealthFinding finding = Evaluate(facts).Findings.Single();

        finding.Remedy!.Explanation.Should().Contain("the instance's database flags (Cloud SQL)");
        finding.Evidence.Should().Contain(new HealthEvidence("Platform", "Google Cloud SQL for PostgreSQL"));
    }

    // ---------------------------------------------------------------------------------------------------
    // The samples R01 writes
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// H10-fix2: a forgotten slot in a database nothing else sweeps - another database's, on PostgreSQL 16, with no
    /// inactive_since - is raised from this database's own samples after 10 minutes, and its finding still names nothing.
    /// </summary>
    [Fact]
    public void Another_databases_forgotten_slot_before_Postgres_17_is_a_warning_from_this_databases_samples()
    {
        ReplicationSlot theirs = Slot("debezium_billing", 3 * GiB, type: "logical", thisDatabase: false);
        HealthHistoryView history = History((0, [Slot("debezium_billing", 2 * GiB, type: "logical", thisDatabase: false)]), (10, [theirs]));

        ReplicationSlotFacts facts = Facts(Start.AddMinutes(10), -1, theirs);
        facts.Slots.ServerVersion.Should().BeLessThan(170000, "the case is a server with no inactive_since");

        HealthFinding finding = Evaluate(facts, history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("inactive");
        NamesNothingOfTheirs(finding);
    }

    [Fact]
    public void Another_databases_slot_at_eighty_percent_on_two_consecutive_samples_is_critical_and_names_nothing()
    {
        const long Cap = 100 * MiB;
        ReplicationSlot theirs = Slot("debezium_billing", 85 * MiB, type: "logical", thisDatabase: false);
        HealthHistoryView history = CapHistory(Cap, (0, [Slot("debezium_billing", 84 * MiB, type: "logical", thisDatabase: false)]), (1, [theirs]));

        HealthFinding finding = Evaluate(Facts(Start.AddMinutes(1), Cap, theirs), history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("near-limit");
        NamesNothingOfTheirs(finding);
    }

    private static void NamesNothingOfTheirs(HealthFinding finding)
    {
        string everything = string.Join("\n", new[] { finding.Title, finding.WhyItMatters, finding.Caveat, finding.Subject, finding.Key, finding.Remedy?.Code, finding.Remedy?.Explanation }
            .Concat(finding.Evidence.Select(static e => e.Label + " " + e.Value)));

        everything.Should().NotContain("debezium_billing").And.NotContain("tenant_acme").And.NotContain("pgoutput");
        finding.Evidence.Should().Contain(static x => x.Gate == HealthGate.Activity && x.Value.StartsWith(ReplicationSlot.AnotherDatabase, StringComparison.Ordinal));
    }

    /// <summary>
    /// A series' qualifier - another database's slot name, here - is never logged: the sweep's one-writer rule names a
    /// series by its family.
    /// </summary>
    [Fact]
    public void A_series_is_named_in_a_log_line_by_its_family_and_never_by_its_slot()
    {
        HealthSweeper.SeriesFamily(ReplicationSlotCheck.SeriesPrefix + "debezium_billing").Should().Be("replication.slot-idle-retained:…");
        HealthSweeper.SeriesFamily("daemon.highest-sequence").Should().Be("daemon.highest-sequence");
    }

    [Fact]
    public void At_most_the_reads_limit_of_slots_is_sampled_most_wal_first()
    {
        ReplicationSlot[] many = [.. Enumerable.Range(0, HealthServerQueries.MaxSlots + 5)
            .Select(static i => Slot("slot_" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), (i + 1) * MiB))];

        List<HealthSamplePart> parts = [.. new ReplicationSlotCheck().Sample(Facts(Start, -1, many))];

        parts.Should().HaveCount(HealthServerQueries.MaxSlots);
        parts.Select(static x => x.Series).Should().NotContain(ReplicationSlotCheck.SeriesPrefix + "slot_000", "the smallest are the ones left out");
    }

    [Fact]
    public void Every_slot_is_sampled_another_databases_too_with_what_it_holds_while_inactive_and_zero_otherwise()
    {
        Dictionary<string, double> parts = new ReplicationSlotCheck()
            .Sample(Facts(
                Start,
                -1,
                Slot("idle", 2 * GiB),
                Slot("connected", 3 * GiB, active: true),
                Slot("gone", null, walStatus: "lost"),
                Slot("unreserving", null),
                Slot("another_databases", 5 * GiB, type: "logical", thisDatabase: false)))
            .ToDictionary(static x => x.Series, static x => x.Value, StringComparer.Ordinal);

        parts.Should().BeEquivalentTo(new Dictionary<string, double>
        {
            ["replication.slot-idle-retained:idle"] = 2 * GiB,
            ["replication.slot-idle-retained:connected"] = 0,
            ["replication.slot-idle-retained:gone"] = 0,
            ["replication.slot-idle-retained:unreserving"] = 0,
            ["replication.slot-idle-retained:another_databases"] = 5 * GiB,
        });
    }

    [Fact]
    public void No_slot_means_no_sample()
    {
        new ReplicationSlotCheck().Sample(Facts(Start, -1)).Should().BeEmpty();
    }
}
