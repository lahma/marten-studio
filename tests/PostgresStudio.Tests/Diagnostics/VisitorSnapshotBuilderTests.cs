using System.Text.Json;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The studio's snapshot download (API design §2.4, §5.3): the file is the pages' views, each already this visitor's, and nothing
/// a page withholds reaches it - sentinel tests for a visitor short of <c>ViewDatabaseActivity</c>, of the store policy and of a
/// schema's browser gate, and the file name of §4.3.
/// </summary>
public class VisitorSnapshotBuilderTests
{
    private const string RawEvidenceSentinel = "SENTINEL-RAW-pid-4242";
    private const string StorageSentinel = "SENTINEL-STORAGE-orders";
    private const string ApplicationSentinel = "SENTINEL-APP-billing";
    private const string StatementSentinel = "SENTINEL_STATEMENT_column";

    private const string ActivitySentence = "needs Capabilities.ViewDatabaseActivity";
    private const string StorePolicySentence = "The store policy does not allow you this database.";
    private const string SchemaSentence = "1 table is in a schema you may not browse, so its name is not shown.";

    private static readonly HealthCheckRegistry Registry = new(PostgresStudioDescriptor.Instance);

    private static HealthReport Report()
    {
        HealthCheckInfo c05 = Info("C05", HealthArea.Connections);

        // The sweep's finding still carries the Activity value; the report's evidence - the visitor's - does not.
        HealthFinding raw = Finding(c05, "A statement has run for 12 minutes") with
        {
            Evidence = [new HealthEvidence("Longest", "12 min"), new HealthEvidence("Session 1 (longest)", RawEvidenceSentinel, HealthGate.Activity)],
        };

        HealthReportFinding shown = new(
            raw,
            [new HealthEvidence("Longest", "12 min"), new HealthEvidence("Session 1 (longest)", "withheld - " + ActivitySentence, HealthGate.Activity)],
            "tenants|acme|C05",
            "https://example.test/docs#c05",
            RemedyAvailable: true,
            HostRuns: null);

        return new HealthReport
        {
            StoreKey = "tenants",
            DatabaseId = "acme",
            MeasuredAt = At,
            Findings = [shown],
            WithheldCount = 1,
            WithheldReason = "1 finding is not shown to you: it " + ActivitySentence + ".",
            Checks =
            [
                new HealthCheckStatus("C04", "Idle in transaction", HealthArea.Connections, HealthCostTier.Catalog, HealthCheckOutcome.Checked, 0, null, null, null, "docs", [], FindingsWithheld: true)
                {
                    BelowGate = ActivitySentence,
                },
                new HealthCheckStatus("C05", "Long-running statement", HealthArea.Connections, HealthCostTier.Catalog, HealthCheckOutcome.Checked, 1, null, null, null, "docs", []),
            ],
        };
    }

    private static StorageView Storage() => new()
    {
        MeasuredAt = At,
        DatabaseBytes = 1_000_000,
        EstimatedDatabaseBytes = 900_000,
        Tables =
        [
            new StorageTableView("public", StorageSentinel, null, StudioTableOwnership.Owned, null, 600_000, 500_000, 80_000, 20_000, 1_000, 0.6, 0.9, null, 1234.4),
            new StorageTableView(null, null, null, null, null, 300_000, 300_000, 0, 0, 10, 0.3, null, null, null),
        ],
        WithheldReason = SchemaSentence,
    };

    private static StatementsView Statements(bool open) => open
        ? new StatementsView
        {
            State = StatementsState.Read,
            MeasuredAt = At,
            TotalExecMs = 200,
            Rows =
            [
                new StatementRowView(11, "select " + StatementSentinel + " from orders", null, "orders", "orders",
                    new StatementCounts(5, 120, 20, 450, 15, 2, 0, null), new StatementCounts(10, 240, 40, 900, 30, 5, 0, null), 0.6, At, null),
                new StatementRowView(12, null, "withheld: it still carries a literal", "orders", "orders",
                    new StatementCounts(1, 10, 1, 1, 0, 0, 0, null), new StatementCounts(1, 10, 1, 1, 0, 0, 0, null), 0.05, At, null),
            ],
        }
        : StatementsView.Refused(DatabaseRefusal.CapabilityOff, "Statements " + ActivitySentence + ".");

    private static ServerPageView Server() => new()
    {
        MeasuredAt = At,
        Settings =
        [
            new ServerSettingGroup("memory", "Memory", [new ServerSettingLine(
                new ServerSettingRow("shared_buffers", "memory", default, new ServerSettingValue("shared_buffers", "16384", "8kB", "configuration file", false, "postmaster", "16384")),
                "128 MB", null, "postgresql.conf")]),
        ],
    };

    private static SessionActivitySummary Summary() => new()
    {
        MeasuredAt = At,
        Connections = new SessionApplicationCounts(null, 5, 1, 3, 1, 0, 0, TimeSpan.FromMinutes(2)),
        Budget = new SessionConnectionBudget(100, 3, 12, AllSessionsVisible: true),
        ApplicationCount = 1,
    };

    private static SessionActivityDetails Details(bool open) => open
        ? new SessionActivityDetails
        {
            MeasuredAt = At,
            Applications = [new SessionApplicationCounts(ApplicationSentinel, 5, 1, 3, 1, 0, 0, TimeSpan.FromMinutes(2))],
            ApplicationCount = 1,
        }
        : SessionActivityDetails.Refused(DatabaseRefusal.CapabilityOff, "Session details " + ActivitySentence + ".");

    private static string Json(HealthReport report, StorageView storage, bool activity) =>
        VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("tenants", "acme"),
            report,
            storage,
            Statements(activity),
            Server(),
            Summary(),
            Details(activity),
            Registry,
            "PostgresStudio 0.5.0",
            At).ToJson();

    [Fact]
    public void A_visitor_short_of_ViewDatabaseActivity_gets_the_pages_sentences_and_no_Activity_grade_value()
    {
        string json = Json(Report(), Storage(), activity: false);

        json.Should().NotContain(RawEvidenceSentinel, "the report's evidence is the visitor's, never the sweep's")
            .And.NotContain(ApplicationSentinel)
            .And.NotContain(StatementSentinel);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("outcome").GetString().Should().Be("read");
        root.GetProperty("statements").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("withheld").GetProperty("statements").GetString().Should().Contain(ActivitySentence, "the Statements page's own sentence");
        root.GetProperty("withheld").GetProperty("connections.byApplication").GetString().Should().Contain(ActivitySentence);
        root.GetProperty("withheld").GetProperty("findings").GetString().Should().Contain(ActivitySentence);

        JsonElement connections = root.GetProperty("connections");
        connections.GetProperty("byApplication").GetArrayLength().Should().Be(0);
        connections.GetProperty("database").GetProperty("total").GetInt32().Should().Be(5, "the totals are the database-wide gate's");

        JsonElement findings = root.GetProperty("findings");
        findings.GetProperty("withheld").GetInt32().Should().Be(1);
        findings.GetProperty("items")[0].GetProperty("evidence").EnumerateArray()
            .Select(static x => x.GetProperty("value").GetString())
            .Should().Contain("withheld - " + ActivitySentence);

        JsonElement c04 = findings.GetProperty("checks").EnumerateArray().Single(static x => x.GetProperty("code").GetString() == "C04");
        c04.GetProperty("outcome").GetString().Should().Be("withheld");
        c04.GetProperty("reason").GetString().Should().Contain(ActivitySentence);
        c04.GetProperty("variants").GetArrayLength().Should().Be(0, "the registry's variants, which C04 has none of");
    }

    [Fact]
    public void A_visitor_through_every_gate_gets_the_names_and_the_texts_the_pages_show()
    {
        string json = Json(Report(), Storage(), activity: true);

        json.Should().Contain(ApplicationSentinel).And.Contain(StatementSentinel).And.Contain(StorageSentinel);
        json.Should().NotContain(RawEvidenceSentinel, "still the report's evidence");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement top = document.RootElement.GetProperty("statements").GetProperty("top");

        top.GetArrayLength().Should().Be(2);
        top[1].GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);
        top[1].GetProperty("textWithheld").GetString().Should().Contain("literal", "the page's note for a text it does not show");
        document.RootElement.GetProperty("withheld").TryGetProperty("statements", out _).Should().BeFalse();
    }

    [Fact]
    public void A_visitor_the_store_policy_refuses_gets_the_refusal_and_nothing_about_the_database()
    {
        string json = Json(HealthReport.Refused(HealthRefusal.StorePolicy, StorePolicySentence), Storage(), activity: true);

        json.Should().NotContain(StorageSentinel).And.NotContain(ApplicationSentinel).And.NotContain(StatementSentinel);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        root.GetProperty("outcome").GetString().Should().Be("withheld");
        root.GetProperty("reason").GetString().Should().Be(StorePolicySentence);
        root.GetProperty("findings").GetProperty("items").GetArrayLength().Should().Be(0);
        root.GetProperty("storage").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("connections").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void A_table_in_a_schema_the_browser_withholds_is_left_out_and_the_page_says_why()
    {
        using JsonDocument document = JsonDocument.Parse(Json(Report(), Storage(), activity: true));
        JsonElement root = document.RootElement;

        JsonElement tables = root.GetProperty("storage").GetProperty("largestTables");
        tables.GetArrayLength().Should().Be(1, "the withheld row has no name to give, and is not listed as a nameless one");
        tables[0].GetProperty("name").GetString().Should().Be(StorageSentinel);
        tables[0].GetProperty("growthBytesPerDay").GetInt64().Should().Be(1234);
        root.GetProperty("withheld").GetProperty("storage.largestTables").GetString().Should().Be(SchemaSentence);
    }

    [Fact]
    public void A_server_the_store_policy_refuses_is_withheld_with_its_settings()
    {
        PostgresDiagnosticsSnapshot snapshot = VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("tenants", "acme"),
            Report(),
            Storage(),
            Statements(open: true),
            ServerPageView.Refused(ServerRefusal.StorePolicy, StorePolicySentence),
            Summary(),
            Details(open: true),
            Registry,
            "PostgresStudio 0.5.0",
            At);

        snapshot.Server.Should().BeNull();
        snapshot.Settings.Should().BeNull();
        snapshot.Withheld.Should().Contain("server", StorePolicySentence).And.Contain("settings", StorePolicySentence);
    }

    [Fact]
    public void The_settings_are_the_server_pages_curated_rows()
    {
        PostgresDiagnosticsSnapshot snapshot = VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("tenants", "acme"), Report(), Storage(), Statements(true), Server(), Summary(), Details(true), Registry, "p", At);

        snapshot.Settings!.Settings.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Name = "shared_buffers",
            Group = "memory",
            Value = "16384",
            Unit = "8kB",
            Source = "configuration file",
            PendingRestart = false,
        });
    }

    [Fact]
    public void The_download_carries_the_checksum_state_and_failures_and_the_all_visible_ratio_the_pages_already_hold_and_no_wal()
    {
        StorageView storage = new()
        {
            MeasuredAt = At,
            DatabaseBytes = 1_000_000,
            EstimatedDatabaseBytes = 900_000,
            Tables =
            [
                new StorageTableView("public", "orders", null, StudioTableOwnership.Owned, null, 600_000, 500_000, 80_000, 20_000, 1_000, 0.6, 0.9, null, 1234.4, AllVisibleRatio: 0.8),
                new StorageTableView("public", "empty", null, StudioTableOwnership.Owned, null, 8_192, 8_192, 0, 0, -1, 0.1, null, null, null),
            ],
        };
        ServerPageView server = Server() with
        {
            DataChecksums = "on",
            ChecksumFailures = 2,
            LastChecksumFailure = At.AddHours(-3),
        };

        PostgresDiagnosticsSnapshot snapshot = VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("tenants", "acme"), Report(), storage, Statements(true), server, Summary(), Details(true), Registry, "p", At);

        snapshot.Settings!.DataChecksums.Should().BeTrue();
        snapshot.Settings.ChecksumFailures.Should().Be(2);
        snapshot.Settings.LastChecksumFailure.Should().Be(At.AddHours(-3));
        snapshot.Storage!.LargestTables.Select(static x => x.AllVisibleRatio).Should().Equal(0.8, null);
        snapshot.Wal.Should().BeNull("the Replication page's facts are not the download's; the API's snapshot carries them");

        using JsonDocument document = JsonDocument.Parse(snapshot.ToJson());
        document.RootElement.GetProperty("wal").ValueKind.Should().Be(JsonValueKind.Null, "the key is there, as every key always is");
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("not read", null)]
    public void The_checksum_setting_the_page_shows_is_a_bool_only_when_it_was_read(string shown, bool? expected)
    {
        PostgresDiagnosticsSnapshot snapshot = VisitorSnapshotBuilder.Compose(
            new PostgresDatabaseRef("tenants", "acme"), Report(), Storage(), Statements(true), Server() with { DataChecksums = shown }, Summary(), Details(true), Registry, "p", At);

        snapshot.Settings!.DataChecksums.Should().Be(expected);
    }

    [Theory]
    [InlineData("postgres", "default", "", "postgres-snapshot-default-default-20261102T060001Z.json")]
    [InlineData("marten", "tenants", "acme db/1", "marten-snapshot-tenants-acme-db-1-20261102T060001Z.json")]
    [InlineData("marten", "Tenants.v2", "pg_tenant-01", "marten-snapshot-Tenants.v2-pg_tenant-01-20261102T060001Z.json")]
    [InlineData("postgres", "ünïcode", "a:b", "postgres-snapshot--n-code-a-b-20261102T060001Z.json")]
    public void The_file_name_keeps_the_safe_characters_and_says_when(string studio, string source, string database, string expected)
    {
        VisitorSnapshotBuilder.FileName(studio, source, database, At).Should().Be(expected);
    }
}
