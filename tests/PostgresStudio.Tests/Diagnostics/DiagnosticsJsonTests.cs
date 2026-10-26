using System.Text.Json;

using PostgresStudio.Services.Diagnostics;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The snapshot's JSON, <c>schemaVersion</c> 1 (API design §4.1): a fully populated snapshot and a minimal one, each a Verify
/// baseline - the minimal one proving that every key is always present - and the conventions a consumer relies on.
/// </summary>
public class DiagnosticsJsonTests
{
    /// <summary>Every section, every kind of value: the shape a host's parser is written against.</summary>
    internal static PostgresDiagnosticsSnapshot Full() => new()
    {
        SchemaVersion = 1,
        Producer = "PostgresStudio 0.5.0",
        GeneratedAt = At.AddSeconds(3),
        Database = new PostgresDatabaseRef("tenants", "acme"),
        Outcome = PostgresReadOutcome.Read,
        Server = new PostgresServerInfo
        {
            VersionNumber = 140013,
            Version = "14.13",
            Platform = PostgresPlatformKind.AmazonAurora,
            Instance = "acme-writer-1",
            InRecovery = false,
            StartedAt = new DateTimeOffset(2026, 10, 1, 3, 12, 0, TimeSpan.Zero),
        },
        Compute = PostgresComputeCapacity.AuroraServerlessV2(4.5, 0.5, 16, At) with { IoOptimized = true },
        Findings =
        [
            new PostgresFinding
            {
                Code = "T07",
                Variant = "dead-rows",
                Subject = "public.orders",
                TenantId = null,
                Severity = PostgresSeverity.Warning,
                Category = PostgresFindingCategory.Tables,
                Title = "Vacuum is falling behind on public.orders",
                Detail = "Dead rows accumulate faster than autovacuum clears them.",
                Caveat = "Statistics since 2026-10-30.",
                Action = "ALTER TABLE \"public\".\"orders\" SET (autovacuum_vacuum_scale_factor = 0.02);",
                ActionExplanation = "Vacuums it at 2% dead rows instead of 20%.",
                ActionKind = PostgresActionKind.Migration,
                ActionRequires = null,
                Evidence = [new KeyValuePair<string, string>("Dead rows", "1,204,331"), new KeyValuePair<string, string>("Session 1", HostGate.WithheldValue)],
                DocsUrl = "https://github.com/lahma/marten-studio/blob/v0.5.0/docs/health/tables.md#t07-vacuum-falling-behind",
                MeasuredAt = At,
            },
        ],
        Checks =
        [
            new PostgresCheck
            {
                Code = "C04",
                Category = PostgresFindingCategory.Connections,
                Title = "Idle in transaction",
                RequiresActivity = true,
                DocsUrl = "https://github.com/lahma/marten-studio/blob/v0.5.0/docs/health/connections.md#c04-idle-in-transaction",
                Outcome = PostgresReadOutcome.Withheld,
            },
            new PostgresCheck
            {
                Code = "T07",
                Category = PostgresFindingCategory.Tables,
                Title = "Vacuum falling behind",
                Variants = ["dead-rows", "inserts"],
                DocsUrl = "https://github.com/lahma/marten-studio/blob/v0.5.0/docs/health/tables.md#t07-vacuum-falling-behind",
                Outcome = PostgresReadOutcome.Read,
                Findings = 1,
            },
            new PostgresCheck
            {
                Code = "T08",
                Category = PostgresFindingCategory.Tables,
                Title = "Never analysed, or stale statistics",
                DocsUrl = "https://github.com/lahma/marten-studio/blob/v0.5.0/docs/health/tables.md#t08-never-analysed-or-stale-statistics",
                Outcome = PostgresReadOutcome.Unavailable,
                Reason = "Not checked: this needs pg_read_all_stats for role studio_reader.",
                SqlState = "42501",
            },
        ],
        FindingsMeasuredAt = At,
        FindingsWithheld = 2,
        Storage = new PostgresStorageSection
        {
            Outcome = PostgresReadOutcome.Read,
            MeasuredAt = At,
            DatabaseBytes = 51_234_567_890,
            SizesAreEstimates = true,
            StatisticsSince = At.AddDays(-30),
            GrowthBytesPerDay = 812_345_678,
            LargestTables =
            [
                new PostgresTableStorage
                {
                    Schema = "public",
                    Name = "orders",
                    Ownership = PostgresTableOwnership.Application,
                    TotalBytes = 26_000_000_000,
                    TableBytes = 20_000_000_000,
                    IndexBytes = 5_000_000_000,
                    ToastBytes = 1_000_000_000,
                    EstimatedRows = 300_000_000,
                    ShareOfDatabase = 0.5075,
                    HotUpdateRatio = 0.25,
                    GrowthBytesPerDay = 600_000_000,
                },
                new PostgresTableStorage
                {
                    Schema = "quartz",
                    Name = "qrtz_triggers",
                    Ownership = PostgresTableOwnership.Library,
                    Owner = "Quartz.NET",
                    TotalBytes = 8192,
                    TableBytes = 8192,
                    EstimatedRows = 3,
                },
            ],
        },
        Statements = new PostgresStatementsSection
        {
            Outcome = PostgresReadOutcome.Read,
            MeasuredAt = At,
            StatisticsSince = At.AddDays(-3),
            Window = TimeSpan.FromMinutes(10),
            TotalMilliseconds = 200.5,
            Top =
            [
                new PostgresStatement
                {
                    QueryId = -7_234_567_890_123_456_789,
                    Text = "select d.data from public.mt_doc_order as d where d.id = $1",
                    Calls = 5,
                    TotalMilliseconds = 120.25,
                    MeanMilliseconds = 24.05,
                    ShareOfTime = 0.6,
                    Rows = 5,
                    SharedBlocksHit = 450,
                    SharedBlocksRead = 15,
                    TempBlocksWritten = 2,
                    StorageBlocksRead = 9,
                },
                new PostgresStatement
                {
                    QueryId = 42,
                    TextWithheld = "Withheld: it still carries a string constant as written.",
                    Calls = 1,
                    TotalMilliseconds = 10,
                    MeanMilliseconds = 10,
                    ShareOfTime = 0.05,
                },
            ],
        },
        Settings = new PostgresSettingsSection
        {
            Outcome = PostgresReadOutcome.Read,
            MeasuredAt = At,
            Settings =
            [
                new PostgresSetting { Name = "shared_buffers", Group = "memory", Value = "16384", Unit = "8kB", Source = "configuration file" },
                new PostgresSetting { Name = "max_connections", Group = "connections", Value = "100", Source = "configuration file", PendingRestart = true },
                new PostgresSetting { Name = "max_slot_wal_keep_size", Group = "wal" },
            ],
            Extensions =
            [
                new PostgresExtension { Name = "pg_stat_statements", InstalledVersion = "1.11", AvailableVersion = "1.11", Usable = true },
                new PostgresExtension { Name = "pgstattuple", AvailableVersion = "1.5", Note = "Not installed in this database." },
            ],
        },
        Connections = new PostgresConnectionsSection
        {
            Outcome = PostgresReadOutcome.Read,
            MeasuredAt = At,
            MaxConnections = 100,
            ReservedConnections = 3,
            ServerConnections = 41,
            Database = new PostgresConnectionCounts { Total = 12, Active = 2, Idle = 8, IdleInTransaction = 1, Waiting = 1, OldestTransaction = TimeSpan.FromSeconds(95.5) },
            ByApplication =
            [
                new PostgresConnectionCounts { ApplicationName = "orders-api", Total = 10, Active = 2, Idle = 7, IdleInTransaction = 1, Waiting = 1, OldestTransaction = TimeSpan.FromSeconds(95.5) },
                new PostgresConnectionCounts { ApplicationName = "", Total = 2, Idle = 1, StateUnknown = 1 },
            ],
            AllSessionsVisible = false,
        },
        Withheld = new Dictionary<string, string> { ["connections.byApplication"] = HostGate.WithheldSection, ["statements"] = HostGate.WithheldSection },
    };

    /// <summary>A database that was not found: every section null, every key still written.</summary>
    internal static PostgresDiagnosticsSnapshot Minimal() => new()
    {
        SchemaVersion = 1,
        Producer = "PostgresStudio 0.5.0",
        GeneratedAt = At,
        Database = new PostgresDatabaseRef("tenants", "gone"),
        Outcome = PostgresReadOutcome.NotFound,
        Reason = "Data source 'tenants' lists no database named 'gone'.",
    };

    [Fact]
    public Task A_full_snapshot_matches_its_baseline() =>
        Verify(Full().ToJson(), extension: "json")
            .UseDirectory("../Verify")
            .UseFileName("DiagnosticsSnapshot_v1");

    [Fact]
    public Task A_minimal_snapshot_writes_every_key_and_matches_its_baseline() =>
        Verify(Minimal().ToJson(), extension: "json")
            .UseDirectory("../Verify")
            .UseFileName("DiagnosticsSnapshot_v1_minimal");

    [Fact]
    public void Every_key_of_the_full_shape_is_present_in_the_minimal_one_at_the_top_and_in_findings()
    {
        using JsonDocument full = JsonDocument.Parse(Full().ToJson());
        using JsonDocument minimal = JsonDocument.Parse(Minimal().ToJson());

        Keys(minimal.RootElement).Should().Equal(Keys(full.RootElement), "a consumer reads one shape whatever was found");
        Keys(minimal.RootElement.GetProperty("findings")).Should().Equal(Keys(full.RootElement.GetProperty("findings")));

        static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(static x => x.Name)];
    }

    [Fact]
    public void The_conventions_hold_ids_as_strings_durations_as_seconds_enums_camel_case_times_in_utc()
    {
        using JsonDocument json = JsonDocument.Parse(Full().ToJson());
        JsonElement root = json.RootElement;

        root.GetProperty("schema").GetString().Should().Be("postgresstudio-diagnostics");
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("statements").GetProperty("top")[0].GetProperty("queryId").GetString().Should().Be("-7234567890123456789",
            "a 64-bit hash does not fit a JavaScript number");
        root.GetProperty("statements").GetProperty("windowSeconds").GetDouble().Should().Be(600);
        root.GetProperty("connections").GetProperty("database").GetProperty("oldestTransactionSeconds").GetDouble().Should().Be(95.5);
        root.GetProperty("server").GetProperty("platform").GetString().Should().Be("amazonAurora");
        root.GetProperty("findings").GetProperty("items")[0].GetProperty("actionKind").GetString().Should().Be("migration");
        root.GetProperty("findings").GetProperty("items")[0].GetProperty("key").GetString().Should().Be("T07|dead-rows|public.orders");
        root.GetProperty("generatedAt").GetString().Should().Be("2026-11-02T06:00:04.0000000+00:00");
        root.GetProperty("storage").GetProperty("databaseBytes").GetInt64().Should().Be(51_234_567_890, "byte counts are numbers");
    }

    [Fact]
    public void A_time_in_another_offset_is_written_in_utc_and_a_number_that_is_not_finite_is_null()
    {
        PostgresDiagnosticsSnapshot snapshot = Minimal() with
        {
            GeneratedAt = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.FromHours(2)),
            Statements = new PostgresStatementsSection { TotalMilliseconds = double.NaN },
        };

        using JsonDocument json = JsonDocument.Parse(snapshot.ToJson());

        json.RootElement.GetProperty("generatedAt").GetString().Should().Be("2026-11-02T06:00:00.0000000+00:00");
        json.RootElement.GetProperty("statements").GetProperty("totalMilliseconds").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Text_is_written_as_it_is_and_only_what_json_requires_is_escaped()
    {
        string json = (Minimal() with { Reason = "Tietokanta ‹withheld› isn't \"here\"\n\\ <script>" }).ToJson();

        json.Should().Contain("Tietokanta ‹withheld› isn't").And.Contain("+00:00", "a time's offset reads as it is");
        json.Should().Contain("\\\"here\\\"\\n\\\\", "quotes, a control character and a backslash are escaped as JSON requires");

        using JsonDocument parsed = JsonDocument.Parse(json);
        parsed.RootElement.GetProperty("reason").GetString().Should().Be("Tietokanta ‹withheld› isn't \"here\"\n\\ <script>", "and the text round-trips");
    }
}
