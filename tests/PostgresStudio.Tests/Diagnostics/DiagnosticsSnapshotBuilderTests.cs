using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Statements;

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
        });
        orders.ShareOfDatabase.Should().BeApproximately(26_000_000_000d / 51_234_567_890, 1e-9);

        storage.LargestTables[1].Should().BeEquivalentTo(
            new { Schema = "quartz", Name = "qrtz_triggers", Ownership = PostgresTableOwnership.Library, Owner = "Quartz.NET" },
            "a schema the database browser withholds from a visitor is the host's role's to read (API design §2.3)");
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
