using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>
/// T14's two follow-ups (W5-V1, backlog §C): honest time beyond the ring - a hold dated by the first sweep that saw its own
/// unchanged identity, kept in a bounded first-seen map that outlives the history's ring - and slots matched to a writer by
/// raw transaction id; and B0 §4's correction, that a standby's feedback through a slot is the slot's xmin.
/// </summary>
public class HorizonFollowUpTests
{
    private static HealthWraparoundDatabase Database => new()
    {
        FreezeMaxAge = 200_000_000,
        MultixactFreezeMaxAge = 400_000_000,
        ServerVersion = 170_000,
        DatabaseXidAge = 5_000,
        ServerXidAge = 5_000,
        XidCounter = 1_000_000,
    };

    private static HorizonFacts Facts(params HorizonHolder[] holders) =>
        new(Now, SessionVisibility.Everything, holders, Database, [], false, false);

    /// <summary>A writer whose own transaction id is <paramref name="xid" />, whose timings this role may not read.</summary>
    private static HorizonHolder Writer(long xid = 700_000, long xids = 300_000) => HorizonHolder.From(new HealthHorizonHolderRow
    {
        Kind = HealthHorizonHolderKind.Session,
        XidAge = xids,
        OwnXidAge = xids,
        OwnXid = xid,
        ProcessId = 4242,
        BackendStart = Now.AddDays(-2),
        HasTransactionId = true,
        Hidden = true,
    });

    private static ReplicationSlot Slot(string name, int? xminAge, long? xmin, bool active = true, string type = "physical") =>
        new(name, type, null, null, false, active, 16_777_216, "reserved", null, xminAge, null, null, null, null, ThisDatabase: true)
        {
            Xmin = xmin,
        };

    /// <summary>A history whose first-seen map saw <paramref name="series" /> first at <paramref name="first" /> - beyond any ring.</summary>
    private static HealthHistoryView Seen(string series, DateTimeOffset first) =>
        History((TimeSpan.Zero, [("tables.anything", 1)])) with
        {
            Sightings = new Dictionary<string, HealthSighting> { [series] = new(first, Now, HealthGate.Database) },
        };

    [Fact]
    public void A_writer_whose_timings_are_withheld_is_dated_by_the_first_sweep_that_saw_it_well_beyond_the_ring()
    {
        HorizonHolder writer = Writer();
        string series = HorizonHeldCheck.SightingOf(writer)!;

        (TimeSpan? age, HorizonTimeKind kind) = HorizonHeldCheck.AgeOf(writer, Facts(writer), Evaluation(Seen(series, Now.AddHours(-3))), HealthHistoryView.Empty.Trend("x"));

        age.Should().Be(TimeSpan.FromHours(3), "the map outlives the ring's hour");
        kind.Should().Be(HorizonTimeKind.Watched);

        HealthFinding finding = new HorizonHeldCheck().Evaluate(Facts(writer), Evaluation(Seen(series, Now.AddHours(-3))))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning, "a writer held three hours is past the hour");
        finding.Title.Should().Contain("at least 3 h 0 min (the advisor has seen it hold this horizon, unchanged, that long)");
        finding.Caveat.Should().Contain("dated from the first sweep that saw this very hold");
    }

    [Fact]
    public void A_sighting_names_nothing_of_the_identity_it_dates()
    {
        HorizonHolder writer = Writer();

        HealthSamplePart part = new HorizonHeldCheck().Sample(Facts(writer)).Should().ContainSingle().Subject;

        part.Remembered.Should().BeTrue();
        part.Series.Should().StartWith(HorizonHeldCheck.SightingSeries).And.NotContain("4242").And.NotContain("700000");
        writer.Identity.Should().Contain("4242").And.Contain("700000", "the identity itself is what changes when the hold does");
    }

    [Fact]
    public void A_different_backend_or_transaction_is_a_different_hold()
    {
        HorizonHeldCheck.SightingOf(Writer(xid: 700_000)).Should().NotBe(HorizonHeldCheck.SightingOf(Writer(xid: 700_001)));
    }

    [Fact]
    public void A_slot_whose_xmin_stays_put_with_few_transactions_behind_it_is_not_dated_by_the_map()
    {
        HorizonHolder slot = HorizonHolder.From(Slot("standby1", 500, 999_500), 0).Single();
        string series = HorizonHeldCheck.SightingOf(slot)!;

        HorizonHeldCheck.AgeOf(slot, Facts(slot), Evaluation(Seen(series, Now.AddHours(-5))), HealthHistoryView.Empty.Trend("x"))
            .Kind.Should().Be(HorizonTimeKind.Unknown, "five hundred transactions held back is nothing, however long");
    }

    [Fact]
    public void A_slot_holding_thousands_back_unchanged_for_two_hours_is_a_warning_worded_as_the_standby_behind_it()
    {
        HorizonHolder slot = HorizonHolder.From(Slot("standby1", 90_000, 910_000), 0).Single();
        string series = HorizonHeldCheck.SightingOf(slot)!;

        slot.StandbyThroughSlot.Should().BeTrue();
        HorizonHeldCheck.KindText(slot).Should().Be("a standby, through a physical replication slot");

        HealthFinding finding = new HorizonHeldCheck().Evaluate(Facts(slot), Evaluation(Seen(series, Now.AddHours(-2))))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().StartWith("A standby, through a physical replication slot, has held back vacuum for at least 2 h 0 min");
        finding.Evidence.Should().Contain(x => x.Label == "Who it is" && x.Gate == HealthGate.Activity
            && x.Value.StartsWith("a standby, through slot 'standby1'", StringComparison.Ordinal));
        finding.WhyItMatters.Should().Contain("pg_stat_replication shows no backend_xmin");
    }

    [Fact]
    public void An_inactive_physical_slot_is_still_a_slot_not_a_standby()
    {
        HorizonHolder slot = HorizonHolder.From(Slot("gone", 90_000, 910_000, active: false), 0).Single();

        slot.StandbyThroughSlot.Should().BeFalse();
        HorizonHeldCheck.KindText(slot).Should().Be("a physical replication slot");
    }

    /// <summary>
    /// Slots by raw xid: a slot is read in another statement, so its age is never matched - but its raw xmin pinned at
    /// exactly a writer's own backend_xid says it is behind that writer, which is the one to end.
    /// </summary>
    [Fact]
    public void A_slot_pinned_at_a_writer_s_own_transaction_id_is_behind_that_writer()
    {
        HorizonHolder writer = Writer(xid: 700_000, xids: 300_000);
        HorizonHolder slot = HorizonHolder.From(Slot("standby1", 300_002, 700_000), 0).Single();

        IReadOnlyDictionary<HorizonHolder, HorizonHolder> roots = HorizonHeldCheck.RootsOf([writer, slot]);

        roots.Should().ContainKey(slot).WhoseValue.Should().BeSameAs(writer);
        roots.Should().NotContainKey(writer);

        HorizonHolder elsewhere = HorizonHolder.From(Slot("standby2", 300_002, 699_999), 0).Single();
        HorizonHeldCheck.RootsOf([writer, elsewhere]).Should().BeEmpty("a slot at another id is its own holder");
    }

    [Fact]
    public void The_slot_read_carries_each_xmin_raw_and_the_holder_its_identity()
    {
        ReplicationSlot slot = Slot("cdc", 40, 123_456, type: "logical") with { CatalogXmin = 123_400 };
        slot = slot with { CatalogXminAge = 96 };

        List<HorizonHolder> holders = [.. HorizonHolder.From(slot, 3)];

        holders.Should().HaveCount(2);
        holders[0].Xmin.Should().Be(123_456);
        holders[1].Xmin.Should().Be(123_400);
        holders.Select(static x => x.Identity).Should().OnlyHaveUniqueItems().And.AllSatisfy(static x => x.Should().Contain("cdc"));
    }

    // -----------------------------------------------------------------------------------------------
    // The first-seen map itself
    // -----------------------------------------------------------------------------------------------

    private static HealthSample Sample(DateTimeOffset at) => new(at, new Dictionary<string, double> { ["tables.x"] = 1 });

    [Fact]
    public void The_map_keeps_the_first_sighting_beyond_the_ring_and_drops_a_series_no_sweep_in_the_window_saw()
    {
        HealthHistory history = new();
        Dictionary<string, HealthGate> seen = new() { ["tables.horizon-seen:aa"] = HealthGate.Database };

        DateTimeOffset start = Now.AddHours(-3);

        // The hold is seen in every sweep for three hours - three times the ring's sixty one-minute samples.
        for (int i = 0; i <= 180; i++)
        {
            history.Append("s", "db", Sample(start.AddMinutes(i)), TimeSpan.Zero, seen);
        }

        HealthHistoryView view = history.Read("s", "db");
        view.Samples.Should().HaveCount(HealthHistory.Capacity);
        view.FirstSeen("tables.horizon-seen:aa").Should().Be(start, "the map outlives the ring");

        // It ends: sweeps go on without it, and once no sample in the ring saw it, it is gone.
        for (int i = 181; i <= 245; i++)
        {
            history.Append("s", "db", Sample(start.AddMinutes(i)), TimeSpan.Zero, new Dictionary<string, HealthGate> { ["tables.horizon-seen:bb"] = HealthGate.Database });
        }

        history.Read("s", "db").FirstSeen("tables.horizon-seen:aa").Should().BeNull();
        history.Read("s", "db").FirstSeen("tables.horizon-seen:bb").Should().Be(start.AddMinutes(181));
    }

    [Fact]
    public void The_map_is_bounded_and_a_series_past_the_bound_waits_for_room()
    {
        HealthHistory history = new();
        Dictionary<string, HealthGate> many = Enumerable.Range(0, HealthHistory.SightingCapacity + 10)
            .ToDictionary(static i => "tables.horizon-seen:" + i, static _ => HealthGate.Database);

        history.Append("s", "db", Sample(Now), TimeSpan.Zero, many);

        history.Read("s", "db").Sightings.Should().HaveCount(HealthHistory.SightingCapacity, "every one was seen in this very sweep, so none could go");

        // A later sweep that sees only a new one evicts the one seen longest ago.
        history.Append("s", "db", Sample(Now.AddMinutes(1)), TimeSpan.Zero, new Dictionary<string, HealthGate> { ["tables.horizon-seen:new"] = HealthGate.Database });

        HealthHistoryView view = history.Read("s", "db");
        view.Sightings.Should().HaveCount(HealthHistory.SightingCapacity);
        view.FirstSeen("tables.horizon-seen:new").Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void A_sighting_is_read_at_or_below_its_writer_s_gate_only()
    {
        HealthHistory history = new();
        history.Append("s", "db", Sample(Now), TimeSpan.Zero, new Dictionary<string, HealthGate> { ["tables.horizon-seen:aa"] = HealthGate.Database });

        HealthHistoryView view = history.Read("s", "db");
        view.VisibleAt(HealthGate.Database).FirstSeen("tables.horizon-seen:aa").Should().Be(Now);
        view.VisibleAt(HealthGate.None).FirstSeen("tables.horizon-seen:aa").Should().BeNull();
    }

    [Fact]
    public void A_sighting_is_never_a_number_of_the_sample()
    {
        HealthHistory history = new();
        history.Append("s", "db", Sample(Now), TimeSpan.Zero, new Dictionary<string, HealthGate> { ["tables.horizon-seen:aa"] = HealthGate.Database });

        history.Read("s", "db").Samples.Should().ContainSingle().Which.Values.Should().NotContainKey("tables.horizon-seen:aa");
    }
}
