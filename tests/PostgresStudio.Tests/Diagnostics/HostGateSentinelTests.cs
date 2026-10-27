using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Statements;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The host gate's sentinel tests (API design §5.3): a sentinel in every Activity-grade place - an evidence value, a tenant id, a
/// statement text, an application name and a finding of an Activity check - and none of them anywhere in <c>ToJson()</c>
/// without <c>IncludeActivity</c>, every count right; with it, every one, while the content rules - a literal-bearing text
/// withheld, comments masked - still hold. (A hidden document type's absence is MartenStudio's: <c>DiagnosticsServiceTests</c>.)
/// </summary>
public class HostGateSentinelTests
{
    private const string EvidenceSentinel = "SENTINEL-EVIDENCE-pid-4242";
    private const string TenantSentinel = "SENTINEL-TENANT-acme";
    private const string StatementSentinel = "SENTINEL_STATEMENT_column";
    private const string ApplicationSentinel = "SENTINEL-APP-billing";
    private const string ActivityFindingSentinel = "SENTINEL-ACTIVITY-FINDING";
    private const string LiteralSentinel = "SENTINEL-LITERAL-123-45-6789";
    private const string CommentSentinel = "SENTINEL-COMMENT-traceparent";

    private static readonly string[] ActivitySentinels =
        [EvidenceSentinel, TenantSentinel, StatementSentinel, ApplicationSentinel, ActivityFindingSentinel];

    private static string Json(bool includeActivity)
    {
        HostGate gate = Gate(includeActivity);

        HealthCheckInfo database = Info("C05", HealthArea.Connections);
        HealthCheckInfo activity = Info("C04", HealthArea.Connections, HealthGate.Activity);
        HealthCheckInfo tenants = Info("E03", HealthArea.Events);

        HealthSweep sweep = Sweep(
            Result(database, findings:
            [
                Finding(database, "A statement has run for 12 minutes") with
                {
                    Evidence =
                    [
                        new HealthEvidence("Longest", "12 min"),
                        new HealthEvidence("Session 1 (longest)", EvidenceSentinel, HealthGate.Activity),
                        HealthEvidence.StatementText("Statement", "select " + StatementSentinel + " from orders", truncated: false),
                    ],
                },
            ]),
            Result(activity, findings: [Finding(activity, ActivityFindingSentinel)]),
            Result(tenants, findings: [Finding(tenants, "A hot stream") with { TenantId = TenantSentinel, Subject = "stream-7" }]));

        (IReadOnlyList<PostgresFinding> findings, IReadOnlyList<PostgresCheck> checks, int withheld) =
            DiagnosticsSnapshotBuilder.Findings(sweep.Results, gate, NoStudioRuntime.Instance, "store", new PostgresStudio.Services.StudioScope("main", "db1", null));

        Dictionary<string, string> sections = new(StringComparer.Ordinal);
        PostgresStatementsSection? statements = null;

        if (includeActivity)
        {
            // Through the Statements page's own facts read, where T02's withholding and comment masking are applied.
            StatementFacts facts = StatementFacts.Build(
                "tenants",
                "acme",
                At,
                StatementsTestFacts.Outcome(StatementsTestFacts.Read(
                    null,
                    StatementsTestFacts.Statement(1, "select " + StatementSentinel + " from orders /* " + CommentSentinel + " */", 5, 120),
                    StatementsTestFacts.Statement(2, "select * from orders where ssn = '" + LiteralSentinel + "'", 1, 10))),
                null,
                HealthHistoryView.Empty,
                null,
                null,
                null);

            statements = DiagnosticsSnapshotBuilder.Statements(facts, gate, PostgresStudio.Services.Postgres.PostgresStudioText.Instance);
        }
        else
        {
            sections["statements"] = HostGate.WithheldSection;
            sections["connections.byApplication"] = HostGate.WithheldSection;
        }

        PostgresDiagnosticsSnapshot snapshot = new()
        {
            SchemaVersion = 1,
            Producer = "PostgresStudio 0.5.0",
            GeneratedAt = At,
            Database = new PostgresDatabaseRef("tenants", "acme"),
            Findings = findings,
            Checks = checks,
            FindingsWithheld = withheld,
            Statements = statements,
            Connections = DiagnosticsSnapshotBuilder.Connections(
                SessionsOf(new SessionApplicationCounts(ApplicationSentinel, 2, 1, 1, 0, 0, 0, null)), gate),
            Withheld = sections,
        };

        return snapshot.ToJson();
    }

    [Fact]
    public void Without_IncludeActivity_no_sentinel_appears_anywhere_and_every_count_is_right()
    {
        string json = Json(includeActivity: false);

        foreach (string sentinel in ActivitySentinels)
        {
            json.Should().NotContain(sentinel);
        }

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement findings = document.RootElement.GetProperty("findings");

        findings.GetProperty("withheld").GetInt32().Should().Be(2, "the Activity check's finding and the tenant's finding are counted, never described");
        findings.GetProperty("items").GetArrayLength().Should().Be(1);
        findings.GetProperty("checks").EnumerateArray()
            .Single(static x => x.GetProperty("code").GetString() == "C04").GetProperty("outcome").GetString()
            .Should().Be("withheld", "a check past the gate says withheld, whether or not it found anything");
        findings.GetProperty("checks").EnumerateArray()
            .Single(static x => x.GetProperty("code").GetString() == "E03").GetProperty("findings").GetInt32()
            .Should().Be(0, "its one finding names a tenant");
        findings.GetProperty("items")[0].GetProperty("evidence").EnumerateArray()
            .Select(static x => x.GetProperty("value").GetString())
            .Should().Contain("12 min").And.Contain(HostGate.WithheldValue, "an Activity value keeps its label and loses its value");

        System.Text.Json.JsonElement connections = document.RootElement.GetProperty("connections");
        connections.GetProperty("byApplication").GetArrayLength().Should().Be(0);
        connections.GetProperty("database").GetProperty("total").GetInt32().Should().Be(2, "the database's total is always shown");

        document.RootElement.GetProperty("statements").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        document.RootElement.GetProperty("withheld").GetProperty("statements").GetString().Should().Be(HostGate.WithheldSection);
    }

    [Fact]
    public void With_IncludeActivity_every_sentinel_appears_and_the_content_rules_still_hold()
    {
        string json = Json(includeActivity: true);

        foreach (string sentinel in ActivitySentinels)
        {
            json.Should().Contain(sentinel);
        }

        json.Should().NotContain(LiteralSentinel, "a literal-bearing text stays withheld whoever asks")
            .And.NotContain(CommentSentinel, "a comment stays masked");

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        System.Text.Json.JsonElement top = document.RootElement.GetProperty("statements").GetProperty("top");

        top.EnumerateArray().Single(static x => x.GetProperty("queryId").GetString() == "2").GetProperty("text").ValueKind
            .Should().Be(System.Text.Json.JsonValueKind.Null);
        top.EnumerateArray().Single(static x => x.GetProperty("queryId").GetString() == "2").GetProperty("textWithheld").GetString()
            .Should().NotBeNullOrEmpty();
    }
}
