using DataStudio.Services;
using DataStudio.Kafka.Services;
using DataStudio.Kafka.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Services;

/// <summary>
/// The two capabilities M5 adds (AGENTS.md D45 and D46, D-M5 decision 1): both default to off, both need <c>SearchRecords</c>, the audit gate judges their
/// entries with it, and no profile and no combination of flags lets anything through that the flags did not grant.
/// </summary>
public class KafkaExportCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<List<StudioActionLogEntry>> Visible(KafkaHarness harness) =>
        harness.Context.ActivityRing.GetVisibleAsync(cancellationToken: Ct);

    private static void Record(KafkaHarness harness, string[] topics, KafkaCapability capability) =>
        harness.ActionLog.Record(KafkaHarness.Primary, topics, capability.ToString(), string.Join(", ", topics), succeeded: true, "done", capability);

    // ---- the flags and the guard

    [Fact]
    public void The_two_new_flags_default_to_false_and_All_turns_them_on()
    {
        new KafkaCapabilities().ExportRecords.Should().BeFalse();
        new KafkaCapabilities().LiveTail.Should().BeFalse();
        KafkaCapabilities.None().ExportRecords.Should().BeFalse();
        new KafkaOptions().Capabilities.LiveTail.Should().BeFalse();
        KafkaCapabilities.All().ExportRecords.Should().BeTrue();
        KafkaCapabilities.All().LiveTail.Should().BeTrue();
    }

    [Fact]
    public void Prerequisites_name_SearchRecords_for_every_other_capability_and_nothing_for_SearchRecords()
    {
        KafkaCapabilityGuard.Prerequisites(KafkaCapability.SearchRecords).Should().BeEmpty();

        foreach (KafkaCapability capability in KafkaCapabilityGuard.Offered.Where(static x => x != KafkaCapability.SearchRecords))
        {
            KafkaCapabilityGuard.Prerequisites(capability).Should().Equal(KafkaCapability.SearchRecords);
        }
    }

    [Theory]
    [InlineData("ReadPayloads")]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public void Requiring_a_capability_names_SearchRecords_first_when_it_is_off(string name)
    {
        KafkaCapability capability = Enum.Parse<KafkaCapability>(name);
        using KafkaHarness harness = new(options =>
        {
            options.Capabilities.ReadPayloads = true;
            options.Capabilities.ExportRecords = true;
            options.Capabilities.LiveTail = true;
        });
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action require = () => guard.Require(capability);

        require.Should().Throw<KafkaCapabilityDeniedException>().Which.Capability.Should().Be(KafkaCapability.SearchRecords);
    }

    [Fact]
    public void Requiring_a_capability_with_SearchRecords_on_names_the_capability_when_it_is_off()
    {
        using KafkaHarness harness = new(static o => o.Capabilities.SearchRecords = true);
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action export = () => guard.Require(KafkaCapability.ExportRecords);

        export.Should().Throw<KafkaCapabilityDeniedException>().WithMessage(
            "Data Studio refused 'ExportRecords' because KafkaOptions.Capabilities.ExportRecords is false.");
    }

    [Fact]
    public void ReadOnly_turns_all_four_off()
    {
        using KafkaHarness harness = new(static o => o.Capabilities = KafkaCapabilities.All());
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();
        guard.Offered().Should().HaveCount(4);
        guard.Offered().Should().OnlyContain(c => guard.IsEnabled(c));

        harness.Context.ShellOptions.ReadOnly = true;

        guard.Offered().Should().OnlyContain(c => !guard.IsEnabled(c), "the shell's master switch turns every capability off");
    }

    [Fact]
    public void A_host_that_sets_ExportRecords_or_LiveTail_without_SearchRecords_still_starts_and_every_use_is_refused()
    {
        using KafkaHarness harness = new(static o => o.Capabilities.ExportRecords = true);
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        guard.IsConfigured(KafkaCapability.ExportRecords).Should().BeTrue();
        Action require = () => guard.Require(KafkaCapability.ExportRecords);

        require.Should().Throw<KafkaCapabilityDeniedException>().WithMessage("*SearchRecords*");
    }

    // ---- the audit gate

    [Theory]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public async Task An_entry_of_the_new_capabilities_is_judged_with_SearchRecords_per_topic(string name)
    {
        KafkaCapability capability = Enum.Parse<KafkaCapability>(name);
        using KafkaHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        harness.Options.Capabilities.ExportRecords = true;
        harness.Options.Capabilities.LiveTail = true;
        Record(harness, ["orders.v1"], capability);

        (await Visible(harness)).Should().BeEmpty("SearchRecords is off, so the entry would reveal a read the visitor may not make");

        harness.Options.Capabilities.SearchRecords = true;
        (await Visible(harness)).Should().ContainSingle("the capability and SearchRecords are on and both policies pass");

        harness.Authorization.AllowKafka(r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses SearchRecords on the entry's topic");

        harness.Authorization.AllowKafka(r => r.Capability != capability.ToString());
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses the capability itself");

        harness.Authorization.AllowKafka(static _ => true);
        (await Visible(harness)).Should().ContainSingle();

        harness.Context.ShellOptions.ReadOnly = true;
        (await Visible(harness)).Should().BeEmpty("the host's master switch turns both off");
    }

    [Fact]
    public async Task An_entry_of_the_new_capabilities_that_names_no_topic_is_judged_at_cluster_level_with_SearchRecords()
    {
        using KafkaHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        harness.Options.Capabilities.SearchRecords = true;
        harness.Options.Capabilities.ExportRecords = true;
        Record(harness, [], KafkaCapability.ExportRecords);

        (await Visible(harness)).Should().ContainSingle();

        harness.Authorization.AllowKafka(static r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("SearchRecords is refused for the cluster");
    }
}

internal static class KafkaCapabilityGuardTestExtensions
{
    /// <summary>Every capability the Kafka module offers, for a test that loops over them.</summary>
    public static IReadOnlyList<KafkaCapability> Offered(this KafkaCapabilityGuard guard) => KafkaCapabilityGuard.Offered;
}
