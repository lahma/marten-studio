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

    /// <summary>The advisor's own samples of an index seen unscanned, every ten minutes for so long, up to now.</summary>
    private static HealthHistoryView SeenUnscanned(IndexFacts index, TimeSpan forSo) =>
        History([.. Enumerable.Range(0, (int) (forSo / TimeSpan.FromMinutes(10)) + 1)
            .Select(i => (TimeSpan.FromMinutes(10 * i), new[]
            {
                (UnusedIndexCheck.SweptSeries, 1d),
                (UnusedIndexCheck.UnscannedSeries + index.Key, 1d),
            }))]);

    [Fact]
    public void A_never_scanned_index_of_a_megabyte_on_a_table_with_a_hundred_thousand_writes_is_a_warning_once_a_day_of_samples_says_so()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "(data ->> 'Status'::text)") with { Scans = 0, Bytes = 2 * 1024 * 1024 });
        TableFacts table = Table(orders, row => row with { Inserts = 90_000, Updates = 20_000, HotUpdates = 0 });

        HealthFinding finding = new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation(SeenUnscanned(index, TimeSpan.FromDays(1))))
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("T05");
        finding.Severity.Should().Be(HealthSeverity.Warning, "a day of the advisor's own samples proves what counters of unknown age cannot");
        finding.Subject.Should().Be(index.Key);
        finding.Title.Should().Contain("110,000 writes");
        finding.WhyItMatters.Should().Contain("110,000 entries").And.Contain("2.0 MiB");
        finding.Caveat.Should().Contain("never", "a database whose statistics were never reset says so");
        finding.Remedy!.Code.Should().Contain("opts.Schema.For<HealthOrderDoc>()");
    }

    [Fact]
    public void Statistics_never_reset_are_of_unknown_age_and_cap_the_finding_at_Info_until_the_history_covers_a_day()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthFinding unknown = new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation()).Findings.Single();
        unknown.Severity.Should().Be(HealthSeverity.Info);
        unknown.Caveat.Should().Contain("never reset, so how long they have been counting is not known")
            .And.Contain("Info until the advisor's own samples have seen the index unscanned for a day");

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation(SeenUnscanned(index, TimeSpan.FromHours(23))))
            .Findings.Single().Severity.Should().Be(HealthSeverity.Info, "23 hours is not a day");
    }

    /// <summary>
    /// The advisor's history is samples of the primary's own <c>idx_scan</c>: however many days of zeroes it holds,
    /// it says nothing about the scans a standby served, so on a multi-host data source T05 is Info always.
    /// </summary>
    [Fact]
    public void A_multi_host_data_source_keeps_the_finding_at_Info_however_old_the_counters_and_however_long_the_history()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthFinding standby = new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddDays(-30), multiHost: true), Evaluation()).Findings.Single();
        standby.Severity.Should().Be(HealthSeverity.Info, "reads served by a standby are counted there, not on the primary");
        standby.Caveat.Should().Contain("multi-host data source").And.Contain("every standby").And.Contain("stays Info")
            .And.NotContain("until the advisor's own samples", "no length of primary-side history lifts it");

        HealthFinding afterDays = new UnusedIndexCheck()
            .Evaluate(Unused(index, table, Now.AddDays(-30), multiHost: true), Evaluation(SeenUnscanned(index, TimeSpan.FromDays(7))))
            .Findings.Single();
        afterDays.Severity.Should().Be(HealthSeverity.Info, "a week of the primary's zeroes proves nothing about a standby's scans");

        new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddDays(-30)), Evaluation(SeenUnscanned(index, TimeSpan.FromDays(7))))
            .Findings.Single().Severity.Should().Be(HealthSeverity.Warning, "the same history on one host is the proof");
    }

    [Fact]
    public void A_scan_in_between_restarts_the_day_the_history_must_cover()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthHistoryView broken = History(
            (TimeSpan.FromDays(2), [(UnusedIndexCheck.SweptSeries, 1d), (UnusedIndexCheck.UnscannedSeries + index.Key, 1d)]),
            (TimeSpan.FromHours(5), [(UnusedIndexCheck.SweptSeries, 1d)]),
            (TimeSpan.FromHours(4), [(UnusedIndexCheck.SweptSeries, 1d), (UnusedIndexCheck.UnscannedSeries + index.Key, 1d)]),
            (TimeSpan.Zero, [(UnusedIndexCheck.SweptSeries, 1d), (UnusedIndexCheck.UnscannedSeries + index.Key, 1d)]));

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation(broken)).Findings.Single()
            .Severity.Should().Be(HealthSeverity.Info, "five hours ago the index was not seen unscanned, so the day starts four hours ago");
    }

    [Fact]
    public void The_sample_marks_every_sweep_and_every_unscanned_candidate()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts unscanned = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        IndexFacts scanned = Index(orders, IndexRow(orders, "by_name", "id") with { Scans = 3 });
        UnusedIndexFacts facts = new(Now, [unscanned, scanned], [Table(orders)], new DatabaseStatistics(null, true, 2));

        new UnusedIndexCheck().Sample(facts).Should().BeEquivalentTo(
        [
            new HealthSamplePart(UnusedIndexCheck.SweptSeries, 1),
            new HealthSamplePart(UnusedIndexCheck.UnscannedSeries + unscanned.Key, 1),
        ]);
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

        new UnusedIndexCheck().Evaluate(Unused(index, table, null), Evaluation()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_quiet_table_or_a_small_index_is_not_worth_a_finding()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts small = Index(orders, IndexRow(orders, "small", "id") with { Scans = 0, Bytes = 8192 });
        IndexFacts large = Index(orders, IndexRow(orders, "large", "id") with { Scans = 0 });

        new UnusedIndexCheck().Evaluate(Unused(small, Table(orders, row => row with { Inserts = 1_000_000 }), null), Evaluation())
            .Findings.Should().BeEmpty();
        new UnusedIndexCheck().Evaluate(Unused(large, Table(orders, row => row with { Inserts = 99_999 }), null), Evaluation())
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void Statistics_reset_less_than_a_day_ago_make_the_finding_Info_and_say_why_and_older_ones_a_Warning()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "by_status", "id") with { Scans = 0 });
        TableFacts table = Table(orders, row => row with { Inserts = 1_000_000 });

        HealthFinding young = new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddHours(-3)), Evaluation()).Findings.Single();
        young.Severity.Should().Be(HealthSeverity.Info);
        young.Caveat.Should().Contain("The statistics were reset 3 h ago").And.Contain("Info until");

        new UnusedIndexCheck().Evaluate(Unused(index, table, Now.AddDays(-3)), Evaluation()).Findings.Single()
            .Severity.Should().Be(HealthSeverity.Warning);
    }

    [Fact]
    public void Marten_s_structural_index_is_never_reported_unused()
    {
        MartenTable orders = Document<HealthOrderDoc>();
        IndexFacts index = Index(orders, IndexRow(orders, "mt_doc_healthorderdoc_idx_mt_deleted", "mt_deleted") with { Scans = 0 }, IndexOrigin.Structural);

        new UnusedIndexCheck().Evaluate(Unused(index, Table(orders, row => row with { Inserts = 1_000_000 }), null), Evaluation())
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
