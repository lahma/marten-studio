using System.Text;
using System.Text.Json.Nodes;

using PostgresStudio.Services.Diagnostics;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The compare's file reader (1.2-H-B, D-HEALTH C5): the writer's mirror, bounded and fail-closed, and a refusal that never
/// repeats a word the file holds.
/// </summary>
public class SnapshotFileReaderTests
{
    private const string Sentinel = "SENTINEL-FILE-CONTENT-9d41";

    /// <summary>Every section, including the internal Connection one and a tenant finding.</summary>
    internal static PostgresDiagnosticsSnapshot Everything()
    {
        PostgresDiagnosticsSnapshot full = DiagnosticsJsonTests.Full();

        return full with
        {
            Findings =
            [
                .. full.Findings,
                new PostgresFinding
                {
                    Code = "E06",
                    Variant = "long-streams",
                    Subject = null,
                    TenantId = "acme",
                    Severity = PostgresSeverity.Info,
                    Category = PostgresFindingCategory.Events,
                    Title = "Long streams",
                    Detail = "Detail.",
                    DocsUrl = "https://example.test/e06",
                    MeasuredAt = DiagnosticsTestFacts.At,
                    Accepted = true,
                    AcceptedReason = "We know.",
                },
            ],
            Connection = new ConnectionSnapshotSection
            {
                MeasuredAt = DiagnosticsTestFacts.At,
                NpgsqlVersion = "9.0.4",
                Platform = "amazonAurora",
                Pooler = ["PgBouncer"],
                Tls = "verified",
                Settings =
                [
                    new ConnectionSnapshotSetting("Host", "Connection", "db.example", "connectionString", null, null, null),
                    new ConnectionSnapshotSetting("Password", "Authentication", "set", "connectionString", "PGPASSWORD", null, null),
                ],
                Observed = [new ConnectionSnapshotValue("Server", "PostgreSQL 17")],
                Hints = [new ConnectionSnapshotHint("S11", "SSL Mode", "Use VerifyFull.", "SSL Mode=VerifyFull", "builder.SslMode = SslMode.VerifyFull;")],
                Applications = [new ConnectionSnapshotApplication("orders-api", 10, DiagnosticsTestFacts.At.AddHours(-2), 10, 10)],
                Notes = ["A note."],
            },
        };
    }

    private static SnapshotFileRead Read(string json) => SnapshotFileReader.Read(Encoding.UTF8.GetBytes(json));

    private static JsonObject Tree(PostgresDiagnosticsSnapshot snapshot) => JsonNode.Parse(DiagnosticsJson.Write(snapshot))!.AsObject();

    private static SnapshotFileRead Read(JsonObject tree) => Read(tree.ToJsonString());

    // -------------------------------------------------------------------------------------------------
    // The drift guard
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_written_and_read_back_is_written_byte_for_byte_the_same()
    {
        PostgresDiagnosticsSnapshot snapshot = Everything();
        string written = DiagnosticsJson.Write(snapshot);

        SnapshotFileRead read = Read(written);

        read.Problem.Should().BeNull();
        read.SkippedItems.Should().Be(0);
        DiagnosticsJson.Write(read.Snapshot!).Should().Be(written, "the reader is the writer's mirror: a key added to the writer and not to the reader fails here");
    }

    [Fact]
    public void The_round_trip_fixture_covers_every_section_of_the_writer()
    {
        // A section the fixture leaves null would round-trip as null whatever the reader does.
        JsonObject tree = Tree(Everything());

        foreach (string key in (string[]) ["server", "compute", "findings", "storage", "statements", "settings", "connections", "wal", "connectionHistory", "connection", "withheld"])
        {
            tree[key].Should().NotBeNull(key + " is null in the fixture, so the round trip proves nothing about it");
        }
    }

    [Fact]
    public async Task A_snapshot_is_read_from_a_stream()
    {
        PostgresDiagnosticsSnapshot snapshot = Everything();
        await using MemoryStream stream = new(Encoding.UTF8.GetBytes(DiagnosticsJson.Write(snapshot)));

        SnapshotFileRead read = await SnapshotFileReader.ReadAsync(stream, TestContext.Current.CancellationToken);

        read.Snapshot.Should().NotBeNull();
        read.Snapshot!.Database.Source.Should().Be("tenants");
    }

    [Fact]
    public void A_byte_order_mark_is_tolerated()
    {
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(DiagnosticsJson.Write(Everything()))];

        SnapshotFileReader.Read(bytes).Snapshot.Should().NotBeNull();
    }

    [Fact]
    public void A_finding_is_keyed_by_its_code_variant_and_subject_and_the_files_key_is_ignored()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!["key"] = Sentinel;

        SnapshotFileRead read = Read(tree);

        read.Snapshot!.Findings[0].Key.Should().Be("T07|dead-rows|public.orders");
    }

    // -------------------------------------------------------------------------------------------------
    // Tolerance
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Missing_keys_are_tolerated_and_unknown_keys_ignored()
    {
        SnapshotFileRead read = Read("""
            {"schema":"postgresstudio-diagnostics","schemaVersion":1,"generatedAt":"2026-11-01T00:00:00Z",
             "database":{"source":"s","database":"d"},"outcome":"read","somethingNew":{"a":[1,2,3]}}
            """);

        read.Problem.Should().BeNull();
        read.Snapshot!.Findings.Should().BeEmpty();
        read.Snapshot.Storage.Should().BeNull();
        read.Snapshot.Statements.Should().BeNull();
        read.Snapshot.Withheld.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_keys_inside_items_are_ignored()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!["futureKey"] = "x";
        tree["storage"]!["largestTables"]![0]!["futureKey"] = JsonNode.Parse("[1]");

        Read(tree).Snapshot.Should().NotBeNull();
    }

    [Fact]
    public void An_unknown_enumeration_member_leaves_the_item_out_and_counts_it()
    {
        JsonObject tree = Tree(Everything());
        int findings = tree["findings"]!["items"]!.AsArray().Count;
        tree["findings"]!["items"]![0]!["severity"] = "catastrophic";
        tree["storage"]!["largestTables"]![1]!["ownership"] = "somethingNew";
        tree["server"]!["platform"] = "mainframe";

        SnapshotFileRead read = Read(tree);

        read.Problem.Should().BeNull();
        read.SkippedItems.Should().Be(3);
        read.Snapshot!.Findings.Should().HaveCount(findings - 1);
        read.Snapshot.Storage!.LargestTables.Should().HaveCount(1);
        read.Snapshot.Server.Should().BeNull();
    }

    [Fact]
    public void An_outcome_this_studio_does_not_know_reads_as_unavailable()
    {
        JsonObject tree = Tree(Everything());
        tree["wal"]!["outcome"] = "someNewOutcome";

        SnapshotFileRead read = Read(tree);

        read.Snapshot!.Wal!.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        read.SkippedItems.Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_that_is_not_json_is_refused_with_where_it_stopped_and_not_what_it_held()
    {
        SnapshotFileRead read = Read("{\n  \"" + Sentinel + "\": nope\n}");

        read.Snapshot.Should().BeNull();
        read.Problem.Should().StartWith("This file is not a snapshot's JSON (line ").And.NotContain(Sentinel);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"something-else\",\"schemaVersion\":1}")]
    public void A_file_that_is_not_a_snapshot_is_refused_by_its_schema(string json)
    {
        SnapshotFileRead read = Read(json);

        read.Snapshot.Should().BeNull();
        read.Problem.Should().Contain("not a snapshot this studio writes");
    }

    [Fact]
    public void A_schema_name_from_the_file_is_never_repeated()
    {
        SnapshotFileRead read = Read("{\"schema\":\"" + Sentinel + "\",\"schemaVersion\":1}");

        read.Problem.Should().NotBeNull().And.NotContain(Sentinel);
    }

    [Theory]
    [InlineData("2", "newer studio (schemaVersion 2); this one reads 1")]
    [InlineData("0", "schemaVersion 0 is not one this studio reads")]
    [InlineData("\"1\"", "schemaVersion is not a whole number")]
    [InlineData("1.5", "schemaVersion is not a whole number")]
    public void Only_schemaVersion_1_is_read(string version, string sentence)
    {
        SnapshotFileRead read = Read("{\"schema\":\"postgresstudio-diagnostics\",\"schemaVersion\":" + version + "}");

        read.Snapshot.Should().BeNull();
        read.Problem.Should().Contain(sentence);
    }

    [Fact]
    public void Nesting_deeper_than_the_bound_is_refused()
    {
        string deep = new string('[', SnapshotFileReader.MaxDepth + 1) + new string(']', SnapshotFileReader.MaxDepth + 1);

        SnapshotFileRead read = Read(deep);

        read.Problem.Should().StartWith("This file is not a snapshot's JSON");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_refused()
    {
        string json = DiagnosticsJson.Write(Everything());

        Read("// no\n" + json).Problem.Should().StartWith("This file is not a snapshot's JSON");
        Read(json[..^1] + ",}").Problem.Should().StartWith("This file is not a snapshot's JSON");
    }

    [Fact]
    public async Task A_file_over_the_byte_bound_is_refused_without_parsing_it()
    {
        await using MemoryStream stream = new(new byte[SnapshotFileReader.MaxBytes + 1]);

        SnapshotFileRead read = await SnapshotFileReader.ReadAsync(stream, TestContext.Current.CancellationToken);

        read.Problem.Should().Contain("over 4 MB");
    }

    public static TheoryData<string, string, int> Caps => new()
    {
        { "findings.items", "findings.items has more items", 5_001 },
        { "findings.checks", "findings.checks has more items", 1_001 },
        { "storage.largestTables", "storage.largestTables has more items", 1_001 },
        { "statements.top", "statements.top has more items", 1_001 },
        { "settings.settings", "settings.settings has more items", 2_001 },
        { "settings.extensions", "settings.extensions has more items", 1_001 },
        { "connections.byApplication", "connections.byApplication has more items", 1_001 },
        { "connectionHistory.byApplication", "connectionHistory.byApplication has more items", 1_001 },
        { "connection.settings", "connection.settings has more items", 2_001 },
        { "connection.observed", "connection.observed has more items", 1_001 },
        { "connection.hints", "connection.hints has more items", 501 },
        { "connection.applications", "connection.applications has more items", 1_001 },
        { "findings.items[0].evidence", "findings.items[0].evidence has more items", 101 },
    };

    [Theory]
    [MemberData(nameof(Caps))]
    public void A_list_over_its_cap_refuses_the_file(string path, string sentence, int count)
    {
        JsonObject tree = Tree(Everything());
        JsonNode parent = tree;
        string[] steps = path.Split('.');

        for (int i = 0; i < steps.Length - 1; i++)
        {
            parent = Step(parent, steps[i]);
        }

        JsonArray items = [];

        for (int i = 0; i < count; i++)
        {
            items.Add(new JsonObject());
        }

        Step(parent, steps[^1], items);

        SnapshotFileRead read = Read(tree);

        read.Snapshot.Should().BeNull();
        read.Problem.Should().Contain(sentence);

        static JsonNode Step(JsonNode node, string step, JsonNode? set = null)
        {
            if (step.EndsWith("[0]", StringComparison.Ordinal))
            {
                string name = step[..^3];

                return node[name]![0]!;
            }

            if (set is not null)
            {
                node[step] = set;
            }

            return node[step]!;
        }
    }

    [Fact]
    public void A_string_over_the_cap_refuses_the_file()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!["detail"] = new string('x', SnapshotFileReader.MaxString + 1);

        SnapshotFileRead read = Read(tree);

        read.Problem.Should().Be("findings.items[0].detail is longer than a snapshot this studio writes.");
    }

    [Fact]
    public void A_string_at_the_cap_is_read()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!["detail"] = new string('x', SnapshotFileReader.MaxString);

        Read(tree).Snapshot.Should().NotBeNull();
    }

    [Fact]
    public void More_withheld_entries_than_a_snapshot_writes_refuse_the_file()
    {
        JsonObject tree = Tree(Everything());
        JsonObject withheld = [];

        for (int i = 0; i < 101; i++)
        {
            withheld["k" + i] = "w";
        }

        tree["withheld"] = withheld;

        Read(tree).Problem.Should().Contain("withheld has more entries");
    }

    public static TheoryData<string, string> WrongTypes => new()
    {
        { "findings.items[0].severity", "findings.items[0].severity is not a severity" },
        { "findings.items[0].title", "findings.items[0].title is not text" },
        { "findings.items[0].accepted", "findings.items[0].accepted is not true or false" },
        { "findings.items[0].measuredAt", "findings.items[0].measuredAt is not a time" },
        { "findings.withheld", "findings.withheld is not a whole number" },
        { "storage.databaseBytes", "storage.databaseBytes is not a whole number" },
        { "storage.largestTables[0].shareOfDatabase", "storage.largestTables[0].shareOfDatabase is not a number" },
        { "statements.top[0].queryId", "statements.top[0].queryId is not a number" },
        { "statements.windowSeconds", "statements.windowSeconds is not a number" },
        { "wal.statsReset", "wal.statsReset is not a time" },
        { "server", "server is not an object" },
        { "findings.items", "findings.items is not a list" },
        { "connection.settings[0].value", "connection.settings[0].value is not text" },
    };

    [Theory]
    [MemberData(nameof(WrongTypes))]
    public void A_wrong_type_is_refused_naming_the_studios_key_path_and_never_a_value_from_the_file(string path, string sentence)
    {
        JsonObject tree = Tree(Everything());

        // A value of the wrong kind that carries the sentinel: a string where a number is wanted, an object where text is.
        JsonNode wrong = path.EndsWith("severity", StringComparison.Ordinal) ? JsonValue.Create(17)! : JsonValue.Create(Sentinel)!;

        if (path is "findings.items[0].title" or "connection.settings[0].value")
        {
            wrong = new JsonObject { ["x"] = Sentinel };
        }

        Set(tree, path, wrong);

        SnapshotFileRead read = Read(tree);

        read.Snapshot.Should().BeNull();
        read.Problem.Should().Be(sentence + ".");
        read.Problem.Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_severity_that_is_a_string_the_file_made_up_skips_the_item_and_is_never_echoed()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!["severity"] = Sentinel;

        SnapshotFileRead read = Read(tree);

        read.Problem.Should().BeNull();
        read.SkippedItems.Should().Be(1);
        DiagnosticsJson.Write(read.Snapshot!).Should().NotContain(Sentinel);
    }

    [Fact]
    public void A_finding_without_a_code_is_refused_by_path()
    {
        JsonObject tree = Tree(Everything());
        tree["findings"]!["items"]![0]!.AsObject().Remove("code");

        Read(tree).Problem.Should().Be("findings.items[0].code is missing.");
    }

    [Fact]
    public void A_compute_that_is_not_a_capacity_is_refused()
    {
        JsonObject tree = Tree(Everything());
        tree["compute"]!["minimum"] = 100;
        tree["compute"]!["maximum"] = 1;

        Read(tree).Problem.Should().Be("compute is not a capacity.");
    }

    [Fact]
    public void A_database_that_names_no_source_is_refused()
    {
        JsonObject tree = Tree(Everything());
        tree["database"]!["source"] = "  ";

        Read(tree).Problem.Should().Be("database.source is missing.");
    }

    [Fact]
    public void No_refusal_holds_a_sentinel_placed_in_any_string_position()
    {
        // Every string position of the fixture, set to the sentinel with a wrong-typed neighbour: whatever the refusal says, not the sentinel.
        string json = DiagnosticsJson.Write(Everything());
        string poisoned = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": \"" + Sentinel + "\"", StringComparison.Ordinal);

        Read(poisoned).Problem.Should().NotBeNull().And.NotContain(Sentinel);

        string badTime = json.Replace("\"generatedAt\": \"", "\"generatedAt\": \"" + Sentinel, StringComparison.Ordinal);

        Read(badTime).Problem.Should().Be("generatedAt is not a time.");
    }

    private static void Set(JsonObject root, string path, JsonNode value)
    {
        JsonNode node = root;
        string[] steps = path.Split('.');

        for (int i = 0; i < steps.Length; i++)
        {
            string step = steps[i];
            int bracket = step.IndexOf('[', StringComparison.Ordinal);
            string name = bracket < 0 ? step : step[..bracket];
            bool last = i == steps.Length - 1;

            if (bracket >= 0)
            {
                int index = int.Parse(step[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
                JsonNode array = node[name]!;

                if (last)
                {
                    array.AsArray()[index] = value;
                    return;
                }

                node = array[index]!;
            }
            else if (last)
            {
                node[name] = value;
            }
            else
            {
                node = node[name]!;
            }
        }
    }
}
