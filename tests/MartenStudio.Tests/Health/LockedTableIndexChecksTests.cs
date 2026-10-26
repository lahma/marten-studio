using static MartenStudio.Tests.Health.TableCheckBuilders;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Health;

/// <summary>
/// One table locked for a schema change costs the index checks that table and nothing more: T03, T04, T05 and T06 judge
/// every other table, skip the locked one and say how many they skipped - a count, never a name - and T06 never takes a
/// locked table's missing indexes for "no index blocks HOT" (W5-VERS-fix item 2).
/// </summary>
public class LockedTableIndexChecksTests
{
    private const string OneLocked = "1 table was locked for a schema change and not judged this sweep.";

    private static HashSet<string> Locked(params MartenTable[] tables) =>
        new(tables.Select(static x => x.Key), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void The_note_is_a_count_and_never_a_name()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable ints = Document<HealthIntDoc>();

        HealthTables.LockedNote(Locked()).Should().BeNull();
        HealthTables.LockedNote(Locked(orders)).Should().Be(OneLocked);
        HealthTables.LockedNote(Locked(orders, ints)).Should().Be("2 tables were locked for a schema change and not judged this sweep.")
            .And.NotContain(orders.Table);
        HealthTables.WithLockedNote("All indexes are valid.", Locked(orders)).Should().Be("All indexes are valid. " + OneLocked);
        HealthTables.WithLockedNote("All indexes are valid.", Locked()).Should().Be("All indexes are valid.");
    }

    [Fact]
    public void T03_judges_the_unlocked_table_skips_the_locked_one_and_counts_it()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable ints = Document<HealthIntDoc>();
        IndexFacts invalidHere = Index(orders, IndexRow(orders, "broken_here", "id") with { IsValid = false });
        IndexFacts invalidThere = Index(ints, IndexRow(ints, "broken_there", "id") with { IsValid = false });

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(
            new InvalidIndexFacts([invalidHere, invalidThere], [Table(orders), Table(ints, row => row with { InvalidPartitionIndexes = 2 })])
            {
                LockedTables = Locked(ints),
            },
            Evaluation());

        verdict.Findings.Should().ContainSingle().Which.Subject.Should().Be(invalidHere.Key, "the locked table is not judged, by its indexes or its partitions'");
        verdict.Note.Should().Be(OneLocked);
    }

    [Fact]
    public void T03_with_every_table_locked_is_quiet_and_counts_them_rather_than_saying_nothing_is_judged()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([], [Table(orders)]) { LockedTables = Locked(orders) }, Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Be(OneLocked);
    }

    [Fact]
    public void T04_judges_the_unlocked_table_and_counts_the_locked_one()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable ints = Document<HealthIntDoc>();
        IndexFacts single = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)"));
        IndexFacts pair = Index(orders, IndexRow(orders, "by_status_and_id", "(data ->> 'Status'::text)", "id"));
        IndexFacts lockedSingle = Index(ints, IndexRow(ints, "by_n", "n"));
        IndexFacts lockedPair = Index(ints, IndexRow(ints, "by_n_and_id", "n", "id"));

        HealthVerdict verdict = new RedundantIndexCheck().Evaluate(
            new RedundantIndexFacts([single, pair, lockedSingle, lockedPair]) { LockedTables = Locked(ints) },
            Evaluation());

        verdict.Findings.Should().ContainSingle().Which.Subject.Should().Be(single.Key);
        verdict.Note.Should().Be(OneLocked);
    }

    [Fact]
    public void T05_counts_the_locked_table_and_a_sweep_with_one_is_not_a_sweep_for_the_unscanned_clock()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts unused = Index(orders, IndexRow(orders, "never", "id") with { Scans = 0 });
        UnusedIndexFacts facts = new(Now, [unused], [Table(orders)], new DatabaseStatistics(Now.AddDays(-30), true, 2))
        {
            LockedTables = Locked(Document<HealthIntDoc>()),
        };

        new UnusedIndexCheck().Evaluate(facts, Evaluation()).Note.Should().Be(OneLocked);

        new UnusedIndexCheck().Sample(facts).Select(static x => x.Series).Should().NotContain(UnusedIndexCheck.SweptSeries,
            "a locked table's indexes are absent from the sample, which a swept sample would read as scanned and reset their clock");
        new UnusedIndexCheck().Sample(facts with { LockedTables = Locked() }).Select(static x => x.Series).Should().Contain(UnusedIndexCheck.SweptSeries);
    }

    [Fact]
    public void T06_never_raises_the_fill_factor_finding_for_a_locked_table_whose_indexes_it_did_not_see()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(20), [(HotUpdateCheck.UpdatesSeries + orders.Key, 0), (HotUpdateCheck.HotUpdatesSeries + orders.Key, 0)]),
            (TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + orders.Key, 20_000), (HotUpdateCheck.HotUpdatesSeries + orders.Key, 0)]));

        HotUpdateFacts unlocked = new([Table(orders)], [], new DatabaseStatistics(Now.AddDays(-30), true, 2));

        new HotUpdateCheck().Evaluate(unlocked, Evaluation(history)).Findings.Should().ContainSingle(
            "the anti-vacuity half: unlocked, no index read means the fill-factor finding");

        HealthVerdict locked = new HotUpdateCheck().Evaluate(unlocked with { LockedTables = Locked(orders) }, Evaluation(history));

        locked.Findings.Should().BeEmpty("its indexes were not read, so whether one blocks HOT is unknown, not 'none'");
        locked.Note.Should().Be(OneLocked);
    }

    [Fact]
    public void T02s_advisor_is_not_asked_about_a_whole_table_read_of_a_locked_table()
    {
        MartenTable doc = StatementBuilders.Doc;
        ObservedStatement statement = StatementBuilders.Statement(1, StatementBuilders.SelectOnDoc, [doc], 100, 5_000);
        ExplainPlanScan[] scans = [new("Seq Scan", doc.Table, null, "((data ->> $2) = $1)", null)];

        CountingAdvisor unlocked = new();
        _ = StatementPlanAdvice.Read(scans, statement, [], unlocked, Locked());
        unlocked.Calls.Should().Be(1, "the anti-vacuity half: an unlocked table's whole-table read is advised on");

        CountingAdvisor locked = new();
        _ = StatementPlanAdvice.Read(scans, statement, [], locked, Locked(doc));
        locked.Calls.Should().Be(0, "a locked table's indexes were not read, so the advisor would advise an index it may have");

        new HealthIndexRead([], Locked(doc)).IsLocked(doc.Key.ToUpperInvariant()).Should().BeTrue("a key compares ignoring case, as the checks compare table keys");
    }

    private sealed class CountingAdvisor : IHealthPlanAdvisor
    {
        public int Calls { get; private set; }

        public HealthScanAdvice? AdviseSequentialScan(PostgresStudio.Services.Sources.StudioTable table, string? filter, IReadOnlyList<IndexFacts> indexesOnTable)
        {
            Calls++;
            return null;
        }
    }
}
