using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Replication;
using PostgresStudio.Tests.Server;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The diagnostics API's sections from the pages' facts (API design §6.2): every field from its fact, nothing read twice, and the
/// host gate applied where the page's visitor gate is.
/// </summary>
public class DiagnosticsSnapshotBuilderTests
{
    [Fact]
    public void The_storage_section_is_the_storage_pages_facts_with_every_table_named()
    {
        PostgresStorageSection storage = DiagnosticsSnapshotBuilder.Storage(StorageOf(), PostgresStudioSettings.Of(new PostgresStudioOptions()));

        storage.Outcome.Should().Be(PostgresReadOutcome.Read);
        storage.DatabaseBytes.Should().Be(51_234_567_890);
        storage.SizesAreEstimates.Should().BeTrue();
        storage.StatisticsSince.Should().Be(At.AddDays(-30));
        storage.GrowthBytesPerDay.Should().Be(812_345_678);
        storage.Reason.Should().BeNull();

        PostgresTableStorage orders = storage.LargestTables[0];
        orders.Should().BeEquivalentTo(new
        {
            Schema = "public",
            Name = "orders",
            Ownership = PostgresTableOwnership.Application,
            TotalBytes = 26_000_000_000L,
            TableBytes = 20_000_000_000L,
            IndexBytes = 5_000_000_000L,
            ToastBytes = 1_000_000_000L,
            EstimatedRows = 300_000_000L,
            HotUpdateRatio = (double?) 0.25,
            AllVisibleRatio = (double?) 0.75,
        });
        orders.ShareOfDatabase.Should().BeApproximately(26_000_000_000d / 51_234_567_890, 1e-9);

        storage.LargestTables[1].Should().BeEquivalentTo(
            new { Schema = "quartz", Name = "qrtz_triggers", Ownership = PostgresTableOwnership.Library, Owner = "Quartz.NET" },
            "a schema the database browser withholds from a visitor is the host's role's to read (API design §2.3)");
        storage.LargestTables[1].AllVisibleRatio.Should().BeNull("the catalog counted no pages for it (relpages = 0)");
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.2, 1.0)]
    [InlineData(-0.1, 0.0)]
    public void The_all_visible_ratio_is_held_to_zero_to_one(double read, double expected) =>
        DiagnosticsMapper.Ratio(read).Should().Be(expected);

    [Fact]
    public void The_all_visible_ratio_of_a_table_the_catalog_has_not_counted_is_null_never_nought_or_not_a_number()
    {
        DiagnosticsMapper.Ratio(null).Should().BeNull();
        DiagnosticsMapper.Ratio(double.NaN).Should().BeNull();
        DiagnosticsMapper.Ratio(double.PositiveInfinity).Should().BeNull();
    }

    /// <summary>The Server page's facts with this posture: nothing else about them matters to the checksum fields.</summary>
    private static ServerSettingsFacts ServerOf(ServerRead<ServerConnectionPosture> posture) =>
        new(
            At,
            new PostgresServerFacts(At, 170011, "17.11", PostgresPlatform.SelfHosted),
            ServerRead<PostgresGrants>.Of(ServerTestFacts.Grants()),
            ServerRead<ServerSettingsRead>.Of(ServerTestFacts.Settings()),
            ServerRead<ServerExtensionsRead>.Of(new ServerExtensionsRead([], 0)),
            posture,
            []);

    [Fact]
    public void The_settings_section_says_whether_checksums_are_on_and_what_they_counted_since_the_statistics_began()
    {
        ServerConnectionPosture on = ServerTestFacts.Posture() with
        {
            DataChecksums = "on",
            ChecksumFailures = 3,
            LastChecksumFailure = At.AddHours(-2),
        };

        PostgresSettingsSection section = DiagnosticsSnapshotBuilder.Settings(ServerOf(ServerRead<ServerConnectionPosture>.Of(on)));

        section.DataChecksums.Should().BeTrue();
        section.ChecksumFailures.Should().Be(3);
        section.LastChecksumFailure.Should().Be(At.AddHours(-2));
    }

    [Fact]
    public void Checksums_off_are_false_with_no_failure_count_because_the_server_counts_none()
    {
        PostgresSettingsSection section = DiagnosticsSnapshotBuilder.Settings(
            ServerOf(ServerRead<ServerConnectionPosture>.Of(ServerTestFacts.Posture())));

        section.DataChecksums.Should().BeFalse("the posture's data_checksums reads off");
        section.ChecksumFailures.Should().BeNull("pg_stat_database.checksum_failures is null while checksums are off");
        section.LastChecksumFailure.Should().BeNull();
    }

    [Fact]
    public void Checksums_on_with_no_failure_are_a_count_of_nought_and_no_time()
    {
        PostgresSettingsSection section = DiagnosticsSnapshotBuilder.Settings(
            ServerOf(ServerRead<ServerConnectionPosture>.Of(ServerTestFacts.Posture() with { DataChecksums = "on", ChecksumFailures = 0 })));

        section.DataChecksums.Should().BeTrue();
        section.ChecksumFailures.Should().Be(0, "nought is a count; null would be \"not known\"");
        section.LastChecksumFailure.Should().BeNull();
    }

    [Fact]
    public void A_posture_that_was_not_read_leaves_every_checksum_field_null_and_the_section_says_why()
    {
        PostgresSettingsSection section = DiagnosticsSnapshotBuilder.Settings(
            ServerOf(new ServerRead<ServerConnectionPosture>(null, "This could not be read (SQLSTATE 57014).")));

        (section.DataChecksums, section.ChecksumFailures, section.LastChecksumFailure).Should().Be(((bool?) null, (long?) null, (DateTimeOffset?) null));
        section.Reason.Should().Contain("connection:").And.Contain("57014");
    }

    [Fact]
    public void The_wal_section_is_pg_stat_wal_as_the_replication_page_reads_it()
    {
        ReplicationFacts facts = ReplicationViewTests.Facts() with
        {
            Wal = ReplicationRead<WalStatistics>.Of(new WalStatistics(1000, 250, 64e6, 3, 10, 5, null, null, At.AddDays(-9))),
        };

        PostgresWalSection wal = DiagnosticsSnapshotBuilder.Wal(facts);

        wal.Outcome.Should().Be(PostgresReadOutcome.Read);
        wal.Reason.Should().BeNull();
        wal.MeasuredAt.Should().Be(ReplicationTestFacts.Now, "the Replication page's read time");
        (wal.Records, wal.FullPageImages, wal.Bytes, wal.StatsReset).Should().Be((1000L, 250L, 64_000_000L, (DateTimeOffset?) At.AddDays(-9)));
    }

    [Fact]
    public void The_wal_bytes_are_the_numerics_whole_number_and_a_counter_that_is_not_finite_is_null()
    {
        PostgresWalSection big = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts() with
        {
            Wal = ReplicationRead<WalStatistics>.Of(new WalStatistics(1, 1, 98_765_432_100.4, 0, null, null, null, null, null)),
        });
        PostgresWalSection broken = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts() with
        {
            Wal = ReplicationRead<WalStatistics>.Of(new WalStatistics(1, 1, double.NaN, 0, null, null, null, null, null)),
        });

        big.Bytes.Should().Be(98_765_432_100);
        broken.Bytes.Should().BeNull();
        broken.StatsReset.Should().BeNull("the view carries no reset time here");
    }

    [Fact]
    public void The_wal_section_on_aurora_is_not_applicable_in_the_replication_pages_own_sentence()
    {
        PostgresWalSection wal = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts(platform: PostgresPlatformKind.AmazonAurora));

        wal.Outcome.Should().Be(PostgresReadOutcome.NotApplicable);
        wal.Reason.Should().Be(ReplicationPlatform.Aurora);
        (wal.Records, wal.FullPageImages, wal.Bytes, wal.StatsReset).Should().Be(((long?) null, (long?) null, (long?) null, (DateTimeOffset?) null));
    }

    [Fact]
    public void The_wal_section_below_the_release_that_has_the_view_needs_it_and_a_failed_read_is_failed()
    {
        PostgresWalSection old = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts() with { Wal = ReplicationRead<WalStatistics>.Needs(PostgresFeature.PgStatWal) });
        PostgresWalSection failed = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts() with
        {
            Wal = new ReplicationRead<WalStatistics>(null, ReplicationReadOutcome.Failed, "This could not be read (SQLSTATE 57014)."),
        });
        PostgresWalSection empty = DiagnosticsSnapshotBuilder.Wal(ReplicationViewTests.Facts() with
        {
            Wal = new ReplicationRead<WalStatistics>(null, ReplicationReadOutcome.Read, null),
        });

        old.Outcome.Should().Be(PostgresReadOutcome.NotApplicable);
        old.Reason.Should().Contain("PostgreSQL 14");
        failed.Outcome.Should().Be(PostgresReadOutcome.Failed);
        failed.Reason.Should().Contain("57014");
        empty.Outcome.Should().Be(PostgresReadOutcome.Unavailable, "a read that returned no row is not a failure the host can act on");
        empty.Reason.Should().Contain("no row");
    }

    [Fact]
    public void A_database_size_the_role_may_not_measure_is_the_estimate_and_the_reason_says_so()
    {
        PostgresStorageSection storage = DiagnosticsSnapshotBuilder.Storage(
            StorageOf() with { DatabaseBytes = null, DatabaseBytesNotRead = "Not measured: pg_database_size needs CONNECT on this database.", GrowthBytesPerDay = null, Window = null },
            PostgresStudioSettings.Of(new PostgresStudioOptions()));

        storage.DatabaseBytes.Should().Be(50_000_000_000);
        storage.Reason.Should().Contain("needs CONNECT").And.Contain("the catalog's estimate").And.Contain("No growth figure yet");
        storage.GrowthBytesPerDay.Should().BeNull();
    }

    [Fact]
    public void The_statements_section_maps_the_window_counts_and_leaves_out_a_statement_with_none()
    {
        StatementFacts facts = StatementsOf(
            Statement(11, "select 1 from orders"),
            Statement(12, "select 2 from orders") with { Window = null });

        PostgresStatementsSection section = DiagnosticsSnapshotBuilder.Statements(facts, Gate(true), PostgresStudioText.Instance);

        section.Outcome.Should().Be(PostgresReadOutcome.Read);
        section.Window.Should().Be(TimeSpan.FromMinutes(10), "the facts' span");
        section.StatisticsSince.Should().Be(At.AddDays(-3), "stats_reset");
        section.TotalMilliseconds.Should().Be(200);
        section.Top.Should().ContainSingle("a statement new to the window has no window counts, and would not add up")
            .Which.Should().BeEquivalentTo(new
            {
                QueryId = 11L,
                Text = "select 1 from orders",
                Calls = 5L,
                TotalMilliseconds = 120d,
                MeanMilliseconds = 24d,
                ShareOfTime = 0.6,
                Rows = 20L,
                SharedBlocksHit = 450L,
                SharedBlocksRead = 15L,
                TempBlocksWritten = 2L,
                StorageBlocksRead = (long?) null,
            });
    }

    [Fact]
    public void A_statements_read_that_did_not_read_says_why_in_the_pages_words()
    {
        StatementFacts facts = StatementsOf() with { State = StatementsState.NotInstalled };

        PostgresStatementsSection section = DiagnosticsSnapshotBuilder.Statements(facts, Gate(true), PostgresStudioText.Instance);

        section.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        section.Reason.Should().Contain("pg_stat_statements is not installed in this database");
        section.Top.Should().BeEmpty();
    }

    [Fact]
    public void The_connections_section_names_applications_only_past_the_gate()
    {
        SessionSnapshot snapshot = SessionsOf(
            new SessionApplicationCounts("orders-api", 10, 2, 7, 1, 1, 0, TimeSpan.FromSeconds(30)),
            new SessionApplicationCounts("", 2, 0, 1, 0, 0, 1, null));

        PostgresConnectionsSection hidden = DiagnosticsSnapshotBuilder.Connections(snapshot, Gate(false));
        PostgresConnectionsSection named = DiagnosticsSnapshotBuilder.Connections(snapshot, Gate(true));

        hidden.ByApplication.Should().BeEmpty();
        hidden.Database.Total.Should().Be(12);
        hidden.Database.ApplicationName.Should().BeNull("the total names no application");
        hidden.MaxConnections.Should().Be(100);
        hidden.ReservedConnections.Should().Be(3);
        hidden.ServerConnections.Should().Be(12);
        hidden.AllSessionsVisible.Should().BeTrue();

        named.ByApplication.Select(static x => x.ApplicationName).Should().Equal("orders-api", "");
        named.ByApplication[1].StateUnknown.Should().Be(1);
        named.ByApplication[0].OldestTransaction.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Findings_are_worst_first_then_by_code_and_key_and_checks_by_code()
    {
        HealthCheckInfo c05 = Info("C05", HealthArea.Connections);
        HealthCheckInfo t03 = Info("T03", HealthArea.Tables, variants: ["index", "partitions"]);

        HealthSweep sweep = Sweep(
            Result(t03, findings:
            [
                Finding(t03, "b", "public.b_idx", "index") with { Severity = HealthSeverity.Critical },
                Finding(t03, "a", "public.a_idx", "index") with { Severity = HealthSeverity.Critical },
            ]),
            Result(c05, findings: [Finding(c05, "slow") with { Severity = HealthSeverity.Info }]));

        (IReadOnlyList<PostgresFinding> findings, IReadOnlyList<PostgresCheck> checks, int withheld) =
            DiagnosticsSnapshotBuilder.Findings(sweep.Results, Gate(false), NoStudioRuntime.Instance, "store", new PostgresStudio.Services.StudioScope("main", "db1", null));

        findings.Select(static x => x.Key).Should().Equal("T03|index|public.a_idx", "T03|index|public.b_idx", "C05");
        checks.Select(static x => (x.Code, x.Findings)).Should().Equal(("C05", 1), ("T03", 2));
        withheld.Should().Be(0);
    }
}
