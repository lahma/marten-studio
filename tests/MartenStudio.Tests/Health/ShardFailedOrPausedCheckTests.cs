using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>D05's judgement: failed-and-stopped is Critical, paused is a Warning, a plain stop is nobody's alarm.</summary>
public class ShardFailedOrPausedCheckTests
{
    private static HealthVerdict Evaluate(IReadOnlyList<DaemonShardRow>? rows, DaemonInProcess? inProcess = null) =>
        new ShardFailedOrPausedCheck().Evaluate(
            new ShardFailedOrPausedFacts(DaemonConfiguration(extended: rows is not null), rows, inProcess ?? DaemonInProcess.NotHosted),
            HealthEvaluation.At(Now));

    [Fact]
    public void A_shard_stopped_by_a_failure_is_critical_with_the_failure_and_its_remedy()
    {
        DaemonShardRow row = new(
            "ShipmentTracker:All", 40, Now, Now - TimeSpan.FromMinutes(3), "Stopped", "ShipmentTracker cannot ship SKU 'POISON'.",
            "ApplyEvent", 41, "item_added");

        HealthFinding finding = Evaluate([row]).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("D05");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("ShipmentTracker:All");
        finding.TenantId.Should().BeNull();
        finding.Evidence.Should().Contain(new HealthEvidence("Failure", "ApplyEvent"));
        finding.Evidence.Should().Contain(new HealthEvidence("Failed on event", "41 (item_added)"));
        finding.Remedy!.Code.Should().Be("opts.Projections.Errors.SkipApplyErrors = true;");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_paused_shard_is_a_warning()
    {
        DaemonShardRow row = new("DailySales:All", 10, Now, Now, "Paused", "transient");

        Evaluate([row]).Findings.Single().Severity.Should().Be(HealthSeverity.Warning);
    }

    /// <summary>The studio's own coordinator pause, a host shutting down: a stop with no failure is nobody's alarm.</summary>
    [Fact]
    public void A_stop_without_a_failure_is_quiet()
    {
        Evaluate([new DaemonShardRow("DailySales:All", 10, Now, Now, "Stopped")]).Findings.Should().BeEmpty();
    }

    /// <summary>The daemon here is fresher than any row it wrote.</summary>
    [Fact]
    public void A_running_agent_here_overrides_a_stale_paused_row()
    {
        DaemonInProcess running = DaemonInProcess.NotHosted with
        {
            Hosting = DaemonHostingState.Hosted,
            IsRunning = true,
            Agents = [new DaemonAgentFact("DailySales:All", "Running", null, null, null)],
        };

        Evaluate([new DaemonShardRow("DailySales:All", 10, Now, Now, "Paused")], running).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_paused_agent_here_is_reported_without_extended_tracking()
    {
        DaemonInProcess paused = DaemonInProcess.NotHosted with
        {
            Hosting = DaemonHostingState.Hosted,
            IsRunning = true,
            Agents = [new DaemonAgentFact("DailySales:All", "Paused", "ApplyEvent", "boom", Now - TimeSpan.FromSeconds(20))],
        };

        HealthFinding finding = Evaluate(null, paused).Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Status", "Paused (the daemon in this process)"));
    }

    /// <summary>A tenant's shard names the tenant: in the finding's tenant, and in gated evidence - never in the title.</summary>
    [Fact]
    public void A_tenants_shard_carries_the_tenant_behind_the_gate_and_not_in_the_title()
    {
        DaemonShardRow row = new("DailySales:All:acme", 10, Now, Now, "Stopped", "boom", "ApplyEvent");

        HealthFinding finding = Evaluate([row]).Findings.Single();

        finding.TenantId.Should().Be("acme");
        finding.Title.Should().NotContain("acme");
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme", HealthGate.Activity));
    }

    [Fact]
    public void A_shard_being_rebuilt_is_stopped_on_purpose()
    {
        DaemonInProcess rebuilding = DaemonInProcess.NotHosted with { StudioRebuilds = ["ShipmentTracker"] };

        Evaluate([new DaemonShardRow("ShipmentTracker:All", 10, Now, Now, "Stopped", "boom", "ApplyEvent")], rebuilding)
            .Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_start_the_studio_saw_refused_is_evidence_with_its_classified_reason()
    {
        DaemonInProcess refused = DaemonInProcess.NotHosted with
        {
            StartRefusals = new Dictionary<string, StudioStartRefusal>
            {
                ["DailySales:All"] = new("default", "db", "DailySales:All", "AgentPaused", false, "refused", Now),
            },
        };

        HealthFinding finding = Evaluate([new DaemonShardRow("DailySales:All", 10, Now, Now, "Paused", "boom")], refused).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Last start from Marten Studio"
            && x.Value.Contains(": AgentPaused, which a retry will not resolve on its own", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_tracking_or_a_daemon_here_nothing_can_say_and_the_check_says_so()
    {
        HealthVerdict verdict = Evaluate(null);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("K02");
    }

    /// <summary>A finding names a projection, so it carries what that projection writes: one of a hidden type is dropped.</summary>
    [Fact]
    public void A_finding_carries_the_document_types_its_projection_writes()
    {
        DaemonCheckConfiguration configuration = DaemonConfiguration(extended: true) with
        {
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase) { ["DailySales"] = [typeof(Uri)] },
        };

        new ShardFailedOrPausedCheck()
            .Evaluate(
                new ShardFailedOrPausedFacts(configuration, [new DaemonShardRow("DailySales:All", 10, Now, Now, "Paused", "boom")], DaemonInProcess.NotHosted),
                HealthEvaluation.At(Now))
            .Findings.Single().DocumentTypes.Should().Equal(typeof(Uri));
    }

    /// <summary>The high-water bookkeeping rows carry no status, and are never a shard.</summary>
    [Fact]
    public void A_bookkeeping_row_is_never_a_shard()
    {
        Evaluate([new DaemonShardRow("HighWaterMark", 10, Now, Now, "Stopped", "boom", "Other")]).Findings.Should().BeEmpty();
    }
}
