using static MartenStudio.Tests.Health.TableCheckBuilders;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>T03, T04 and T05 over stated facts.</summary>
public class IndexChecksTests
{
    // -----------------------------------------------------------------------------------------------
    // T03 Invalid index
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void An_invalid_declared_index_is_critical_and_the_remedy_is_a_reapply_that_says_it_is_not_additive()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "mt_doc_healthorderdoc_idx_status", "(data ->> 'Status'::text)") with { IsValid = false });

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([index], [Table(orders)]), Evaluation());

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T03");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be(orders.Key + ".mt_doc_healthorderdoc_idx_status", "the Indexes tab finds its row by this key");
        finding.WhyItMatters.Should().Contain("declares it, so a re-apply drops it and builds it again");
        finding.Remedy!.Code.Should().Contain("ApplyAllConfiguredChangesToDatabaseAsync()");
        finding.Remedy.Explanation.Should().Contain("not additive");
        finding.Evidence.Select(static x => x.Value).Should().NotContain(static x => x.Contains("CREATE INDEX", StringComparison.Ordinal),
            "a deparsed definition can name a schema the visitor may not see, and evidence is not masked");
    }

    [Fact]
    public void An_invalid_index_nobody_declares_is_one_the_next_apply_drops_and_an_ignored_one_has_no_remedy_line()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts handMade = Index(orders, IndexRow(orders, "hand_made", "(data ->> 'Status'::text)") with { IsValid = false }, IndexOrigin.Undeclared);
        IndexFacts ignored = Index(orders, IndexRow(orders, "ignored", "(data ->> 'Status'::text)") with { IsValid = false }, IndexOrigin.Ignored);

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([handMade, ignored], [Table(orders)]), Evaluation());

        verdict.Findings.Single(static x => x.Subject!.EndsWith(".hand_made", StringComparison.Ordinal)).WhyItMatters
            .Should().Contain("next apply drops it");
        HealthFinding ignoredFinding = verdict.Findings.Single(static x => x.Subject!.EndsWith(".ignored", StringComparison.Ordinal));
        ignoredFinding.Remedy.Should().BeNull("an apply skips an index the configuration ignores, so re-applying does not repair it");
        ignoredFinding.WhyItMatters.Should().Contain("IgnoreIndex");
    }

    [Fact]
    public void Invalid_indexes_on_partitions_are_counted_against_their_table_and_never_named()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with { Kind = "p", InvalidPartitionIndexes = 2 });

        HealthFinding finding = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([], [table]), Evaluation()).Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be(orders.Key + "#partitions", "a subject distinct from every index's");
        finding.Title.Should().StartWith("2 indexes on the partitions of " + orders.Key);
        finding.WhyItMatters.Should().Contain("after the tenant");
        finding.WhyItMatters.Should().Contain("A DBA drops that leftover partition index first", "a re-apply cannot resume a partition's build");
        finding.Remedy.Should().BeNull();
        finding.Severity.Should().Be(HealthSeverity.Critical);
    }

    /// <summary>
    /// Every concurrent build looks exactly like a failed one while it runs - and the re-apply remedy then queues a
    /// DROP INDEX for an AccessExclusiveLock behind it. A build in progress is no finding, only a note.
    /// </summary>
    [Fact]
    public void An_invalid_index_a_build_is_still_working_on_is_no_finding_only_a_note()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts building = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)") with
        {
            IsValid = false,
            BuildPhase = "building index: scanning table",
        });
        IndexFacts ccnew = Index(orders, IndexRow(orders, "by_status_ccnew", "(data ->> 'Status'::text)") with
        {
            IsValid = false,
            BuildPhase = "waiting for old snapshots",
        }, IndexOrigin.Undeclared);

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([building, ccnew], [Table(orders)]), Evaluation());

        verdict.Findings.Should().BeEmpty("re-applying mid-build would queue a DROP INDEX behind the build");
        verdict.Note.Should().Contain("Being built right now")
            .And.Contain("by_status (building index: scanning table)")
            .And.Contain("by_status_ccnew (waiting for old snapshots)");
    }

    [Fact]
    public void Invalid_partition_indexes_a_build_is_still_working_on_are_no_finding_either()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            InvalidPartitionIndexes = 0,
            BuildingPartitionIndexes = 3,
            PartitionBuildPhase = "building index: loading tuples in tree",
            TreeBuildPhase = "building index: loading tuples in tree",
        });

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([], [table]), Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("3 indexes on the partitions of " + orders.Key + " (building index: loading tuples in tree)");
    }

    /// <summary>
    /// The review's probe, as facts: an older failed index stays invalid while a <c>REINDEX CONCURRENTLY</c> of
    /// another index on the same table runs. That build is not this index's, so it is still a finding - and its
    /// advice is to let the build finish first, because the re-apply's DROP INDEX would queue behind it. The
    /// reindex's own <c>_ccnew</c> copy is the build's, and only a note.
    /// </summary>
    [Fact]
    public void An_older_failed_index_stays_a_finding_while_a_build_of_another_index_runs_and_says_to_wait_for_it()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts oldFailed = Index(orders, IndexRow(orders, "old_failed", "(data ->> 'Status'::text)") with
        {
            IsValid = false,
            TableBuildPhase = "waiting for old snapshots",
        });
        IndexFacts copy = Index(orders, IndexRow(orders, "by_y_ccnew", "(data ->> 'Rank'::text)") with
        {
            IsValid = false,
            BuildPhase = "waiting for old snapshots",
            TableBuildPhase = "waiting for old snapshots",
        }, IndexOrigin.Undeclared);

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([oldFailed, copy], [Table(orders)]), Evaluation());

        HealthFinding finding = verdict.Findings.Should().ContainSingle("only the index no build is working on is judged").Subject;
        finding.Subject.Should().EndWith(".old_failed");
        finding.Severity.Should().Be(HealthSeverity.Critical, "every build is in view, and none is this one's");
        finding.WhyItMatters.Should().Contain("the index build running on the table is another index's");
        finding.Caveat.Should().Contain("A build of another index on this table is running right now (waiting for old snapshots)")
            .And.Contain("let it finish before re-applying");
        verdict.Note.Should().Contain("by_y_ccnew (waiting for old snapshots)", "the note still says what is being built beside the finding");
    }

    [Fact]
    public void Invalid_partition_indexes_no_build_is_working_on_are_found_while_another_partition_is_being_built()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        TableFacts table = Table(orders, row => row with
        {
            Kind = "p",
            InvalidPartitionIndexes = 1,
            BuildingPartitionIndexes = 1,
            PartitionBuildPhase = "waiting for old snapshots",
            TreeBuildPhase = "waiting for old snapshots",
        });

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([], [table]), Evaluation());

        HealthFinding finding = verdict.Findings.Should().ContainSingle("a nightly rolling REINDEX must not hide an older failure").Subject;
        finding.Title.Should().StartWith("1 index on the partitions of " + orders.Key);
        finding.WhyItMatters.Should().Contain("the build running on the table now is another index's");
        finding.Caveat.Should().Contain("let it finish before re-applying");
        verdict.Note.Should().Contain("an index on the partitions of " + orders.Key);
    }

    [Fact]
    public void Builds_this_role_cannot_see_lower_the_finding_to_a_Warning_that_says_why()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)") with { IsValid = false, UnseenIndexBuilds = 2 });
        TableFacts partitioned = Table(Document<HealthStringDoc>(), row => row with { Kind = "p", InvalidPartitionIndexes = 1, UnseenIndexBuilds = 2 });

        HealthVerdict verdict = new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([index], [Table(orders), partitioned]), Evaluation());

        verdict.Findings.Should().HaveCount(2).And.OnlyContain(static x => x.Severity == HealthSeverity.Warning,
            "a build another role runs is invisible without pg_read_all_stats, and this may be it");
        verdict.Findings.Should().OnlyContain(static x => x.Caveat!.Contains("pg_read_all_stats", StringComparison.Ordinal)
            && x.Caveat.Contains("2 index builds", StringComparison.Ordinal));

        // D: the why must not contradict the caveat - it may not say no build is running when one may be this one.
        verdict.Findings.Should().OnlyContain(static x => !x.WhyItMatters.Contains("no index build is running", StringComparison.Ordinal)
            && !x.WhyItMatters.Contains("no build is running", StringComparison.Ordinal));
        verdict.Findings.Should().OnlyContain(static x => x.WhyItMatters.Contains("most likely", StringComparison.Ordinal)
            && x.WhyItMatters.Contains("this role can see", StringComparison.Ordinal));
    }

    [Fact]
    public void A_partitioned_table_s_own_invalid_index_says_in_words_what_a_DBA_does_and_offers_no_reapply()
    {
        MartenTable tenants = Document<HealthStringDoc>();
        IndexFacts index = Index(tenants, IndexRow(tenants, "by_name", "(data ->> 'Name'::text)") with { IsValid = false });

        HealthFinding finding = new InvalidIndexCheck().Evaluate(
                new InvalidIndexFacts([index], [Table(tenants, row => row with { Kind = "p" })]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Remedy.Should().BeNull("Weasel reads only the parent's indexes, and the deterministic child name fails with 42P07");
        finding.WhyItMatters.Should().Contain("A DBA drops that leftover partition index first")
            .And.Contain("is not named here")
            .And.NotContain("partitions and all");
    }

    [Fact]
    public void Valid_indexes_say_nothing()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([Index(orders, IndexRow(orders, "fine", "id"))], [Table(orders)]), Evaluation())
            .Findings.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // T04 Redundant index
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_declared_index_that_is_a_prefix_of_another_is_redundant_and_the_remedy_names_its_type()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts single = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)"));
        IndexFacts pair = Index(orders, IndexRow(orders, "by_status_and_id", "(data ->> 'Status'::text)", "id"));

        HealthFinding finding = new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([single, pair]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T04");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be(single.Key);
        finding.Evidence.Should().Contain(new HealthEvidence("Covered by", "by_status_and_id"));
        finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthOrderDoc>()").And.Contain("by_status");
    }

    [Fact]
    public void Marten_s_own_structural_and_event_store_indexes_are_never_reported_redundant()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts structural = Index(orders, IndexRow(orders, "mt_doc_healthorderdoc_idx_mt_deleted", "mt_deleted"), IndexOrigin.Structural);
        IndexFacts declared = Index(orders, IndexRow(orders, "by_deleted_and_status", "mt_deleted", "(data ->> 'Status'::text)"));

        new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([structural, declared]), Evaluation())
            .Findings.Should().BeEmpty("no line of the host's removes the index Marten adds for soft deletes");
    }

    [Fact]
    public void Of_two_exact_duplicates_the_hand_made_one_is_reported_and_the_declaration_kept()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts declared = Index(orders, IndexRow(orders, "a_declared", "(data ->> 'Status'::text)"));
        IndexFacts handMade = Index(orders, IndexRow(orders, "z_hand_made", "(data ->> 'Status'::text)"), IndexOrigin.Undeclared);

        HealthFinding finding = new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([declared, handMade]), Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().EndWith(".z_hand_made");
        finding.WhyItMatters.Should().Contain("next apply drops it anyway");
        finding.Remedy.Should().BeNull("there is nothing in the configuration to remove");
    }

    // -----------------------------------------------------------------------------------------------
    // T05 Unused index on a write-heavy table
    // -----------------------------------------------------------------------------------------------

    private static UnusedIndexFacts Unused(IndexFacts index, TableFacts table, DateTimeOffset? statsReset, bool multiHost = false) =>
        new(Now, [index], [table], new DatabaseStatistics(statsReset, true, 2), multiHost);

    [Fact]
    public void A_never_scanned_index_of_a_megabyte_on_a_table_with_a_hundred_thousand_writes_is_a_warning_over_a_covering_window()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)") with { Scans = 0, Bytes = 2 * 1024 * 1024 });
        TableFacts table = Table(orders, row => row with { Inserts = 90_000, Updates = 20_000, HotUpdates = 0 });

        HealthFinding finding = new UnusedIndexCheck().Evaluate(Unused(index, table, null), UsageEvaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T05");
        finding.Severity.Should().Be(HealthSeverity.Warning, "thirty days of statistics, not looking reset, is the proof (issue #8 G4)");
        finding.Subject.Should().Be(index.Key);
        finding.Title.Should().Contain("110,000 writes").And.Contain("has never been scanned");
        finding.WhyItMatters.Should().Contain("110,000 entries").And.Contain("2.0 MiB").And.Contain("30 days of statistics");
        finding.Caveat.Should().Contain("Judged over 30 days of statistics").And.Contain("pg_stat_checkpointer.stats_reset");
        finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthOrderDoc>()");
    }

    /// <summary>
    /// Issue #8 G4 folds 0.4's "Info until a day of the advisor's own samples" into the guard: under seven days, or over
    /// counters that look reset, there is no finding at all - the status row says why - and from seven days a Warning.
    /// </summary>
    [Fact]
    public void Under_seven_days_of_statistics_or_over_counters_that_look_reset_there_is_no_finding_and_the_row_says_why()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthVerdict young = new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation() with { Statistics = StatisticsOver(TimeSpan.FromDays(6)) });
        young.Findings.Should().BeEmpty();
        young.Note.Should().StartWith("Withheld: statistics 6 days old").And.Contain("pg_stat_checkpointer.stats_reset").And.Contain("7 days");

        HealthVerdict reset = new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation() with { Statistics = StatisticsOver(TimeSpan.FromDays(30), looksReset: true) });
        reset.Findings.Should().BeEmpty();
        reset.Note.Should().StartWith("Withheld: the counters look reset (the server restarted without its statistics)");

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation() with { Statistics = StatisticsOver(TimeSpan.FromDays(7)) })
            .Findings.Single().Severity.Should().Be(HealthSeverity.Warning, "seven days is the line");

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation()).Note.Should().Be(UsageGuard.NotRead,
            "a window nobody measured is never one a verdict is given over");
    }

    /// <summary>
    /// The primary's counters say nothing about the scans a standby served, so on a multi-host data source T05 is Info always,
    /// however long the window.
    /// </summary>
    [Fact]
    public void A_multi_host_data_source_keeps_the_finding_at_Info_however_long_the_window()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthFinding standby = new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddDays(-30), multiHost: true), UsageEvaluation()).Findings.Single();
        standby.Severity.Should().Be(HealthSeverity.Info, "reads served by a standby are counted there, not on the primary");
        standby.Caveat.Should().Contain("multi-host data source").And.Contain("every standby").And.Contain("stays Info");

        new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddDays(-30)), UsageEvaluation())
            .Findings.Single().Severity.Should().Be(HealthSeverity.Warning, "the same window on one host is the proof");
    }

    [Fact]
    public void T05_writes_no_history_of_its_own_any_more()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts unscanned = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        UnusedIndexFacts facts = new(Now, [unscanned], [Table(orders)], new DatabaseStatistics(null, true, 2));

        new UnusedIndexCheck().Sample(facts).Should().BeEmpty("the statistics window is the proof now, not the advisor's own samples");
    }

    [Theory]
    [InlineData("Host=db;Database=app", false)]
    [InlineData("Host=primary,standby;Database=app", true)]
    [InlineData("Host=primary:5432,standby:5433;Target Session Attributes=prefer-standby", true)]
    [InlineData("", false)]
    public void A_connection_string_naming_more_than_one_host_is_multi_host(string connectionString, bool multiHost)
    {
        UnusedIndexCheck.IsMultiHost(connectionString).Should().Be(multiHost);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void A_unique_a_primary_key_or_a_scanned_index_is_never_unused(bool unique, bool primaryKey, bool scanned)
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "x", "id") with
        {
            IsUnique = unique || primaryKey,
            IsPrimaryKey = primaryKey,
            Scans = scanned ? 1 : 0,
        });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), UsageEvaluation()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_quiet_table_or_a_small_index_is_not_worth_a_finding()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts small = Index(orders, IndexRow(orders, "small", "id") with { Scans = 0, Bytes = 8192 });
        IndexFacts large = Index(orders, IndexRow(orders, "large", "id") with { Scans = 0 });

        new UnusedIndexCheck().Evaluate(Unused(small, Table(orders, row => row with { Inserts = 1_000_000 }), null), UsageEvaluation())
            .Findings.Should().BeEmpty();
        new UnusedIndexCheck().Evaluate(Unused(large, Table(orders, row => row with { Inserts = 99_999 }), null), UsageEvaluation())
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void Marten_s_structural_index_is_never_reported_unused()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "mt_doc_healthorderdoc_idx_mt_deleted", "mt_deleted") with { Scans = 0 }, IndexOrigin.Structural);

        new UnusedIndexCheck().Evaluate(Unused(index, Table(orders, row => row with { Inserts = 1_000_000 }), null), UsageEvaluation())
            .Findings.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Wording, all three
    // -----------------------------------------------------------------------------------------------

    /// <summary>Hard rule 14, the wording half: an apply is never called additive.</summary>
    [Fact]
    public void Nothing_the_index_checks_say_calls_an_apply_additive()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts invalid = Index(orders, IndexRow(orders, "broken", "id") with { IsValid = false });
        IndexFacts single = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)"), IndexOrigin.Undeclared);
        IndexFacts pair = Index(orders, IndexRow(orders, "by_status_and_id", "(data ->> 'Status'::text)", "id"));

        IEnumerable<HealthFinding> findings =
        [
            .. new InvalidIndexCheck().Evaluate(new InvalidIndexFacts([invalid], [Table(orders)]), Evaluation()).Findings,
            .. new RedundantIndexCheck().Evaluate(new RedundantIndexFacts([single, pair]), Evaluation()).Findings,
        ];

        foreach (HealthFinding finding in findings)
        {
            AllText(finding).Replace("not additive", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Should().NotContainEquivalentOf("additive", "{0} may say only that an apply is not additive", finding.Code);
        }
    }
}
