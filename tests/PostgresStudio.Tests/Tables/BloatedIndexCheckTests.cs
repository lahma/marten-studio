using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// T25 at each edge, both variants, on B0's numbers (G4: a 95%-empty <c>int4</c> primary key at 7.1 times the issue's rule,
/// a dense one at 0.4) and on the calibration W5-B1 measured on 17 (a dense <c>int4</c> key at 22.6 bytes an entry).
/// </summary>
public class BloatedIndexCheckTests
{
    private const long MiB = 1024 * 1024;

    private static HealthIndexRow Int4Key(StudioTable table, long bytes, double entries) =>
        IndexRow(table, table.Table + "_pkey", 1) with { Bytes = bytes, EstimatedTuples = entries, IsPrimaryKey = true, IsUnique = true };

    [Fact]
    public void An_entry_is_its_header_and_keys_aligned_to_eight_bytes_and_a_line_pointer()
    {
        BloatedIndexCheck.EntryBytesFor([4], [-1]).Should().Be(20, "int4: 8 + 4 = 12, aligned to 16, + 4");
        BloatedIndexCheck.EntryBytesFor([8], [-1]).Should().Be(20, "int8: 8 + 8 = 16, + 4");
        BloatedIndexCheck.EntryBytesFor([4, 4], [-1, -1]).Should().Be(20, "(int4, int4) as one pair");
        BloatedIndexCheck.EntryBytesFor([16], [-1]).Should().Be(28, "uuid: 8 + 16 = 24, + 4");
        BloatedIndexCheck.EntryBytesFor([-1], [-1]).Should().Be(44, "text with no statistics, at 32: 40, + 4");
        BloatedIndexCheck.EntryBytesFor([-1], [12]).Should().Be(28, "text averaging 12 bytes: 20 aligned to 24, + 4");
    }

    [Fact]
    public void A_dense_int4_key_reads_as_its_calibration_and_is_quiet()
    {
        // 200,000 entries in 4,513,792 bytes measured on 17: 22.6 an entry against 22.2 expected - scaled up past 64 MiB.
        StudioTable orders = Host("orders");
        IndexBloatEstimate dense = BloatedIndexCheck.Estimate(Int4Key(orders, 4_513_792L * 20, 4_000_000))!;

        dense.KeyWidthRatio.Should().BeApproximately(1.02, 0.01);
        dense.IssueRuleRatio.Should().BeApproximately(0.35, 0.01, "B0: a dense int4 key is 0.4 of the issue's rule");
        BloatedIndexCheck.VariantOf(dense).Should().BeNull();
    }

    [Fact]
    public void A_95_percent_empty_int4_key_misses_the_issues_rule_and_is_caught_by_the_key_width_screen_at_Info()
    {
        // B0's bloat_pkey, 4,513,792 bytes for 10,000 entries, scaled by 20 past the 64 MiB line.
        StudioTable orders = Host("orders");
        IndexBloatEstimate empty = BloatedIndexCheck.Estimate(Int4Key(orders, 4_513_792L * 20, 200_000))!;

        empty.IssueRuleRatio.Should().BeApproximately(7.05, 0.01, "B0: 7.1 times - under the issue's 10");
        empty.KeyWidthRatio.Should().BeApproximately(20.3, 0.1);
        BloatedIndexCheck.VariantOf(empty).Should().Be(BloatedIndexCheck.KeyWidthVariant);

        HealthFinding finding = new BloatedIndexCheck().Evaluate(
            new BloatedIndexFacts([Index(orders, Int4Key(orders, 4_513_792L * 20, 200_000), IndexOrigin.PrimaryKey)]),
            Evaluation()).Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Variant.Should().Be("key-width");
        finding.Caveat.Should().Contain("An estimate from the catalog").And.Contain("pgstatindex");
        finding.Remedy!.Code.Should().Be("REINDEX INDEX CONCURRENTLY \"app\".\"orders_pkey\";");
        finding.Link!.Route.Should().Be("maintenance");
    }

    [Fact]
    public void The_issues_rule_fires_at_ten_times_64_bytes_an_entry_and_wins_over_the_screen()
    {
        StudioTable events = Host("events");
        long bytes = 100 * MiB;

        IndexBloatEstimate atTen = BloatedIndexCheck.Estimate(Int4Key(events, bytes, bytes / 640.0))!;
        atTen.IssueRuleRatio.Should().BeApproximately(10, 1e-9);
        BloatedIndexCheck.VariantOf(atTen).Should().Be(BloatedIndexCheck.IssueRuleVariant);

        IndexBloatEstimate under = BloatedIndexCheck.Estimate(Int4Key(events, bytes, (bytes / 640.0) + 10))!;
        BloatedIndexCheck.VariantOf(under).Should().Be(BloatedIndexCheck.KeyWidthVariant, "just under the issue's rule, still far past the screen");

        HealthFinding warning = new BloatedIndexCheck().Evaluate(new BloatedIndexFacts([Index(events, Int4Key(events, bytes, bytes / 640.0))]), Evaluation())
            .Findings.Single();
        warning.Severity.Should().Be(HealthSeverity.Warning);
        warning.Variant.Should().Be("issue-rule");
    }

    [Fact]
    public void The_screen_fires_at_four_times_the_packed_size_and_not_below()
    {
        StudioTable orders = Host("orders");
        double expectedPerEntry = 20 / 0.9;
        long bytes = 80 * MiB;

        IndexBloatEstimate four = BloatedIndexCheck.Estimate(Int4Key(orders, bytes, bytes / (4 * expectedPerEntry)))!;
        four.KeyWidthRatio.Should().BeApproximately(4, 1e-9);
        BloatedIndexCheck.VariantOf(four).Should().Be(BloatedIndexCheck.KeyWidthVariant);

        BloatedIndexCheck.VariantOf(BloatedIndexCheck.Estimate(Int4Key(orders, bytes, bytes / (3.9 * expectedPerEntry)))!).Should().BeNull();
    }

    [Fact]
    public void Below_64_MiB_an_unknown_entry_count_or_another_access_method_is_never_judged()
    {
        StudioTable orders = Host("orders");

        BloatedIndexCheck.VariantOf(BloatedIndexCheck.Estimate(Int4Key(orders, (64 * MiB) - 8192, 10))!).Should().BeNull("under 64 MiB");
        BloatedIndexCheck.VariantOf(BloatedIndexCheck.Estimate(Int4Key(orders, 64 * MiB, 10))!).Should().Be(BloatedIndexCheck.IssueRuleVariant, "at 64 MiB");
        BloatedIndexCheck.Estimate(Int4Key(orders, 100 * MiB, 10) with { EstimatedTuples = null }).Should().BeNull("never vacuumed or analysed");
        BloatedIndexCheck.Estimate(Int4Key(orders, 100 * MiB, 10) with { AccessMethod = "gin" }).Should().BeNull("the catalog says nothing honest of a GIN index's entries");
    }

    [Fact]
    public void An_indexs_own_fillfactor_is_the_one_it_is_judged_at()
    {
        StudioTable orders = Host("orders");
        HealthIndexRow row = Int4Key(orders, 80 * MiB, 1_000_000);

        double packedAt90 = BloatedIndexCheck.Estimate(row)!.ExpectedBytes;
        BloatedIndexCheck.Estimate(row with { IndexOptions = ["fillfactor=50"] })!.ExpectedBytes.Should().BeApproximately(packedAt90 * 90 / 50, 1);
    }

    [Fact]
    public void A_librarys_table_is_judged_too_and_a_locked_one_is_counted()
    {
        StudioTable triggers = LibraryTable("qrtz_triggers");
        HealthIndexRow bloated = Int4Key(triggers, 100 * MiB, 1_000);

        HealthFinding finding = new BloatedIndexCheck().Evaluate(new BloatedIndexFacts([Index(triggers, bloated)]), Evaluation()).Findings.Single();
        finding.Remedy!.Code.Should().Contain("REINDEX INDEX CONCURRENTLY", "bloat is upkeep, not index design (issue #8 G1)");

        HealthVerdict locked = new BloatedIndexCheck().Evaluate(
            new BloatedIndexFacts([Index(triggers, bloated)]) { LockedTables = new HashSet<string>([triggers.Key], StringComparer.Ordinal) },
            Evaluation());
        locked.Findings.Should().BeEmpty();
        locked.Note.Should().Contain("1 table was locked");
    }

    [Fact]
    public void A_hostile_index_name_is_quoted_in_the_reindex()
    {
        StudioTable odd = Host("odd", "Sales Data");
        HealthIndexRow row = Int4Key(odd, 100 * MiB, 1_000) with { Name = "Weird \"Name\"" };

        new BloatedIndexCheck().Evaluate(new BloatedIndexFacts([Index(odd, row)]), Evaluation()).Findings.Single().Remedy
            .Should().BeNull("a name with a double quote in it gets no statement");

        new BloatedIndexCheck().Evaluate(new BloatedIndexFacts([Index(odd, row with { Name = "Weird; Name" })]), Evaluation()).Findings.Single().Remedy!.Code
            .Should().Be("REINDEX INDEX CONCURRENTLY \"Sales Data\".\"Weird; Name\";");
    }
}
