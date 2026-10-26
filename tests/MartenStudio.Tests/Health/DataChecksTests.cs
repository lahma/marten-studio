using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.TableCheckBuilders;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>T09, T10, T11 and T12 over stated facts.</summary>
public class DataChecksTests
{
    // -----------------------------------------------------------------------------------------------
    // T09 Large documents
    // -----------------------------------------------------------------------------------------------

    private static DocumentSizeFacts Sampled(MartenTable table, int sampled, double p95, long toast = 0) =>
        new(Table(table, row => row with { LiveRows = 50_000, HeapBytes = 1_000_000, ToastBytes = toast }), new DocumentSizeSampleRow(sampled, p95, (long) p95 * 2), null);

    [Theory]
    [InlineData(70 * 1024, nameof(HealthSeverity.Info))]
    [InlineData(600 * 1024, nameof(HealthSeverity.Warning))]
    public void A_large_95th_percentile_from_enough_rows_is_a_finding_and_a_very_large_one_a_warning(double p95, string expected)
    {
        HealthSeverity severity = Enum.Parse<HealthSeverity>(expected);
        MartenTable orders = Document<HealthOrderDoc>();

        HealthFinding finding = new LargeDocumentsCheck()
            .Evaluate(new LargeDocumentsFacts([Sampled(orders, 1_500, p95, toast: 3_000_000)], false), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T09");
        finding.Severity.Should().Be(severity);
        finding.Title.Should().Contain("HealthOrderDoc");
        finding.Evidence.Should().Contain(new HealthEvidence("Share of the table in TOAST", "75%"));
        finding.Remedy.Should().BeNull("modelling a document is the host's design, not a line");
    }

    [Fact]
    public void A_small_sample_or_small_documents_say_nothing_and_the_note_counts_what_was_sampled()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable media = Document<HealthStringDoc>();

        HealthVerdict verdict = new LargeDocumentsCheck().Evaluate(
            new LargeDocumentsFacts([Sampled(orders, 40, 900 * 1024), Sampled(media, 2_000, 10 * 1024)], StoppedEarly: true),
            Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Sampled 2 document tables").And.Contain("budget ran out");
    }

    [Theory]
    [InlineData(0, 100f)]
    [InlineData(1_000, 100f)]
    [InlineData(400_000, 1f)]
    [InlineData(1_000_000_000, 0.01f)]
    public void The_sample_percentage_aims_at_the_target_and_stays_between_a_hundredth_and_all(long rows, float percent)
    {
        LargeDocumentsCheck.PercentFor(rows).Should().BeApproximately(percent, 0.0001f);
    }

    // -----------------------------------------------------------------------------------------------
    // T10 Soft deletes accumulating
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_soft_deleted_collection_that_is_mostly_deleted_is_info_with_a_purge_job()
    {
        MartenTable orders = Document<HealthOrderDoc>(static o => o.Schema.For<HealthOrderDoc>().SoftDeleted());
        orders.SoftDeleted.Should().BeTrue("the mapping's DeleteStyle says so (hard rule 10)");

        HealthFinding finding = new SoftDeletesCheck()
            .Evaluate(new SoftDeletesFacts([new SoftDeleteFacts(orders, new SoftDeleteShareRow(orders.Schema, orders.Table, 10_000, 10_000, 0.45))]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T10");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Be("45% of the HealthOrderDoc documents are soft-deleted");
        finding.Remedy!.Code.Should().Contain("session.HardDeleteWhere<HealthOrderDoc>(x => x.DeletedBefore(");
        finding.Remedy.Code.Should().NotContain("per tenant", "the collection is not multi-tenanted");
    }

    [Fact]
    public void A_multi_tenanted_collection_s_purge_job_says_to_run_it_per_tenant()
    {
        MartenTable orders = Document<HealthOrderDoc>(static o => o.Schema.For<HealthOrderDoc>().SoftDeleted().MultiTenanted());

        HealthFinding finding = new SoftDeletesCheck()
            .Evaluate(new SoftDeletesFacts([new SoftDeleteFacts(orders, new SoftDeleteShareRow(orders.Schema, orders.Table, 10_000, 10_000, 0.9))]), Evaluation())
            .Findings.Single();

        finding.Remedy!.Code.Should().Contain("per tenant");
    }

    [Fact]
    public void No_statistics_is_cannot_see_never_none_and_a_small_table_or_a_low_share_says_nothing()
    {
        MartenTable orders = Document<HealthOrderDoc>(static o => o.Schema.For<HealthOrderDoc>().SoftDeleted());
        SoftDeletesCheck check = new();

        HealthVerdict unseen = check.Evaluate(
            new SoftDeletesFacts([new SoftDeleteFacts(orders, new SoftDeleteShareRow(orders.Schema, orders.Table, 10_000, 0, null))]), Evaluation());
        unseen.Findings.Should().BeEmpty();
        unseen.Note.Should().Contain("cannot be seen");

        check.Evaluate(new SoftDeletesFacts([new SoftDeleteFacts(orders, new SoftDeleteShareRow(orders.Schema, orders.Table, 999, 999, 0.9))]), Evaluation())
            .Findings.Should().BeEmpty();
        check.Evaluate(new SoftDeletesFacts([new SoftDeleteFacts(orders, new SoftDeleteShareRow(orders.Schema, orders.Table, 10_000, 10_000, 0.3))]), Evaluation())
            .Findings.Should().BeEmpty("30% is the line, not past it");
    }

    [Fact]
    public void A_store_with_no_soft_deleted_type_says_so()
    {
        new SoftDeletesCheck().Evaluate(new SoftDeletesFacts([]), Evaluation()).Note.Should().Contain("soft-deleted");
    }

    // -----------------------------------------------------------------------------------------------
    // T11 Numeric id headroom
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Int_ids_and_strong_typed_ids_wrapping_an_int_draw_int_ids_and_long_string_and_Guid_ids_do_not()
    {
        HiloHeadroomCheck.DrawsIntIds(Sql.SqlTestStore.DocumentType<HealthIntDoc>()).Should().BeTrue();
        HiloHeadroomCheck.DrawsIntIds(Sql.SqlTestStore.DocumentType<HealthStrongIdDoc>()).Should().BeTrue();
        HiloHeadroomCheck.DrawsIntIds(Sql.SqlTestStore.DocumentType<HealthLongDoc>()).Should().BeFalse();
        HiloHeadroomCheck.DrawsIntIds(Sql.SqlTestStore.DocumentType<HealthStringDoc>()).Should().BeFalse();
        HiloHeadroomCheck.DrawsIntIds(Sql.SqlTestStore.DocumentType<HealthOrderDoc>()).Should().BeFalse();
    }

    private static HiloHeadroomFacts Hilo(long hiValue, int maxLo = 1_000, bool exists = true) =>
        new(
            "studio_sql",
            [new HiloSequenceFacts("HealthIntDoc", maxLo, [Sql.SqlTestStore.DocumentType<HealthIntDoc>()])],
            exists,
            exists ? [new HiloRow("HealthIntDoc", hiValue)] : []);

    [Theory]
    [InlineData(1_000_000, null)]
    [InlineData(1_100_000, nameof(HealthSeverity.Warning))]
    [InlineData(1_800_000, nameof(HealthSeverity.Critical))]
    public void Headroom_is_judged_as_hi_plus_one_times_MaxLo_against_int_MaxValue(long hiValue, string? expected)
    {
        HealthSeverity? severity = expected is null ? null : Enum.Parse<HealthSeverity>(expected);
        HealthVerdict verdict = new HiloHeadroomCheck().Evaluate(Hilo(hiValue), Evaluation());

        if (severity is null)
        {
            verdict.Findings.Should().BeEmpty("(1,000,001 x 1,000) is 46.6% of int.MaxValue");
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T11");
        finding.Severity.Should().Be(severity);
        finding.Subject.Should().Be("HealthIntDoc");
        finding.DocumentTypes.Should().Equal(typeof(HealthIntDoc));
        finding.Remedy!.Code.Should().Contain("public long Id");
    }

    [Fact]
    public void An_absent_mt_hilo_is_a_value_that_says_no_HiLo_id_has_been_issued()
    {
        HealthVerdict verdict = new HiloHeadroomCheck().Evaluate(Hilo(0, exists: false), Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("mt_hilo does not exist").And.Contain("no HiLo id has been issued");
    }

    [Fact]
    public void A_store_with_no_int_id_says_so_without_reading_anything()
    {
        new HiloHeadroomCheck().Evaluate(new HiloHeadroomFacts("s", [], false, []), Evaluation()).Note.Should().Contain("int id");
    }

    // -----------------------------------------------------------------------------------------------
    // T12 Transaction-id age
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_lines_sit_on_the_way_from_the_forced_freeze_to_wraparound()
    {
        TransactionIdAgeCheck.WarningAge(200_000_000).Should().BeInRange(950_000_000, 1_000_000_000,
            "at the default autovacuum_freeze_max_age the Warning line is about a billion, as it always was");
        TransactionIdAgeCheck.CriticalAge(200_000_000).Should().BeInRange(1_450_000_000, 1_500_000_000,
            "and the Critical line about a billion and a half");

        TransactionIdAgeCheck.WarningAge(1_500_000_000).Should().BeGreaterThan(1_500_000_000,
            "a raised autovacuum_freeze_max_age moves both lines past it: autovacuum has not been asked to freeze below it");
        TransactionIdAgeCheck.CriticalAge(2_000_000_000).Should().BeLessThan(TransactionIdAgeCheck.WraparoundAge,
            "even at the setting's maximum the Critical line is before wraparound");
        TransactionIdAgeCheck.CriticalAge(2_000_000_000).Should().BeGreaterThan(TransactionIdAgeCheck.WarningAge(2_000_000_000));
    }

    [Theory]
    [InlineData(950_000_000L, 200_000_000L, null)]
    [InlineData(1_200_000_000L, 200_000_000L, nameof(HealthSeverity.Warning))]
    [InlineData(1_600_000_000L, 200_000_000L, nameof(HealthSeverity.Critical))]
    [InlineData(1_200_000_000L, 1_500_000_000L, null)]
    [InlineData(1_800_000_000L, 1_500_000_000L, nameof(HealthSeverity.Warning))]
    [InlineData(2_000_000_000L, 1_500_000_000L, nameof(HealthSeverity.Critical))]
    public void The_oldest_table_s_age_decides_one_finding_for_the_whole_database(long oldest, long freezeMaxAge, string? expected)
    {
        HealthSeverity? severity = expected is null ? null : Enum.Parse<HealthSeverity>(expected);
        MartenTable orders = Document<HealthOrderDoc>();
        MartenTable events = EventTable();

        TransactionIdAgeFacts facts = new(
        [
            Table(orders, row => row with { OldestXidAge = oldest, FreezeMaxAge = freezeMaxAge }),
            Table(events, row => row with { OldestXidAge = 500_000_000, FreezeMaxAge = freezeMaxAge }),
        ]);

        HealthVerdict verdict = new TransactionIdAgeCheck().Evaluate(facts, Evaluation());

        if (severity is null)
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().Contain(orders.Key, "the note names the oldest table");
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle("tables age together: one finding, not one per table").Subject;
        finding.Code.Should().Be("T12");
        finding.Severity.Should().Be(severity);
        finding.Subject.Should().BeNull();
        finding.Remedy.Should().BeNull("VACUUM is a DBA's statement, and a remedy is never one");
        finding.Evidence.Should().Contain(static x => x.Label == "Tables the studio judges past the warning line");
        finding.Evidence.Should().Contain(new HealthEvidence(
            "autovacuum_freeze_max_age", freezeMaxAge.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        finding.WhyItMatters.Should().Contain("which this server sets to");
    }

    [Fact]
    public void A_partitioned_table_with_no_partition_has_no_age_and_says_nothing()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        HealthVerdict verdict = new TransactionIdAgeCheck().Evaluate(
            new TransactionIdAgeFacts([Table(orders, row => row with { Kind = "p", OldestXidAge = null })]), Evaluation());

        verdict.Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_young_database_names_its_oldest_table_in_the_note()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        new TransactionIdAgeCheck().Evaluate(new TransactionIdAgeFacts([Table(orders, row => row with { OldestXidAge = 4_242 })]), Evaluation())
            .Note.Should().Contain(orders.Key).And.Contain("4,242");
    }
}
