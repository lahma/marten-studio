using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>R02 at every edge: when archiving is judged at all, the <c>failing</c> triggers, and the <c>backlog</c> thresholds.</summary>
public class ArchiverFailingCheckTests
{
    private static readonly ArchiverFailingCheck Check = new();
    private static readonly HealthCheckInfo Info = HealthCheckRegistry.Describe(typeof(ArchiverFailingCheck));

    private static ArchiverFailingFacts Facts(ArchiverFacts archiver, WalDirectoryFacts? directory = null) =>
        new(archiver, directory, Grants(), ServerOf());

    private static HealthVerdict Evaluate(ArchiverFacts archiver, HealthHistoryView? history = null, WalDirectoryFacts? directory = null)
    {
        HealthVerdict verdict = Check.Evaluate(Facts(archiver, directory), At(history));

        foreach (HealthFinding finding in verdict.Findings)
        {
            HealthCheckRegistry.FindingProblems(Info, finding).Should().BeEmpty("every variant R02 makes is one it lists");
        }

        return verdict;
    }

    [Fact]
    public void R02_lists_its_two_variants_at_the_database_gate()
    {
        Info.Code.Should().Be("R02");
        Info.Gate.Should().Be(HealthGate.Database);
        Info.Tier.Should().Be(HealthCostTier.Catalog);
        Info.Variants.Should().Equal("failing", "backlog");
    }

    [Theory]
    [InlineData("off", false, "archive_mode is off")]
    [InlineData("on", true, "only archive_mode = always archives")]
    public void A_server_that_does_not_archive_is_quiet(string mode, bool inRecovery, string note)
    {
        HealthVerdict verdict = Evaluate(Archiver(failed: 5, lastArchived: null, lastFailedAt: Now, inRecovery: inRecovery, settings: Archiving(mode)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(note);
    }

    [Fact]
    public void Always_on_a_standby_is_judged_for_failures()
    {
        HealthVerdict verdict = Evaluate(Archiver(lastArchived: null, failed: 3, lastFailed: Wal(5), lastFailedAt: Now.AddMinutes(-1), inRecovery: true, settings: Archiving("always")));

        verdict.Findings.Should().ContainSingle().Which.Variant.Should().Be("failing");
    }

    [Fact]
    public void Failures_and_no_success_at_all_are_failing_and_critical()
    {
        HealthFinding finding = Evaluate(Archiver(lastArchived: null, failed: 17, lastFailed: Wal(1), lastFailedAt: Now.AddSeconds(-20))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("failing");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("WAL archiving is failing");
        finding.Subject.Should().BeNull("one finding per variant; the subject names an object, and there is none but the server");
        finding.Remedy.Should().BeNull("the command is the host's own");
        finding.WhyItMatters.Should().Contain("WAL accumulates in pg_wal until it succeeds").And.Contain("server log");
        finding.Evidence.Should().Contain(new HealthEvidence("Last failed WAL file", Wal(1)));
        finding.Evidence.Should().Contain(new HealthEvidence("Last successful archive", "none since the archiver's statistics began"));
        finding.Evidence.Single(static x => x.Label == "Failed attempts").Value.Should().StartWith("17 since");
    }

    [Fact]
    public void A_failure_after_the_last_success_is_failing()
    {
        HealthVerdict verdict = Evaluate(Archiver(lastArchivedAt: Now.AddMinutes(-5), lastArchivedAge: TimeSpan.FromMinutes(5), failed: 1, lastFailed: Wal(17), lastFailedAt: Now.AddMinutes(-1)));

        verdict.Findings.Should().ContainSingle().Which.Title.Should().Be("WAL archiving is failing");
    }

    [Fact]
    public void Old_failures_before_the_last_success_with_none_new_are_quiet()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-10), [(ReplicationHistory.ArchiverFailed, 4)]),
            (Now, [(ReplicationHistory.ArchiverFailed, 4)]));

        HealthVerdict verdict = Evaluate(Archiver(failed: 4, lastFailed: Wal(2), lastFailedAt: Now.AddDays(-2)), history);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("4 failures, none of them new");
    }

    [Fact]
    public void Failures_that_rose_in_the_window_are_failing_even_after_a_later_success()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-10), [(ReplicationHistory.ArchiverFailed, 3)]),
            (Now, [(ReplicationHistory.ArchiverFailed, 5)]));

        HealthFinding finding = Evaluate(Archiver(failed: 5, lastFailed: Wal(15), lastFailedAt: Now.AddMinutes(-3)), history).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Title.Should().Be("WAL archiving failed 2 times in the last 10 min");
        finding.Caveat.Should().Contain("may have recovered");
    }

    [Fact]
    public void A_counter_that_went_down_was_reset_and_counts_from_the_reset()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-10), [(ReplicationHistory.ArchiverFailed, 40)]),
            (Now.AddMinutes(-5), [(ReplicationHistory.ArchiverFailed, 1)]),
            (Now, [(ReplicationHistory.ArchiverFailed, 1)]));

        Evaluate(Archiver(failed: 1, lastFailed: Wal(2), lastFailedAt: Now.AddDays(-1)), history).Findings.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // backlog: 64 segments and 1 GiB, grown across ten minutes; Critical at 10 GiB or an hour without a success
    // -------------------------------------------------------------------------------------------

    private static HealthHistoryView Backlog(params (double MinutesAgo, double Segments)[] points) =>
        History([.. points.Select(static x => (Now.AddMinutes(-x.MinutesAgo), new[] { (ReplicationHistory.ArchiverBacklog, x.Segments) }))]);

    private static ArchiverFacts Behind(long segments, TimeSpan? lastSuccess = null) =>
        Archiver(lastArchived: Wal(100), current: Wal(100 + (ulong) segments), lastArchivedAge: lastSuccess ?? TimeSpan.FromSeconds(30));

    [Fact]
    public void Sixty_four_segments_of_16_MiB_grown_across_ten_minutes_is_a_backlog_warning()
    {
        HealthFinding finding = Evaluate(Behind(64), Backlog((10, 50), (0, 64))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("backlog");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("WAL archiving is 1.0 GiB behind, and falling further behind");
        finding.Evidence.Should().Contain(new HealthEvidence("WAL file being written", Wal(164)));
        finding.Evidence.Single(static x => x.Label == "Failed attempts").Value.Should().Contain("never counts as one");
    }

    [Fact]
    public void Sixty_three_segments_is_not_a_backlog_yet()
    {
        Evaluate(Behind(63), Backlog((10, 50), (0, 63))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Sixty_four_segments_under_a_GiB_is_not_a_backlog()
    {
        const long Segment1 = 1024 * 1024;
        ArchiverFacts small = Archiver(
            lastArchived: Wal(100, segmentBytes: Segment1),
            current: Wal(200, segmentBytes: Segment1),
            settings: ReplicationTestFacts.Settings(("archive_mode", "on", null), ("archive_command", "cp", null), ("wal_segment_size", "1048576", "B")));

        Evaluate(small, Backlog((15, 10), (0, 100))).Findings.Should().BeEmpty("100 segments of 1 MiB are not a gibibyte");
    }

    [Fact]
    public void Not_grown_across_ten_minutes_of_samples_is_watching()
    {
        HealthVerdict verdict = Evaluate(Behind(70), Backlog((9, 50), (0, 70)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("once that has grown across 10 min");
    }

    [Fact]
    public void A_backlog_that_drained_in_between_has_not_grown()
    {
        Evaluate(Behind(70), Backlog((10, 50), (5, 40), (0, 70))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Ten_GiB_behind_is_critical()
    {
        Evaluate(Behind(640), Backlog((10, 600), (0, 640))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
        Evaluate(Behind(639), Backlog((10, 600), (0, 639))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    [Fact]
    public void No_success_for_an_hour_while_it_grows_is_critical()
    {
        Evaluate(Behind(70, TimeSpan.FromHours(1)), Backlog((10, 50), (0, 70))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
        Evaluate(Behind(70, TimeSpan.FromMinutes(59)), Backlog((10, 50), (0, 70))).Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    /// <summary>The segment being written and the archived count, sampled (W5-R2-fix B2: never one series with the distance).</summary>
    private static HealthHistoryView Written(params (double MinutesAgo, double Segment, double Archived)[] points) =>
        History([.. points.Select(static x => (Now.AddMinutes(-x.MinutesAgo), new[] { (ReplicationHistory.WalSegment, x.Segment), (ReplicationHistory.ArchiverArchived, x.Archived) }))]);

    [Fact]
    public void Nothing_archived_yet_is_judged_by_what_was_written_while_nothing_was_archived()
    {
        // The segment being written gained 75 in the window and the archiver archived nothing: a lower bound of 75 segments.
        ArchiverFacts never = Archiver(lastArchived: null, archived: 0, current: Wal(80));

        HealthFinding finding = Evaluate(never, Written((15, 5, 0), (0, 80, 0))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("backlog");
        finding.Severity.Should().Be(HealthSeverity.Warning, "the whole WAL sequence never sizes an alarm, and it has not grown for an hour");
        finding.Title.Should().StartWith("Nothing has been archived while 1.2 GiB of WAL was written");
        finding.Caveat.Should().Contain("a lower bound");

        Evaluate(Archiver(lastArchived: null, archived: 0, current: Wal(5000)), Written((15, 4990, 0), (0, 5000, 0))).Findings.Should().BeEmpty(
            "ten segments written while watching are not a backlog, whatever number the current segment has");

        Evaluate(never, Written((61, 5, 0), (30, 40, 0), (0, 80, 0))).Findings.Should().ContainSingle().Which.Severity.Should().Be(
            HealthSeverity.Critical, "grown across an hour of samples with nothing archived");
    }

    [Fact]
    public void Nothing_archived_is_never_judged_while_the_archiver_archived_something_in_the_window()
    {
        // A history file archived at a promotion: nothing names a segment, but the archiver works (archived_count rose).
        ArchiverFacts promoted = Archiver(lastArchived: "00000002.history", archived: 121, current: Wal(200));

        HealthVerdict verdict = Evaluate(promoted, Written((15, 100, 120), (0, 200, 121)));

        verdict.Findings.Should().BeEmpty("an archived file is a working archiver, whatever it named");
    }

    [Fact]
    public void Distance_samples_followed_by_a_reset_or_a_history_file_archived_last_stay_quiet()
    {
        // B2: distance samples, then a read with no last segment. Before the fix one series held both the distance and the
        // current segment's number, so this read looked like a backlog jumping to the whole WAL sequence.
        HealthHistoryView history = History(
            (Now.AddMinutes(-15), [(ReplicationHistory.ArchiverBacklog, 2), (ReplicationHistory.WalSegment, 150), (ReplicationHistory.ArchiverArchived, 148)]),
            (Now.AddMinutes(-5), [(ReplicationHistory.ArchiverBacklog, 2), (ReplicationHistory.WalSegment, 190), (ReplicationHistory.ArchiverArchived, 188)]));

        // pg_stat_reset_shared('archiver'), or a crash: no last archived file, and archived_count back at zero.
        Evaluate(Archiver(lastArchived: null, archived: 0, current: Wal(200)), history).Findings.Should().BeEmpty();

        // A promotion: the history file archived last, and the counter still counting.
        Evaluate(Archiver(lastArchived: "00000002.history", archived: 189, current: Wal(200)), history).Findings.Should().BeEmpty();

        // A .partial segment archived last is measured from: two segments behind is nothing.
        Evaluate(Archiver(lastArchived: Wal(198) + ".partial", archived: 189, current: Wal(200)), history).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_failing_archiver_is_said_once_as_failing_with_its_backlog_in_the_evidence()
    {
        ArchiverFacts both = Archiver(lastArchived: Wal(100), current: Wal(300), failed: 9, lastFailed: Wal(101), lastFailedAt: Now.AddMinutes(-1), lastArchivedAt: Now.AddHours(-2));

        HealthFinding finding = Evaluate(both, Backlog((20, 150), (0, 200))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("failing");
        finding.Evidence.Single(static x => x.Label == "Archiver behind").Value.Should().StartWith("200 segments");
    }

    [Fact]
    public void The_commands_text_is_activity_evidence_and_its_state_is_the_databases()
    {
        HealthFinding finding = Evaluate(Archiver(lastArchived: null, failed: 1, lastFailedAt: Now)).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "archive_command" && x.Value.StartsWith("set - its text is shown with ", StringComparison.Ordinal) && x.Gate == HealthGate.None);
        finding.Evidence.Should().Contain(new HealthEvidence("archive_command's text", "/usr/local/bin/archive %p", HealthGate.Activity));
    }

    [Fact]
    public void An_empty_command_and_a_library_are_said_as_such()
    {
        HealthFinding empty = Evaluate(Behind(64) with { Settings = Archiving(command: string.Empty) }, Backlog((10, 50), (0, 64))).Findings.Single();
        empty.Evidence.Should().Contain(new HealthEvidence("archive_command", "empty: Postgres keeps every WAL segment and archives none"));
        empty.Evidence.Should().NotContain(static x => x.Label == "archive_command's text");

        HealthFinding library = Evaluate(Behind(64) with { Settings = Archiving(library: "basic_archive") }, Backlog((10, 50), (0, 64))).Findings.Single();
        library.Evidence.Should().Contain(new HealthEvidence("archive_library", "basic_archive (archive_command is not used while it is set)"));
    }

    [Fact]
    public void Without_pg_monitor_the_directory_is_not_checked_and_names_the_grant()
    {
        HealthFinding without = Evaluate(Archiver(lastArchived: null, failed: 1, lastFailedAt: Now)).Findings.Single();
        without.Evidence.Single(static x => x.Label == "Waiting in pg_wal").Value
            .Should().Be(PostgresGrants.NotChecked(PostgresGrant.Monitor, PostgresPlatform.SelfHosted, "app"));

        HealthFinding with = Evaluate(Archiver(lastArchived: null, failed: 1, lastFailedAt: Now), directory: new WalDirectoryFacts(42, 60, 60 * Segment16)).Findings.Single();
        with.Evidence.Single(static x => x.Label == "Waiting in pg_wal").Value.Should().Be("42 files ready for the archiver; pg_wal holds 60 files, 960 MiB");
    }

    [Fact]
    public void It_samples_the_counters_the_segment_and_a_distance_only_when_there_is_one()
    {
        Check.Sample(Facts(Behind(10))).Select(static x => (x.Series, x.Value)).Should().Equal(
            (ReplicationHistory.ArchiverFailed, 0d), (ReplicationHistory.ArchiverArchived, 10d),
            (ReplicationHistory.WalSegment, 110d), (ReplicationHistory.ArchiverBacklog, 10d));

        Check.Sample(Facts(Archiver(lastArchived: null, archived: 0, current: Wal(80)))).Select(static x => x.Series).Should().Equal(
            [ReplicationHistory.ArchiverFailed, ReplicationHistory.ArchiverArchived, ReplicationHistory.WalSegment],
            "with nothing archived there is no distance, and none is made up");

        Check.Sample(Facts(Behind(10) with { Settings = Archiving("off") })).Should().BeEmpty();
    }

    [Fact]
    public void A_pg_wal_that_could_not_be_read_is_said_in_the_evidence_and_R02_still_judges()
    {
        ArchiverFailingFacts facts = new(Archiver(lastArchived: null, failed: 1, lastFailedAt: Now), null, Grants("pg_monitor"), ServerOf(),
            ArchiverFailingCheck.DirectoryProblemText(new Npgsql.PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014")));

        HealthFinding finding = Check.Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("failing");
        finding.Evidence.Single(static x => x.Label == "Waiting in pg_wal").Value
            .Should().Be("could not be read (SQLSTATE 57014); R02 judges the archiver's own counters regardless");
    }
}
