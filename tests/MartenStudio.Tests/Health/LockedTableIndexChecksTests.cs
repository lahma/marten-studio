using static MartenStudio.Tests.Health.TableCheckBuilders;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Health;

/// <summary>
/// One table locked for a schema change costs the index checks that table and nothing more: T03, T04, T05 and T06 judge
/// every other table, skip the locked one and say how many they skipped - a count, never a name - and T06 never takes a
/// locked table's missing indexes for "no index blocks HOT" (W5-VERS-fix item 2).
/// </summary>
public class LockedTableIndexChecksTests
{
    private const string OneLocked = "1 table was locked for a schema change and not judged this sweep.";

    private static HashSet<string> Locked(params StudioTable[] tables) =>
        new(tables.Select(static x => x.Key), StringComparer.Ordinal);

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
    public void T05_judges_the_unlocked_table_and_counts_the_locked_one()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable ints = Document<HealthIntDoc>();
        IndexFacts unused = Index(orders, IndexRow(orders, "never", "id") with { Scans = 0 });
        IndexFacts lockedUnused = Index(ints, Undeparsed(IndexRow(ints, "never_either", "n") with { Scans = 0 }));
        UnusedIndexFacts facts = new(
            Now,
            [unused, lockedUnused],
            [Table(orders, row => row with { Inserts = 200_000 }), Table(ints, row => row with { Inserts = 200_000 })],
            new DatabaseStatistics(Now.AddDays(-30), true, 2))
        {
            LockedTables = Locked(ints),
        };

        HealthVerdict verdict = new UnusedIndexCheck().Evaluate(facts, UsageEvaluation());
        verdict.Note.Should().Be(OneLocked);
        verdict.Findings.Should().ContainSingle("the unlocked table is judged").Which.Subject.Should().Be(unused.Key);
    }

    /// <summary>
    /// While the usage guard withholds every verdict (issue #8 G4), the status row still counts the locked table: the version
    /// matrix holds every index check's row to it behind a held lock.
    /// </summary>
    [Fact]
    public void T05_withheld_by_the_guard_still_counts_the_locked_table()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts unused = Index(orders, IndexRow(orders, "never", "id") with { Scans = 0 });
        UnusedIndexFacts facts = new(Now, [unused], [Table(orders, row => row with { Inserts = 200_000 })], new DatabaseStatistics(null, true, 2))
        {
            LockedTables = Locked(Document<HealthIntDoc>()),
        };

        HealthVerdict verdict = new UnusedIndexCheck().Evaluate(facts, Evaluation() with { Statistics = StatisticsOver(TimeSpan.FromHours(5)) });

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().StartWith("Withheld: statistics 5 h old").And.EndWith(OneLocked);
    }

    /// <summary>What an index of a locked table looks like as the read returns it: no definition, no deparsed columns.</summary>
    private static HealthIndexRow Undeparsed(HealthIndexRow row) => row with { Definition = string.Empty, Columns = [], TableLocked = true };

    /// <summary>Item 1's retry, the first half: a 55P03 from the race is asked once more, and the second answer is the read's.</summary>
    [Fact]
    public async Task A_lock_timeout_on_the_first_attempt_is_asked_again()
    {
        int attempts = 0;

        int answer = await HealthTables.RetryOnceBehindALockAsync(
            _ => ++attempts == 1 ? Task.FromException<int>(LockTimeout()) : Task.FromResult(42),
            CancellationToken.None);

        answer.Should().Be(42);
        attempts.Should().Be(2);
    }

    /// <summary>
    /// The second half: a second 55P03 is not guessed at - it reaches the sweep, which classifies it as a timeout - and
    /// anything else is never retried.
    /// </summary>
    [Fact]
    public async Task A_second_lock_timeout_reaches_the_sweep_and_nothing_else_is_retried()
    {
        int attempts = 0;

        Func<Task> twice = () => HealthTables.RetryOnceBehindALockAsync<int>(_ => { attempts++; throw LockTimeout(); }, CancellationToken.None);
        (await twice.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        attempts.Should().Be(2);

        HealthSweeper.Classify(LockTimeout(), budgetExpired: false).Problem.UnavailableReason.Should().Be(HealthUnavailableReason.TimedOut,
            "the export keeps a timeout a timeout");

        int other = 0;
        Func<Task> refused = () => HealthTables.RetryOnceBehindALockAsync<int>(
            _ => { other++; throw new PostgresException("permission denied", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege); },
            CancellationToken.None);
        await refused.Should().ThrowAsync<PostgresException>();
        other.Should().Be(1);
    }

    private static PostgresException LockTimeout() =>
        new("canceling statement due to lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable);

    [Fact]
    public void T06_never_raises_the_fill_factor_finding_for_a_locked_table_whose_indexes_it_did_not_see()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(20), [(HotUpdateCheck.UpdatesSeries + orders.Key, 0), (HotUpdateCheck.HotUpdatesSeries + orders.Key, 0)]),
            (TimeSpan.Zero, [(HotUpdateCheck.UpdatesSeries + orders.Key, 20_000), (HotUpdateCheck.HotUpdatesSeries + orders.Key, 0)]));

        HotUpdateFacts unlocked = new([Table(orders)], [], new DatabaseStatistics(Now.AddDays(-30), true, 2));

        new HotUpdateCheck().Evaluate(unlocked, UsageEvaluation(history)).Findings.Should().ContainSingle(
            "the anti-vacuity half: unlocked, no index read means the fill-factor finding");

        HealthVerdict locked = new HotUpdateCheck().Evaluate(unlocked with { LockedTables = Locked(orders) }, UsageEvaluation(history));

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

        new HealthIndexRead([], Locked(doc)).IsLocked(doc.Key).Should().BeTrue();
    }

    /// <summary>
    /// G1M keeps <c>"Orders"</c> and <c>orders</c> as two tables (Postgres compares names ordinally), so every key comparison
    /// here is ordinal: T05 judges both instead of throwing on the second, and each index is told to its own table.
    /// </summary>
    [Fact]
    public void Two_tables_whose_names_differ_only_in_case_are_two_tables_to_T05()
    {
        StudioTable upper = new("public", "Orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        StudioTable lower = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        HealthTableRow row = Row(Document<HealthOrderDoc>()) with { Inserts = 200_000 };

        UnusedIndexFacts facts = new(
            Now,
            [Index(upper, CaseRow(upper, "upper_never")), Index(lower, CaseRow(lower, "lower_never"))],
            [new TableFacts(upper, row with { Schema = "public", Table = "Orders" }), new TableFacts(lower, row with { Schema = "public", Table = "orders", Inserts = 10 })],
            new DatabaseStatistics(Now.AddDays(-30), true, 2));

        HealthVerdict verdict = new UnusedIndexCheck().Evaluate(facts, UsageEvaluation());

        verdict.Findings.Should().ContainSingle("only \"Orders\" took enough writes - its index is not judged by orders' row, nor the other way")
            .Which.Subject.Should().Be("public.Orders.upper_never");
    }

    /// <summary>A lock on <c>"Orders"</c> skips that table and never <c>orders</c>.</summary>
    [Fact]
    public void A_lock_on_one_of_two_case_variant_tables_skips_that_one_alone()
    {
        StudioTable upper = new("public", "Orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        StudioTable lower = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        HealthIndexRead read = new([], Locked(upper));

        read.IsLocked(upper.Key).Should().BeTrue();
        read.IsLocked(lower.Key).Should().BeFalse("\"Orders\" and orders are two tables");
        HealthIndexRead.Empty.LockedTables.Should().BeEmpty();

        IndexFacts lowerInvalid = Index(lower, CaseRow(lower, "lower_broken") with { IsValid = false });
        new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([lowerInvalid], []) { LockedTables = read.LockedTables }, Evaluation())
            .Findings.Should().ContainSingle("a lock on \"Orders\" leaves orders judged").Which.Subject.Should().Be(lowerInvalid.Key);
    }

    private static HealthIndexRow CaseRow(StudioTable table, string name) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        Name = name,
        Columns = ["id"],
        KeyColumnCount = 1,
        Bytes = 8 * 1024 * 1024,
        Scans = 0,
        ReferencedColumns = ["id"],
    };

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
