using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// C13 and C14 are Activity checks for the diagnostics API too (issue #20, D31): they name other people's applications, roles,
/// addresses and databases, so a call without <c>IncludeActivity</c> counts their findings and describes none - and says "withheld"
/// of each check, whether or not it found anything - and one with it returns the findings, names and all.
/// </summary>
public class ConnectionHolderFindingsGateTests
{
    private const string Application = "SENTINEL-APP-billing";
    private const string Role = "SENTINEL-ROLE-hr";

    private static SessionHolderGroup Group(string application, long idle) =>
        new("SENTINEL-DB-orders", application, Role, "10.1.2.3", false, idle, 0, idle, 0, 0, TimeSpan.FromMinutes(3), null);

    private static ConnectionHolderFacts Facts() => new(
        At,
        new ServerConnectionLimits(95, 0, 103, 3, 0, 170000),
        new SessionServerHolders(true, 103, 3, 0, 170000, 95, 2, [Group(Application, 14), Group(string.Empty, 6)]));

    private static HealthSweep Swept()
    {
        HealthCheckInfo hoard = HealthCheckRegistry.Describe(typeof(IdleConnectionsHoardedCheck));
        HealthCheckInfo unnamed = HealthCheckRegistry.Describe(typeof(UnattributedConnectionsCheck));
        HealthEvaluation evaluation = HealthEvaluation.At(At);

        // As the sweep stamps a finding with its check's own metadata.
        HealthFinding[] Stamp(HealthCheckInfo info, HealthVerdict verdict) =>
            [.. verdict.Findings.Select(x => x with { Area = info.Area, Gate = info.Gate, CheckTitle = info.Title, MeasuredAt = At })];

        return Sweep(
            Result(hoard, findings: Stamp(hoard, new IdleConnectionsHoardedCheck().Evaluate(Facts(), evaluation))),
            Result(unnamed, findings: Stamp(unnamed, new UnattributedConnectionsCheck().Evaluate(Facts(), evaluation))));
    }

    private static (IReadOnlyList<PostgresStudio.PostgresFinding> Findings, IReadOnlyList<PostgresStudio.PostgresCheck> Checks, int Withheld) Build(bool includeActivity) =>
        DiagnosticsSnapshotBuilder.Findings(Swept().Results, Gate(includeActivity), NoStudioRuntime.Instance, "store", new PostgresStudio.Services.StudioScope("main", "db1", null));

    [Fact]
    public void Both_checks_are_catalogued_as_requiring_activity()
    {
        foreach (Type check in new[] { typeof(IdleConnectionsHoardedCheck), typeof(UnattributedConnectionsCheck) })
        {
            DiagnosticsMapper.Check(HealthCheckRegistry.Describe(check)).RequiresActivity.Should().BeTrue(check.Name);
        }
    }

    [Fact]
    public void Without_IncludeActivity_both_are_counted_never_described_and_say_withheld()
    {
        (IReadOnlyList<PostgresStudio.PostgresFinding> findings, IReadOnlyList<PostgresStudio.PostgresCheck> checks, int withheld) = Build(includeActivity: false);

        findings.Should().BeEmpty("neither Activity check's finding is described");
        withheld.Should().Be(2, "C13's finding and C14's are counted");
        checks.Single(static x => x.Code == "C13").Outcome.Should().Be(PostgresStudio.PostgresReadOutcome.Withheld);
        checks.Single(static x => x.Code == "C14").Outcome.Should().Be(PostgresStudio.PostgresReadOutcome.Withheld);

        System.Text.Json.JsonSerializer.Serialize(findings).Should().NotContain(Application).And.NotContain(Role).And.NotContain("SENTINEL-DB");
    }

    [Fact]
    public void With_IncludeActivity_both_findings_are_returned_with_their_names()
    {
        (IReadOnlyList<PostgresStudio.PostgresFinding> findings, IReadOnlyList<PostgresStudio.PostgresCheck> checks, int withheld) = Build(includeActivity: true);

        withheld.Should().Be(0);
        findings.Select(static x => x.Code).Should().Equal("C13", "C14");
        checks.Single(static x => x.Code == "C13").Outcome.Should().Be(PostgresStudio.PostgresReadOutcome.Read);
        checks.Single(static x => x.Code == "C13").Findings.Should().Be(1);

        string json = System.Text.Json.JsonSerializer.Serialize(findings);
        json.Should().Contain(Application).And.Contain(Role).And.Contain("SENTINEL-DB-orders");
        findings.Single(static x => x.Code == "C13").StudioLinks.Should().ContainSingle().Which.Path.Should().Contain("view=holders");
    }
}
