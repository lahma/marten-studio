using KStudio.Services;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>Hard rule 5 for KStudio: a refusal is recorded on failure as well as on success, to the ring and to the log, and a value from the address is escaped.</summary>
public class KafkaActionLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_capability_refusal_is_recorded_on_failure_to_the_ring_and_as_9602()
    {
        using KafkaStudioHarness harness = new();
        CapturingLoggerProvider captured = new();
        KafkaActionLog log = new(harness.Context.ActionLog, captured.CreateFactory().CreateLogger<KafkaActionLog>(), harness.Visitor);
        KafkaCapabilityDeniedException denial = new(KafkaCapability.SearchRecords, PostgresStudio.Services.CapabilityDenialReason.Disabled, "PostgresStudioOptions.ReadOnly");

        log.RecordCapabilityDenied(KafkaStudioHarness.Primary, ["orders.v1"], denial, "SearchRecords", "orders.v1");

        StudioActionLogEntry entry = harness.Context.Services.GetRequiredService<StudioActionLogService>().GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be("SearchRecords");
        entry.Message.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords");
        entry.ScopeKind.Should().Be("kafka");
        captured.Entries.Should().ContainSingle().Which.Should().Match<CapturedLogEntry>(
            static x => x.EventId.Id == 9602 && x.Level == LogLevel.Warning && x.Message.Contains("KafkaStudioOptions.Capabilities.SearchRecords", StringComparison.Ordinal));
    }

    [Fact]
    public void A_policy_refusal_is_recorded_as_9603_and_the_ring_entry_says_nothing_about_why()
    {
        using KafkaStudioHarness harness = new();
        CapturingLoggerProvider captured = new();
        KafkaActionLog log = new(harness.Context.ActionLog, captured.CreateFactory().CreateLogger<KafkaActionLog>(), harness.Visitor);

        log.RecordClusterDenied(KafkaStudioHarness.Primary, "secrets.v1", ["secrets.v1"], "kafka-topics", "SearchRecords", "secrets.v1", KafkaCapability.SearchRecords);

        StudioActionLogEntry entry = harness.Context.Services.GetRequiredService<StudioActionLogService>().GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(KafkaStudioText.RefusedEntry);
        captured.Entries.Should().ContainSingle().Which.EventId.Id.Should().Be(9603);
    }

    [Fact]
    public void A_target_and_a_label_are_bounded_like_the_consoles()
    {
        using KafkaStudioHarness harness = new();

        harness.ActionLog.Record(new KafkaClusterEntry("primary", null, new string('x', 500), null), [], "SearchRecords", new string('t', 500), succeeded: true);

        StudioActionLogEntry entry = harness.Context.Services.GetRequiredService<StudioActionLogService>().GetLatest().Should().ContainSingle().Which;
        entry.Target.Should().HaveLength(KafkaActionLog.MaxTargetLength + 1).And.EndWith("…");
        entry.ScopeLabel.Should().HaveLength(KafkaActionLog.MaxTargetLength + 1).And.EndWith("…");
    }

    [Fact]
    public async Task A_line_break_in_a_cluster_from_the_address_is_escaped_in_9610()
    {
        using KafkaStudioHarness harness = new(null, KafkaStudioHarness.Primary);
        harness.Context.Navigate("/postgres/kafka?cluster=evil%0D%0A2026-11-12%20forged%20line");
        CapturingLoggerProvider captured = new();
        KafkaState state = new(
            harness.Entries,
            harness.KafkaAuthorization,
            harness.Context.Services.GetRequiredService<NavigationManager>(),
            captured.CreateFactory().CreateLogger<KafkaState>());

        await state.EnsureInitializedAsync(Ct);

        CapturedLogEntry entry = captured.Entries.Should().ContainSingle().Which;
        entry.EventId.Id.Should().Be(9610);
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Contain("evil\\r\\n2026-11-12 forged line", "the break is written as the two characters it is, not as a second line")
            .And.NotContain("\r").And.NotContain("\n");
    }
}
